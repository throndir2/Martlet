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
/// and, while "Keep in sync" is on (the default), checks every paired host every 15 seconds: it merges the hosts' copies, follows
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

    /// <summary>Checked/Unchecked rather than Click, so UI Automation's toggle (MCP ui_toggle) changes the choice too.
    /// Setting the box to the current choice (at start, or after a failed save) changes nothing.</summary>
    private void ClusterSync_Changed(object sender, RoutedEventArgs e)
    {
        var on = ClusterSyncChoice.IsChecked == true;
        if (store is null || on == clusterEnabled) return;
        try { ClusterSync.SaveEnabled(store.DataDirectory, on); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ClusterSyncChoice.IsChecked = clusterEnabled;
            ActionText.Text = $"Couldn't save sync setting. Sync stays {(clusterEnabled ? "on" : "off")}.";
            return;
        }
        clusterEnabled = on;
        clusterMisses.Clear();
        clusterProbes.Clear();
        clusterFollow.Clear();
        clusterCheckedAt = null;
        settingsCheckedAt = null;
        settingsCopies.Clear();
        memoryCopies.Clear();
        if (on) StartCluster();
        else clusterTimer.Stop();
        QueueSettingsSync();
        QueueMemorySync();
        if (on)
        {
            QueueVoiceSync();
            QueueSpeakingVoiceSync();
            QueueCharacterModelSync();
            QueueCreationSync();
            SyncHomeShareAsync().Forget();
        }
        ActionText.Text = on ? "Martlet is now the same on all your computers: who does what, its settings, memories, people, voices, characters, creations and Home Assistant stay in sync."
            : "Sync is off. This PC keeps its own choices, settings and memories.";
        ShowClusterStatus();
        ShowSettingsStatus();
        ShowMemorySyncStatus();
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

    /// <summary>Notices jobs that changed outside the Devices page (for example in Setup) and records them. A host PC uses no
    /// jobs, so it records none.</summary>
    private void ObserveLocalJobs()
    {
        if (store is null || homeSettings?.Setup is null) return;
        foreach (var job in ClusterJobs.All)
        {
            var local = ClusterSync.Local(job, homeSettings, homeAvatar);
            if (Role == DeviceRole.Companion && clusterObserved.TryGetValue(job, out var seen) && seen != local) RecordClusterJob(job, local);
            clusterObserved[job] = local;
        }
    }

    /// <summary>Marks a forgotten host as removed in the shared plan, so its entry does not return from an older copy.</summary>
    private void ForgetClusterHost(string hostId)
    {
        clusterProbes.Remove(hostId);
        if (store is null) return;
        if (clusterPlan.Node(hostId) is not { Removed: false }) return;
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
        if (on && !ConfirmationDialog.Confirm(this,
                $"Turn on failover for {ClusterSync.Title(job).ToLowerInvariant()}? If {(owner.HostId is { } host ? host : "its host")} stops responding, " +
                "Martlet moves it to another paired host that can handle it. That host will receive this job's data." +
                (job == ClusterJobs.Speaking ? " The selected voice goes with it." : ""),
                "Turn on failover"))
        {
            RenderMap();
            return;
        }
        clusterPlan = clusterPlan.Assign(job, owner.HostId, owner.Off, on, current?.MovedFrom, ClusterDevice, DateTimeOffset.UtcNow);
        SaveClusterPlan();
        ActionText.Text = on ? $"Failover is on for {ClusterSync.Title(job).ToLowerInvariant()}."
            : $"Failover is off for {ClusterSync.Title(job).ToLowerInvariant()}.";
        QueueClusterSync();
        RenderMap();
    }

    private void SaveClusterPlan()
    {
        if (store is null) return;
        try { ClusterSync.SavePlan(store.DataDirectory, clusterPlan); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException)
        {
            ActionText.Text = "Couldn't save device sync. Hosts keep their last copy.";
        }
    }

    /// <summary>Runs a sync once the current role change has finished (it holds <see cref="assigningRole"/>).</summary>
    private void QueueClusterSync()
    {
        if (clusterEnabled && !closing) Dispatcher.InvokeAsync(() => SyncClusterAsync().Forget(), DispatcherPriority.ContextIdle);
    }

    private async Task SyncClusterAsync()
    {
        if (!clusterEnabled || clusterBusy || closing || store is null || setupService is null) return;
        clusterBusy = true;
        var events = new List<string>();
        var followed = false;
        // A host PC uses no jobs: it receives the plan (so it shows who does what) and passes it on, but never records, fails
        // over or follows a job.
        var host = Role == DeviceRole.Host;
        try
        {
            if (!host) ObserveLocalJobs();
            var hosts = NetworkMap.Hosts(Inputs());
            var probes = await Task.WhenAll(hosts.Select(h => ClusterSync.ProbeAsync(h.Pairing, lifetime.Token)));
            if (closing || !clusterEnabled) return;
            clusterCheckedAt = DateTimeOffset.UtcNow;
            clusterProbes.Clear();
            foreach (var probe in probes) RecordProbe(probe);
            if (!host) ObserveLocalJobs();

            var now = DateTimeOffset.UtcNow;
            var before = clusterPlan.Digest();
            var plan = clusterPlan;
            foreach (var probe in probes)
                if (probe.Plan is { } copy) plan = ClusterPlan.Merge(plan, copy);
            if (!host && homeSettings?.Setup is not null)
                foreach (var job in ClusterJobs.All.Where(job => plan.For(job) is null))
                {
                    var local = ClusterSync.Local(job, homeSettings, homeAvatar);
                    plan = plan.Assign(job, local.HostId, local.Off, false, null, ClusterDevice, now);
                }
            if (!host)
                foreach (var probe in probes.Where(p => p.Reachable))
                {
                    var origin = hosts.First(h => h.HostId == probe.HostId).Pairing.Origin;
                    var roles = ClusterSync.Roles(probe.Routes ?? []);
                    if (plan.Node(probe.HostId) is not { Removed: false } node || node.Origin != origin || !node.Roles.SequenceEqual(roles))
                        plan = plan.Observe(probe.HostId, origin, roles, false, ClusterDevice, now);
                }
            clusterPlan = host ? plan : Failover(plan, probes, now, events);
            if (clusterPlan.Digest() != before) SaveClusterPlan();
            // A new PC follows the plan as soon as it is paired, before anything is set up on it: that is how it starts using
            // your hosts. Unreadable settings are left alone.
            if (!host && !assigningRole && !setupOperations.IsRunning && homeSettingsState is SettingsLoadState.Loaded or SettingsLoadState.FirstRun)
                followed = await FollowClusterAsync(events);
            await PushClusterAsync(probes);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (!closing) ActionText.Text = "Couldn't sync device choices: " + error.Message;
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
        var release = previous?.MartletVersion ?? hostReleases.GetValueOrDefault(probe.HostId);
        if (previous?.Reachable != false && !probe.Reachable)
            ErrorLog.Warn($"Host {probe.HostId} {(previous is null ? "didn't answer" : "stopped answering")}: {probe.Text}");
        else if (previous?.Reachable == false && probe.Reachable)
            ErrorLog.Info($"Host {probe.HostId} answers again.");
        if (!probe.Reachable)
        {
            hostChecks[probe.HostId] = new(false, probe.Text, null, release);
            return;
        }
        var offers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var route in probe.Routes ?? [])
            if (HostRoles.ForRoute(route.RouteId) is { } role) offers[role.Kind] = route.ModelId;
        hostChecks[probe.HostId] = new(true, previous?.Reachable == true && previous.Offers?.Count == offers.Count &&
                offers.All(o => previous.Offers.GetValueOrDefault(o.Key) == o.Value) ? previous.Text : HostControl.Describe(offers),
            offers, release, probe.Routes);
        NoteSingingHost();
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
                if (ClusterSync.Matches(desired, before) && !SpeakingEngineMoved(job, desired))
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
                    events.Add($"{title} now uses {ClusterSync.Who(job, after.HostId, after.Off)}" +
                        (desired.UpdatedBy == ClusterDevice ? "." : $", as chosen on {desired.UpdatedBy}."));
            }
        }
        finally { assigningRole = false; }
        if (changed) openConversation?.ReloadWhenIdle("Device choices changed.");
        return changed;
    }

    /// <summary>Speaking stays on its host but that host now speaks with another voice engine (one runs on a computer at a
    /// time, and another computer switched it): this PC follows the engine the host runs.</summary>
    private bool SpeakingEngineMoved(string job, ClusterAssignment desired)
    {
        if (job != ClusterJobs.Speaking || desired.HostId is not { } hostId) return false;
        var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
        if (route is not { RouteType: SetupRouteType.GatewayF5, GatewaySnapshot: { } snapshot } || route.Gateway?.HostId != hostId) return false;
        return clusterProbes.GetValueOrDefault(hostId) is { Reachable: true, Routes: { } routes } &&
            routes.All(r => r.RouteId != snapshot.RouteId) && HostVoiceEngine(routes) is not null;
    }

    /// <summary>The voice engine a host speaks with now, from the routes it offers (it runs one at a time).</summary>
    private static SpeechEngine? HostVoiceEngine(IEnumerable<HostRoute> routes) =>
        routes.Select(r => SpeechEngines.ForRoute(r.RouteId)).OfType<SpeechEngine>().FirstOrDefault();

    private async Task<string?> FollowJobAsync(HostJob job, ClusterAssignment desired)
    {
        if (desired.HostId is null)
        {
            // The route the owner's computers share wins over the one this PC kept aside, which is used when this PC can't
            // use the shared one yet (the settings sync keeps trying and says why).
            var (shared, sharedProblem) = await HandBackToSharedRouteAsync(job);
            if (shared && sharedProblem is null) return null;
            if (JobSavedRoute.Load(store!.DataDirectory, job.SavedFile) is not { } saved)
                return sharedProblem ?? $"choose a Setup option for {job.Job} on this PC.";
            var loaded = await setupService!.LoadAsync(lifetime.Token);
            if (loaded.Error is not null) return loaded.Error.Summary;
            var next = HostHandoff.Back(UseModels(SetupSettings.Begin(loaded.Settings)), saved);
            var result = await setupService.SaveAsync(next, loaded.Revision, lifetime.Token);
            if (!result.Save.Saved) return result.Summary;
            homeSettings = next;
            return null;
        }
        if (FindHost(desired.HostId) is not { } host)
            return $"pair {desired.HostId} with this PC first.";
        var routes = clusterProbes.GetValueOrDefault(host.HostId)?.Routes;
        // Speaking follows the voice engine the host runs (another computer may have switched it), so every computer speaks
        // with the same engine.
        if (job.Role == SetupRole.Tts && routes?.Any(r => r.RouteId == job.RouteId) != true && routes is not null &&
            HostVoiceEngine(routes) is { } running)
        {
            job = HostJob.SpeakingFor(running);
            if (SpeakingEngineChoice.Current != running) SpeakingEngineChoice.Save(store!.DataDirectory, running);
        }
        if (routes?.FirstOrDefault(r => r.RouteId == job.RouteId) is not { } route)
            return $"{host.HostId} isn't ready for {job.Job} yet.";
        F5ReferenceSettings? reference = null;
        F5ReferenceSnapshot? voice = null;
        if (job.RouteType == SetupRouteType.GatewayF5)
        {
            reference = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts)?.Reference;
            if (reference is null || reference.ProcessingDestinationId != route.DestinationId)
            {
                // The voice chosen on all computers (shared speaking voices) for this engine, else the one applied here.
                reference = null;
                voice = await F5Voices.DefaultAsync(store!.DataDirectory, route.DestinationId, lifetime.Token, SpeechEngines.ForRoute(job.RouteId));
                if (voice is null || voice.Rights.ProcessingDestinationId != route.DestinationId)
                    return $"choose the voice for {host.HostId} under Speaking.";
            }
        }
        await SaveJobHostAsync(job, host, route, voice, reference);
        return null;
    }

    private async Task<string?> FollowLipSyncAsync(ClusterAssignment desired)
    {
        PairedHost? host = null;
        if (desired.HostId is { } id && (host = FindHost(id)) is null)
            return $"pair {id} with this PC first.";
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
            ClusterStatusText.Text = store is null ? "Device sync is unavailable."
                : "Sync is off. This PC keeps its own choices.";
            return;
        }
        if (clusterCheckedAt is not { } checkedAt)
        {
            ClusterStatusText.Text = "Sync is on. Checking hosts...";
            return;
        }
        var probes = clusterProbes.Values.ToList();
        if (probes.Count == 0)
        {
            ClusterStatusText.Text = "Sync is on. Add a computer to share choices.";
            return;
        }
        var digest = clusterPlan.Digest();
        var current = probes.Count(p => p.Plan?.Digest() == digest);
        var down = probes.Count(p => !p.Reachable);
        var old = probes.Count(p => p.Reachable && !p.Shares);
        ClusterStatusText.Text = $"Sync is on. {current}/{probes.Count} hosts are up to date; checked {checkedAt.ToLocalTime():t}." +
            (down > 0 ? $" {down} not responding." : "") +
            (old > 0 ? $" Update {(old == 1 ? "one host" : old + " hosts")} to sync choices." : "");
    }

    /// <summary>The failover choice and sync notes under a job's row while Keep in sync is on, or null (sync off, or no
    /// host paired).</summary>
    private FrameworkElement? ClusterControls(string job)
    {
        if (!clusterEnabled || NetworkMap.Hosts(Inputs()).Count == 0) return null;
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var failover = new CheckBox
        {
            Content = "Fail over automatically", IsChecked = clusterPlan.For(job)?.Failover == true,
            ToolTip = "Move this job to another paired host if this one stops responding."
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
            parts.Add($"Sync wants {(assignment is null ? "another computer" : ClusterSync.Who(job, assignment.HostId, assignment.Off))}, but {follow}");
        if (clusterEnabled && local.HostId is { } host && clusterProbes.ContainsKey(host) &&
            clusterMisses.GetValueOrDefault(host + "/" + job) > 0)
        {
            parts.Add(assignment?.Failover != true ? $"{host} isn't handling this right now. Turn on failover to move it automatically."
                : ClusterSync.FailoverTarget(clusterPlan, job, host, clusterProbes.Values, HardwareStore?.Load() ?? []) is null
                    ? $"{host} isn't handling this, and no other paired host can take over."
                    : $"{host} isn't handling this. Martlet will move it if this continues.");
        }
        if (assignment is { MovedFrom: { } from } && assignment.HostId == local.HostId && assignment.HostId is not null)
            parts.Add($"Moved from {from} after it stopped responding. Choose {from} to move it back.");
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}
