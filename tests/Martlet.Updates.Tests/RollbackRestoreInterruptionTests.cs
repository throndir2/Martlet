using Martlet.Core.Settings;
using static Martlet.Updates.Tests.RollbackRestoreTests;

namespace Martlet.Updates.Tests;

public sealed class RollbackRestoreInterruptionTests(SigningKeys keys) : IClassFixture<SigningKeys>
{
    public static IEnumerable<object[]> ControlWrites()
    {
        foreach (var file in new[] { "before.json", "journal-v3.json", "pending.json", "pending-publication.json",
            "restore-committed.json", "selected.json", "selected-publication.json" })
            foreach (var point in new[] { SelectionIoPoint.BeforeCreate, SelectionIoPoint.AfterCreate,
                SelectionIoPoint.BeforeWrite, SelectionIoPoint.AfterWrite, SelectionIoPoint.BeforeFlush, SelectionIoPoint.AfterFlush })
                yield return [file, (int)point];
    }

    [Theory]
    [MemberData(nameof(ControlWrites))]
    public async Task EveryControlWriteFailurePreservesOriginalAndNeverGuessesCompletion(string file, int boundary)
    {
        using var f = new SelectionFixture(keys);
        var rollback = f.Rollback();
        var settings = File.ReadAllBytes(f.Settings.FilePath);
        var before = History(f);
        var hit = false;
        var engine = f.Engine((point, path, _) =>
        {
            if (point == (SelectionIoPoint)boundary && Path.GetFileName(path) == file)
            {
                hit = true;
                throw new IOException("PRIVATE CONTROL FAILURE");
            }
        });
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        var result = await engine.RestoreRollbackConfiguration(plan, Approve(plan), effects).Completion;
        Assert.True(hit);
        Assert.NotEqual(RollbackAcknowledgment.Recorded, result.Acknowledgment);
        Assert.Null(result.Selection);
        Assert.NotNull(result.Failure);
        Assert.DoesNotContain("PRIVATE CONTROL FAILURE", result.ToString());
        foreach (var pair in before.Where(pair => pair.Key != f.Control))
            Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
        var directory = Path.Combine(f.Root, "transaction-" + plan.OperationId.ToString("N"));
        var foreign = Path.Combine(directory, "unknown-owner");
        File.WriteAllText(foreign, "PRESERVE THIS");
        var after = History(f);
        for (var iteration = 0; iteration < 2; iteration++)
        {
            SelectionReceipt? observed = null;
            var inspectError = Record.Exception(() => observed = f.Engine().Inspect());
            if (inspectError is null)
            {
                Assert.NotNull(observed);
                Assert.True(observed.CurrentSelection!.ConfigurationRestoreRequired);
                if (observed.Status == SelectionStatus.RecoveryRequired)
                {
                    Assert.Equal(SelectionFailure.ConfigurationRestoreReconciliationRequired,
                        Assert.Throws<SelectionException>(() => f.Engine().Recover()).Failure);
                }
                else
                {
                    Assert.Equal(rollback.Revision, observed.Revision);
                    Assert.Equal(SelectionOutcome.Unchanged, f.Engine().Recover(plan.OperationId).Outcome);
                }
            }
            else
            {
                Assert.Equal(SelectionFailure.InvalidControl, Assert.IsType<SelectionException>(inspectError).Failure);
                Assert.Equal(SelectionFailure.InvalidControl, Assert.Throws<SelectionException>(() => f.Engine().Recover()).Failure);
            }
            foreach (var pair in after) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
        }
        if (result.SettingsProgress == RollbackSettingsProgress.VerifiedCommitted)
        {
            Assert.Equal(plan.CandidateJson, File.ReadAllText(f.Settings.FilePath));
            Assert.Equal(settings, File.ReadAllBytes(result.OriginalSnapshot!));
        }
        else Assert.Equal(settings, File.ReadAllBytes(f.Settings.FilePath));
        f.Package.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData((int)SelectionIoPoint.BeforePendingReplace)]
    [InlineData((int)SelectionIoPoint.AfterPendingReplace)]
    [InlineData((int)SelectionIoPoint.BeforeConfigurationRestore)]
    [InlineData((int)SelectionIoPoint.AfterConfigurationRestore)]
    [InlineData((int)SelectionIoPoint.BeforeRestoreAcknowledgment)]
    [InlineData((int)SelectionIoPoint.BeforeSelectionReplace)]
    [InlineData((int)SelectionIoPoint.AfterSelectionReplace)]
    public async Task CancellationChecksOriginalTokenAndHonorsActualCommitBoundaries(int boundary)
    {
        using var f = new SelectionFixture(keys);
        var rollback = f.Rollback();
        var effects = new SetupOperationRunner();
        using var callbackEntered = new ManualResetEventSlim();
        using var callbackReleased = new ManualResetEventSlim();
        var boundaryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueBoundary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The runner's original token is observed at a pre-effect point, including for the post-commit test.
        var registered = false;
        var coordinated = new LocalSelectionEngine(f.Root, f.Staging, f.Settings)
        {
            Io = (point, path, token) =>
            {
                if (!registered && token.CanBeCanceled)
                {
                    registered = true;
                    token.Register(() => { callbackEntered.Set(); callbackReleased.Wait(); });
                }
                if (point == (SelectionIoPoint)boundary)
                {
                    boundaryEntered.TrySetResult();
                    continueBoundary.Task.GetAwaiter().GetResult();
                }
            }
        };
        var plan = await coordinated.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        var operation = coordinated.RestoreRollbackConfiguration(plan, Approve(plan), effects);
        try
        {
            await boundaryEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            operation.RequestCancellation();
            Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(10)));
            continueBoundary.TrySetResult();
            await Task.Delay(30);
            Assert.False(operation.Completion.IsCompleted);
            Assert.True(effects.IsRunning);
            Assert.Null(effects.TryStart(_ => Task.FromResult(new SetupWorkResult(SetupWorkOutcome.Completed))));
        }
        finally
        {
            continueBoundary.TrySetResult();
            callbackReleased.Set();
        }
        var result = await operation.Completion;
        Assert.False(effects.IsRunning);
        if ((SelectionIoPoint)boundary == SelectionIoPoint.AfterSelectionReplace)
        {
            Assert.Equal(RollbackAcknowledgment.Recorded, result.Acknowledgment);
            Assert.Null(result.Failure);
            Assert.Equal(SelectionStatus.AwaitingReadiness, f.Engine().Inspect().Status);
        }
        else
        {
            Assert.NotEqual(RollbackAcknowledgment.Recorded, result.Acknowledgment);
            Assert.True(f.Engine().Inspect().CurrentSelection!.ConfigurationRestoreRequired);
            if ((SelectionIoPoint)boundary is SelectionIoPoint.AfterConfigurationRestore or
                SelectionIoPoint.BeforeRestoreAcknowledgment or SelectionIoPoint.BeforeSelectionReplace)
            {
                Assert.Equal(RollbackSettingsProgress.VerifiedCommitted, result.SettingsProgress);
                Assert.Equal(SelectionFailure.ConfigurationRestoreReconciliationRequired, result.Failure);
            }
        }
    }

    [Theory]
    [InlineData("BeforeStage")]
    [InlineData("AfterWrite")]
    [InlineData("BeforeFlush")]
    [InlineData("AfterFlush")]
    [InlineData("BeforeCommit")]
    [InlineData("AfterCommit")]
    public async Task RealCoreFailureCannotClearRestoreAndPostCommitFailureIsDistinguished(string boundary)
    {
        var enabled = false;
        var stages = 0;
        var hit = false;
        using var f = new SelectionFixture(keys, settingsIo: (point, _) =>
        {
            if (!enabled) return;
            if (point == SettingsIoPoint.BeforeStage) stages++;
            if (stages == 2 && point.ToString() == boundary)
            {
                hit = true;
                throw new IOException("PRIVATE CORE IO FAILURE");
            }
        });
        var rollback = f.Rollback();
        var effects = new SetupOperationRunner();
        var engine = f.Engine();
        var plan = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        var original = File.ReadAllBytes(f.Settings.FilePath);
        enabled = true;
        var result = await engine.RestoreRollbackConfiguration(plan, Approve(plan), effects).Completion;
        Assert.True(hit);
        Assert.Equal(RecoveryFailure.Unavailable, result.ConfigurationFailure);
        Assert.NotEqual(RollbackAcknowledgment.Recorded, result.Acknowledgment);
        var pending = f.Engine().Inspect();
        Assert.Equal(SelectionStatus.RecoveryRequired, pending.Status);
        Assert.Equal(RollbackRestoreProgress.OutcomeUnproven, pending.ConfigurationRestoreProgress);
        Assert.Equal(3, Wire.Read<ControlDocument>(File.ReadAllBytes(f.Control), 32768).FormatVersion);
        Assert.Equal(SelectionFailure.ConfigurationRestoreReconciliationRequired,
            Assert.Throws<SelectionException>(() => f.Engine().Recover()).Failure);
        if (boundary == "AfterCommit")
        {
            Assert.Equal(RollbackSettingsProgress.VerifiedCommitted, result.SettingsProgress);
            Assert.Equal(plan.CandidateJson, File.ReadAllText(f.Settings.FilePath));
        }
        else Assert.Equal(original, File.ReadAllBytes(f.Settings.FilePath));
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Directory.GetFiles(f.Settings.DataDirectory, "settings.recovery.*.bak"))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostAcknowledgmentNeverPromotesFromMatchingBytesOrARecordedSettingsCommit(bool marker)
    {
        using var f = new SelectionFixture(keys);
        var rollback = f.Rollback();
        var effects = new SetupOperationRunner();
        var engine = f.Engine((point, _, _) =>
        {
            if (point == (marker ? SelectionIoPoint.BeforeRestoreAcknowledgment : SelectionIoPoint.AfterConfigurationRestore))
                throw new IOException();
        });
        var plan = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        var result = await engine.RestoreRollbackConfiguration(plan, Approve(plan), effects).Completion;
        Assert.Equal(RollbackSettingsProgress.VerifiedCommitted, result.SettingsProgress);
        Assert.Equal(plan.CandidateJson, File.ReadAllText(f.Settings.FilePath));
        var before = History(f);
        for (var index = 0; index < 2; index++)
        {
            var reopened = f.Engine();
            Assert.Equal(marker ? RollbackRestoreProgress.SettingsCommittedSelectionPending : RollbackRestoreProgress.OutcomeUnproven,
                reopened.Inspect().ConfigurationRestoreProgress);
            Assert.Equal(SelectionFailure.ConfigurationRestoreReconciliationRequired,
                Assert.Throws<SelectionException>(() => reopened.Recover()).Failure);
            await Assert.ThrowsAsync<SelectionException>(() =>
                reopened.PreviewRollbackConfigurationRestore(rollback.Revision + 1, effects).Completion);
            foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
        }
    }

    [Theory]
    [InlineData((int)SelectionIoPoint.AfterConfigurationRestore)]
    [InlineData((int)SelectionIoPoint.BeforeRestoreAcknowledgment)]
    public async Task LaterReadFailureDoesNotEraseAnAlreadyVerifiedCommitOrAuthorizeAcknowledgment(int boundary)
    {
        var failReads = false;
        using var f = new SelectionFixture(keys, settingsIo: (point, _) =>
        {
            if (failReads && point == SettingsIoPoint.BeforeRead)
                throw new IOException("Persistent read failure after verified commit");
        });
        var rollback = f.Rollback();
        var engine = f.Engine((point, _, _) =>
        {
            if (point == (SelectionIoPoint)boundary) failReads = true;
        });
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        var original = File.ReadAllBytes(f.Settings.FilePath);
        var result = await engine.RestoreRollbackConfiguration(plan, Approve(plan), effects).Completion;
        Assert.True(failReads);
        Assert.Equal(RecoveryFailure.Unavailable, result.ConfigurationFailure);
        Assert.Equal(RollbackSettingsProgress.VerifiedCommitted, result.SettingsProgress);
        Assert.Equal(RollbackAcknowledgment.NotRecorded, result.Acknowledgment);
        Assert.Null(result.Selection);
        Assert.Equal(plan.CandidateDigest, result.ResultingSettingsRevision);
        Assert.Equal(plan.ProfileId, result.ProfileId);
        Assert.Equal(2, result.SettingsSchemaVersion);
        Assert.Equal(original, File.ReadAllBytes(result.OriginalSnapshot!));
        Assert.Equal(plan.CandidateJson, File.ReadAllText(f.Settings.FilePath));
        var retained = History(f);
        Assert.Equal(RollbackRestoreProgress.SettingsCommittedSelectionPending,
            f.Engine().Inspect().ConfigurationRestoreProgress);
        Assert.Equal(SelectionFailure.ConfigurationRestoreReconciliationRequired,
            Assert.Throws<SelectionException>(() => f.Engine().Recover()).Failure);
        foreach (var pair in retained) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
    }
}
