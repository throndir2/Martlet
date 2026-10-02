using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.F5;

namespace Martlet.Desktop;

/// <summary>Shared "who does what". This PC keeps a copy of the cluster plan, records every job change made here,
/// and, while "Keep in sync" is on, checks every paired host every 15 seconds: it merges the hosts' copies, follows
/// changes made on the owner's other computers, moves a job whose host stopped answering to another host that runs
/// the same engine (failover, per job) and pushes the result to every host with an older copy.</summary>
public partial class MainWindow
{
    private static readonly string ClusterDevice = HostSetupCommands.SuggestedDeviceId();
    private readonly DispatcherTimer clusterTimer = new() { Interval = ClusterSync.Interval };
    private ClusterPlan clusterPlan = ClusterPlan.Empty;
    private bool clusterEnabled;
    private bool clusterBusy;
    private DateTimeOffset? clusterCheckedAt;
    /// <summary>Consecutive checks in which a host did not serve a job, keyed "host/job".</summary>
    private readonly Dictionary<string, int> clusterMisses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClusterProbe> clusterProbes = new(StringComparer.Ordinal);
    /// <summary>Why this PC does not follow the shared plan for a job (not paired with that host, no voice chosen...).</summary>
    private readonly Dictionary<string, string> clusterFollow = new(StringComparer.Ordinal);
    /// <summary>What this PC did for each job when last observed, to notice changes made elsewhere in Martlet (Setup).</summary>
    private readonly Dictionary<string, LocalJob> clusterObserved = new(StringComparer.Ordinal);
    private string? clusterSignature;
    private LiveConversationWindow? openConversation;

    private void InitializeCluster()
    {
        clusterTimer.Tick += (_, _) => SyncClusterAsync().Forget();
        if (store is null)
        {
            ClusterSyncChoice.IsEnabled = false;
            return;
        }
        clusterEnabled = ClusterSync.LoadEnabled(store.DataDirectory);
        clusterPlan = ClusterSync.LoadPlan(store.DataDirectory);
        ClusterSyncChoice.IsChecked = clusterEnabled;
        ShowClusterStatus();
    }

    private void StartCluster()
    {
        if (!clusterEnabled || store is null || closing) return;
        clusterTimer.Start();
        SyncClusterAsync().Forget();
    }

    private void ClusterSync_Click(object sender, RoutedEventArgs e)
    {
        if (store is null) return;
        var on = ClusterSyncChoice.IsChecked == true;
        if (on && !ConfirmationDialog.Confirm(this,
                "Keep who does what in sync on all your computers? While Martlet runs, this PC checks each paired host every " +
                "15 seconds over its pinned pairing and keeps the shared plan on every host. When a job changes on another of " +
                "your computers it changes here too: it can move to another of your paired hosts or back to this PC's Setup " +
                "choice. Jobs you set to fail over move to another paired host that runs the same engine when their host stops " +
                "answering. Nothing moves to a cloud provider by itself, and the plan holds no keys or conversation data.",
                "Keep in sync"))
        {
            ClusterSyncChoice.IsChecked = false;
            return;
        }
        try { ClusterSync.SaveEnabled(store.DataDirectory, on); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ClusterSyncChoice.IsChecked = clusterEnabled;
            ActionText.Text = $"Could not save {ClusterSync.PreferenceFile} in Martlet's data folder, so sync stays {(clusterEnabled ? "on" : "off")}.";
            return;
        }
        clusterEnabled = on;
        clusterMisses.Clear();
        clusterProbes.Clear();
        clusterFollow.Clear();
        clusterCheckedAt = null;
        if (on) StartCluster();
        else clusterTimer.Stop();
        ActionText.Text = on ? "Who does what now stays in sync with your hosts and your other computers."
            : "Sync is off. This PC keeps its own choices and checks nothing in the background.";
        ShowClusterStatus();
        if (DevicesPage.IsVisible) RenderMap();
        RefreshCoverage();
    }

    /// <summary>Records a job change made on this PC (on the Devices page or in Setup) as the newest entry of the shared plan.</summary>
    private void RecordClusterJob(string job, LocalJob local)
    {
        clusterObserved[job] = local;
        clusterFollow.Remove(job);
        if (store is null) return;
        var current = clusterPlan.For(job);
        if (current is not null && ClusterSync.Matches(current, local) && current.MovedFrom is null) return;
        clusterPlan = clusterPlan.Assign(job, local.HostId, local.Off, current?.Failover ?? false, null, ClusterDevice, DateTimeOffset.UtcNow);
        SaveClusterPlan();
        QueueClusterSync();
    }

