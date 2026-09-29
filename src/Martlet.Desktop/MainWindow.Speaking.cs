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

/// <summary>Who speaks: hands the voice (the TTS route) to a paired host's F5, cloning a reference voice from the local F5
/// preset store, or back to the voice route saved before (OpenAI or Windows speech). The host route is saved with the
/// existing gateway settings model, the applied reference voice and an explicit selection consent.</summary>
public partial class MainWindow
{
    /// <summary>The processing destination the f5 host role advertises (Martlet.Gateway.F5.F5RelayWorker); voice-rights
    /// confirmations name it, so a voice chosen before the role is installed stays valid once it runs.</summary>
    private const string F5Destination = "f5-host";
    private string? pendingSpeakingHost;
    private F5ReferenceSnapshot? pendingSpeakingVoice;

    private ComboBox SpeakingChoice()
    {
        var hosts = NetworkMap.Hosts(Inputs());
        var current = NetworkMap.SpeakingHost(homeSettings) is { } id ? "host:" + id : "saved";
        var choice = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(choice, "Who does the speaking");
        AutomationProperties.SetAutomationId(choice, "SpeakingOwner");
        void Option(string key, string text)
        {
            var item = new ComboBoxItem { Content = text, Tag = key };
            choice.Items.Add(item);
            if (key == current) choice.SelectedItem = item;
        }
        var tts = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
        var saved = tts is not null && tts.RouteType != SetupRouteType.GatewayF5 ? tts
            : store is null ? null : SpeakingSavedRoute.Load(store.DataDirectory);
        Option("saved", saved is null ? "Voice from Setup (choose in Setup)" : $"Voice from Setup: {NetworkMap.ProviderName(saved)}");
        foreach (var host in hosts)
        {
            var check = hostChecks.GetValueOrDefault(host.HostId);
            var model = check?.Offers?.GetValueOrDefault(HostRoles.F5);
            Option("host:" + host.HostId, host.HostId + (model is not null ? $" (F5 voice {model})"
                : check?.Reachable == true ? " (F5 not installed)" : check?.Reachable == false ? " (not reachable)" : ""));
        }
        choice.SelectionChanged += (_, _) =>
        {
            if (!renderingBoard && choice.SelectedItem is ComboBoxItem { Tag: string key } && key != current) _ = AssignSpeakingAsync(key);
        };
        return choice;
    }

