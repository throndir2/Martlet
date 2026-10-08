using System.IO;
using System.Windows;
using System.Windows.Threading;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Cluster;
using Martlet.Core.Nodes;
using Martlet.Core.Planning;
using Martlet.Core.Sync;

namespace Martlet.Desktop;

/// <summary>Applies the recommended setup to all your computers (docs/CLUSTER.md, "Applying the recommended setup") and shows
/// the Configuring state everywhere ("Configuring"). <see cref="PrepareRecommendedSetupAsync"/> reads what the review must
/// show before Reconfigure (each role's terms, secrets, downloads, changes that need someone at a computer or can't be made);
/// <see cref="ApplyRecommendedSetupAsync"/> makes the changes in order through Martlet's existing paths: host roles through
/// Martlet on that computer (node commands), this PC's own host service or SSH; who does each job through the shared cluster
/// plan with failover on; Devices › Sharing work; then one cluster check, so every companion PC follows within one check. The
/// run is published as this PC's setup-run entry in the shared settings, so every computer shows Configuring.</summary>
public partial class MainWindow
{
    /// <summary>How the engine runs on this PC name a recommended-setup change to other runs that wait for the same step.</summary>
    private const string SetupRunTitle = "Reconfiguring your computers";
    /// <summary>How long Prepare waits for Martlet on a host to read a role there.</summary>
    private static readonly TimeSpan DescribePatience = TimeSpan.FromMinutes(2);
    /// <summary>How long a role change waits for Martlet on that host to take it.</summary>
    private static readonly TimeSpan TakePatience = TimeSpan.FromMinutes(10);

    /// <summary>The run this PC started last (also published in the shared settings).</summary>
    private SetupRun? setupRun;
    private bool setupApplying;
    /// <summary>This PC switches a job to follow the shared plan right now (a plan change made elsewhere), in words.</summary>
    private string? clusterFollowingText;
    private string? configuringShown;
    /// <summary>The map's Configuring states as <see cref="ShowConfiguring"/> last worked them out.</summary>
    private IReadOnlyDictionary<string, string> configuringMachines = new Dictionary<string, string>();

    /// <summary>What the owner must see or accept before Reconfigure. Reads each role to install from its computer (martlet-host
    /// describe there); changes nothing.</summary>
    internal Task<SetupRunPreflight> PrepareRecommendedSetupAsync(NetworkRecommendation recommendation, CancellationToken cancel) =>
        SetupExecutor.PrepareAsync(recommendation, new SetupTargets(this), cancel);

    /// <summary>Applies <paramref name="recommendation"/> after the owner's Reconfigure in the review that showed
    /// <paramref name="preflight"/> (the click accepts the terms it showed). Makes the changes in order, continues past a failed
    /// one and reports each computer; never throws for a failed step.</summary>
    internal async Task<SetupRunOutcome> ApplyRecommendedSetupAsync(NetworkRecommendation recommendation, SetupRunPreflight preflight,
        IProgress<SetupRun>? progress, CancellationToken cancel)
    {
        if (setupApplying) throw new InvalidOperationException("Martlet is already reconfiguring your computers.");
        setupApplying = true;
        try
        {
            ErrorLog.Info($"Recommended setup: reconfiguring your computers ({recommendation.Changes.Count} changes, " +
                $"{recommendation.Changes.Select(c => SetupExecutor.RunMachine(c, ClusterDevice)).Distinct(StringComparer.Ordinal).Count()} computers).");
            var outcome = await SetupExecutor.ApplyAsync(recommendation, preflight, new SetupTargets(this), progress, cancel);
            foreach (var step in outcome.Steps)
                if (step.State == SetupMachineState.Done) ErrorLog.Info($"Recommended setup: {step.Change.Summary} Done: {step.Text}");
                else ErrorLog.Warn($"Recommended setup: {step.Change.Summary} {step.State}: {step.Text}");
            if (!closing) ActionText.Text = outcome.Summary;
            return outcome;
        }
        finally
        {
            setupApplying = false;
            if (!closing) ShowConfiguring();
        }
    }

