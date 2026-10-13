using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

// The changes the setup pages save. Each one takes valid settings and returns new valid settings.
public sealed partial record SetupSettings
{
    public static AppSettings Begin(AppSettings? prior) =>
        AppSettings.UpgradeToCurrent(prior);

    public static AppSettings SelectRoute(AppSettings settings, SetupRole role, string modelId, string? voiceId)
    {
        settings.Validate();
        var setup = settings.Setup ?? throw new ContractException(ErrorCode.InvalidContract,
            "Open setup before selecting a route.");
        var old = setup.Routes.SingleOrDefault(r => r.Role == role);
        var route = new SetupRoute
        {
            RouteSchemaVersion = 1,
            RouteType = SetupRouteType.OpenAi,
            Enabled = true,
            Role = role,
            ProviderAlias = OpenAiSetup.Alias(role),
            Origin = OpenAiSetup.Origin,
            ModelId = modelId,
            VoiceId = voiceId,
            CredentialId = old?.RouteType == SetupRouteType.OpenAi ? old.CredentialId : null,
            ConfigurationRevision = Guid.NewGuid()
        };
        route.Validate();
        if (old is not null && old.RouteType == SetupRouteType.OpenAi &&
            old.ModelId == modelId && old.VoiceId == voiceId)
            route = old;
        return ReplaceRoute(settings, route);
    }

    public static AppSettings ConfigureGatewayEndpoint(AppSettings settings, SetupRouteType routeType,
        GatewayEndpointSettings endpoint, string expectedModelId)
    {
        settings.Validate();
        ContractRules.Require(SelfHostSetup.IsGateway(routeType),
            "Choose the named Ollama, F5 or speech-to-text gateway route.");
        endpoint.Validate();
        OpenAiSetup.UpstreamId(expectedModelId);
        var role = SelfHostSetup.Gateway(routeType).Role;
        var old = settings.Setup!.Routes.SingleOrDefault(item => item.Role == role);
        var sameScope = old?.RouteType == routeType && old.Gateway == endpoint;
        var route = new SetupRoute
        {
            RouteSchemaVersion = 1,
            RouteType = routeType,
            Enabled = false,
            Role = role,
            ProviderAlias = SelfHostSetup.Gateway(routeType).Alias,
            Origin = endpoint.Origin,
            ModelId = expectedModelId,
            CredentialId = sameScope ? old!.CredentialId : null,
            ConfigurationRevision = Guid.NewGuid(),
            Gateway = endpoint,
            GatewayDeviceId = sameScope ? old!.GatewayDeviceId : null,
            GatewaySnapshot = sameScope && old!.GatewaySnapshot?.ModelId == expectedModelId
                ? old.GatewaySnapshot
                : null,
            Reference = sameScope && routeType == SetupRouteType.GatewayF5
                ? old!.Reference
                : null
        };
        route.Validate();
        if (old is not null &&
            (old with { Consent = null, ConfigurationRevision = Guid.Empty }) ==
            (route with { Consent = null, ConfigurationRevision = Guid.Empty }))
            route = old;
        return ReplaceRoute(settings, route);
    }

    public static AppSettings ApplyGatewaySnapshot(AppSettings settings, SetupRole role,
        GatewayRouteSnapshot snapshot)
    {
        settings.Validate();
        snapshot.Validate();
        var route = settings.Setup!.Routes.SingleOrDefault(item => item.Role == role) ??
            throw new ContractException(ErrorCode.InvalidContract, "Configure the pinned gateway endpoint before probing it.");
        ContractRules.Require(route.RouteType == snapshot.RouteType &&
            SelfHostSetup.IsGateway(route.RouteType),
            "The probe result does not match the selected gateway route.");
        return ReplaceRoute(settings, route with
        {
            Enabled = false,
            ModelId = snapshot.ModelId,
            GatewaySnapshot = snapshot,
            Reference = route.Reference?.ProcessingDestinationId == snapshot.DestinationId
                ? route.Reference
                : null,
            Consent = null,
            ConfigurationRevision = Guid.NewGuid()
        });
    }