    /// <summary>Notices jobs that changed outside the Devices page (for example in Setup) and records them.</summary>
    private void ObserveLocalJobs()
    {
        if (store is null || homeSettings?.Setup is null) return;
        foreach (var job in ClusterJobs.All)
        {
            var local = ClusterSync.Local(job, homeSettings, homeAvatar);
            if (clusterObserved.TryGetValue(job, out var seen) && seen != local) RecordClusterJob(job, local);
            clusterObserved[job] = local;
        }
    }

    /// <summary>Marks a forgotten host as removed in the shared plan, so its entry does not return from an older copy.</summary>
    private void ForgetClusterHost(string hostId)
    {
        clusterProbes.Remove(hostId);
        if (store is null || clusterPlan.Node(hostId) is not { Removed: false }) return;
        clusterPlan = clusterPlan.Observe(hostId, null, [], true, ClusterDevice, DateTimeOffset.UtcNow);
        SaveClusterPlan();
        QueueClusterSync();
    }

    private void SetFailover(string job, bool on)
    {
        if (store is null || !clusterEnabled) return;
        var local = ClusterSync.Local(job, homeSettings, homeAvatar);
        var current = clusterPlan.For(job);
        var owner = current is null ? local : new LocalJob(current.HostId, current.Off);
        var engine = ClusterSync.Engine(job);
        if (on && !ConfirmationDialog.Confirm(this,
                $"When {(owner.HostId is { } host ? host : "the host doing it")} stops answering for about 30 seconds, move " +
                $"{ClusterSync.Title(job).ToLowerInvariant()} to another of your paired hosts that already runs {engine}? Its model may " +
                "differ. The move is shared with your other computers; it never goes to a cloud provider by itself and does not " +
                "move back on its own." + (job == ClusterJobs.Speaking ? " Your chosen reference voice goes to the new host." : ""),
                "Turn on failover"))
        {
            RenderMap();
            return;
        }
        clusterPlan = clusterPlan.Assign(job, owner.HostId, owner.Off, on, current?.MovedFrom, ClusterDevice, DateTimeOffset.UtcNow);
        SaveClusterPlan();
        ActionText.Text = on ? $"{ClusterSync.Title(job)} fails over to another host that runs {engine} if its host stops answering."
            : $"{ClusterSync.Title(job)} stays where it is when its host stops answering.";
        QueueClusterSync();
        RenderMap();
    }

    private void SaveClusterPlan()
    {
        if (store is null) return;
        try { ClusterSync.SavePlan(store.DataDirectory, clusterPlan); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException)
        {
            ActionText.Text = $"Could not save {ClusterSync.PlanFile} in Martlet's data folder; your hosts keep the shared copy.";
        }
    }

    /// <summary>Runs a sync once the current role change has finished (it holds <see cref="assigningRole"/>).</summary>
    private void QueueClusterSync()
    {
        if (clusterEnabled && !closing) Dispatcher.InvokeAsync(() => SyncClusterAsync().Forget(), DispatcherPriority.ContextIdle);
    }

