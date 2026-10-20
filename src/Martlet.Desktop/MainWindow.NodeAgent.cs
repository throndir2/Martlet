using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Martlet.HostClient;
using Martlet.Core.Nodes;

namespace Martlet.Desktop;

/// <summary>This PC as a node your other Martlet computers can command. When this PC runs a host service (Docker Desktop),
/// Martlet here is that host's agent: every few seconds it asks the host's gateway for commands paired computers sent (with
/// the token the gateway wrote where only this PC can read it), runs them side by side in the background (update Martlet and
/// the host service, add or remove a role, status; an update runs alone) and streams their output back. Each change to this
/// PC's host service is a background task here too, out of sight (Background tasks lists it, with its output, and can cancel
/// it). This PC's host
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
        "Off: your other computers can't update Martlet here or manage this PC's host service until you turn this on.";

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
            ShowNodeAgentStatus(allowed ? "On: checking this PC's host service for commands..." : NodeCommandsOffText);
            if (allowed) NodeAgentTickAsync().Forget();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            changingNodeChoice = true;
            AllowNodeCommands.IsChecked = nodeCommandsAllowed;
            changingNodeChoice = false;
            ShowNodeAgentStatus("Could not save node-commands.txt, so the previous choice stays; check access to your data directory.");
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
                ShowNodeAgentStatus("This PC runs no host service, so your other computers have nothing to manage here.");
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
                    ShowNodeAgentStatus($"{host.HostId} isn't answering ({pass.Text}), so commands from your other computers wait there.");
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (ClusterSync.IsHostFailure(error))
        {
            if (!closing) ShowNodeAgentStatus($"Could not reach {host?.HostId ?? "this PC's host service"}: {error.Message}");
        }
        finally
        {
            nodeAgentBusy = false;
            // Role changes and updates other computers asked for show this PC as Configuring while they run.
            if (!closing) ShowConfiguring();
        }
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
            ShowNodeAgentStatus($"{NodeCommandAgent.Describe(waiting)} waits until nothing needs Martlet here (now: {blocker}).");
        else if (NodeCommandsRunning.Count > 0)
            ShowNodeAgentStatus($"On: running {NodeCommandsRunningText}." + LastNodeCommandText);
        else
            ShowNodeAgentStatus($"On: your other computers can update Martlet here and manage {host.HostId} (roles, status) " +
                "over its paired connection." + LastNodeCommandText);
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
                $"{host.HostId} isn't running here (is Docker Desktop started?), so commands from your other computers wait for it.",
            { Step: OwnHostStep.Current, Version: var current } =>
                $"{host.HostId} runs Martlet {current} but did not hand this PC its agent token: {why}",
            _ => $"{host.HostId}: {OwnHostUpdateText.Text}"
        });
    }
}
