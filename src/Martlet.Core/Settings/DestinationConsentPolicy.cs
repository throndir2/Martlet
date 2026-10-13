using System.Security.Cryptography;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>What the owner's consent to a route covers. <see cref="Selection"/> is the consent that a route's current
/// selection needs: its destination, model, voice, key and a digest of every other field, and what the route type may do
/// (send data over the network, start a local process, use a reference recording, cost money). A route's saved
/// <see cref="SetupRoute.Consent"/> is valid only while it equals this.</summary>
internal static class DestinationConsentPolicy
{
    public static DestinationConsent Selection(SetupRoute route)
    {
        if (route.RouteType is null)
            return new()
            {
                SchemaVersion = 1,
                Role = route.Role,
                ProviderAlias = route.ProviderAlias,
                Origin = route.Origin,
                ModelId = route.ModelId,
                VoiceId = route.VoiceId,
                CredentialId = route.CredentialId,
                ConfigurationRevision = route.ConfigurationRevision,
                UserSelected = true
            };
        var routeType = route.RouteType.Value;
        return new()
        {
            SchemaVersion = 2,
            Role = route.Role,
            ProviderAlias = route.ProviderAlias,
            Origin = route.Origin,
            ModelId = route.ModelId,
            VoiceId = route.VoiceId,
            CredentialId = route.CredentialId,
            ConfigurationRevision = route.ConfigurationRevision,
            UserSelected = true,
            RouteType = routeType,
            RouteSchemaVersion = route.RouteSchemaVersion,
            Enabled = route.Enabled,
            SelectionSha256 = SelectionDigest(route),
            AllowNetworkDisclosure = routeType is SetupRouteType.OpenAi or SetupRouteType.ChatCompletions or SetupRouteType.ElevenLabs ||
                SelfHostSetup.IsGateway(routeType),
            AllowLocalProcess = routeType is SetupRouteType.LocalWhisper or SetupRouteType.LocalWindowsStt or SetupRouteType.LocalWindowsTts or SetupRouteType.LocalParakeet,
            // An ElevenLabs voice was cloned from an uploaded recording: the owner's consent covers that recording.
            AllowReferenceAudio = routeType is SetupRouteType.GatewayF5 or SetupRouteType.ElevenLabs,
            AllowPotentialCost = routeType is not (SetupRouteType.LocalWindowsStt or SetupRouteType.LocalWindowsTts or SetupRouteType.LocalParakeet)
        };
    }

    private static string SelectionDigest(SetupRoute route)
    {
        var bytes = ContractJson.Write(route with { Consent = null }, 65_536);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
