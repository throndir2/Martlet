using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Nodes;

namespace Martlet.Desktop;

/// <summary>This PC as a node your other Martlet computers can command. When this PC runs a host service (Docker Desktop),
/// Martlet here is that host's agent: every few seconds it asks the host's gateway for commands paired computers sent (with
/// the token the gateway wrote where only this PC can read it), runs them side by side in the background (update Martlet and
/// the host service, add or remove a role, status; an update runs alone) and streams their output back. This PC's host
/// service follows this PC's version by itself (<see cref="FollowOwnHostAsync"/>), so a host service from before commands
/// existed gets them too. On by default; Settings › Your other computers turns it off (node-commands.txt).</summary>
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
    /// <summary>What the update another computer asked for waits for here, and when the asker was last told.</summary>
    private string? nodeUpdateBlocker;
    private DateTimeOffset nodeUpdateToldAt;
    /// <summary>The commands from other computers this PC runs right now, side by side, from the moment they are taken (an
    /// install waits for them to finish).</summary>
    private IReadOnlyList<Martlet.Core.Nodes.NodeCommand> NodeCommandsRunning => nodeAgent?.Running ?? [];
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

    /// <summary>node-command.txt holds the IDs of the latest commands from other computers whose martlet-host run started here
    /// (one per line, newest last), so after a restart Martlet can tell a command it only took (which it then simply runs)
    /// from one cut off midway.</summary>
    private const string NodeCommandStartedFile = "node-command.txt";
    private const int NodeCommandsRemembered = 32;

    private bool NodeCommandStarted(string id)
    {
        if (store is null) return true;
        try { return File.ReadAllLines(Path.Combine(store.DataDirectory, NodeCommandStartedFile)).Any(line => line.Trim() == id); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    private void MarkNodeCommandStarted(string id)
    {
        if (store is null) return;
        var path = Path.Combine(store.DataDirectory, NodeCommandStartedFile);
        try
        {
            List<string> started;
            try { started = File.ReadAllLines(path).Select(line => line.Trim()).Where(line => line.Length > 0 && line != id).ToList(); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { started = []; }
            started.Add(id);
            File.WriteAllLines(path, started.TakeLast(NodeCommandsRemembered));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn("Could not note which command from another computer started here", error);
        }
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

    /// <summary>A command from another computer that continues later (an update waiting until nothing needs Martlet here).</summary>
    private Martlet.Core.Nodes.NodeCommand? pendingNodeCommand;

    private async Task NodeAgentTickAsync()
    {
        if (nodeAgentBusy || closing || store is null) return;
        var host = NetworkMap.Hosts(Inputs()).FirstOrDefault(h => h.Method == HostSetupMethod.ThisPcDocker);
        nodeAgentBusy = true;
        try
        {
            // Commands run in the background, side by side; those that ended since the last tick report here.
            if (nodeAgent is { } previous)
                foreach (var ended in previous.Collect()) NodeCommandEnded(ended, host?.HostId == nodeAgentHostId ? host : null);
            // Exiting (for example to install an update): take no new command that the exit would cut off.
            if (closing || exiting) return;
            if (!nodeCommandsAllowed)
            {
                ShowNodeAgentStatus(NodeCommandsOffText);
                return;
            }
            if (host is null)
            {
                ShowNodeAgentStatus("This PC runs no host service, so there is nothing here for your other computers to manage. " +
                    "Martlet on this PC still updates itself as set in App updates.");
                return;
            }
            if (nodeAgent is null || nodeAgentHostId != host.HostId && nodeAgent.Running.Count == 0)
            {
                nodeAgent = new(ReadAgentTokenAsync, Version, new LocalCommandRunner(this));
                nodeAgentHostId = host.HostId;
            }
            // Commands still running for this PC's previous host service end first.
            if (nodeAgentHostId != host.HostId) return;
            var agent = nodeAgent;
            var pairing = host.Pairing;
            var pass = await agent.TakeAsync(() => ClusterSync.Connect(pairing), lifetime.Token);
            if (closing) return;
            switch (pass.Kind)
            {
                case NodeAgentPassKind.Idle or NodeAgentPassKind.Started:
                    ShowNodeAgentRunning(host);
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
            if (!closing) ShowNodeAgentStatus($"Could not reach {host?.HostId ?? "this PC's host service"}: {error.Message}");
        }
        finally { nodeAgentBusy = false; }
    }

    /// <summary>A command from another computer ended here, or continues later (an update that waits, or one Martlet restarts
    /// into).</summary>
    private void NodeCommandEnded(NodeAgentPass pass, PairedHost? host)
    {
        if (pass.Kind == NodeAgentPassKind.Pending)
        {
            pendingNodeCommand = pass.Command;
            if (installAfterNodeCommand)
            {
                installAfterNodeCommand = false;
                ErrorLog.Info("Installing a Martlet update another computer asked for: " + pass.Text);
                InstallNow(unattended: true);
            }
            return;
        }
        if (pendingNodeCommand?.Id == pass.Command?.Id) pendingNodeCommand = null;
        lastNodeCommand = $"{pass.Text} ({DateTime.Now:t})";
        ErrorLog.Info("Ran a command from another computer: " + pass.Text);
        if (host is not null && pass.Command?.Kind is not (NodeCommandKinds.Status or NodeCommandKinds.DescribeRole))
            CheckHostsAsync([host]).Forget();
    }

    /// <summary>The commands from other computers running here, in words.</summary>
    private string NodeCommandsRunningText => NodeCommandsRunning switch
    {
        [] => "no command from another computer",
        [var one] => NodeCommandAgent.Describe(one),
        var several => $"{several.Count} commands from your other computers side by side: " + string.Join("; ", several.Select(NodeCommandAgent.Describe))
    };

    private void ShowNodeAgentRunning(PairedHost host)
    {
        if (!installAfterNodeCommand && nodeUpdateBlocker is { } blocker && pendingNodeCommand is { } waiting && nodeUpdateWaiting == waiting.Id)
            ShowNodeAgentStatus($"{NodeCommandAgent.Describe(waiting)} waits until nothing needs Martlet here. Now: {blocker}.");
        else if (NodeCommandsRunning.Count > 0)
            ShowNodeAgentStatus($"On. Running {NodeCommandsRunningText}." + LastNodeCommandText);
        else
            ShowNodeAgentStatus($"On. Your other computers can update Martlet here and manage {host.HostId} (roles, status) " +
                "through its paired connection; several of their commands run side by side." + LastNodeCommandText);
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

    /// <summary>This PC's host service is older than commands between computers (or didn't hand this PC its agent token): it is
    /// brought to this PC's version by the same background update that follows every Martlet update
    /// (<see cref="FollowOwnHostAsync"/>), and this says where that stands.</summary>
    private async Task KeepOwnHostServiceCurrentAsync(PairedHost host, string why)
    {
        await FollowOwnHostAsync();
        if (closing) return;
        ShowNodeAgentStatus(ownHostSeen switch
        {
            null or { Step: OwnHostStep.NotFound } =>
                $"{host.HostId} isn't running here (is Docker Desktop started?). Commands from your other computers wait for it.",
            { Step: OwnHostStep.Current, Version: var current } =>
                $"{host.HostId} runs Martlet {current} but did not hand this PC its agent token: {why}",
            _ => $"{host.HostId}: {OwnHostUpdateText.Text}"
        });
    }

    /// <summary>How a command from another computer names itself to runs on this PC that wait for the same step.</summary>
    private const string NodeCommandRunTitle = "A command from your other computer";

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
        ShowNodeAgentStatus($"Running {NodeCommandsRunningText}...");
        if (command.Kind == NodeCommandKinds.Update) return await RunUpdateCommandAsync(command, output, token);
        // Taken but never started here (Martlet exited first, for example to install an update): it simply runs now.
        if (resumed && !NodeCommandStarted(command.Id)) resumed = false;
        if (resumed) return new(false, $"Martlet on {here} restarted while it ran. Send it again.");
        // Martlet is exiting: leave it waiting for after the restart rather than start what the exit would cut off.
        if (closing || exiting) return null;
        var role = command.Arguments.GetValueOrDefault("role");
        string[] engine = command.Kind switch
        {
            NodeCommandKinds.Status => ["status"],
            NodeCommandKinds.DescribeRole => ["describe", role!],
            NodeCommandKinds.AddRole => ["add", role!],
            NodeCommandKinds.RemoveRole => ["remove", role!],
            // Checked against the closed argument rules again here: only known options and canonical addresses reach the engine.
            NodeCommandKinds.Exposure => ["exposure", .. NodeCommandRules.ExposureOptions(command.Arguments)],
            _ => throw new InvalidOperationException($"Martlet on {here} does not run {command.Kind}.")
        };
        if (command.Kind == NodeCommandKinds.AddRole && CannotHand(nodeAgentHostId ?? "", role!, HostRoles.All.FirstOrDefault(r => r.Kind == role)?.Job ?? "")
            is { } cannot)
            return new(false, $"{role} can't be installed on {here}: {cannot}");
        await EnsureLocalEngineAsync(output, token);
        var target = await HostLocal.EngineForChangeAsync(ThisPcTarget(), role, output.Report, output, token, NodeCommandRunTitle);
        Dictionary<string, string>? answers = null;
        if (command.Kind == NodeCommandKinds.AddRole)
        {
            answers = new(StringComparer.Ordinal);
            foreach (var (key, value) in command.Arguments.Where(pair => pair.Key.StartsWith("choice.", StringComparison.Ordinal))) answers[key] = value;
            foreach (var (key, value) in secrets) answers[key] = value;
        }
        // A change waits only for one on this host it collides with (the same role, or a setup or update); its output says so.
        var engineOutput = new EngineOutput(output);
        MarkNodeCommandStarted(command.Id);
        var exit = await HostLocal.EngineAsync(target, engine, engineOutput, token, answers: answers);
        if (engineOutput.Busy(exit) is { } busy)
            return new(false, $"{here}'s host stayed busy with another change ({busy}), so nothing was changed. Send it again when that finishes.", exit);
        if (exit != 0) return new(false, $"martlet-host {string.Join(' ', engine)} stopped on {here} (exit {exit}). The output shows why.", exit);
        if (command.Kind is NodeCommandKinds.AddRole or NodeCommandKinds.RemoveRole) gpuProbe = null;
        return new(true, command.Kind switch
        {
            NodeCommandKinds.Status => $"{here}'s host service status is shown above.",
            NodeCommandKinds.DescribeRole => $"Read what {role} needs on {here}.",
            NodeCommandKinds.AddRole => $"{role} is running in {here}'s host service.",
            NodeCommandKinds.Exposure => $"{here}'s host service serves with its new outside access settings.",
            _ => $"{role} was removed from {here}'s host service."
        }, 0);
    }

    /// <summary>Brings this PC to at least the asked version: first Martlet itself (from its GitHub Release, checked against
    /// GitHub's SHA-256 digest; Martlet restarts into it and then continues), then its host service. Returns null while it
    /// continues later: while it waits until nothing needs Martlet here (a reply, a setup task, another host
    /// update), telling the computer that asked what it waits for, or while Martlet restarts into the new version.</summary>
    private async Task<NodeCommandOutcome?> RunUpdateCommandAsync(Martlet.Core.Nodes.NodeCommand command, IProgress<string> output, CancellationToken token)
    {
        var here = Environment.MachineName;
        var wanted = System.Version.Parse(command.Arguments["version"]);
        NodeCommandOutcome? Wait(string why, string message)
        {
            var now = DateTimeOffset.UtcNow;
            if (nodeUpdateWaiting != command.Id || nodeUpdateBlocker != why && now - nodeUpdateToldAt >= TimeSpan.FromMinutes(1))
            {
                output.Report(message);
                nodeUpdateToldAt = now;
            }
            nodeUpdateWaiting = command.Id;
            nodeUpdateBlocker = why;
            return null;
        }

        if (System.Version.Parse(Version) < wanted)
        {
            // A check or download already under way (the periodic one) is joined rather than mistaken for a failure.
            await WaitForUpdateWorkAsync(token);
            if (readyUpdate is not { } ready || ready.Update.Version < wanted)
            {
                output.Report($"Martlet on {here} is {Version}; {command.RequestedBy} asked for {wanted.ToString(3)}. Checking Martlet's GitHub Releases...");
                await CheckForUpdatesAsync(background: true);
                if (availableUpdate is not { } offered || offered.Version < wanted)
                    return new(false, $"GitHub offers no Martlet {wanted.ToString(3)} for {here} yet " +
                        $"({(availableUpdate is { } other ? "newest: " + other.Version.ToString(3) : "nothing newer than " + Version)}).");
                if (failedInstall == offered.Version.ToString(3))
                    return new(false, $"Installing Martlet {failedInstall} on {here} failed before: {failedInstallMessage ?? "see Settings > App updates there"}");
                // This command installs it, so the periodic check doesn't also ask here whether to install it.
                announcedUpdate = offered.Tag;
                output.Report($"Downloading Martlet {offered.Version.ToString(3)} ({Mib(offered)}) and checking it against GitHub's SHA-256 digest...");
                for (var attempt = 0; attempt < 3 && readyUpdate?.Update.Version != offered.Version && !closing; attempt++)
                {
                    await WaitForUpdateWorkAsync(token);
                    if (readyUpdate?.Update.Version != offered.Version) await DownloadUpdateAsync();
                }
                if (readyUpdate is not { } downloaded || downloaded.Update.Version != offered.Version)
                    return new(false, $"The download on {here} didn't finish: {UpdateStatusText.Text}");
                output.Report($"Martlet {offered.Version.ToString(3)} is downloaded and matches GitHub's SHA-256 digest. " +
                    "(The installer is not code-signed; the digest detects a damaged download, not who published it.)");
            }
            var installing = readyUpdate!.Value.Update;
            var version = installing.Version.ToString(3);
            announcedUpdate = installing.Tag;
            if (InstallBlocker(asked: true) is { } blocker)
                return Wait(blocker, $"Martlet {version} installs on {here} as soon as nothing needs Martlet there. Waiting: {blocker}.");
            output.Report($"Installing Martlet {version} on {here} in the background, with no installer window. Martlet restarts " +
                "into it by itself and then brings its host service up to date.");
            nodeUpdateBlocker = null;
            installAfterNodeCommand = true;
            return null;
        }

        var current = await HostSetupCommands.ThisPcGatewayVersionAsync(token);
        if (current is not null && !AppVersions.IsOlder(current, Version))
        {
            if (!hostUpdates.IsUpdating(ThisPcHostId)) HostUpdateSettled(ThisPcHostId, current, seen: true);
            return new(true, $"{here} runs Martlet {Version}, and its host service runs {current}.", 0);
        }
        if (hostUpdatesRunning || hostUpdates.IsUpdating(ThisPcHostId))
            return Wait("another update of this PC's host service is running",
                $"Another update of {here}'s host service is running; this one continues when it ends.");
        nodeUpdateBlocker = null;
        // Claimed before the first await, so the automatic pass can't start a second update of this host meanwhile.
        hostUpdatesRunning = true;
        using var updating = hostUpdates.Begin(ThisPcHostId);
        try
        {
            await EnsureLocalEngineAsync(output, token);
            output.Report($"Updating {here}'s host service from {current ?? "an unknown version"} to {Version}. Its pairings and roles stay; " +
                "it restarts at the end, so it stops answering for a moment.");
            var target = ThisPcTarget();
            await HostLocal.EnsureImageAsync(target, output.Report, output, token, NodeCommandRunTitle);
            // A change already running on this host (an install, for example) finishes first; the output says so.
            var engineOutput = new EngineOutput(output);
            var exit = await HostLocal.EngineAsync(target, ["update"], engineOutput, token);
            if (engineOutput.Busy(exit) is { } busy)
                return new(false, $"{here}'s host stayed busy with another change ({busy}), so its host service wasn't updated. Send the update again when that finishes.", exit);
            if (exit != 0) return new(false, $"Updating {here}'s host service stopped (exit {exit}). The output shows why.", exit);
            thisPcHostVersion = Version;
            HostUpdateSettled(ThisPcHostId);
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
