using Martlet.Core.Settings;
using static Martlet.Updates.Tests.SelectionFixture;

namespace Martlet.Updates.Tests;

public sealed class SelectionInterruptionTests(SigningKeys keys) : IClassFixture<SigningKeys>
{
    public static IEnumerable<object[]> DurableWrites()
    {
        foreach (var file in new[] { "before.json", "configuration.martlet-config", "journal-v2.json", "pending.json", "selected.json" })
            foreach (var point in new[] { SelectionIoPoint.BeforeCreate, SelectionIoPoint.AfterCreate,
                SelectionIoPoint.BeforeWrite, SelectionIoPoint.AfterWrite, SelectionIoPoint.BeforeFlush, SelectionIoPoint.AfterFlush })
                yield return [file, (int)point];
        yield return ["directory", (int)SelectionIoPoint.BeforeCreate];
        yield return ["directory", (int)SelectionIoPoint.AfterCreate];
        yield return ["directory", (int)SelectionIoPoint.BeforePendingReplace];
        yield return ["directory", (int)SelectionIoPoint.AfterPendingReplace];
        yield return ["directory", (int)SelectionIoPoint.BeforeSelectionReplace];
    }

    [Theory]
    [MemberData(nameof(DurableWrites))]
    public void InterruptedEveryDurableWritePreservesOriginalAndRecoveryIsIdempotent(string file, int boundary)
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        var stage = f.Stage();
        var original = File.ReadAllBytes(f.Settings.FilePath);
        var control = File.ReadAllBytes(f.Control);
        var hit = false;
        var engine = f.Engine((point, path, _) =>
        {
            if (hit || point != (SelectionIoPoint)boundary ||
                (file == "directory" ? !Path.GetFileName(path).StartsWith("transaction-", StringComparison.Ordinal) :
                    Path.GetFileName(path) != file)) return;
            hit = true;
            throw new IOException("Synthetic private storage interruption");
        });
        var plan = f.Prepare(engine, stage);
        var error = Assert.Throws<SelectionException>(() => engine.CommitSelection(plan, Approve(plan)));
        Assert.Equal(SelectionFailure.Unavailable, error.Failure);
        Assert.Equal(plan.TransactionId, error.TransactionId);
        Assert.True(hit);
        var directory = Path.Combine(f.Root, "transaction-" + plan.TransactionId.ToString("N"));
        var exists = Directory.Exists(directory);
        string? foreign = null;
        if (exists)
        {
            foreign = Path.Combine(directory, "unknown-owner");
            File.WriteAllText(foreign, "NEVER DELETE THIS");
        }
        var afterFailure = f.Engine().Inspect();
        var recovered = f.Engine().Recover(exists ? plan.TransactionId : null);
        Assert.Null(recovered.CurrentSelection);
        Assert.False(recovered.IsRunnable);
        Assert.Equal(afterFailure.Status == SelectionStatus.RecoveryRequired
            ? SelectionOutcome.RecoveredOriginal : SelectionOutcome.Unchanged, recovered.Outcome);
        var twice = f.Engine().Recover(exists ? plan.TransactionId : null);
        Assert.Equal(SelectionOutcome.Unchanged, twice.Outcome);
        Assert.Equal(recovered.Revision, twice.Revision);
        Assert.Null(twice.CurrentSelection);
        Assert.Equal(original, File.ReadAllBytes(f.Settings.FilePath));
        if (recovered.Revision == 0) Assert.Equal(control, File.ReadAllBytes(f.Control));
        if (foreign is not null) Assert.Equal("NEVER DELETE THIS", File.ReadAllText(foreign));
        f.Package.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData((int)SelectionIoPoint.BeforeCreate)]
    [InlineData((int)SelectionIoPoint.AfterCreate)]
    [InlineData((int)SelectionIoPoint.BeforeWrite)]
    [InlineData((int)SelectionIoPoint.AfterWrite)]
    [InlineData((int)SelectionIoPoint.BeforeFlush)]
    [InlineData((int)SelectionIoPoint.AfterFlush)]
    [InlineData((int)SelectionIoPoint.BeforeRecoveryReplace)]
    public void RecoveryItselfCanBeInterruptedAndRetriedWithoutOverwrite(int boundary)
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        var stage = f.Stage();
        var engine = f.Engine((point, _, _) =>
        {
            if (point == SelectionIoPoint.AfterPendingReplace) throw new IOException();
        });
        var plan = f.Prepare(engine, stage);
        Assert.Throws<SelectionException>(() => engine.CommitSelection(plan, Approve(plan)));
        Assert.Equal(SelectionStatus.RecoveryRequired, f.Engine().Inspect().Status);
        var recovery = f.Engine((point, _, _) =>
        {
            if (point == (SelectionIoPoint)boundary) throw new IOException();
        });
        Assert.Throws<SelectionException>(() => recovery.Recover());
        var receipt = f.Engine().Recover();
        Assert.Equal(SelectionOutcome.RecoveredOriginal, receipt.Outcome);
        Assert.Equal(SelectionStatus.BootstrapUnqualified, receipt.Status);
        var again = f.Engine().Recover();
        Assert.Equal(receipt.Revision, again.Revision);
        Assert.Equal(SelectionOutcome.Unchanged, again.Outcome);
    }

    [Theory]
    [InlineData((int)SelectionIoPoint.BeforePendingReplace)]
    [InlineData((int)SelectionIoPoint.AfterPendingReplace)]
    [InlineData((int)SelectionIoPoint.BeforeSelectionReplace)]
    [InlineData((int)SelectionIoPoint.AfterSelectionReplace)]
    public async Task OriginalCancellationIsVisibleWhileCallbackIsBlockedAndFinalizationWins(int boundary)
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        var stage = f.Stage();
        using var cancellation = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var registration = cancellation.Token.Register(() =>
        {
            entered.Set();
            release.Wait();
        });
        Task? cancellationTask = null;
        var engine = f.Engine((point, _, _) =>
        {
            if (point != (SelectionIoPoint)boundary) return;
            cancellationTask = cancellation.CancelAsync();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        });
        var plan = f.Prepare(engine, stage);
        try
        {
            if ((SelectionIoPoint)boundary == SelectionIoPoint.AfterSelectionReplace)
            {
                var result = engine.CommitSelection(plan, Approve(plan), cancellation.Token);
                Assert.Equal(SelectionOutcome.Committed, result.Outcome);
                Assert.Equal(SelectionStatus.AwaitingReadiness, result.Status);
                Assert.Equal("0.2.0.0", f.Engine().Recover().CurrentSelection!.Version);
            }
            else
            {
                var error = Assert.Throws<SelectionException>(() => engine.CommitSelection(plan, Approve(plan), cancellation.Token));
                Assert.Equal(SelectionFailure.Cancelled, error.Failure);
                Assert.Null(f.Engine().Recover().CurrentSelection);
            }
            Assert.NotNull(cancellationTask);
            Assert.False(cancellationTask.IsCompleted);
        }
        finally
        {
            release.Set();
            if (cancellationTask is not null) await cancellationTask;
        }
    }

    [Fact]
    public async Task BlockedSynchronousWriteRetainsBothOwnersUntilActualCompletion()
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        var stage = f.Stage();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var engine = f.Engine((point, _, _) =>
        {
            if (point != SelectionIoPoint.BeforeSelectionReplace) return;
            entered.Set();
            release.Wait();
        });
        var plan = f.Prepare(engine, stage);
        var operation = Task.Run(() => engine.CommitSelection(plan, Approve(plan), cancellation.Token));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            cancellation.Cancel();
            Assert.False(operation.IsCompleted);
            Assert.Equal(SelectionFailure.Busy, Assert.Throws<SelectionException>(() => f.Engine().Recover()).Failure);
            Assert.Equal(SelectionFailure.Busy, Assert.Throws<SelectionException>(() => engine.Inspect()).Failure);
            Assert.Equal(StagingFailure.Busy, Assert.Throws<StagingException>(() => f.Staging.InspectStaged(stage)).Failure);
        }
        finally { release.Set(); }
        var error = await Assert.ThrowsAsync<SelectionException>(async () => await operation);
        Assert.Equal(SelectionFailure.Cancelled, error.Failure);
        Assert.Equal(SelectionOutcome.RecoveredOriginal, f.Engine().Recover().Outcome);
    }

    [Theory]
    [InlineData(false, SelectionFailure.AccessDenied)]
    [InlineData(true, SelectionFailure.InsufficientDisk)]
    public void StorageFailuresRetainExactEvidenceAndNeverCleanUnknownChildren(bool full, SelectionFailure failure)
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        var stage = f.Stage();
        var engine = f.Engine((point, path, _) =>
        {
            if (point != SelectionIoPoint.BeforeFlush || Path.GetFileName(path) != "pending.json") return;
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "foreign"), "PRIVATE OTHER OWNER");
            if (full) throw new DiskFull();
            throw new UnauthorizedAccessException();
        });
        var plan = f.Prepare(engine, stage);
        Assert.Equal(failure, Assert.Throws<SelectionException>(() => engine.CommitSelection(plan, Approve(plan))).Failure);
        var directory = Path.Combine(f.Root, "transaction-" + plan.TransactionId.ToString("N"));
        Assert.True(File.Exists(Path.Combine(directory, "before.json")));
        Assert.True(File.Exists(Path.Combine(directory, "configuration.martlet-config")));
        f.Engine().Recover(plan.TransactionId);
        Assert.Equal("PRIVATE OTHER OWNER", File.ReadAllText(Path.Combine(directory, "foreign")));
        Assert.True(Directory.Exists(stage));
        f.Package.AssertPrivateDataUnchanged();
    }

    [Fact]
    public void CorruptPendingJournalCannotBeUsedToGuessOriginal()
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        var stage = f.Stage();
        var engine = f.Engine((point, _, _) =>
        {
            if (point == SelectionIoPoint.AfterPendingReplace) throw new IOException();
        });
        var plan = f.Prepare(engine, stage);
        Assert.Throws<SelectionException>(() => engine.CommitSelection(plan, Approve(plan)));
        var directory = Path.Combine(f.Root, "transaction-" + plan.TransactionId.ToString("N"));
        File.WriteAllText(Path.Combine(directory, "before.json"), "{}");
        var control = File.ReadAllBytes(f.Control);
        Assert.Equal(SelectionFailure.InvalidControl, Assert.Throws<SelectionException>(() => f.Engine().Recover()).Failure);
        Assert.Equal(control, File.ReadAllBytes(f.Control));
    }

    [Fact]
    public void SimulatedProcessExitAfterAtomicSelectionLeavesCommittedUnreadySelection()
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        var stage = f.Stage();
        var engine = f.Engine((point, _, _) =>
        {
            if (point == SelectionIoPoint.AfterSelectionReplace) throw new SimulatedTermination();
        });
        var plan = f.Prepare(engine, stage);
        Assert.Throws<SimulatedTermination>(() => engine.CommitSelection(plan, Approve(plan)));
        var result = f.Engine().Recover();
        Assert.Equal(2, result.Revision);
        Assert.Equal(SelectionStatus.AwaitingReadiness, result.Status);
        Assert.False(result.IsRunnable);
        Assert.Equal("0.2.0.0", result.CurrentSelection!.Version);
        Assert.Equal(result.Revision, f.Engine().Recover().Revision);
    }

    [Fact]
    public async Task V1OnlyPreviousCannotPretendItCanReadV07aRestoredV2()
    {
        using var f = new SelectionFixture(keys, legacy: true);
        f.Initialize();
        f.Select(f.Stage(maximumReader: 1));
        var second = f.Select(f.Stage("0.3.0.0"));
        var error = Assert.Throws<SelectionException>(() => f.Engine().PrepareRollback(second.Revision, f.Snapshot()));
        Assert.Equal(SelectionFailure.IncompatibleSettings, error.Failure);
        Assert.Equal(1, (await f.Settings.LoadAsync()).Settings!.SchemaVersion);
    }

    [Theory]
    [InlineData((int)SelectionIoPoint.BeforeCreate)]
    [InlineData((int)SelectionIoPoint.AfterCreate)]
    [InlineData((int)SelectionIoPoint.BeforeWrite)]
    [InlineData((int)SelectionIoPoint.AfterWrite)]
    [InlineData((int)SelectionIoPoint.BeforeFlush)]
    [InlineData((int)SelectionIoPoint.AfterFlush)]
    public void InterruptedInitializationNeverAdoptsAStageOrSilentlyOverwritesState(int boundary)
    {
        using var f = new SelectionFixture(keys);
        var engine = f.Engine((point, _, _) =>
        {
            if (point == (SelectionIoPoint)boundary) throw new IOException();
        });
        Assert.Throws<SelectionException>(() => engine.Initialize(f.Package.Current));
        Assert.False(File.Exists(f.Control));
        Assert.Equal(SelectionFailure.Uninitialized, Assert.Throws<SelectionException>(() => f.Engine().Inspect()).Failure);
        if ((SelectionIoPoint)boundary == SelectionIoPoint.BeforeCreate)
            Assert.Equal(SelectionStatus.BootstrapUnqualified, f.Initialize().Status);
        else
        {
            Assert.True(File.Exists(Path.Combine(f.Root, "initial.json")));
            Assert.Equal(SelectionFailure.RecoveryRequired, Assert.Throws<SelectionException>(() => f.Initialize()).Failure);
        }
        f.Package.AssertPrivateDataUnchanged();
    }

    private sealed class DiskFull : IOException
    {
        internal DiskFull() => HResult = unchecked((int)0x80070070);
    }
    private sealed class SimulatedTermination : Exception;
}
