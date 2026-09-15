using Martlet.Core.Settings;

namespace Martlet.Updates;

public enum RollbackRestoreProgress
{
    None, AwaitingConsent, OutcomeUnproven, SettingsCommittedSelectionPending, Recorded, AcknowledgedVerifiedState
}

public enum RollbackSettingsProgress { NotAttempted, NotCommitted, OutcomeUnproven, VerifiedCommitted }
public enum RollbackAcknowledgment { NotRecorded, PublicationAmbiguous, Recorded }

public sealed class RollbackRestoreOperation<T>
{
    private readonly SetupOperation operation;
    public Task<T> Completion { get; }
    internal RollbackRestoreOperation(SetupOperation operation, Task<T> work, Func<T, T> ownerFailed, Action released)
    {
        this.operation = operation;
        Completion = CompleteAsync(operation, work, ownerFailed, released);
    }
    public void RequestCancellation() => operation.RequestCancellation();

    private static async Task<T> CompleteAsync(SetupOperation operation, Task<T> work, Func<T, T> ownerFailed, Action released)
    {
        try
        {
            var retired = await operation.Completion.ConfigureAwait(false);
            if (!work.IsCompleted)
                throw new SelectionException(retired.Outcome == SetupWorkOutcome.Canceled
                    ? SelectionFailure.Cancelled : SelectionFailure.Unavailable);
            var result = await work.ConfigureAwait(false);
            return retired.Outcome == SetupWorkOutcome.Failed ? ownerFailed(result) : result;
        }
        finally { released(); }
    }
}

public sealed class RollbackRestorePlan
{
    internal LocalSelectionEngine Owner { get; }
    internal SetupOperationRunner Effects { get; }
    internal long Sequence { get; }
    internal ControlDocument Before { get; }
    internal ConfigurationRestorePlan CorePlan { get; }
    internal RestoreLifetime Lifetime { get; }
    private int approved;
    public Guid OperationId { get; } = Guid.NewGuid();
    public Guid RollbackTransactionId => Before.LastTransaction!.Value;
    public long ExpectedSelectionRevision => Before.Revision;
    public string ControlDigest { get; }
    public SelectedVersion Candidate { get; }
    public SelectedVersion? Previous { get; }
    public string SettingsPath => CorePlan.Destination;
    public Guid ProfileId => CorePlan.ProfileId;
    public string ExpectedSettingsRevision => CorePlan.ExpectedRevision;
    public int CurrentSettingsSchemaVersion { get; }
    public string SnapshotRelativePath => Candidate.SnapshotRelativePath;
    public ConfigurationSnapshotInspection Snapshot { get; }
    public string CandidateJson => CorePlan.CandidateJson;
    public string CandidateDigest => CorePlan.CandidateDigest;
    public string Summary => CorePlan.Summary;
    public string Scope => ConfigurationSnapshot.Scope;
    public DateTimeOffset ExpiresUtc => Lifetime.ExpiresUtc;
    public string PlanDigest { get; }
    public string PlannedEffects => $"Explicitly fence private selection control to format {Math.Max(3, Before.FormatVersion)}; preserve the exact current original; " +
        "restore the displayed inert V07a configuration; acknowledge only the actual verified commit. " +
        "Interrupted/unproven transactions require manual reconciliation. Remain AwaitingReadiness, NOT RUNNABLE.";

    internal RollbackRestorePlan(LocalSelectionEngine owner, SetupOperationRunner effects, long sequence,
        ControlDocument before, ConfigurationRestorePlan corePlan, int schema,
        ConfigurationSnapshotInspection snapshot, RestoreLifetime lifetime)
    {
        Owner = owner; Effects = effects; Sequence = sequence; Before = before; CorePlan = corePlan;
        CurrentSettingsSchemaVersion = schema; Snapshot = snapshot; Lifetime = lifetime;
        Candidate = new(before.Current!); Previous = before.Previous is null ? null : new(before.Previous);
        ControlDigest = Wire.Hash(Wire.Write(before));
        PlanDigest = Wire.Hash(Wire.Write(new
        {
            OperationId, before, CurrentSettingsSchemaVersion, SnapshotRelativePath,
            snapshot.SnapshotId, snapshot.FileDigest, snapshot.ManifestDigest, corePlan.Destination,
            corePlan.ProfileId, corePlan.ExpectedRevision, corePlan.CandidateDigest, ExpiresUtc, PlannedEffects
        }));
    }

