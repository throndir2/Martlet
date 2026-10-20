using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.HostClient;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.F5;

namespace Martlet.Desktop;

/// <summary>More of MainWindow.HostJobs.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    /// <summary>Saves the job's route as the host's gateway route: pinned endpoint, this PC's pairing (whose secret stays
    /// where pairing saved it), the advertised route snapshot, enabled, with the selection recorded as the user's choice.
    /// Speaking also applies the chosen reference voice in the F5 preset store and keeps it on the route, or reuses an
    /// already applied <paramref name="reference"/> (failover between F5 hosts keeps the voice).
    /// The previous route is kept aside so the job can go back to it without re-entering its key.</summary>
    private async Task SaveJobHostAsync(HostJob job, PairedHost host, HostRoute route, F5ReferenceSnapshot? voice = null,
        F5ReferenceSettings? reference = null)
    {
        var loaded = await setupService!.LoadAsync(lifetime.Token);
        if (loaded.Error is not null) throw new InvalidOperationException(loaded.Error.Summary);
        var settings = UseModels(SetupSettings.Begin(loaded.Settings));
        if (job.RouteType == SetupRouteType.GatewayF5 && reference is null)
        {
            if (voice is null) throw new InvalidOperationException("Choose a voice first.");
            if (voice.Rights.ProcessingDestinationId != route.DestinationId)
                throw new InvalidOperationException($"Choose a voice for {host.HostId} again.");
            reference = await F5Voices.ApplyAsync(store!.DataDirectory, voice, lifetime.Token);
        }
        var previous = settings.Setup!.Routes.FirstOrDefault(r => r.Role == job.Role);
        var endpoint = new GatewayEndpointSettings
        {
            SchemaVersion = 1, Origin = host.Pairing.Origin, HostId = host.HostId,
            SpkiFingerprint = host.Pairing.SpkiFingerprint, DeviceRole = SelfHostSetup.GatewayRole
        };
        var next = HostHandoff.ToHost(settings, job.RouteType, endpoint, HostPairingCredential.ToGuid(host.Pairing.CredentialId),
            host.Pairing.DeviceId, Snapshot(route, job.RouteType), reference);
        if (previous is not null && !SelfHostSetup.IsGateway(previous.RouteType))
            JobSavedRoute.Save(store!.DataDirectory, job.SavedFile, previous);
        var saved = await setupService.SaveAsync(next, loaded.Revision, lifetime.Token);
        if (!saved.Save.Saved) throw new InvalidOperationException(saved.Summary);
        homeSettings = next;
        FollowSavedSetup(saved.Save.Revision);
    }

    /// <summary>Puts the job back on its Setup choice after one confirmation. <paramref name="turn"/> is the caller's
    /// <see cref="ChangeTurns"/> turn, let go once saved: stopping the voice engine Speaking left is a host run.</summary>
    private async Task JobBackAsync(HostJob job, ChangeTurns.Turn turn)
    {
        var loaded = await setupService!.LoadAsync(lifetime.Token);
        if (loaded.Error is not null) throw new InvalidOperationException(loaded.Error.Summary);
        pendingJobHosts.Remove(job.Role);
        pendingJobVoices.Remove(job.Role);
        if (NetworkMap.JobHost(loaded.Settings, job.Role) is null)
        {
            ActionText.Text = $"{job.Title} already uses the choice from Setup.";
            return;
        }
        if (JobSavedRoute.Load(store!.DataDirectory, job.SavedFile) is not { } saved)
        {
            ActionText.Text = $"Choose how Martlet handles {job.Job}.";
            OpenCompanion(TabFor(job.Role));
            return;
        }
        var name = NetworkMap.ProviderName(saved);
        var leaving = job.Role == SetupRole.Tts ? LeavingEngine(null, null) : null;
        if (!ConfirmationDialog.Confirm(this,
                (IsCloud(saved) ? $"Use {name} for {job.Job} again, sending {Lowered(job.Sent)} there where requests may cost money"
                    : $"Use {name} for {job.Job} again on this PC") + StopsClause([], leaving) + "?",
                "Use the Setup choice"))
            return;
        await HandBackAsync(job, saved);
        turn.Dispose();
        if (leaving is not null) ActionText.Text += await StopLeftVoiceEngineAsync(leaving);
    }

    /// <summary>Puts the job back on the route kept aside while a host did it, already confirmed by the caller, and records
    /// it in the shared plan (each computer uses its own choice).</summary>
    private async Task HandBackAsync(HostJob job, SetupRoute saved, bool record = true)
    {
        var loaded = await setupService!.LoadAsync(lifetime.Token);
        if (loaded.Error is not null) throw new InvalidOperationException(loaded.Error.Summary);
        var settings = UseModels(SetupSettings.Begin(loaded.Settings));
        pendingJobHosts.Remove(job.Role);
        pendingJobVoices.Remove(job.Role);
        var name = NetworkMap.ProviderName(saved);
        var next = HostHandoff.Back(settings, saved);
        var result = await setupService.SaveAsync(next, loaded.Revision, lifetime.Token);
        if (!result.Save.Saved) throw new InvalidOperationException(result.Summary);
        homeSettings = next;
        FollowSavedSetup(result.Save.Revision);
        if (record) RecordClusterJob(job.Job, new(null, false));
        else clusterObserved[job.Job] = ObservedJob(job.Job);
        ActionText.Text = $"{job.Title} now uses {name}.{OpenConversationFollows}" +
            (saved.CredentialId is not null && next.Setup!.Routes.First(r => r.Role == job.Role).CredentialId is null
                ? " Its key is missing, so save it again in Setup." : "");
    }

    /// <summary>", stopping XTTS-v2 there and F5-TTS on gpu-1 to free graphics card memory": the voice engines a move stops
    /// (<paramref name="others"/> on the new host, <paramref name="leaving"/> where Speaking was), for a one-sentence
    /// confirmation; nothing when it stops none. Their downloads stay (docs/wiki/Multiple-Computers.md).</summary>
    private string StopsClause(IReadOnlyList<HostRoleInfo> others, (PairedHost Host, SpeechEngine Engine)? leaving)
    {
        var parts = new List<string>();
        if (others.Count > 0) parts.Add($"{HostRoles.Names(others.Select(o => o.Kind))} there");
        if (leaving is { } left) parts.Add($"{left.Engine.Name} on {HostWhere(left.Host)}");
        return parts.Count == 0 ? "" : $", stopping {string.Join(" and ", parts)} to free graphics card memory";
    }

    /// <summary>After an install started from a handoff, checks the host until its route appears (up to an hour, as model
    /// downloads can be slow) and then completes the handoff the user already confirmed, including stopping
    /// <paramref name="leaving"/>, the voice engine Speaking leaves on another computer.</summary>
    private async Task WatchJobHandoffAsync(HostJob job, PairedHost host, (PairedHost Host, SpeechEngine Engine)? leaving = null)
    {
        for (var attempt = 0; attempt < 180; attempt++)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(20), lifetime.Token); }
            catch (OperationCanceledException) { return; }
            if (pendingJobHosts.GetValueOrDefault(job.Role) != host.HostId || closing) return;
            HostCheck check;
            try { check = await HostControl.CheckAsync(host.Pairing, HardwareStore, lifetime.Token); }
            catch (OperationCanceledException) { return; }
            if (check.Routes?.FirstOrDefault(r => r.RouteId == job.RouteId) is not { } route) continue;
            hostChecks[host.HostId] = check;
            if (pendingJobHosts.GetValueOrDefault(job.Role) != host.HostId || closing) return;
            ChangeTurns.Turn? turn = null;
            try
            {
                // The install is done; the switch waits only for a change that is saving right now.
                turn = await changes.TakeAsync(lifetime.Token);
                if (pendingJobHosts.GetValueOrDefault(job.Role) != host.HostId || closing) return;
                await SaveJobHostAsync(job, host, route, pendingJobVoices.GetValueOrDefault(job.Role));
                RecordClusterJob(job.Job, new(host.HostId, false));
                pendingJobHosts.Remove(job.Role);
                pendingJobVoices.Remove(job.Role);
                turn.Dispose();
                ActionText.Text = $"{host.HostId} now handles {job.Job}.";
                // A host whose martlet-host predates one voice engine per computer still runs the replaced engine.
                if (job.Role == SetupRole.Tts && HostRoles.OtherVoiceEngines(check.Offers, job.HostRoleKind) is { Count: > 0 } leftovers)
                    ActionText.Text += await ReleaseVoiceEnginesAsync(host, leftovers);
                if (leaving is not null) ActionText.Text += await StopLeftVoiceEngineAsync(leaving);
            }
            catch (OperationCanceledException) { return; }
            catch (F5Exception error)
            {
                pendingJobHosts.Remove(job.Role);
                pendingJobVoices.Remove(job.Role);
                ActionText.Text = $"{host.HostId} is ready, but couldn't switch {job.Job}: {F5Voices.Describe(error)}";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
                ContractException or JsonException or ArgumentException)
            {
                pendingJobHosts.Remove(job.Role);
                pendingJobVoices.Remove(job.Role);
                ActionText.Text = $"{host.HostId} is ready, but couldn't switch {job.Job}: {error.Message}";
            }
            finally
            {
                turn?.Dispose();
                if (!closing)
                {
                    RenderHome();
                    RenderDevicePages();
                }
            }
            return;
        }
        if (pendingJobHosts.GetValueOrDefault(job.Role) == host.HostId)
        {
            pendingJobHosts.Remove(job.Role);
            pendingJobVoices.Remove(job.Role);
        }
    }

    /// <summary>Stops voice engines a host no longer needs because Speaking uses another engine there (the owner already
    /// agreed), one run window each, so their models leave its graphics card's memory; their downloads stay on the host.
    /// The engine that speaks reloads by itself if it couldn't load while they held the memory. One Speaking moved back to
    /// meanwhile is left running; while one is being stopped, Speaking waits for that before it moves to it
    /// (<see cref="releasingEngines"/>). Returns the outcome in words, starting with a space.</summary>
    private async Task<string> ReleaseVoiceEnginesAsync(PairedHost host, IReadOnlyList<HostRoleInfo> engines)
    {
        var stopped = new List<string>();
        var running = new List<string>();
        foreach (var engine in engines)
        {
            if (closing) return "";
            var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
            if (route is { RouteType: SetupRouteType.GatewayF5 } && route.Gateway?.HostId == host.HostId &&
                SpeechEngines.ForRoute(route.GatewaySnapshot?.RouteId)?.HostRoleKind == engine.Kind)
                continue;
            var key = (host.HostId, engine.Kind);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            releasingEngines[key] = done.Task;
            try { (await RunHostActionAsync(host, engine.Remove, confirmed: true) is null ? running : stopped).Add(engine.Kind); }
            finally
            {
                if (releasingEngines.TryGetValue(key, out var mine) && ReferenceEquals(mine, done.Task)) releasingEngines.Remove(key);
                done.TrySetResult();
            }
        }
        return (stopped.Count > 0 ? $" Stopped {HostRoles.Names(stopped)} on {host.HostId} to free its graphics card's memory." : "") +
            (running.Count > 0 ? $" {HostRoles.Names(running)} still {(running.Count == 1 ? "runs" : "run")} on {host.HostId} and " +
                $"{(running.Count == 1 ? "uses" : "use")} graphics card memory; remove {(running.Count == 1 ? "it" : "them")} on the Devices map." : "");
    }

    internal static GatewayRouteSnapshot Snapshot(HostRoute route, SetupRouteType routeType) => route.Snapshot(routeType);
}
