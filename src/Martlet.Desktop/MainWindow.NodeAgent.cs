using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Nodes;

namespace Martlet.Desktop;

/// <summary>This PC as a node your other Martlet computers can command. When this PC runs a host service (Docker Desktop),
/// Martlet here is that host's agent: every few seconds it asks the host's gateway for commands paired computers sent (with
/// the token the gateway wrote where only this PC can read it), runs them (update Martlet and the host service, add or
/// remove a role, status) and streams their output back. It also keeps this PC's host service on this PC's version, so a
/// host service from before commands existed gets them by itself. On by default; Settings › Your other computers turns it
/// off (node-commands.txt).</summary>
public partial class MainWindow
{
    private const string NodeCommandsFile = "node-commands.txt";
    private const string AgentTokenPath = "/var/lib/martlet/config/gateway/agent.token";
    private static readonly TimeSpan DockerStartPatience = TimeSpan.FromMinutes(4);
    private readonly DispatcherTimer nodeAgentTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool nodeCommandsAllowed = true;
    private bool changingNodeChoice;
    private bool nodeAgentBusy;
    private NodeCommandAgent? nodeAgent;
    private string? nodeAgentHostId;
    private bool installAfterNodeCommand;
    private string? nodeUpdateWaiting;
    private string? ownHostUpdateTried;
    private string? lastNodeCommand;

    private void InitializeNodeAgent()
    {
        nodeAgentTimer.Tick += (_, _) => NodeAgentTickAsync().Forget();
        if (store is null)
        {
            AllowNodeCommands.IsEnabled = false;
            return;
        }
        nodeCommandsAllowed = LoadNodeCommandsAllowed(store.DataDirectory);
        changingNodeChoice = true;
        AllowNodeCommands.IsChecked = nodeCommandsAllowed;
        changingNodeChoice = false;
        ShowNodeAgentStatus(nodeCommandsAllowed ? "Checking for this PC's host service..." : NodeCommandsOffText);
    }

    private void StartNodeAgent()
    {
        if (store is null || closing) return;
        nodeAgentTimer.Start();
        NodeAgentTickAsync().Forget();
    }

    private const string NodeCommandsOffText =
        "Off. Your other computers can't update Martlet here or manage this PC's host service; commands they send wait until you turn this on.";

    internal static bool LoadNodeCommandsAllowed(string directory)
    {
        try { return File.ReadAllText(Path.Combine(directory, NodeCommandsFile)).Trim() != "off"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>Checked/Unchecked, so UI Automation's toggle (MCP ui_toggle) changes the choice too.</summary>
    private void AllowNodeCommands_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || changingNodeChoice || store is null) return;
        var allowed = AllowNodeCommands.IsChecked == true;
        try
        {
            Directory.CreateDirectory(store.DataDirectory);
            File.WriteAllText(Path.Combine(store.DataDirectory, NodeCommandsFile), allowed ? "on" : "off");
            nodeCommandsAllowed = allowed;
            ShowNodeAgentStatus(allowed ? "On. Checking this PC's host service for commands..." : NodeCommandsOffText);
            if (allowed) NodeAgentTickAsync().Forget();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            changingNodeChoice = true;
            AllowNodeCommands.IsChecked = nodeCommandsAllowed;
            changingNodeChoice = false;
            ShowNodeAgentStatus("Could not save node-commands.txt. The previous choice stays in effect; check access to your data directory.");
        }
    }

    private void ShowNodeAgentStatus(string text)
    {
        if (NodeAgentStatus.Text != text) NodeAgentStatus.Text = text;
    }

    private string LastNodeCommandText => lastNodeCommand is null ? "" : $" Last: {lastNodeCommand}";

