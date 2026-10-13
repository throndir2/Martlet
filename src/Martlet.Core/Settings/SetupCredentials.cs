using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

// The keys that setup owns but no route uses now: what a key may be used for (its scope), keys listed for removal and
// host pairings kept for later.

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CredentialScopeSettings : IContract
{
    public required int SchemaVersion { get; init; }
    public required SetupRouteType RouteType { get; init; }
    public required string ProviderAlias { get; init; }
    public required string Origin { get; init; }
    public string? HostId { get; init; }
    public string? SpkiFingerprint { get; init; }
    public string? DeviceRole { get; init; }
    public string? DeviceId { get; init; }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == 1, "Unsupported credential scope version.", ErrorCode.UnsupportedVersion);
        ContractRules.Defined(RouteType);
        if (RouteType == SetupRouteType.ChatCompletions)
        {
            _ = ChatCompletionsSetup.BaseUri(Origin);
            ContractRules.Require(ProviderAlias == ChatCompletionsSetup.Alias && HostId is null &&
                SpkiFingerprint is null && DeviceRole is null && DeviceId is null,
                "The Chat Completions credential cleanup scope is invalid.");
            return;
        }
        if (RouteType == SetupRouteType.OpenAi)
        {
            ContractRules.Require(Origin == OpenAiSetup.Origin && HostId is null &&
                SpkiFingerprint is null && DeviceRole is null && DeviceId is null,
                "The OpenAI credential cleanup scope is invalid.");
            return;
        }
        if (RouteType == SetupRouteType.ElevenLabs)
        {
            ContractRules.Require(ProviderAlias == ElevenLabsSetup.Alias && Origin == ElevenLabsSetup.Origin && HostId is null &&
                SpkiFingerprint is null && DeviceRole is null && DeviceId is null,
                "The ElevenLabs credential cleanup scope is invalid.");
            return;
        }
        ContractRules.Require(SelfHostSetup.IsGateway(RouteType),
            "Only OpenAI and paired gateway routes own provider credentials.");
        SelfHostSetup.Identifier(DeviceId, 64);
        new GatewayEndpointSettings
        {
            SchemaVersion = 1,
            Origin = Origin,
            HostId = HostId!,
            SpkiFingerprint = SpkiFingerprint!,
            DeviceRole = DeviceRole!
        }.Validate();
        ContractRules.Require(ProviderAlias == SelfHostSetup.Gateway(RouteType).Alias,
            "The gateway credential cleanup scope does not match its route.");
    }

    internal static CredentialScopeSettings From(SetupRoute route)
    {
        var routeType = route.RouteType ?? SetupRouteType.OpenAi;
        ContractRules.Require(routeType is not (SetupRouteType.LocalWhisper or SetupRouteType.LocalWindowsStt or SetupRouteType.LocalWindowsTts or
                SetupRouteType.LocalParakeet),
            "Local speech has no provider credential.");
        return new()
        {
            SchemaVersion = 1,
            RouteType = routeType,
            ProviderAlias = route.ProviderAlias,
            Origin = route.Origin,
            HostId = route.Gateway?.HostId,
            SpkiFingerprint = route.Gateway?.SpkiFingerprint,
            DeviceRole = route.Gateway?.DeviceRole,
            DeviceId = route.GatewayDeviceId
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PendingCredentialRemoval
{
    public required SetupRole Role { get; init; }
    public required Guid CredentialId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CredentialScopeSettings? Scope { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RetainedGatewayCredential : IContract
{
    public required SetupRole Role { get; init; }
    public required Guid CredentialId { get; init; }
    public required CredentialScopeSettings Scope { get; init; }

    public void Validate()
    {
        ContractRules.Require(CredentialId != Guid.Empty && Scope is not null,
            "A retained pairing requires its owned reference and scope.");
        Scope!.Validate();
        ContractRules.Require(SelfHostSetup.IsGateway(Scope.RouteType) && Role == SelfHostSetup.Gateway(Scope.RouteType).Role,
            "A retained pairing must match its exact gateway role.");
    }

    internal static RetainedGatewayCredential From(SetupRoute route) => new()
    {
        Role = route.Role,
        CredentialId = route.CredentialId!.Value,
        Scope = CredentialScopeSettings.From(route)
    };
}
