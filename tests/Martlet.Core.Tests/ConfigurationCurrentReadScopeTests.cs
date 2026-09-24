using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class ConfigurationCurrentReadScopeTests : IDisposable
{
    private const string Canary = "PRIVATE-CURRENT-READ-CANARY";
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "current-read-fixtures", Guid.NewGuid().ToString("N"));
    private SettingsStore Store => new(directory);
    private string Backup => Path.Combine(directory, "private.martlet-config");

    private async Task<AppSettings> Prepare(int version = 2)
    {
        var settings = AppSettings.CreateUnconfigured();
        settings = settings with { Profile = settings.Profile with
        {
            Credentials = [new() { ProviderId = Canary, CredentialId = Guid.NewGuid() }]
        } };
        if (version == 2) settings = SetupSettings.Begin(settings) with
        {
            SchemaVersion = 2,
            Setup = SetupSettings.Begin(settings).Setup!.DowngradeOpenAiForHistoricalSettings(),
            Companion = null,
            Memory = null
        };
        Assert.True((await Store.SaveAsync(settings, null)).Saved);
        return settings;
    }

    [Theory]
    [InlineData(1, "canonical")]
    [InlineData(1, "noncanonical")]
    [InlineData(1, "maximum")]
    [InlineData(2, "canonical")]
    [InlineData(2, "noncanonical")]
    [InlineData(2, "maximum")]
    public async Task ObservesExactValidatedCurrentBytesWithoutMigrationOrReserialization(int version, string format)
    {
        var settings = await Prepare(version);
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        if (format == "noncanonical")
            original = Encoding.UTF8.GetBytes("\r\n \t" + Encoding.UTF8.GetString(original).Replace(
                "\"schema_version\"", "\"\\u0073chema_version\"", StringComparison.Ordinal) + "\n \t");
        if (format == "maximum")
            original = [.. original, .. Enumerable.Repeat((byte)' ', AppSettings.MaxFileBytes - original.Length)];
        await File.WriteAllBytesAsync(Store.FilePath, original);
        var files = Directory.GetFiles(directory).Order().ToArray();
        var scope = await Store.OpenCurrentConfigurationReadAsync();
        var inspection = scope.Inspection;
        try
        {
            Assert.Equal(Store.FilePath, inspection.Path);
            Assert.Equal(Encoding.UTF8.GetString(original), inspection.CurrentJson);
            Assert.Equal(original, Encoding.UTF8.GetBytes(inspection.CurrentJson));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(original)), inspection.Revision);
            Assert.Matches("^[0-9A-F]{64}$", inspection.Revision);
            Assert.Equal(settings.Profile.Id, inspection.ProfileId);
            Assert.Equal(version, inspection.SchemaVersion);
            Assert.Same(inspection, await scope.VerifyAsync());
            Assert.Same(inspection, await scope.VerifyAsync());
            AssertPrivate(scope.ToString());
            AssertPrivate(inspection.ToString());
            Assert.Equal(files, Directory.GetFiles(directory).Order());
            Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
            AssertWriterHeld();
            AssertPinned(Store.FilePath);
        }
        finally { await scope.DisposeAsync(); }
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
        AssertWriterReleased();
        AssertPinsReleased();
        await File.AppendAllTextAsync(Store.FilePath, "\n");
        Assert.NotEqual(inspection.Revision, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Store.FilePath))));
        Assert.Equal(Encoding.UTF8.GetString(original), inspection.CurrentJson);
        await Failure(RecoveryFailure.Conflict, () => scope.VerifyAsync());
        await scope.DisposeAsync();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("empty-profile")]
    [InlineData("utf8")]
    [InlineData("surrogate")]
    [InlineData("future")]
    public async Task RefusesMissingInvalidOrFutureCurrentWithoutRepair(string scenario)
    {
        await Prepare();
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        var text = Encoding.UTF8.GetString(original);
        var bytes = scenario switch
        {
            "empty" => [],
            "malformed" => Encoding.UTF8.GetBytes("{\"" + Canary),
            "oversized" => Enumerable.Repeat((byte)' ', AppSettings.MaxFileBytes + 1).ToArray(),
            "duplicate" => Encoding.UTF8.GetBytes(text.Replace("\"schema_version\": 2,", "\"schema_version\": 2, \"schema_version\": 2,", StringComparison.Ordinal)),
            "unknown" => Encoding.UTF8.GetBytes(text.Replace("\"schema_version\": 2,", "\"schema_version\": 2, \"private_unknown\": \"" + Canary + "\",", StringComparison.Ordinal)),
            "empty-profile" => Encoding.UTF8.GetBytes(text.Replace((await Store.LoadAsync()).Settings!.Profile.Id.ToString(), Guid.Empty.ToString(), StringComparison.Ordinal)),
            "utf8" => [.. Encoding.UTF8.GetBytes("{\""), 0xff, .. Encoding.UTF8.GetBytes("\":\"" + Canary + "\"}")],
            "surrogate" => Encoding.UTF8.GetBytes("{\"\\uD800\":\"" + Canary + "\"}"),
            "future" => Encoding.UTF8.GetBytes("{\"schema_version\":99,\"private_future\":\"" + Canary + "\"}"),
            _ => original
        };
        if (scenario == "missing") File.Delete(Store.FilePath);
        else await File.WriteAllBytesAsync(Store.FilePath, bytes);
        var error = await Failure(scenario == "future" ? RecoveryFailure.Incompatible : RecoveryFailure.Unavailable,
            () => Store.OpenCurrentConfigurationReadAsync());
        AssertPrivate(error.ToString());
        Assert.Null(error.InnerException);
        Assert.Null(error.RetainedFile);
        if (scenario == "missing") Assert.False(File.Exists(Store.FilePath));
        else Assert.Equal(bytes, await File.ReadAllBytesAsync(Store.FilePath));
        Assert.Empty(Directory.GetFiles(directory, "*.bak"));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        AssertWriterReleased();
        if (scenario != "missing") AssertPinsReleased();
    }

    [Theory]
    [InlineData("absent-directory")]
    [InlineData("absent-settings")]
    [InlineData("directory-is-file")]
    [InlineData("settings-is-directory")]
    public async Task RequiresAnExistingExplicitDirectoryAndProfileRatherThanInitializing(string scenario)
    {
        var data = Path.Combine(directory, "selected", "profile");
        var store = new SettingsStore(data);
        if (scenario != "absent-directory")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(data)!);
            if (scenario == "directory-is-file") await File.WriteAllTextAsync(data, Canary);
            else Directory.CreateDirectory(data);
        }
        if (scenario == "settings-is-directory") Directory.CreateDirectory(store.FilePath);
        await Failure(RecoveryFailure.Unavailable, () => store.OpenCurrentConfigurationReadAsync());
        Assert.False(File.Exists(store.FilePath));
        if (scenario is "absent-directory" or "directory-is-file")
        {
            Assert.False(Directory.Exists(data));
            Assert.False(File.Exists(store.FilePath + ".lock"));
        }
        if (scenario == "absent-directory") Assert.False(Directory.Exists(directory));
        if (scenario == "directory-is-file") Assert.Equal(Canary, await File.ReadAllTextAsync(data));
        if (scenario == "absent-settings") Assert.Equal([store.FilePath + ".lock"], Directory.GetFiles(data));
        if (scenario == "settings-is-directory") Assert.True(Directory.Exists(store.FilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockAcquisitionIsTheOnlyPersistentChangeAndNeverTruncatesAnExistingLock(bool existingLock)
    {
        await Prepare();
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        var modified = File.GetLastWriteTimeUtc(Store.FilePath);
        var unrelated = Path.Combine(directory, "unrelated.private");
        await File.WriteAllTextAsync(unrelated, Canary);
        File.Delete(Store.FilePath + ".lock");
        if (existingLock) await File.WriteAllTextAsync(Store.FilePath + ".lock", Canary);
        var points = new List<SettingsIoPoint>();
        var store = new SettingsStore(directory) { RecoveryIo = (point, _) => points.Add(point) };
        await using (var scope = await store.OpenCurrentConfigurationReadAsync())
        {
            Assert.Equal(Enumerable.Repeat(new[] { SettingsIoPoint.BeforeRead, SettingsIoPoint.AfterRead }, 3).SelectMany(pair => pair), points);
            points.Clear();
            await scope.VerifyAsync();
            Assert.Equal(Enumerable.Repeat(new[] { SettingsIoPoint.BeforeRead, SettingsIoPoint.AfterRead }, 2).SelectMany(pair => pair), points);
            Assert.Equal(new[] { store.FilePath, store.FilePath + ".lock", unrelated }.Order(), Directory.GetFiles(directory).Order());
        }
        Assert.Equal(existingLock ? Canary : "", await File.ReadAllTextAsync(store.FilePath + ".lock"));
        Assert.Equal(original, await File.ReadAllBytesAsync(store.FilePath));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(store.FilePath));
        Assert.Equal(Canary, await File.ReadAllTextAsync(unrelated));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnsTheRealWriterAgainstSameAndSecondStoreSavesSnapshotsPreviewsAndRestores(bool secondStore)
    {
        var settings = await Prepare();
        var owner = Store;
        var other = secondStore ? Store : owner;
        await owner.CreateConfigurationSnapshotAsync(Backup);
        var scopedPlan = await other.PreviewConfigurationRestoreAsync(Backup);
        var standalonePlan = await other.PreviewConfigurationRestoreAsync(Backup);
        var bytes = await File.ReadAllBytesAsync(owner.FilePath);
        await using (var scope = await owner.OpenCurrentConfigurationReadAsync())
        {
            Assert.Equal(SettingsLoadState.Loaded, (await other.LoadAsync()).State);
            Assert.False((await other.SaveAsync(settings, scope.Inspection.Revision)).Saved);
            await Failure(RecoveryFailure.Unavailable, () => other.OpenCurrentConfigurationReadAsync());
            await Failure(RecoveryFailure.Unavailable, () => other.CreateConfigurationSnapshotAsync(Backup + ".new"));
            await Failure(RecoveryFailure.Unavailable, () => other.PreviewConfigurationRestoreAsync(Backup));
            await Failure(RecoveryFailure.Unavailable, () => other.OpenConfigurationRestoreAsync(scopedPlan, Approve(scopedPlan)));
            await Failure(RecoveryFailure.Unavailable, () => other.RestoreConfigurationAsync(standalonePlan, Approve(standalonePlan)));
            Assert.False(File.Exists(Backup + ".new"));
            Assert.Empty(Directory.GetFiles(directory, "settings.recovery.*.bak"));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            AssertWriterHeld();
            AssertPinned(owner.FilePath);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(owner.FilePath));
        }
        AssertWriterReleased();
        AssertPinsReleased();
        Assert.True((await other.SaveAsync(settings, (await other.LoadAsync()).Revision)).Saved);
        var fresh = await other.PreviewConfigurationRestoreAsync(Backup);
        var receipt = await other.RestoreConfigurationAsync(fresh, Approve(fresh));
        Assert.Equal(fresh.CandidateDigest, receipt.Revision);
        await using var reopened = await owner.OpenCurrentConfigurationReadAsync();
        Assert.Equal(receipt.Revision, (await reopened.VerifyAsync()).Revision);
    }

    [Theory]
    [InlineData("writer")]
    [InlineData("restore")]
    [InlineData("current-write")]
    public async Task RefusesExistingRealWriterRestoreAndWritableCurrentOwnership(string held)
    {
        if (held == "current-write" && !OperatingSystem.IsWindows()) return;
        await Prepare();
        await Store.CreateConfigurationSnapshotAsync(Backup);
        var plan = await Store.PreviewConfigurationRestoreAsync(Backup);
        var bytes = await File.ReadAllBytesAsync(Store.FilePath);
        IAsyncDisposable blocker = held switch
        {
            "writer" => new FileStream(Store.FilePath + ".lock", FileMode.Open, FileAccess.Write, FileShare.None),
            "restore" => await Store.OpenConfigurationRestoreAsync(plan, Approve(plan)),
            _ => new FileStream(Store.FilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)
        };
        try
        {
            await Failure(RecoveryFailure.Unavailable, () => Store.OpenCurrentConfigurationReadAsync());
        }
        finally { await blocker.DisposeAsync(); }
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Store.FilePath));
        AssertWriterReleased();
        AssertPinsReleased();
        await using var scope = await Store.OpenCurrentConfigurationReadAsync();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), (await scope.VerifyAsync()).Revision);
    }

    [Fact]
    public async Task PendingActualStandaloneRestoreCleanupIsRefusedWithoutAutomaticRetry()
    {
        await Prepare();
        await Store.CreateConfigurationSnapshotAsync(Backup);
        var plan = await Store.PreviewConfigurationRestoreAsync(Backup);
        var stages = 0;
        var failCleanup = true;
        var reads = 0;
        var store = new SettingsStore(directory) { RecoveryIo = (point, _) =>
        {
            if (point == SettingsIoPoint.BeforeStage) stages++;
            if (point == SettingsIoPoint.AfterWrite && stages == 2) throw new IOException(Canary);
            if (point == SettingsIoPoint.BeforeCleanup && failCleanup) throw new UnauthorizedAccessException(Canary);
            if (point == SettingsIoPoint.BeforeRead) reads++;
        } };
        var failure = await Failure(RecoveryFailure.CleanupPending, () => store.RestoreConfigurationAsync(plan, Approve(plan)));
        var retained = Assert.IsType<string>(failure.RetainedFile);
        try
        {
            var files = Directory.GetFiles(directory).Order().ToArray();
            var current = await File.ReadAllBytesAsync(store.FilePath);
            var beforeReads = reads;
            var blocked = await Failure(RecoveryFailure.CleanupPending, () => store.OpenCurrentConfigurationReadAsync());
            Assert.Equal(retained, blocked.RetainedFile);
            AssertPrivate(blocked.ToString());
            Assert.Equal(beforeReads, reads);
            Assert.Equal(files, Directory.GetFiles(directory).Order());
            Assert.True(File.Exists(retained));
            Assert.Equal(current, await File.ReadAllBytesAsync(store.FilePath));
            AssertWriterReleased();
        }
        finally
        {
            failCleanup = false;
            store.RetryRecoveryCleanup(retained);
        }
        await using var scope = await store.OpenCurrentConfigurationReadAsync();
        await scope.VerifyAsync();
        Assert.False(File.Exists(retained));
    }

    [Theory]
    [InlineData(false, "BeforeRead", 1)]
    [InlineData(false, "AfterRead", 1)]
    [InlineData(false, "BeforeRead", 2)]
    [InlineData(false, "AfterRead", 2)]
    [InlineData(false, "BeforeRead", 3)]
    [InlineData(false, "AfterRead", 3)]
    [InlineData(true, "BeforeRead", 1)]
    [InlineData(true, "AfterRead", 1)]
    [InlineData(true, "BeforeRead", 2)]
    [InlineData(true, "AfterRead", 2)]
    public async Task ReadFailuresAreSanitizedAndOnlyFailedOpenReleasesOwnership(bool verifying, string boundary, int occurrence)
    {
        await Prepare();
        var bytes = await File.ReadAllBytesAsync(Store.FilePath);
        var fail = !verifying;
        var seen = 0;
        var store = new SettingsStore(directory) { RecoveryIo = (point, _) =>
        {
            Assert.True(point is SettingsIoPoint.BeforeRead or SettingsIoPoint.AfterRead);
            if (fail && point.ToString() == boundary && ++seen == occurrence)
                throw new IOException(Canary + directory + Encoding.UTF8.GetString(bytes));
        } };
        ConfigurationCurrentReadScope? scope = null;
        try
        {
            if (verifying)
            {
                scope = await store.OpenCurrentConfigurationReadAsync();
                fail = true;
            }
            var error = await Failure(RecoveryFailure.Unavailable,
                () => scope is null ? store.OpenCurrentConfigurationReadAsync() : scope.VerifyAsync());
            AssertPrivate(error.ToString());
            Assert.Null(error.InnerException);
            Assert.Null(error.RetainedFile);
            if (scope is not null)
            {
                AssertWriterHeld();
                AssertPinned(store.FilePath);
                fail = false;
                Assert.Same(scope.Inspection, await scope.VerifyAsync());
            }
        }
        finally { if (scope is not null) await scope.DisposeAsync(); }
        AssertWriterReleased();
        AssertPinsReleased();
        Assert.Equal(bytes, await File.ReadAllBytesAsync(store.FilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccessDenialIsSanitizedWithoutLeakingPrivateIoErrors(bool verifying)
    {
        await Prepare();
        var fail = !verifying;
        var store = new SettingsStore(directory) { RecoveryIo = (_, _) =>
        {
            if (fail) throw new UnauthorizedAccessException(Canary + directory);
        } };
        ConfigurationCurrentReadScope? scope = null;
        try
        {
            if (verifying)
            {
                scope = await store.OpenCurrentConfigurationReadAsync();
                fail = true;
            }
            var error = await Failure(RecoveryFailure.Unavailable,
                () => scope is null ? store.OpenCurrentConfigurationReadAsync() : scope.VerifyAsync());
            AssertPrivate(error.ToString());
            Assert.Null(error.InnerException);
            if (scope is not null)
            {
                AssertWriterHeld();
                AssertPinned(store.FilePath);
            }
        }
        finally { if (scope is not null) await scope.DisposeAsync(); }
        AssertWriterReleased();
        AssertPinsReleased();
    }

    [Theory]
    [InlineData("BeforeRead", "success")]
    [InlineData("AfterRead", "success")]
    [InlineData("BeforeRead", "cancel")]
    [InlineData("AfterRead", "cancel")]
    [InlineData("BeforeRead", "error")]
    [InlineData("AfterRead", "error")]
    public async Task DisposalWaitsForActualBlockedVerificationAndRepeatedDisposalsShareRetirement(string boundary, string outcome)
    {
        await Prepare();
        var verifying = false;
        using var stop = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var store = new SettingsStore(directory) { RecoveryIo = (point, observed) =>
        {
            Assert.Equal(stop.Token, observed);
            if (!verifying || point.ToString() != boundary) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException();
            if (outcome == "error") throw new IOException(Canary + directory);
        } };
        var scope = await store.OpenCurrentConfigurationReadAsync(stop.Token);
        verifying = true;
        var work = Task.Run(() => scope.VerifyAsync());
        Task? disposal = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            await Failure(RecoveryFailure.Conflict, () => scope.VerifyAsync());
            if (outcome == "cancel") stop.Cancel();
            Assert.False(work.IsCompleted);
            AssertWriterHeld();
            AssertPinned(store.FilePath);
            disposal = scope.DisposeAsync().AsTask();
            Assert.Same(disposal, scope.DisposeAsync().AsTask());
            Assert.False(disposal.IsCompleted);
            AssertWriterHeld();
            AssertPinned(store.FilePath);
            await Failure(RecoveryFailure.Conflict, () => scope.VerifyAsync());
        }
        finally
        {
            release.Set();
            try
            {
                if (outcome == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
                else if (outcome == "error") await Failure(RecoveryFailure.Unavailable, () => work);
                else Assert.Same(scope.Inspection, await work);
            }
            finally { await (disposal ?? scope.DisposeAsync().AsTask()); }
        }
        AssertWriterReleased();
        AssertPinsReleased();
        await Failure(RecoveryFailure.Conflict, () => scope.VerifyAsync());
        Assert.Same(disposal, scope.DisposeAsync().AsTask());
    }

    [Theory]
    [InlineData(false, "BeforeRead", 1)]
    [InlineData(false, "AfterRead", 1)]
    [InlineData(false, "BeforeRead", 2)]
    [InlineData(false, "AfterRead", 2)]
    [InlineData(false, "BeforeRead", 3)]
    [InlineData(false, "AfterRead", 3)]
    [InlineData(true, "BeforeRead", 1)]
    [InlineData(true, "AfterRead", 1)]
    [InlineData(true, "BeforeRead", 2)]
    [InlineData(true, "AfterRead", 2)]
    public async Task OriginalTokenIsCheckedDirectlyAtEachActualReadBoundary(bool verifying, string boundary, int occurrence)
    {
        await Prepare();
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        using var stop = new CancellationTokenSource();
        var cancel = !verifying;
        var seen = 0;
        var store = new SettingsStore(directory) { RecoveryIo = (point, observed) =>
        {
            Assert.Equal(stop.Token, observed);
            Assert.True(point is SettingsIoPoint.BeforeRead or SettingsIoPoint.AfterRead);
            if (cancel && point.ToString() == boundary && ++seen == occurrence) stop.Cancel();
        } };
        ConfigurationCurrentReadScope? scope = null;
        try
        {
            if (verifying)
            {
                scope = await store.OpenCurrentConfigurationReadAsync(stop.Token);
                cancel = true;
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => scope is null ? store.OpenCurrentConfigurationReadAsync(stop.Token) : scope.VerifyAsync());
            Assert.Equal(occurrence, seen);
            if (scope is not null)
            {
                AssertWriterHeld();
                AssertPinned(store.FilePath);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.VerifyAsync());
                Assert.Equal(occurrence, seen);
            }
        }
        finally { if (scope is not null) await scope.DisposeAsync(); }
        AssertWriterReleased();
        AssertPinsReleased();
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlreadyCanceledOpenPerformsNoInitializationOrLockAcquisition(bool existingProfile)
    {
        if (existingProfile)
        {
            await Prepare();
            File.Delete(Store.FilePath + ".lock");
        }
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var reads = 0;
        var store = new SettingsStore(directory) { RecoveryIo = (_, _) => reads++ };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.OpenCurrentConfigurationReadAsync(stop.Token));
        Assert.Equal(0, reads);
        Assert.False(File.Exists(store.FilePath + ".lock"));
        Assert.Equal(existingProfile, Directory.Exists(directory));
        Assert.Equal(existingProfile, File.Exists(store.FilePath));
    }

    [Fact]
    public async Task CancellationIsObservedWhileItsCallbackRemainsBlockedAndNeverRetiresOwnership()
    {
        await Prepare();
        using var stop = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var registration = stop.Token.Register(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException();
        });
        var verifying = false;
        Task? cancellation = null;
        var store = new SettingsStore(directory) { RecoveryIo = (point, _) =>
        {
            if (!verifying || point != SettingsIoPoint.BeforeRead) return;
            cancellation = Task.Run(stop.Cancel);
            if (!entered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        } };
        var scope = await store.OpenCurrentConfigurationReadAsync(stop.Token);
        try
        {
            verifying = true;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.VerifyAsync());
            Assert.NotNull(cancellation);
            Assert.False(cancellation.IsCompleted);
            AssertWriterHeld();
            AssertPinned(store.FilePath);
            await scope.DisposeAsync();
            AssertWriterReleased();
            AssertPinsReleased();
            Assert.False(cancellation.IsCompleted);
        }
        finally
        {
            release.Set();
            if (cancellation is not null) await cancellation;
            await scope.DisposeAsync();
        }
    }

    [Fact]
    public void PublicShapeIsImmutableCurrentObservationWithNoAuthorityOrProofIngestion()
    {
        var scope = typeof(ConfigurationCurrentReadScope);
        var inspection = typeof(ConfigurationCurrentInspection);
        foreach (var type in new[] { scope, inspection })
        {
            Assert.True(type.IsSealed);
            Assert.Empty(type.GetConstructors());
            Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
            Assert.All(type.GetProperties(BindingFlags.Public | BindingFlags.Instance), property => Assert.Null(property.SetMethod));
        }
        Assert.Equal(new[] { "CurrentJson", "Path", "ProfileId", "Revision", "SchemaVersion" },
            inspection.GetProperties().Select(property => property.Name).Order());
        Assert.All(inspection.GetProperties(), property => Assert.Contains(property.PropertyType,
            new[] { typeof(string), typeof(Guid), typeof(int) }));
        Assert.Equal(inspection, Assert.Single(scope.GetProperties()).PropertyType);
        Assert.Equal(nameof(ConfigurationCurrentReadScope.Inspection), Assert.Single(scope.GetProperties()).Name);
        Assert.DoesNotContain(inspection.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), method => !method.IsSpecialName);
        var methods = scope.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(method => !method.IsSpecialName).ToArray();
        Assert.Equal(new[] { "DisposeAsync", "VerifyAsync" }, methods.Select(method => method.Name).Order());
        Assert.All(methods, method => Assert.Empty(method.GetParameters()));
        Assert.Equal(typeof(Task<ConfigurationCurrentInspection>), scope.GetMethod("VerifyAsync")!.ReturnType);
        Assert.Equal(typeof(ValueTask), scope.GetMethod("DisposeAsync")!.ReturnType);
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(scope));
        var open = Assert.Single(typeof(SettingsStore).GetMethods(), method => method.Name == "OpenCurrentConfigurationReadAsync");
        Assert.Equal(typeof(Task<ConfigurationCurrentReadScope>), open.ReturnType);
        var token = Assert.Single(open.GetParameters());
        Assert.Equal(typeof(CancellationToken), token.ParameterType);
        Assert.True(token.HasDefaultValue);
        Assert.DoesNotContain(typeof(SettingsStore).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method => method.GetParameters().Any(parameter => parameter.ParameterType == inspection || parameter.ParameterType == scope));
    }

    private static ConfigurationRestoreApproval Approve(ConfigurationRestorePlan plan) =>
        plan.Approve(plan.SnapshotDigest, plan.Destination, plan.ExpectedRevision);

    private void AssertPrivate(string? text)
    {
        Assert.NotNull(text);
        Assert.DoesNotContain(Canary, text, StringComparison.Ordinal);
        Assert.DoesNotContain(directory, text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"schema_version\"", text, StringComparison.Ordinal);
    }

    private void AssertWriterHeld() =>
        Assert.Throws<IOException>(() => new FileStream(Store.FilePath + ".lock", FileMode.Open, FileAccess.Write, FileShare.None).Dispose());

    private void AssertWriterReleased()
    {
        using var writer = new FileStream(Store.FilePath + ".lock", FileMode.Open, FileAccess.Write, FileShare.None);
    }

    private static void AssertPinned(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Throws<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose());
        Assert.Throws<IOException>(() => File.WriteAllText(path, Canary));
        Assert.Throws<IOException>(() => File.Move(path, path + ".denied"));
        Assert.Throws<IOException>(() => File.Delete(path));
    }

    private void AssertPinsReleased()
    {
        using (new FileStream(Store.FilePath, FileMode.Open, FileAccess.Write, FileShare.None)) { }
        File.Move(Store.FilePath, Store.FilePath + ".released");
        File.Move(Store.FilePath + ".released", Store.FilePath);
    }

    private async Task<RecoveryException> Failure(RecoveryFailure failure, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<RecoveryException>(action);
        Assert.Equal(failure, error.Failure);
        AssertPrivate(error.ToString());
        return error;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