    // ---------- the Configuring state ----------

    /// <summary>What is being configured now: the text for Home (null: nothing) and, by computer (a host ID, a device ID or
    /// "this-pc"), the step it works on, for the Devices map. From every computer's published run, the role changes this PC
    /// sends a host (<see cref="HostActivity"/>), the commands other computers sent this PC's own host service, and this PC
    /// following a plan change.</summary>
    internal (string? Text, IReadOnlyDictionary<string, string> Machines, bool Active, bool Trouble) ConfiguringNow(DateTimeOffset now)
    {
        var machines = new Dictionary<string, string>(StringComparer.Ordinal);
        // This PC's own host service ID is left as it is: the map draws it on this PC (and Inputs() must not be called from here).
        string Local(string id) => id == ClusterDevice ? DeviceCapacityInputs.ThisPcId : id;
        var runs = settingsNode is null ? new List<SetupRun>() : SetupRun.All(settingsNode.Document).ToList();
        if (setupRun is { } mine && runs.All(r => r.RunId != mine.RunId)) runs.Add(mine);
        string? text = null;
        var active = false;
        var trouble = false;
        foreach (var run in runs.Where(r => r.Shown(now)).OrderByDescending(r => r.UpdatedAt))
        {
            if (text is null)
            {
                text = run.Summary(now) + (run.StartedBy == ClusterDevice ? "" : $" Started on {run.StartedBy}.");
                trouble = run.Finished && run.Machines.Any(m => m.State is SetupMachineState.Failed or SetupMachineState.NeedsAttention);
            }
            if (!run.Active(now)) continue;
            active = true;
            foreach (var machine in run.Configuring) machines.TryAdd(Local(machine.MachineId), machine.Step ?? "Working");
        }
        foreach (var (host, step) in HostActivity.Now) machines.TryAdd(Local(host), step);
        foreach (var command in NodeCommandsRunning.Where(c => c.Kind is NodeCommandKinds.AddRole or NodeCommandKinds.RemoveRole or NodeCommandKinds.Update))
            machines.TryAdd(DeviceCapacityInputs.ThisPcId, NodeCommandAgent.Describe(command));
        if (clusterFollowingText is { } following) machines.TryAdd(DeviceCapacityInputs.ThisPcId, following);
        if (machines.Count > 0) active = true;
        if (text is null && machines.Count > 0)
            text = "Configuring " + string.Join("; ", machines.Take(3).Select(m =>
                $"{(m.Key == DeviceCapacityInputs.ThisPcId ? "this PC" : m.Key)}: {m.Value}")) + (machines.Count > 3 ? $" and {machines.Count - 3} more." : ".");
        return (text, machines, active, trouble);
    }

