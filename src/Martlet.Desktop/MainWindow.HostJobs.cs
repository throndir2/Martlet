using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.F5;

namespace Martlet.Desktop;

/// <summary>A conversation job (a Setup role) that the Devices page can hand to any paired host running
/// <paramref name="HostRoleKind"/>, or back to the route it used before (kept in <paramref name="SavedFile"/>).</summary>
internal sealed record HostJob(SetupRole Role, SetupRouteType RouteType, string HostRoleKind, string RouteId, string Job,
    string Engine, string Use, string SavedFile, string Sent, string Disclosure)
{
    internal static readonly HostJob Thinking = new(SetupRole.Llm, SetupRouteType.GatewayOllama, HostRoles.Ollama,
        HostRoute.OllamaChatRouteId, "thinking", "conversation model", "answer your conversations", "thinking-cloud.json",
        "Your messages and recent conversation",
        "Your messages and recent conversation go to that computer instead of a cloud provider. There is no per-request charge.");

    internal static readonly HostJob Listening = new(SetupRole.Stt, SetupRouteType.GatewayStt, HostRoles.Stt,
        Audio2FaceHostConnection.TranscriptionRouteId, "listening", "speech recognition", "turn what you say into text", "listening-previous.json",
        "Your recorded speech",
        "Your speech goes to that computer for transcription instead of a cloud provider. It is not stored. There is no per-request charge.");

    internal static HostJob Speaking => SpeakingFor(SpeakingEngineChoice.Current);

    private static readonly HostJob F5Speaking = new(SetupRole.Tts, SetupRouteType.GatewayF5, HostRoles.F5,
        HostRoute.F5RouteId, "speaking", "voice service", "speak your replies", "speaking-previous.json",
        "Reply text",
        "Reply text and the selected voice sample go to that computer instead of a cloud voice. There is no per-request charge.");

    private static readonly HostJob XttsSpeaking = F5Speaking with
    {
        HostRoleKind = HostRoles.Xtts, RouteId = HostRoute.XttsRouteId,
        Disclosure = F5Speaking.Disclosure + " XTTS-v2's model (Coqui Public Model License) allows noncommercial use only."
    };

    /// <summary>The Speaking job done by <paramref name="engine"/>'s host role (F5-TTS or XTTS-v2).</summary>
    internal static HostJob SpeakingFor(SpeechEngine engine) => engine.Key == SpeechEngines.Xtts.Key ? XttsSpeaking : F5Speaking;

    internal static IReadOnlyList<HostJob> All => [Thinking, Listening, Speaking];

    internal static HostJob? For(SetupRole role) => All.FirstOrDefault(job => job.Role == role);

    internal string Title => char.ToUpperInvariant(Job[0]) + Job[1..];
}

/// <summary>Which self-hosted voice engine (<see cref="SpeechEngines"/>) does the Speaking job, kept in speaking-engine.txt
/// next to the other local preferences. A speaking route already saved on a host's engine wins over the file, so the choice
/// always matches what speaks.</summary>
internal static class SpeakingEngineChoice
{
    internal const string FileName = "speaking-engine.txt";

    internal static SpeechEngine Current { get; private set; } = SpeechEngines.F5;

    /// <summary>Reads the saved choice, then follows the engine the TTS route uses when Speaking is on a host.</summary>
    internal static void Sync(string directory, AppSettings? settings)
    {
        try { Current = SpeechEngines.ForKey(File.ReadAllText(Path.Combine(directory, FileName)).Trim()) ?? SpeechEngines.F5; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Current = SpeechEngines.F5; }
        var route = settings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
        if (route is { RouteType: SetupRouteType.GatewayF5, GatewaySnapshot: { } snapshot } &&
            SpeechEngines.ForRoute(snapshot.RouteId) is { } used && used != Current)
            Save(directory, used);
    }

    internal static void Save(string directory, SpeechEngine engine)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, FileName), engine.Key);
        Current = engine;
    }
}

/// <summary>Who does each job: hands a role's route to a paired host's gateway route, or back to the route chosen in Setup.
/// The host route is saved with the existing gateway settings model, this PC's pairing and an explicit selection consent.</summary>
public partial class MainWindow
{
    /// <summary>The processing destination the f5 host role advertises (Martlet.Gateway.F5.F5RelayWorker); voice-rights
    /// confirmations name it, so a voice chosen before the role is installed stays valid once it runs.</summary>
    private const string F5Destination = "f5-host";
    private readonly Dictionary<SetupRole, string> pendingJobHosts = [];
    private readonly Dictionary<SetupRole, F5ReferenceSnapshot> pendingJobVoices = [];