    /// <summary>Switches the F5 route to another applied reference voice, keeping its host and enabled state. A route whose
    /// selection was confirmed stays confirmed with the new voice, which the owner chose explicitly.</summary>
    public static AppSettings ApplyF5Reference(AppSettings settings, F5ReferenceSettings reference)
    {
        settings.Validate();
        reference.Validate();
        var route = settings.Setup!.Routes.SingleOrDefault(item => item.Role == SetupRole.Tts) ??
            throw new ContractException(ErrorCode.InvalidContract, "Configure and probe the F5 gateway route first.");
        ContractRules.Require(route.RouteType == SetupRouteType.GatewayF5 &&
            route.GatewaySnapshot?.DestinationId == reference.ProcessingDestinationId,
            "The applied F5 reference does not match the probed processing destination.");
        var changed = route with
        {
            Reference = reference,
            Consent = null,
            ConfigurationRevision = Guid.NewGuid()
        };
        return ReplaceRoute(settings, changed with { Consent = route.Consent is null ? null : changed.Selection() });
    }

    public static AppSettings ConfigureLocalStt(AppSettings settings, LocalSttPackageSettings package)
    {
        settings.Validate();
        package.Validate();
        var old = settings.Setup!.Routes.SingleOrDefault(item => item.Role == SetupRole.Stt);
        var route = new SetupRoute
        {
            RouteSchemaVersion = 1,
            RouteType = SetupRouteType.LocalWhisper,
            Enabled = false,
            Role = SetupRole.Stt,
            ProviderAlias = SelfHostSetup.LocalWhisperAlias,
            Origin = SelfHostSetup.LocalOrigin,
            ModelId = package.ModelId,
            ConfigurationRevision = Guid.NewGuid(),
            LocalStt = package
        };
        if (old is not null &&
            (old with { Consent = null, ConfigurationRevision = Guid.Empty }) ==
            (route with { Consent = null, ConfigurationRevision = Guid.Empty }))
            route = old;
        return ReplaceRoute(settings, route);
    }

    public static AppSettings SetRouteEnabled(AppSettings settings, SetupRole role, bool enabled,
        bool recordSelection)
    {
        settings.Validate();
        var route = settings.Setup!.Routes.SingleOrDefault(item => item.Role == role) ??
            throw new ContractException(ErrorCode.InvalidContract, "Select a route before changing its enablement.");
        if (route.Enabled == enabled && (recordSelection ? route.Consent == route.Selection() : route.Consent is null))
            return settings;
        var changed = route with
        {
            Enabled = enabled,
            Consent = null,
            ConfigurationRevision = Guid.NewGuid()
        };
        changed.Validate();
        if (recordSelection)
            changed = changed with { Consent = changed.Selection() };
        changed.Validate();
        return ReplaceRoute(settings, changed);
    }

    public static AppSettings ReconnectRetainedGateway(AppSettings settings, Guid credentialId)
    {
        settings.Validate();
        var retained = (settings.Setup?.RetainedGatewayCredentials ?? [])
            .SingleOrDefault(item => item.CredentialId == credentialId) ??
            throw new ContractException(ErrorCode.InvalidContract, "Select a current retained pairing.");
        var route = settings.Setup!.Routes.SingleOrDefault(item => item.Role == retained.Role);
        ContractRules.Require(route is not null && route.CredentialId is null &&
            route.RouteType == retained.Scope.RouteType && route.Origin == retained.Scope.Origin &&
            route.Gateway?.HostId == retained.Scope.HostId &&
            route.Gateway?.SpkiFingerprint == retained.Scope.SpkiFingerprint &&
            route.Gateway?.DeviceRole == retained.Scope.DeviceRole,
            "Saved pairing retained; route not connected/mismatched. Select the exact current binding.");
        var updated = settings with { Setup = settings.Setup with
        {
            RetainedGatewayCredentials = settings.Setup.RetainedGatewayCredentials!
                .Where(item => item != retained).ToArray()
        } };
        return ReplaceRoute(updated, route!.DisableForRestore() with
        {
            CredentialId = retained.CredentialId, GatewayDeviceId = retained.Scope.DeviceId
        });
    }

    public static AppSettings DetachRetainedGateway(AppSettings settings, Guid credentialId)
    {
        settings.Validate();
        var retained = (settings.Setup?.RetainedGatewayCredentials ?? [])
            .SingleOrDefault(item => item.CredentialId == credentialId) ??
            throw new ContractException(ErrorCode.InvalidContract, "Select a current retained pairing.");
        var updated = settings with { Setup = settings.Setup! with
        {
            RetainedGatewayCredentials = settings.Setup!.RetainedGatewayCredentials!
                .Where(item => item != retained).ToArray(),
            PendingRemovals = settings.Setup.PendingRemovals.Append(new()
            {
                Role = retained.Role, CredentialId = retained.CredentialId, Scope = retained.Scope
            }).ToArray()
        } };
        updated.Validate();
        return updated;
    }

