using System.IO;
using System.Windows;
using System.Windows.Threading;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Cluster;
using Martlet.Core.Nodes;
using Martlet.Core.Planning;
using Martlet.Core.Settings;
using Martlet.Core.Sync;

namespace Martlet.Desktop;

/// <summary>Applies the recommended setup to all your computers (docs/CLUSTER.md, "Applying the recommended setup") and shows
/// the Configuring state everywhere ("Configuring"). <see cref="PrepareRecommendedSetupAsync"/> reads what the review must
/// show before Reconfigure (each role's terms, secrets, downloads, changes that need someone at a computer or can't be made);
/// <see cref="ReconfigureAsync"/> runs <see cref="ApplyRecommendedSetupAsync"/> as a background task (its run window, in
/// Background tasks), which makes the changes in order through Martlet's existing paths: host roles through
/// Martlet on that computer (node commands), this PC's own host service or SSH; who does each job through the shared cluster
/// plan with failover on; Devices › Sharing work; then one cluster check, so every companion PC follows within one check. The
/// run is published as this PC's setup-run entry in the shared settings, so every computer shows Configuring.</summary>
public partial class MainWindow
{
    /// <summary>The background task (run window) that applies the recommended setup. The engine runs on this PC name it to other
    /// runs that wait for the same step.</summary>
    internal const string SetupRunTitle = "Reconfigure your computers";
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
        SetupExecutor.PrepareAsync(recommendation, SetupRunTargets(), cancel);

    /// <summary>The paths a run takes: this PC's real ones, or the FIXTURE's simulated computers (<see cref="SimulatedRecommendedSetup"/>).</summary>
    private ISetupTargets SetupRunTargets() => SimulatedRecommendedSetup.Active
        ? new SimulatedRecommendedSetup.Targets(ClusterDevice, run =>
        {
            setupRun = run;
            ShowConfiguring();
        })
        : new SetupTargets(this);

    /// <summary>Reconfigure: applies <paramref name="recommendation"/> as a background task, in a run window over
    /// <paramref name="owner"/> (the review, which closes; the run stays with Martlet's main window). The window lists the
    /// changes, then each computer's steps and what its host engine prints, and ends with each change's outcome; Hide keeps it
    /// going in Background tasks, and Cancel task stops the changes not made yet (each says so). A second Reconfigure while it
    /// runs shows the same run.</summary>
    private Task<string?> ReconfigureAsync(Window owner, NetworkRecommendation recommendation, SetupRunPreflight preflight) =>
        HostRunWindow.RunAsync(owner, SetupRunTitle, async run =>
        {
            var changes = recommendation.Changes;
            run.Status($"Starting {changes.Count} change{(changes.Count == 1 ? "" : "s")} on your computers...");
            for (var i = 0; i < changes.Count; i++) run.Output.Report($"{i + 1}. {changes[i].Summary}");
            // Each computer's new state is a line, in order with what its host engine prints.
            SetupRun? shown = null;
            var progress = new RunProgress(next =>
            {
                foreach (var line in next.ChangesSince(shown)) run.Output.Report(line);
                shown = next;
                if (!next.Finished) run.Status(next.Summary(DateTimeOffset.UtcNow));
            });
            var outcome = await ApplyRecommendedSetupAsync(recommendation, preflight, progress, run.Output, run.Token);
            foreach (var step in outcome.Steps)
                run.Output.Report(step.State switch
                {
                    SetupMachineState.Done => "Done",
                    SetupMachineState.Failed => "Failed",
                    _ => "Needs you"
                } + $": {step.Change.Summary} {step.Text}");
            ErrorLog.Info($"Recommended setup: reconfigured ({(outcome.Succeeded ? "every change done" : "not every change done")}).");
            // Canceled: the changes not made are listed above, and the task shows it was canceled.
            run.Token.ThrowIfCancellationRequested();
            return outcome.Summary;
        }, join: true);