    /// <summary>Shows <see cref="ConfiguringNow"/> on Home (both homes), the tray and, when it changed, the Devices map.</summary>
    private void ShowConfiguring()
    {
        if (closing) return;
        var (text, machines, active, trouble) = ConfiguringNow(DateTimeOffset.UtcNow);
        var brush = active ? "AccentBrush" : trouble ? "WarningBrush" : "SuccessBrush";
        foreach (var (indicator, status, dot) in new[] { (ConfiguringIndicator, ConfiguringStatusText, ConfiguringDot),
                     (HostConfiguringIndicator, HostConfiguringStatusText, HostConfiguringDot) })
        {
            indicator.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
            if (status.Text != (text ?? "")) status.Text = text ?? "";
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        }
        var signature = string.Join("|", machines.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => m.Key + "=" + m.Value));
        if (signature != configuringShown)
        {
            configuringShown = signature;
            configuringMachines = machines;
            if (DevicesPage.IsVisible) RenderMap();
        }
        UpdateTray();
    }

    /// <summary>The tray tooltip's line while computers are configured, or null.</summary>
    private string? TrayConfiguringText() => configuringMachines.Count switch
    {
        0 => null,
        1 => "Configuring a computer",
        var count => $"Configuring {count} computers"
    };

    private void Configuring_Click(object sender, RoutedEventArgs e) => Navigate(NavDevices);

    /// <summary>The map's Configuring states by computer, as last shown (see <see cref="ConfiguringNow"/>).</summary>
    private IReadOnlyDictionary<string, string> ConfiguringMachines() => configuringMachines;

    private void InitializeConfiguring() => HostActivity.Changed += () => Dispatcher.InvokeAsync(ShowConfiguring, DispatcherPriority.Background);

    // ---------- the paths a run takes ----------

    /// <summary>The run's side effects through this PC's real paths (see <see cref="ISetupTargets"/>).</summary>
    private sealed class SetupTargets(MainWindow window) : ISetupTargets
    {
        public string Device => ClusterDevice;

        /// <summary>"this-pc" is this PC's own host service.</summary>
        private PairedHost? Host(string machineId) =>
            window.FindHost(machineId == DeviceCapacityInputs.ThisPcId || machineId == ClusterDevice ? window.OwnHostId() : machineId);

        public SetupReach Reach(string machineId)
        {
            if (Host(machineId) is not { } host)
                return new(false, machineId == ClusterDevice || machineId == DeviceCapacityInputs.ThisPcId ||
                    window.OtherComputers().Any(c => c.DeviceId == machineId)
                        ? $"{machineId} runs no host service, so it can't run host roles."
                        : $"{machineId} isn't paired with this PC. It pairs by itself once it's in your Martlet network; check again then.");
            if (window.store is null) return new(false, "Martlet can't use its data folder on this PC.");
            return host.Method switch
            {
                HostSetupMethod.ThisPcDocker or HostSetupMethod.Agent => SetupReach.Yes,
                HostSetupMethod.SshDocker or HostSetupMethod.SshNative when !string.IsNullOrWhiteSpace(host.SshTarget) => SetupReach.Yes,
                _ => new(false, $"Martlet doesn't know how to reach {host.HostId}'s host service. Set its connection method on the Devices map, " +
                    $"or make this change on {host.HostId}.", SomeoneThere: true)
            };
        }

        public MachineSpecs? Specs(string machineId)
        {
            var host = Host(machineId);
            if (host?.Method == HostSetupMethod.ThisPcDocker || machineId == DeviceCapacityInputs.ThisPcId || machineId == ClusterDevice)
                return DeviceCapacityInputs.ThisPc(window.machine, null);
            return window.HardwareStore?.Find(host?.HostId ?? machineId) is { } report ? MachineSpecs.FromHostHardware(report) : null;
        }

        public string RoleName(string kind) => HostRoles.Names([kind]);

        public async Task<SetupRoleNeeds> DescribeAsync(string machineId, string roleKind, CancellationToken cancel)
        {
            var host = Host(machineId) ?? throw new InvalidOperationException($"{machineId} isn't paired with this PC.");
            var output = new LineSink(_ => { });
            var inputs = host.Method switch
            {
                HostSetupMethod.ThisPcDocker => await HostLocal.DescribeAsync(await ReadingEngineAsync(roleKind, output, cancel), roleKind, output, cancel),
                HostSetupMethod.SshDocker or HostSetupMethod.SshNative => await OverSshAsync(host, (remote, target, sudo, key) =>
                    remote.DescribeAsync(target, roleKind, sudo, key, output, cancel), cancel),
                _ => await ThroughAgentAsync(host, async connection =>
                {
                    var command = await SetupHostCommands.RunAsync(connection, host.HostId, NodeCommandKinds.DescribeRole,
                        new Dictionary<string, string> { ["role"] = roleKind }, null, DescribePatience, _ => { }, cancel);
                    if (command.State != NodeCommandState.Succeeded)
                        throw new InvalidOperationException(command.Summary ?? $"Martlet on {host.HostId} couldn't read {roleKind}.");
                    return HostRemote.ParseRole(command.Output.Where(line => line.StartsWith("role.", StringComparison.Ordinal)));
                }, cancel)
            };
            return SetupRoleReading.Needs(inputs);
        }

        public async Task<SetupStepResult> ChangeRoleAsync(SetupRoleCommand command, IProgress<string> progress, CancellationToken cancel)
        {
            var host = Host(command.MachineId) ?? throw new InvalidOperationException($"{command.MachineId} isn't paired with this PC.");
            var name = RoleName(command.RoleKind);
            using var activity = HostActivity.Begin(host.HostId, (command.Add ? "Installing " : "Removing ") + name);
            var answers = new Dictionary<string, string>(command.Arguments, StringComparer.Ordinal);
            foreach (var (key, value) in command.Secrets) answers[key] = value;
            string[] engine = [command.Add ? "add" : "remove", command.RoleKind];
            var output = new EngineOutput(progress);
            var here = host.Method == HostSetupMethod.ThisPcDocker ? "this PC" : host.HostId;
            int exit;
            switch (host.Method)
            {
                case HostSetupMethod.ThisPcDocker:
                    var local = await LocalEngineAsync(command.RoleKind, progress, cancel);
                    exit = await HostLocal.EngineAsync(local, engine, output, cancel, answers: command.Add ? answers : null);
                    window.gpuProbe = null;
                    break;
                case HostSetupMethod.SshDocker or HostSetupMethod.SshNative:
                    exit = await OverSshAsync(host, async (remote, target, sudo, key) =>
                    {
                        var supplied = await remote.SupplyIfOfflineAsync(target, command.Add ? HostVerb.Add : HostVerb.Remove,
                            window.store!.DataDirectory, key, progress, cancel);
                        return (await remote.RunAsync(target, string.Join(' ', engine), false, sudo, command.Add ? answers : null, key, output,
                            cancel, supplied: supplied)).ExitCode;
                    }, cancel);
                    break;
                default:
                    var arguments = new Dictionary<string, string>(command.Arguments, StringComparer.Ordinal) { ["role"] = command.RoleKind };
                    var sent = await ThroughAgentAsync(host, connection => SetupHostCommands.RunAsync(connection, host.HostId,
                        command.Add ? NodeCommandKinds.AddRole : NodeCommandKinds.RemoveRole, arguments,
                        command.Add && command.Secrets.Count > 0 ? command.Secrets : null, TakePatience, activity.Update, cancel), cancel);
                    return sent.State == NodeCommandState.Succeeded
                        ? SetupStepResult.Done(sent.Summary ?? $"{name} {(command.Add ? "runs" : "was removed")} on {here}.")
                        : SetupStepResult.Failed(sent.Summary ?? $"Martlet on {here} couldn't finish it.");
            }
            if (output.Busy(exit) is { } busy)
                return SetupStepResult.Failed($"{here}'s host stayed busy with another change ({busy}), so nothing was changed.");
            return exit == 0
                ? SetupStepResult.Done(command.Add ? $"{name} runs on {here}." : $"{name} was removed from {here}.")
                : SetupStepResult.Failed($"martlet-host {string.Join(' ', engine)} stopped on {here} (exit {exit}).");
        }

        public async Task<SetupStepResult> AssignJobAsync(string job, string? hostId, bool off, CancellationToken cancel)
        {
            if (window.store is null) return SetupStepResult.Failed("Martlet can't use its data folder on this PC.");
            if (!window.clusterEnabled)
                return SetupStepResult.Attention("Keep Martlet the same on all my computers is off on this PC, so who does what can't change " +
                    "on your other computers. Turn it on in Devices, then Reconfigure again.");
            var turn = await window.ChangeTurnAsync();
            try
            {
                var current = window.clusterPlan.For(job);
                var failover = hostId is not null && !off;
                var who = ClusterSync.Who(job, hostId, off);
                if (current is not null && current.HostId == hostId && current.Off == off && current.Failover == failover && current.MovedFrom is null)
                    return SetupStepResult.Done($"{ClusterSync.Title(job)} already goes to {who}.");
                window.clusterPlan = window.clusterPlan.Assign(job, hostId, off, failover, null, ClusterDevice, DateTimeOffset.UtcNow);
                window.SaveClusterPlan();
                ErrorLog.Info($"Recommended setup: {ClusterSync.Title(job)} now goes to {who} on all your computers" +
                    (failover ? ", with failover on." : "."));
                return SetupStepResult.Done($"{ClusterSync.Title(job)} now goes to {who} on all your computers" + (failover ? ", with failover on." : "."));
            }
            finally { turn.Dispose(); }
        }

        public Task<SetupStepResult> ShareAsync(string job, string machineId, bool join, CancellationToken cancel)
        {
            var settings = window.SharingSettings();
            var rules = settings.Job(job);
            var next = join
                ? rules with { Share = rules.Shares ? rules.Share : true, Never = [.. rules.Never.Where(n => n != machineId)] }
                : rules with { Never = [.. rules.Never.Where(n => n != machineId), machineId] };
            var title = WorkSharingJobs.Title(job);
            if (next.Shares == rules.Shares && next.Never.Order(StringComparer.Ordinal).SequenceEqual(rules.Never.Order(StringComparer.Ordinal)))
                return Task.FromResult(SetupStepResult.Done(join ? $"{machineId} already takes {title} when it's needed."
                    : $"{machineId} already takes no {title}."));
            var done = join ? $"{title} now goes to {machineId} too when its computer is busy." : $"{title} never uses {machineId} now.";
            return Task.FromResult(window.SaveSharing(settings.With(next), done)
                ? SetupStepResult.Done(done) : SetupStepResult.Failed("Couldn't save Sharing work on this PC."));
        }

        public async Task<IReadOnlyDictionary<string, string>> CheckAsync(CancellationToken cancel)
        {
            var hosts = NetworkMap.Hosts(window.Inputs());
            if (hosts.Count > 0) await window.CheckHostsAsync(hosts);
            while (window.clusterBusy && !window.closing) await Task.Delay(200, cancel);
            await window.SyncClusterAsync();
            return new Dictionary<string, string>(window.clusterFollow, StringComparer.Ordinal);
        }

        public async Task PublishAsync(SetupRun run, CancellationToken cancel)
        {
            window.setupRun = run;
            if (window.settingsNode is { } node)
            {
                // A sync replaces the copy it started from when it ends: write between syncs, as reminders do.
                while (window.settingsBusy && !window.closing) await Task.Delay(100, cancel);
                if (!window.closing)
                {
                    node.Put(SetupRun.Key(ClusterDevice), run.Write(), DateTimeOffset.UtcNow);
                    window.QueueSettingsSync();
                }
            }
            window.ShowConfiguring();
        }

        public void Accepted(SetupPreflightItem item) =>
            ErrorLog.Info($"Recommended setup: the owner accepted the terms of {RoleName(item.Change.RoleKind ?? "")} on {item.Change.MachineId} " +
                $"with Reconfigure, after the review showed them: {Clip(item.Terms ?? "", 600)}");

        private static string Clip(string text, int length) => text.Length <= length ? text : text[..(length - 3)] + "...";

        /// <summary>This PC's host engine for a change, with Docker Desktop started first.</summary>
        private async Task<HostSetupTarget> LocalEngineAsync(string role, IProgress<string> output, CancellationToken cancel)
        {
            await EnsureLocalEngineAsync(output, cancel);
            return await HostLocal.EngineForChangeAsync(window.ThisPcTarget(), role, _ => { }, output, cancel, SetupRunTitle);
        }

        /// <summary>This PC's host engine to read a role with. Reading (for the review) never starts Docker Desktop.</summary>
        private async Task<HostSetupTarget> ReadingEngineAsync(string role, IProgress<string> output, CancellationToken cancel)
        {
            if (!(await HostLocal.ProbeEngineAsync(cancel)).Answered)
                throw new InvalidOperationException("Docker Desktop isn't running on this PC, so Martlet can't read what the role needs. " +
                    "Start Docker Desktop, then check again.");
            return await HostLocal.EngineForChangeAsync(window.ThisPcTarget(), role, _ => { }, output, cancel, SetupRunTitle);
        }

        /// <summary>Runs over SSH without asking anything (Martlet's key, the pinned host key, a sudo password the owner chose to
        /// remember), as Martlet's automatic host updates do.</summary>
        private async Task<T> OverSshAsync<T>(PairedHost host, Func<HostRemote, HostSetupTarget, bool, string, Task<T>> run, CancellationToken cancel)
        {
            var target = host.Target(Version);
            var remote = new HostRemote(new HostShell(window.store!.DataDirectory, NoHostShellPrompts.Instance));
            var (probe, hostKey) = await remote.ProbeAsync(HostShellTarget.Parse(target.SshTarget), host.SshHostKey, cancel);
            return await run(remote, target, HostRemote.NeedsSudo(target.Method, probe), hostKey);
        }

        /// <summary>Runs through Martlet on that computer (the host's paired gateway), after checking Martlet there takes commands.</summary>
        private static async Task<T> ThroughAgentAsync<T>(PairedHost host, Func<Audio2FaceHostConnection, Task<T>> run, CancellationToken cancel)
        {
            try
            {
                return await ClusterSync.WithConnectionAsync(host.Pairing, async connection =>
                {
                    var list = await connection.ReadCommandsAsync(cancel);
                    if (list.Agent is not { } agent || DateTimeOffset.UtcNow - agent.SeenAt > TimeSpan.FromMinutes(2))
                        throw new InvalidOperationException(HostAgentRun.AgentText(host.HostId, list.Agent));
                    return await run(connection);
                });
            }
            catch (Audio2FaceHostException error) when (error.Code == "request.invalid")
            {
                throw new InvalidOperationException($"{host.HostId}'s host service is older than commands between computers. Open Martlet on " +
                    $"{host.HostId} once: it brings its host service up to date by itself.");
            }
        }

    }
}

