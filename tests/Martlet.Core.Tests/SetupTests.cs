using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Diagnostics;

namespace Martlet.Core.Tests;

public sealed class SetupTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Martlet.Setup.Tests", Guid.NewGuid().ToString("N"));
    private readonly FakeCredentialNative native = new();
    private SettingsStore Store => new(directory);
    private SetupService Service => new(Store, new WindowsCredentialStore(native));
    private const string Canary = "PRIVATE-SECRET-CANARY-DO-NOT-LOG";

    private static AppSettings Configured()
    {
        var settings = SetupSettings.Begin(null);
        settings = settings with { Profile = settings.Profile with { Kind = ProfileKind.Api } };
        settings = SetupSettings.SelectRoute(settings, SetupRole.Stt, "gpt-4o-mini-transcribe", null);
        var route = settings.Setup!.Routes.Single();
        return SetupSettings.ReplaceRoute(settings, route with { Consent = route.Selection() });
    }

    [Fact]
    public async Task OpeningFixtureSetupAndStatusHaveNoVaultOrPersistenceEffects()
    {
        Assert.Equal(SettingsLoadState.FirstRun, (await Service.LoadAsync()).State);
        var draft = SetupSettings.Begin(null);
        Assert.Equal(SetupStep.Choice, draft.Setup!.Checkpoint);
        Assert.Empty(draft.Setup.Routes);
        var report = await new FoundationStatusService(Store).GetReportAsync();
        Assert.False(report.Ready);
        Assert.Empty(native.Events);
        Assert.False(Directory.Exists(directory));
        Assert.DoesNotContain(Canary, ReportFormatter.Human(report));
    }

    [Fact]
    public async Task MigrationIsExplicitPreservesIdentityBehaviorReferencesAndOriginalBytes()
    {
        var legacy = AppSettings.CreateUnconfigured();
        legacy = legacy with { Profile = legacy.Profile with
        {
            Kind = ProfileKind.ExistingEndpoints,
            Credentials = [new() { ProviderId = "legacy-provider", CredentialId = Guid.NewGuid() }]
        } };
        var saved = await Store.SaveAsync(legacy, null);
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        var loaded = await Service.LoadAsync();
        var draft = SetupSettings.Begin(loaded.Settings);
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
        Assert.Equal(legacy.Profile.Id, draft.Profile.Id);
        Assert.Equal(legacy.Profile.Kind, draft.Profile.Kind);
        Assert.Equal(legacy.Profile.Credentials, draft.Profile.Credentials);
        Assert.Empty(Directory.GetFiles(directory, "*.bak"));
        var result = await Service.SaveAsync(draft, saved.Revision);
        Assert.True(result.Save.Saved);
        Assert.True(result.Save.MigratedFromVersion1);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(directory, result.Save.SnapshotFileName!)));
        Assert.Equal(legacy.Profile.Id, (await Store.LoadAsync()).Settings!.Profile.Id);
        Assert.Equal(legacy.Profile.Kind, (await Store.LoadAsync()).Settings!.Profile.Kind);
        Assert.False((await Store.SaveAsync(legacy, result.Save.Revision)).Saved);
        Assert.False((await Store.SaveAsync(draft with { Profile = draft.Profile with { Id = Guid.NewGuid() } }, result.Save.Revision)).Saved);
        Assert.False((await Store.SaveAsync(draft with { Profile = draft.Profile with { Credentials = [] } }, result.Save.Revision)).Saved);
        Assert.Empty(native.Events);
    }

    [Fact]
    public async Task EveryCheckpointResumesAndBackPreservesRoutesAndConsent()
    {
        var settings = Configured();
        string? revision = null;
        foreach (var step in new[] { SetupStep.Choice, SetupStep.Destinations, SetupStep.Credentials, SetupStep.Review, SetupStep.Destinations })
        {
            settings = settings with { Setup = settings.Setup! with { Checkpoint = step } };
            var saved = await Service.SaveAsync(settings, revision);
            Assert.True(saved.Save.Saved);
            revision = saved.Save.Revision;
            var resumed = SetupSettings.Begin((await Service.LoadAsync()).Settings);
            Assert.Equal(step, resumed.Setup!.Checkpoint);
            Assert.Equal(settings.Setup.Routes, resumed.Setup.Routes);
            Assert.Equal(settings.Profile.Id, resumed.Profile.Id);
        }
        Assert.Empty(native.Events);
    }

    [Fact]
    public void ChangedModelVoiceCredentialInvalidateOnlyAffectedRole()
    {
        var settings = Configured();
        settings = SetupSettings.SelectRoute(settings, SetupRole.Tts, "gpt-4o-mini-tts", "coral");
        var tts = settings.Setup!.Routes.Single(r => r.Role == SetupRole.Tts);
        settings = SetupSettings.ReplaceRoute(settings, tts with { Consent = tts.Selection() });
        var originalStt = settings.Setup!.Routes.Single(r => r.Role == SetupRole.Stt);
        var changed = SetupSettings.SelectRoute(settings, SetupRole.Tts, tts.ModelId, "alloy");
        Assert.Null(changed.Setup!.Routes.Single(r => r.Role == SetupRole.Tts).Consent);
        Assert.Equal(originalStt, changed.Setup.Routes.Single(r => r.Role == SetupRole.Stt));
        Assert.Equal(originalStt, SetupSettings.SelectRoute(settings, SetupRole.Stt, originalStt.ModelId, null).Setup!.Routes[0]);
        Assert.Null(SetupSettings.SelectRoute(settings, SetupRole.Stt, "whisper-1", null).Setup!.Routes[0].Consent);
        var rotated = originalStt.WithCredential(Guid.NewGuid());
        Assert.Null(rotated.Consent);
        Assert.NotEqual(originalStt.ConfigurationRevision, rotated.ConfigurationRevision);
        Assert.Throws<ContractException>(() => (rotated with { Consent = originalStt.Consent }).Validate());
        Assert.Throws<ContractException>(() => (originalStt with { Origin = "https://untrusted.example" }).Validate());
        Assert.Throws<ContractException>(() => (originalStt with { ProviderAlias = "other" }).Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("model/endpoint")]
    [InlineData("a\r\nb")]
    [InlineData("model space")]
    public void ModelIdsHaveExplicitBoundsAndNoFallback(string model) =>
        Assert.Throws<ContractException>(() => SetupSettings.SelectRoute(Configured(), SetupRole.Llm, model, null));

    [Fact]
    public void SchemaRejectsInvalidConsentAndBoundedUnsupportedState()
    {
        var settings = Configured();
        var route = settings.Setup!.Routes.Single();
        Assert.Throws<ContractException>(() => (route.Consent! with { UserSelected = false }).Validate());
        Assert.Throws<ContractException>(() => (route with { ConfigurationRevision = Guid.Empty }).Validate());
        Assert.Throws<ContractException>(() => (route with { VoiceId = "coral" }).Validate());
        Assert.Throws<ContractException>(() => SetupSettings.SelectRoute(settings, SetupRole.Tts, "gpt-4o-mini-tts", null));
        Assert.Throws<ContractException>(() => SetupSettings.SelectRoute(settings, SetupRole.Llm, new string('a', 129), null));
        Assert.Throws<ContractException>(() => (settings.Setup with { Routes = [route, route] }).Validate());
        Assert.Throws<ContractException>(() => (settings.Setup with { Checkpoint = (SetupStep)4 }).Validate());
        Assert.Throws<ContractException>(() => (settings.Setup with { SchemaVersion = 2 }).Validate());
        Assert.Throws<ContractException>(() => (settings with { SchemaVersion = 1 }).Validate());
        Assert.Throws<ContractException>(() => (settings with { Setup = null }).Validate());
    }

    [Theory]
    [InlineData("unknown_field")]
    [InlineData("api_key")]
    [InlineData("capture_on_launch")]
    public async Task UnknownSetupFieldsAreRejectedAndOriginalBytesPreserved(string field)
    {
        var json = Encoding.UTF8.GetString(ContractJson.Write(Configured()));
        json = json.Replace("\"checkpoint\":", $"\"{field}\": \"{Canary}\", \"checkpoint\":", StringComparison.Ordinal);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Store.FilePath, json);
        var bytes = await File.ReadAllBytesAsync(Store.FilePath);
        var load = await Service.LoadAsync();
        Assert.Equal(SettingsLoadState.Invalid, load.State);
        Assert.DoesNotContain(Canary, load.Error!.ToString());
        Assert.False((await Service.SaveAsync(Configured(), null)).Save.Saved);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Store.FilePath));
        Assert.Empty(native.Events);
    }

    [Fact]
    public async Task NewerUnknownVersionsAndStaleMigrationCannotReplaceOriginal()
    {
        var initial = await Store.SaveAsync(AppSettings.CreateUnconfigured(), null);
        var stale = await Store.LoadAsync();
        var newer = stale.Settings! with { Profile = stale.Settings.Profile with { Kind = ProfileKind.Fixture } };
        Assert.True((await Store.SaveAsync(newer, initial.Revision)).Saved);
        Assert.False((await Service.SaveAsync(SetupSettings.Begin(stale.Settings), stale.Revision)).Save.Saved);
        Assert.Empty(Directory.GetFiles(directory, "*.bak"));
        await File.WriteAllTextAsync(Store.FilePath, "{\"schema_version\":99,\"future\":true}");
        var bytes = await File.ReadAllBytesAsync(Store.FilePath);
        Assert.Equal(ErrorCode.UnsupportedVersion, (await Service.LoadAsync()).Error!.Code);
        Assert.False((await Service.SaveAsync(Configured(), initial.Revision)).Save.Saved);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Store.FilePath));
    }

    [Theory]
    [InlineData("{\"schema_version\":2,\"setup\":{\"schema_version\":99,\"future\":true}}")]
    [InlineData("{\"schema_version\":2,\"setup\":{\"schema_version\":1,\"routes\":[{\"consent\":{\"schema_version\":99,\"future\":true}}]}}")]
    public async Task NestedFutureVersionsHaveVersionRemedyAndArePreserved(string json)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Store.FilePath, json);
        Assert.Equal(ErrorCode.UnsupportedVersion, (await Service.LoadAsync()).Error!.Code);
        Assert.False((await Service.SaveAsync(Configured(), null)).Save.Saved);
        Assert.Equal(json, await File.ReadAllTextAsync(Store.FilePath));
    }

    [Fact]
    public async Task RealServiceStagesMetadataBeforeVaultWriteAndCommitsWithoutImplicitReadOrDelete()
    {
        var settings = Configured();
        native.OnWrite = () =>
        {
            var persisted = Store.LoadAsync().GetAwaiter().GetResult().Settings!;
            Assert.Single(persisted.Setup!.PendingRemovals);
            Assert.Null(persisted.Setup.Routes.Single().CredentialId);
        };
        using var secret = new SecretLease(Canary);
        var saved = await Service.ReplaceCredentialAsync(settings, null, SetupRole.Stt, secret);
        Assert.True(saved.Save.Saved);
        Assert.Single(native.Events);
        Assert.StartsWith("write Martlet/v2/", native.Events[0]);
        Assert.Contains("/api.openai.com/openai-stt/", native.Events[0]);
        Assert.DoesNotContain(Canary, native.Events[0]);
        Assert.Empty(saved.Settings.Setup!.PendingRemovals);
        Assert.NotNull(saved.Settings.Setup.Routes.Single().CredentialId);
        Assert.Null(saved.Settings.Setup.Routes.Single().Consent);
        var json = await File.ReadAllTextAsync(Store.FilePath);
        Assert.DoesNotContain(Canary, json);
        var report = await new FoundationStatusService(Store).GetReportAsync();
        Assert.False(report.Ready);
        Assert.Equal(12, report.Probes.Count);
        Assert.DoesNotContain(Canary, ReportFormatter.Human(report));
        Assert.DoesNotContain(Canary, Encoding.UTF8.GetString(ContractJson.Write(report)));
        Assert.DoesNotContain(saved.Settings.Setup.Routes.Single().CredentialId!.ToString()!, Encoding.UTF8.GetString(ContractJson.Write(report)));
        Assert.Single(native.Events);
        Assert.Equal(CredentialError.None, Service.CheckCredential(saved.Settings, SetupRole.Stt));
        Assert.Equal(2, native.Events.Count);
    }

    [Fact]
    public async Task ReplacementAndDetachRequireSeparateScopedDeletionAndInvalidateConsent()
    {
        using var secret = new SecretLease(Canary);
        var first = await Service.ReplaceCredentialAsync(Configured(), null, SetupRole.Stt, secret);
        var old = first.Settings.Setup!.Routes.Single().CredentialId;
        var second = await Service.ReplaceCredentialAsync(first.Settings, first.Save.Revision, SetupRole.Stt, secret);
        Assert.True(second.Save.Saved);
        Assert.Equal(2, native.Events.Count);
        Assert.Equal(old, second.Settings.Setup!.PendingRemovals.Single().CredentialId);
        Assert.NotEqual(old, second.Settings.Setup.Routes.Single().CredentialId);
        Assert.Equal(2, native.Keys.Count);
        var removed = await Service.RemoveDetachedAsync(second.Settings, second.Save.Revision, second.Settings.Setup.PendingRemovals.Single());
        Assert.True(removed.Save.Saved);
        Assert.Empty(removed.Settings.Setup!.PendingRemovals);
        Assert.Single(native.Keys);
        var detached = await Service.DetachCredentialAsync(removed.Settings, removed.Save.Revision, SetupRole.Stt);
        Assert.True(detached.Save.Saved);
        Assert.Null(detached.Settings.Setup!.Routes.Single().CredentialId);
        Assert.Single(native.Keys);
        Assert.Equal(CredentialError.Missing, Service.CheckCredential(detached.Settings, SetupRole.Stt));
        var final = await Service.RemoveDetachedAsync(detached.Settings, detached.Save.Revision, detached.Settings.Setup.PendingRemovals.Single());
        Assert.True(final.Save.Saved);
        Assert.Empty(native.Keys);
        Assert.Equal(CredentialError.Missing, Service.CheckCredential(first.Settings, SetupRole.Stt));
        Assert.DoesNotContain(Canary, final.ToString());
    }

    [Fact]
    public async Task DeleteSeesDetachedCheckpointAndLockExcludesConcurrentWriter()
    {
        using var secret = new SecretLease(Canary);
        var saved = await Service.ReplaceCredentialAsync(Configured(), null, SetupRole.Stt, secret);
        var detached = await Service.DetachCredentialAsync(saved.Settings, saved.Save.Revision, SetupRole.Stt);
        native.OnDelete = () =>
        {
            var current = Store.LoadAsync().GetAwaiter().GetResult();
            Assert.Null(current.Settings!.Setup!.Routes.Single().CredentialId);
            Assert.Single(current.Settings.Setup.PendingRemovals);
            var attempted = Store.SaveAsync(current.Settings, current.Revision).GetAwaiter().GetResult();
            Assert.False(attempted.Saved);
            Assert.Equal(ErrorCode.SettingsInaccessible, attempted.Error!.Code);
        };
        var deleted = await Service.RemoveDetachedAsync(detached.Settings, detached.Save.Revision, detached.Settings.Setup!.PendingRemovals.Single());
        Assert.True(deleted.Save.Saved);
    }

    [Fact]
    public async Task StaleKeySaveAndStaleDeletionNeverAccessVault()
    {
        var settings = Configured();
        var saved = await Service.SaveAsync(settings, null);
        var updated = settings with { Setup = settings.Setup! with { Checkpoint = SetupStep.Review } };
        Assert.True((await Service.SaveAsync(updated, saved.Save.Revision)).Save.Saved);
        using var secret = new SecretLease(Canary);
        Assert.False((await Service.ReplaceCredentialAsync(settings, saved.Save.Revision, SetupRole.Stt, secret)).Save.Saved);
        Assert.Empty(native.Events);
        var current = await Service.LoadAsync();
        var added = await Service.ReplaceCredentialAsync(current.Settings!, current.Revision, SetupRole.Stt, secret);
        var detached = await Service.DetachCredentialAsync(added.Settings, added.Save.Revision, SetupRole.Stt);
        Assert.True((await Service.SaveAsync(detached.Settings with { Setup = detached.Settings.Setup! with { Checkpoint = SetupStep.Choice } }, detached.Save.Revision)).Save.Saved);
        native.Events.Clear();
        var deletion = await Service.RemoveDetachedAsync(detached.Settings, detached.Save.Revision, detached.Settings.Setup!.PendingRemovals.Single());
        Assert.False(deletion.Save.Saved);
        Assert.Empty(native.Events);
        Assert.Single(native.Keys);
    }

    [Fact]
    public async Task CancellationAfterWriteRollsBackOnlyFreshKeyAndLeavesResumableMarker()
    {
        using var secret = new SecretLease(Canary);
        var first = await Service.ReplaceCredentialAsync(Configured(), null, SetupRole.Stt, secret);
        var oldId = first.Settings.Setup!.Routes.Single().CredentialId;
        using var cancellation = new CancellationTokenSource();
        native.OnWrite = cancellation.Cancel;
        var canceled = await Service.ReplaceCredentialAsync(first.Settings, first.Save.Revision, SetupRole.Stt, secret, cancellation.Token);
        Assert.False(canceled.Save.Saved);
        Assert.Single(native.Keys);
        var reloaded = (await Service.LoadAsync()).Settings!;
        Assert.Equal(oldId, reloaded.Setup!.Routes.Single().CredentialId);
        var pending = Assert.Single(reloaded.Setup.PendingRemovals);
        Assert.NotEqual(oldId, pending.CredentialId);
        Assert.EndsWith(pending.CredentialId.ToString("N"), native.Events.Last());
        Assert.DoesNotContain(Canary, canceled.Summary);
        native.OnWrite = null;
        var current = await Service.LoadAsync();
        Assert.True((await Service.RemoveDetachedAsync(current.Settings!, current.Revision, pending)).Save.Saved);
    }

    [Fact]
    public async Task FailedRollbackIsVisibleAndRecoverableAfterRestartWithoutEnumeration()
    {
        using var secret = new SecretLease(Canary);
        using var cancellation = new CancellationTokenSource();
        native.OnWrite = cancellation.Cancel;
        native.DeleteError = 5;
        var failed = await Service.ReplaceCredentialAsync(Configured(), null, SetupRole.Stt, secret, cancellation.Token);
        Assert.False(failed.Save.Saved);
        Assert.Equal(CredentialError.AccessDenied, failed.CredentialError);
        Assert.Contains("Reload", failed.Summary);
        Assert.Single(native.Keys);
        var resumed = await Service.LoadAsync();
        Assert.Single(resumed.Settings!.Setup!.PendingRemovals);
        Assert.Null(resumed.Settings.Setup.Routes.Single().CredentialId);
        Assert.Contains("cleanup pending", SetupSettings.Describe(resumed.Settings));
        native.DeleteError = 0;
        var cleanup = await Service.RemoveDetachedAsync(resumed.Settings, resumed.Revision, resumed.Settings.Setup.PendingRemovals.Single());
        Assert.True(cleanup.Save.Saved);
        Assert.Empty(native.Keys);
    }

    [Fact]
    public async Task FailedNativeWriteLeavesExactPendingMetadataAndNoAttachedKey()
    {
        native.WriteError = 5;
        using var secret = new SecretLease(Canary);
        var result = await Service.ReplaceCredentialAsync(Configured(), null, SetupRole.Stt, secret);
        Assert.False(result.Save.Saved);
        Assert.Equal(CredentialError.AccessDenied, result.CredentialError);
        Assert.Empty(native.Keys);
        var loaded = await Service.LoadAsync();
        Assert.Null(loaded.Settings!.Setup!.Routes.Single().CredentialId);
        var pending = Assert.Single(loaded.Settings.Setup.PendingRemovals);
        Assert.True((await Service.RemoveDetachedAsync(loaded.Settings, loaded.Revision, pending)).Save.Saved);
    }

    [Fact]
    public async Task PostDeleteSaveFailureKeepsRetryMarkerAndExplainsIrreversibility()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var secret = new SecretLease(Canary);
        var added = await Service.ReplaceCredentialAsync(Configured(), null, SetupRole.Stt, secret);
        var detached = await Service.DetachCredentialAsync(added.Settings, added.Save.Revision, SetupRole.Stt);
        FileStream? blocker = null;
        try
        {
            native.OnDelete = () => blocker = new FileStream(Store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var failed = await Service.RemoveDetachedAsync(detached.Settings, detached.Save.Revision, detached.Settings.Setup!.PendingRemovals.Single());
            Assert.False(failed.Save.Saved);
            Assert.Contains("key is removed", failed.Summary);
            Assert.Empty(native.Keys);
            Assert.Single((await Store.LoadAsync()).Settings!.Setup!.PendingRemovals);
        }
        finally { blocker?.Dispose(); native.OnDelete = null; }
        var reloaded = await Service.LoadAsync();
        Assert.True((await Service.RemoveDetachedAsync(reloaded.Settings!, reloaded.Revision, reloaded.Settings!.Setup!.PendingRemovals.Single())).Save.Saved);
    }

    [Fact]
    public async Task InterruptedProcessLeavesTrackedFreshTargetForExplicitRestartCleanup()
    {
        using var secret = new SecretLease(Canary);
        native.AfterWrite = () => throw new InvalidOperationException("Simulated process interruption after OS write.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.ReplaceCredentialAsync(Configured(), null, SetupRole.Stt, secret));
        var reloaded = await Service.LoadAsync();
        Assert.Single(native.Keys);
        Assert.Single(reloaded.Settings!.Setup!.PendingRemovals);
        Assert.Null(reloaded.Settings.Setup.Routes.Single().CredentialId);
        var removal = reloaded.Settings.Setup.PendingRemovals.Single();
        Assert.True((await Service.RemoveDetachedAsync(reloaded.Settings, reloaded.Revision, removal)).Save.Saved);
        Assert.Empty(native.Keys);
    }

    [Fact]
    public async Task UnchangedRevisionCannotCarryChangedModelConsentOrCredentialPolicy()
    {
        var settings = Configured();
        var saved = await Service.SaveAsync(settings, null);
        var route = settings.Setup!.Routes.Single() with { ModelId = "whisper-1", Consent = null };
        var changed = SetupSettings.ReplaceRoute(settings, route with { Consent = route.Selection() });
        Assert.False((await Service.SaveAsync(changed, saved.Save.Revision)).Save.Saved);
        var loaded = await Service.LoadAsync();
        Assert.Equal("gpt-4o-mini-transcribe", loaded.Settings!.Setup!.Routes.Single().ModelId);
    }

    [Fact]
    public async Task InvalidProfileOrConsentIsRejectedBeforeNativeWrites()
    {
        var settings = Configured();
        var route = settings.Setup!.Routes.Single();
        using var secret = new SecretLease(Canary);
        var invalid = settings with { Setup = settings.Setup with { Routes = [route with { ModelId = "changed" }] } };
        await Assert.ThrowsAsync<ContractException>(() => Service.ReplaceCredentialAsync(invalid, null, SetupRole.Stt, secret));
        var saved = await Service.SaveAsync(settings, null);
        Assert.False((await Service.ReplaceCredentialAsync(settings with { Profile = settings.Profile with { Id = Guid.NewGuid() } },
            saved.Save.Revision, SetupRole.Stt, secret)).Save.Saved);
        Assert.Empty(native.Events);
    }

    [Fact]
    public async Task ActiveAndPendingCredentialsCannotBeSilentlyDroppedOrDeleteOtherProfiles()
    {
        using var secret = new SecretLease(Canary);
        var added = await Service.ReplaceCredentialAsync(Configured(), null, SetupRole.Stt, secret);
        var lost = added.Settings with { Setup = added.Settings.Setup! with { Routes = [] } };
        Assert.False((await Service.SaveAsync(lost, added.Save.Revision)).Save.Saved);
        var detached = await Service.DetachCredentialAsync(added.Settings, added.Save.Revision, SetupRole.Stt);
        var missing = detached.Settings with { Setup = detached.Settings.Setup! with { PendingRemovals = [] } };
        Assert.False((await Service.SaveAsync(missing, detached.Save.Revision)).Save.Saved);
        var foreign = detached.Settings with { Profile = detached.Settings.Profile with { Id = Guid.NewGuid() } };
        native.Events.Clear();
        Assert.False((await Service.RemoveDetachedAsync(foreign, detached.Save.Revision, foreign.Setup!.PendingRemovals.Single())).Save.Saved);
        Assert.Empty(native.Events);
        Assert.Single(native.Keys);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        native.Dispose();
    }
}

