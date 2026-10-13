using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>The rules a saved setup (<see cref="SetupSettings"/>) and each of its routes (<see cref="SetupRoute"/>) must
/// follow: one route for each role, keys that are not shared, and for each route type the exact fields it may have and
/// the evidence it needs before it can be on. The smaller setup records check themselves in their own Validate.</summary>
internal static class SetupValidation
{
    public static void Settings(SetupSettings settings)
    {
        ContractRules.Require(settings.SchemaVersion is 1 or SetupSettings.CurrentSchemaVersion,
            "Unsupported setup version.", ErrorCode.UnsupportedVersion);
        ContractRules.Defined(settings.Checkpoint);
        ContractRules.Require(settings.SchemaVersion != 1 || settings.RetainedGatewayCredentials is null,
            "Historical setup cannot contain retained gateway pairings.");
        ContractRules.Require(settings.RetainedGatewayCredentials is null or { Count: <= SetupSettings.MaximumRetainedGatewayCredentials },
            "Retained pairing capacity reached. Explicitly remove an unused pairing; none were evicted.");
        ContractRules.Require(settings.Routes is { Count: <= 3 } && settings.PendingRemovals is { Count: <= 16 },
            "Setup supports three roles and at most sixteen pending credential removals.");
        var roles = new HashSet<SetupRole>();
        var ids = new HashSet<Guid>();
        var pairings = new Dictionary<Guid, SetupRoute>();
        foreach (var route in settings.Routes!)
        {
            ContractRules.Require(route is not null, "A route cannot be null.");
            route!.Validate();
            ContractRules.Require(settings.SchemaVersion == 1 ? route.RouteType is null : route.RouteType is not null,
                "The setup and route discriminator versions must advance atomically.");
            ContractRules.Require(roles.Add(route.Role), "Each role must have exactly one selected route.");
            if (route.CredentialId is { } id)
            {
                // Roles handed to the same paired host share that pairing's one device credential.
                ContractRules.Require(ids.Add(id) || pairings.TryGetValue(id, out var other) && SetupSettings.SamePairing(other, route),
                    "Credentials cannot be shared across role policies.");
                if (SelfHostSetup.IsGateway(route.RouteType)) pairings.TryAdd(id, route);
            }
        }
        foreach (var removal in settings.PendingRemovals!)
        {
            ContractRules.Require(removal is not null && removal.CredentialId != Guid.Empty,
                "A pending removal requires its owned reference.");
            ContractRules.Defined(removal!.Role);
            removal.Scope?.Validate();
            ContractRules.Require(settings.SchemaVersion != 1 || removal.Scope is null,
                "Historical cleanup cannot contain versioned credential scopes.");
            ContractRules.Require(removal.Scope is null ||
                removal.Scope.RouteType != SetupRouteType.LocalWhisper &&
                (removal.Scope.RouteType switch
                {
                    SetupRouteType.OpenAi => removal.Scope.ProviderAlias == OpenAiSetup.Alias(removal.Role),
                    SetupRouteType.ChatCompletions => removal.Role == SetupRole.Llm,
                    SetupRouteType.GatewayOllama => removal.Role == SetupRole.Llm,
                    SetupRouteType.GatewayF5 or SetupRouteType.ElevenLabs => removal.Role == SetupRole.Tts,
                    SetupRouteType.GatewayStt => removal.Role == SetupRole.Stt,
                    _ => false
                }), "The pending credential cleanup scope does not match its role.");
            ContractRules.Require(ids.Add(removal.CredentialId),
                "A pending removal cannot delete an active or duplicate credential.");
        }
        foreach (var retained in settings.RetainedGatewayCredentials ?? [])
        {
            ContractRules.Require(retained is not null, "A retained pairing cannot be null.");
            retained!.Validate();
            ContractRules.Require(ids.Add(retained.CredentialId),
                "Active, retained and pending credential references must be unique.");
        }
    }

    public static void Route(SetupRoute route)
    {
        ContractRules.Defined(route.Role);
        ContractRules.Require(route.CredentialId != Guid.Empty, "A credential reference must be a nonempty UUID.");
        ContractRules.Require(route.ConfigurationRevision != Guid.Empty,
            "A route requires a fresh configuration revision.");
        if (route.RouteType is null)
            LegacyRoute(route);
        else
            CurrentRoute(route, route.RouteType.Value);
        route.Consent?.Validate();
        ContractRules.Require(route.Consent is null || route.Consent == route.Selection(),
            "This route changed. Review its destination, model, voice, credential and execution boundary, then explicitly consent again.");
    }

