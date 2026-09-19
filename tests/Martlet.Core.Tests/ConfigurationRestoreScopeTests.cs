using System.Reflection;
using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class ConfigurationRestoreScopeTests : IDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "restore-scope-fixtures", Guid.NewGuid().ToString("N"));
    private SettingsStore Store => new(directory);
    private string Backup => Path.Combine(directory, "private.martlet-config");
    private static readonly Action NoVeto = static () => { };

    private async Task<ConfigurationRestorePlan> Prepare()
    {
        var settings = SetupSettings.SelectRoute(SetupSettings.Begin(null), SetupRole.Tts, "tts-1", "alloy");
        Assert.True((await Store.SaveAsync(settings, null)).Saved);
        await Store.CreateConfigurationSnapshotAsync(Backup);
        return await Store.PreviewConfigurationRestoreAsync(Backup);
    }

    private static ConfigurationRestoreApproval Approve(ConfigurationRestorePlan plan) =>
        plan.Approve(plan.SnapshotDigest, plan.Destination, plan.ExpectedRevision);

    [Fact]
    public async Task OpeningConsumesApprovalAndOwnsWriterSourceAndCurrentWithoutCreatingOriginal()
    {
        var plan = await Prepare();
        var approval = Approve(plan);
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        var source = await File.ReadAllBytesAsync(Backup);
        var scope = await Store.OpenConfigurationRestoreAsync(plan, approval);
        try
        {
            Assert.Equal(Store.FilePath, scope.Destination);
            Assert.Equal(plan.ProfileId, scope.ProfileId);
            Assert.Equal(plan.ExpectedRevision, scope.ExpectedRevision);
            Assert.Equal(ConfigurationSnapshot.Hash(source), scope.SourceFileDigest);
            Assert.Equal(plan.CandidateDigest, scope.CandidateDigest);
            Assert.Equal(directory, Path.GetDirectoryName(scope.OriginalSnapshotPath));
            Assert.StartsWith("settings.recovery.", Path.GetFileName(scope.OriginalSnapshotPath), StringComparison.Ordinal);
            Assert.False(File.Exists(scope.OriginalSnapshotPath));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            Assert.Equal(ConfigurationRestoreCommitState.NotStarted, scope.CommitState);
            Assert.Null(scope.Cleanup);
            Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
            Assert.False((await Store.SaveAsync(SettingsJson.Read(original), plan.ExpectedRevision)).Saved);
            AssertWriterHeld();
            AssertPinned(Backup);
            AssertPinned(Store.FilePath);
            await Conflict(() => Store.OpenConfigurationRestoreAsync(plan, approval));
            await Conflict(() => scope.VerifyCommittedAsync());
        }
        finally { await scope.DisposeAsync(); }
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
        Assert.Equal(source, await File.ReadAllBytesAsync(Backup));
        Assert.Empty(Directory.GetFiles(directory, "settings.recovery.*.bak"));
        AssertWriterReleased();
        await Conflict(() => scope.CommitAsync(NoVeto, NoVeto));
        await Conflict(() => scope.VerifyCommittedAsync());
        await scope.DisposeAsync();
    }

    [Theory]
    [InlineData("source")]
    [InlineData("current")]
    [InlineData("store")]
    [InlineData("canceled")]
    [InlineData("busy")]
    public async Task FailedOpenConsumesApprovalAndDoesNotWriteConfiguration(string failure)
    {
        var plan = await Prepare();
        var approval = Approve(plan);
        using var canceled = new CancellationTokenSource();
        if (failure == "source") await File.AppendAllTextAsync(Backup, "\n");
        if (failure == "current") await File.AppendAllTextAsync(Store.FilePath, "\n");
        if (failure == "canceled") canceled.Cancel();
        using var held = failure == "busy"
            ? new FileStream(Store.FilePath + ".lock", FileMode.Open, FileAccess.Write, FileShare.None) : null;
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        var store = failure == "store" ? new SettingsStore(Path.Combine(directory, "other")) : Store;
        if (failure == "canceled")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.OpenConfigurationRestoreAsync(plan, approval, canceled.Token));
        else
            await Failure(failure == "busy" ? RecoveryFailure.Unavailable : RecoveryFailure.Conflict,
                () => store.OpenConfigurationRestoreAsync(plan, approval));
        await Conflict(() => Store.OpenConfigurationRestoreAsync(plan, approval));
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
        Assert.Empty(Directory.GetFiles(directory, "settings.recovery.*.bak"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongPlanPairingConsumesTheApprovalAcrossAllStoreInstancesAndRestoreEntryPoints(bool standalone)
    {
        var plan = await Prepare();
        var wrongPlan = await Store.PreviewConfigurationRestoreAsync(Backup);
        var approval = Approve(plan);
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        await Conflict(() => standalone ? Store.RestoreConfigurationAsync(wrongPlan, approval) :
            Store.OpenConfigurationRestoreAsync(wrongPlan, approval));
        await Conflict(() => Store.OpenConfigurationRestoreAsync(plan, approval));
        await Conflict(() => Store.RestoreConfigurationAsync(plan, approval));
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
        Assert.Empty(Directory.GetFiles(directory, "settings.recovery.*.bak"));
        AssertWriterReleased();
        var fresh = await Store.PreviewConfigurationRestoreAsync(Backup);
        var restored = await Store.RestoreConfigurationAsync(fresh, Approve(fresh));
        Assert.Equal(fresh.CandidateDigest, restored.Revision);
        Assert.Equal(original, await File.ReadAllBytesAsync(restored.OriginalSnapshot!));
    }

    [Fact]
    public async Task ConcurrentAttemptsCannotConsumeTheSameApprovalTwice()
    {
        var plan = await Prepare();
        var approval = Approve(plan);
        var attempts = await Task.WhenAll(OpenOnce(), OpenOnce());
        var winner = Assert.Single(attempts.OfType<ConfigurationRestoreScope>());
        Assert.Single(attempts, scope => scope is null);
        Assert.Empty(Directory.GetFiles(directory, "settings.recovery.*.bak"));
        await winner.DisposeAsync();
        AssertWriterReleased();
        await Conflict(() => Store.OpenConfigurationRestoreAsync(plan, approval));

        async Task<ConfigurationRestoreScope?> OpenOnce()
        {
            try { return await Store.OpenConfigurationRestoreAsync(plan, approval); }
            catch (RecoveryException error) { Assert.Equal(RecoveryFailure.Conflict, error.Failure); return null; }
        }
    }

    [Fact]
    public async Task RealCommitEvidenceKeepsExactOriginalResultSourceAndWriterUntilRetirement()
    {
        var plan = await Prepare();
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        // Preserve a valid noncanonical original, not a reserialized version of the settings.
        original = Encoding.UTF8.GetBytes("\n" + Encoding.UTF8.GetString(original) + "\n");
        await File.WriteAllBytesAsync(Store.FilePath, original);
        plan = await Store.PreviewConfigurationRestoreAsync(Backup);
        var scope = await Store.OpenConfigurationRestoreAsync(plan, Approve(plan));
        ConfigurationRestoreEvidence evidence;
        try
        {
            await scope.CommitAsync(NoVeto, () =>
            {
                Assert.Equal(ConfigurationRestoreCommitState.NotCommitted, scope.CommitState);
                Assert.Equal(original, File.ReadAllBytes(Store.FilePath));
                AssertPinned(Store.FilePath);
                AssertPinned(scope.OriginalSnapshotPath);
                AssertPinned(Backup);
            });
            Assert.Equal(ConfigurationRestoreCommitState.ReplacementReturned, scope.CommitState);
            evidence = await scope.VerifyCommittedAsync();
            Assert.Equal(ConfigurationRestoreCommitState.VerifiedCommitted, scope.CommitState);
            Assert.Equal(Store.FilePath, evidence.Path);
            Assert.Equal(plan.CandidateDigest, evidence.Revision);
            Assert.Equal(plan.CandidateDigest.ToUpperInvariant(), evidence.Revision);
            Assert.Equal(plan.ProfileId, evidence.ProfileId);
            Assert.Equal(AppSettings.CurrentSchemaVersion, evidence.SchemaVersion);
            Assert.Equal(scope.OriginalSnapshotPath, evidence.OriginalSnapshot);
            Assert.Equal(ConfigurationSnapshot.Hash(original), evidence.OriginalRevision);
            Assert.Equal(original, await File.ReadAllBytesAsync(evidence.OriginalSnapshot));
            Assert.Equal(plan.CandidateJson, await File.ReadAllTextAsync(evidence.Path));
            Assert.DoesNotContain(directory, scope.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(directory, evidence.ToString(), StringComparison.Ordinal);
            AssertWriterHeld();
            AssertPinned(Backup);
            AssertPinned(evidence.Path);
            AssertPinned(evidence.OriginalSnapshot);
            await Conflict(() => scope.CommitAsync(NoVeto, NoVeto));
            Assert.Equal(evidence.Revision, (await scope.VerifyCommittedAsync()).Revision);
        }
        finally { await scope.DisposeAsync(); }
        AssertWriterReleased();
        await Conflict(() => scope.VerifyCommittedAsync());
        // Evidence remains an observation only; it cannot keep or regain ownership.
        await File.AppendAllTextAsync(evidence.Path, "\n");
        Assert.NotEqual(evidence.Revision, ConfigurationSnapshot.Hash(await File.ReadAllBytesAsync(evidence.Path)));
    }

    [Theory]
    [InlineData("before-original")]
    [InlineData("before-replacement")]
    [InlineData("after-replacement")]
    public async Task DisposalAndConcurrentCallsCannotReleaseAnActualInFlightOperation(string boundary)
    {
        var plan = await Prepare();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var stages = 0;
        var commits = 0;
        var store = new SettingsStore(directory) { RecoveryIo = (point, _) =>
        {
            if (point == SettingsIoPoint.BeforeStage) stages++;
            if (point == SettingsIoPoint.AfterCommit) commits++;
            if ((boundary == "before-original" && point == SettingsIoPoint.BeforeStage && stages == 1) ||
                (boundary == "before-replacement" && point == SettingsIoPoint.BeforeCommit && stages == 2) ||
                (boundary == "after-replacement" && point == SettingsIoPoint.AfterCommit && commits == 2))
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException();
            }
        } };
        var scope = await store.OpenConfigurationRestoreAsync(plan, Approve(plan));
        var work = Task.Run(() => scope.CommitAsync(NoVeto, NoVeto));
        Task? disposal = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            await Conflict(() => scope.CommitAsync(NoVeto, NoVeto));
            await Conflict(() => scope.VerifyCommittedAsync());
            disposal = scope.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            AssertWriterHeld();
            AssertPinned(Backup);
            AssertPinned(Store.FilePath);
            if (boundary != "before-original") AssertPinned(scope.OriginalSnapshotPath);
            await Conflict(() => scope.VerifyCommittedAsync());
        }
        finally
        {
            release.Set();
            await work;
            await (disposal ?? scope.DisposeAsync().AsTask());
        }
        Assert.Equal(ConfigurationRestoreCommitState.ReplacementReturned, scope.CommitState);
        AssertWriterReleased();
        Assert.Equal(plan.CandidateJson, await File.ReadAllTextAsync(Store.FilePath));
        await Conflict(() => scope.VerifyCommittedAsync());
    }

    [Theory]
    [InlineData("io")]
    [InlineData("cancel")]
    public async Task SecondAfterCommitFailureNeverErasesRealReplacementAndCanStillBeVerified(string interruption)
    {
        var plan = await Prepare();
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        using var stop = new CancellationTokenSource();
        var commits = 0;
        ConfigurationRestoreScope? scope = null;
        var store = new SettingsStore(directory) { RecoveryIo = (point, observed) =>
        {
            Assert.Equal(stop.Token, observed);
            if (point != SettingsIoPoint.AfterCommit || ++commits != 2) return;
            Assert.Equal(ConfigurationRestoreCommitState.ReplacementReturned, scope!.CommitState);
            AssertPinned(Store.FilePath);
            if (interruption == "io") throw new IOException("PRIVATE-POSTCOMMIT-CANARY");
            stop.Cancel();
        } };
        await using (scope = await store.OpenConfigurationRestoreAsync(plan, Approve(plan), stop.Token))
        {
            if (interruption == "io")
            {
                var error = await Failure(RecoveryFailure.Unavailable, () => scope.CommitAsync(NoVeto, NoVeto));
                Assert.DoesNotContain("PRIVATE-POSTCOMMIT-CANARY", error.ToString(), StringComparison.Ordinal);
            }
            else await scope.CommitAsync(NoVeto, NoVeto);
            Assert.Equal(ConfigurationRestoreCommitState.ReplacementReturned, scope.CommitState);
            var evidence = await scope.VerifyCommittedAsync();
            Assert.Equal(plan.CandidateDigest, evidence.Revision);
            Assert.Equal(original, await File.ReadAllBytesAsync(evidence.OriginalSnapshot));
            Assert.Equal(ConfigurationRestoreCommitState.VerifiedCommitted, scope.CommitState);
            await Conflict(() => scope.CommitAsync(NoVeto, NoVeto));
        }
    }

    [Fact]
    public async Task RealRenameFailureIsOutcomeUnknownNotDefinitelyUncommitted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var plan = await Prepare();
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        await using var scope = await Store.OpenConfigurationRestoreAsync(plan, Approve(plan));
        using var denied = new FileStream(Store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await Failure(RecoveryFailure.Unavailable, () => scope.CommitAsync(NoVeto, NoVeto));
        Assert.Equal(ConfigurationRestoreCommitState.OutcomeUnknown, scope.CommitState);
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
        Assert.Equal(original, await File.ReadAllBytesAsync(scope.OriginalSnapshotPath));
        await Conflict(() => scope.VerifyCommittedAsync());
        await Conflict(() => scope.CommitAsync(NoVeto, NoVeto));
        Assert.Null(scope.Cleanup);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualReadbackFailureCannotProduceEvidenceOrResetReplacementState(bool afterCommit)
    {
        var plan = await Prepare();
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        var failRead = false;
        ConfigurationRestoreScope? scope = null;
        var store = new SettingsStore(directory) { RecoveryIo = (point, _) =>
        {
            if (point == SettingsIoPoint.BeforeRead &&
                (failRead || (!afterCommit && scope?.CommitState == ConfigurationRestoreCommitState.ReplacementReturned)))
                throw new IOException("PRIVATE-READBACK-CANARY");
        } };
        await using (scope = await store.OpenConfigurationRestoreAsync(plan, Approve(plan)))
        {
            if (afterCommit)
            {
                await scope.CommitAsync(NoVeto, NoVeto);
                failRead = true;
                await Failure(RecoveryFailure.Unavailable, () => scope.VerifyCommittedAsync());
            }
            else await Failure(RecoveryFailure.Unavailable, () => scope.CommitAsync(NoVeto, NoVeto));
            Assert.Equal(ConfigurationRestoreCommitState.ReplacementReturned, scope.CommitState);
            Assert.Equal(plan.CandidateJson, await File.ReadAllTextAsync(Store.FilePath));
            Assert.Equal(original, await File.ReadAllBytesAsync(scope.OriginalSnapshotPath));
            AssertPinned(Store.FilePath);
            AssertPinned(scope.OriginalSnapshotPath);
            Assert.Null(scope.Cleanup);
            await Conflict(() => scope.CommitAsync(NoVeto, NoVeto));
        }
    }

    [Fact]
    public async Task DisposalWaitsForActualVerificationAndRejectsParallelOrLaterVerification()
    {
        var plan = await Prepare();
        var verifying = false;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var store = new SettingsStore(directory) { RecoveryIo = (point, _) =>
        {
            if (!verifying || point != SettingsIoPoint.BeforeRead) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException();
        } };
        var scope = await store.OpenConfigurationRestoreAsync(plan, Approve(plan));
        await scope.CommitAsync(NoVeto, NoVeto);
        verifying = true;
        var work = Task.Run(() => scope.VerifyCommittedAsync());
        Task? disposal = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            await Conflict(() => scope.VerifyCommittedAsync());
            disposal = scope.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            AssertWriterHeld();
            AssertPinned(Store.FilePath);
            AssertPinned(scope.OriginalSnapshotPath);
            AssertPinned(Backup);
        }
        finally
        {
            release.Set();
            await work;
            await (disposal ?? scope.DisposeAsync().AsTask());
        }
        Assert.Equal(ConfigurationRestoreCommitState.VerifiedCommitted, scope.CommitState);
        AssertWriterReleased();
        await Conflict(() => scope.VerifyCommittedAsync());
    }

    [Fact]
    public async Task GeneratedOriginalIsCreateOnlyAndNeverOverwritesAnExistingFile()
    {
        var plan = await Prepare();
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        await using var scope = await Store.OpenConfigurationRestoreAsync(plan, Approve(plan));
        await File.WriteAllTextAsync(scope.OriginalSnapshotPath, "EXISTING-ORIGINAL-CANARY");
        await Failure(RecoveryFailure.Unavailable, () => scope.CommitAsync(NoVeto, NoVeto));
        Assert.Equal(ConfigurationRestoreCommitState.NotCommitted, scope.CommitState);
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
        Assert.Equal("EXISTING-ORIGINAL-CANARY", await File.ReadAllTextAsync(scope.OriginalSnapshotPath));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task ValidationAfterBlockedIoStopsEffectsAndRetiresOnlyOwnedScratch()
    {
        var plan = await Prepare();
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        var expired = false;
        var store = new SettingsStore(directory) { RecoveryIo = (point, _) =>
        {
            if (point == SettingsIoPoint.AfterWrite) expired = true;
        } };
        var scope = await store.OpenConfigurationRestoreAsync(plan, Approve(plan));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.CommitAsync(() =>
        {
            if (expired) throw new InvalidOperationException("expired");
        }, NoVeto));
        Assert.Null(scope.Cleanup);
        Assert.Equal(ConfigurationRestoreCommitState.NotCommitted, scope.CommitState);
        Assert.False(File.Exists(scope.OriginalSnapshotPath));
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        await scope.DisposeAsync();
    }

    [Fact]
    public async Task OriginalTokenStopsReplacementWhileItsCancellationCallbackIsStillBlocked()
    {
        var plan = await Prepare();
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        using var stop = new CancellationTokenSource();
        using var cancelEntered = new ManualResetEventSlim();
        using var cancelRelease = new ManualResetEventSlim();
        using var registration = stop.Token.Register(() =>
        {
            cancelEntered.Set();
            if (!cancelRelease.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException();
        });
        Task? cancel = null;
        var stages = 0;
        var store = new SettingsStore(directory) { RecoveryIo = (point, _) =>
        {
            if (point == SettingsIoPoint.BeforeStage) stages++;
            if (point != SettingsIoPoint.BeforeCommit || stages != 2) return;
            cancel = Task.Run(stop.Cancel);
            if (!cancelEntered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        } };
        var scope = await store.OpenConfigurationRestoreAsync(plan, Approve(plan), stop.Token);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.CommitAsync(NoVeto, NoVeto));
            Assert.NotNull(cancel);
            Assert.False(cancel.IsCompleted);
            Assert.Equal(ConfigurationRestoreCommitState.NotCommitted, scope.CommitState);
            Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
            Assert.Equal(original, await File.ReadAllBytesAsync(scope.OriginalSnapshotPath));
            Assert.Null(scope.Cleanup);
        }
        finally
        {
            cancelRelease.Set();
            if (cancel is not null) await cancel;
            await scope.DisposeAsync();
        }
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData("validation")]
    [InlineData("replacement")]
    [InlineData("original-token")]
    public async Task VetoesAndCapturedOriginalCancellationCannotAuthorizeReplacement(string veto)
    {
        var plan = await Prepare();
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        using var stop = new CancellationTokenSource();
        await using var scope = await Store.OpenConfigurationRestoreAsync(plan, Approve(plan), stop.Token);
        if (veto == "original-token") stop.Cancel();
        Action validate = veto == "validation" ? () => throw new InvalidOperationException("veto") : NoVeto;
        Action replace = veto == "replacement" ? () => throw new InvalidOperationException("veto") : NoVeto;
        if (veto == "original-token") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.CommitAsync(validate, replace));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => scope.CommitAsync(validate, replace));
        Assert.Equal(ConfigurationRestoreCommitState.NotCommitted, scope.CommitState);
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
        await Conflict(() => scope.CommitAsync(NoVeto, NoVeto));
        await Conflict(() => scope.VerifyCommittedAsync());
    }

    [Fact]
    public async Task CleanupCapabilityRetainsOnlyOwnedTemporaryAcrossDisposalAndExplicitRetries()
    {
        var plan = await Prepare();
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        var source = await File.ReadAllBytesAsync(Backup);
        var stages = 0;
        var failCleanup = true;
        var store = new SettingsStore(directory) { RecoveryIo = (point, _) =>
        {
            if (point == SettingsIoPoint.BeforeStage) stages++;
            if (point == SettingsIoPoint.AfterWrite && stages == 2) throw new IOException("stage failed");
            if (point == SettingsIoPoint.BeforeCleanup && failCleanup) throw new UnauthorizedAccessException();
        } };
        var scope = await store.OpenConfigurationRestoreAsync(plan, Approve(plan));
        var error = await Failure(RecoveryFailure.CleanupPending, () => scope.CommitAsync(NoVeto, NoVeto));
        var cleanup = Assert.IsType<ConfigurationRestoreCleanup>(scope.Cleanup);
        Assert.Equal(error.RetainedFile, cleanup.Path);
        Assert.DoesNotContain(directory, cleanup.ToString(), StringComparison.Ordinal);
        Assert.True(cleanup.IsPending);
        Assert.Equal(directory, Path.GetDirectoryName(cleanup.Path));
        Assert.Equal(".tmp", Path.GetExtension(cleanup.Path));
        Assert.NotEqual(scope.OriginalSnapshotPath, cleanup.Path);
        Assert.NotEqual(Backup, cleanup.Path);
        Assert.NotEqual(Store.FilePath, cleanup.Path);
        await scope.DisposeAsync();
        AssertWriterReleased();
        Assert.True(cleanup.IsPending);
        Assert.True(File.Exists(cleanup.Path));
        await Failure(RecoveryFailure.CleanupPending, () => cleanup.RetryAsync());
        Assert.True(cleanup.IsPending);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cleanup.RetryAsync(stop.Token));
        Assert.True(cleanup.IsPending);
        failCleanup = false;
        await cleanup.RetryAsync();
        await cleanup.RetryAsync();
        Assert.False(cleanup.IsPending);
        Assert.False(File.Exists(cleanup.Path));
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
        Assert.Equal(original, await File.ReadAllBytesAsync(scope.OriginalSnapshotPath));
        Assert.Equal(source, await File.ReadAllBytesAsync(Backup));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScratchNameSubstitutionIsDetectedWithoutDeletingUnknownReplacement(bool inVetoCallback)
    {
        var plan = await Prepare();
        var original = await File.ReadAllBytesAsync(Store.FilePath);
        string? moved = null;
        string? substituted = null;
        var stages = 0;
        var store = new SettingsStore(directory) { RecoveryIo = (point, _) =>
        {
            if (point == SettingsIoPoint.BeforeStage) stages++;
            if (inVetoCallback || point != SettingsIoPoint.BeforeCommit || stages != 2) return;
            Substitute();
        } };
        void Substitute()
        {
            substituted = Assert.Single(Directory.GetFiles(directory, "*.tmp"));
            moved = substituted + ".moved";
            File.Move(substituted, moved);
            File.WriteAllText(substituted, "UNOWNED-SUBSTITUTION");
        }
        var scope = await store.OpenConfigurationRestoreAsync(plan, Approve(plan));
        await Failure(RecoveryFailure.CleanupPending, () => scope.CommitAsync(NoVeto, inVetoCallback ? Substitute : NoVeto));
        Assert.Equal(ConfigurationRestoreCommitState.NotCommitted, scope.CommitState);
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
        Assert.Equal("UNOWNED-SUBSTITUTION", await File.ReadAllTextAsync(substituted!));
        Assert.Equal(plan.CandidateJson, await File.ReadAllTextAsync(moved!));
        var cleanup = Assert.IsType<ConfigurationRestoreCleanup>(scope.Cleanup);
        await scope.DisposeAsync();
        await Failure(RecoveryFailure.CleanupPending, () => cleanup.RetryAsync());
        Assert.Equal("UNOWNED-SUBSTITUTION", await File.ReadAllTextAsync(substituted!));
        // Only the fixture owner removes its injected replacement and restores the owned scratch name.
        File.Delete(substituted!);
        File.Move(moved!, substituted!);
        await cleanup.RetryAsync();
        Assert.False(cleanup.IsPending);
    }

    [Fact]
    public async Task FlushedRestoreScratchIsActuallyPinnedAgainstWritesBeforeBothRenames()
    {
        if (!OperatingSystem.IsWindows()) return;
        var plan = await Prepare();
        var checkedScratch = 0;
        var store = new SettingsStore(directory) { RecoveryIo = (point, _) =>
        {
            if (point != SettingsIoPoint.BeforeCommit) return;
            var scratch = Assert.Single(Directory.GetFiles(directory, "*.tmp"));
            Assert.Throws<IOException>(() =>
                new FileStream(scratch, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose());
            checkedScratch++;
        } };
        await using var scope = await store.OpenConfigurationRestoreAsync(plan, Approve(plan));
        await scope.CommitAsync(NoVeto, NoVeto);
        Assert.Equal(2, checkedScratch);
        Assert.Equal(plan.CandidateDigest, (await scope.VerifyCommittedAsync()).Revision);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void CapabilitiesAndEvidenceHaveNoPublicConstructorSetterOrAuthorityIngestion()
    {
        foreach (var type in new[] { typeof(ConfigurationRestoreScope), typeof(ConfigurationRestoreCleanup), typeof(ConfigurationRestoreEvidence) })
        {
            Assert.True(type.IsSealed);
            Assert.Empty(type.GetConstructors());
            Assert.All(type.GetProperties(BindingFlags.Public | BindingFlags.Instance), property => Assert.Null(property.SetMethod));
            Assert.DoesNotContain(type.GetMethods(BindingFlags.Public | BindingFlags.Instance),
                method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(ConfigurationRestoreEvidence)));
        }
        Assert.DoesNotContain(typeof(SettingsStore).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(ConfigurationRestoreEvidence)));
        Assert.Single(typeof(ConfigurationRestoreCleanup).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => method.Name == nameof(ConfigurationRestoreCleanup.RetryAsync));
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
        Assert.Throws<IOException>(() => File.Move(path, path + ".denied"));
        Assert.Throws<IOException>(() => File.Delete(path));
    }

    private static Task Conflict(Func<Task> action) => Failure(RecoveryFailure.Conflict, action);

    private static async Task<RecoveryException> Failure(RecoveryFailure failure, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<RecoveryException>(action);
        Assert.Equal(failure, error.Failure);
        return error;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