/// <summary>Sends one command to Martlet on a host (through its paired gateway) and follows it to its end without a window, for
/// the runs that apply the recommended setup. A command still waiting after <c>patience</c> (Martlet isn't running there) is
/// withdrawn; canceling withdraws it or asks Martlet there to stop it.</summary>
internal static class SetupHostCommands
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(1.5);
    /// <summary>How long the host may stay unreachable while a command runs (its gateway restarts during some changes).</summary>
    private static readonly TimeSpan UnreachablePatience = TimeSpan.FromMinutes(15);

    internal static async Task<Martlet.Core.Nodes.NodeCommand> RunAsync(Audio2FaceHostConnection connection, string host, string kind,
        IReadOnlyDictionary<string, string> arguments, IReadOnlyDictionary<string, string>? secrets, TimeSpan patience,
        Action<string> status, CancellationToken cancel)
    {
        // Sending the same command again while it waits or runs returns that one, so a retry never sends it twice.
        var command = await connection.SendCommandAsync(kind, arguments, secrets, cancel);
        var sent = DateTimeOffset.UtcNow;
        DateTimeOffset? unreachableSince = null;
        try
        {
            while (!command.Finished)
            {
                if (command.State == NodeCommandState.Queued && DateTimeOffset.UtcNow - sent > patience)
                {
                    await WithdrawAsync(connection, command.Id);
                    throw new InvalidOperationException($"Martlet on {host} didn't take it within {patience.TotalMinutes:0} minutes. Open Martlet " +
                        "there (or let it start with Windows), then try again.");
                }
                status(command.State == NodeCommandState.Queued ? $"waiting for Martlet on {host}"
                    : command.Summary is { } summary ? summary : $"running on {host}");
                await Task.Delay(Poll, cancel);
                try
                {
                    command = await connection.ReadCommandAsync(command.Id, cancel);
                    unreachableSince = null;
                }
                catch (Exception error) when (error is Audio2FaceHostException { Code: "host.unreachable" or "auth.clock" or "gateway.internal" } ||
                    error is OperationCanceledException && !cancel.IsCancellationRequested)
                {
                    unreachableSince ??= DateTimeOffset.UtcNow;
                    if (DateTimeOffset.UtcNow - unreachableSince > UnreachablePatience)
                        throw new InvalidOperationException($"{host}'s host service stopped answering for {UnreachablePatience.TotalMinutes:0} minutes.");
                }
                catch (Audio2FaceHostException error) when (error.Code == "command.not_found")
                {
                    throw new InvalidOperationException($"{host} no longer has this command (its host service restarted).");
                }
            }
            return command;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            await WithdrawAsync(connection, command.Id);
            throw;
        }
    }

    private static async Task WithdrawAsync(Audio2FaceHostConnection connection, string id)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await connection.CancelCommandAsync(id, limit.Token);
        }
        catch (Exception error) when (error is Audio2FaceHostException or OperationCanceledException or System.Net.Http.HttpRequestException or IOException) { }
    }
}

