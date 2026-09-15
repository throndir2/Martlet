using Martlet.Core.Settings;
using static Martlet.Updates.Tests.RollbackRestoreTests;

namespace Martlet.Updates.Tests;

public sealed class RollbackRestoreAdmissionTests(SigningKeys keys) : IClassFixture<SigningKeys>
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task AcceptedPreviewCanceledBeforeRunnerActionStillSupersedesApprovedPlan(int cancellations)
    {
        using var f = new SelectionFixture(keys);
        var rollback = f.Rollback();
        var engine = f.Engine();
        var effects = new SetupOperationRunner();
        var first = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        var approval = Approve(first);
        var original = File.ReadAllBytes(f.Settings.FilePath);
        var control = File.ReadAllBytes(f.Control);
        for (var index = 0; index < cancellations; index++)
        {
            using var gate = new RunnerStartGate();
            var canceled = gate.Start(() => engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects));
            SelectionException? cancellation = null;
            try
            {
                gate.WaitForEntry();
                await Task.Run(canceled.RequestCancellation);
                Assert.True(effects.IsRunning);
                Assert.False(canceled.Completion.IsCompleted);
                Assert.Null(effects.TryStart(_ => Task.FromResult(new SetupWorkResult(SetupWorkOutcome.Completed))));
            }
            finally
            {
                canceled.RequestCancellation();
                gate.Release();
                cancellation = await Assert.ThrowsAsync<SelectionException>(() => canceled.Completion);
            }
            Assert.Equal(SelectionFailure.Cancelled, cancellation.Failure);
            gate.AssertNoCallbackFailure();
            Assert.False(effects.IsRunning);
            Assert.Equal(control, File.ReadAllBytes(f.Control));
            Assert.Equal(original, File.ReadAllBytes(f.Settings.FilePath));
        }
        var error = await Assert.ThrowsAsync<SelectionException>(() =>
            engine.RestoreRollbackConfiguration(first, approval, effects).Completion);
        Assert.Equal(SelectionFailure.Conflict, error.Failure);
        Assert.Empty(Directory.GetFiles(f.Settings.DataDirectory, "settings.recovery.*.bak"));
        Assert.Equal(original, File.ReadAllBytes(f.Settings.FilePath));
        Assert.Equal(control, File.ReadAllBytes(f.Control));
        var fresh = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        var result = await engine.RestoreRollbackConfiguration(fresh, Approve(fresh), effects).Completion;
        Assert.Equal(RollbackAcknowledgment.Recorded, result.Acknowledgment);
        Assert.False(result.IsRunnable);
        f.Package.AssertPrivateDataUnchanged();
    }

    [Fact]
    public async Task ConcurrentCallersCannotAdmitThroughAnotherRunnersPreStartOwnership()
    {
        using var f = new SelectionFixture(keys);
        var rollback = f.Rollback();
        var engine = f.Engine();
        var effects = new[] { new SetupOperationRunner(), new SetupOperationRunner() };
        var first = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects[0]).Completion;
        var approval = Approve(first);
        using var start = new ManualResetEventSlim();
        using var gate = new RunnerStartGate();
        var submissions = effects.Select(runner => Task.Run(() =>
        {
            start.Wait();
            try { return gate.Start(() => engine.PreviewRollbackConfigurationRestore(rollback.Revision, runner)); }
            catch (SelectionException error) when (error.Failure == SelectionFailure.Busy) { return null; }
        })).ToArray();
        start.Set();
        var submitted = await Task.WhenAll(submissions);
        try
        {
            Assert.Single(submitted.OfType<RollbackRestoreOperation<RollbackRestorePlan>>());
            gate.WaitForEntry();
            Assert.Equal(SelectionFailure.Busy, Assert.Throws<SelectionException>(() => engine.Inspect()).Failure);
            Assert.Equal(SelectionFailure.Busy, Assert.Throws<SelectionException>(() =>
                engine.PreviewRollbackConfigurationRestore(rollback.Revision, new SetupOperationRunner())).Failure);
        }
        finally
        {
            foreach (var operation in submitted.OfType<RollbackRestoreOperation<RollbackRestorePlan>>())
                operation.RequestCancellation();
            gate.Release();
            foreach (var operation in submitted.OfType<RollbackRestoreOperation<RollbackRestorePlan>>())
                Assert.Equal(SelectionFailure.Cancelled,
                    (await Assert.ThrowsAsync<SelectionException>(() => operation.Completion)).Failure);
        }
        gate.AssertNoCallbackFailure();
        Assert.All(effects, runner => Assert.False(runner.IsRunning));
        Assert.Equal(SelectionFailure.Conflict, (await Assert.ThrowsAsync<SelectionException>(() =>
            engine.RestoreRollbackConfiguration(first, approval, effects[0]).Completion)).Failure);
        Assert.Empty(Directory.GetFiles(f.Settings.DataDirectory, "settings.recovery.*.bak"));
        var fresh = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects[1]).Completion;
        Assert.Equal(RollbackAcknowledgment.Recorded,
            (await engine.RestoreRollbackConfiguration(fresh, Approve(fresh), effects[1]).Completion).Acknowledgment);
    }

    [Fact]
    public async Task RefusedBusyPreviewLeavesEarlierApprovalUsable()
    {
        using var f = new SelectionFixture(keys);
        var rollback = f.Rollback();
        var engine = f.Engine();
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        var approval = Approve(plan);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var other = effects.TryStart(async _ =>
        {
            await release.Task;
            return new SetupWorkResult(SetupWorkOutcome.Completed);
        })!;
        try
        {
            Assert.Equal(SelectionFailure.Busy, Assert.Throws<SelectionException>(() =>
                engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects)).Failure);
            Assert.Equal(rollback.Revision, engine.Inspect().Revision);
        }
        finally { release.TrySetResult(); await other.Completion; }
        Assert.Equal(RollbackAcknowledgment.Recorded,
            (await engine.RestoreRollbackConfiguration(plan, approval, effects).Completion).Acknowledgment);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusedPreviewCannotRevokeOrReleaseAnAlreadyRunningRestore(bool otherRunner)
    {
        using var f = new SelectionFixture(keys);
        var rollback = f.Rollback();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var engine = f.Engine((point, _, _) =>
        {
            if (point != SelectionIoPoint.BeforeConfigurationRestore) return;
            entered.Set();
            release.Wait();
        });
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        var operation = engine.RestoreRollbackConfiguration(plan, Approve(plan), effects);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(SelectionFailure.Busy, Assert.Throws<SelectionException>(() =>
                engine.PreviewRollbackConfigurationRestore(rollback.Revision,
                    otherRunner ? new SetupOperationRunner() : effects)).Failure);
            Assert.Equal(SelectionFailure.Busy, Assert.Throws<SelectionException>(() => engine.Inspect()).Failure);
            Assert.True(effects.IsRunning);
            Assert.False(operation.Completion.IsCompleted);
        }
        finally { release.Set(); }
        var result = await operation.Completion;
        Assert.Equal(RollbackAcknowledgment.Recorded, result.Acknowledgment);
        Assert.Null(result.Failure);
        Assert.Equal(result.Selection!.Revision, engine.Inspect().Revision);
    }

    [Fact]
    public async Task UnexpectedWorkerFailureReleasesOnlyItsOwnedAdmission()
    {
        using var f = new SelectionFixture(keys);
        var rollback = f.Rollback();
        var fail = true;
        var engine = f.Engine((point, _, _) =>
        {
            if (fail && point == SelectionIoPoint.BeforeCreate) throw new InvalidOperationException("PRIVATE WORKER FAILURE");
        });
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        var error = await Assert.ThrowsAsync<SelectionException>(() =>
            engine.RestoreRollbackConfiguration(plan, Approve(plan), effects).Completion);
        Assert.Equal(SelectionFailure.Unavailable, error.Failure);
        Assert.DoesNotContain("PRIVATE WORKER FAILURE", error.ToString());
        Assert.False(effects.IsRunning);
        Assert.Equal(rollback.Revision, engine.Inspect().Revision);
        fail = false;
        var fresh = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        Assert.Equal(RollbackAcknowledgment.Recorded,
            (await engine.RestoreRollbackConfiguration(fresh, Approve(fresh), effects).Completion).Acknowledgment);
    }

    [Fact]
    public async Task EngineAdmissionWaitsForActualCancellationCallbackRetirement()
    {
        using var f = new SelectionFixture(keys);
        var rollback = f.Rollback();
        using var atCommit = new ManualResetEventSlim();
        using var leaveCommit = new ManualResetEventSlim();
        using var callbackEntered = new ManualResetEventSlim();
        using var leaveCallback = new ManualResetEventSlim();
        var registered = false;
        var engine = f.Engine((point, _, token) =>
        {
            if (!registered && token.CanBeCanceled)
            {
                registered = true;
                token.Register(() =>
                {
                    callbackEntered.Set();
                    leaveCallback.Wait();
                    throw new InvalidOperationException("Controlled cancellation callback failure");
                });
            }
            if (point == SelectionIoPoint.AfterSelectionReplace) { atCommit.Set(); leaveCommit.Wait(); }
        });
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        var operation = engine.RestoreRollbackConfiguration(plan, Approve(plan), effects);
        try
        {
            Assert.True(atCommit.Wait(TimeSpan.FromSeconds(10)));
            operation.RequestCancellation();
            Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(10)));
            leaveCommit.Set();
            Assert.False(operation.Completion.IsCompleted);
            Assert.True(effects.IsRunning);
            Assert.Equal(SelectionFailure.Busy, Assert.Throws<SelectionException>(() => engine.Inspect()).Failure);
            Assert.Equal(SelectionFailure.Busy, Assert.Throws<SelectionException>(() =>
                engine.PreviewRollbackConfigurationRestore(rollback.Revision, new SetupOperationRunner())).Failure);
        }
        finally { leaveCommit.Set(); leaveCallback.Set(); }
        var result = await operation.Completion;
        Assert.True(result.OwnershipFailed);
        Assert.Equal(RollbackAcknowledgment.Recorded, result.Acknowledgment);
        Assert.Equal(result.Selection!.Revision, engine.Inspect().Revision);
        Assert.False(effects.IsRunning);
    }
}

