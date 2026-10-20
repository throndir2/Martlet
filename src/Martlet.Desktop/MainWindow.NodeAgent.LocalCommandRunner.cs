using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Martlet.HostClient;
using Martlet.Core.Nodes;

namespace Martlet.Desktop;

/// <summary>More of MainWindow.NodeAgent.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    /// <summary>How a command from another computer names itself to runs on this PC that wait for the same step, when it runs
    /// without a background task (a status or a reading).</summary>
    private const string NodeCommandRunTitle = "A command from your other computer";

    /// <summary>The background tasks of the commands from other computers running here now, by command ID.</summary>
    private readonly Dictionary<string, HostRunWindow> nodeCommandRuns = new(StringComparer.Ordinal);

    /// <summary>Runs commands on the UI thread, where Martlet's update and host-service state lives.</summary>
    private sealed class LocalCommandRunner(MainWindow window) : INodeCommandRunner
    {
        public IReadOnlyList<string> Kinds => NodeCommandKinds.All;

        public Task<NodeCommandOutcome?> RunAsync(Martlet.Core.Nodes.NodeCommand command, IReadOnlyDictionary<string, string> secrets, bool resumed,
            IProgress<string> output, CancellationToken cancellationToken) =>
            window.Dispatcher.InvokeAsync(() => window.RunNodeCommandAsync(command, secrets, resumed, output, cancellationToken)).Task.Unwrap();
    }

    /// <summary>Runs <paramref name="work"/> for <paramref name="command"/> as a background task out of sight, titled
    /// <paramref name="title"/> (it names the run to others that wait for the same step) with <paramref name="status"/> as its
    /// status line. What it prints goes back to the computer that asked and into the task's window. Cancel task stops it here,
    /// tells that computer so and calls <paramref name="canceledByOwner"/>; Martlet exiting leaves it for after the restart,
    /// as before.</summary>
    private async Task<NodeCommandOutcome?> RunAsBackgroundTaskAsync(Martlet.Core.Nodes.NodeCommand command, string title, string status,
        IProgress<string> output, CancellationToken token, Func<IProgress<string>, string, CancellationToken, Task<NodeCommandOutcome?>> work,
        Action? canceledByOwner = null)
    {
        NodeCommandOutcome? outcome = null;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var canceledHere = false;
        await HostRunWindow.RunAsync(this, title, async run =>
        {
            nodeCommandRuns[command.Id] = run;
            try
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, run.Token);
                run.Status(status);
                var both = new LineSink(line =>
                {
                    output.Report(line);
                    run.Output.Report(line);
                });
                outcome = await work(both, title, cancellation.Token);
            }
            catch (OperationCanceledException) when (run.Token.IsCancellationRequested && !token.IsCancellationRequested && !closing)
            {
                canceledHere = true;
                throw;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);
                throw;
            }
            finally { nodeCommandRuns.Remove(command.Id); }
            return outcome is null ? "It continues later."
                : outcome.Succeeded ? outcome.Summary : throw new InvalidOperationException(outcome.Summary);
        }, hidden: true);
        failure?.Throw();
        if (canceledHere)
        {
            canceledByOwner?.Invoke();
            return new(false, $"Canceled on {Martlet.Core.Network.ThisComputer.Name}: its owner canceled it in Background tasks.");
        }
        token.ThrowIfCancellationRequested();
        return outcome;
    }

    private async Task<NodeCommandOutcome?> RunNodeCommandAsync(Martlet.Core.Nodes.NodeCommand command, IReadOnlyDictionary<string, string> secrets, bool resumed,
        IProgress<string> output, CancellationToken token)
    {
        var here = Martlet.Core.Network.ThisComputer.Name;
        ShowNodeAgentStatus($"Running {NodeCommandsRunningText}...");
        if (command.Kind == NodeCommandKinds.Update) return await RunUpdateCommandAsync(command, output, token);
        // Taken but never started here (Martlet exited first, for example to install an update): it simply runs now.
        if (resumed && !NodeCommandStarted(command.Id)) resumed = false;
        if (resumed) return new(false, $"Martlet on {here} restarted while it ran, so send it again.");
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
        // A status or a reading takes moments; a change to this PC's host service is a background task here (Background tasks).
        if (command.Kind is NodeCommandKinds.Status or NodeCommandKinds.DescribeRole)
            return await RunEngineCommandAsync(command, secrets, role, engine, output, NodeCommandRunTitle, token);
        var name = role is null ? "" : HostRoles.Names([role]);
        return await RunAsBackgroundTaskAsync(command, NodeCommandAgent.Describe(command), command.Kind switch
        {
            NodeCommandKinds.AddRole => $"Installing {name} on this PC for {command.RequestedBy}; this can take a while...",
            NodeCommandKinds.RemoveRole => $"Removing {name} from this PC for {command.RequestedBy}...",
            _ => $"Changing how this PC's host service is reached from outside home, for {command.RequestedBy}..."
        }, output, token, (lines, title, cancel) => RunEngineCommandAsync(command, secrets, role, engine, lines, title, cancel));
    }

    /// <summary>Runs a command from another computer with this PC's host engine (<paramref name="engine"/>), as
    /// <paramref name="by"/> for runs that wait for the same step.</summary>
    private async Task<NodeCommandOutcome?> RunEngineCommandAsync(Martlet.Core.Nodes.NodeCommand command, IReadOnlyDictionary<string, string> secrets,
        string? role, string[] engine, IProgress<string> output, string by, CancellationToken token)
    {
        var here = Martlet.Core.Network.ThisComputer.Name;
        await EnsureLocalEngineAsync(output, token);
        var target = await HostLocal.EngineForChangeAsync(ThisPcTarget(), role, output.Report, output, token, by);
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
            return new(false, $"{here}'s host stayed busy with another change ({busy}), so nothing changed; send it again when that finishes.", exit);
        if (exit != 0) return new(false, $"martlet-host {string.Join(' ', engine)} stopped on {here} (exit {exit}); the output shows why.", exit);
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
        var here = Martlet.Core.Network.ThisComputer.Name;
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
                output.Report($"Martlet on {here} is {Version} and {command.RequestedBy} asked for {wanted.ToString(3)}; checking Martlet's GitHub Releases...");
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
                    // You canceled the download here (Background tasks): it isn't started again for this command.
                    if (readyUpdate?.Update.Version != offered.Version && await DownloadUpdateAsync())
                        return new(false, $"The download of Martlet {offered.Version.ToString(3)} on {here} was canceled there (Background tasks).");
                }
                if (readyUpdate is not { } downloaded || downloaded.Update.Version != offered.Version)
                    return new(false, $"The download on {here} didn't finish: {UpdateStatusText.Text}");
                output.Report($"Martlet {offered.Version.ToString(3)} is downloaded and matches GitHub's SHA-256 digest " +
                    "(the installer is not code-signed, so the digest detects a damaged download, not who published it).");
            }
            var installing = readyUpdate!.Value.Update;
            var version = installing.Version.ToString(3);
            announcedUpdate = installing.Tag;
            // Installed beside the running Martlet first, so Martlet there is away only for a restart of a few seconds.
            if (!UpdatePrepared)
            {
                if (stagingCanceledVersion == installing.Version && stagingTask is not { IsCompleted: false })
                {
                    stagingCanceledVersion = null;
                    return new(false, $"Installing Martlet {version} on {here} was canceled there (Background tasks).");
                }
                if (stagingTask is not { IsCompleted: false }) StageUpdateAsync(asked: true).Forget();
                return Wait("Martlet is installing the update beside the running one",
                    $"Installing Martlet {version} on {here} beside the running Martlet, so it restarts into it in seconds...");
            }
            if (InstallBlocker(asked: true) is { } blocker)
                return Wait(blocker, $"Martlet {version} installs on {here} as soon as nothing needs Martlet there (waiting: {blocker}).");
            output.Report(UpdateStaged
                ? $"Restarting Martlet on {here} into {version}, installed beside it; it is back in a few seconds and then " +
                  "brings its host service up to date."
                : $"Installing Martlet {version} on {here} in the background with no installer window; Martlet restarts " +
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
            return await RunAsBackgroundTaskAsync(command, $"Update this PC's host service to Martlet {Version} (from {command.RequestedBy})",
                $"Updating this PC's host service to Martlet {Version} for {command.RequestedBy}...", output, token, async (lines, by, cancel) =>
                {
                    await EnsureLocalEngineAsync(lines, cancel);
                    lines.Report($"Updating {here}'s host service from {current ?? "an unknown version"} to {Version}; its pairings and roles stay, but " +
                        "it stops answering for a moment when it restarts at the end.");
                    var target = ThisPcTarget();
                    await HostLocal.EnsureImageAsync(target, lines.Report, lines, cancel, by);
                    // A change already running on this host (an install, for example) finishes first; the output says so.
                    var engineOutput = new EngineOutput(lines);
                    var exit = await HostLocal.EngineAsync(target, ["update"], engineOutput, cancel);
                    if (engineOutput.Busy(exit) is { } busy)
                        return new(false, $"{here}'s host stayed busy with another change ({busy}), so its host service wasn't updated; send the update again when that finishes.", exit);
                    if (exit != 0) return new(false, $"Updating {here}'s host service stopped (exit {exit}); the output shows why.", exit);
                    thisPcHostVersion = Version;
                    HostUpdateSettled(ThisPcHostId);
                    return new(true, $"{here} runs Martlet {Version}: the app and its host service.", 0);
                },
                // Keeping this PC's host service current doesn't start the update you canceled again by itself.
                canceledByOwner: () => ownHost.Failed(Version));
        }
        finally { hostUpdatesRunning = false; }
    }

    /// <summary>Makes sure Docker Desktop's engine answers on this PC, starting Docker Desktop and waiting for it when needed.</summary>
    private static async Task EnsureLocalEngineAsync(IProgress<string> output, CancellationToken token)
    {
        if ((await HostLocal.ProbeEngineAsync(token)).Answered) return;
        if (!MachineInfo.DockerDesktopInstalled())
            throw new InvalidOperationException($"Docker Desktop isn't installed on {Martlet.Core.Network.ThisComputer.Name}, so its host service can't run.");
        output.Report("Starting Docker Desktop...");
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(MachineInfo.DockerDesktopPath) { UseShellExecute = true })?.Dispose(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            throw new InvalidOperationException($"Could not start Docker Desktop on {Martlet.Core.Network.ThisComputer.Name}: {error.Message}");
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
        throw new InvalidOperationException($"Docker Desktop on {Martlet.Core.Network.ThisComputer.Name} did not start within {DockerStartPatience.TotalMinutes:0} minutes.");
    }
}
