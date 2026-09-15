using System.Text.Json.Nodes;
using Martlet.Core.Settings;
using static Martlet.Updates.Tests.RollbackAcknowledgmentTests;
using static Martlet.Updates.Tests.RollbackRestoreTests;

namespace Martlet.Updates.Tests;

public sealed class RollbackAcknowledgmentInterruptionTests(SigningKeys keys) : IClassFixture<SigningKeys>
{
    public static IEnumerable<object[]> Writes()
    {
        foreach (var name in new[] { "before.json", "journal-v4.json", "selected.json", "selected-publication.json" })
            foreach (var point in new[] { SelectionIoPoint.BeforeCreate, SelectionIoPoint.AfterCreate,
                SelectionIoPoint.BeforeWrite, SelectionIoPoint.AfterWrite, SelectionIoPoint.BeforeFlush, SelectionIoPoint.AfterFlush })
                yield return [name, (int)point];
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task EveryNewWriteFailurePreservesOldCommitWithoutInventingAnAcknowledgment(string name, int boundary)
    {
        using var f = new SelectionFixture(keys);
        var old = await Interrupt(f);
        var history = History(f); var data = Data(f);
        var hit = false;
        var engine = f.Engine((point, path, _) =>
        {
            if (point == (SelectionIoPoint)boundary && Path.GetFileName(path) == name)
            {
                hit = true;
                throw new IOException("PRIVATE ACKNOWLEDGMENT IO");
            }
        });
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationAcknowledgment(engine.Inspect().Revision, effects).Completion;
        var result = await engine.AcknowledgeRollbackConfiguration(plan, ApproveAcknowledgment(plan), effects).Completion;
        Assert.True(hit);
        Assert.Equal(RollbackHistoricalCommitVerification.VerifiedRecordedCommit, result.HistoricalCommitVerification);
        Assert.Equal(RollbackCurrentSettingsVerification.VerifiedPresentState, result.CurrentSettingsVerification);
        Assert.NotEqual(RollbackAcknowledgment.Recorded, result.Acknowledgment);
        Assert.Null(result.Selection);
        Assert.NotNull(result.Failure);
        Assert.DoesNotContain("PRIVATE ACKNOWLEDGMENT IO", result.ToString());
        Unchanged(history); Unchanged(data);
        var directory = Path.Combine(f.Root, "transaction-" + plan.OperationId.ToString("N"));
        var intent = File.Exists(Path.Combine(directory, "selected-publication.json"));
        Assert.Equal(intent ? RollbackAcknowledgment.PublicationAmbiguous : RollbackAcknowledgment.NotRecorded, result.Acknowledgment);
        var retained = History(f);
        for (var index = 0; index < 2; index++)
        {
            if (intent)
            {
                Assert.Equal(SelectionFailure.InvalidControl, Assert.Throws<SelectionException>(() => f.Engine().Inspect()).Failure);
                Assert.Equal(SelectionFailure.InvalidControl, Assert.Throws<SelectionException>(() => f.Engine().Recover()).Failure);
                Assert.Equal(SelectionFailure.InvalidControl, (await Assert.ThrowsAsync<SelectionException>(() =>
                    f.Engine().PreviewRollbackConfigurationAcknowledgment(plan.ExpectedSelectionRevision, new()).Completion)).Failure);
            }
            else
            {
                Assert.Equal(RollbackRestoreProgress.SettingsCommittedSelectionPending, f.Engine().Inspect().ConfigurationRestoreProgress);
                Assert.Equal(SelectionFailure.ConfigurationRestoreReconciliationRequired,
                    Assert.Throws<SelectionException>(() => f.Engine().Recover(old.OperationId)).Failure);
            }
            Unchanged(retained); Unchanged(data);
        }
        Assert.False(effects.IsRunning);
        f.Package.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshAttemptSkipsIntactDraftReservationButRefusesAnUnreadableReservation(bool partial)
    {
        using var f = new SelectionFixture(keys);
        await Interrupt(f);
        var engine = f.Engine((point, path, _) =>
        {
            if (point == (partial ? SelectionIoPoint.AfterCreate : SelectionIoPoint.AfterWrite) &&
                Path.GetFileName(path) == "journal-v4.json") throw new IOException();
        });
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationAcknowledgment(engine.Inspect().Revision, effects).Completion;
        await engine.AcknowledgeRollbackConfiguration(plan, ApproveAcknowledgment(plan), effects).Completion;
        var retained = History(f); var data = Data(f);
        var freshEngine = f.Engine();
        if (partial)
            Assert.Equal(SelectionFailure.InvalidControl, (await Assert.ThrowsAsync<SelectionException>(() =>
                freshEngine.PreviewRollbackConfigurationAcknowledgment(plan.ExpectedSelectionRevision, effects).Completion)).Failure);
        else
        {
            var fresh = await freshEngine.PreviewRollbackConfigurationAcknowledgment(plan.ExpectedSelectionRevision, effects).Completion;
            Assert.NotEqual(plan.OperationId, fresh.OperationId);
            Assert.Equal(plan.ProposedSelectionRevision + 1, fresh.ProposedSelectionRevision);
            Assert.Equal(RollbackAcknowledgment.Recorded,
                (await freshEngine.AcknowledgeRollbackConfiguration(fresh, ApproveAcknowledgment(fresh), effects).Completion).Acknowledgment);
        }
        Unchanged(retained.Where(pair => pair.Key != f.Control).ToDictionary()); Unchanged(data);
    }

    [Fact]
    public async Task RetainedMaximumReservationRefusesOverflowWithoutGenerationReuse()
    {
        using var f = new SelectionFixture(keys);
        await Interrupt(f);
        var engine = f.Engine((point, path, _) =>
        {
            if (point == SelectionIoPoint.AfterWrite && Path.GetFileName(path) == "journal-v4.json")
                throw new IOException();
        });
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationAcknowledgment(engine.Inspect().Revision, effects).Completion;
        await engine.AcknowledgeRollbackConfiguration(plan, ApproveAcknowledgment(plan), effects).Completion;
        var path = Path.Combine(f.Root, "transaction-" + plan.OperationId.ToString("N"), "journal-v4.json");
        var journal = Wire.Read<SelectionJournalV4>(File.ReadAllBytes(path), 131072);
        File.WriteAllBytes(path, Wire.Write(journal with { After = journal.After with { Revision = long.MaxValue } }));
        var retained = History(f); var data = Data(f);
        Assert.Equal(SelectionFailure.CapacityExceeded, (await Assert.ThrowsAsync<SelectionException>(() =>
            f.Engine().PreviewRollbackConfigurationAcknowledgment(plan.ExpectedSelectionRevision, effects).Completion)).Failure);
        Unchanged(retained); Unchanged(data);
    }

    [Theory]
    [InlineData(127)]
    [InlineData(128)]
    public async Task ExactChildCapacityNeverMakesReadablePendingHistoryUnreadable(int children)
    {
        using var f = new SelectionFixture(keys);
        await Interrupt(f);
        var index = 0;
        while (Directory.GetFileSystemEntries(f.Root).Length < children)
            File.WriteAllText(Path.Combine(f.Root, $"retained-owner-{index++:D3}.txt"), "PRESERVE");
        var before = History(f); var data = Data(f);
        var engine = f.Engine(); var effects = new SetupOperationRunner();
        var pending = engine.Inspect();
        RollbackAcknowledgmentResult? result = null;
        var error = await Record.ExceptionAsync(async () =>
        {
            var plan = await engine.PreviewRollbackConfigurationAcknowledgment(pending.Revision, effects).Completion;
            result = await engine.AcknowledgeRollbackConfiguration(plan, ApproveAcknowledgment(plan), effects).Completion;
        });
        Assert.Equal(128, Directory.GetFileSystemEntries(f.Root).Length);
        if (children == 128)
        {
            Assert.Equal(SelectionFailure.CapacityExceeded, Assert.IsType<SelectionException>(error).Failure);
            Assert.Equal(pending.Revision, f.Engine().Inspect().Revision);
            Assert.Equal(RollbackRestoreProgress.SettingsCommittedSelectionPending, f.Engine().Inspect().ConfigurationRestoreProgress);
            Unchanged(before);
        }
        else
        {
            Assert.Null(error);
            Assert.Equal(RollbackAcknowledgment.Recorded, result!.Acknowledgment);
            Assert.Equal(result.Selection!.Revision, f.Engine().Inspect().Revision);
            Unchanged(before.Where(pair => pair.Key != f.Control).ToDictionary());
        }
        Unchanged(data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChildCapacityIsRecheckedAtAdmissionAndImmediatelyBeforeDirectoryCreation(bool atCreation)
    {
        using var f = new SelectionFixture(keys);
        await Interrupt(f);
        var index = 0;
        while (Directory.GetFileSystemEntries(f.Root).Length < 127)
            File.WriteAllText(Path.Combine(f.Root, $"retained-owner-{index++:D3}.txt"), "PRESERVE");
        var engine = f.Engine((point, path, _) =>
        {
            if (atCreation && point == SelectionIoPoint.BeforeCreate &&
                Path.GetFileName(path).StartsWith("transaction-", StringComparison.Ordinal))
                File.WriteAllText(Path.Combine(f.Root, "other-owner.txt"), "NEW OWNER DATA");
        });
        var effects = new SetupOperationRunner();
        var revision = engine.Inspect().Revision;
        var plan = await engine.PreviewRollbackConfigurationAcknowledgment(revision, effects).Completion;
        if (!atCreation) File.WriteAllText(Path.Combine(f.Root, "other-owner.txt"), "NEW OWNER DATA");
        var before = History(f); var data = Data(f);
        RollbackAcknowledgmentResult? result = null;
        var error = await Record.ExceptionAsync(async () => result =
            await engine.AcknowledgeRollbackConfiguration(plan, ApproveAcknowledgment(plan), effects).Completion);
        Assert.Equal(SelectionFailure.CapacityExceeded,
            atCreation ? result!.Failure : Assert.IsType<SelectionException>(error).Failure);
        if (atCreation) Assert.Equal(RollbackAcknowledgment.NotRecorded, result!.Acknowledgment);
        Assert.Equal(128, Directory.GetFileSystemEntries(f.Root).Length);
        Assert.False(Directory.Exists(Path.Combine(f.Root, "transaction-" + plan.OperationId.ToString("N"))));
        Assert.Equal(revision, f.Engine().Inspect().Revision);
        Unchanged(before); Unchanged(data);
    }

    [Theory]
    [InlineData("control-before")]
    [InlineData("control-bootstrap")]
    [InlineData("missing-commit")]
    [InlineData("missing-old-pending")]
    [InlineData("missing-selected")]
    [InlineData("old-terminal")]
    [InlineData("new-pending")]
    [InlineData("new-restore-marker")]
    [InlineData("recovery")]
    [InlineData("wrong-kind")]
    [InlineData("dual-journal")]
    [InlineData("future")]
    [InlineData("source")]
    [InlineData("generation")]
    [InlineData("previous")]
    [InlineData("digest")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("oversize")]
    public async Task VersionFourCannotRewriteOldHistoryOrHideAmbiguousPublications(string tamper)
    {
        using var f = new SelectionFixture(keys);
        var old = await Interrupt(f);
        var before = File.ReadAllBytes(f.Control);
        var engine = f.Engine(); var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationAcknowledgment(engine.Inspect().Revision, effects).Completion;
        var complete = await engine.AcknowledgeRollbackConfiguration(plan, ApproveAcknowledgment(plan), effects).Completion;
        Assert.Equal(RollbackAcknowledgment.Recorded, complete.Acknowledgment);
        var oldDirectory = Path.Combine(f.Root, "transaction-" + old.OperationId.ToString("N"));
        var directory = Path.Combine(f.Root, "transaction-" + plan.OperationId.ToString("N"));
        var path = Path.Combine(directory, "journal-v4.json");
        var bytes = File.ReadAllBytes(path);
        var journal = Wire.Read<SelectionJournalV4>(bytes, 131072);
        switch (tamper)
        {
            case "control-before": File.WriteAllBytes(f.Control, before); break;
            case "control-bootstrap":
                var bootstrap = Wire.Read<ControlDocument>(before, 32768);
                File.WriteAllBytes(f.Control, Wire.Write(bootstrap with { FormatVersion = 2, Revision = 0,
                    Current = null, Previous = null, LastTransaction = null, Pending = null })); break;
            case "missing-commit": File.Delete(Path.Combine(oldDirectory, "restore-committed.json")); break;
            case "missing-old-pending": File.Delete(Path.Combine(oldDirectory, "pending-publication.json")); break;
            case "missing-selected": File.Delete(Path.Combine(directory, "selected-publication.json")); break;
            case "old-terminal":
                var oldJournal = Wire.Read<SelectionJournalV3>(File.ReadAllBytes(Path.Combine(oldDirectory, "journal-v3.json")), 131072);
                File.WriteAllBytes(Path.Combine(oldDirectory, "selected-publication.json"), Wire.Write(oldJournal.After)); break;
            case "new-pending": File.WriteAllBytes(Path.Combine(directory, "pending-publication.json"), before); break;
            case "new-restore-marker": File.Copy(Path.Combine(oldDirectory, "restore-committed.json"), Path.Combine(directory, "restore-committed.json")); break;
            case "recovery": File.WriteAllBytes(Path.Combine(directory, "recovered-0.json.publication"), Wire.Write(journal.After)); break;
            case "wrong-kind": File.WriteAllBytes(path, Wire.Write(journal with { Kind = SelectionTransactionKind.ConfigurationRestore })); break;
            case "dual-journal": File.Copy(path, Path.Combine(directory, "journal-v3.json")); break;
            case "future": File.WriteAllBytes(path, Wire.Write(journal with { FormatVersion = 5 })); break;
            case "source": File.WriteAllBytes(path, Wire.Write(journal with { Acknowledgment = journal.Acknowledgment! with { RestoreTransactionId = journal.TransactionId } })); break;
            case "generation": File.WriteAllBytes(path, Wire.Write(journal with { After = journal.After with { Revision = plan.SkippedRestoreTerminalRevision } })); break;
            case "previous": File.WriteAllBytes(path, Wire.Write(journal with { After = journal.After with { Previous = null } })); break;
            case "digest": File.WriteAllBytes(path, Wire.Write(journal with { Acknowledgment = journal.Acknowledgment! with { CommitDigest = new('0', 64) } })); break;
            case "unknown":
                var node = JsonNode.Parse(bytes)!.AsObject(); node["unexpected"] = true;
                File.WriteAllText(path, node.ToJsonString()); break;
            case "duplicate": File.WriteAllText(path, System.Text.Encoding.UTF8.GetString(bytes).Replace("\"formatVersion\":4,", "\"formatVersion\":4,\"formatVersion\":4,", StringComparison.Ordinal)); break;
            case "oversize": File.WriteAllBytes(path, new byte[131073]); break;
        }
        var retained = History(f); var data = Data(f);
        for (var index = 0; index < 2; index++)
        {
            var error = Assert.Throws<SelectionException>(() => f.Engine().Inspect());
            Assert.Equal(SelectionFailure.InvalidControl, error.Failure);
            Assert.Equal(SelectionFailure.InvalidControl, Assert.Throws<SelectionException>(() => f.Engine().Recover()).Failure);
            Unchanged(retained); Unchanged(data);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalPublicationVetoChecksNewEvidenceEvenAfterIntentWasWritten(bool conflict)
    {
        using var f = new SelectionFixture(keys);
        await Interrupt(f);
        var hit = false;
        var engine = f.Engine((point, path, _) =>
        {
            if (point != SelectionIoPoint.BeforePublicationRename) return;
            hit = true;
            if (conflict)
            {
                var orphan = Path.Combine(f.Root, "transaction-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(orphan);
                File.WriteAllText(Path.Combine(orphan, "selected-publication.json"), "{}");
            }
            else
            {
                File.Move(path, path + ".preserved");
                File.WriteAllText(path, "{}");
            }
        });
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationAcknowledgment(engine.Inspect().Revision, effects).Completion;
        var data = Data(f);
        var result = await engine.AcknowledgeRollbackConfiguration(plan, ApproveAcknowledgment(plan), effects).Completion;
        Assert.True(hit);
        Assert.Equal(RollbackAcknowledgment.PublicationAmbiguous, result.Acknowledgment);
        Assert.Null(result.Selection);
        Assert.Equal(SelectionFailure.InvalidControl, Assert.Throws<SelectionException>(() => f.Engine().Inspect()).Failure);
        Unchanged(data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterReadErrorPreservesKnownFactsButNeverAuthorizesPublication(bool afterIntent)
    {
        var fail = false;
        using var f = new SelectionFixture(keys, settingsIo: (point, _) =>
        {
            if (fail && point == SettingsIoPoint.BeforeRead) throw new IOException("PRIVATE READ FAILURE");
        });
        await Interrupt(f);
        var engine = f.Engine((point, _, _) =>
        {
            if (point == (afterIntent ? SelectionIoPoint.BeforePublicationRename : SelectionIoPoint.BeforeAcknowledgmentPublication))
                fail = true;
        });
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationAcknowledgment(engine.Inspect().Revision, effects).Completion;
        var result = await engine.AcknowledgeRollbackConfiguration(plan, ApproveAcknowledgment(plan), effects).Completion;
        Assert.True(fail);
        Assert.Equal(RecoveryFailure.Unavailable, result.ConfigurationFailure);
        Assert.Equal(RollbackHistoricalCommitVerification.VerifiedRecordedCommit, result.HistoricalCommitVerification);
        Assert.Equal(RollbackCurrentSettingsVerification.VerifiedPresentState, result.CurrentSettingsVerification);
        Assert.Equal(plan.ExpectedSettingsRevision, result.ObservedSettingsRevision);
        Assert.Equal(afterIntent ? RollbackAcknowledgment.PublicationAmbiguous : RollbackAcknowledgment.NotRecorded, result.Acknowledgment);
        Assert.Null(result.Selection);
    }
}
