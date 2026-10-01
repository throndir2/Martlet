using System.Text;
using System.Text.Json.Nodes;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class MemorySettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(
        AppContext.BaseDirectory, "memory-settings", Guid.NewGuid().ToString("N"));
    private SettingsStore Store => new(directory);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task HistoricalSchemaMigratesAtomicallyToEnabledAppLocalMemory(int schema)
    {
        var current = SetupSettings.Begin(null);
        var historical = current with
        {
            SchemaVersion = schema, Memory = null,
            Companion = schema >= 3 ? current.Companion : null,
            Setup = schema >= 2 ? current.Setup!.DowngradeOpenAiForHistoricalSettings() : null
        };
        historical.Validate();
        Assert.True((await Store.SaveAsync(historical, null)).Saved);
        var original = await File.ReadAllBytesAsync(Store.FilePath);

        var loaded = await Store.LoadAsync();
        var draft = SetupSettings.Begin(loaded.Settings);
        Assert.Equal(AppSettings.CurrentSchemaVersion, draft.SchemaVersion);
        Assert.NotNull(draft.Memory);
        Assert.True(draft.Memory.Enabled);
        Assert.Equal(MemoryStoragePolicy.AppLocalData, draft.Memory.StoragePolicy);
        Assert.Null(draft.Memory.CustomDirectory);
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
        Assert.False(Directory.Exists(Path.Combine(directory, MemorySettings.AppLocalDirectoryName)));

        var migrated = await Store.SaveAsync(draft, loaded.Revision);
        Assert.True(migrated.Saved);
        Assert.Equal(schema, migrated.MigratedFromSchemaVersion);
        Assert.Equal(original, await File.ReadAllBytesAsync(
            Path.Combine(directory, migrated.SnapshotFileName!)));
        Assert.True((await Store.LoadAsync()).Settings!.Memory!.Enabled);
        Assert.False(Directory.Exists(Path.Combine(directory, MemorySettings.AppLocalDirectoryName)));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task OffByOldDefaultReadsAsOnUntilAnExplicitOffIsSavedAsCurrentSchema(int schema)
    {
        var current = SetupSettings.Begin(null);
        var off = current.Memory!.Configure(false, MemoryStoragePolicy.AppLocalData, null);
        var historical = current with
        {
            SchemaVersion = schema, Memory = off,
            Setup = schema >= 5 ? current.Setup : current.Setup!.DowngradeOpenAiForHistoricalSettings()
        };
        historical.Validate();
        Assert.True((await Store.SaveAsync(historical, null)).Saved);
        var original = await File.ReadAllBytesAsync(Store.FilePath);

        var loaded = await Store.LoadAsync();
        Assert.Equal(schema, loaded.Settings!.SchemaVersion);
        Assert.True(loaded.Settings.Memory!.Enabled);
        Assert.Equal(off.ConfigurationRevision, loaded.Settings.Memory.ConfigurationRevision);
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));

        var migrated = SetupSettings.Begin(loaded.Settings);
        Assert.True(migrated.Memory!.Enabled);
        var saved = await Store.SaveAsync(migrated, loaded.Revision);
        Assert.True(saved.Saved);
        Assert.Equal(schema, saved.MigratedFromSchemaVersion);

        var reloaded = await Store.LoadAsync();
        var explicitOff = reloaded.Settings! with
        {
            Memory = reloaded.Settings.Memory!.Configure(false, MemoryStoragePolicy.AppLocalData, null)
        };
        Assert.True((await Store.SaveAsync(explicitOff, reloaded.Revision)).Saved);
        var final = (await Store.LoadAsync()).Settings!;
        Assert.Equal(AppSettings.CurrentSchemaVersion, final.SchemaVersion);
        Assert.False(final.Memory!.Enabled);
        Assert.False(SetupSettings.Begin(final).Memory!.Enabled);
    }

    [Fact]
    public void NewProfilesStartWithMemoryOnInAppLocalData()
    {
        var memory = SetupSettings.Begin(null).Memory!;
        Assert.True(memory.Enabled);
        Assert.Equal(MemoryStoragePolicy.AppLocalData, memory.StoragePolicy);
        Assert.True(MemorySettings.Create().Enabled);
    }

    [Fact]
    public async Task StoragePolicyIsStrictAndChangesRequireFreshRevision()
    {
        var settings = SetupSettings.Begin(null);
        var custom = Path.Combine(directory, "custom-memory");
        var configured = settings.Memory!.Configure(
            true, MemoryStoragePolicy.CustomLocalDirectory, custom);
        settings = settings with { Memory = configured };
        var saved = await Store.SaveAsync(settings, null);
        Assert.True(saved.Saved);
        Assert.Equal(Path.GetFullPath(custom), configured.CustomDirectory);

        var bypass = settings with
        {
            Memory = settings.Memory with { Enabled = false }
        };
        var rejected = await Store.SaveAsync(bypass, saved.Revision);
        Assert.False(rejected.Saved);
        Assert.Equal(ErrorCode.InvalidContract, rejected.Error!.Code);
        Assert.True((await Store.LoadAsync()).Settings!.Memory!.Enabled);

        Assert.Throws<ContractException>(() =>
            MemorySettings.Create().Configure(true,
                MemoryStoragePolicy.CustomLocalDirectory, @"\\server\share\memory"));
        Assert.Throws<ContractException>(() =>
            MemorySettings.Create().Configure(true,
                MemoryStoragePolicy.CustomLocalDirectory, Path.GetPathRoot(directory)));
        Assert.Throws<ContractException>(() =>
            (MemorySettings.Create() with
            {
                StoragePolicy = MemoryStoragePolicy.AppLocalData,
                CustomDirectory = custom
            }).Validate());
    }

    [Fact]
    public async Task RecoveryNeverReenablesOrCopiesMemoryFacts()
    {
        var sourceStoreDirectory = Path.Combine(directory, "source");
        var sourceStore = new SettingsStore(sourceStoreDirectory);
        var source = SetupSettings.Begin(null);
        var sourcePath = Path.Combine(directory, "source-memory");
        source = source with
        {
            Memory = source.Memory!.Configure(
                true, MemoryStoragePolicy.CustomLocalDirectory, sourcePath)
        };
        Assert.True((await sourceStore.SaveAsync(source, null)).Saved);
        var backup = Path.Combine(directory, "memory-config.martlet-config");
        await sourceStore.CreateConfigurationSnapshotAsync(backup);

        var currentPath = Path.Combine(directory, "current-memory");
        var current = SetupSettings.Begin(source with
        {
            Memory = source.Memory.Configure(
                true, MemoryStoragePolicy.CustomLocalDirectory, currentPath)
        });
        var currentStore = Store;
        Assert.True((await currentStore.SaveAsync(current, null)).Saved);
        var plan = await currentStore.PreviewConfigurationRestoreAsync(backup);

        Assert.Contains("Memory facts, store files and exports are NOT backed up or restored", plan.Summary);
        Assert.Contains("\"enabled\": false", plan.CandidateJson);
        Assert.Contains(Path.GetFullPath(sourcePath).Replace("\\", "\\\\", StringComparison.Ordinal),
            plan.CandidateJson);
        await currentStore.RestoreConfigurationAsync(plan,
            plan.Approve(plan.SnapshotDigest, plan.Destination, plan.ExpectedRevision));

        var restored = (await currentStore.LoadAsync()).Settings!.Memory!;
        Assert.False(restored.Enabled);
        Assert.Equal(Path.GetFullPath(sourcePath), restored.CustomDirectory);
        Assert.NotEqual(source.Memory!.ConfigurationRevision,
            restored.ConfigurationRevision);
        Assert.False(Directory.Exists(sourcePath));
        Assert.False(Directory.Exists(currentPath));
        Assert.Empty(Directory.GetFiles(directory, ".martlet-memory.*", SearchOption.AllDirectories));
    }

    [Fact]
    public void JsonRejectsFutureMemoryShapeWithoutLeakingPath()
    {
        var settings = SetupSettings.Begin(null);
        var json = Encoding.UTF8.GetString(ContractJson.Write(settings));
        var memory = json.IndexOf("\"memory\"", StringComparison.Ordinal);
        var version = json.IndexOf("\"schema_version\": 1", memory, StringComparison.Ordinal);
        var future = json[..version] + "\"schema_version\": 99" +
            json[(version + "\"schema_version\": 1".Length)..];
        var error = Assert.Throws<ContractException>(() =>
            SettingsJson.Read(Encoding.UTF8.GetBytes(future)));
        Assert.Equal(ErrorCode.UnsupportedVersion, error.Code);
        Assert.DoesNotContain(directory, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("enum")]
    [InlineData("null")]
    [InlineData("legacy")]
    public void NestedMemoryShapeIsStrict(string mutation)
    {
        var node = JsonNode.Parse(ContractJson.Write(SetupSettings.Begin(null)))!;
        var memory = node["memory"]!;
        switch (mutation)
        {
            case "unknown": memory["private_unknown"] = "synthetic"; break;
            case "missing": memory.AsObject().Remove("enabled"); break;
            case "enum": memory["storage_policy"] = 0; break;
            case "null": node["memory"] = null; break;
            case "legacy": node["schema_version"] = 3; break;
        }
        var json = node.ToJsonString();
        if (mutation == "duplicate")
            json = json.Replace("\"enabled\":true", "\"enabled\":true,\"enabled\":false", StringComparison.Ordinal);
        Assert.Throws<ContractException>(() => SettingsJson.Read(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void CurrentMemorySettingsRetainStrictUtf8AndExactFileAndSnapshotLimits()
    {
        var bytes = ContractJson.Write(SetupSettings.Begin(null));
        var atLimit = new byte[AppSettings.MaxFileBytes];
        Array.Fill(atLimit, (byte)' ');
        bytes.CopyTo(atLimit, 0);
        Assert.NotNull(SettingsJson.Read(atLimit).Memory);
        Assert.Equal(ErrorCode.PayloadTooLarge, Assert.Throws<ContractException>(() =>
            SettingsJson.Read(atLimit.Concat(new byte[] { 32 }).ToArray())).Code);
        var snapshot = ConfigurationSnapshot.Create(atLimit);
        Assert.InRange(snapshot.Length, AppSettings.MaxFileBytes + 1, ConfigurationSnapshot.MaximumBytes);
        Assert.Equal(AppSettings.CurrentSchemaVersion, ConfigurationSnapshot.Inspect(snapshot).SettingsSchemaVersion);
        var malformed = Encoding.UTF8.GetString(bytes).Replace("\"app_local_data\"", "\"BAD\"", StringComparison.Ordinal);
        var invalid = Encoding.UTF8.GetBytes(malformed);
        invalid[malformed.IndexOf("BAD", StringComparison.Ordinal)] = 0xff;
        Assert.Throws<ContractException>(() => SettingsJson.Read(invalid));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