// Task.Run enters its captured ExecutionContext before invoking the original runner token check.
// Only the synchronous Start call carries this marker; no scheduler/global thread-pool changes.
internal sealed class RunnerStartGate : IDisposable
{
    private readonly ManualResetEventSlim entered = new();
    private readonly ManualResetEventSlim release = new();
    private readonly object lifetime = new(), signal = new();
    private readonly AsyncLocal<bool> context;
    private bool disposed;
    private int timedOut, callbackFailure;

    internal RunnerStartGate()
    {
        context = new(change =>
        {
            if (!change.ThreadContextChanged || !change.CurrentValue) return;
            try
            {
                lock (lifetime)
                {
                    if (Volatile.Read(ref disposed)) return;
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(30))) Volatile.Write(ref timedOut, 1);
                }
            }
            catch (ObjectDisposedException) { Volatile.Write(ref callbackFailure, 1); }
            catch (ThreadInterruptedException) { Volatile.Write(ref callbackFailure, 1); }
        });
    }

    internal T Start<T>(Func<T> start)
    {
        var previous = context.Value;
        context.Value = true;
        try { return start(); }
        finally { context.Value = previous; }
    }
    internal void WaitForEntry() => Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
    internal void Release()
    {
        lock (signal) { if (!disposed) release.Set(); }
    }
    internal void AssertNoCallbackFailure()
    {
        Assert.Equal(0, Volatile.Read(ref timedOut));
        Assert.Equal(0, Volatile.Read(ref callbackFailure));
    }
    public void Dispose()
    {
        Release();
        lock (lifetime)
        {
            lock (signal)
            {
                if (disposed) return;
                Volatile.Write(ref disposed, true);
                entered.Dispose();
                release.Dispose();
            }
        }
    }
}