/// <summary>The changes this PC makes on a host's service right now (a role it installs or removes there, an update), by host
/// ID, so Home and the Devices map show that computer as Configuring while it works.</summary>
internal static class HostActivity
{
    private static readonly Lock Gate = new();
    private static readonly List<Entry> Entries = [];

    /// <summary>Raised (on any thread) when an activity starts, changes or ends.</summary>
    internal static event Action? Changed;

    /// <summary>What each host is busy with, the newest activity first.</summary>
    internal static IReadOnlyDictionary<string, string> Now
    {
        get
        {
            lock (Gate)
                return Entries.AsEnumerable().Reverse().GroupBy(e => e.Host, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First().Text, StringComparer.Ordinal);
        }
    }

    /// <summary>Marks <paramref name="host"/> as busy with <paramref name="text"/> until the result is disposed.</summary>
    internal static Entry Begin(string host, string text)
    {
        var entry = new Entry(host, text);
        lock (Gate) Entries.Add(entry);
        Changed?.Invoke();
        return entry;
    }

    internal sealed class Entry(string host, string text) : IDisposable
    {
        private readonly string start = text;
        internal string Host { get; } = host;
        internal string Text { get; private set; } = text;

        /// <summary>Adds what the change does now ("Installing Chatterbox Turbo: waiting for Martlet on gpu-box").</summary>
        internal void Update(string now)
        {
            var next = string.IsNullOrWhiteSpace(now) ? start : $"{start}: {now}";
            if (next == Text) return;
            Text = next.Length <= 200 ? next : next[..197] + "...";
            Changed?.Invoke();
        }

