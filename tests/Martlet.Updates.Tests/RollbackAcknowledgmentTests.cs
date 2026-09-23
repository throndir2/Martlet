using System.Text;
using Martlet.Core.Settings;
using static Martlet.Updates.Tests.RollbackRestoreTests;

namespace Martlet.Updates.Tests;

public sealed class RollbackAcknowledgmentTests(SigningKeys keys) : IClassFixture<SigningKeys>
{
    internal static RollbackAcknowledgmentApproval ApproveAcknowledgment(RollbackAcknowledgmentPlan plan) =>
        plan.Approve(plan.OperationId, plan.ExpectedSelectionRevision, plan.ExpectedSettingsRevision, plan.PlanDigest);

    internal static async Task<RollbackRestoreResult> Interrupt(SelectionFixture f, bool marker = true)
    {
        var rollback = f.Rollback();
        return await InterruptSelected(f, rollback.Revision, marker);
    }

    internal static async Task<RollbackRestoreResult> InterruptSelected(SelectionFixture f, long revision, bool marker = true)
    {
        var engine = f.Engine((point, _, _) =>
        {
            if (point == (marker ? SelectionIoPoint.BeforeRestoreAcknowledgment : SelectionIoPoint.AfterConfigurationRestore))
                throw new IOException("Controlled genuine restore interruption");
        });
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationRestore(revision, effects).Completion;
        var result = await engine.RestoreRollbackConfiguration(plan, Approve(plan), effects).Completion;
        Assert.Equal(RollbackSettingsProgress.VerifiedCommitted, result.SettingsProgress);
        Assert.Equal(RollbackAcknowledgment.NotRecorded, result.Acknowledgment);
        Assert.False(effects.IsRunning);
        return result;
    }