    private async Task SyncClusterAsync()
    {
        if (!clusterEnabled || clusterBusy || closing || store is null || setupService is null || Role != DeviceRole.Companion) return;
        clusterBusy = true;
        var events = new List<string>();
        var followed = false;
        try
        {
            ObserveLocalJobs();
            var hosts = NetworkMap.Hosts(Inputs());
            var probes = await Task.WhenAll(hosts.Select(h => ClusterSync.ProbeAsync(h.Pairing, lifetime.Token)));
            if (closing || !clusterEnabled) return;
            clusterCheckedAt = DateTimeOffset.UtcNow;
            clusterProbes.Clear();
            foreach (var probe in probes) RecordProbe(probe);
            ObserveLocalJobs();

            var now = DateTimeOffset.UtcNow;
            var before = clusterPlan.Digest();
            var plan = clusterPlan;
            foreach (var probe in probes)
                if (probe.Plan is { } copy) plan = ClusterPlan.Merge(plan, copy);
            if (homeSettings?.Setup is not null)
                foreach (var job in ClusterJobs.All.Where(job => plan.For(job) is null))
                {
                    var local = ClusterSync.Local(job, homeSettings, homeAvatar);
                    plan = plan.Assign(job, local.HostId, local.Off, false, null, ClusterDevice, now);
                }
            foreach (var probe in probes.Where(p => p.Reachable))
            {
                var origin = hosts.First(h => h.HostId == probe.HostId).Pairing.Origin;
                var roles = ClusterSync.Roles(probe.Routes ?? []);
                if (plan.Node(probe.HostId) is not { Removed: false } node || node.Origin != origin || !node.Roles.SequenceEqual(roles))
                    plan = plan.Observe(probe.HostId, origin, roles, false, ClusterDevice, now);
            }
            clusterPlan = Failover(plan, probes, now, events);
            if (clusterPlan.Digest() != before) SaveClusterPlan();
            if (!assigningRole && !setupOperations.IsRunning && homeSettings?.Setup is not null) followed = await FollowClusterAsync(events);
            await PushClusterAsync(probes);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (!closing) ActionText.Text = "Who does what could not sync this time: " + error.Message;
        }
        finally
        {
            clusterBusy = false;
            if (!closing)
            {
                if (events.Count > 0) ActionText.Text = string.Join(" ", events);
                if (followed) RenderHome();
                ShowClusterStatus();
                var signature = ClusterSignature();
                if (followed || signature != clusterSignature)
                {
                    clusterSignature = signature;
                    if (DevicesPage.IsVisible) RenderMap();
                }
                RefreshCoverage();
            }
        }
    }

