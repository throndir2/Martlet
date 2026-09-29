using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Who thinks: hands the conversation model (the LLM route) to a paired host's Ollama, or back to the cloud route
/// chosen in Setup. The host route is saved with the existing gateway settings model and an explicit selection consent.</summary>
public partial class MainWindow
{
    private string? pendingThinkingHost;

    private ComboBox ThinkingChoice()
    {
        var hosts = NetworkMap.Hosts(Inputs());
        var current = NetworkMap.ThinkingHost(homeSettings) is { } id ? "host:" + id : "cloud";
        var choice = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(choice, "Who does the thinking");
        AutomationProperties.SetAutomationId(choice, "ThinkingOwner");
        void Option(string key, string text)
        {
            var item = new ComboBoxItem { Content = text, Tag = key };
            choice.Items.Add(item);
            if (key == current) choice.SelectedItem = item;
        }
        var llm = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var cloud = llm is not null && llm.RouteType != SetupRouteType.GatewayOllama ? llm
            : store is null ? null : ThinkingCloudRoute.Load(store.DataDirectory);
        Option("cloud", cloud is null ? "Cloud (choose in Setup)" : $"Cloud: {NetworkMap.ProviderName(cloud)}");
        foreach (var host in hosts)
        {
            var check = hostChecks.GetValueOrDefault(host.HostId);
            var model = check?.Offers?.GetValueOrDefault(HostRoles.Ollama);
            Option("host:" + host.HostId, host.HostId + (model is not null ? $" (Ollama {model})"
                : check?.Reachable == true ? " (Ollama not installed)" : check?.Reachable == false ? " (not reachable)" : ""));
        }
        choice.SelectionChanged += (_, _) =>
        {
            if (!renderingBoard && choice.SelectedItem is ComboBoxItem { Tag: string key } && key != current) _ = AssignThinkingAsync(key);
        };
        return choice;
    }