    /// <summary>Takes the executor's reports where it makes them (on the UI thread, which the run started on), without a
    /// second trip through the dispatcher.</summary>
    private sealed class RunProgress(Action<SetupRun> report) : IProgress<SetupRun>
    {
        public void Report(SetupRun value) => report(value);
    }

    /// <summary>Applies <paramref name="recommendation"/> after the owner's Reconfigure in the review that showed
    /// <paramref name="preflight"/> (the click accepts the terms it showed). Makes the changes in order, continues past a failed
    /// one and reports each computer (and what each role change's host engine prints, to <paramref name="output"/>); never
    /// throws for a failed step.</summary>
    internal async Task<SetupRunOutcome> ApplyRecommendedSetupAsync(NetworkRecommendation recommendation, SetupRunPreflight preflight,
        IProgress<SetupRun>? progress, IProgress<string>? output, CancellationToken cancel)
    {
        if (setupApplying) throw new InvalidOperationException("Martlet is already reconfiguring your computers.");
        setupApplying = true;
        try
        {
            ErrorLog.Info($"Recommended setup: reconfiguring your computers ({recommendation.Changes.Count} changes, " +
                $"{recommendation.Changes.Select(c => SetupExecutor.RunMachine(c, ClusterDevice)).Distinct(StringComparer.Ordinal).Count()} computers).");
            var outcome = await SetupExecutor.ApplyAsync(recommendation, preflight, SetupRunTargets(), progress, cancel, output: output);
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

    private void InitializeConfiguring()
    {
        HostActivity.Changed += () => Dispatcher.InvokeAsync(ShowConfiguring, DispatcherPriority.Background);
        // A run this PC started can't still be running when Martlet starts (it closed or stopped first): finish its entry, so no
        // computer keeps showing it as Configuring.
        if (settingsNode?.Document.Find(SetupRun.Key(ClusterDevice)) is { } own && own.UpdatedBy == ClusterDevice &&
            SetupRun.Read(own.Value) is { Finished: false } left)
        {
            var now = DateTimeOffset.UtcNow;
            var ended = left.Interrupted(now, $"Martlet closed on {ClusterDevice} before this change was made. Check the recommended setup again.");
            try { settingsNode.Put(own.Key, ended.Write(), now); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
            {
                ErrorLog.Warn("Couldn't finish the recommended setup run Martlet left when it closed.", error);
            }
            setupRun = ended;
            ErrorLog.Info($"Recommended setup: the run {left.RunId} ended when Martlet closed; the changes not made need you.");
        }
    }

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
                    // Martlet there reports where the command stands; each new state is a line in the run's output too.
                    string? reported = null;
                    void Status(string now)
                    {
                        activity.Update(now);
                        if (now == reported) return;
                        reported = now;
                        progress.Report($"{host.HostId}: {now}");
                    }
                    var sent = await ThroughAgentAsync(host, connection => SetupHostCommands.RunAsync(connection, host.HostId,
                        command.Add ? NodeCommandKinds.AddRole : NodeCommandKinds.RemoveRole, arguments,
                        command.Add && command.Secrets.Count > 0 ? command.Secrets : null, TakePatience, Status, cancel), cancel);
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
            var title = WorkSharingJobs.Title(job);
            // A job with a pool list: the computer joins its list (or turns on in it), or leaves it.
            if (PoolAreas.Find(job) is { } area && window.store is { } store && WorkSharingRoster.Pool(store.DataDirectory, area) is { } list)
            {
                var key = PoolMember.Computer(machineId).Key;
                var member = list.Find(key);
                if (join ? member is { Off: false } : member is null)
                    return Task.FromResult(SetupStepResult.Done(join ? $"{machineId} is already in the {title} list." : $"{machineId} isn't in the {title} list."));
                var listed = join ? list.With((member ?? PoolMember.Computer(machineId)) with { Off = false }) : list.Without(key);
                var joined = join ? $"{machineId} is now in the {title} list: it takes {title} when the ones before it are busy."
                    : $"{machineId} is no longer in the {title} list.";
                if (!PoolSettings.SaveFor(store.DataDirectory, area, listed)) return Task.FromResult(SetupStepResult.Failed($"Couldn't save the {title} list on this PC."));
                WorkSharingRoster.Forget();
                ErrorLog.Info("Pools: " + joined);
                window.QueueSettingsSync();
                return Task.FromResult(SetupStepResult.Done(joined));
            }
            var settings = window.SharingSettings();
            var rules = settings.Job(job);
            var next = join
                ? rules with { Share = rules.Shares ? rules.Share : true, Never = [.. rules.Never.Where(n => n != machineId)] }
                : rules with { Never = [.. rules.Never.Where(n => n != machineId), machineId] };
            if (next.Shares == rules.Shares && next.Never.Order(StringComparer.Ordinal).SequenceEqual(rules.Never.Order(StringComparer.Ordinal)))
                return Task.FromResult(SetupStepResult.Done(join ? $"{machineId} already takes {title} when it's needed."
                    : $"{machineId} already takes no {title}."));
            var done = join ? $"{title} now goes to {machineId} too when its computer is busy." : $"{title} never uses {machineId} now.";
            return Task.FromResult(window.SaveSharing(settings.With(next), done)
                ? SetupStepResult.Done(done) : SetupStepResult.Failed("Couldn't save Sharing work on this PC."));
        }

        public Task<SetupRouteReading> ReadRouteAsync(string job, string optionId, CancellationToken cancel)
        {
            var role = SetupRoutes.Role(job);
            var parakeet = window.parakeet;
            return Task.FromResult(SetupRoutes.Read(job, optionId, new(window.Role == DeviceRole.Host,
                window.homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == role),
                Prerequisites.IsMissing(Prerequisites.Ollama), parakeet is null ? null : parakeet.Installed,
                LocalChatModels.FirstOrDefault(m => m.Id == PlanningCatalog.Current.Find(optionId)?.ModelId)?.Size ??
                    (PlanningCatalog.Current.Find(optionId) is { Origin: OptionOrigin.LocalFacts, Peak.DiskGb: > 0 } catalogModel
                        ? $"about {catalogModel.Peak.DiskGb.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} GB" : null),
                window.ServedApp(ServedModels.ModelOf(optionId)))));
        }

        public async Task<SetupStepResult> UseRouteAsync(string job, string optionId, CancellationToken cancel)
        {
            var reading = await ReadRouteAsync(job, optionId, cancel);
            if (reading.Verdict != SetupStepVerdict.Ready) return SetupStepResult.Attention(reading.Text);
            // Already used here: only the shared plan changes (no host does it).
            if (reading.InUse) return await AssignJobAsync(job, null, false, cancel) is { State: SetupMachineState.Done } ? SetupStepResult.Done(reading.Text)
                : SetupStepResult.Attention(reading.Text + " Who does it on your other computers didn't change: Keep in sync is off.");
            // A model the owner's own model app serves: Thinking switches to that app's address on this PC.
            if (job == ClusterJobs.Thinking && window.ServedApp(ServedModels.ModelOf(optionId)) is { } served)
                return await window.UseServedThinkingAsync(served) ? SetupStepResult.Done(reading.Text)
                    : SetupStepResult.Failed($"Thinking didn't change on this PC: {window.ActionText.Text}");
            var option = PlanningCatalog.Current.Find(optionId)!;
            // A voice engine runs in the host service, never in the app, so Speaking is never a route this PC makes itself.
            if (job is not (ClusterJobs.Thinking or ClusterJobs.Listening))
                return SetupStepResult.Attention($"Choose {option.DisplayName} for {job} in Companion › Voice.");
            // The Companion page's own paths, already confirmed by Reconfigure (they save the route and record the plan).
            var saved = job == ClusterJobs.Thinking
                ? await window.SaveLocalThinkingAsync(option.ModelId!, confirmed: true)
                : await window.UseParakeetAsync(SetupRoutes.Parakeet(option.ModelId!)!, confirmed: true);
            return saved ? SetupStepResult.Done(reading.Text)
                : SetupStepResult.Failed($"{SetupRoutes.Title(job)} didn't change on this PC: {window.ActionText.Text}");
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
                // A sync replaces the copy it started from when it ends: write between syncs, as reminders do. While Martlet
                // closes no sync starts any more; the entry is still saved here and goes out with the next start's sync.
                while (window.settingsBusy && !window.closing) await Task.Delay(100, cancel);
                node.Put(SetupRun.Key(ClusterDevice), run.Write(), DateTimeOffset.UtcNow);
                if (!window.closing) window.QueueSettingsSync();
            }
            window.ShowConfiguring();
        }