internal sealed class FakeCredentialNative : ICredentialNative, IDisposable
{
    public bool IsSupported { get; set; } = true;
    public Dictionary<string, char[]> Keys { get; } = [];
    public List<string> Events { get; } = [];
    public Action? OnWrite { get; set; }
    public Action? AfterWrite { get; set; }
    public Action? OnDelete { get; set; }
    public int WriteError { get; set; }
    public int ReadError { get; set; }
    public int DeleteError { get; set; }

    public int Write(string target, ReadOnlySpan<char> secret)
    {
        Events.Add("write " + target);
        OnWrite?.Invoke();
        if (WriteError != 0) return WriteError;
        Keys.Add(target, secret.ToArray());
        AfterWrite?.Invoke();
        return 0;
    }

    public int Read(string target, out SecretLease? secret)
    {
        Events.Add("read " + target);
        secret = null;
        if (ReadError != 0) return ReadError;
        if (!Keys.TryGetValue(target, out var chars)) return 1168;
        secret = new(chars);
        return 0;
    }

    public int Delete(string target)
    {
        Events.Add("delete " + target);
        OnDelete?.Invoke();
        if (DeleteError != 0) return DeleteError;
        if (!Keys.Remove(target, out var chars)) return 1168;
        Array.Clear(chars);
        return 0;
    }

    public void Dispose()
    {
        foreach (var chars in Keys.Values) Array.Clear(chars);
    }
}
