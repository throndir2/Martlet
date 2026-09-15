using Martlet.Core.Settings;

namespace Martlet.Updates;

public enum SelectionFailure
{
    Uninitialized, Conflict, Busy, InvalidControl, RecoveryRequired, CapacityExceeded,
    InvalidSnapshot, IncompatibleSettings, NoVerifiedPrevious, AccessDenied, InsufficientDisk, Unavailable, Cancelled,
    ConfigurationRestoreRequired, ConfigurationRestoreReconciliationRequired, ConsentExpired, CleanupPending
}

public sealed class SelectionException : Exception
{
    public SelectionFailure Failure { get; }
    public Guid? TransactionId { get; }
    internal SelectionException(SelectionFailure failure, Guid? transactionId = null) : base(failure switch
    {
        SelectionFailure.Uninitialized => "Explicitly initialize this private control store with unqualified bootstrap facts first.",
        SelectionFailure.Conflict => "The approved sources, settings, generation or origin changed, or approval was used. Review a fresh selection plan.",
        SelectionFailure.Busy => "A cooperating owner holds this operation. Await its actual completion; do not remove its lock.",
        SelectionFailure.InvalidControl => "Control evidence is corrupt, unsupported or inconsistent. Preserve it for explicit manual recovery; do not reinitialize trust.",
        SelectionFailure.RecoveryRequired => "An interrupted transaction is retained. Recover its original selection before preparing again; no executable was activated.",
        SelectionFailure.CapacityExceeded => "The bounded control store is full. Preserve its history for an explicit future archival policy; no automatic purge is available.",
        SelectionFailure.InvalidSnapshot => "The configuration snapshot is invalid, changed, foreign or not fresh for the current settings store. Create and review a fresh V07a snapshot.",
        SelectionFailure.IncompatibleSettings => "The signed reader bounds do not support the required configuration. Keep the original version and configuration.",
        SelectionFailure.NoVerifiedPrevious => "No previously recorded signed selection is available. The unqualified bootstrap is not a rollback trust source.",
        SelectionFailure.AccessDenied => "Private local storage access was denied. Check ownership without elevation, permission changes or moving user data.",
        SelectionFailure.InsufficientDisk => "Private control storage ran out of space. Preserve transaction evidence and recover after resolving capacity.",
        SelectionFailure.Cancelled => "Selection was cancelled before atomic finalization. Preserve any retained transaction and recover it explicitly.",
        SelectionFailure.ConfigurationRestoreRequired => "The selected rollback requires its separate configuration restore. Review that exact restore before selecting another version.",
        SelectionFailure.ConfigurationRestoreReconciliationRequired => "Configuration restore was interrupted. Preserve settings, originals and all control evidence for explicit manual reconciliation. No restore or selection acknowledgment was resumed.",
        SelectionFailure.ConsentExpired => "The original rollback restore review lifetime expired. No new effect is authorized; preserve any pending transaction and review its actual outcome.",
        SelectionFailure.CleanupPending => "An exact owned restore temporary still requires cleanup. Keep this engine and retry its cleanup before another operation.",
        _ => "Private control IO failed. Preserve the control store and transaction evidence; resolve storage access and recover explicitly."
    }) { Failure = failure; TransactionId = transactionId; }
}

public enum SelectionKind { Activation, Rollback }
public enum SelectionStatus { BootstrapUnqualified, AwaitingReadiness, AwaitingConfigurationRestore, RecoveryRequired }
public enum SelectionOutcome { Unchanged, Committed, RecoveredOriginal }