        public void Accepted(SetupPreflightItem item) =>
            ErrorLog.Info($"Recommended setup: the owner accepted the terms of " +
                (item.Change.RoleKind is { } kind ? $"{RoleName(kind)} on {item.Change.MachineId}" : $"the {item.Change.Job} change on this PC") +
                $" with Reconfigure, after the review showed them: {Clip(item.Terms ?? "", 600)}");

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

/// <summary>What this PC knows when it reads how it would do a job no host does next (<see cref="SetupRoutes.Read"/>): whether
/// it is a host PC (it uses no jobs), its route for the job now, whether Ollama is missing, which Parakeet models it has (null:
/// Parakeet can't run here), the download size of the Ollama model named, when known, and for a model one of the owner's model
/// apps serves, the app found running it (<paramref name="Served"/>; null when Martlet didn't find it this time).</summary>
internal sealed record SetupRouteFacts(bool HostPc, SetupRoute? Route, bool OllamaMissing, Func<string, bool>? ParakeetInstalled,
    string? OllamaModelSize = null, ServedModel? Served = null);

/// <summary>How this PC does a job that no host does next, for the recommended setup: the Companion page's own choices it
/// makes itself (a model in this PC's Ollama, Parakeet) and, for everything else (a hosted provider that
/// needs your key), where you choose it. Reads nothing itself.</summary>
internal static class SetupRoutes
{
    internal static SetupRole Role(string job) => job switch
    {
        ClusterJobs.Thinking => SetupRole.Llm,
        ClusterJobs.Listening => SetupRole.Stt,
        _ => SetupRole.Tts
    };

