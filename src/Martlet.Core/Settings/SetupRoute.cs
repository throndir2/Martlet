using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>The route of one job (Listening, Thinking or Voice): where it goes, the model and voice, its key and, for a
/// self-host route, its pairing, probe, reference or package. The rules are in <see cref="SetupValidation"/> and the
/// consent it needs in <see cref="DestinationConsentPolicy"/>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SetupRoute : IContract
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RouteSchemaVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SetupRouteType? RouteType { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Enabled { get; init; }
    public required SetupRole Role { get; init; }
    public required string ProviderAlias { get; init; }
    public required string Origin { get; init; }
    public required string ModelId { get; init; }
    public string? VoiceId { get; init; }
    public Guid? CredentialId { get; init; }
    public required Guid ConfigurationRevision { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GatewayEndpointSettings? Gateway { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GatewayDeviceId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GatewayRouteSnapshot? GatewaySnapshot { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public F5ReferenceSettings? Reference { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LocalSttPackageSettings? LocalStt { get; init; }
    /// <summary>For an ElevenLabs route, the saved speaking voice its <see cref="VoiceId"/> was cloned from.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ClonedVoiceSettings? ClonedVoice { get; init; }
    public DestinationConsent? Consent { get; init; }

    public DestinationConsent Selection() => DestinationConsentPolicy.Selection(this);

    public void Validate() => SetupValidation.Route(this);

    public SetupRoute WithCredential(Guid? id)
    {
        ContractRules.Require(RouteType is not (SetupRouteType.LocalWhisper or SetupRouteType.LocalWindowsStt or SetupRouteType.LocalWindowsTts or
                SetupRouteType.LocalParakeet),
            "Local speech has no provider credential.");
        return this with
        {
            CredentialId = id,
            Consent = null,
            Enabled = RouteType switch
            {
                null => null,
                SetupRouteType.OpenAi or SetupRouteType.ChatCompletions or SetupRouteType.ElevenLabs => Enabled,
                _ => false
            },
            ConfigurationRevision = Guid.NewGuid()
        };
    }

    public SetupRoute DisableForRestore()
    {
        if (RouteType is null)
            return WithCredential(null);
        return this with
        {
            Enabled = RouteType == SetupRouteType.OpenAi && Enabled == true,
            CredentialId = null,
            GatewayDeviceId = null,
            Consent = null,
            GatewaySnapshot = SelfHostSetup.IsGateway(RouteType)
                ? null
                : GatewaySnapshot,
            Reference = RouteType == SetupRouteType.GatewayF5 ? null : Reference,
            LocalStt = RouteType == SetupRouteType.LocalWhisper ? null : LocalStt,
            ConfigurationRevision = Guid.NewGuid()
        };
    }

    internal SetupRoute UpgradeLegacy()
    {
        if (RouteType is not null)
            return this;
        var upgraded = this with
        {
            RouteSchemaVersion = 1,
            RouteType = SetupRouteType.OpenAi,
            Enabled = true,
            Consent = null
        };
        return upgraded with { Consent = Consent is null ? null : upgraded.Selection() };
    }

    internal string CredentialScope() => RouteType switch
    {
        null or SetupRouteType.OpenAi => $"openai:{Role}",
        SetupRouteType.ChatCompletions => $"chat-completions:{Origin}",
        SetupRouteType.ElevenLabs => $"elevenlabs:{Origin}",
        SetupRouteType.GatewayOllama or SetupRouteType.GatewayF5 or SetupRouteType.GatewayStt =>
            $"gateway:{RouteType}:{Gateway!.Origin}:{Gateway.HostId}:{Gateway.SpkiFingerprint}:{Gateway.DeviceRole}",
        _ => $"none:{RouteType}"
    };
}