    private void RecordProbe(ClusterProbe probe)
    {
        clusterProbes[probe.HostId] = probe;
        foreach (var job in ClusterJobs.All)
        {
            var key = probe.HostId + "/" + job;
            if (probe.Serves(job)) clusterMisses.Remove(key);
            else clusterMisses[key] = Math.Min(clusterMisses.GetValueOrDefault(key) + 1, 1000);
        }
        var previous = hostChecks.GetValueOrDefault(probe.HostId);
        if (!probe.Reachable)
        {
            hostChecks[probe.HostId] = new(false, probe.Text, null, previous?.MartletVersion);
            return;
        }
        var offers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var route in probe.Routes ?? [])
            if (HostRoles.ForRoute(route.RouteId) is { } role) offers[role.Kind] = route.ModelId;
        hostChecks[probe.HostId] = new(true, previous?.Reachable == true && previous.Offers?.Count == offers.Count &&
                offers.All(o => previous.Offers.GetValueOrDefault(o.Key) == o.Value) ? previous.Text : HostControl.Describe(offers),
            offers, previous?.MartletVersion, probe.Routes);
    }

    /// <summary>Moves each job whose host missed <see cref="ClusterSync.FailAfter"/> checks, when its failover is on, to
    /// the best other host that runs the same engine. Skipped when this PC reaches no host at all: then its own network
    /// is the likelier problem. The job does not move back by itself.</summary>
    private ClusterPlan Failover(ClusterPlan plan, IReadOnlyList<ClusterProbe> probes, DateTimeOffset now, List<string> events)
    {
        if (!probes.Any(p => p.Reachable)) return plan;
        foreach (var job in ClusterJobs.All)
        {
            if (plan.For(job) is not { HostId: { } failed, Failover: true } assignment || !clusterProbes.ContainsKey(failed) ||
                clusterMisses.GetValueOrDefault(failed + "/" + job) < ClusterSync.FailAfter)
                continue;
            if (ClusterSync.FailoverTarget(plan, job, failed, probes, HardwareStore?.Load() ?? []) is not { } target) continue;
            var home = assignment.MovedFrom ?? failed;
            plan = plan.Assign(job, target, false, true, home == target ? null : home, ClusterDevice, now);
            events.Add($"{ClusterSync.Title(job)} moved from {failed} to {target} because {failed} stopped answering.");
        }
        return plan;
    }

    /// <summary>Makes this PC do what the shared plan says for each job. A job it cannot follow keeps its current route
    /// and shows why on its tile; the plan itself is not changed for that.</summary>
    private async Task<bool> FollowClusterAsync(List<string> events)
    {
        var changed = false;
        assigningRole = true;
        try
        {
            foreach (var job in ClusterJobs.All)
            {
                if (closing || clusterPlan.For(job) is not { } desired) continue;
                var before = ClusterSync.Local(job, homeSettings, homeAvatar);
                if (ClusterSync.Matches(desired, before))
                {
                    clusterFollow.Remove(job);
                    continue;
                }
                string? problem;
                try
                {
                    problem = job == ClusterJobs.LipSync ? await FollowLipSyncAsync(desired)
                        : await FollowJobAsync(HostJob.All.First(j => j.Job == job), desired);
                }
                catch (OperationCanceledException) { throw; }
                catch (F5Exception error) { problem = F5Voices.Describe(error); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
                    ContractException or JsonException or ArgumentException or Audio2FaceHostException)
                {
                    problem = error.Message;
                }
                var after = ClusterSync.Local(job, homeSettings, homeAvatar);
                clusterObserved[job] = after;
                if (problem is not null)
                {
                    clusterFollow[job] = problem;
                    continue;
                }
                clusterFollow.Remove(job);
                changed = true;
                var title = ClusterSync.Title(job);
                if (!events.Any(e => e.StartsWith(title, StringComparison.Ordinal)))
                    events.Add($"{title} is now done by {ClusterSync.Who(job, after.HostId, after.Off)}" +
                        (desired.UpdatedBy == ClusterDevice ? "." : $", as chosen on {desired.UpdatedBy}."));
            }
        }
        finally { assigningRole = false; }
        if (changed) openConversation?.ReloadWhenIdle("Who does what changed.");
        return changed;
    }

    private async Task<string?> FollowJobAsync(HostJob job, ClusterAssignment desired)
    {
        if (desired.HostId is null)
        {
            if (JobSavedRoute.Load(store!.DataDirectory, job.SavedFile) is not { } saved)
                return $"this PC has no Setup choice for {job.Job} to go back to; choose one in Setup.";
            var loaded = await setupService!.LoadAsync(lifetime.Token);
            if (loaded.Settings is not { Setup: not null } settings) return "complete Setup once first.";
            var next = HostHandoff.Back(settings, saved);
            var result = await setupService.SaveAsync(next, loaded.Revision, lifetime.Token);
            if (!result.Save.Saved) return result.Summary;
            homeSettings = next;
            return null;
        }
        if (FindHost(desired.HostId) is not { } host)
            return $"{desired.HostId} is not paired with this PC. Pair it here (Add a computer) to follow.";
        if (clusterProbes.GetValueOrDefault(host.HostId)?.Routes?.FirstOrDefault(r => r.RouteId == job.RouteId) is not { } route)
            return $"{host.HostId} did not answer or does not run {job.Engine} yet.";
        F5ReferenceSettings? reference = null;
        if (job.RouteType == SetupRouteType.GatewayF5)
        {
            reference = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts)?.Reference;
            if (reference is null || reference.ProcessingDestinationId != route.DestinationId)
                return $"choose the voice {host.HostId} speaks with: pick {host.HostId} under Speaking.";
        }
        await SaveJobHostAsync(job, host, route, reference: reference);
        return null;
    }

    private async Task<string?> FollowLipSyncAsync(ClusterAssignment desired)
    {
        PairedHost? host = null;
        if (desired.HostId is { } id && (host = FindHost(id)) is null)
            return $"{id} is not paired with this PC. Pair it here (Add a computer) to follow.";
        await ApplyLipSyncAsync(host, desired.Off);
        return null;
    }

    /// <summary>Merges this PC's plan into every reachable host whose copy differs; a host's reply can carry newer changes,
    /// which the next check follows.</summary>
    private async Task PushClusterAsync(IReadOnlyList<ClusterProbe> probes)
    {
        foreach (var probe in probes.Where(p => p.Reachable && p.Shares))
        {
            if (closing) return;
            var digest = clusterPlan.Digest();
            if (probe.Plan?.Digest() == digest || FindHost(probe.HostId) is not { } host) continue;
            if (await ClusterSync.MergeAsync(host.Pairing, clusterPlan, lifetime.Token) is not { } merged) continue;
            clusterProbes[probe.HostId] = probe with { Plan = merged };
            var next = ClusterPlan.Merge(clusterPlan, merged);
            if (next.Digest() == digest) continue;
            clusterPlan = next;
            SaveClusterPlan();
        }
    }

    // ---------- presentation ----------

    private string ClusterSignature() => string.Join("|", clusterEnabled, clusterPlan.Digest(),
        string.Join(",", clusterMisses.Where(m => m.Value > 0).OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => m.Key + Math.Min(m.Value, ClusterSync.FailAfter))),
        string.Join(",", clusterFollow.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => f.Key + f.Value)),
        string.Join(",", clusterProbes.Values.OrderBy(p => p.HostId, StringComparer.Ordinal)
            .Select(p => $"{p.HostId}:{p.Reachable}:{string.Join(';', ClusterSync.Roles(p.Routes ?? []).Select(r => r.Kind + r.Model))}")));

    private void ShowClusterStatus()
    {
        if (!clusterEnabled)
        {
            ClusterStatusText.Text = store is null ? "Unavailable without a local data folder."
                : "Off: each computer keeps its own choices and nothing is checked in the background. Turn it on to let jobs fail over to another host.";
            return;
        }
        if (clusterCheckedAt is not { } checkedAt)
        {
            ClusterStatusText.Text = "On. Checking your hosts...";
            return;
        }
        var probes = clusterProbes.Values.ToList();
        if (probes.Count == 0)
        {
            ClusterStatusText.Text = "On. No host is paired yet; the plan is shared as soon as you add a computer.";
            return;
        }
        var digest = clusterPlan.Digest();
        var current = probes.Count(p => p.Plan?.Digest() == digest);
        var down = probes.Count(p => !p.Reachable);
        var old = probes.Count(p => p.Reachable && !p.Shares);
        ClusterStatusText.Text = $"On. {current} of {probes.Count} hosts hold the current plan; checked {checkedAt.ToLocalTime():t}." +
            (down > 0 ? $" {down} not answering." : "") +
            (old > 0 ? $" {old} run{(old == 1 ? "s" : "")} an older Martlet: update {(old == 1 ? "it" : "them")} to share the plan (they can still do jobs)." : "");
    }

    /// <summary>The failover choice and sync notes under a job's row while Keep in sync is on, or null (sync off, or no
    /// host paired).</summary>
    private FrameworkElement? ClusterControls(string job)
    {
        if (!clusterEnabled || NetworkMap.Hosts(Inputs()).Count == 0) return null;
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var failover = new CheckBox
        {
            Content = "Fail over to another host", IsChecked = clusterPlan.For(job)?.Failover == true,
            ToolTip = $"If its host stops answering for about 30 seconds, move it to another paired host that runs {ClusterSync.Engine(job)}."
        };
        AutomationProperties.SetAutomationId(failover, ClusterSync.Title(job).Replace("-", "", StringComparison.Ordinal) + "Failover");
        AutomationProperties.SetName(failover, $"Fail {ClusterSync.Title(job).ToLowerInvariant()} over to another host");
        failover.Click += (_, _) => SetFailover(job, failover.IsChecked == true);
        panel.Children.Add(failover);
        if (ClusterNote(job) is { } note)
        {
            var text = new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
            text.SetResourceReference(StyleProperty, "Muted");
            AutomationProperties.SetAutomationId(text, ClusterSync.Title(job).Replace("-", "", StringComparison.Ordinal) + "ClusterNote");
            panel.Children.Add(text);
        }
        return panel;
    }

    private string? ClusterNote(string job)
    {
        var parts = new List<string>();
        var local = ClusterSync.Local(job, homeSettings, homeAvatar);
        var assignment = clusterPlan.For(job);
        if (clusterEnabled && clusterFollow.TryGetValue(job, out var follow))
            parts.Add($"The shared plan gives it to {(assignment is null ? "another computer" : ClusterSync.Who(job, assignment.HostId, assignment.Off))}, but {follow}");
        if (clusterEnabled && local.HostId is { } host && clusterProbes.ContainsKey(host) &&
            clusterMisses.GetValueOrDefault(host + "/" + job) > 0)
        {
            var engine = ClusterSync.Engine(job);
            parts.Add(assignment?.Failover != true ? $"{host} is not doing it right now. Turn on failover to move it automatically."
                : ClusterSync.FailoverTarget(clusterPlan, job, host, clusterProbes.Values, HardwareStore?.Load() ?? []) is null
                    ? $"{host} is not doing it right now, and no other paired host runs {engine} to take over."
                    : $"{host} is not doing it right now; it moves to another host that runs {engine} if this continues.");
        }
        if (assignment is { MovedFrom: { } from } && assignment.HostId == local.HostId && assignment.HostId is not null)
            parts.Add($"Moved here from {from} when it stopped answering; choose {from} again to move it back.");
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}
