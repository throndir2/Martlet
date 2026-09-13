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
        public void Validate()
        {
            ContractRules.Require(SchemaVersion == AppSettings.CurrentSchemaVersion, "Unsupported settings version.", ErrorCode.UnsupportedVersion);
            ContractRules.Require(Profile is null || Profile.SchemaVersion == 1, "Unsupported profile version.", ErrorCode.UnsupportedVersion);
        }
    }

    private sealed record ProfileHeader
    {
        public required int SchemaVersion { get; init; }
    }
}
