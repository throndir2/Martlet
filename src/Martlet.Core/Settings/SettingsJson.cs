using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

public static class SettingsJson
{
    public static AppSettings Read(ReadOnlyMemory<byte> bytes)
    {
        // Inspect versions before rejecting new fields, so a newer file has an actionable version error.
        ContractJson.Read<VersionHeader>(bytes, AppSettings.MaxFileBytes);
        return AppSettings.ApplyMemoryDefault(ContractJson.Read<AppSettings>(bytes, AppSettings.MaxFileBytes));
    }

    private sealed record VersionHeader : IContract
    {
        public required int SchemaVersion { get; init; }
        public ProfileHeader? Profile { get; init; }
        public SetupHeader? Setup { get; init; }
        public ProfileHeader? Audio { get; init; }
        public ProfileHeader? Companion { get; init; }
        public ProfileHeader? Memory { get; init; }
        public void Validate()
        {
            ContractRules.Require(SchemaVersion is >= 1 and <= AppSettings.CurrentSchemaVersion, "Unsupported settings version.", ErrorCode.UnsupportedVersion);
            ContractRules.Require(Profile is null || Profile.SchemaVersion == 1, "Unsupported profile version.", ErrorCode.UnsupportedVersion);
            ContractRules.Require(Setup is null || Setup.SchemaVersion is >= 1 and <= SetupSettings.CurrentSchemaVersion,
                "Unsupported setup version.", ErrorCode.UnsupportedVersion);
            ContractRules.Require(Audio is null || Audio.SchemaVersion == 1, "Unsupported audio settings version.", ErrorCode.UnsupportedVersion);
            ContractRules.Require(Companion is null || Companion.SchemaVersion == 1, "Unsupported companion settings version.", ErrorCode.UnsupportedVersion);
            ContractRules.Require(Memory is null || Memory.SchemaVersion == 1, "Unsupported memory settings version.", ErrorCode.UnsupportedVersion);
            if (Setup?.Routes is { } routes)
                foreach (var route in routes)
                {
                    ContractRules.Require(route?.RouteSchemaVersion is null or 1,
                        "Unsupported route version.", ErrorCode.UnsupportedVersion);
                    ContractRules.Require(route?.Consent is null || route.Consent.SchemaVersion is 1 or 2,
                        "Unsupported destination consent version.", ErrorCode.UnsupportedVersion);
                    ContractRules.Require(route?.Gateway is null || route.Gateway.SchemaVersion == 1,
                        "Unsupported gateway endpoint version.", ErrorCode.UnsupportedVersion);
                    ContractRules.Require(route?.GatewaySnapshot is null || route.GatewaySnapshot.SchemaVersion == 1,
                        "Unsupported gateway route snapshot version.", ErrorCode.UnsupportedVersion);
                    ContractRules.Require(route?.Reference is null || route.Reference.SchemaVersion == 1,
                        "Unsupported reference selection version.", ErrorCode.UnsupportedVersion);
                    ContractRules.Require(route?.LocalStt is null || route.LocalStt.SchemaVersion == 1,
                        "Unsupported local STT selection version.", ErrorCode.UnsupportedVersion);
                }
            if (Setup?.PendingRemovals is { } removals)
                foreach (var removal in removals)
                    ContractRules.Require(removal?.Scope is null || removal.Scope.SchemaVersion == 1,
                        "Unsupported credential cleanup scope version.", ErrorCode.UnsupportedVersion);
            if (Setup?.RetainedGatewayCredentials is { } retained)
                foreach (var pairing in retained)
                    ContractRules.Require(pairing?.Scope is null || pairing.Scope.SchemaVersion == 1,
                        "Unsupported retained pairing scope version.", ErrorCode.UnsupportedVersion);
        }
    }

    private sealed record ProfileHeader
    {
        public required int SchemaVersion { get; init; }
    }

    private sealed record SetupHeader
    {
        public required int SchemaVersion { get; init; }
        public IReadOnlyList<RouteHeader?>? Routes { get; init; }
        public IReadOnlyList<RemovalHeader?>? PendingRemovals { get; init; }
        public IReadOnlyList<RemovalHeader?>? RetainedGatewayCredentials { get; init; }
    }

    private sealed record RouteHeader
    {
        public int? RouteSchemaVersion { get; init; }
        public ProfileHeader? Consent { get; init; }
        public ProfileHeader? Gateway { get; init; }
        public ProfileHeader? GatewaySnapshot { get; init; }
        public ProfileHeader? Reference { get; init; }
        public ProfileHeader? LocalStt { get; init; }
    }

    private sealed record RemovalHeader
    {
        public ProfileHeader? Scope { get; init; }
    }
}