    /// <summary>Hands thinking to a paired host ("host:ID") or back to the cloud ("cloud"). A host without Ollama can
    /// install it in the same step; Martlet keeps thinking where it does until the host's model is ready.</summary>
    private async Task AssignThinkingAsync(string key)
    {
        if (store is null || setupService is null || closing) return;
        if (assigningRole) { ActionText.Text = "Another role change is still finishing."; RenderMap(); return; }
        assigningRole = true;
        try
        {
            if (key == "cloud")
            {
                await ThinkInCloudAsync();
                return;
            }
            var host = FindHost(key[5..]) ?? throw new InvalidOperationException("That host is no longer paired.");
            ActionText.Text = $"Checking {host.HostId}...";
            var check = await HostControl.CheckAsync(host.Pairing, HardwareStore, lifetime.Token);
            hostChecks[host.HostId] = check;
            if (check.Reachable != true)
            {
                ActionText.Text = $"Thinking stays where it is: {host.HostId} did not answer ({check.Text})";
                return;
            }
            var disclosure = "Your messages, recent conversation, persona and any memory facts you allow then go only to that computer, " +
                "over its pinned TLS gateway, instead of a cloud provider. There is no per-request charge.";
            if (check.Routes?.FirstOrDefault(r => r.RouteId == HostRoute.OllamaChatRouteId) is not { } route)
            {
                var role = HostRoles.Get(HostRoles.Ollama);
                if (!ConfirmationDialog.Confirm(this,
                        $"{host.HostId} does not run Ollama yet. Install it there and hand thinking to it once it is ready? " +
                        (host.Method is HostSetupMethod.SshDocker or HostSetupMethod.SshNative
                            ? $"Martlet installs it over SSH ({host.Reach}), asks which model and shows its progress. "
                            : host.CanLaunch ? $"A console opens ({host.Reach}) where you confirm each step and pick the model. " : "Martlet copies the command to run on it. ") +
                        $"It needs {role.Needs}. Until the model is downloaded Martlet keeps thinking where it does now, then switches over by itself. " +
                        disclosure, "Install and hand over"))
                    return;
                pendingThinkingHost = host.HostId;
                LaunchOnHost(host, role.Add);
                _ = WatchThinkingHandoffAsync(host);
                return;
            }
            if (!ConfirmationDialog.Confirm(this,
                    $"Hand thinking to {host.HostId}? Its Ollama model {route.ModelId} answers your conversations. {disclosure}",
                    "Hand over thinking"))
                return;
            pendingThinkingHost = null;
            await SaveThinkingHostAsync(host, route);
            ActionText.Text = $"Thinking is now handled by {host.HostId} (Ollama {route.ModelId}). An open conversation window picks it up on Reload.";
        }
        catch (OperationCanceledException) { }
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

    /// <summary>Saves the LLM route as the host's gateway Ollama route: pinned endpoint, this PC's pairing (whose secret stays
    /// where pairing saved it), the advertised route snapshot, enabled, with the selection recorded as the user's choice.
    /// The previous cloud route is kept aside so thinking can go back to it without re-entering its key.</summary>
    private async Task SaveThinkingHostAsync(PairedHost host, HostRoute route)
    {
        var loaded = await setupService!.LoadAsync(lifetime.Token);
        if (loaded.Settings is not { Setup: not null } settings)
            throw new InvalidOperationException("Complete Setup once so Martlet can save who does the thinking.");
        var previous = settings.Setup.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var endpoint = new GatewayEndpointSettings
        {
            SchemaVersion = 1, Origin = host.Pairing.Origin, HostId = host.HostId,
            SpkiFingerprint = host.Pairing.SpkiFingerprint, DeviceRole = SelfHostSetup.GatewayRole
        };
        var next = SetupSettings.ConfigureGatewayEndpoint(settings, SetupRouteType.GatewayOllama, endpoint, route.ModelId);
        var llm = next.Setup!.Routes.Single(r => r.Role == SetupRole.Llm);
        next = SetupSettings.ReplaceRoute(next, llm with
        {
            CredentialId = HostPairingCredential.ToGuid(host.Pairing.CredentialId), GatewayDeviceId = host.Pairing.DeviceId,
            Consent = null, ConfigurationRevision = Guid.NewGuid()
        });
        next = SetupSettings.ApplyGatewaySnapshot(next, SetupRole.Llm, Snapshot(route));
        next = SetupSettings.SetRouteEnabled(next, SetupRole.Llm, enabled: true, recordSelection: true);
        if (previous is not null && previous.RouteType != SetupRouteType.GatewayOllama)
            ThinkingCloudRoute.Save(store!.DataDirectory, previous);
        var saved = await setupService.SaveAsync(next, loaded.Revision, lifetime.Token);
        if (!saved.Save.Saved) throw new InvalidOperationException(saved.Summary);
        homeSettings = next;
    }

    private async Task ThinkInCloudAsync()
    {
        var loaded = await setupService!.LoadAsync(lifetime.Token);
        if (loaded.Settings is not { Setup: not null } settings) throw new InvalidOperationException("Complete Setup once first.");
        pendingThinkingHost = null;
        if (NetworkMap.ThinkingHost(settings) is null)
        {
            ActionText.Text = "Thinking already uses the cloud choice from Setup.";
            return;
        }
        if (ThinkingCloudRoute.Load(store!.DataDirectory) is not { } cloud)
        {
            ActionText.Text = "Choose a cloud model for thinking in Setup.";
            RunNodeAction(NodeAction.Setup);
            return;
        }
        if (!ConfirmationDialog.Confirm(this,
                $"Hand thinking back to {NetworkMap.ProviderName(cloud)} (model {cloud.ModelId}), as you chose in Setup? Your messages and " +
                "recent conversation go to that provider again, and requests may cost money there.", "Use the cloud"))
            return;
        var next = SetupSettings.ReplaceRoute(settings, cloud);
        var saved = await setupService.SaveAsync(next, loaded.Revision, lifetime.Token);
        if (!saved.Save.Saved) throw new InvalidOperationException(saved.Summary);
        homeSettings = next;
        ActionText.Text = $"Thinking is back on {NetworkMap.ProviderName(cloud)}. An open conversation window picks it up on Reload.";
    }

    /// <summary>After an Ollama install started from a thinking handoff, checks the host until its route appears (up to an
    /// hour, as model downloads can be slow) and then completes the handoff the user already confirmed.</summary>
    private async Task WatchThinkingHandoffAsync(PairedHost host)
    {
        for (var attempt = 0; attempt < 180; attempt++)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(20), lifetime.Token); }
            catch (OperationCanceledException) { return; }
            if (pendingThinkingHost != host.HostId || closing) return;
            HostCheck check;
            try { check = await HostControl.CheckAsync(host.Pairing, HardwareStore, lifetime.Token); }
            catch (OperationCanceledException) { return; }
            if (check.Routes?.FirstOrDefault(r => r.RouteId == HostRoute.OllamaChatRouteId) is not { } route) continue;
            hostChecks[host.HostId] = check;
            if (pendingThinkingHost != host.HostId || closing) return;
            if (assigningRole) continue;
            assigningRole = true;
            try
            {
                await SaveThinkingHostAsync(host, route);
                pendingThinkingHost = null;
                ActionText.Text = $"{host.HostId} now runs Ollama ({route.ModelId}) and handles thinking.";
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
                ContractException or JsonException or ArgumentException)
            {
                pendingThinkingHost = null;
                ActionText.Text = $"{host.HostId} runs Ollama now, but thinking could not be handed to it: {error.Message}";
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
        if (pendingThinkingHost == host.HostId) pendingThinkingHost = null;
    }

    internal static GatewayRouteSnapshot Snapshot(HostRoute route) => new()
    {
        SchemaVersion = 1, RouteType = SetupRouteType.GatewayOllama, RegistryId = SelfHostSetup.RegistryId,
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

/// <summary>The cloud LLM route in use before thinking went to a host, kept verbatim (including its recorded selection) in
/// thinking-cloud.json next to the other local preferences, so thinking can return to it without re-entering a key.</summary>
internal static class ThinkingCloudRoute
{
    internal const string FileName = "thinking-cloud.json";

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
