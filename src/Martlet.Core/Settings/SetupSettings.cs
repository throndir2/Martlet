using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

public enum SetupRole { Stt, Llm, Tts }
public enum SetupStep { Choice, Destinations, Credentials, Review }
/// <summary>How a job is done. <see cref="LocalWindowsTts"/> (a Windows voice) is retired: files that have it still load, and
/// the route is dropped when settings are read (<see cref="AppSettings.DropWindowsVoice"/>).</summary>
public enum SetupRouteType { OpenAi, GatewayOllama, GatewayF5, LocalWhisper, ChatCompletions, LocalWindowsStt, LocalWindowsTts, GatewayStt, LocalParakeet, ElevenLabs }
public enum GatewayCancellationMode { DiscardOnly, RequestAbort, CooperativeComputeCancel }

/// <summary>The saved setup of the three jobs (<see cref="SetupRole"/>): one <see cref="SetupRoute"/> for each, and the keys
/// to remove or keep. The rules are in <see cref="SetupValidation"/>; the changes the setup pages save are in
/// SetupSettings.Actions.cs and the status text in SetupSettings.Describe.cs.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed partial record SetupSettings : IContract
{
    public const int CurrentSchemaVersion = 2;
    public const int MaximumRetainedGatewayCredentials = 16;
    public required int SchemaVersion { get; init; }
    public required SetupStep Checkpoint { get; init; }
    public required IReadOnlyList<SetupRoute> Routes { get; init; }
    public required IReadOnlyList<PendingCredentialRemoval> PendingRemovals { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RetainedGatewayCredential>? RetainedGatewayCredentials { get; init; }

    public void Validate() => SetupValidation.Settings(this);

    /// <summary>Two gateway routes that use the same paired host with the same device credential.</summary>
    public static bool SamePairing(SetupRoute first, SetupRoute second) =>
        SelfHostSetup.IsGateway(first.RouteType) && SelfHostSetup.IsGateway(second.RouteType) &&
        first.CredentialId is not null && first.CredentialId == second.CredentialId &&
        first.Gateway == second.Gateway && first.GatewayDeviceId == second.GatewayDeviceId;

    internal SetupSettings UpgradeToCurrent()
    {
        if (SchemaVersion == CurrentSchemaVersion)
            return this;
        ContractRules.Require(SchemaVersion == 1, "Unsupported setup version.", ErrorCode.UnsupportedVersion);
        return this with
        {
            SchemaVersion = CurrentSchemaVersion,
            Routes = Routes.Select(route => route.UpgradeLegacy()).ToArray()
        };
    }

    internal SetupSettings DowngradeOpenAiForHistoricalSettings()
    {
        ContractRules.Require(Routes.All(route => route.RouteType == SetupRouteType.OpenAi),
            "Historical settings can contain only legacy OpenAI routes.");
        ContractRules.Require((RetainedGatewayCredentials?.Count ?? 0) == 0 &&
            PendingRemovals.All(item => item.Scope is null || item.Scope.RouteType == SetupRouteType.OpenAi),
            "Historical settings cannot discard owned gateway pairings.");
        return this with
        {
            SchemaVersion = 1,
            RetainedGatewayCredentials = null,
            Routes = Routes.Select(route =>
            {
                var legacy = route with
                {
                    RouteSchemaVersion = null,
                    RouteType = null,
                    Enabled = null,
                    Gateway = null,
                    GatewayDeviceId = null,
                    GatewaySnapshot = null,
                    Reference = null,
                    LocalStt = null,
                    ClonedVoice = null,
                    Consent = null
                };
                return legacy with { Consent = route.Consent is null ? null : legacy.Selection() };
            }).ToArray(),
            PendingRemovals = PendingRemovals.Select(item => item with { Scope = null }).ToArray()
        };
    }
}