// Not a launcher ticket. There is deliberately no API that upgrades this requirement to "passed".
public sealed class SelectionReceipt
{
    public const string ProofRequirements = "NOT RUNNABLE. A later exclusive activation coordinator must reverify " +
        "current publisher policy, exact retained package/files, control generation and current settings; perform " +
        "any V07a restore through the shared effect owner (revoking stale consent/keys), then prove compatible " +
        "migration and actual candidate readiness. This library neither executes nor authorizes execution.";
    public long Revision { get; }
    public SelectionStatus Status { get; }
    public SelectionOutcome Outcome { get; }
    public Guid? TransactionId { get; }
    public string BootstrapVersion { get; }
    public SelectedVersion? CurrentSelection { get; }
    public SelectedVersion? PreviousSelection { get; }
    public bool IsRunnable => false;
    public RollbackRestoreProgress ConfigurationRestoreProgress { get; }
    internal SelectionReceipt(ControlDocument state, SelectionOutcome outcome,
        RollbackRestoreProgress restoreProgress = RollbackRestoreProgress.None)
    {
        Revision = state.Revision; Outcome = outcome; TransactionId = state.Pending ?? state.LastTransaction;
        BootstrapVersion = state.Bootstrap.Version;
        CurrentSelection = state.Current is null ? null : new(state.Current);
        PreviousSelection = state.Previous is null ? null : new(state.Previous);
        ConfigurationRestoreProgress = restoreProgress == RollbackRestoreProgress.None &&
            state.Pending is null && state.Current?.RestoreRequired == true
                ? RollbackRestoreProgress.AwaitingConsent : restoreProgress;
        Status = state.Pending is not null ? SelectionStatus.RecoveryRequired :
            state.Current is null ? SelectionStatus.BootstrapUnqualified :
            state.Current.RestoreRequired ? SelectionStatus.AwaitingConfigurationRestore : SelectionStatus.AwaitingReadiness;
    }
}

public sealed class SelectedVersion
{
    public string Version { get; }
    public string StageName { get; }
    public string SignerId { get; }
    public string ArchiveSha256 { get; }
    public string ManifestSha256 { get; }
    public string ReceiptSha256 { get; }
    public string Rid => "win-x64";
    public int SettingsMinimumReader { get; }
    public int SettingsMaximumReader { get; }
    public string SnapshotRelativePath { get; }
    public string SnapshotFileDigest { get; }
    public Guid SnapshotId { get; }
    public Guid SnapshotProfileId { get; }
    public int SnapshotSchemaVersion { get; }
    public string SnapshotSourceRevision { get; }
    public string SnapshotManifestDigest { get; }
    public bool ConfigurationRestoreRequired { get; }
    internal SelectedVersion(SelectionEntry entry)
    {
        Version = entry.Version; StageName = entry.StageName; SignerId = entry.SignerId;
        ArchiveSha256 = entry.ArchiveSha256; ManifestSha256 = entry.ManifestSha256;
        ReceiptSha256 = entry.ReceiptSha256; SnapshotRelativePath = entry.Snapshot.RelativePath;
        SnapshotFileDigest = entry.Snapshot.FileDigest; ConfigurationRestoreRequired = entry.RestoreRequired;
        SettingsMinimumReader = entry.MinimumReader; SettingsMaximumReader = entry.MaximumReader;
        SnapshotId = entry.Snapshot.SnapshotId; SnapshotProfileId = entry.Snapshot.ProfileId;
        SnapshotSchemaVersion = entry.Snapshot.Schema; SnapshotSourceRevision = entry.Snapshot.SourceRevision;
        SnapshotManifestDigest = entry.Snapshot.ManifestDigest;
    }
}