    public RollbackRestoreApproval Approve(Guid operationId, long selectionRevision,
        string settingsRevision, string planDigest)
    {
        if (Interlocked.Exchange(ref approved, 1) != 0)
            throw new SelectionException(SelectionFailure.Conflict);
        Lifetime.Check();
        if (operationId != OperationId || selectionRevision != ExpectedSelectionRevision ||
            settingsRevision != ExpectedSettingsRevision || planDigest != PlanDigest)
            throw new SelectionException(SelectionFailure.Conflict);
        return new(this, CorePlan.Approve(CorePlan.SnapshotDigest, SettingsPath, ExpectedSettingsRevision));
    }
}

public sealed class RollbackRestoreApproval
{
    private readonly RollbackRestorePlan plan;
    private readonly ConfigurationRestoreApproval core;
    private int consumed;
    internal RollbackRestoreApproval(RollbackRestorePlan plan, ConfigurationRestoreApproval core)
    { this.plan = plan; this.core = core; }
    internal ConfigurationRestoreApproval Consume(RollbackRestorePlan expected)
    {
        if (Interlocked.Exchange(ref consumed, 1) != 0 || !ReferenceEquals(plan, expected))
            throw new SelectionException(SelectionFailure.Conflict);
        return core;
    }
}

public sealed class RollbackRestoreResult
{
    public Guid OperationId { get; }
    public RollbackSettingsProgress SettingsProgress { get; }
    public RollbackAcknowledgment Acknowledgment { get; }
    public SelectionReceipt? Selection { get; }
    public string? ResultingSettingsRevision { get; }
    public Guid? ProfileId { get; }
    public int? SettingsSchemaVersion { get; }
    public string? OriginalSnapshot { get; }
    public string? RetainedCleanupFile { get; }
    public SelectionFailure? Failure { get; }
    public RecoveryFailure? ConfigurationFailure { get; }
    public StagingFailure? PackageFailure { get; }
    public bool OwnershipFailed { get; }
    public bool IsRunnable => false;

    internal RollbackRestoreResult(Guid id, RollbackSettingsProgress settings, RollbackAcknowledgment acknowledgment,
        SelectionReceipt? selection, ConfigurationRestoreEvidence? evidence, string? cleanup,
        SelectionFailure? failure = null, RecoveryFailure? configurationFailure = null,
        StagingFailure? packageFailure = null)
    {
        OperationId = id; SettingsProgress = settings; Acknowledgment = acknowledgment; Selection = selection;
        ResultingSettingsRevision = evidence?.Revision; ProfileId = evidence?.ProfileId;
        SettingsSchemaVersion = evidence?.SchemaVersion; OriginalSnapshot = evidence?.OriginalSnapshot;
        RetainedCleanupFile = cleanup; Failure = failure; ConfigurationFailure = configurationFailure;
        PackageFailure = packageFailure;
    }
    private RollbackRestoreResult(RollbackRestoreResult prior)
    {
        OperationId = prior.OperationId; SettingsProgress = prior.SettingsProgress;
        Acknowledgment = prior.Acknowledgment; Selection = prior.Selection;
        ResultingSettingsRevision = prior.ResultingSettingsRevision; ProfileId = prior.ProfileId;
        SettingsSchemaVersion = prior.SettingsSchemaVersion; OriginalSnapshot = prior.OriginalSnapshot;
        RetainedCleanupFile = prior.RetainedCleanupFile; Failure = prior.Failure;
        ConfigurationFailure = prior.ConfigurationFailure; PackageFailure = prior.PackageFailure;
        OwnershipFailed = true;
    }
    internal RollbackRestoreResult WithOwnerFailure() => new(this);
}

internal sealed class RestoreLifetime
{
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(5);
    private readonly TimeProvider clock;
    private readonly long started;
    public DateTimeOffset ExpiresUtc { get; }
    internal RestoreLifetime(TimeProvider clock)
    {
        this.clock = clock; started = clock.GetTimestamp(); ExpiresUtc = clock.GetUtcNow() + Duration;
    }
    internal void Check()
    {
        if (clock.GetElapsedTime(started) >= Duration || clock.GetUtcNow() >= ExpiresUtc)
            throw new SelectionException(SelectionFailure.ConsentExpired);
    }
}

internal enum SelectionTransactionKind { Selection, ConfigurationRestore, ConfigurationAcknowledgment }
internal sealed record RestoreTransactionBinding(SnapshotBinding Snapshot, string ExpectedRevision,
    string CandidateDigest, string OriginalFileName);
internal sealed record SelectionJournalV3(int FormatVersion, Guid TransactionId, SelectionTransactionKind Kind,
    string PlanDigest, ControlDocument Before, ControlDocument Pending, ControlDocument After,
    RestoreTransactionBinding? Restore);
internal sealed record RestoreCommitDocument(int FormatVersion, Guid TransactionId, string PlanDigest,
    Guid ProfileId, string Revision, int SchemaVersion, string OriginalFileName, string OriginalRevision);
