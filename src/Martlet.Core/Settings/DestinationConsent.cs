using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>The owner's saved consent to one route's exact selection. <see cref="DestinationConsentPolicy"/> makes it
/// from a route.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DestinationConsent : IContract
{
    public required int SchemaVersion { get; init; }
    public required SetupRole Role { get; init; }
    public required string ProviderAlias { get; init; }
    public required string Origin { get; init; }
    public required string ModelId { get; init; }
    public string? VoiceId { get; init; }
    public Guid? CredentialId { get; init; }
    public required Guid ConfigurationRevision { get; init; }
    public required bool UserSelected { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SetupRouteType? RouteType { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RouteSchemaVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Enabled { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectionSha256 { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllowNetworkDisclosure { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllowLocalProcess { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllowReferenceAudio { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllowPotentialCost { get; init; }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion is 1 or 2, "Unsupported consent version.", ErrorCode.UnsupportedVersion);
        ContractRules.Defined(Role);
        ContractRules.Require(UserSelected && ConfigurationRevision != Guid.Empty,
            "Destination consent requires an explicit user selection and configuration revision.");
        if (RouteType == SetupRouteType.ChatCompletions) ChatCompletionsSetup.ModelId(ModelId);
        else if (RouteType == SetupRouteType.LocalWindowsStt) WindowsSpeechSetup.InstalledId(ModelId);
        else OpenAiSetup.UpstreamId(ModelId);
        if (VoiceId is not null)
        {
            if (RouteType == SetupRouteType.LocalWindowsTts) WindowsSpeechSetup.InstalledId(VoiceId);
            else OpenAiSetup.UpstreamId(VoiceId);
        }
        ContractRules.Require(CredentialId != Guid.Empty, "A credential reference must be a nonempty UUID.");
        if (SchemaVersion == 1)
        {
            ContractRules.Require(RouteType is null && RouteSchemaVersion is null && Enabled is null &&
                SelectionSha256 is null && AllowNetworkDisclosure is null && AllowLocalProcess is null &&
                AllowReferenceAudio is null && AllowPotentialCost is null &&
                ProviderAlias == OpenAiSetup.Alias(Role) && Origin == OpenAiSetup.Origin &&
                (Role == SetupRole.Tts ? VoiceId is not null : VoiceId is null),
                "Legacy destination consent is limited to the named OpenAI route.");
            return;
        }
        ContractRules.Require(RouteType is not null && RouteSchemaVersion == 1 && Enabled is not null &&
            AllowNetworkDisclosure is not null && AllowLocalProcess is not null &&
            AllowReferenceAudio is not null && AllowPotentialCost is not null,
            "Versioned route consent is incomplete.");
        ContractRules.Defined(RouteType!.Value);
        SelfHostSetup.Sha256(SelectionSha256);
    }
}