    internal static string Title(string job) => char.ToUpperInvariant(job[0]) + job[1..];

    private static string Tab(string job) => job switch
    {
        ClusterJobs.Thinking => "Companion › Thinking",
        ClusterJobs.Listening => "Companion › Listening",
        ClusterJobs.Speaking => "Companion › Voice",
        _ => "Companion › Lip-sync"
    };

    internal static SetupRouteReading Read(string job, string optionId, SetupRouteFacts facts)
    {
        var tab = Tab(job);
        var option = PlanningCatalog.Current.Find(optionId);
        var name = option?.DisplayName ?? optionId;
        if (facts.HostPc)
            return new(SetupStepVerdict.NeedsOwner, $"Choose {name} for {job} in {tab} on your companion PC: this PC is a host PC and uses no jobs.");
        var route = facts.Route;
        const string Others = " Your other companion PCs follow it through the shared settings once they can (their Companion page says what they need).";
        switch (job, option)
        {
            case (ClusterJobs.Thinking, { ModelId: { } served }) when ServedModels.IsServed(option):
                if (route is { RouteType: SetupRouteType.ChatCompletions } && route.ModelId == served &&
                    (facts.Served is null || route.Origin == facts.Served.BaseUrl))
                    return new(SetupStepVerdict.Ready, $"Thinking already uses {served} on this PC.") { InUse = true };
                if (facts.Served is not { BaseUrl.Length: > 0 } app)
                    return new(SetupStepVerdict.NeedsOwner, $"Martlet didn't find the app that runs {served} on this PC this time. " +
                        $"Start it, then choose it in {tab} › This PC.");
                return new(SetupStepVerdict.Ready, $"Thinking uses {served} in {app.AppName} on this PC. It already runs there, so nothing downloads.{Others}");
            case (ClusterJobs.Thinking, { IsLocal: true, HostRoleKind: HostRoles.Ollama, ModelId: { } model }):
                if (route is { RouteType: SetupRouteType.ChatCompletions, Origin: MainWindow.LocalOllamaBaseUrl } && route.ModelId == model)
                    return new(SetupStepVerdict.Ready, $"Thinking already uses {model} in Ollama on this PC.") { InUse = true };
                if (facts.OllamaMissing)
                    return new(SetupStepVerdict.NeedsOwner, $"Ollama isn't installed on this PC. Install it in {tab}, then check again to use {name}.");
                var size = facts.OllamaModelSize;
                return new(SetupStepVerdict.Ready, $"Thinking uses {name} in Ollama on this PC.{Others}")
                {
                    Terms = $"Ollama downloads {model}{(size is null ? "" : $" ({size})")} from " +
                        $"{(model.StartsWith("hf.co/", StringComparison.OrdinalIgnoreCase) ? "Hugging Face" : "ollama.com")} when it isn't on this PC yet. " +
                        "The model's own license applies."
                };
            case (ClusterJobs.Listening, { RunsInApp: true, ModelId: { } id }) when Parakeet(id) is { } parakeet:
                if (route is { RouteType: SetupRouteType.LocalParakeet } && route.ModelId == parakeet.Id)
                    return new(SetupStepVerdict.Ready, $"Listening already uses {parakeet} on this PC.") { InUse = true };
                if (facts.ParakeetInstalled is not { } installed)
                    return new(SetupStepVerdict.CannotApply, "Parakeet can't run without Martlet's data folder on this PC.");
                return new(SetupStepVerdict.Ready, $"Listening uses {parakeet} on this PC's processor. Speech stays on this PC.{Others}")
                {
                    Terms = installed(parakeet.Id) ? null
                        : $"{parakeet} (NVIDIA, CC BY 4.0) downloads from Hugging Face ({Martlet.Sherpa.SherpaComponents.Megabytes(parakeet.DownloadBytes)})."
                };
            case (_, { IsLocal: false, ProviderId: { } provider }):
                if (route is { RouteType: null or SetupRouteType.OpenAi or SetupRouteType.ChatCompletions } && Provider(route) == provider)
                    return new(SetupStepVerdict.Ready, $"{Title(job)} already uses {name} on this PC.") { InUse = true };
                return new(SetupStepVerdict.NeedsOwner, $"Choose {name} for {job} in {tab}; it needs your API key there.");
            default:
                return new(SetupStepVerdict.NeedsOwner, $"Choose {name} for {job} in {tab}.");
        }
    }

    /// <summary>The Parakeet model a catalog option names ("parakeet-tdt-0.6b-v3" is the int8 download Martlet uses).</summary>
    internal static Martlet.Sherpa.ParakeetModel? Parakeet(string id) =>
        Martlet.Sherpa.ParakeetModels.Find(id) ?? Martlet.Sherpa.ParakeetModels.All.FirstOrDefault(m => m.Id.StartsWith(id + "-", StringComparison.Ordinal));

    /// <summary>The hosted provider a cloud route goes to, as the footprint catalog names it, or null.</summary>
    internal static string? Provider(SetupRoute route)
    {
        var origin = route.Origin.ToLowerInvariant();
        return route.RouteType == SetupRouteType.OpenAi || origin.Contains("openai.com", StringComparison.Ordinal) ? "openai"
            : origin.Contains("nvidia", StringComparison.Ordinal) ? "nvidia-build"
            : origin.Contains("openrouter", StringComparison.Ordinal) ? "openrouter" : null;
    }
}