    public static AppSettings ReplaceRoute(AppSettings settings, SetupRoute route)
    {
        route.Validate();
        var old = settings.Setup!.Routes.SingleOrDefault(item => item.Role == route.Role);
        if (old is not null && settings.Setup.PendingRemovals.Any(item => item.Role == route.Role && SelfHostSetup.IsGateway(item.Scope?.RouteType)))
            ContractRules.Require(old.CredentialScope() == route.CredentialScope(),
                "Remove this job's old pairing key under Keys from before on its Companion page before changing its computer or route.");
        var updated = settings with
        {
            Setup = settings.Setup! with
            {
                Routes = settings.Setup!.Routes.Where(r => r.Role != route.Role)
                    .Append(route).OrderBy(r => r.Role).ToArray()
            }
        };
        updated.Validate();
        return updated;
    }

    // A destination switch must not orphan the previous owned key; list it for explicit removal instead.
    public static AppSettings QueueReplacedCredential(AppSettings settings, SetupRoute? previous)
    {
        settings.Validate();
        if (previous?.CredentialId is not { } id ||
            previous.RouteType is not (null or SetupRouteType.OpenAi or SetupRouteType.ChatCompletions or SetupRouteType.ElevenLabs))
            return settings;
        var setup = settings.Setup!;
        if (setup.Routes.Any(route => route.CredentialId == id) ||
            setup.PendingRemovals.Any(removal => removal.CredentialId == id))
            return settings;
        var updated = settings with
        {
            Setup = setup with
            {
                PendingRemovals = setup.PendingRemovals.Append(new()
                {
                    Role = previous.Role,
                    CredentialId = id,
                    Scope = previous.RouteType is SetupRouteType.ChatCompletions or SetupRouteType.ElevenLabs
                        ? CredentialScopeSettings.From(previous)
                        : null
                }).ToArray()
            }
        };
        updated.Validate();
        return updated;
    }

    /// <summary>Keys set aside (listed for removal) when <paramref name="role"/> left a destination, newest first, that
    /// fit that exact destination again: OpenAI when <paramref name="chatBaseUrl"/> is null, otherwise that Chat Completions
    /// base URL.</summary>
    public static IReadOnlyList<PendingCredentialRemoval> SetAsideCredentials(AppSettings? settings, SetupRole role, string? chatBaseUrl) =>
        (settings?.Setup?.PendingRemovals ?? []).Where(item => item.Role == role && (chatBaseUrl is null
                ? item.Scope is null || item.Scope.RouteType == SetupRouteType.OpenAi
                : item.Scope is { RouteType: SetupRouteType.ChatCompletions } scope && scope.Origin == chatBaseUrl))
            .Reverse().ToArray();

    /// <summary>ElevenLabs keys set aside (listed for removal) when Voice left ElevenLabs, newest first.</summary>
    public static IReadOnlyList<PendingCredentialRemoval> SetAsideElevenLabsCredentials(AppSettings? settings) =>
        (settings?.Setup?.PendingRemovals ?? []).Where(item => item.Role == SetupRole.Tts &&
                item.Scope is { RouteType: SetupRouteType.ElevenLabs } scope && scope.Origin == ElevenLabsSetup.Origin)
            .Reverse().ToArray();

    /// <summary>Uses a key set aside earlier again for the role's route, which must be keyless and at the key's exact
    /// destination: switching back to a provider needs no key typed again. The key leaves the removal list.</summary>
    public static AppSettings ReattachSetAsideCredential(AppSettings settings, PendingCredentialRemoval removal)
    {
        ArgumentNullException.ThrowIfNull(removal);
        settings.Validate();
        var setup = settings.Setup!;
        var route = setup.Routes.SingleOrDefault(item => item.Role == removal.Role);
        ContractRules.Require(setup.PendingRemovals.Contains(removal) && route is { CredentialId: null } &&
            (route.RouteType is SetupRouteType.OpenAi or SetupRouteType.ChatCompletions &&
                SetAsideCredentials(settings, removal.Role, route.RouteType == SetupRouteType.ChatCompletions ? route.Origin : null).Contains(removal) ||
             route.RouteType == SetupRouteType.ElevenLabs && SetAsideElevenLabsCredentials(settings).Contains(removal)),
            "Only a key set aside for this job's exact destination can be used again.");
        var updated = settings with
        {
            Setup = setup with { PendingRemovals = setup.PendingRemovals.Where(item => item != removal).ToArray() }
        };
        return ReplaceRoute(updated, route!.WithCredential(removal.CredentialId));
    }
}
