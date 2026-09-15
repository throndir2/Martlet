using Martlet.Core.Settings;
using static Martlet.Updates.Tests.RollbackAcknowledgmentTests;
using static Martlet.Updates.Tests.RollbackRestoreTests;

namespace Martlet.Updates.Tests;

public sealed class RollbackAcknowledgmentOwnershipTests(SigningKeys keys) : IClassFixture<SigningKeys>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualAcknowledgmentKeepsEveryOwnerAndReadPinUntilPublication(bool afterIntent)
    {
        using var f = new SelectionFixture(keys);
        var old = await Interrupt(f);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var engine = f.Engine((point, _, _) =>
        {
            if (point == (afterIntent ? SelectionIoPoint.BeforePublicationRename : SelectionIoPoint.BeforeAcknowledgmentPublication))
            { entered.Set(); release.Wait(); }
        });
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationAcknowledgment(engine.Inspect().Revision, effects).Completion;
        var operation = engine.AcknowledgeRollbackConfiguration(plan, ApproveAcknowledgment(plan), effects);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            Assert.True(effects.IsRunning);
            Assert.False(operation.Completion.IsCompleted);
            Assert.Null(effects.TryStart(_ => Task.FromResult(new SetupWorkResult(SetupWorkOutcome.Completed))));
            Assert.Equal(SelectionFailure.Busy, Assert.Throws<SelectionException>(() => engine.Inspect()).Failure);
            Assert.Equal(SelectionFailure.Busy, Assert.Throws<SelectionException>(() => f.Engine().Recover()).Failure);
            Assert.Equal(SelectionFailure.Busy, Assert.Throws<SelectionException>(() =>
                engine.PreviewRollbackConfigurationAcknowledgment(plan.ExpectedSelectionRevision, new())).Failure);
            Assert.Equal(SelectionFailure.Busy, Assert.Throws<SelectionException>(() =>
                engine.PreviewRollbackConfigurationRestore(plan.ExpectedSelectionRevision, effects)).Failure);
            var loaded = await f.Settings.LoadAsync();
            Assert.False((await f.Settings.SaveAsync(loaded.Settings!, loaded.Revision)).Saved);
            Assert.Equal(RecoveryFailure.Unavailable, (await Assert.ThrowsAsync<RecoveryException>(() =>
                f.Settings.PreviewConfigurationRestoreAsync(Path.Combine(f.Root, plan.SnapshotRelativePath.Replace('/', '\\'))))).Failure);
            foreach (var entry in new[] { plan.Candidate, plan.Previous! })
            {
                Assert.Equal(StagingFailure.Busy, Assert.Throws<StagingException>(() => f.Staging.InspectStaged(
                    Path.Combine(f.Package.StagingRoot, entry.StageName))).Failure);
                foreach (var file in new[] { "candidate.json", "candidate.zip", "staged.json", "payload\\Doctor\\Martlet.Doctor.exe" })
                    Pinned(Path.Combine(f.Package.StagingRoot, entry.StageName, file));
                Pinned(Path.Combine(f.Root, entry.SnapshotRelativePath.Replace('/', '\\')));
            }
            Pinned(f.Settings.FilePath); Pinned(old.OriginalSnapshot!); Pinned(f.Control);
            foreach (var file in new[] { "before.json", "journal-v3.json", "pending-publication.json", "restore-committed.json" })
                Pinned(Path.Combine(f.Root, "transaction-" + old.OperationId.ToString("N"), file));
            await Task.WhenAny(operation.Completion, Task.Delay(30));
            Assert.False(operation.Completion.IsCompleted);
        }
        finally { release.Set(); }
        var result = await operation.Completion;
        Assert.Equal(RollbackAcknowledgment.Recorded, result.Acknowledgment);
        Assert.False(result.OwnershipFailed); Assert.False(effects.IsRunning);
        using var writer = new FileStream(f.Settings.FilePath + ".lock", FileMode.Open, FileAccess.Write, FileShare.None);
        f.Package.AssertPrivateDataUnchanged();
    }

    [Fact]
    public async Task LiveOriginalRestoreCannotBeAdoptedBeforeItsActualOwnerRetires()
    {
        using var f = new SelectionFixture(keys);
        var rollback = f.Rollback();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var engine = f.Engine((point, _, _) =>
        {
            if (point != SelectionIoPoint.BeforeRestoreAcknowledgment) return;
            entered.Set(); release.Wait();
            throw new IOException("Retain the genuine recorded commit without acknowledgment");
        });
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        var operation = engine.RestoreRollbackConfiguration(plan, Approve(plan), effects);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            Assert.Equal(SelectionFailure.Busy, Assert.Throws<SelectionException>(() =>
                engine.PreviewRollbackConfigurationAcknowledgment(rollback.Revision + 1, effects)).Failure);
            Assert.Equal(SelectionFailure.Busy, Assert.Throws<SelectionException>(() =>
                f.Engine().PreviewRollbackConfigurationAcknowledgment(rollback.Revision + 1, effects)).Failure);
            Assert.Equal(SelectionFailure.Busy, (await Assert.ThrowsAsync<SelectionException>(() =>
                f.Engine().PreviewRollbackConfigurationAcknowledgment(rollback.Revision + 1, new()).Completion)).Failure);
        }
        finally { release.Set(); }
        Assert.Equal(RollbackAcknowledgment.NotRecorded, (await operation.Completion).Acknowledgment);
        var reopened = f.Engine();
        var fresh = await reopened.PreviewRollbackConfigurationAcknowledgment(rollback.Revision + 1, effects).Completion;
        Assert.Equal(RollbackAcknowledgment.Recorded,
            (await reopened.AcknowledgeRollbackConfiguration(fresh, ApproveAcknowledgment(fresh), effects).Completion).Acknowledgment);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedPreStartCanceledPreviewSupersedesAcknowledgmentEvenWithoutWorkerAction(bool restorePreview)
    {
        using var f = new SelectionFixture(keys);
        await Interrupt(f);
        var engine = f.Engine(); var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationAcknowledgment(engine.Inspect().Revision, effects).Completion;
        var approval = ApproveAcknowledgment(plan);
        var history = History(f); var data = Data(f);
        using var gate = new RunnerStartGate();
        Task completion;
        Action cancel;
        if (restorePreview)
        {
            var operation = gate.Start(() => engine.PreviewRollbackConfigurationRestore(plan.ExpectedSelectionRevision, effects));
            completion = operation.Completion; cancel = operation.RequestCancellation;
        }
        else
        {
            var operation = gate.Start(() => engine.PreviewRollbackConfigurationAcknowledgment(plan.ExpectedSelectionRevision, effects));
            completion = operation.Completion; cancel = operation.RequestCancellation;
        }
        try
        {
            gate.WaitForEntry();
            cancel();
            Assert.True(effects.IsRunning);
            Assert.Equal(SelectionFailure.Busy, Assert.Throws<SelectionException>(() =>
                engine.PreviewRollbackConfigurationAcknowledgment(plan.ExpectedSelectionRevision, new())).Failure);
        }
        finally { cancel(); gate.Release(); }
        Assert.Equal(SelectionFailure.Cancelled, (await Assert.ThrowsAsync<SelectionException>(() => completion)).Failure);
        gate.AssertNoCallbackFailure();
        Assert.Equal(SelectionFailure.Conflict, (await Assert.ThrowsAsync<SelectionException>(() =>
            engine.AcknowledgeRollbackConfiguration(plan, approval, effects).Completion)).Failure);
        Unchanged(history); Unchanged(data);
        var fresh = await engine.PreviewRollbackConfigurationAcknowledgment(plan.ExpectedSelectionRevision, effects).Completion;
        Assert.Equal(RollbackAcknowledgment.Recorded,
            (await engine.AcknowledgeRollbackConfiguration(fresh, ApproveAcknowledgment(fresh), effects).Completion).Acknowledgment);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusyPreviewDoesNotRevokeApprovalButBusyUseConsumesIt(bool use)
    {
        using var f = new SelectionFixture(keys);
        await Interrupt(f);
        var engine = f.Engine(); var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationAcknowledgment(engine.Inspect().Revision, effects).Completion;
        var approval = ApproveAcknowledgment(plan);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = effects.TryStart(async _ => { await release.Task; return new(SetupWorkOutcome.Completed); })!;
        try
        {
            Assert.Equal(SelectionFailure.Busy, Assert.Throws<SelectionException>(() =>
            {
                if (use) engine.AcknowledgeRollbackConfiguration(plan, approval, effects);
                else engine.PreviewRollbackConfigurationAcknowledgment(plan.ExpectedSelectionRevision, effects);
            }).Failure);
        }
        finally { release.SetResult(); await held.Completion; }
        if (use)
            Assert.Equal(SelectionFailure.Conflict, Assert.Throws<SelectionException>(() =>
                engine.AcknowledgeRollbackConfiguration(plan, approval, effects)).Failure);
        else Assert.Equal(RollbackAcknowledgment.Recorded,
            (await engine.AcknowledgeRollbackConfiguration(plan, approval, effects).Completion).Acknowledgment);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task OriginalUtcAndMonotonicLifetimeNeverRenewsAfterIo(bool monotonic, bool utc, bool afterIntent)
    {
        using var f = new SelectionFixture(keys);
        await Interrupt(f);
        var clock = new RestoreTestClock();
        var engine = new LocalSelectionEngine(f.Root, f.Staging, f.Settings)
        {
            RestoreClock = clock,
            Io = (point, _, _) =>
            {
                if (point == (afterIntent ? SelectionIoPoint.BeforePublicationRename : SelectionIoPoint.BeforeAcknowledgmentPublication))
                    clock.Advance(monotonic ? TimeSpan.FromMinutes(5) : TimeSpan.Zero,
                        utc ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(-1));
            }
        };
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationAcknowledgment(engine.Inspect().Revision, effects).Completion;
        var data = Data(f);
        var result = await engine.AcknowledgeRollbackConfiguration(plan, ApproveAcknowledgment(plan), effects).Completion;
        Assert.Equal(SelectionFailure.ConsentExpired, result.Failure);
        Assert.Equal(afterIntent ? RollbackAcknowledgment.PublicationAmbiguous : RollbackAcknowledgment.NotRecorded, result.Acknowledgment);
        Unchanged(data);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OriginalCancellationAndCallbackRetirementCannotHideOrRevokeRealAcknowledgment(bool afterRename, bool failCallback)
    {
        using var f = new SelectionFixture(keys);
        await Interrupt(f);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var callbackEntered = new ManualResetEventSlim();
        using var callbackRelease = new ManualResetEventSlim();
        var registered = false;
        var engine = f.Engine((point, _, token) =>
        {
            if (!registered && point == SelectionIoPoint.BeforeCreate)
            {
                registered = true;
                token.Register(() =>
                {
                    callbackEntered.Set(); callbackRelease.Wait();
                    if (failCallback) throw new InvalidOperationException("Controlled callback retirement failure");
                });
            }
            if (point == (afterRename ? SelectionIoPoint.AfterAcknowledgmentPublication : SelectionIoPoint.BeforePublicationRename))
            { entered.Set(); release.Wait(); }
        });
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationAcknowledgment(engine.Inspect().Revision, effects).Completion;
        var operation = engine.AcknowledgeRollbackConfiguration(plan, ApproveAcknowledgment(plan), effects);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            operation.RequestCancellation();
            Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(15)));
            release.Set();
            Assert.False(operation.Completion.IsCompleted); Assert.True(effects.IsRunning);
            Assert.Equal(SelectionFailure.Busy, Assert.Throws<SelectionException>(() => engine.Inspect()).Failure);
        }
        finally { release.Set(); callbackRelease.Set(); }
        var result = await operation.Completion;
        Assert.Equal(afterRename ? RollbackAcknowledgment.Recorded : RollbackAcknowledgment.PublicationAmbiguous, result.Acknowledgment);
        Assert.Equal(failCallback, result.OwnershipFailed);
        if (afterRename)
        {
            Assert.Null(result.Failure);
            Assert.Equal(result.Selection!.Revision, f.Engine().Inspect().Revision);
        }
        Assert.False(effects.IsRunning);
    }

    private static void Pinned(string path)
    {
        Assert.Throws<IOException>(() => File.WriteAllText(path, "UNAUTHORIZED"));
        Assert.Throws<IOException>(() => File.Move(path, path + ".moved"));
    }
}
