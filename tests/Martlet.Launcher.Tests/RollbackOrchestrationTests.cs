using Martlet.Core.Settings;
using Martlet.Updates;

namespace Martlet.Launcher.Tests;

[Collection(LauncherProcessCollection.Name)]
public sealed class RollbackOrchestrationTests
{
    [Fact]
    public async Task RollbackUsesOnlyRetainedPreviousAfterRecordedRestore()
    {
        using var fixture = new LauncherFixture();
        fixture.InitializeSelection();
        fixture.Select(fixture.Stage("0.2.0.0"));
        fixture.ConfigureActivation(TimeSpan.FromSeconds(2));
        fixture.InitializeActivation();
        fixture.ActivateCurrent();
        fixture.ChangeSettings();
        var lastActiveSettings = await fixture.Settings.LoadAsync();
        var selected = fixture.Select(fixture.Stage("0.3.0.0"));
        var beforeRollback = fixture.ActivateCurrent();
        fixture.ChangeSettings();
        var orchestrator = new DeterministicRollbackOrchestrator(
            fixture.Selection, fixture.Activation!);

        var selectionPlan = orchestrator.PrepareRollbackSelection(
            beforeRollback.Revision,
            selected.Revision,
            fixture.Snapshot());
        Assert.Equal("0.2.0.0", selectionPlan.Candidate.Version);
        Assert.Equal(lastActiveSettings.Revision, selectionPlan.Candidate.SnapshotSourceRevision);
        var rollbackSelection = orchestrator.CommitRollbackSelection(
            selectionPlan,
            selectionPlan.Approve(
                selectionPlan.TransactionId,
                selectionPlan.ExpectedRevision,
                selectionPlan.PlanDigest));
        Assert.Equal(
            SelectionStatus.AwaitingConfigurationRestore,
            rollbackSelection.Status);

        var effects = new SetupOperationRunner();
        var restorePlan = await orchestrator.PreviewConfigurationRestore(
            rollbackSelection.Revision, effects).Completion;
        var restore = await orchestrator.RestoreConfiguration(
            restorePlan,
            restorePlan.Approve(
                restorePlan.OperationId,
                restorePlan.ExpectedSelectionRevision,
                restorePlan.ExpectedSettingsRevision,
                restorePlan.PlanDigest),
            effects).Completion;
        Assert.Equal(
            RollbackAcknowledgment.Recorded, restore.Acknowledgment);
        Assert.Equal(
            RollbackSettingsProgress.VerifiedCommitted,
            restore.SettingsProgress);
        Assert.Equal(
            RollbackRestoreProgress.Recorded,
            restore.Selection!.ConfigurationRestoreProgress);

        var activationPlan = orchestrator.PrepareActivation(
            beforeRollback.Revision,
            restore.Selection.Revision);
        Assert.Equal(
            ActivationTransitionKind.Rollback, activationPlan.Kind);
        Assert.Equal("0.2.0.0", activationPlan.Candidate.Version);
        var rolledBack = orchestrator.Activate(
            activationPlan,
            activationPlan.Approve(
                activationPlan.TransactionId,
                activationPlan.ExpectedActivationRevision,
                activationPlan.ExpectedSelectionRevision,
                activationPlan.ExpectedSettingsRevision,
                activationPlan.PlanDigest));

        Assert.Equal(ActivationOutcome.RolledBack, rolledBack.Outcome);
        Assert.Equal("0.2.0.0", rolledBack.Current!.Version);
        Assert.Equal("0.3.0.0", rolledBack.Previous!.Version);
        Assert.Equal(
            restore.ResultingSettingsRevision,
            (await fixture.Settings.LoadAsync()).Revision);
    }

    [Fact]
    public async Task RetainedActivePreviousSurvivesSelectionAheadOfActivation()
    {
        using var fixture = new LauncherFixture();
        fixture.InitializeSelection();
        fixture.Select(fixture.Stage("0.2.0.0"));
        fixture.ConfigureActivation(TimeSpan.FromSeconds(2));
        fixture.InitializeActivation();
        fixture.ActivateCurrent();
        fixture.ChangeSettings();
        var lastActiveSettings = await fixture.Settings.LoadAsync();
        fixture.Select(fixture.Stage("0.3.0.0"));
        fixture.ActivateCurrent();
        var ahead = fixture.Select(fixture.Stage(
            "0.4.0.0", activationMode: "wrong-nonce"));
        var failed = Assert.Throws<ActivationException>(
            fixture.ActivateCurrent);
        var active = fixture.Activation!.Recover(failed.TransactionId);
        Assert.Equal("0.3.0.0", active.Current!.Version);
        Assert.Equal("0.2.0.0", active.Previous!.Version);
        Assert.Equal("0.4.0.0", ahead.CurrentSelection!.Version);
        fixture.ChangeSettings();
        var orchestrator = new DeterministicRollbackOrchestrator(
            fixture.Selection, fixture.Activation);

        var selectionPlan = orchestrator.PrepareRollbackSelection(
            active.Revision,
            ahead.Revision,
            fixture.Snapshot());
        Assert.Equal("0.2.0.0", selectionPlan.Candidate.Version);
        Assert.Equal(lastActiveSettings.Revision, selectionPlan.Candidate.SnapshotSourceRevision);
        Assert.Equal(
            "0.3.0.0", selectionPlan.PreviousAfterSelection!.Version);
        var selected = orchestrator.CommitRollbackSelection(
            selectionPlan,
            selectionPlan.Approve(
                selectionPlan.TransactionId,
                selectionPlan.ExpectedRevision,
                selectionPlan.PlanDigest));
        var effects = new SetupOperationRunner();
        var restorePlan = await orchestrator.PreviewConfigurationRestore(
            selected.Revision, effects).Completion;
        var restored = await orchestrator.RestoreConfiguration(
            restorePlan,
            restorePlan.Approve(
                restorePlan.OperationId,
                restorePlan.ExpectedSelectionRevision,
                restorePlan.ExpectedSettingsRevision,
                restorePlan.PlanDigest),
            effects).Completion;
        var activationPlan = orchestrator.PrepareActivation(
            active.Revision, restored.Selection!.Revision);
        var rolledBack = orchestrator.Activate(
            activationPlan,
            activationPlan.Approve(
                activationPlan.TransactionId,
                activationPlan.ExpectedActivationRevision,
                activationPlan.ExpectedSelectionRevision,
                activationPlan.ExpectedSettingsRevision,
                activationPlan.PlanDigest));

        Assert.Equal("0.2.0.0", rolledBack.Current!.Version);
        Assert.Equal("0.3.0.0", rolledBack.Previous!.Version);
    }

    [Fact]
    public void OrchestratorRejectsForwardActivationAndDoesNotRetry()
    {
        using var fixture = new LauncherFixture();
        fixture.InitializeSelection();
        fixture.Select(fixture.Stage("0.2.0.0"));
        fixture.ConfigureActivation(TimeSpan.FromSeconds(1));
        fixture.InitializeActivation();
        var activation = fixture.Activation!;
        var orchestrator = new DeterministicRollbackOrchestrator(
            fixture.Selection, activation);

        var error = Assert.Throws<LauncherException>(() =>
            orchestrator.PrepareActivation(
                activation.Inspect().Revision,
                fixture.Selection.Inspect().Revision));

        Assert.Equal(LauncherFailure.InvalidActivation, error.Failure);
        Assert.Empty(fixture.Attempts());
        Assert.False(activation.Inspect().IsRunnable);
    }
}