    private Task AssignThinkingAsync(string key) => AssignJobAsync(HostJob.Thinking, key);

    private ComboBox JobChoice(HostJob job)
    {
        var hosts = NetworkMap.Hosts(Inputs());
        var current = NetworkMap.JobHost(homeSettings, job.Role) is { } id ? "host:" + id : "saved";
        var choice = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(choice, $"Who handles {job.Job}");
        AutomationProperties.SetAutomationId(choice, job.Title + "Owner");
        void Option(string key, string text, string? blocked = null)
        {
            var item = new ComboBoxItem { Content = text, Tag = key };
            if (blocked is not null)
            {
                item.IsEnabled = false;
                item.ToolTip = blocked;
                ToolTipService.SetShowOnDisabled(item, true);
                AutomationProperties.SetHelpText(item, blocked);
            }
            choice.Items.Add(item);
            if (key == current) choice.SelectedItem = item;
        }
        var saved = SavedRoute(job);
        Option("saved", saved is null ? "Choose in Setup" : SavedName(saved));
        foreach (var host in hosts)
        {
            var check = hostChecks.GetValueOrDefault(host.HostId);
            var model = check?.Offers?.GetValueOrDefault(job.HostRoleKind);
            // A host that cannot run the engine at all (no NVIDIA GPU for F5, an iPhone for Ollama...) or whose roles are
            // switched on on the device itself is shown, not offered.
            if (model is null && CannotHand(host.HostId, job.HostRoleKind, job.Job) is { } cannot)
            {
                Option("host:" + host.HostId, $"{host.HostId} (not available)", cannot);
                continue;
            }
            Option("host:" + host.HostId, host.HostId + (model is not null ? " (ready)"
                : check?.Reachable == true ? " (not installed)" : check?.Reachable == false ? " (not reachable)" : ""));
        }
        choice.SelectionChanged += (_, _) =>
        {
            if (!renderingBoard && choice.SelectedItem is ComboBoxItem { Tag: string key } && key != current) AssignJobAsync(job, key).Forget();
        };
        return choice;
    }