public sealed class SelectionPlan
{
    internal object Owner { get; }
    internal long Sequence { get; }
    internal ControlDocument Before { get; }
    internal SelectionEntry Entry { get; }
    internal byte[] SnapshotBytes { get; }
    internal SnapshotBinding FreshSnapshot { get; }
    internal string SourcePath { get; }
    internal InstalledVersionFacts Origin { get; }
    internal Guid Profile { get; }
    internal string SettingsRevision { get; }
    internal SelectionEntry? RetainedPrevious { get; }
    private int approved;
    public Guid TransactionId { get; }
    public long ExpectedRevision => Before.Revision;
    public SelectionKind Kind { get; }
    public SelectedVersion Candidate { get; }
    public SelectedVersion? PreviousAfterSelection { get; }
    public ConfigurationSnapshotInspection Snapshot { get; }
    public string ConfigurationSnapshotPath => SourcePath;
    public string SettingsPath => Before.SettingsPath;
    public string StageDirectory => Path.Combine(Before.StagingRoot, Entry.StageName);
    public string PlanDigest { get; }
    public string PlannedEffects => Kind == SelectionKind.Activation
        ? "Retain exact original and configuration; select signed candidate; remain AwaitingReadiness. No installation or settings mutation."
        : "Retain exact original and current configuration; select the recorded previous candidate; remain AwaitingConfigurationRestore then AwaitingReadiness. No automatic restore.";

    internal SelectionPlan(object owner, long sequence, Guid id, SelectionKind kind, ControlDocument before,
        SelectionEntry entry, byte[] snapshot, SnapshotBinding freshSnapshot, string sourcePath,
        InstalledVersionFacts origin, ConfigurationSnapshotInspection inspection, Guid profile, string settingsRevision)
    {
        Owner = owner; Sequence = sequence; TransactionId = id; Kind = kind; Before = before; Entry = entry;
        SnapshotBytes = snapshot; FreshSnapshot = freshSnapshot; SourcePath = sourcePath; Origin = origin;
        Snapshot = inspection; Profile = profile; SettingsRevision = settingsRevision; Candidate = new(entry);
        RetainedPrevious = before.Current is null ? null :
            freshSnapshot.Schema >= before.Current.MinimumReader && freshSnapshot.Schema <= before.Current.MaximumReader
                ? before.Current with { Snapshot = freshSnapshot } : before.Current;
        PreviousAfterSelection = RetainedPrevious is null ? null : new(RetainedPrevious);
        PlanDigest = Wire.Hash(Wire.Write(new
        {
            id, kind, before, entry, sourcePath, origin, freshSnapshot, profile, settingsRevision,
            retainedPrevious = RetainedPrevious, effects = PlannedEffects
        }));
    }

    public SelectionApproval Approve(Guid transactionId, long currentRevision, string planDigest)
    {
        if (transactionId != TransactionId || currentRevision != ExpectedRevision || planDigest != PlanDigest ||
            Interlocked.Exchange(ref approved, 1) != 0)
            throw new SelectionException(SelectionFailure.Conflict);
        return new(this);
    }
}

public sealed class SelectionApproval
{
    private readonly SelectionPlan plan;
    private int consumed;
    internal SelectionApproval(SelectionPlan plan) => this.plan = plan;
    internal void Consume(SelectionPlan expected)
    {
        if (Interlocked.Exchange(ref consumed, 1) != 0 || !ReferenceEquals(plan, expected))
            throw new SelectionException(SelectionFailure.Conflict);
    }
}

internal sealed record SnapshotBinding(string RelativePath, Guid SnapshotId, Guid ProfileId,
    int Schema, string SourceRevision, string FileDigest, string ManifestDigest);
internal sealed record SelectionEntry(string StageName, string Version, string SignerId, string ArchiveSha256,
    string ManifestSha256, string ReceiptSha256, int MinimumReader, int MaximumReader,
    SnapshotBinding Snapshot, bool RestoreRequired);
internal sealed record ControlDocument(int FormatVersion, long Revision, string ControlRoot, string StagingRoot, string SettingsPath,
    Guid ProfileId, InstalledVersionFacts Bootstrap,
    SelectionEntry? Current, SelectionEntry? Previous, Guid? Pending, Guid? LastTransaction);
internal sealed record SelectionJournal(int FormatVersion, Guid TransactionId, string PlanDigest,
    ControlDocument Before, ControlDocument Pending, ControlDocument After);
