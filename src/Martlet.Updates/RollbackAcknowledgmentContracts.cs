using Martlet.Core.Settings;

namespace Martlet.Updates;

public enum RollbackHistoricalCommitVerification { NotVerified, VerifiedRecordedCommit }
public enum RollbackCurrentSettingsVerification { NotVerified, VerifiedPresentState }

public sealed class RollbackAcknowledgmentPlan
{
    internal LocalSelectionEngine Owner { get; }
    internal SetupOperationRunner Effects { get; }
    internal long Sequence { get; }
    internal ControlDocument Before { get; }
    internal RestoreAcknowledgmentBinding Binding { get; }
    internal RestoreLifetime Lifetime { get; }
    private int approved;

    public Guid OperationId { get; } = Guid.NewGuid();
    public Guid InterruptedRestoreTransactionId => Binding.RestoreTransactionId;
    public Guid RollbackTransactionId => Before.LastTransaction!.Value;
    public long ExpectedSelectionRevision => Before.Revision;
    public long ProposedSelectionRevision { get; }
    public long SkippedRestoreTerminalRevision => Binding.RestoreTerminalRevision;
    public string ControlDigest => Binding.PendingDigest;
    public string RestoreJournalDigest => Binding.JournalDigest;
    public string RestorePlanDigest => Binding.RestorePlanDigest;
    public string RecordedCommitDigest => Binding.CommitDigest;
    public string OriginalSnapshotPath { get; }
    public string OriginalRevision => Binding.OriginalRevision;
    public int OriginalSchemaVersion => Binding.OriginalSchema;
    public string SettingsPath => Before.SettingsPath;
    public Guid ProfileId => Before.ProfileId;
    public string ExpectedSettingsRevision => Binding.SettingsRevision;
    public int SettingsSchemaVersion => Binding.SettingsSchema;
    public string CurrentJson { get; }
    public SelectedVersion Candidate { get; }
    public SelectedVersion? Previous { get; }
    public InstalledVersionFacts Bootstrap => Before.Bootstrap;
    public ConfigurationSnapshotInspection Snapshot { get; }
    public string SnapshotRelativePath => Candidate.SnapshotRelativePath;
    public string TrustPolicyDigest => Binding.TrustPolicyDigest;
    public string HistoryDigest => Binding.HistoryDigest;
    public DateTimeOffset ExpiresUtc => Lifetime.ExpiresUtc;
    public string PlanDigest { get; }
    public string Scope => ConfigurationSnapshot.Scope;
    public string PlannedEffects => "Settings were already restored and their commit was recorded. This NEW approval " +
        "acknowledges their verified present state WITHOUT performing another restore. Preserve the earlier interrupted " +
        "operation, original and all prior history. Publish a new format-4 acknowledgment; older readers refuse its " +
        "published protocol. No settings writes or new original. AwaitingReadiness only, NOT RUNNABLE.";

    internal RollbackAcknowledgmentPlan(LocalSelectionEngine owner, SetupOperationRunner effects, long sequence,
        ControlDocument before, RestoreAcknowledgmentBinding binding, long revision, string originalPath,
        string currentJson, ConfigurationSnapshotInspection snapshot, RestoreLifetime lifetime)
    {
        Owner = owner; Effects = effects; Sequence = sequence; Before = before; Binding = binding;
        ProposedSelectionRevision = revision; OriginalSnapshotPath = originalPath; CurrentJson = currentJson;
        Snapshot = snapshot; Lifetime = lifetime; Candidate = new(before.Current!);
        Previous = before.Previous is null ? null : new(before.Previous);
        PlanDigest = Wire.Hash(Wire.Write(new
        {
            OperationId, before, binding, ProposedSelectionRevision, OriginalSnapshotPath, ExpiresUtc, PlannedEffects
        }));
    }

    public RollbackAcknowledgmentApproval Approve(Guid operationId, long selectionRevision,
        string settingsRevision, string planDigest)
    {
        if (Interlocked.Exchange(ref approved, 1) != 0) throw new SelectionException(SelectionFailure.Conflict);
        Lifetime.Check();
        if (operationId != OperationId || selectionRevision != ExpectedSelectionRevision ||
            settingsRevision != ExpectedSettingsRevision || planDigest != PlanDigest)
            throw new SelectionException(SelectionFailure.Conflict);
        return new(this);
    }
}

