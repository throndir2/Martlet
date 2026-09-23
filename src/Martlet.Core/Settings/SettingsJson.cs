using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

public static class SettingsJson
{
    public static AppSettings Read(ReadOnlyMemory<byte> bytes)
    {
        // Inspect versions before rejecting new fields, so a newer file has an actionable version error.
        ContractJson.Read<VersionHeader>(bytes, AppSettings.MaxFileBytes);
        return ContractJson.Read<AppSettings>(bytes, AppSettings.MaxFileBytes);
    }

    private sealed record VersionHeader : IContract
    {
        public required int SchemaVersion { get; init; }
        public ProfileHeader? Profile { get; init; }
        public SetupHeader? Setup { get; init; }
        public ProfileHeader? Audio { get; init; }
        public ProfileHeader? Companion { get; init; }
        public void Validate()
        {
            ContractRules.Require(SchemaVersion is >= 1 and <= AppSettings.CurrentSchemaVersion, "Unsupported settings version.", ErrorCode.UnsupportedVersion);
            ContractRules.Require(Profile is null || Profile.SchemaVersion == 1, "Unsupported profile version.", ErrorCode.UnsupportedVersion);
            ContractRules.Require(Setup is null || Setup.SchemaVersion == 1, "Unsupported setup version.", ErrorCode.UnsupportedVersion);
            ContractRules.Require(Audio is null || Audio.SchemaVersion == 1, "Unsupported audio settings version.", ErrorCode.UnsupportedVersion);
            ContractRules.Require(Companion is null || Companion.SchemaVersion == 1, "Unsupported companion settings version.", ErrorCode.UnsupportedVersion);
            if (Setup?.Routes is { } routes)
                foreach (var route in routes)
                    ContractRules.Require(route?.Consent is null || route.Consent.SchemaVersion == 1,
                        "Unsupported destination consent version.", ErrorCode.UnsupportedVersion);
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
    }

    private sealed record RouteHeader
    {
        public ProfileHeader? Consent { get; init; }
    }
}