        public void Dispose()
        {
            bool removed;
            lock (Gate) removed = Entries.Remove(this);
            if (removed) Changed?.Invoke();
        }
    }
}

/// <summary>A role as the executor takes it (<see cref="SetupRoleNeeds"/>), from the desktop's reading of martlet-host describe.</summary>
internal static class SetupRoleReading
{
    /// <summary>The desktop's reading of a role (<see cref="HostRoleInputs"/>) as the executor takes it.</summary>
    internal static SetupRoleNeeds Needs(HostRoleInputs inputs) => new(inputs.Title, inputs.Terms)
    {
        Installed = inputs.Installed, GpuOrCpu = inputs.GpuOrCpu,
        GpuWhen = inputs.GpuWhen is { } gpuWhen ? new(gpuWhen.Variable, gpuWhen.Value) : null,
        Choices = [.. inputs.Choices.Select(c => new SetupRoleChoice(c.Variable, c.Options, c.Default,
            c.When is { } when ? new(when.Variable, when.Value) : null))],
        Secrets = [.. inputs.Secrets.Select(s => new SetupRoleSecret(s.Name, s.Prompt, s.Stored,
            inputs.SecretWhen.TryGetValue(s.Name, out var when) ? new(when.Variable, when.Value) : null))],
        TermsWhen = [.. inputs.TermsWhen.Select(t => new SetupRoleTerms(new(t.Variable, t.Value), t.Text))],
        Gpus = [.. inputs.Gpus.Select(g => new SetupRoleCard(g.Id, g.Name, g.MemoryMb))],
        Current = inputs.Current, Stops = inputs.Stops
    };
}
