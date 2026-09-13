using System.Text.Json.Nodes;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class ConfigurationSnapshotInspectionTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("Martlet.Inspection-").FullName;

    [Fact]
    public async Task ActualSnapshotHasImmutableTypedIdentityWithoutPayloadOrIo()
    {
        var store = new SettingsStore(root);
        var settings = AppSettings.CreateUnconfigured();
        var saved = await store.SaveAsync(settings, null);
        Assert.True(saved.Saved);
        var path = Path.Combine(root, "snapshot");
        var receipt = await store.CreateConfigurationSnapshotAsync(path);
        var bytes = await File.ReadAllBytesAsync(path);
        var before = Directory.GetFileSystemEntries(root);
        var inspection = ConfigurationSnapshot.Inspect(bytes);
        Assert.Equal(settings.Profile.Id, inspection.ProfileId);
        Assert.Equal(settings.SchemaVersion, inspection.SettingsSchemaVersion);
        Assert.Equal(saved.Revision, inspection.SourceRevision);
        Assert.Equal(saved.Revision!.ToUpperInvariant(), inspection.SourceRevision);
        Assert.Equal(receipt.Revision, inspection.FileDigest);
        Assert.Equal(ConfigurationSnapshot.Read(bytes).Sha256, inspection.ManifestDigest);
        Assert.NotEqual(Guid.Empty, inspection.SnapshotId);
        bytes.AsSpan().Fill(0);
        Assert.Equal(receipt.Revision, inspection.FileDigest);
        Assert.Equal(before, Directory.GetFileSystemEntries(root));
        Assert.Empty(typeof(ConfigurationSnapshotInspection).GetConstructors());
        Assert.All(typeof(ConfigurationSnapshotInspection).GetProperties(), property =>
        {
            Assert.Null(property.SetMethod);
            Assert.Contains(property.PropertyType, new[] { typeof(Guid), typeof(string), typeof(int) });
        });
        Assert.Equal(saved.Revision, (await store.LoadAsync()).Revision);
    }

    [Theory]
    [InlineData("malformed", RecoveryFailure.InvalidBackup)]
    [InlineData("tampered", RecoveryFailure.InvalidBackup)]
    [InlineData("oversize", RecoveryFailure.InvalidBackup)]
    [InlineData("future", RecoveryFailure.Incompatible)]
    [InlineData("case", RecoveryFailure.InvalidBackup)]
    public async Task InvalidEnvelopeDoesNotBecomeEvidence(string scenario, RecoveryFailure expected)
    {
        var store = new SettingsStore(root);
        Assert.True((await store.SaveAsync(AppSettings.CreateUnconfigured(), null)).Saved);
        var path = Path.Combine(root, "snapshot");
        await store.CreateConfigurationSnapshotAsync(path);
        var bytes = await File.ReadAllBytesAsync(path);
        var document = JsonNode.Parse(bytes)!;
        switch (scenario)
        {
            case "malformed": bytes = "{"u8.ToArray(); break;
            case "oversize": bytes = new byte[ConfigurationSnapshot.MaximumBytes + 1]; break;
            case "future":
                document["manifest"]!["format_version"] = 2;
                bytes = System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()); break;
            case "case":
                document["manifest"]!["source_revision"] =
                    document["manifest"]!["source_revision"]!.GetValue<string>().ToLowerInvariant();
                bytes = System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()); break;
            default:
                document["sha256"] = new string('0', 64);
                bytes = System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()); break;
        }
        Assert.Equal(expected, Assert.Throws<RecoveryException>(() => ConfigurationSnapshot.Inspect(bytes)).Failure);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
