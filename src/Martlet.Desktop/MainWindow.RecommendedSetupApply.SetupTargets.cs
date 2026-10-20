using System.IO;
using System.Windows;
using System.Windows.Threading;
using Martlet.HostClient;
using Martlet.Core.Cluster;
using Martlet.Core.Nodes;
using Martlet.Core.Planning;
using Martlet.Core.Settings;
using Martlet.Core.Sync;

namespace Martlet.Desktop;

/// <summary>More of MainWindow.RecommendedSetupApply.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
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
                        : $"{machineId} isn't paired with this PC; it pairs by itself once it's in your Martlet network, so check again then.");
            if (window.store is null) return new(false, "Martlet can't use its data folder on this PC.");
            return host.Method switch
            {
                HostSetupMethod.ThisPcDocker or HostSetupMethod.Agent => SetupReach.Yes,
                HostSetupMethod.SshDocker or HostSetupMethod.SshNative when !string.IsNullOrWhiteSpace(host.SshTarget) => SetupReach.Yes,
                _ => new(false, $"Martlet doesn't know how to reach {host.HostId}'s host service: set its connection method on the Devices map, " +
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
                return SetupStepResult.Attention("Keep Martlet the same on all my computers is off on this PC: turn it on in Devices, " +
                    "then Reconfigure again.");
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
            // People's Voice recognition list: the computer goes first (this PC's processor stays the fallback), also before any
            // list was saved (which means this PC's processor alone).
            if (job == WorkSharingJobs.VoiceRecognition && window.store is { } own)
            {
                var recognition = PoolAreas.VoiceRecognition;
                var people = WorkSharingRoster.Shared.Pool(own.DataDirectory, recognition);
                var computer = PoolMember.Computer(machineId).Key;
                if (join ? Martlet.Core.Speakers.VoiceRecognitionPool.IsFirst(people, machineId) : people?.Find(computer) is null)
                    return Task.FromResult(SetupStepResult.Done(join ? $"{machineId} is already first in the {title} list." : $"{machineId} isn't in the {title} list."));
                var reordered = join ? Martlet.Core.Speakers.VoiceRecognitionPool.First(people, machineId) : people!.Without(computer);
                var said = join ? $"{machineId} is now first in the {title} list: it recognizes who is talking, and each companion PC's " +
                    "processor does when it doesn't answer in time." : $"{machineId} is no longer in the {title} list.";
                if (!PoolSettings.SaveFor(own.DataDirectory, recognition, reordered)) return Task.FromResult(SetupStepResult.Failed($"Couldn't save the {title} list on this PC."));
                ErrorLog.Info("Pools: " + said);
                window.QueueSettingsSync();
                return Task.FromResult(SetupStepResult.Done(said));
            }
            // A job with a pool list: the computer joins its list (or turns on in it), or leaves it.
            if (PoolAreas.Find(job) is { } area && window.store is { } store && WorkSharingRoster.Shared.Pool(store.DataDirectory, area) is { } list)
            {
                var key = PoolMember.Computer(machineId).Key;
                var member = list.Find(key);
                if (join ? member is { Off: false } : member is null)
                    return Task.FromResult(SetupStepResult.Done(join ? $"{machineId} is already in the {title} list." : $"{machineId} isn't in the {title} list."));
                var listed = join ? list.With((member ?? PoolMember.Computer(machineId)) with { Off = false }) : list.Without(key);
                var joined = join ? $"{machineId} is now in the {title} list: it takes {title} when the ones before it are busy."
                    : $"{machineId} is no longer in the {title} list.";
                if (!PoolSettings.SaveFor(store.DataDirectory, area, listed)) return Task.FromResult(SetupStepResult.Failed($"Couldn't save the {title} list on this PC."));
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
        private static async Task<T> ThroughAgentAsync<T>(PairedHost host, Func<HostConnection, Task<T>> run, CancellationToken cancel)
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
            catch (HostConnectionException error) when (error.Code == "request.invalid")
            {
                throw new InvalidOperationException($"{host.HostId}'s host service is older than commands between computers. Open Martlet on " +
                    $"{host.HostId} once: it brings its host service up to date by itself.");
            }
        }

    }
}
