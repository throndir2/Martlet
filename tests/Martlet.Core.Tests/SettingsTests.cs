using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Martlet.Tests", Guid.NewGuid().ToString("N"));
    private SettingsStore Store => new(directory);

    [Fact]
    public async Task FirstRunDoesNotCreateFilesOrInventSettings()
    {
        var result = await Store.LoadAsync();
        Assert.Equal(SettingsLoadState.FirstRun, result.State);
        Assert.Null(result.Settings);
        Assert.Null(result.Error);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task AtomicRoundTripUsesReferencesOnlyAndRejectsStaleWrites()
    {
        var initial = AppSettings.CreateUnconfigured();
        var saved = await Store.SaveAsync(initial, null);
        Assert.True(saved.Saved);
        var loaded = await Store.LoadAsync();
        Assert.Equal(initial.Profile.Id, loaded.Settings!.Profile.Id);
        Assert.Equal(saved.Revision, loaded.Revision);
        Assert.Empty(loaded.Settings.Profile.Credentials);
        var modified = initial with
        {
            Profile = initial.Profile with
            {
                Kind = ProfileKind.Api,
                Credentials = [new SecretReference { ProviderId = "example", CredentialId = Guid.NewGuid() }]
            }
        };
        Assert.True((await Store.SaveAsync(modified, loaded.Revision)).Saved);
        var bytes = await File.ReadAllBytesAsync(Store.FilePath);
        var stale = await Store.SaveAsync(initial, loaded.Revision);
        Assert.False(stale.Saved);
        Assert.Equal(ErrorCode.SettingsConflict, stale.Error!.Code);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Store.FilePath));
        Assert.False((await Store.SaveAsync(initial, null)).Saved);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData("{", ErrorCode.SettingsMalformed)]
    [InlineData("null", ErrorCode.SettingsMalformed)]
    [InlineData("{\"schema_version\":5,\"new_setting\":true}", ErrorCode.UnsupportedVersion)]
    [InlineData("{\"schema_version\":0}", ErrorCode.UnsupportedVersion)]
    [InlineData("{\"schema_version\":1,\"profile\":{\"schema_version\":2,\"new_setting\":true}}", ErrorCode.UnsupportedVersion)]
    [InlineData("{\"schema_version\":1,\"schema_version\":2}", ErrorCode.SettingsMalformed)]
    public async Task InvalidFilesAreActionableAndNeverOverwritten(string json, ErrorCode expected)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Store.FilePath, json);
        var before = await File.ReadAllBytesAsync(Store.FilePath);
        var result = await Store.LoadAsync();
        Assert.Equal(SettingsLoadState.Invalid, result.State);
        Assert.Equal(expected, result.Error!.Code);
        Assert.Null(result.Settings);
        var saved = await Store.SaveAsync(AppSettings.CreateUnconfigured(), null);
        Assert.False(saved.Saved);
        Assert.Equal(before, await File.ReadAllBytesAsync(Store.FilePath));
    }

    [Theory]
    [InlineData("{\"\\uD800\":0}")]
    [InlineData("{\"\\uDC00\":0}")]
    [InlineData("{\"PRIVATE-CANARY\\uD800\":0}")]
    [InlineData("{\"nested\":{\"\\uD800\":0}}")]
    [InlineData("{\"unknown\":\"\\uD800\"}")]
    public async Task MalformedUnicodeIsInvalidAndPreserved(string json) =>
        await AssertMalformedEncodingIsPreserved(Encoding.UTF8.GetBytes(json));

    [Fact]
    public async Task MalformedUtf8PropertyNameIsInvalidAndPreserved() =>
        await AssertMalformedEncodingIsPreserved([(byte)'{', (byte)'"', 0xED, 0xA0, 0x80, (byte)'"', (byte)':', (byte)'0', (byte)'}']);

    private async Task AssertMalformedEncodingIsPreserved(byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Store.FilePath, bytes);
        var loaded = await Store.LoadAsync();
        Assert.Equal(SettingsLoadState.Invalid, loaded.State);
        Assert.Equal(ErrorCode.SettingsMalformed, loaded.Error!.Code);
        Assert.DoesNotContain("PRIVATE-CANARY", loaded.Error.Summary);
        Assert.Null(loaded.Settings);
        var saved = await Store.SaveAsync(AppSettings.CreateUnconfigured(), null);
        Assert.False(saved.Saved);
        Assert.Equal(ErrorCode.SettingsMalformed, saved.Error!.Code);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Store.FilePath));
    }

    [Fact]
    public async Task OversizedFileAndInvalidSavePreserveOriginal()
    {
        Directory.CreateDirectory(directory);
        var bytes = new byte[AppSettings.MaxFileBytes + 1];
        await File.WriteAllBytesAsync(Store.FilePath, bytes);
        Assert.Equal(ErrorCode.SettingsMalformed, (await Store.LoadAsync()).Error!.Code);
        var invalid = AppSettings.CreateUnconfigured() with { SchemaVersion = 99 };
        Assert.False((await Store.SaveAsync(invalid, null)).Saved);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Store.FilePath));
    }

    [Fact]
    public async Task FileLocksReportInaccessibleWithoutDiscardingSettings()
    {
        await Store.SaveAsync(AppSettings.CreateUnconfigured(), null);
        var loaded = await Store.LoadAsync();
        using (var locked = new FileStream(Store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(SettingsLoadState.Inaccessible, (await Store.LoadAsync()).State);
            Assert.False((await Store.SaveAsync(AppSettings.CreateUnconfigured(), loaded.Revision)).Saved);
        }
        Assert.Equal(loaded.Revision, (await Store.LoadAsync()).Revision);
        using (var locked = new FileStream(Store.FilePath + ".lock", FileMode.Open, FileAccess.Write, FileShare.None))
        {
            var saved = await Store.SaveAsync(AppSettings.CreateUnconfigured(), loaded.Revision);
            Assert.False(saved.Saved);
            Assert.Equal(ErrorCode.SettingsInaccessible, saved.Error!.Code);
        }
    }

    [Fact]
    public async Task InvalidStorageLocationsDoNotBecomeDefaults()
    {
        Directory.CreateDirectory(Store.FilePath);
        Assert.Equal(SettingsLoadState.Inaccessible, (await Store.LoadAsync()).State);
        Assert.False((await Store.SaveAsync(AppSettings.CreateUnconfigured(), null)).Saved);
    }

    [Fact]
    public async Task FileInAncestorPathIsNotFirstRun()
    {
        Directory.CreateDirectory(directory);
        var blocker = Path.Combine(directory, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "preserve");
        var store = new SettingsStore(Path.Combine(blocker, "data"));
        Assert.Equal(SettingsLoadState.Inaccessible, (await store.LoadAsync()).State);
        Assert.False((await store.SaveAsync(AppSettings.CreateUnconfigured(), null)).Saved);
        Assert.Equal("preserve", await File.ReadAllTextAsync(blocker));
    }
    [Fact]
    public async Task FailedReplaceAndCancellationPreserveData()
    {
        await Store.SaveAsync(AppSettings.CreateUnconfigured(), null);
        var before = await Store.LoadAsync();
        // Permit validation reads but deny the atomic replacement on Windows.
        if (OperatingSystem.IsWindows())
        {
            using var locked = new FileStream(Store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.False((await Store.SaveAsync(AppSettings.CreateUnconfigured(), before.Revision)).Saved);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.SaveAsync(AppSettings.CreateUnconfigured(), before.Revision, cancellation.Token));
        Assert.Equal(before.Revision, (await Store.LoadAsync()).Revision);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void SchemaRejectsRawSecretFieldsAndUnknownProperties()
    {
        var json = Encoding.UTF8.GetString(ContractJson.Write(AppSettings.CreateUnconfigured()));
        foreach (var name in new[] { "api_key", "capture_on_startup", "unknown_option" })
        {
            var altered = json.Replace("\"schema_version\": 1,", $"\"schema_version\": 1, \"{name}\": \"PRIVATE-CANARY\",", StringComparison.Ordinal);
            var error = Assert.Throws<ContractException>(() => SettingsJson.Read(Encoding.UTF8.GetBytes(altered)));
            Assert.DoesNotContain("PRIVATE-CANARY", error.Message);
        }
    }

    [Fact]
    public void ReferencesAndProfilesEnforceBounds()
    {
        var settings = AppSettings.CreateUnconfigured();
        Assert.Throws<ContractException>(() => (settings with { Profile = settings.Profile with { Id = Guid.Empty } }).Validate());
        Assert.Throws<ContractException>(() => (settings with { Profile = settings.Profile with { Kind = (ProfileKind)999 } }).Validate());
        var reference = new SecretReference { ProviderId = "test", CredentialId = Guid.NewGuid() };
        Assert.Throws<ContractException>(() => (settings.Profile with { Credentials = [reference, reference] }).Validate());
        Assert.Throws<ContractException>(() => (reference with { ProviderId = new string('a', 65) }).Validate());
        Assert.Throws<ContractException>(() => (reference with { CredentialId = Guid.Empty }).Validate());
        Assert.Throws<ContractException>(() => (settings.Profile with { Credentials = Enumerable.Range(0, 17).Select(i => reference with { ProviderId = $"p{i}" }).ToArray() }).Validate());
        Assert.Throws<ArgumentException>(() => new SettingsStore("relative-path"));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
