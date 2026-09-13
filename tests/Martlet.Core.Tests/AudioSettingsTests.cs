using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Diagnostics;

namespace Martlet.Core.Tests;

public sealed class AudioSettingsTests
{
    [Fact]
    public void SelectionChangeInvalidatesOnlyChangedCheckpointAndReportsNoIdentity()
    {
        var audio = AudioSettings.Create();
        var input = audio.Input.Select("PRIVATE-ENDPOINT", "PRIVATE-LABEL");
        input = input with { Checkpoint = new() { ConfigurationRevision = input.ConfigurationRevision,
            TestedAt = DateTimeOffset.UtcNow, Outcome = LocalAudioOutcome.SamplesReceived } };
        audio = audio with { Input = input };
        var settings = SetupSettings.Begin(null) with { Audio = audio };
        var decoded = SettingsJson.Read(ContractJson.Write(settings));
        Assert.Equal(audio, decoded.Audio);
        Assert.Equal(input, input.Select(input.EndpointId, input.DisplayName));
        Assert.Null(input.Select("NEW", "New").Checkpoint);
        var status = SetupStatus.From(settings)!;
        var json = Encoding.UTF8.GetString(ContractJson.Write(status));
        Assert.DoesNotContain("PRIVATE", json);
        Assert.DoesNotContain(input.ConfigurationRevision.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("STALE", status.Describe());
        Assert.Contains("not fixture", AudioSetupDiagnostics.Describe(status.Audio));
    }

    [Fact]
    public async Task MigrationSnapshotsExactV1AndPreservesV2RoutesAndConflicts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Audio.Settings." + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(directory);
            var original = AppSettings.CreateUnconfigured();
            original = original with { Profile = original.Profile with { Credentials = [new() { ProviderId = "legacy", CredentialId = Guid.NewGuid() }] } };
            var saved = await store.SaveAsync(original, null);
            var bytes = await File.ReadAllBytesAsync(store.FilePath);
            var next = SetupSettings.SelectRoute(SetupSettings.Begin(original), SetupRole.Llm, "model", null) with { Audio = AudioSettings.Create() };
            var migrated = await store.SaveAsync(next, saved.Revision);
            Assert.True(migrated.Saved);
            Assert.True(migrated.MigratedFromVersion1);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(directory, migrated.SnapshotFileName!)));
            Assert.False((await store.SaveAsync(next, saved.Revision)).Saved);
            var loaded = (await store.LoadAsync()).Settings!;
            Assert.Equal(original.Profile.Id, loaded.Profile.Id);
            Assert.Equal(original.Profile.Kind, loaded.Profile.Kind);
            Assert.Equal(original.Profile.Credentials, loaded.Profile.Credentials);
            Assert.Equal(next.Setup!.Routes.Single(), loaded.Setup!.Routes.Single());
            var reused = loaded with { Audio = loaded.Audio! with { Output = loaded.Audio!.Output with { EndpointId = "changed-without-revision" } } };
            Assert.False((await store.SaveAsync(reused, migrated.Revision)).Saved);
            var update = loaded with { Audio = loaded.Audio! with { Output = loaded.Audio!.Output.Select("new", "Headset") } };
            Assert.True((await store.SaveAsync(update, migrated.Revision)).Saved);
            Assert.Equal(loaded.Setup.Routes, (await store.LoadAsync()).Settings!.Setup!.Routes);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("\"schema_version\": 1", "\"schema_version\": 99")]
    [InlineData("\"schema_version\": 1", "\"unexpected\": true,\"schema_version\": 1")]
    public async Task FutureOrUnknownAudioSettingsAreNeverReset(string from, string to)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Audio.Invalid." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var store = new SettingsStore(directory);
            var settings = SetupSettings.Begin(null) with { Audio = AudioSettings.Create() };
            var json = Encoding.UTF8.GetString(ContractJson.Write(settings));
            var audioStart = json.IndexOf("\"audio\":", StringComparison.Ordinal);
            var bytes = Encoding.UTF8.GetBytes(json[..audioStart] + json[audioStart..].Replace(from, to, StringComparison.Ordinal));
            await File.WriteAllBytesAsync(store.FilePath, bytes);
            Assert.NotNull((await store.LoadAsync()).Error);
            Assert.False((await store.SaveAsync(settings, null)).Saved);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(store.FilePath));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void CheckpointCannotBeReusedAcrossConfigurationOrStage()
    {
        var audio = AudioSettings.Create();
        var checkpoint = new AudioCheckpoint { ConfigurationRevision = audio.Input.ConfigurationRevision,
            TestedAt = DateTimeOffset.UtcNow, Outcome = LocalAudioOutcome.Heard };
        Assert.Throws<ContractException>(() => (audio with { Input = audio.Input with { Checkpoint = checkpoint } }).Validate());
        Assert.Throws<ContractException>(() => (audio.Output with { Checkpoint = checkpoint }).Validate());
        Assert.Throws<ContractException>(() => (audio.Input with { DisplayName = new string('x', 257) }).Validate());
        Assert.Throws<ContractException>(() => (AppSettings.CreateUnconfigured() with { Audio = audio }).Validate());
    }
}