    internal static Dictionary<string, byte[]> Data(SelectionFixture f) =>
        Directory.GetFiles(f.Settings.DataDirectory).Where(path => !path.EndsWith(".lock", StringComparison.Ordinal))
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);

    internal static void Unchanged(Dictionary<string, byte[]> before)
    {
        foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshConsentAcknowledgesActualRestoredBytesWithoutRestoringAgain(bool legacy)
    {
        var forbidWrites = false;
        var mutations = 0;
        using var f = new SelectionFixture(keys, legacy, (point, _) =>
        {
            if (forbidWrites && point is not (SettingsIoPoint.BeforeRead or SettingsIoPoint.AfterRead))
            {
                mutations++;
                throw new IOException("Acknowledgment must not mutate settings");
            }
        });
        var old = await Interrupt(f);
        forbidWrites = true;
        var pending = f.Engine().Inspect();
        var history = History(f);
        var data = Data(f);
        var engine = f.Engine();
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationAcknowledgment(pending.Revision, effects).Completion;
        Unchanged(history); Unchanged(data);
        Assert.Equal(history.Keys.Order(), History(f).Keys.Order());
        Assert.Equal(old.OperationId, plan.InterruptedRestoreTransactionId);
        Assert.Equal(old.ResultingSettingsRevision, plan.ExpectedSettingsRevision);
        Assert.Equal(AppSettings.CurrentSchemaVersion, plan.SettingsSchemaVersion);
        Assert.Equal(legacy ? 1 : AppSettings.CurrentSchemaVersion, plan.OriginalSchemaVersion);
        Assert.Equal(File.ReadAllText(f.Settings.FilePath), plan.CurrentJson);
        Assert.Contains("WITHOUT performing another restore", plan.PlannedEffects);
        Assert.Equal(pending.Revision + 2, plan.ProposedSelectionRevision);
        Assert.Equal(pending.Revision + 1, plan.SkippedRestoreTerminalRevision);
        var result = await engine.AcknowledgeRollbackConfiguration(plan, ApproveAcknowledgment(plan), effects).Completion;
        Assert.Equal(RollbackAcknowledgment.Recorded, result.Acknowledgment);
        Assert.Equal(RollbackHistoricalCommitVerification.VerifiedRecordedCommit, result.HistoricalCommitVerification);
        Assert.Equal(RollbackCurrentSettingsVerification.VerifiedPresentState, result.CurrentSettingsVerification);
        Assert.Null(result.Failure); Assert.Null(result.ConfigurationFailure); Assert.Null(result.PackageFailure);
        Assert.False(result.OwnershipFailed); Assert.False(result.IsRunnable);
        Assert.False(effects.IsRunning);
        Assert.Equal(0, mutations);
        Unchanged(history.Where(pair => pair.Key != f.Control).ToDictionary());
        Unchanged(data);
        Assert.Equal(data.Keys.Order(), Data(f).Keys.Order());
        Assert.False(File.Exists(Path.Combine(f.Root, "transaction-" + old.OperationId.ToString("N"), "selected-publication.json")));
        var newDirectory = Path.Combine(f.Root, "transaction-" + plan.OperationId.ToString("N"));
        Assert.Equal(new[] { "before.json", "journal-v4.json", "selected-publication.json" },
            Directory.GetFiles(newDirectory).Select(Path.GetFileName).Order());
        var journal = Wire.Read<SelectionJournalV4>(File.ReadAllBytes(Path.Combine(newDirectory, "journal-v4.json")), 131072);
        Assert.Null(journal.Pending); Assert.Null(journal.Restore);
        Assert.Equal(old.OperationId, journal.Before.Pending);
        Assert.Equal(SelectionTransactionKind.ConfigurationAcknowledgment, journal.Kind);
        var complete = History(f);
        for (var index = 0; index < 2; index++)
        {
            var receipt = index == 0 ? f.Engine().Inspect() : f.Engine().Recover(old.OperationId);
            Assert.Equal(SelectionOutcome.Unchanged, receipt.Outcome);
            Assert.Equal(plan.ProposedSelectionRevision, receipt.Revision);
            Assert.Equal(plan.OperationId, receipt.TransactionId);
            Assert.Equal(old.OperationId, receipt.AcknowledgedRestoreTransactionId);
            Assert.Equal(RollbackRestoreProgress.AcknowledgedVerifiedState, receipt.ConfigurationRestoreProgress);
            Assert.Equal(SelectionStatus.AwaitingReadiness, receipt.Status);
            Assert.False(receipt.IsRunnable);
        }
        Unchanged(complete);
        f.ChangeSettings();
        Assert.Equal(plan.ProposedSelectionRevision, f.Engine().Recover().Revision);
        f.Package.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData("no-marker")]
    [InlineData("settings")]
    [InlineData("settings-case")]
    [InlineData("future-settings")]
    [InlineData("missing-settings")]
    [InlineData("malformed-settings")]
    [InlineData("profile")]
    [InlineData("original")]
    [InlineData("missing-original")]
    [InlineData("marker")]
    [InlineData("marker-revision")]
    [InlineData("source")]
    [InlineData("stage")]
    [InlineData("previous-stage")]
    [InlineData("trust")]
    [InlineData("terminal-intent")]
    [InlineData("partial-terminal")]
    [InlineData("revision")]
    [InlineData("capacity")]
    public async Task PreviewRefusesAnythingExceptProvenUnambiguousAndPresentState(string change)
    {
        using var f = new SelectionFixture(keys);
        var old = await Interrupt(f, marker: change != "no-marker");
        var pending = f.Engine().Inspect();
        var directory = Path.Combine(f.Root, "transaction-" + old.OperationId.ToString("N"));
        switch (change)
        {
            case "settings": File.AppendAllText(f.Settings.FilePath, "\n"); break;
            case "settings-case":
                var marker = Wire.Read<RestoreCommitDocument>(File.ReadAllBytes(Path.Combine(directory, "restore-committed.json")), 32768);
                File.WriteAllBytes(Path.Combine(directory, "restore-committed.json"), Wire.Write(marker with { Revision = marker.Revision.ToLowerInvariant() }));
                break;
            case "future-settings": File.WriteAllText(f.Settings.FilePath, "{\"schema_version\":99}"); break;
            case "missing-settings": File.Delete(f.Settings.FilePath); break;
            case "malformed-settings": File.WriteAllText(f.Settings.FilePath, "{"); break;
            case "profile":
                var current = await f.Settings.LoadAsync();
                File.WriteAllBytes(f.Settings.FilePath, Martlet.Core.Contracts.ContractJson.Write(current.Settings! with
                { Profile = current.Settings!.Profile with { Id = Guid.NewGuid() } }));
                break;
            case "original": File.AppendAllText(old.OriginalSnapshot!, " "); break;
            case "missing-original": File.Delete(old.OriginalSnapshot!); break;
            case "marker": File.WriteAllText(Path.Combine(directory, "restore-committed.json"), "{}"); break;
            case "marker-revision":
                var commit = Wire.Read<RestoreCommitDocument>(File.ReadAllBytes(Path.Combine(directory, "restore-committed.json")), 32768);
                File.WriteAllBytes(Path.Combine(directory, "restore-committed.json"), Wire.Write(commit with { Revision = new('A', 64) }));
                break;
            case "source": File.AppendAllText(Path.Combine(f.Root, pending.CurrentSelection!.SnapshotRelativePath.Replace('/', '\\')), " "); break;
            case "stage": File.AppendAllText(Path.Combine(f.Package.StagingRoot, pending.CurrentSelection!.StageName, "candidate.json"), " "); break;
            case "previous-stage": File.AppendAllText(Path.Combine(f.Package.StagingRoot, pending.PreviousSelection!.StageName, "candidate.zip"), " "); break;
            case "terminal-intent":
                var journal = Wire.Read<SelectionJournalV3>(File.ReadAllBytes(Path.Combine(directory, "journal-v3.json")), 131072);
                File.WriteAllBytes(Path.Combine(directory, "selected-publication.json"), Wire.Write(journal.After)); break;
            case "partial-terminal": File.WriteAllText(Path.Combine(directory, "selected-publication.json"), "{"); break;
            case "capacity":
                while (Directory.GetDirectories(f.Root, "transaction-*").Length < 32)
                    Directory.CreateDirectory(Path.Combine(f.Root, "transaction-" + Guid.NewGuid().ToString("N")));
                break;
        }
        var engine = change == "trust" ? f.Engine(staging: new(f.Package.StagingRoot,
            new([keys.Stranger.ExportSubjectPublicKeyInfo()]), () => f.Package.Current)) : f.Engine();
        var history = History(f); var data = Data(f);
        for (var index = 0; index < 2; index++)
        {
            var error = await Record.ExceptionAsync(async () => await engine.PreviewRollbackConfigurationAcknowledgment(
                pending.Revision + (change == "revision" ? 1 : 0), new()).Completion);
            Assert.True(error is SelectionException or RecoveryException or StagingException, error?.ToString());
            Unchanged(history); Unchanged(data);
            Assert.Equal(history.Keys.Order(), History(f).Keys.Order());
        }
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("selection")]
    [InlineData("settings")]
    [InlineData("digest")]
    public async Task WrongApprovalIssuanceBurnsAttemptAndTypesExposeNoAuthorityInjection(string wrong)
    {
        using var f = new SelectionFixture(keys);
        await Interrupt(f);
        var engine = f.Engine();
        var plan = await engine.PreviewRollbackConfigurationAcknowledgment(engine.Inspect().Revision, new()).Completion;
        foreach (var type in new[] { typeof(RollbackAcknowledgmentPlan), typeof(RollbackAcknowledgmentApproval), typeof(RollbackAcknowledgmentResult) })
        {
            Assert.Empty(type.GetConstructors());
            Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod));
        }
        Assert.DoesNotContain(f.Root, plan.ToString());
        Assert.DoesNotContain(plan.CurrentJson, plan.ToString());
        Assert.Equal(SelectionFailure.Conflict, Assert.Throws<SelectionException>(() => plan.Approve(
            wrong == "operation" ? Guid.NewGuid() : plan.OperationId,
            plan.ExpectedSelectionRevision + (wrong == "selection" ? 1 : 0),
            wrong == "settings" ? plan.ExpectedSettingsRevision.ToLowerInvariant() : plan.ExpectedSettingsRevision,
            wrong == "digest" ? new('0', 64) : plan.PlanDigest)).Failure);
        Assert.Throws<SelectionException>(() => ApproveAcknowledgment(plan));
    }

    [Theory]
    [InlineData("engine")]
    [InlineData("runner")]
    [InlineData("plan")]
    [InlineData("settings")]
    [InlineData("history")]
    [InlineData("superseded")]
    [InlineData("failed-preview")]
    [InlineData("restore-preview")]
    public async Task ApprovedPlanCannotEscapeExactOwnershipAndFrozenState(string change)
    {
        using var f = new SelectionFixture(keys);
        await Interrupt(f);
        var engine = f.Engine(); var effects = new SetupOperationRunner();
        var revision = engine.Inspect().Revision;
        var plan = await engine.PreviewRollbackConfigurationAcknowledgment(revision, effects).Completion;
        var approval = ApproveAcknowledgment(plan);
        var attemptedPlan = plan;
        if (change is "superseded" or "plan")
            attemptedPlan = await engine.PreviewRollbackConfigurationAcknowledgment(revision, effects).Completion;
        if (change == "superseded") attemptedPlan = plan;
        if (change == "failed-preview")
            await Assert.ThrowsAsync<SelectionException>(() => engine.PreviewRollbackConfigurationAcknowledgment(revision + 1, effects).Completion);
        if (change == "restore-preview")
            await Assert.ThrowsAsync<SelectionException>(() => engine.PreviewRollbackConfigurationRestore(revision, effects).Completion);
        if (change == "settings") File.AppendAllText(f.Settings.FilePath, "\n");
        if (change == "history") Directory.CreateDirectory(Path.Combine(f.Root, "transaction-" + Guid.NewGuid().ToString("N")));
        var history = History(f); var data = Data(f);
        RollbackAcknowledgmentResult? result = null;
        var error = await Record.ExceptionAsync(async () => result = await (change == "engine" ? f.Engine() : engine)
            .AcknowledgeRollbackConfiguration(attemptedPlan, approval, change == "runner" ? new() : effects).Completion);
        if (change == "settings")
        {
            Assert.Null(error);
            Assert.Equal(SelectionFailure.Conflict, result!.Failure);
            Assert.Equal(RollbackAcknowledgment.NotRecorded, result.Acknowledgment);
            Assert.Equal(RollbackCurrentSettingsVerification.NotVerified, result.CurrentSettingsVerification);
            Assert.Null(result.Selection);
        }
        else Assert.NotNull(error);
        Assert.Throws<SelectionException>(() => engine.AcknowledgeRollbackConfiguration(plan, approval, effects));
        Unchanged(history); Unchanged(data);
    }

    [Fact]
    public async Task SubsequentSelectionRestoreAndKnownCommittedReconciliationStayVersionFour()
    {
        using var f = new SelectionFixture(keys);
        await Interrupt(f);
        var engine = f.Engine(); var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationAcknowledgment(engine.Inspect().Revision, effects).Completion;
        await engine.AcknowledgeRollbackConfiguration(plan, ApproveAcknowledgment(plan), effects).Completion;
        f.RefreshFacts();
        var next = f.Select(f.Stage("0.4.0.0"));
        var rollback = engine.PrepareRollback(next.Revision, f.Snapshot());
        var selected = engine.CommitSelection(rollback, SelectionFixture.Approve(rollback));
        var old = await InterruptSelected(f, selected.Revision);
        Assert.Equal(4, Wire.Read<ControlDocument>(File.ReadAllBytes(f.Control), 32768).FormatVersion);
        var fresh = await engine.PreviewRollbackConfigurationAcknowledgment(engine.Inspect().Revision, effects).Completion;
        var result = await engine.AcknowledgeRollbackConfiguration(fresh, ApproveAcknowledgment(fresh), effects).Completion;
        Assert.Equal(RollbackAcknowledgment.Recorded, result.Acknowledgment);
        Assert.Equal(old.OperationId, result.Selection!.AcknowledgedRestoreTransactionId);
        Assert.Equal(result.Selection.Revision, f.Engine().Recover().Revision);
        f.Package.AssertPrivateDataUnchanged();
    }
}
