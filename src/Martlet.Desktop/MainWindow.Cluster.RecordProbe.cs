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

/// <summary>More of MainWindow.Cluster.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
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
        // Logged once per change: a host counts as not answering only after it missed ClusterSync.FailAfter checks in a row.
        if (hostAnswers.Record(probe.HostId, probe.Reachable, probe.Text) is { } change)
        {
            if (change.Answering) ErrorLog.Info(change.Text);
            else ErrorLog.Warn(change.Text);
        }
        HostPresence.Note(probe.HostId, probe.Reachable);
        if (!probe.Reachable)
        {
            // One slow answer from a busy host is not a change: its last check stands until it counts as not answering.
            if (previous?.Reachable == true && hostAnswers.Answering(probe.HostId)) return;
            hostChecks[probe.HostId] = new(false, probe.Text, null, release);
            return;
        }
        var offers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var route in probe.Routes ?? [])
            if (HostRoles.ForRoute(route.RouteId) is { } role) offers[role.Kind] = route.ModelId;
        var next = new HostCheck(true, "", offers, release, probe.Routes);
        hostChecks[probe.HostId] = next with
        {
            Text = previous?.Reachable == true && previous.Offers?.Count == offers.Count &&
                offers.All(o => previous.Offers.GetValueOrDefault(o.Key) == o.Value) && previous.DeepThinkingSlots == next.DeepThinkingSlots
                ? previous.Text : HostControl.Describe(offers, probe.Routes)
        };
        NoteSingingHost();
        NoteThinkingPoolHost(probe.HostId);
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

    /// <summary>Makes this PC do what the shared plan says for each job (the caller holds the <see cref="ChangeTurns"/> turn). A
    /// job it cannot follow keeps its current route and shows why on its tile; the plan itself is not changed for that.</summary>
    private async Task<bool> FollowClusterAsync(List<string> events)
    {
        var changed = false;
        foreach (var job in ClusterJobs.All)
        {
            if (closing || clusterPlan.For(job) is not { } desired) continue;
            var before = LocalJobFor(job);
            if (ClusterSync.Matches(desired, before) && !SpeakingEngineMoved(job, desired))
            {
                clusterFollow.Remove(job);
                continue;
            }
            string? problem;
            // While it switches to a host that is ready (a change usually made on another computer), this PC shows Configuring; not
            // again on every check for a job it couldn't follow last time.
            if (!clusterFollow.ContainsKey(job) && (desired.HostId is null || clusterProbes.GetValueOrDefault(desired.HostId)?.Serves(job) == true))
            {
                clusterFollowingText = $"Switching {ClusterSync.Title(job).ToLowerInvariant()} to {ClusterSync.Who(job, desired.HostId, desired.Off)}";
                ShowConfiguring();
            }
            try
            {
                problem = job == ClusterJobs.LipSync ? await FollowLipSyncAsync(desired)
                    : await FollowJobAsync(HostJob.All.First(j => j.Job == job), desired);
            }
            catch (OperationCanceledException) { throw; }
            catch (F5Exception error) { problem = F5Voices.Describe(error); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
                ContractException or JsonException or ArgumentException or HostConnectionException)
            {
                problem = error.Message;
            }
            finally
            {
                if (clusterFollowingText is not null)
                {
                    clusterFollowingText = null;
                    ShowConfiguring();
                }
            }
            var after = LocalJobFor(job);
            clusterObserved[job] = ObservedJob(job);
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
        WorkSharingRoster.Shared.OwnHostId = OwnHostId();
        if (!clusterEnabled)
        {
            ClusterStatusText.Text = store is null ? "Device sync is unavailable."
                : "Sync is off, so this PC keeps its own choices.";
            return;
        }
        if (clusterCheckedAt is not { } checkedAt)
        {
            ClusterStatusText.Text = "Sync is on; checking hosts...";
            return;
        }
        var probes = clusterProbes.Values.ToList();
        if (probes.Count == 0)
        {
            ClusterStatusText.Text = "Sync is on: pair a host to share choices with your other computers.";
            return;
        }
        var digest = clusterPlan.Digest();
        var current = probes.Count(p => p.Plan?.Digest() == digest);
        var down = probes.Count(p => !p.Reachable && !hostAnswers.Answering(p.HostId));
        var old = probes.Count(p => p.Reachable && !p.Shares);
        ClusterStatusText.Text = $"Sync is on: {current} of {probes.Count} hosts up to date, checked {checkedAt.ToLocalTime():t}" +
            (down > 0 ? $", {down} not responding" : "") +
            (old > 0 ? $"; update {(old == 1 ? "one host" : old + " hosts")} to sync choices" : "") + ".";
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
        var local = LocalJobFor(job);
        var assignment = clusterPlan.For(job);
        if (local.Shared)
            parts.Add($"{local.HostId} is a host a friend shares with this PC: only this PC uses it for {job}, your other computers " +
                "keep your network's choice, and its owner's own work comes first.");
        if (clusterEnabled && clusterFollow.TryGetValue(job, out var follow))
            parts.Add($"Sync wants {(assignment is null ? "another computer" : ClusterSync.Who(job, assignment.HostId, assignment.Off))}, but {follow}");
        if (clusterEnabled && local is { Here: true, HostId: { } own } && assignment?.HostId == own &&
            clusterProbes.GetValueOrDefault(own) is { Reachable: true } ownProbe && !ownProbe.Serves(job))
            parts.Add($"Your other computers use this PC for {job} through {own}, which doesn't run " +
                $"{HostRoles.Get(ClusterSync.RoleKind(job)).Name} yet, so add it to {own}.");
        else if (clusterEnabled && local.HostId is { } host && clusterProbes.ContainsKey(host) &&
            clusterMisses.GetValueOrDefault(host + "/" + job) > 0)
        {
            parts.Add(assignment?.Failover != true ? $"{host} isn't handling this right now; turn on failover to move it automatically."
                : ClusterSync.FailoverTarget(clusterPlan, job, host, clusterProbes.Values, HardwareStore?.Load() ?? []) is null
                    ? $"{host} isn't handling this, and no other paired host can take over."
                    : $"{host} isn't handling this, so Martlet will move it if this continues.");
        }
        if (assignment is { MovedFrom: { } from } && assignment.HostId == local.HostId && assignment.HostId is not null)
            parts.Add($"Moved from {from} after it stopped responding; choose {from} to move it back.");
        // Two notes at once join into one sentence.
        return parts.Count == 0 ? null
            : string.Join("; ", parts.Select((p, i) => (i == 0 ? p : char.ToLowerInvariant(p[0]) + p[1..]).TrimEnd('.'))) + ".";
    }
}