    private async Task NodeAgentTickAsync()
    {
        if (nodeAgentBusy || closing || store is null) return;
        if (!nodeCommandsAllowed)
        {
            ShowNodeAgentStatus(NodeCommandsOffText);
            return;
        }
        var host = NetworkMap.Hosts(Inputs()).FirstOrDefault(h => h.Method == HostSetupMethod.ThisPcDocker);
        if (host is null)
        {
            ShowNodeAgentStatus("This PC runs no host service, so there is nothing here for your other computers to manage. " +
                "Martlet on this PC still updates itself as set in App updates.");
            return;
        }
        nodeAgentBusy = true;
        try
        {
            if (nodeAgent is null || nodeAgentHostId != host.HostId)
            {
                nodeAgent = new(ReadAgentTokenAsync, Version, new LocalCommandRunner(this));
                nodeAgentHostId = host.HostId;
            }
            var agent = nodeAgent;
            var pass = await ClusterSync.WithConnectionAsync(host.Pairing, connection => agent.RunOnceAsync(connection, lifetime.Token));
            if (closing) return;
            switch (pass.Kind)
            {
                case NodeAgentPassKind.Idle:
                    ShowNodeAgentStatus($"On. Your other computers can update Martlet here and manage {host.HostId} (roles, status) " +
                        "through its paired connection." + LastNodeCommandText);
                    break;
                case NodeAgentPassKind.Ran:
                    lastNodeCommand = $"{pass.Text} ({DateTime.Now:t})";
                    ErrorLog.Info("Ran a command from another computer: " + pass.Text);
                    ShowNodeAgentStatus("On." + LastNodeCommandText);
                    if (pass.Command?.Kind != NodeCommandKinds.Status && pass.Command?.Kind != NodeCommandKinds.DescribeRole)
                        CheckHostsAsync([host]).Forget();
                    break;
                case NodeAgentPassKind.Pending:
                    ShowNodeAgentStatus(pass.Text);
                    if (installAfterNodeCommand)
                    {
                        installAfterNodeCommand = false;
                        ErrorLog.Info("Installing a Martlet update another computer asked for: " + pass.Text);
                        InstallNow(quiet: true);
                    }
                    break;
                case NodeAgentPassKind.HostTooOld or NodeAgentPassKind.NoToken:
                    await KeepOwnHostServiceCurrentAsync(host, pass.Text);
                    break;
                default:
                    ShowNodeAgentStatus($"{host.HostId} isn't answering ({pass.Text}). Commands from your other computers wait there.");
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (ClusterSync.IsHostFailure(error))
        {
            if (!closing) ShowNodeAgentStatus($"Could not reach {host.HostId}: {error.Message}");
        }
        finally { nodeAgentBusy = false; }
    }

    /// <summary>Reads the agent token this PC's gateway wrote at its last start, from inside its container (only this PC can);
    /// null when the gateway predates commands or Docker isn't running. Never shown or logged.</summary>
    private static async Task<string?> ReadAgentTokenAsync(CancellationToken token)
    {
        var lines = new List<string>();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var exit = await LocalProcess.RunAsync(HostLocal.Docker, ["exec", "martlet-host-gateway", "cat", AgentTokenPath],
                new LineSink(line => { lock (lines) lines.Add(line); }), limit.Token);
            string? value;
            lock (lines) value = lines.Count == 1 ? lines[0].Trim() : null;
            return exit == 0 && value is { Length: 43 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? value : null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException) { return null; }
    }

    /// <summary>Brings this PC's host service to this PC's version when it is older (for example one from before commands
    /// between computers), once per version and session; its output goes to the host-runs log.</summary>
    private async Task KeepOwnHostServiceCurrentAsync(PairedHost host, string why)
    {
        var current = await HostSetupCommands.ThisPcGatewayVersionAsync(lifetime.Token);
        if (closing) return;
        if (current is null)
        {
            ShowNodeAgentStatus($"{host.HostId} isn't running here (is Docker Desktop started?). Commands from your other computers wait for it.");
            return;
        }
        if (!AppVersions.IsOlder(current, Version))
        {
            ShowNodeAgentStatus($"{host.HostId} runs Martlet {current} but did not hand this PC its agent token: {why}");
            return;
        }
        if (ownHostUpdateTried == Version || hostUpdatesRunning)
        {
            ShowNodeAgentStatus($"{host.HostId} runs Martlet {current}, older than commands between computers. Bringing it to {Version} " +
                "did not finish; the host-runs log shows why. Update hosts now (above) tries again.");
            return;
        }
        ownHostUpdateTried = Version;
        hostUpdatesRunning = true;
        const string title = "Keep this PC's host service current";
        var output = new LineSink(line => HostRunLog.Write(title, line));
        ShowNodeAgentStatus($"Updating {host.HostId} from Martlet {current} to {Version}, so your other computers can update and manage it from now on...");
        HostRunLog.Write(title, $"--- started: {current} -> {Version}");
        try
        {
            var target = ThisPcTarget();
            await HostLocal.EnsureImageAsync(target, status => HostRunLog.Write(title, "status: " + status), output, lifetime.Token);
            var exit = await HostLocal.EngineAsync(target, ["update"], output, lifetime.Token);
            HostRunLog.Write(title, $"--- exit {exit}");
            if (closing) return;
            if (exit == 0)
            {
                thisPcHostVersion = Version;
                ErrorLog.Info($"Updated this PC's host service from {current} to {Version}.");
                ShowNodeAgentStatus($"Updated {host.HostId} to Martlet {Version}. Your other computers can now update and manage it from there.");
                CheckHostsAsync([host]).Forget();
            }
            else ShowNodeAgentStatus($"Updating {host.HostId} to {Version} stopped (exit {exit}); the host-runs log shows why.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            HostRunLog.Write(title, "--- stopped: " + error.Message);
            if (!closing) ShowNodeAgentStatus($"Could not update {host.HostId}: {error.Message}");
        }
        finally
        {
            hostUpdatesRunning = false;
        }
    }

    /// <summary>Runs commands on the UI thread, where Martlet's update and host-service state lives.</summary>
    private sealed class LocalCommandRunner(MainWindow window) : INodeCommandRunner
    {
        public IReadOnlyList<string> Kinds => NodeCommandKinds.All;

        public Task<NodeCommandOutcome?> RunAsync(Martlet.Core.Nodes.NodeCommand command, IReadOnlyDictionary<string, string> secrets, bool resumed,
            IProgress<string> output, CancellationToken cancellationToken) =>
            window.Dispatcher.InvokeAsync(() => window.RunNodeCommandAsync(command, secrets, resumed, output, cancellationToken)).Task.Unwrap();
    }

    private async Task<NodeCommandOutcome?> RunNodeCommandAsync(Martlet.Core.Nodes.NodeCommand command, IReadOnlyDictionary<string, string> secrets, bool resumed,
        IProgress<string> output, CancellationToken token)
    {
        var here = Environment.MachineName;
        ShowNodeAgentStatus($"Running {NodeCommandAgent.Describe(command)}...");
        if (command.Kind == NodeCommandKinds.Update) return await RunUpdateCommandAsync(command, output, token);
        if (resumed) return new(false, $"Martlet on {here} restarted while it ran. Send it again.");
        var role = command.Arguments.GetValueOrDefault("role");
        string[] engine = command.Kind switch
        {
            NodeCommandKinds.Status => ["status"],
            NodeCommandKinds.DescribeRole => ["describe", role!],
            NodeCommandKinds.AddRole => ["add", role!],
            NodeCommandKinds.RemoveRole => ["remove", role!],
            _ => throw new InvalidOperationException($"Martlet on {here} does not run {command.Kind}.")
        };
        if (command.Kind == NodeCommandKinds.AddRole && CannotHand(nodeAgentHostId ?? "", role!, HostRoles.All.FirstOrDefault(r => r.Kind == role)?.Job ?? "")
            is { } cannot)
            return new(false, $"{role} can't be installed on {here}: {cannot}");
        await EnsureLocalEngineAsync(output, token);
        var target = ThisPcTarget();
        await HostLocal.EnsureImageAsync(target, output.Report, output, token);
        Dictionary<string, string>? answers = null;
        if (command.Kind == NodeCommandKinds.AddRole)
        {
            answers = new(StringComparer.Ordinal);
            foreach (var (key, value) in command.Arguments.Where(pair => pair.Key.StartsWith("choice.", StringComparison.Ordinal))) answers[key] = value;
            foreach (var (key, value) in secrets) answers[key] = value;
        }
        var exit = await HostLocal.EngineAsync(target, engine, output, token, answers: answers);
        if (exit != 0) return new(false, $"martlet-host {string.Join(' ', engine)} stopped on {here} (exit {exit}). The output shows why.", exit);
        if (command.Kind is NodeCommandKinds.AddRole or NodeCommandKinds.RemoveRole) gpuProbe = null;
        return new(true, command.Kind switch
        {
            NodeCommandKinds.Status => $"{here}'s host service status is shown above.",
            NodeCommandKinds.DescribeRole => $"Read what {role} needs on {here}.",
            NodeCommandKinds.AddRole => $"{role} is running in {here}'s host service.",
            _ => $"{role} was removed from {here}'s host service."
        }, 0);
    }

    /// <summary>Brings this PC to at least the asked version: first Martlet itself (from its GitHub Release, checked against
    /// GitHub's SHA-256 digest; Martlet restarts into it and then continues), then its host service. Returns null while it
    /// continues later (waiting until Martlet is idle, or restarting into the new version).</summary>
    private async Task<NodeCommandOutcome?> RunUpdateCommandAsync(Martlet.Core.Nodes.NodeCommand command, IProgress<string> output, CancellationToken token)
    {
        var here = Environment.MachineName;
        var wanted = System.Version.Parse(command.Arguments["version"]);
        if (System.Version.Parse(Version) < wanted)
        {
            if (readyUpdate is not { } ready || ready.Update.Version < wanted)
            {
                output.Report($"Martlet on {here} is {Version}; {command.RequestedBy} asked for {wanted.ToString(3)}. Checking Martlet's GitHub Releases...");
                await CheckForUpdatesAsync(background: true);
                if (availableUpdate is not { } offered || offered.Version < wanted)
                    return new(false, $"GitHub offers no Martlet {wanted.ToString(3)} for {here} yet " +
                        $"({(availableUpdate is { } other ? "newest: " + other.Version.ToString(3) : "nothing newer than " + Version)}).");
                if (failedInstall == offered.Version.ToString(3))
                    return new(false, $"Installing Martlet {failedInstall} on {here} failed before: {failedInstallMessage ?? "see Settings > App updates there"}");
                output.Report($"Downloading Martlet {offered.Version.ToString(3)} ({Mib(offered)}) and checking it against GitHub's SHA-256 digest...");
                await DownloadUpdateAsync();
                if (readyUpdate is not { } downloaded || downloaded.Update.Version != offered.Version)
                    return new(false, $"The download on {here} didn't finish: {UpdateStatusText.Text}");
                output.Report($"Martlet {offered.Version.ToString(3)} is downloaded and matches GitHub's SHA-256 digest. " +
                    "(The installer is not code-signed; the digest detects a damaged download, not who published it.)");
            }
            var version = readyUpdate!.Value.Update.Version.ToString(3);
            if (!IsIdleForUpdate())
            {
                if (nodeUpdateWaiting != command.Id)
                    output.Report($"{BusyReason() ?? "Someone is using Martlet"} on {here} right now, so Martlet {version} installs as soon as it is idle.");
                nodeUpdateWaiting = command.Id;
                return null;
            }
            output.Report($"Installing Martlet {version} on {here}. Martlet restarts into it and then brings its host service up to date.");
            installAfterNodeCommand = true;
            return null;
        }

        var current = await HostSetupCommands.ThisPcGatewayVersionAsync(token);
        if (current is not null && !AppVersions.IsOlder(current, Version))
            return new(true, $"{here} runs Martlet {Version}, and its host service runs {current}.", 0);
        if (hostUpdatesRunning) return null;
        await EnsureLocalEngineAsync(output, token);
        output.Report($"Updating {here}'s host service from {current ?? "an unknown version"} to {Version}. Its pairings and roles stay; " +
            "it restarts at the end, so it stops answering for a moment.");
        hostUpdatesRunning = true;
        try
        {
            var target = ThisPcTarget();
            await HostLocal.EnsureImageAsync(target, output.Report, output, token);
            var exit = await HostLocal.EngineAsync(target, ["update"], output, token);
            if (exit != 0) return new(false, $"Updating {here}'s host service stopped (exit {exit}). The output shows why.", exit);
            thisPcHostVersion = ownHostUpdateTried = Version;
            return new(true, $"{here} runs Martlet {Version}: the app and its host service.", 0);
        }
        finally { hostUpdatesRunning = false; }
    }

    /// <summary>Makes sure Docker Desktop's engine answers on this PC, starting Docker Desktop and waiting for it when needed.</summary>
    private static async Task EnsureLocalEngineAsync(IProgress<string> output, CancellationToken token)
    {
        if ((await HostLocal.ProbeEngineAsync(token)).Answered) return;
        if (!MachineInfo.DockerDesktopInstalled())
            throw new InvalidOperationException($"Docker Desktop isn't installed on {Environment.MachineName}, so its host service can't run.");
        output.Report("Starting Docker Desktop...");
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(MachineInfo.DockerDesktopPath) { UseShellExecute = true })?.Dispose(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            throw new InvalidOperationException($"Could not start Docker Desktop on {Environment.MachineName}: {error.Message}");
        }
        var deadline = DateTimeOffset.UtcNow + DockerStartPatience;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token);
            if ((await HostLocal.ProbeEngineAsync(token)).Answered)
            {
                output.Report("Docker Desktop is running.");
                return;
            }
        }
        throw new InvalidOperationException($"Docker Desktop on {Environment.MachineName} did not start within {DockerStartPatience.TotalMinutes:0} minutes.");
    }
}