    private static void LegacyRoute(SetupRoute route)
    {
        ContractRules.Require(route.RouteSchemaVersion is null && route.Enabled is null && route.Gateway is null &&
            route.GatewayDeviceId is null && route.GatewaySnapshot is null && route.Reference is null && route.LocalStt is null && route.ClonedVoice is null,
            "Legacy routes cannot contain versioned route fields.");
        ContractRules.Require(route.ProviderAlias == OpenAiSetup.Alias(route.Role) && route.Origin == OpenAiSetup.Origin,
            "Only the named OpenAI destination is supported by legacy routes.");
        OpenAiSetup.UpstreamId(route.ModelId);
        ContractRules.Require(route.Role == SetupRole.Tts ? route.VoiceId is not null : route.VoiceId is null,
            "Select a voice only for TTS.");
        if (route.VoiceId is not null)
            OpenAiSetup.UpstreamId(route.VoiceId);
    }

    private static void CurrentRoute(SetupRoute route, SetupRouteType routeType)
    {
        ContractRules.Defined(routeType);
        ContractRules.Require(route.RouteSchemaVersion == 1 && route.Enabled is not null,
            "A current route requires its exact route discriminator and enablement state.");
        if (routeType == SetupRouteType.ChatCompletions) ChatCompletionsSetup.ModelId(route.ModelId);
        else if (routeType == SetupRouteType.LocalWindowsStt) WindowsSpeechSetup.InstalledId(route.ModelId);
        else OpenAiSetup.UpstreamId(route.ModelId);
        if (route.VoiceId is not null)
        {
            if (routeType == SetupRouteType.LocalWindowsTts) WindowsSpeechSetup.InstalledId(route.VoiceId);
            else OpenAiSetup.UpstreamId(route.VoiceId);
        }
        ContractRules.Require(routeType == SetupRouteType.ElevenLabs || route.ClonedVoice is null,
            "Only an ElevenLabs route keeps a cloned voice.");
        switch (routeType)
        {
            case SetupRouteType.ElevenLabs:
                ContractRules.Require(route.Role == SetupRole.Tts && route.ProviderAlias == ElevenLabsSetup.Alias && route.Origin == ElevenLabsSetup.Origin &&
                    ElevenLabsSetup.SupportsModel(route.ModelId) && ElevenLabsSetup.IsVoiceId(route.VoiceId) && route.ClonedVoice is not null &&
                    route.Gateway is null && route.GatewayDeviceId is null && route.GatewaySnapshot is null && route.Reference is null && route.LocalStt is null,
                    "The ElevenLabs route needs its exact origin, a supported model and the voice cloned from one of your recordings.");
                route.ClonedVoice!.Validate();
                break;
            case SetupRouteType.LocalWindowsStt:
            case SetupRouteType.LocalWindowsTts:
                var stt = routeType == SetupRouteType.LocalWindowsStt;
                ContractRules.Require(route.Role == (stt ? SetupRole.Stt : SetupRole.Tts) &&
                    route.ProviderAlias == (stt ? WindowsSpeechSetup.SttAlias : WindowsSpeechSetup.TtsAlias) &&
                    route.Origin == SelfHostSetup.LocalOrigin && route.CredentialId is null &&
                    route.Gateway is null && route.GatewayDeviceId is null && route.GatewaySnapshot is null &&
                    route.Reference is null && route.LocalStt is null &&
                    (stt ? route.VoiceId is null : route.VoiceId is not null && route.ModelId == WindowsSpeechSetup.TtsModelId),
                    "Installed Windows speech requires its exact local role and selection, never a cloud credential or gateway route.");
                break;
            case SetupRouteType.LocalParakeet:
                ContractRules.Require(route.Role == SetupRole.Stt && route.ProviderAlias == LocalSpeechSetup.ParakeetAlias &&
                    route.Origin == SelfHostSetup.LocalOrigin && LocalSpeechSetup.IsParakeetModel(route.ModelId) && route.VoiceId is null &&
                    route.CredentialId is null && route.Gateway is null && route.GatewayDeviceId is null && route.GatewaySnapshot is null &&
                    route.Reference is null && route.LocalStt is null,
                    "Parakeet on this PC requires its exact local selection, never a cloud credential or gateway route.");
                break;
            case SetupRouteType.ChatCompletions:
                _ = ChatCompletionsSetup.BaseUri(route.Origin);
                ContractRules.Require(route.Role == SetupRole.Llm && route.ProviderAlias == ChatCompletionsSetup.Alias &&
                    route.VoiceId is null && route.Gateway is null && route.GatewayDeviceId is null &&
                    route.GatewaySnapshot is null && route.Reference is null && route.LocalStt is null,
                    "The Chat Completions route must have its own endpoint, model and optional scoped credential.");
                break;
            case SetupRouteType.OpenAi:
                ContractRules.Require(route.ProviderAlias == OpenAiSetup.Alias(route.Role) && route.Origin == OpenAiSetup.Origin &&
                    route.Gateway is null && route.GatewayDeviceId is null && route.GatewaySnapshot is null && route.Reference is null && route.LocalStt is null &&
                    (route.Role == SetupRole.Tts ? route.VoiceId is not null : route.VoiceId is null),
                    "The OpenAI route discriminator does not match its route fields.");
                break;
            case SetupRouteType.GatewayOllama:
                ContractRules.Require(route.Role == SetupRole.Llm && route.ProviderAlias == SelfHostSetup.GatewayOllamaAlias &&
                    route.VoiceId is null && route.Gateway is not null && route.Reference is null && route.LocalStt is null,
                    "The gateway Ollama route discriminator does not match its route fields.");
                GatewayRoute(route, routeType);
                break;
            case SetupRouteType.GatewayStt:
                ContractRules.Require(route.Role == SetupRole.Stt && route.ProviderAlias == SelfHostSetup.GatewaySttAlias &&
                    route.VoiceId is null && route.Gateway is not null && route.Reference is null && route.LocalStt is null,
                    "The gateway speech-to-text route discriminator does not match its route fields.");
                GatewayRoute(route, routeType);
                break;
            case SetupRouteType.GatewayF5:
                ContractRules.Require(route.Role == SetupRole.Tts && route.ProviderAlias == SelfHostSetup.GatewayF5Alias &&
                    route.VoiceId is null && route.Gateway is not null && route.LocalStt is null,
                    "The gateway F5 route discriminator does not match its route fields.");
                GatewayRoute(route, routeType);
                route.Reference?.Validate();
                ContractRules.Require(route.Reference is null || route.GatewaySnapshot is null ||
                    route.Reference.ProcessingDestinationId == route.GatewaySnapshot.DestinationId,
                    "The applied F5 reference is bound to a different processing destination.");
                break;
            case SetupRouteType.LocalWhisper:
                ContractRules.Require(route.Role == SetupRole.Stt && route.ProviderAlias == SelfHostSetup.LocalWhisperAlias &&
                    route.Origin == SelfHostSetup.LocalOrigin && route.VoiceId is null && route.CredentialId is null &&
                    route.Gateway is null && route.GatewayDeviceId is null && route.GatewaySnapshot is null && route.Reference is null,
                    "The local whisper route discriminator does not match its route fields.");
                route.LocalStt?.Validate();
                ContractRules.Require(route.LocalStt is null || route.ModelId == route.LocalStt.ModelId,
                    "The local STT route model does not match its verified package.");
                break;
        }
        if (route.Enabled == true)
        {
            ContractRules.Require(routeType switch
            {
                SetupRouteType.OpenAi or SetupRouteType.ChatCompletions or SetupRouteType.LocalWindowsStt or SetupRouteType.LocalWindowsTts or
                    SetupRouteType.LocalParakeet or SetupRouteType.ElevenLabs => true,
                SetupRouteType.GatewayOllama or SetupRouteType.GatewayStt => route.GatewaySnapshot is not null && route.CredentialId is not null,
                SetupRouteType.GatewayF5 => route.GatewaySnapshot is not null && route.CredentialId is not null && route.Reference is not null,
                SetupRouteType.LocalWhisper => route.LocalStt is not null,
                _ => false
            }, "A self-host route cannot be enabled before its exact pairing, probe, reference or package evidence is configured.");
        }
    }

    private static void GatewayRoute(SetupRoute route, SetupRouteType routeType)
    {
        route.Gateway!.Validate();
        ContractRules.Require(route.CredentialId is null || route.GatewayDeviceId is not null,
            "A paired route requires its exact device identity.");
        if (route.GatewayDeviceId is not null) SelfHostSetup.Identifier(route.GatewayDeviceId, 64);
        ContractRules.Require(route.Origin == route.Gateway.Origin,
            "The route origin must match the pinned gateway endpoint.");
        route.GatewaySnapshot?.Validate();
        ContractRules.Require(route.GatewaySnapshot is null ||
            route.GatewaySnapshot.RouteType == routeType && route.GatewaySnapshot.ModelId == route.ModelId,
            "The selected model does not match the exact gateway route snapshot.");
    }
}
