using System.Text;
using System.Text.Json.Nodes;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;

namespace Martlet.Core.Tests;

public sealed class ConfigurationRecoveryTests : IDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "recovery-fixtures", Guid.NewGuid().ToString("N"));
    private SettingsStore Store => new(directory);
    private string Backup => Path.Combine(directory, "selected.martlet-config");
    private static ConfigurationRestoreApproval Approve(ConfigurationRestorePlan plan) =>
        plan.Approve(plan.SnapshotDigest, plan.Destination, plan.ExpectedRevision);

    private async Task<AppSettings> Prepare()
    {
        var settings = SetupSettings.SelectRoute(SetupSettings.Begin(null), SetupRole.Tts, "gpt-4o-mini-tts", "coral");
        var audio = AudioSettings.Create();
        audio = audio with
        {
            Input = audio.Input.Select("private-mic-id", "My private microphone"),
            Output = audio.Output.Select("private-output-id", "My headphones")
        };
        audio = audio with { Output = audio.Output with { Checkpoint = new()
        {
            ConfigurationRevision = audio.Output.ConfigurationRevision, TestedAt = DateTimeOffset.UtcNow, Outcome = LocalAudioOutcome.Heard
        } } };
        settings = settings with { Audio = audio, Profile = settings.Profile with { Kind = ProfileKind.Api } };
        var route = settings.Setup!.Routes.Single();
        settings = SetupSettings.ReplaceRoute(settings, route with { Consent = route.Selection() });
        Assert.True((await Store.SaveAsync(settings, null)).Saved);
        return settings;
    }

    [Fact]
    public async Task ExactValidatedSourceAndDeterministicManifestExcludeOtherData()
    {
        await Prepare();
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        // Valid noncanonical input must remain lossless inside the envelope.
        original = Encoding.UTF8.GetBytes("\n" + Encoding.UTF8.GetString(original) + "\n");
        await File.WriteAllBytesAsync(Store.FilePath, original);
        await File.WriteAllTextAsync(Path.Combine(directory, "transcript.txt"), "PRIVATE-EXCLUDED-CANARY");
        var receipt = await Store.CreateConfigurationSnapshotAsync(Backup);
        var bytes = await File.ReadAllBytesAsync(Backup);
        var envelope = ConfigurationSnapshot.Read(bytes);
        Assert.Equal(original, envelope.Manifest.SettingsBytes);
        Assert.Equal(bytes, ContractJson.Write(envelope, ConfigurationSnapshot.MaximumBytes));
        Assert.Equal(ConfigurationSnapshot.Hash(original), envelope.Manifest.SourceRevision);
        Assert.Equal(ConfigurationSnapshot.Hash(bytes), receipt.Revision);
        Assert.DoesNotContain("PRIVATE-EXCLUDED-CANARY", Encoding.UTF8.GetString(bytes));
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
        await Fails(RecoveryFailure.Unavailable, () => Store.CreateConfigurationSnapshotAsync(Backup));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Backup));
    }

    [Fact]
    public async Task ActualSetupAndWindowsBoundaryRestorePreferencesButNeverReviveRevokedCredentials()
    {
        var settings = await Prepare();
        using var native = new FakeCredentialNative();
        var setup = new SetupService(Store, new WindowsCredentialStore(native));
        using var secret = new SecretLease("SYNTHETIC-SECRET-NOT-IN-BACKUP");
        var first = await setup.ReplaceCredentialAsync(settings, (await Store.LoadAsync()).Revision, SetupRole.Tts, secret);
        Assert.True(first.Save.Saved);
        await Store.CreateConfigurationSnapshotAsync(Backup);
        var revoked = first.Settings.Setup!.Routes.Single().CredentialId!.Value;
        var detached = await setup.DetachCredentialAsync(first.Settings, first.Save.Revision, SetupRole.Tts);
        var removed = await setup.RemoveDetachedAsync(detached.Settings, detached.Save.Revision, detached.Settings.Setup!.PendingRemovals.Single());
        Assert.True(removed.Save.Saved);
        var second = await setup.ReplaceCredentialAsync(removed.Settings, removed.Save.Revision, SetupRole.Tts, secret);
        var third = await setup.ReplaceCredentialAsync(second.Settings, second.Save.Revision, SetupRole.Tts, secret);
        Assert.True(third.Save.Saved);
        var changed = SetupSettings.SelectRoute(third.Settings, SetupRole.Tts, "tts-1", "alloy") with
        {
            Audio = third.Settings.Audio! with { Output = third.Settings.Audio!.Output.Select("other-output", "Other output") }
        };
        Assert.True((await setup.SaveAsync(changed, third.Save.Revision)).Save.Saved);
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        native.Events.Clear();
        var plan = await Store.PreviewConfigurationRestoreAsync(Backup);
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
        var candidate = SettingsJson.Read(Encoding.UTF8.GetBytes(plan.CandidateJson));
        var receipt = await Store.RestoreConfigurationAsync(plan, Approve(plan));
        Assert.Equal(original, await File.ReadAllBytesAsync(receipt.OriginalSnapshot!));
        Assert.Equal(Encoding.UTF8.GetBytes(plan.CandidateJson), await File.ReadAllBytesAsync(Store.FilePath));
        var restored = (await Store.LoadAsync()).Settings!;
        var route = restored.Setup!.Routes.Single();
        Assert.Equal("gpt-4o-mini-tts", route.ModelId);
        Assert.Equal("coral", route.VoiceId);
        Assert.Null(route.CredentialId);
        Assert.Null(route.Consent);
        Assert.NotEqual(settings.Setup!.Routes.Single().ConfigurationRevision, route.ConfigurationRevision);
        Assert.Equal(SetupStep.Destinations, restored.Setup.Checkpoint);
        Assert.Equal(settings.Audio!.Output.EndpointId, restored.Audio!.Output.EndpointId);
        Assert.Equal(settings.Audio.Input.DisplayName, restored.Audio.Input.DisplayName);
        Assert.Null(restored.Audio.Output.Checkpoint);
        Assert.NotEqual(settings.Audio.Output.ConfigurationRevision, restored.Audio.Output.ConfigurationRevision);
        Assert.Equal(candidate.Audio, restored.Audio);
        Assert.Equal(2, restored.Setup.PendingRemovals.Count);
        Assert.DoesNotContain(restored.Setup.PendingRemovals, item => item.CredentialId == revoked);
        Assert.Contains(restored.Setup.PendingRemovals, item => item.CredentialId == second.Settings.Setup!.Routes.Single().CredentialId);
        Assert.Contains(restored.Setup.PendingRemovals, item => item.CredentialId == third.Settings.Setup!.Routes.Single().CredentialId);
        Assert.Empty(native.Events);
        Assert.Equal(2, native.Keys.Count);
        Assert.Equal(CredentialError.Missing, setup.CheckCredential(restored, SetupRole.Tts));
        Assert.Empty(native.Events);
        Assert.DoesNotContain("SYNTHETIC-SECRET-NOT-IN-BACKUP", plan.Summary + plan.CandidateJson +
            Encoding.UTF8.GetString(ConfigurationSnapshot.Read(await File.ReadAllBytesAsync(Backup)).Manifest.SettingsBytes));
        var staleDelete = await setup.RemoveDetachedAsync(third.Settings, third.Save.Revision, third.Settings.Setup!.PendingRemovals.Single());
        Assert.False(staleDelete.Save.Saved);
        Assert.Empty(native.Events);
    }

    [Theory]
    [InlineData("truncated", RecoveryFailure.InvalidBackup)]
    [InlineData("oversized", RecoveryFailure.InvalidBackup)]
    [InlineData("tampered", RecoveryFailure.InvalidBackup)]
    [InlineData("duplicate", RecoveryFailure.InvalidBackup)]
    [InlineData("unknown", RecoveryFailure.InvalidBackup)]
    [InlineData("wrong-model", RecoveryFailure.Incompatible)]
    [InlineData("future", RecoveryFailure.Incompatible)]
    [InlineData("future-settings", RecoveryFailure.Incompatible)]
    [InlineData("wrong-profile", RecoveryFailure.WrongProfile)]
    [InlineData("raw-settings", RecoveryFailure.InvalidBackup)]
    [InlineData("malformed", RecoveryFailure.InvalidBackup)]
    public async Task SnapshotRefusalMatrixPreservesDestination(string scenario, RecoveryFailure failure)
    {
        var settings = await Prepare();
        await Store.CreateConfigurationSnapshotAsync(Backup);
        var text = await File.ReadAllTextAsync(Backup);
        var node = JsonNode.Parse(text)!;
        var bytes = scenario switch
        {
            "truncated" => Encoding.UTF8.GetBytes(text[..^5]),
            "oversized" => new byte[ConfigurationSnapshot.MaximumBytes + 1],
            "tampered" => Encoding.UTF8.GetBytes(text.Replace("\"producer_version\": \"0.1.0.0\"", "\"producer_version\": \"0.2.0.0\"", StringComparison.Ordinal)),
            "duplicate" => Encoding.UTF8.GetBytes(text.Replace("\"format_version\": 1,", "\"format_version\": 1, \"format_version\": 1,", StringComparison.Ordinal)),
            "unknown" => Encoding.UTF8.GetBytes(text.Replace("\"format_version\": 1,", "\"format_version\": 1, \"secret\": \"PRIVATE-CANARY\",", StringComparison.Ordinal)),
            "wrong-model" => Change("application", "AnotherProduct"),
            "future" => Change("format_version", 999),
            "future-settings" => Change("settings_schema_version", 99),
            "wrong-profile" => ConfigurationSnapshot.Create(ContractJson.Write(settings with { Profile = settings.Profile with { Id = Guid.NewGuid() } })),
            "raw-settings" => ContractJson.Write(settings),
            _ => Encoding.UTF8.GetBytes("{\"\\uD800\":0}")
        };
        await File.WriteAllBytesAsync(Backup, bytes);
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        var error = await Fails(failure, () => Store.PreviewConfigurationRestoreAsync(Backup));
        Assert.DoesNotContain("PRIVATE-CANARY", error.ToString());
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
        Assert.Empty(Directory.GetFiles(directory, "settings.recovery.*.bak"));
        byte[] Change<T>(string property, T value)
        {
            node["manifest"]![property] = JsonValue.Create(value);
            return Encoding.UTF8.GetBytes(node.ToJsonString());
        }
    }

    [Theory]
    [InlineData("source")]
    [InlineData("revision")]
    [InlineData("store")]
    [InlineData("replay")]
    [InlineData("approval")]
    public async Task ExactPlanConsentAndRevisionCannotBeReusedOrRedirected(string change)
    {
        var settings = await Prepare();
        await Store.CreateConfigurationSnapshotAsync(Backup);
        var plan = await Store.PreviewConfigurationRestoreAsync(Backup);
        var approval = Approve(plan);
        if (change == "source") await File.AppendAllTextAsync(Backup, "\n");
        if (change == "revision") Assert.True((await Store.SaveAsync(settings with { Profile = settings.Profile with { Kind = ProfileKind.Fixture } }, plan.ExpectedRevision)).Saved);
        if (change == "replay") await Store.RestoreConfigurationAsync(plan, approval);
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        if (change == "approval")
            Assert.Equal(RecoveryFailure.Conflict, Assert.Throws<RecoveryException>(() => Approve(plan)).Failure);
        else
            await Fails(RecoveryFailure.Conflict, () => (change == "store" ? new SettingsStore(Path.Combine(directory, "other")) : Store)
                .RestoreConfigurationAsync(plan, approval));
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"schema_version\":99,\"future\":true}")]
    [InlineData("")]
    public async Task RefusesDamagedFutureAndMissingDestinationRatherThanRepair(string contents)
    {
        await Prepare();
        await Store.CreateConfigurationSnapshotAsync(Backup);
        if (contents.Length == 0) File.Delete(Store.FilePath);
        else await File.WriteAllTextAsync(Store.FilePath, contents);
        await Fails(contents.Contains("99", StringComparison.Ordinal) ? RecoveryFailure.Incompatible : RecoveryFailure.Unavailable,
            () => Store.PreviewConfigurationRestoreAsync(Backup));
        if (contents.Length == 0) Assert.False(File.Exists(Store.FilePath));
        else Assert.Equal(contents, await File.ReadAllTextAsync(Store.FilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocksSerializeRecoveryWithProductionVaultTransactions(bool write)
    {
        var settings = await Prepare();
        await Store.CreateConfigurationSnapshotAsync(Backup);
        var plan = await Store.PreviewConfigurationRestoreAsync(Backup);
        using var native = new FakeCredentialNative();
        using var secret = new SecretLease("SYNTHETIC-KEY");
        var setup = new SetupService(Store, new WindowsCredentialStore(native));
        var attempted = false;
        native.OnWrite = () =>
        {
            attempted = true;
            Fails(RecoveryFailure.Unavailable, () => write ? Store.CreateConfigurationSnapshotAsync(Backup + ".new") :
                Store.RestoreConfigurationAsync(plan, Approve(plan))).GetAwaiter().GetResult();
        };
        Assert.True((await setup.ReplaceCredentialAsync(settings, plan.ExpectedRevision, SetupRole.Tts, secret)).Save.Saved);
        Assert.True(attempted);
        Assert.Single(native.Keys);
    }

    [Theory]
    [InlineData(false, "BeforeStage")]
    [InlineData(false, "AfterWrite")]
    [InlineData(false, "BeforeFlush")]
    [InlineData(false, "AfterFlush")]
    [InlineData(false, "BeforeCommit")]
    [InlineData(true, "BeforeStage")]
    [InlineData(true, "AfterWrite")]
    [InlineData(true, "BeforeFlush")]
    [InlineData(true, "AfterFlush")]
    [InlineData(true, "BeforeCommit")]
    public async Task InjectedSnapshotAndRestoreFailuresPreserveExactOriginals(bool restore, string point)
    {
        await Prepare();
        await Store.CreateConfigurationSnapshotAsync(Backup);
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        var snapshotBytes = await File.ReadAllBytesAsync(Backup);
        var plan = await Store.PreviewConfigurationRestoreAsync(Backup);
        var stages = 0;
        var failing = new SettingsStore(directory) { RecoveryIo = (step, _) =>
        {
            if (step == SettingsIoPoint.BeforeStage) stages++;
            if (step.ToString() == point && (!restore || stages == 2)) throw new IOException("PRIVATE-DISK-ERROR");
        } };
        await Fails(RecoveryFailure.Unavailable, () => restore ? failing.RestoreConfigurationAsync(plan, Approve(plan)) :
            failing.CreateConfigurationSnapshotAsync(Backup + ".new"));
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
        Assert.Equal(snapshotBytes, await File.ReadAllBytesAsync(Backup));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        if (restore)
            Assert.Equal(original, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(directory, "settings.recovery.*.bak"))));
    }

    [Fact]
    public async Task CanceledStagingAndCleanupFailureRetainExactOwnedFileUntilExplicitRetry()
    {
        await Prepare();
        using var cancel = new CancellationTokenSource();
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        var failCleanup = true;
        var store = new SettingsStore(directory) { RecoveryIo = (point, _) =>
        {
            if (point == SettingsIoPoint.AfterWrite) cancel.Cancel();
            if (point == SettingsIoPoint.BeforeCleanup && failCleanup) throw new UnauthorizedAccessException();
        } };
        var error = await Fails(RecoveryFailure.CleanupPending, () => store.CreateConfigurationSnapshotAsync(Backup, cancel.Token));
        Assert.NotNull(error.RetainedFile);
        Assert.True(File.Exists(error.RetainedFile));
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
        Assert.False(File.Exists(Backup));
        failCleanup = false;
        store.RetryRecoveryCleanup(error.RetainedFile);
        Assert.False(File.Exists(error.RetainedFile));
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
    }

    [Fact]
    public async Task StandaloneRestoreRetainsOwnedCleanupForExistingDesktopRetryContract()
    {
        await Prepare();
        await Store.CreateConfigurationSnapshotAsync(Backup);
        var plan = await Store.PreviewConfigurationRestoreAsync(Backup);
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        var source = await File.ReadAllBytesAsync(Backup);
        var stages = 0;
        var failCleanup = true;
        var store = new SettingsStore(directory) { RecoveryIo = (point, _) =>
        {
            if (point == SettingsIoPoint.BeforeStage) stages++;
            if (point == SettingsIoPoint.AfterWrite && stages == 2) throw new IOException();
            if (point == SettingsIoPoint.BeforeCleanup && failCleanup) throw new UnauthorizedAccessException();
        } };
        var error = await Fails(RecoveryFailure.CleanupPending, () => store.RestoreConfigurationAsync(plan, Approve(plan)));
        Assert.NotNull(error.RetainedFile);
        Assert.Equal(".tmp", Path.GetExtension(error.RetainedFile));
        Assert.True(File.Exists(error.RetainedFile));
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
        Assert.Equal(original, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(directory, "settings.recovery.*.bak"))));
        Assert.Equal(source, await File.ReadAllBytesAsync(Backup));
        var next = await store.PreviewConfigurationRestoreAsync(Backup);
        var blocked = await Fails(RecoveryFailure.CleanupPending, () => store.RestoreConfigurationAsync(next, Approve(next)));
        Assert.Equal(error.RetainedFile, blocked.RetainedFile);
        var nextScoped = await store.PreviewConfigurationRestoreAsync(Backup);
        await Fails(RecoveryFailure.CleanupPending, () => store.OpenConfigurationRestoreAsync(nextScoped, Approve(nextScoped)));
        Assert.Single(Directory.GetFiles(directory, "*.tmp"));
        Assert.Single(Directory.GetFiles(directory, "settings.recovery.*.bak"));
        Assert.Equal(RecoveryFailure.CleanupPending, Assert.Throws<RecoveryException>(() => store.RetryRecoveryCleanup(error.RetainedFile)).Failure);
        failCleanup = false;
        store.RetryRecoveryCleanup(error.RetainedFile);
        Assert.False(File.Exists(error.RetainedFile));
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
        Assert.Equal(source, await File.ReadAllBytesAsync(Backup));
        var fresh = await store.PreviewConfigurationRestoreAsync(Backup);
        Assert.Equal(fresh.CandidateDigest, (await store.RestoreConfigurationAsync(fresh, Approve(fresh))).Revision);
    }

    [Fact]
    public async Task DestinationMutationDuringStagingIsDeniedOrDetected()
    {
        var settings = await Prepare();
        await Store.CreateConfigurationSnapshotAsync(Backup);
        var plan = await Store.PreviewConfigurationRestoreAsync(Backup);
        var changed = ContractJson.Write(settings with { Profile = settings.Profile with { Kind = ProfileKind.Fixture } });
        var stages = 0;
        var store = new SettingsStore(directory) { RecoveryIo = (point, _) =>
        {
            if (point == SettingsIoPoint.BeforeStage) stages++;
            if (point == SettingsIoPoint.BeforeCommit && stages == 2) File.WriteAllBytes(Store.FilePath, changed);
        } };
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        // The live restore now pins the current target. Windows rejects this attempted write before it can change bytes.
        await Fails(OperatingSystem.IsWindows() ? RecoveryFailure.Unavailable : RecoveryFailure.Conflict,
            () => store.RestoreConfigurationAsync(plan, Approve(plan)));
        Assert.Equal(OperatingSystem.IsWindows() ? original : changed, await File.ReadAllBytesAsync(Store.FilePath));
    }

    [Fact]
    public async Task V1AndHistoricalMigrationSnapshotArePreservedAndCurrentLegacyAuthorityWins()
    {
        var legacy = AppSettings.CreateUnconfigured();
        legacy = legacy with { Profile = legacy.Profile with { Credentials = [new() { ProviderId = "old", CredentialId = Guid.NewGuid() }] } };
        Assert.True((await Store.SaveAsync(legacy, null)).Saved);
        await Store.CreateConfigurationSnapshotAsync(Backup);
        var current = legacy with { Profile = legacy.Profile with { Credentials = [new() { ProviderId = "current", CredentialId = Guid.NewGuid() }] } };
        Assert.True((await Store.SaveAsync(current, (await Store.LoadAsync()).Revision)).Saved);
        var migration = await Store.SaveAsync(SetupSettings.Begin(current), (await Store.LoadAsync()).Revision);
        var historical = await File.ReadAllBytesAsync(Path.Combine(directory, migration.SnapshotFileName!));
        var plan = await Store.PreviewConfigurationRestoreAsync(Backup);
        await Store.RestoreConfigurationAsync(plan, Approve(plan));
        Assert.Equal(current.Profile.Credentials, (await Store.LoadAsync()).Settings!.Profile.Credentials);
        Assert.Equal(historical, await File.ReadAllBytesAsync(Path.Combine(directory, migration.SnapshotFileName!)));
    }

    [Fact]
    public async Task RestoreIntoValidV1PreservesRawOriginalAndNeverOverwritesEarlierRecovery()
    {
        var settings = AppSettings.CreateUnconfigured();
        Assert.True((await Store.SaveAsync(settings, null)).Saved);
        await Store.CreateConfigurationSnapshotAsync(Backup);
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        var plan = await Store.PreviewConfigurationRestoreAsync(Backup);
        var first = await Store.RestoreConfigurationAsync(plan, Approve(plan));
        Assert.Equal(AppSettings.CurrentSchemaVersion, (await Store.LoadAsync()).Settings!.SchemaVersion);
        Assert.Equal(original, await File.ReadAllBytesAsync(first.OriginalSnapshot!));
        var afterFirst = await File.ReadAllBytesAsync(Store.FilePath);
        plan = await Store.PreviewConfigurationRestoreAsync(Backup);
        var second = await Store.RestoreConfigurationAsync(plan, Approve(plan));
        Assert.NotEqual(first.OriginalSnapshot, second.OriginalSnapshot);
        Assert.Equal(original, await File.ReadAllBytesAsync(first.OriginalSnapshot!));
        Assert.Equal(afterFirst, await File.ReadAllBytesAsync(second.OriginalSnapshot!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealSharingDenialPreservesCurrentAndCreateOnlyTargets(bool restore)
    {
        if (!OperatingSystem.IsWindows()) return;
        await Prepare();
        await Store.CreateConfigurationSnapshotAsync(Backup);
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        var backup = await File.ReadAllBytesAsync(Backup);
        var plan = await Store.PreviewConfigurationRestoreAsync(Backup);
        using (var locked = new FileStream(restore ? Store.FilePath : Backup,
            FileMode.Open, FileAccess.Read, FileShare.Read))
            await Fails(RecoveryFailure.Unavailable, () => restore ? Store.RestoreConfigurationAsync(plan, Approve(plan)) :
                Store.CreateConfigurationSnapshotAsync(Backup));
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
        Assert.Equal(backup, await File.ReadAllBytesAsync(Backup));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task CommitPinsFrozenSourceAgainstWindowsMutationAndRename()
    {
        if (!OperatingSystem.IsWindows()) return;
        await Prepare();
        await Store.CreateConfigurationSnapshotAsync(Backup);
        var plan = await Store.PreviewConfigurationRestoreAsync(Backup);
        var pinned = false;
        var store = new SettingsStore(directory) { RecoveryIo = (point, _) =>
        {
            if (point != SettingsIoPoint.BeforeCommit) return;
            Assert.Throws<IOException>(() => File.AppendAllText(Backup, "\n"));
            Assert.Throws<IOException>(() => File.Move(Backup, Backup + ".moved"));
            pinned = true;
        } };
        await store.RestoreConfigurationAsync(plan, Approve(plan));
        Assert.True(pinned);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptionAfterReplacementKeepsRecoverableOriginal(bool cancel)
    {
        await Prepare();
        await Store.CreateConfigurationSnapshotAsync(Backup);
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        var plan = await Store.PreviewConfigurationRestoreAsync(Backup);
        using var stop = new CancellationTokenSource();
        var commits = 0;
        var store = new SettingsStore(directory) { RecoveryIo = (point, _) =>
        {
            if (point != SettingsIoPoint.AfterCommit || ++commits != 2) return;
            if (cancel) stop.Cancel();
            else throw new IOException("Controlled post-replace interruption");
        } };
        if (cancel)
            Assert.Equal(plan.CandidateDigest, (await store.RestoreConfigurationAsync(plan, Approve(plan), stop.Token)).Revision);
        else await Fails(RecoveryFailure.Unavailable, () => store.RestoreConfigurationAsync(plan, Approve(plan)));
        Assert.Equal(original, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(directory, "settings.recovery.*.bak"))));
        Assert.Equal(plan.CandidateJson, await File.ReadAllTextAsync(Store.FilePath));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task CanceledRestoreRetiresOwnedScratchWithoutRequiringANewCleanupAction()
    {
        await Prepare();
        await Store.CreateConfigurationSnapshotAsync(Backup);
        var plan = await Store.PreviewConfigurationRestoreAsync(Backup);
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        using var stop = new CancellationTokenSource();
        var stages = 0;
        var store = new SettingsStore(directory) { RecoveryIo = (point, _) =>
        {
            if (point == SettingsIoPoint.BeforeStage) stages++;
            if (stages == 2 && point == SettingsIoPoint.AfterWrite) stop.Cancel();
        } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.RestoreConfigurationAsync(plan, Approve(plan), stop.Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
        Assert.Equal(original, await File.ReadAllBytesAsync(Assert.Single(
            Directory.GetFiles(directory, "settings.recovery.*.bak"))));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("future")]
    [InlineData("duplicate")]
    public async Task DecodedPayloadIsStrictRatherThanJustAnIntegrityCheckedBlob(string mutation)
    {
        await Prepare();
        await Store.CreateConfigurationSnapshotAsync(Backup);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(Backup))!;
        var manifest = node["manifest"]!;
        var payload = Encoding.UTF8.GetString(Convert.FromBase64String(manifest["settings_bytes"]!.GetValue<string>()));
        payload = mutation switch
        {
            "unknown" => payload.Replace("\"schema_version\": 3,", "\"schema_version\": 3,\"unexpected\":true,", StringComparison.Ordinal),
            "future" => payload.Replace("\"schema_version\": 3,", "\"schema_version\": 4,", StringComparison.Ordinal),
            _ => payload.Replace("\"schema_version\": 3,", "\"schema_version\": 3,\"schema_version\": 3,", StringComparison.Ordinal)
        };
        var bytes = Encoding.UTF8.GetBytes(payload);
        manifest["settings_bytes"] = Convert.ToBase64String(bytes);
        manifest["source_revision"] = ConfigurationSnapshot.Hash(bytes);
        await File.WriteAllTextAsync(Backup, node.ToJsonString());
        await Fails(mutation == "future" ? RecoveryFailure.Incompatible : RecoveryFailure.InvalidBackup,
            () => Store.PreviewConfigurationRestoreAsync(Backup));
        Assert.Empty(Directory.GetFiles(directory, "settings.recovery.*.bak"));
    }

    [Fact]
    public async Task CleanupCapacityRefusesInsteadOfDroppingOwnedKeys()
    {
        var settings = await Prepare();
        await Store.CreateConfigurationSnapshotAsync(Backup);
        var route = settings.Setup!.Routes.Single().WithCredential(Guid.NewGuid());
        settings = SetupSettings.ReplaceRoute(settings, route);
        settings = settings with { Setup = settings.Setup! with
        {
            PendingRemovals = Enumerable.Range(0, 16).Select(_ => new PendingCredentialRemoval { Role = SetupRole.Tts, CredentialId = Guid.NewGuid() }).ToArray()
        } };
        Assert.True((await Store.SaveAsync(settings, (await Store.LoadAsync()).Revision)).Saved);
        await Fails(RecoveryFailure.CleanupCapacity, () => Store.PreviewConfigurationRestoreAsync(Backup));
        Assert.Equal(16, (await Store.LoadAsync()).Settings!.Setup!.PendingRemovals.Count);
    }

    private static async Task<RecoveryException> Fails(RecoveryFailure failure, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<RecoveryException>(action);
        Assert.Equal(failure, error.Failure);
        return error;
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
