using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.HostClient;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Sync;
using Martlet.F5;

namespace Martlet.Desktop;

/// <summary>Shared "who does what". This PC keeps a copy of the cluster plan, records every job change made here,
/// and, while "Keep in sync" is on (the default), checks every paired host every 15 seconds: it merges the hosts' copies, follows
/// changes made on the owner's other computers, moves a job whose host stopped answering to another host that runs
/// the same engine (failover, per job) and pushes the result to every host with an older copy.</summary>
public partial class MainWindow
{
    private static string ClusterDevice => HostSetupCommands.SuggestedDeviceId();
    private readonly DispatcherTimer clusterTimer = new() { Interval = ClusterSync.Interval };
    private ClusterPlan clusterPlan = ClusterPlan.Empty;
    private bool clusterEnabled;
    private bool clusterBusy;
    private DateTimeOffset? clusterCheckedAt;
    /// <summary>Consecutive checks in which a host did not serve a job, keyed "host/job".</summary>
    private readonly Dictionary<string, int> clusterMisses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClusterProbe> clusterProbes = new(StringComparer.Ordinal);
    /// <summary>Whether each host answers the checks, for the log and the status line (one missed check is not a change).</summary>
    private readonly HostAnswers hostAnswers = new(ClusterSync.FailAfter);
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
        WorkSharingRoster.Shared.DataDirectory = store.DataDirectory;
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
            ActionText.Text = $"Couldn't save the sync setting, so sync stays {(clusterEnabled ? "on" : "off")}.";
            return;
        }
        clusterEnabled = on;
        clusterMisses.Clear();
        clusterProbes.Clear();
        hostAnswers.Clear();
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
            : "Sync is off: this PC keeps its own choices, settings and memories; the people Martlet recognizes by voice stay shared.";
        ShowClusterStatus();
        ShowSettingsStatus();
        ShowMemorySyncStatus();
        RenderDevicePages();
        RefreshCoverage();
    }

    /// <summary>What this PC does for <paramref name="job"/>, counting a job it does itself as done by its own host service.</summary>
    private LocalJob LocalJobFor(string job) => ClusterSync.Local(job, homeSettings, homeAvatar, OwnHostId(), SharedHostIds());

    /// <summary>The hosts friends share with this PC.</summary>
    private IReadOnlyCollection<string> SharedHostIds() => sharedHosts.Select(h => h.HostId).ToHashSet(StringComparer.Ordinal);

    /// <summary>The hosts whose jobs may go into your shared plan: your hosts paired here and this PC's own host service.</summary>
    private IReadOnlyCollection<string> OwnJobHosts()
    {
        var own = homeHosts.Select(h => h.HostId).ToHashSet(StringComparer.Ordinal);
        if (OwnHostId() is { } mine) own.Add(mine);
        return own;
    }

    /// <summary>The host service Martlet runs on this PC: the one paired here, or on a host PC the one its dashboard reads.</summary>
    private string? OwnHostId() => ThisPcHost()?.HostId ?? (Role == DeviceRole.Host ? hostState?.HostId : null);

    /// <summary>The owner's shared route for a conversation job (what it uses when no host does it), or null.</summary>
    private SharedSetting? SharedJobSetting(string job) => job switch
    {
        ClusterJobs.Thinking => settingsNode?.Document.Find(AppSettingsSections.Thinking),
        ClusterJobs.Listening => settingsNode?.Document.Find(AppSettingsSections.Listening),
        ClusterJobs.Speaking => settingsNode?.Document.Find(AppSettingsSections.Speaking),
        _ => null
    };

    /// <summary>Records a job change made on this PC (on the Devices page or in Setup) as the newest entry of the shared plan. A
    /// job on a host a friend shares with this PC stays this PC's own choice: it is never recorded, and neither is a host this PC
    /// no longer has.</summary>
    private void RecordClusterJob(string job, LocalJob local)
    {
        clusterObserved[job] = ObservedJob(job);
        clusterFollow.Remove(job);
        if (store is null || !ClusterSync.Recordable(local, OwnJobHosts())) return;
        var current = clusterPlan.For(job);
        if (current is not null && current.HostId == local.HostId && current.Off == local.Off && current.MovedFrom is null) return;
        clusterPlan = clusterPlan.Assign(job, local.HostId, local.Off, current?.Failover ?? false, null, ClusterDevice, DateTimeOffset.UtcNow);
        SaveClusterPlan();
        QueueClusterSync();
    }

    /// <summary>What this PC does for a job, as compared between checks to notice changes: a job it does itself counts as that
    /// whether or not its own host service is known yet, so learning about the host service later is not a change.</summary>
    private LocalJob ObservedJob(string job) => ClusterSync.Local(job, homeSettings, homeAvatar, ClusterSync.ThisPcMarker, SharedHostIds());

    /// <summary>Notices jobs that changed outside the Devices page (for example in Setup) and records them. A host PC uses no
    /// jobs, so it records none. A job leaving a host a friend shares isn't recorded either: this PC follows the plan again.</summary>
    private void ObserveLocalJobs()
    {
        if (store is null || homeSettings?.Setup is null) return;
        foreach (var job in ClusterJobs.All)
        {
            var observed = ObservedJob(job);
            if (Role == DeviceRole.Companion && clusterObserved.TryGetValue(job, out var seen) && seen != observed && !seen.Shared)
                RecordClusterJob(job, LocalJobFor(job));
            clusterObserved[job] = observed;
        }
    }

    /// <summary>What your Martlet network does for a job, for its Companion page, when that is a host: this PC switches to it,
    /// or (a job this PC does itself) your other computers use this PC for it. Null when sync is off or no host does it.</summary>
    private string? NetworkJobNote(string job)
    {
        if (!clusterEnabled || clusterPlan.For(job) is not { HostId: { } host } assignment) return null;
        var local = LocalJobFor(job);
        if (local.Shared)
            return $"This PC uses {local.HostId}, a host a friend shares with it, for {job}; your Martlet network does it on {host} for " +
                "your other computers.";
        if (local.HostId == host) return local.Here ? $"Your other computers use this PC for {job}, through {host}." : null;
        var by = assignment.UpdatedBy == ClusterDevice ? "" : $", as chosen on {assignment.UpdatedBy}";
        return $"Your Martlet network does {job} on {host}{by}, and this PC switches to it " + (clusterFollow.TryGetValue(job, out var why)
            ? "as soon as it can: " + why
            : "on its next check.");
    }

    /// <summary>Marks a forgotten host as removed in the shared plan, so its entry does not return from an older copy.</summary>
    private void ForgetClusterHost(string hostId)
    {
        clusterProbes.Remove(hostId);
        hostAnswers.Forget(hostId);
        if (store is null) return;
        if (clusterPlan.Node(hostId) is not { Removed: false }) return;
        clusterPlan = clusterPlan.Observe(hostId, null, [], true, ClusterDevice, DateTimeOffset.UtcNow);
        SaveClusterPlan();
        QueueClusterSync();
    }

    private void SetFailover(string job, bool on)
    {
        if (store is null || !clusterEnabled) return;
        var local = LocalJobFor(job);
        var current = clusterPlan.For(job);
        // A job on a host a friend shares is this PC's own: failover concerns what your network chose.
        var owner = current is null ? local.Shared ? new LocalJob(null, false) : local : new LocalJob(current.HostId, current.Off);
        if (on && !ConfirmationDialog.Confirm(this,
                $"Turn on failover for {ClusterSync.Title(job).ToLowerInvariant()}, so that Martlet moves it to another paired host, which gets " +
                $"this job's data{(job == ClusterJobs.Speaking ? " and the selected voice" : "")}, if {(owner.HostId is { } host ? host : "its host")} stops responding?",
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
            ActionText.Text = "Couldn't save device sync, so hosts keep their last copy.";
        }
    }

    /// <summary>Runs a sync once the current role change has finished (it holds its <see cref="ChangeTurns"/> turn).</summary>
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
            if (homeSettings?.Setup is not null)
            {
                var own = OwnJobHosts();
                foreach (var job in ClusterJobs.All)
                {
                    var local = LocalJobFor(job);
                    var current = plan.For(job);
                    // A job nobody recorded yet takes this PC's real choice (never Martlet's default on a new computer), and a
                    // job set up to run on this PC is done by this PC for every computer, companion or host PC alike. Only a
                    // host of yours goes in, never one a friend shares (or shared before this PC forgot it).
                    if (current is null && !host && ClusterSync.Seeds(local) && ClusterSync.Recordable(local, own) ||
                        ClusterSync.Claims(job, current, local, SharedJobSetting(job), ClusterSync.OwnOllama(homeSettings), ClusterDevice,
                            settingsCheckedAt))
                    {
                        plan = plan.Assign(job, local.HostId, local.Off, current?.Failover ?? false, null, ClusterDevice, now);
                        if (current is not null)
                            ErrorLog.Info($"{ClusterSync.Title(job)} was set up to run on this PC, so this PC does it for all your " +
                                $"computers through {local.HostId}.");
                    }
                }
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
            if (!host && !setupOperations.IsRunning && homeSettingsState is SettingsLoadState.Loaded or SettingsLoadState.FirstRun &&
                changes.TryTake() is { } turn)
                using (turn)
                {
                    followed = await FollowClusterAsync(events);
                    // A host that keeps a job but serves another model now (its role's settings changed, maybe on another
                    // computer): every computer follows that model.
                    var reachable = probes.Where(p => p.Reachable).Select(p => p.HostId).ToArray();
                    FollowDeepThinkingModels(reachable, events);
                    followed |= await FollowJobModelsAsync(reachable, events);
                }
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
                    RenderDevicePages();
                    // A host PC's Home asks for the roles of the jobs your computers give it.
                    if (host) RenderHost();
                }
                RefreshCoverage();
            }
        }
    }
}
