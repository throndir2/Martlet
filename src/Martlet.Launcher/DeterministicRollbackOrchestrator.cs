using Martlet.Core.Settings;
using Martlet.Updates;

namespace Martlet.Launcher;

public sealed class DeterministicRollbackOrchestrator
{
    private readonly LocalSelectionEngine selection;
    private readonly LocalActivationEngine activation;

    public DeterministicRollbackOrchestrator(
        LocalSelectionEngine selection,
        LocalActivationEngine activation)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(activation);
        this.selection = selection;
        this.activation = activation;
    }

    public SelectionPlan PrepareRollbackSelection(
        long expectedActivationRevision,
        long expectedSelectionRevision,
        string freshSnapshotPath,
        CancellationToken token = default)
    {
        var plan = activation.PrepareRollbackSelection(
            expectedActivationRevision,
            expectedSelectionRevision,
            freshSnapshotPath,
            token);
        if (plan.Kind != SelectionKind.Rollback)
            throw new LauncherException(LauncherFailure.InvalidActivation);
        return plan;
    }

    public SelectionReceipt CommitRollbackSelection(
        SelectionPlan plan,
        SelectionApproval approval,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Kind != SelectionKind.Rollback)
            throw new LauncherException(LauncherFailure.InvalidActivation);
        var receipt = selection.CommitSelection(plan, approval, token);
        if (receipt.Status != SelectionStatus.AwaitingConfigurationRestore ||
            receipt.CurrentSelection?.ConfigurationRestoreRequired != true)
            throw new LauncherException(LauncherFailure.InvalidActivation);
        return receipt;
    }

    public RollbackRestoreOperation<RollbackRestorePlan> PreviewConfigurationRestore(
        long expectedSelectionRevision,
        SetupOperationRunner sharedEffects) =>
        selection.PreviewRollbackConfigurationRestore(
            expectedSelectionRevision, sharedEffects);

    public RollbackRestoreOperation<RollbackRestoreResult> RestoreConfiguration(
        RollbackRestorePlan plan,
        RollbackRestoreApproval approval,
        SetupOperationRunner sharedEffects) =>
        selection.RestoreRollbackConfiguration(
            plan, approval, sharedEffects);

    public RollbackRestoreOperation<RollbackAcknowledgmentPlan>
        PreviewConfigurationAcknowledgment(
            long expectedSelectionRevision,
            SetupOperationRunner sharedEffects) =>
        selection.PreviewRollbackConfigurationAcknowledgment(
            expectedSelectionRevision, sharedEffects);

    public RollbackRestoreOperation<RollbackAcknowledgmentResult>
        AcknowledgeConfiguration(
            RollbackAcknowledgmentPlan plan,
            RollbackAcknowledgmentApproval approval,
            SetupOperationRunner sharedEffects) =>
        selection.AcknowledgeRollbackConfiguration(
            plan, approval, sharedEffects);

    public ActivationPlan PrepareActivation(
        long expectedActivationRevision,
        long expectedSelectionRevision,
        CancellationToken token = default)
    {
        var plan = activation.PrepareActivation(
            expectedActivationRevision, expectedSelectionRevision, token);
        if (plan.Kind != ActivationTransitionKind.Rollback)
            throw new LauncherException(LauncherFailure.InvalidActivation);
        return plan;
    }

    public ActivationReceipt Activate(
        ActivationPlan plan,
        ActivationApproval approval,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Kind != ActivationTransitionKind.Rollback)
            throw new LauncherException(LauncherFailure.InvalidActivation);
        var receipt = activation.Activate(plan, approval, token);
        if (receipt.Outcome != ActivationOutcome.RolledBack)
            throw new LauncherException(LauncherFailure.InvalidActivation);
        return receipt;
    }

}
