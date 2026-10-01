using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

public enum MemoryStoragePolicy
{
    AppLocalData,
    CustomLocalDirectory
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MemorySettings : IContract
{
    public const string AppLocalDirectoryName = "memory";

    public required int SchemaVersion { get; init; }
    public required Guid ConfigurationRevision { get; init; }
    public required bool Enabled { get; init; }
    public required MemoryStoragePolicy StoragePolicy { get; init; }
    public string? CustomDirectory { get; init; }

    public static MemorySettings Create() => new()
    {
        SchemaVersion = 1,
        ConfigurationRevision = Guid.NewGuid(),
        Enabled = true,
        StoragePolicy = MemoryStoragePolicy.AppLocalData
    };

    public MemorySettings Configure(bool enabled, MemoryStoragePolicy storagePolicy, string? customDirectory)
    {
        ContractRules.Defined(storagePolicy);
        customDirectory = storagePolicy == MemoryStoragePolicy.CustomLocalDirectory
            ? NormalizeCustomDirectory(customDirectory)
            : null;
        if (Enabled == enabled && StoragePolicy == storagePolicy &&
            string.Equals(CustomDirectory, customDirectory, StringComparison.Ordinal))
            return this;
        var updated = this with
        {
            ConfigurationRevision = Guid.NewGuid(),
            Enabled = enabled,
            StoragePolicy = storagePolicy,
            CustomDirectory = customDirectory
        };
        updated.Validate();
        return updated;
    }

    internal MemorySettings DisableForRestore()
    {
        var disabled = this with
        {
            Enabled = false,
            ConfigurationRevision = Guid.NewGuid()
        };
        disabled.Validate();
        return disabled;
    }

    public string ResolveDirectory(string appDataDirectory)
    {
        Validate();
        ContractRules.Require(!string.IsNullOrWhiteSpace(appDataDirectory) &&
            Path.IsPathFullyQualified(appDataDirectory), "The app data directory is unavailable.");
        return StoragePolicy == MemoryStoragePolicy.AppLocalData
            ? Path.Combine(Path.GetFullPath(appDataDirectory), AppLocalDirectoryName)
            : CustomDirectory!;
    }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == 1, "Unsupported memory settings version.",
            ErrorCode.UnsupportedVersion);
        ContractRules.Require(ConfigurationRevision != Guid.Empty,
            "Memory settings require a nonempty configuration revision.");
        ContractRules.Defined(StoragePolicy);
        if (StoragePolicy == MemoryStoragePolicy.AppLocalData)
            ContractRules.Require(CustomDirectory is null,
                "App-local memory cannot specify a custom directory.");
        else
            ContractRules.Require(string.Equals(CustomDirectory,
                NormalizeCustomDirectory(CustomDirectory), StringComparison.Ordinal),
                "The custom memory directory must be an absolute normalized local path.");
    }

    public static string NormalizeCustomDirectory(string? path)
    {
        ContractRules.Require(!string.IsNullOrWhiteSpace(path) &&
            Path.IsPathFullyQualified(path) &&
            !path.StartsWith(@"\\", StringComparison.Ordinal) &&
            !path.StartsWith("//", StringComparison.Ordinal) &&
            !path.Contains('\0'), "Choose an absolute local memory directory.");
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path!));
            var root = Path.GetPathRoot(full);
            ContractRules.Require(!string.IsNullOrWhiteSpace(root) &&
                !string.Equals(full, Path.TrimEndingDirectorySeparator(root!),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
                "Choose a non-root local memory directory.");
            if (OperatingSystem.IsWindows())
                ContractRules.Require(full.Length > 3 && full[1] == ':' && !full[2..].Contains(':'),
                    "Choose a local memory directory without an alternate data stream.");
            return full;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new ContractException(ErrorCode.InvalidContract,
                "Choose a valid absolute local memory directory.");
        }
    }
}
