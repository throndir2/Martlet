using Martlet.Core.Settings;
using static Martlet.Updates.Tests.RollbackRestoreTests;

namespace Martlet.Updates.Tests;

public sealed class RollbackRestoreOwnershipTests(SigningKeys keys) : IClassFixture<SigningKeys>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualCoreReplacementAndAcknowledgmentKeepAllOwnersAndSources(bool acknowledgment)
    {
        var enabled = false;
        var stages = 0;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var f = new SelectionFixture(keys, settingsIo: (point, _) =>
        {
            if (!enabled) return;
            if (point == SettingsIoPoint.BeforeStage) stages++;
            if (!acknowledgment && stages == 2 && point == SettingsIoPoint.BeforeCommit)
            { entered.Set(); release.Wait(); }
        });
        var rollback = f.Rollback();
        var effects = new SetupOperationRunner();
        var engine = f.Engine((point, _, _) =>
        {
            if (acknowledgment && point == SelectionIoPoint.BeforeRestoreAcknowledgment)
            { entered.Set(); release.Wait(); }
        });
        var plan = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        enabled = true;
        var operation = engine.RestoreRollbackConfiguration(plan, Approve(plan), effects);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(effects.IsRunning);
            Assert.False(operation.Completion.IsCompleted);
            Assert.Null(effects.TryStart(_ => Task.FromResult(new SetupWorkResult(SetupWorkOutcome.Completed))));
            Assert.Equal(SelectionFailure.Busy, Assert.Throws<SelectionException>(() => f.Engine().Inspect()).Failure);
            Assert.Equal(SelectionFailure.Busy, Assert.Throws<SelectionException>(() => engine.Recover()).Failure);
            var currentStage = Path.Combine(f.Package.StagingRoot, plan.Candidate.StageName);
            Assert.Equal(StagingFailure.Busy, Assert.Throws<StagingException>(() => f.Staging.InspectStaged(currentStage)).Failure);
            var current = await f.Settings.LoadAsync();
            Assert.False((await f.Settings.SaveAsync(current.Settings!, current.Revision)).Saved);
            Assert.Equal(RecoveryFailure.Unavailable, (await Assert.ThrowsAsync<RecoveryException>(() =>
                f.Settings.PreviewConfigurationRestoreAsync(Path.Combine(f.Root, plan.SnapshotRelativePath.Replace('/', '\\'))))).Failure);
            var pins = new List<string>
            {
                f.Settings.FilePath, f.Control,
                Path.Combine(f.Root, plan.SnapshotRelativePath.Replace('/', '\\')),
                Assert.Single(Directory.GetFiles(f.Settings.DataDirectory, "settings.recovery.*.bak"))
            };
            foreach (var entry in new[] { plan.Candidate, plan.Previous! })
            {
                pins.Add(Path.Combine(f.Package.StagingRoot, entry.StageName, "candidate.json"));
                pins.Add(Path.Combine(f.Package.StagingRoot, entry.StageName, "candidate.zip"));
                pins.Add(Path.Combine(f.Package.StagingRoot, entry.StageName, "staged.json"));
                pins.Add(Path.Combine(f.Package.StagingRoot, entry.StageName, "payload", "Doctor", "Martlet.Doctor.exe"));
                pins.Add(Path.Combine(f.Root, entry.SnapshotRelativePath.Replace('/', '\\')));
            }
            foreach (var path in pins)
            {
                Assert.Throws<IOException>(() => File.WriteAllText(path, "NOT AUTHORIZED"));
                Assert.Throws<IOException>(() => File.Move(path, path + ".moved"));
            }
            var observed = await Task.WhenAny(operation.Completion, Task.Delay(30));
            Assert.NotSame(operation.Completion, observed);
            Assert.True(effects.IsRunning);
        }
        finally { release.Set(); }
        var result = await operation.Completion;
        Assert.Equal(RollbackAcknowledgment.Recorded, result.Acknowledgment);
        Assert.False(effects.IsRunning);
        Assert.False(result.IsRunnable);
        f.Package.AssertPrivateDataUnchanged();
    }

    [Fact]
    public async Task BusyEffectOwnerConsumesApprovalWithoutStartingRestore()
    {
        using var f = new SelectionFixture(keys);
        var rollback = f.Rollback();
        var effects = new SetupOperationRunner();
        var engine = f.Engine();
        var plan = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        var approval = Approve(plan);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var occupied = effects.TryStart(async _ =>
        {
            await release.Task;
            return new SetupWorkResult(SetupWorkOutcome.Completed);
        })!;
        try
        {
            Assert.Equal(SelectionFailure.Busy, Assert.Throws<SelectionException>(() =>
                engine.RestoreRollbackConfiguration(plan, approval, effects)).Failure);
            Assert.Equal(rollback.Revision, engine.Inspect().Revision);
        }
        finally { release.SetResult(); await occupied.Completion; }
        Assert.Equal(SelectionFailure.Conflict, Assert.Throws<SelectionException>(() =>
            engine.RestoreRollbackConfiguration(plan, approval, effects)).Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiryAfterActualBlockedIoDoesNotAcquireAFreshBudget(bool afterSettingsCommit)
    {
        using var f = new SelectionFixture(keys);
        var rollback = f.Rollback();
        var clock = new RestoreTestClock();
        var engine = new LocalSelectionEngine(f.Root, f.Staging, f.Settings)
        {
            RestoreClock = clock,
            Io = (point, _, _) =>
            {
                if (point == (afterSettingsCommit ? SelectionIoPoint.BeforeRestoreAcknowledgment : SelectionIoPoint.BeforeConfigurationRestore))
                    clock.Advance(TimeSpan.FromMinutes(5), TimeSpan.Zero);
            }
        };
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        var result = await engine.RestoreRollbackConfiguration(plan, Approve(plan), effects).Completion;
        Assert.NotEqual(RollbackAcknowledgment.Recorded, result.Acknowledgment);
        Assert.Equal(afterSettingsCommit ? RollbackSettingsProgress.VerifiedCommitted : RollbackSettingsProgress.NotAttempted,
            result.SettingsProgress);
        Assert.True(f.Engine().Inspect().CurrentSelection!.ConfigurationRestoreRequired);
        Assert.Equal(SelectionFailure.ConfigurationRestoreReconciliationRequired,
            Assert.Throws<SelectionException>(() => f.Engine().Recover()).Failure);
    }

    [Theory]
    [InlineData("control-before")]
    [InlineData("control-pending")]
    [InlineData("missing-commit")]
    [InlineData("corrupt-commit")]
    [InlineData("wrong-kind")]
    [InlineData("wrong-original")]
    [InlineData("recovered-substitution")]
    public async Task V3RestoreHistoryRejectsRewindAndUnprovenTerminalEvidence(string tamper)
    {
        using var f = new SelectionFixture(keys);
        var rollback = f.Rollback();
        var before = File.ReadAllBytes(f.Control);
        byte[]? pending = null;
        var engine = f.Engine((point, _, _) =>
        {
            if (point == SelectionIoPoint.AfterPendingReplace) pending = File.ReadAllBytes(f.Control);
        });
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        var result = await engine.RestoreRollbackConfiguration(plan, Approve(plan), effects).Completion;
        Assert.Equal(RollbackAcknowledgment.Recorded, result.Acknowledgment);
        var directory = Path.Combine(f.Root, "transaction-" + plan.OperationId.ToString("N"));
        var journalPath = Path.Combine(directory, "journal-v3.json");
        var journal = Wire.Read<SelectionJournalV3>(File.ReadAllBytes(journalPath), 131072);
        switch (tamper)
        {
            case "control-before": File.WriteAllBytes(f.Control, before); break;
            case "control-pending": File.WriteAllBytes(f.Control, pending!); break;
            case "missing-commit": File.Delete(Path.Combine(directory, "restore-committed.json")); break;
            case "corrupt-commit": File.WriteAllText(Path.Combine(directory, "restore-committed.json"), "{}"); break;
            case "wrong-kind":
                File.WriteAllBytes(journalPath, Wire.Write(journal with { Kind = SelectionTransactionKind.Selection })); break;
            case "wrong-original": File.AppendAllText(result.OriginalSnapshot!, " "); break;
            case "recovered-substitution":
                File.WriteAllBytes(f.Control, Wire.Write(journal.Before with
                { Revision = journal.After.Revision, LastTransaction = plan.OperationId, Pending = null })); break;
        }
        var retained = History(f);
        for (var index = 0; index < 2; index++)
        {
            Assert.Equal(SelectionFailure.InvalidControl, Assert.Throws<SelectionException>(() => f.Engine().Inspect()).Failure);
            Assert.Equal(SelectionFailure.InvalidControl, Assert.Throws<SelectionException>(() => f.Engine().Recover()).Failure);
            foreach (var pair in retained) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
        }
        f.Package.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactRestoreScratchCannotBeSubstitutedBeforeAcknowledgment(bool afterIntent)
    {
        using var f = new SelectionFixture(keys);
        var rollback = f.Rollback();
        var changed = false;
        var attempted = false;
        var engine = f.Engine((point, path, _) =>
        {
            if (point != (afterIntent ? SelectionIoPoint.BeforePublicationRename : SelectionIoPoint.BeforeSelectionReplace) ||
                afterIntent && Path.GetFileName(path) != "selected.json") return;
            attempted = true;
            var scratch = afterIntent ? path : Path.Combine(path, "selected.json");
            try
            {
                File.Move(scratch, scratch + ".retained");
                File.WriteAllText(scratch, "{}");
                changed = true;
            }
            catch (IOException) { }
        });
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        var result = await engine.RestoreRollbackConfiguration(plan, Approve(plan), effects).Completion;
        Assert.True(attempted);
        if (changed)
        {
            Assert.NotEqual(RollbackAcknowledgment.Recorded, result.Acknowledgment);
            if (afterIntent)
                Assert.Equal(SelectionFailure.InvalidControl, Assert.Throws<SelectionException>(() => f.Engine().Inspect()).Failure);
            else Assert.Equal(SelectionStatus.RecoveryRequired, f.Engine().Inspect().Status);
        }
        else Assert.Equal(RollbackAcknowledgment.Recorded, result.Acknowledgment);
        Assert.Equal(RollbackSettingsProgress.VerifiedCommitted, result.SettingsProgress);
    }

    [Fact]
    public async Task FailedCoreTemporaryCleanupRemainsOwnedAndCannotRestartTheRestore()
    {
        var enabled = false;
        var stages = 0;
        var failCleanup = true;
        using var f = new SelectionFixture(keys, settingsIo: (point, _) =>
        {
            if (!enabled) return;
            if (point == SettingsIoPoint.BeforeStage) stages++;
            if (stages == 2 && point == SettingsIoPoint.AfterWrite) throw new IOException();
            if (point == SettingsIoPoint.BeforeCleanup && failCleanup) throw new UnauthorizedAccessException();
        });
        var rollback = f.Rollback();
        var engine = f.Engine();
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        var original = File.ReadAllBytes(f.Settings.FilePath);
        enabled = true;
        var result = await engine.RestoreRollbackConfiguration(plan, Approve(plan), effects).Completion;
        Assert.Equal(RecoveryFailure.CleanupPending, result.ConfigurationFailure);
        Assert.NotNull(result.RetainedCleanupFile);
        Assert.True(File.Exists(result.RetainedCleanupFile));
        Assert.Equal(original, File.ReadAllBytes(f.Settings.FilePath));
        Assert.Equal(SelectionFailure.CleanupPending, Assert.Throws<SelectionException>(() => engine.Inspect()).Failure);
        var wrong = engine.RetryRollbackRestoreCleanup(new SetupOperationRunner());
        Assert.Equal(SelectionFailure.Conflict, (await Assert.ThrowsAsync<SelectionException>(() => wrong.Completion)).Failure);
        failCleanup = false;
        await engine.RetryRollbackRestoreCleanup(effects).Completion;
        Assert.False(File.Exists(result.RetainedCleanupFile));
        Assert.Equal(original, File.ReadAllBytes(f.Settings.FilePath));
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Directory.GetFiles(f.Settings.DataDirectory, "settings.recovery.*.bak"))));
        Assert.Equal(RollbackRestoreProgress.OutcomeUnproven, engine.Inspect().ConfigurationRestoreProgress);
        await Assert.ThrowsAsync<SelectionException>(() =>
            engine.PreviewRollbackConfigurationRestore(rollback.Revision + 1, effects).Completion);
        f.Package.AssertPrivateDataUnchanged();
    }
}