public sealed class RollbackAcknowledgmentApproval
{
    private readonly RollbackAcknowledgmentPlan plan;
    private int consumed;
    internal RollbackAcknowledgmentApproval(RollbackAcknowledgmentPlan plan) => this.plan = plan;
    internal void Consume(RollbackAcknowledgmentPlan expected)
    {
        if (Interlocked.Exchange(ref consumed, 1) != 0 || !ReferenceEquals(plan, expected))
            throw new SelectionException(SelectionFailure.Conflict);
    }
}

public sealed class RollbackAcknowledgmentResult
{
    public Guid OperationId { get; }
    public Guid InterruptedRestoreTransactionId { get; }
    public RollbackHistoricalCommitVerification HistoricalCommitVerification { get; }
    public RollbackCurrentSettingsVerification CurrentSettingsVerification { get; }
    public RollbackAcknowledgment Acknowledgment { get; }
    public SelectionReceipt? Selection { get; }
    public string? ObservedSettingsRevision { get; }
    public Guid? ProfileId { get; }
    public int? SettingsSchemaVersion { get; }
    public string? OriginalSnapshotPath { get; }
    public SelectionFailure? Failure { get; }
    public RecoveryFailure? ConfigurationFailure { get; }
    public StagingFailure? PackageFailure { get; }
    public bool OwnershipFailed { get; }
    public bool IsRunnable => false;

    internal RollbackAcknowledgmentResult(RollbackAcknowledgmentPlan plan, bool historical,
        ConfigurationCurrentInspection? current, RollbackAcknowledgment acknowledgment, SelectionReceipt? selection,
        SelectionFailure? failure = null, RecoveryFailure? configurationFailure = null,
        StagingFailure? packageFailure = null, bool ownershipFailed = false)
    {
        OperationId = plan.OperationId; InterruptedRestoreTransactionId = plan.InterruptedRestoreTransactionId;
        HistoricalCommitVerification = historical ? RollbackHistoricalCommitVerification.VerifiedRecordedCommit :
            RollbackHistoricalCommitVerification.NotVerified;
        CurrentSettingsVerification = current is null ? RollbackCurrentSettingsVerification.NotVerified :
            RollbackCurrentSettingsVerification.VerifiedPresentState;
        Acknowledgment = acknowledgment; Selection = selection;
        ObservedSettingsRevision = current?.Revision; ProfileId = current?.ProfileId;
        SettingsSchemaVersion = current?.SchemaVersion; OriginalSnapshotPath = historical ? plan.OriginalSnapshotPath : null;
        Failure = failure; ConfigurationFailure = configurationFailure; PackageFailure = packageFailure;
        OwnershipFailed = ownershipFailed;
    }

    private RollbackAcknowledgmentResult(RollbackAcknowledgmentResult prior)
    {
        OperationId = prior.OperationId; InterruptedRestoreTransactionId = prior.InterruptedRestoreTransactionId;
        HistoricalCommitVerification = prior.HistoricalCommitVerification;
        CurrentSettingsVerification = prior.CurrentSettingsVerification; Acknowledgment = prior.Acknowledgment;
        Selection = prior.Selection; ObservedSettingsRevision = prior.ObservedSettingsRevision;
        ProfileId = prior.ProfileId; SettingsSchemaVersion = prior.SettingsSchemaVersion;
        OriginalSnapshotPath = prior.OriginalSnapshotPath; Failure = prior.Failure;
        ConfigurationFailure = prior.ConfigurationFailure; PackageFailure = prior.PackageFailure;
        OwnershipFailed = true;
    }

    internal RollbackAcknowledgmentResult WithOwnerFailure() => new(this);
}

internal sealed record RestoreAcknowledgmentBinding(Guid RestoreTransactionId, string JournalDigest,
    string RestorePlanDigest, string CommitDigest, string PendingDigest, long RestoreTerminalRevision,
    SnapshotBinding Snapshot, string OriginalFileName, string OriginalRevision, int OriginalSchema,
    string SettingsRevision, int SettingsSchema, string TrustPolicyDigest, string HistoryDigest);

internal sealed record SelectionJournalV4(int FormatVersion, Guid TransactionId, SelectionTransactionKind Kind,
    string PlanDigest, ControlDocument Before, ControlDocument? Pending, ControlDocument After,
    RestoreTransactionBinding? Restore, RestoreAcknowledgmentBinding? Acknowledgment);