    /// <summary>Hands speaking to a paired host ("host:ID") or back to the saved voice ("saved"). The owner chooses the
    /// reference voice first; a host without F5 can install it in the same step, and Martlet keeps its current voice until
    /// the host's model is ready.</summary>
    private async Task AssignSpeakingAsync(string key)
    {
        if (store is null || setupService is null || closing) return;
        if (assigningRole) { ActionText.Text = "Another role change is still finishing."; RenderMap(); return; }
        assigningRole = true;
        try
        {
            if (key == "saved")
            {
                await SpeakWithSavedVoiceAsync();
                return;
            }
            var host = FindHost(key[5..]) ?? throw new InvalidOperationException("That host is no longer paired.");
            ActionText.Text = $"Checking {host.HostId}...";
            var check = await HostControl.CheckAsync(host.Pairing, HardwareStore, lifetime.Token);
            hostChecks[host.HostId] = check;
            if (check.Reachable != true)
            {
                ActionText.Text = $"Speaking stays where it is: {host.HostId} did not answer ({check.Text})";
                return;
            }
            var route = check.Routes?.FirstOrDefault(r => r.RouteId == HostRoute.F5RouteId);
            if (F5VoiceDialog.Choose(this, store.DataDirectory, host.HostId, route?.DestinationId ?? F5Destination) is not { } voice)
                return;
            var disclosure = "Each reply's text and your reference recording with its transcript then go only to that computer, over " +
                "its pinned TLS gateway, instead of a cloud voice. There is no per-request charge. The F5 model is licensed for " +
                "non-commercial use (CC-BY-NC-4.0).";
            if (route is null)
            {
                var role = HostRoles.Get(HostRoles.F5);
                if (!ConfirmationDialog.Confirm(this,
                        $"{host.HostId} does not run F5 yet. Install it there and hand speaking to it once it is ready? " +
                        (host.Method is HostSetupMethod.SshDocker or HostSetupMethod.SshNative
                            ? $"Martlet installs it over SSH ({host.Reach}) and shows its progress. "
                            : host.CanLaunch ? $"A console opens ({host.Reach}) where you confirm each step. " : "Martlet copies the command to run on it. ") +
                        $"It needs {role.Needs}; the first install builds a large image and downloads the model. Until it is ready " +
                        $"Martlet keeps its current voice, then switches to '{voice.PresetName}' by itself. " + disclosure,
                        "Install and hand over"))
                    return;
                pendingSpeakingHost = host.HostId;
                pendingSpeakingVoice = voice;
                LaunchOnHost(host, role.Add);
                _ = WatchSpeakingHandoffAsync(host);
                return;
            }
            if (!ConfirmationDialog.Confirm(this,
                    $"Hand speaking to {host.HostId}? Its F5 model {route.ModelId} speaks your replies in the voice '{voice.PresetName}'. {disclosure}",
                    "Hand over speaking"))
                return;
            pendingSpeakingHost = null;
            await SaveSpeakingHostAsync(host, route, voice);
            ActionText.Text = $"Speaking is now handled by {host.HostId} (F5, voice '{voice.PresetName}'). An open conversation window picks it up on Reload.";
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

    /// <summary>Saves the TTS route as the host's gateway F5 route: pinned endpoint, this PC's pairing (whose secret stays
    /// where pairing saved it), the advertised route snapshot and the applied reference voice, enabled, with the selection
    /// recorded as the user's choice. The previous voice route is kept aside so speaking can go back to it.</summary>
    private async Task SaveSpeakingHostAsync(PairedHost host, HostRoute route, F5ReferenceSnapshot voice)
    {
        if (voice.Rights.ProcessingDestinationId != route.DestinationId)
            throw new InvalidOperationException($"{host.HostId}'s F5 voice runs under a different destination; choose the voice again.");
        var loaded = await setupService!.LoadAsync(lifetime.Token);
        if (loaded.Settings is not { Setup: not null } settings)
            throw new InvalidOperationException("Complete Setup once so Martlet can save who does the speaking.");
        var reference = await F5Voices.ApplyAsync(store!.DataDirectory, voice, lifetime.Token);
        var previous = settings.Setup.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
        var endpoint = new GatewayEndpointSettings
        {
            SchemaVersion = 1, Origin = host.Pairing.Origin, HostId = host.HostId,
            SpkiFingerprint = host.Pairing.SpkiFingerprint, DeviceRole = SelfHostSetup.GatewayRole
        };
        var next = SetupSettings.ConfigureGatewayEndpoint(settings, SetupRouteType.GatewayF5, endpoint, route.ModelId);
        var tts = next.Setup!.Routes.Single(r => r.Role == SetupRole.Tts);
        next = SetupSettings.ReplaceRoute(next, tts with
        {
            CredentialId = HostPairingCredential.ToGuid(host.Pairing.CredentialId, SetupRole.Tts), GatewayDeviceId = host.Pairing.DeviceId,
            Consent = null, ConfigurationRevision = Guid.NewGuid()
        });
        next = SetupSettings.ApplyGatewaySnapshot(next, SetupRole.Tts, Snapshot(route) with { RouteType = SetupRouteType.GatewayF5 });
        next = SetupSettings.ApplyF5Reference(next, reference);
        next = SetupSettings.SetRouteEnabled(next, SetupRole.Tts, enabled: true, recordSelection: true);
        if (previous is not null && previous.RouteType != SetupRouteType.GatewayF5)
            SpeakingSavedRoute.Save(store.DataDirectory, previous);
        var saved = await setupService.SaveAsync(next, loaded.Revision, lifetime.Token);
        if (!saved.Save.Saved) throw new InvalidOperationException(saved.Summary);
        homeSettings = next;
    }

    private async Task SpeakWithSavedVoiceAsync()
    {
        var loaded = await setupService!.LoadAsync(lifetime.Token);
        if (loaded.Settings is not { Setup: not null } settings) throw new InvalidOperationException("Complete Setup once first.");
        pendingSpeakingHost = null;
        pendingSpeakingVoice = null;
        if (NetworkMap.SpeakingHost(settings) is null)
        {
            ActionText.Text = "Speaking already uses the voice from Setup.";
            return;
        }
        if (SpeakingSavedRoute.Load(store!.DataDirectory) is not { } saved)
        {
            ActionText.Text = "Choose a voice in Setup.";
            RunNodeAction(NodeAction.Setup);
            return;
        }
        var local = saved.RouteType == SetupRouteType.LocalWindowsTts;
        if (!ConfirmationDialog.Confirm(this,
                $"Hand speaking back to {NetworkMap.ProviderName(saved)} ({saved.VoiceId ?? saved.ModelId}), as you chose in Setup? " +
                (local ? "Replies are spoken on this PC again." : "Reply text goes to that provider again, and requests may cost money there."),
                "Use that voice"))
            return;
        var next = SetupSettings.ReplaceRoute(settings, saved);
        var result = await setupService.SaveAsync(next, loaded.Revision, lifetime.Token);
        if (!result.Save.Saved) throw new InvalidOperationException(result.Summary);
        homeSettings = next;
        ActionText.Text = $"Speaking is back on {NetworkMap.ProviderName(saved)}. An open conversation window picks it up on Reload.";
    }

    /// <summary>After an F5 install started from a speaking handoff, checks the host until its route appears (up to an hour:
    /// the image build and model download can be slow) and then completes the handoff the user already confirmed.</summary>
    private async Task WatchSpeakingHandoffAsync(PairedHost host)
    {
        for (var attempt = 0; attempt < 180; attempt++)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(20), lifetime.Token); }
            catch (OperationCanceledException) { return; }
            if (pendingSpeakingHost != host.HostId || pendingSpeakingVoice is not { } voice || closing) return;
            HostCheck check;
            try { check = await HostControl.CheckAsync(host.Pairing, HardwareStore, lifetime.Token); }
            catch (OperationCanceledException) { return; }
            if (check.Routes?.FirstOrDefault(r => r.RouteId == HostRoute.F5RouteId) is not { } route) continue;
            hostChecks[host.HostId] = check;
            if (pendingSpeakingHost != host.HostId || closing) return;
            if (assigningRole) continue;
            assigningRole = true;
            try
            {
                await SaveSpeakingHostAsync(host, route, voice);
                ActionText.Text = $"{host.HostId} now runs F5 ({route.ModelId}) and speaks with the voice '{voice.PresetName}'.";
            }
            catch (OperationCanceledException) { return; }
            catch (F5Exception error)
            {
                ActionText.Text = $"{host.HostId} runs F5 now, but speaking could not be handed to it: {F5Voices.Describe(error)}";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
                ContractException or JsonException or ArgumentException)
            {
                ActionText.Text = $"{host.HostId} runs F5 now, but speaking could not be handed to it: {error.Message}";
            }
            finally
            {
                pendingSpeakingHost = null;
                pendingSpeakingVoice = null;
                assigningRole = false;
                if (!closing)
                {
                    RenderHome();
                    if (DevicesPage.IsVisible) RenderMap();
                }
            }
            return;
        }
        if (pendingSpeakingHost == host.HostId)
        {
            pendingSpeakingHost = null;
            pendingSpeakingVoice = null;
        }
    }
}

/// <summary>The voice route in use before speaking went to a host (OpenAI or Windows speech), kept verbatim (including its
/// recorded selection) in speaking-saved.json next to the other local preferences, so speaking can return to it.</summary>
internal static class SpeakingSavedRoute
{
    internal const string FileName = "speaking-saved.json";

    internal static void Save(string directory, SetupRoute route)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, FileName), ContractJson.Write(route, 65_536));
    }

    internal static SetupRoute? Load(string directory)
    {
        try { return ContractJson.Read<SetupRoute>(File.ReadAllBytes(Path.Combine(directory, FileName))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException) { return null; }
    }
}