    /// <summary>The route this job uses when no host does it: the current one, or the one kept aside while a host does it.</summary>
    private SetupRoute? SavedRoute(HostJob job)
    {
        var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == job.Role);
        return route is not null && !SelfHostSetup.IsGateway(route.RouteType) ? route
            : store is null ? null : JobSavedRoute.Load(store.DataDirectory, job.SavedFile);
    }

    private static bool IsCloud(SetupRoute route) => route.RouteType is null or SetupRouteType.OpenAi or SetupRouteType.ChatCompletions;

    private static string SavedName(SetupRoute route) => (IsCloud(route) ? "Cloud: " : "") + NetworkMap.ProviderName(route);

    /// <summary>Hands the job to a paired host ("host:ID") or back to its Setup choice ("saved"). A host without the role can
    /// install it in the same step; Martlet keeps the job where it is until the host's role is ready.</summary>
    private async Task AssignJobAsync(HostJob job, string key)
    {
        if (store is null || setupService is null || closing) return;
        if (assigningRole) { ActionText.Text = "Another change is still finishing."; RenderMap(); return; }
        assigningRole = true;
        try
        {
            if (key == "saved")
            {
                await JobBackAsync(job);
                return;
            }
            var host = FindHost(key[5..]) ?? throw new InvalidOperationException("That host is no longer paired.");
            ActionText.Text = $"Checking {host.HostId}...";
            var check = await HostControl.CheckAsync(host.Pairing, HardwareStore, lifetime.Token);
            hostChecks[host.HostId] = check;
            if (check.Reachable != true)
            {
                ActionText.Text = $"{job.Title} wasn't moved. {host.HostId} didn't respond ({check.Text}).";
                return;
            }
            var route = check.Routes?.FirstOrDefault(r => r.RouteId == job.RouteId);
            if (route is null && CannotHand(host.HostId, job.HostRoleKind, job.Job) is { } cannot)
            {
                ActionText.Text = $"{job.Title} wasn't moved. {cannot}";
                return;
            }
            // F5 copies a reference voice. Handing Speaking to a host keeps the voice already chosen for it, else Martlet's first
            // included voice; Companion › Voice › Voices adds voices and switches between them.
            F5ReferenceSnapshot? voice = null;
            if (job.RouteType == SetupRouteType.GatewayF5)
                voice = await F5Voices.DefaultAsync(store.DataDirectory, route?.DestinationId ?? F5Destination, lifetime.Token);
            var withVoice = voice is null ? "" : $" using \"{voice.PresetName}\"";
            var changeVoice = voice is null ? "" : " You can change voices in Companion > Voice.";
            if (route is null)
            {
                var role = HostRoles.Get(job.HostRoleKind);
                // On this PC Martlet already knows what suits it, so the install asks nothing more: whisper goes on the
                // graphics card or the processor by what already runs there, F5 has nothing to choose.
                IReadOnlyDictionary<string, string>? answers = null;
                var how = host.Method is HostSetupMethod.SshDocker or HostSetupMethod.SshNative
                    ? "Martlet will install it on that computer and show progress. "
                    : host.Method == HostSetupMethod.Agent ? $"Martlet on {host.HostId} will install it and show progress here. "
                    : host.CanLaunch ? "Martlet will install it in this PC's host service and show progress. "
                    : "Set how Martlet reaches that computer on the Devices map first. ";
                if (host.Method == HostSetupMethod.ThisPcDocker && job.Role == SetupRole.Stt)
                {
                    var advice = await ListeningAdviceAsync();
                    answers = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["choice.accelerator"] = advice.UseGpu ? "gpu" : "cpu", ["choice.STT_MODEL"] = advice.Model
                    };
                    how = "Martlet will install it in this PC's host service with the recommended settings and show progress. ";
                }
                else if (host.Method == HostSetupMethod.ThisPcDocker && job.RouteType == SetupRouteType.GatewayF5)
                {
                    answers = new Dictionary<string, string>(StringComparer.Ordinal);
                    how = "Martlet will install it in this PC's host service and show progress. ";
                }
                if (!ConfirmationDialog.Confirm(this,
                        $"{host.HostId} isn't ready for {job.Job}. Install the needed role there and switch {job.Job} to it when it's ready? " + how +
                        $"It needs {role.Needs}." +
                        (HostCan(host.HostId, job.HostRoleKind) is { Verdict: Martlet.Core.Platforms.PlatformVerdict.Unknown } unsure ? " " + unsure.Reason : "") +
                        $" Martlet keeps {job.Job} where it is until the role is ready, then switches over{withVoice}." +
                        changeVoice + " " + job.Disclosure + HostCaveats(host.HostId), "Install and switch"))
                    return;
                pendingJobHosts[job.Role] = host.HostId;
                if (voice is not null) pendingJobVoices[job.Role] = voice;
                LaunchOnHost(host, role.Add, answers);
                WatchJobHandoffAsync(job, host).Forget();
                return;
            }
            if (!ConfirmationDialog.Confirm(this,
                    $"Use {host.HostId} for {job.Job}? It will {job.Use}{withVoice}.{changeVoice} {job.Disclosure}" +
                    HostCaveats(host.HostId),
                    $"Use {host.HostId}"))
                return;
            pendingJobHosts.Remove(job.Role);
            await SaveJobHostAsync(job, host, route, voice);
            RecordClusterJob(job.Job, new(host.HostId, false));
            ActionText.Text = $"{job.Title} now uses {host.HostId}{withVoice}. Reload any open conversation to use it.";
        }
        catch (OperationCanceledException) { }
        catch (F5Exception error) { ActionText.Text = F5Voices.Describe(error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or
            JsonException or ArgumentException or Audio2FaceHostException)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            assigningRole = false;
            if (!closing)
            {
                RenderHome();
                if (DevicesPage.IsVisible) RenderMap();
            }
        }
    }

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
    }

    private async Task JobBackAsync(HostJob job)
    {
        var loaded = await setupService!.LoadAsync(lifetime.Token);
        if (loaded.Settings is not { Setup: not null } settings) throw new InvalidOperationException("Complete Setup first.");
        pendingJobHosts.Remove(job.Role);
        pendingJobVoices.Remove(job.Role);
        if (NetworkMap.JobHost(settings, job.Role) is null)
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
        if (!ConfirmationDialog.Confirm(this,
                $"Use {name} for {job.Job} again? " +
                (IsCloud(saved) ? $"{job.Sent} go to that provider again, and requests may cost money." : "It runs on this PC again."),
                "Use the Setup choice"))
            return;
        await HandBackAsync(job, saved);
    }

    /// <summary>Puts the job back on the route kept aside while a host did it, already confirmed by the caller, and records
    /// it in the shared plan (each computer uses its own choice).</summary>
    private async Task HandBackAsync(HostJob job, SetupRoute saved)
    {
        var loaded = await setupService!.LoadAsync(lifetime.Token);
        if (loaded.Settings is not { Setup: not null } settings) throw new InvalidOperationException("Complete Setup first.");
        pendingJobHosts.Remove(job.Role);
        pendingJobVoices.Remove(job.Role);
        var name = NetworkMap.ProviderName(saved);
        var next = HostHandoff.Back(settings, saved);
        var result = await setupService.SaveAsync(next, loaded.Revision, lifetime.Token);
        if (!result.Save.Saved) throw new InvalidOperationException(result.Summary);
        homeSettings = next;
        RecordClusterJob(job.Job, new(null, false));
        ActionText.Text = $"{job.Title} now uses {name}. Reload any open conversation to use it." +
            (saved.CredentialId is not null && next.Setup!.Routes.First(r => r.Role == job.Role).CredentialId is null
                ? " Its key is missing. Save it again in Setup." : "");
    }

    /// <summary>After an install started from a handoff, checks the host until its route appears (up to an hour, as model
    /// downloads can be slow) and then completes the handoff the user already confirmed.</summary>
    private async Task WatchJobHandoffAsync(HostJob job, PairedHost host)
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
            if (assigningRole) continue;
            assigningRole = true;
            try
            {
                await SaveJobHostAsync(job, host, route, pendingJobVoices.GetValueOrDefault(job.Role));
                RecordClusterJob(job.Job, new(host.HostId, false));
                pendingJobHosts.Remove(job.Role);
                pendingJobVoices.Remove(job.Role);
                ActionText.Text = $"{host.HostId} now handles {job.Job}.";
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
                assigningRole = false;
                if (!closing)
                {
                    RenderHome();
                    if (DevicesPage.IsVisible) RenderMap();
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

    internal static GatewayRouteSnapshot Snapshot(HostRoute route, SetupRouteType routeType) => new()
    {
        SchemaVersion = 1, RouteType = routeType, RegistryId = SelfHostSetup.RegistryId,
        RegistryVersion = SelfHostSetup.RegistryVersion, RouteId = route.RouteId, Path = route.Path, ContractId = route.ContractId,
        ContractVersion = route.ContractVersion, DestinationId = route.DestinationId, WorkerId = route.WorkerId,
        WorkerPackageRevision = route.AdapterVersion, AdapterVersion = route.AdapterVersion, ModelId = route.ModelId,
        ModelRevision = route.ModelRevision, ModelSha256 = route.ModelSha256, ArtifactIdentitySha256 = route.ArtifactIdentitySha256,
        MaximumRequestBytes = route.MaximumRequestBytes, MaximumInputBytes = route.MaximumInputBytes,
        MaximumOutputBytes = route.MaximumOutputBytes, MaximumEventBytes = route.MaximumEventBytes, MaximumEvents = route.MaximumEvents,
        MaximumStreamBytes = route.MaximumStreamBytes, MaximumDurationSeconds = (int)Math.Ceiling(route.MaximumDuration.TotalSeconds),
        Cancellation = route.Cancellation switch
        {
            "discard_only" => GatewayCancellationMode.DiscardOnly,
            "cooperative_compute_cancel" => GatewayCancellationMode.CooperativeComputeCancel,
            _ => GatewayCancellationMode.RequestAbort
        },
        ObservedAtUtc = DateTimeOffset.UtcNow, ProbeRevision = Guid.NewGuid()
    };
}

/// <summary>The route a job used before it went to a host, kept verbatim (including its recorded selection) next to the
/// other local preferences (for example thinking-cloud.json), so the job can return to it without re-entering a key.</summary>
internal static class JobSavedRoute
{
    internal static void Save(string directory, string fileName, SetupRoute route)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, fileName), ContractJson.Write(route, 65_536));
    }

    internal static SetupRoute? Load(string directory, string fileName)
    {
        try { return ContractJson.Read<SetupRoute>(File.ReadAllBytes(Path.Combine(directory, fileName))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException) { return null; }
    }
}
