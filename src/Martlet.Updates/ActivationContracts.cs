namespace Martlet.Updates;

public enum ActivationFailure
{
    Uninitialized, Conflict, Busy, InvalidControl, RecoveryRequired, PublicationAmbiguous,
    NoSelectedVersion, ConfigurationRestoreRequired, IncompatibleSettings, InvalidSelectedVersion,
    ReadinessUnavailable, ReadinessFailed, ReadinessTimedOut, CapacityExceeded, AccessDenied,
    InsufficientDisk, Unavailable, Cancelled, PublishedOutcomeUncertain
}

public sealed class ActivationException : Exception
{
    public ActivationFailure Failure { get; }
    public Guid? TransactionId { get; }

    internal ActivationException(ActivationFailure failure, Guid? transactionId = null) : base(failure switch
    {
        ActivationFailure.Uninitialized =>
            "Explicitly initialize the private activation store before preparing a transition.",
        ActivationFailure.Conflict =>
            "The activation pointer, selected version, settings, approved plan or retained evidence changed. Review a fresh plan.",
        ActivationFailure.Busy =>
            "A cooperating activation, selection or staging owner is still active. Await its actual completion; do not remove its lock.",
        ActivationFailure.InvalidControl =>
            "Activation evidence is corrupt, unsupported or inconsistent. Preserve it for explicit reconciliation; do not guess a runnable version.",
        ActivationFailure.RecoveryRequired =>
            "A readiness transaction is pending. Recover the previously active pointer explicitly; readiness is never resumed from lost consent.",
        ActivationFailure.PublicationAmbiguous =>
            "Activation publication intent does not match the authoritative pointer. Preserve every byte for explicit manual reconciliation.",
        ActivationFailure.PublishedOutcomeUncertain =>
            "Pointer replacement returned before a later operation failed. Inspect the retained authoritative history; do not report cancellation or retry the approval.",
        ActivationFailure.NoSelectedVersion =>
            "No verified selected candidate is available for activation.",
        ActivationFailure.ConfigurationRestoreRequired =>
            "The selected rollback has not completed its required verified configuration restore and acknowledgment.",
        ActivationFailure.IncompatibleSettings =>
            "The selected executable cannot read the verified current settings schema. Keep the current active version.",
        ActivationFailure.InvalidSelectedVersion =>
            "The selected staged package no longer verifies under current trust or no longer matches its retained evidence.",
        ActivationFailure.ReadinessUnavailable =>
            "No bounded readiness probe is configured. No executable was run and no activation pointer changed.",
        ActivationFailure.ReadinessFailed =>
            "The bounded readiness probe did not accept the exact selected candidate. Recover the previous pointer explicitly.",
        ActivationFailure.ReadinessTimedOut =>
            "The bounded readiness probe exceeded its fixed local deadline. Recover the previous pointer explicitly.",
        ActivationFailure.CapacityExceeded =>
            "The bounded activation store is full or an activation document exceeds its limit. Preserve existing evidence for explicit archival policy.",
        ActivationFailure.AccessDenied =>
            "Private local activation storage access was denied. Check ownership without elevation or permission changes.",
        ActivationFailure.InsufficientDisk =>
            "Private activation storage ran out of space. Preserve the pointer and transaction evidence before resolving capacity.",
        ActivationFailure.Cancelled =>
            "Activation was cancelled before final pointer publication. Recover the previous pointer explicitly.",
        _ =>
            "Local activation IO failed. Preserve the activation, selection, staging and configuration evidence."
    })
    {
        Failure = failure;
        TransactionId = transactionId;
    }
}

public enum ActivationTransitionKind { Activation, Rollback }
public enum ActivationStatus { NoActiveVersion, PointerPublished, ReadinessPending }
public enum ActivationReadiness { MissingReadiness }
public enum ActivationOutcome { Unchanged, Initialized, Activated, RolledBack, RecoveredPrevious }

public sealed class ActivatedVersion
{
    public string Version { get; }
    public string StageName { get; }
    public string SignerId { get; }
    public string ArchiveSha256 { get; }
    public string ManifestSha256 { get; }
    public string ReceiptSha256 { get; }
    public string ExecutableRelativePath { get; }
    public long ExecutableBytes { get; }
    public string ExecutableSha256 { get; }
    public int SettingsMinimumReader { get; }
    public int SettingsMaximumReader { get; }
    public long SelectionRevision { get; }
    public Guid? SelectionTransactionId { get; }
    public string SettingsRevision { get; }
    public int SettingsSchemaVersion { get; }
    public Guid TransitionId { get; }
    public string ReadinessResultSha256 { get; }

    internal ActivatedVersion(ActivationEntry entry)
    {
        var target = entry.Target;
        Version = target.Version;
        StageName = target.StageName;
        SignerId = target.SignerId;
        ArchiveSha256 = target.ArchiveSha256;
        ManifestSha256 = target.ManifestSha256;
        ReceiptSha256 = target.ReceiptSha256;
        ExecutableRelativePath = target.ExecutableRelativePath;
        ExecutableBytes = target.ExecutableBytes;
        ExecutableSha256 = target.ExecutableSha256;
        SettingsMinimumReader = target.SettingsMinimumReader;
        SettingsMaximumReader = target.SettingsMaximumReader;
        SelectionRevision = target.SelectionRevision;
        SelectionTransactionId = target.SelectionTransactionId;
        SettingsRevision = target.SettingsRevision;
        SettingsSchemaVersion = target.SettingsSchemaVersion;
        TransitionId = entry.TransitionId;
        ReadinessResultSha256 = entry.ReadinessResultSha256;
    }
}

public sealed class ActivationReceipt
{
    public long Revision { get; }
    public ActivationStatus Status { get; }
    public ActivationOutcome Outcome { get; }
    public Guid? TransactionId { get; }
    public ActivatedVersion? Current { get; }
    public ActivatedVersion? Previous { get; }
    public ActivationReadiness Readiness => ActivationReadiness.MissingReadiness;
    public bool IsRunnable => false;

    internal ActivationReceipt(ActivationControlDocument state, ActivationOutcome outcome)
    {
        Revision = state.Revision;
        Status = state.Pending is not null ? ActivationStatus.ReadinessPending :
            state.Current is null ? ActivationStatus.NoActiveVersion : ActivationStatus.PointerPublished;
        Outcome = outcome;
        TransactionId = state.Pending ?? state.LastTransaction;
        Current = state.Current is null ? null : new(state.Current);
        Previous = state.Previous is null ? null : new(state.Previous);
    }
}

public sealed class ActivationPlan
{
    internal object Owner { get; }
    internal long Sequence { get; }
    internal ActivationControlDocument Before { get; }
    internal ActivationTargetBinding Target { get; }
    internal ActivationEntry Entry { get; }
    internal ActivationReadinessDocument PassedResult { get; }
    private int approved;

    public Guid TransactionId { get; } = Guid.NewGuid();
    public ActivationTransitionKind Kind { get; }
    public long ExpectedActivationRevision => Before.Revision;
    public long ExpectedSelectionRevision => Target.SelectionRevision;
    public string ExpectedSettingsRevision => Target.SettingsRevision;
    public int ExpectedSettingsSchemaVersion => Target.SettingsSchemaVersion;
    public string ReadinessProbeId { get; }
    public ActivatedVersion Candidate { get; }
    public ActivatedVersion? PreviousAfterActivation { get; }
    public string PlanDigest { get; }
    public string PlannedEffects => Kind == ActivationTransitionKind.Activation
        ? "Publish a pending non-runnable owned pointer, run only the configured bounded injected readiness probe, " +
          "then atomically publish this exact verified selected version while retaining one verified previous version."
        : "Require the recorded configuration restore, publish a pending non-runnable owned pointer, run only the " +
          "configured bounded injected readiness probe, then atomically swap to the one retained verified previous version.";

    internal ActivationPlan(object owner, long sequence, ActivationTransitionKind kind,
        ActivationControlDocument before, ActivationTargetBinding target, string readinessProbeId)
    {
        Owner = owner;
        Sequence = sequence;
        Kind = kind;
        Before = before;
        Target = target;
        ReadinessProbeId = readinessProbeId;
        var targetDigest = Wire.Hash(Wire.Write(target));
        PlanDigest = Wire.Hash(Wire.Write(new
        {
            TransactionId,
            kind,
            before,
            target,
            targetDigest,
            readinessProbeId,
            effects = PlannedEffects
        }));
        PassedResult = new(1, TransactionId, PlanDigest, readinessProbeId, kind, targetDigest,
            ActivationReadinessOutcome.Passed);
        Entry = new(target, TransactionId, Wire.Hash(Wire.Write(PassedResult)));
        Candidate = new(Entry);
        PreviousAfterActivation = before.Current is null ? null : new(before.Current);
    }

    public ActivationApproval Approve(Guid transactionId, long activationRevision,
        long selectionRevision, string settingsRevision, string planDigest)
    {
        if (transactionId != TransactionId || activationRevision != ExpectedActivationRevision ||
            selectionRevision != ExpectedSelectionRevision || settingsRevision != ExpectedSettingsRevision ||
            planDigest != PlanDigest || Interlocked.Exchange(ref approved, 1) != 0)
            throw new ActivationException(ActivationFailure.Conflict);
        return new(this);
    }
}

public sealed class ActivationApproval
{
    private readonly ActivationPlan plan;
    private int consumed;

    internal ActivationApproval(ActivationPlan plan) => this.plan = plan;

    internal void Consume(ActivationPlan expected)
    {
        if (Interlocked.Exchange(ref consumed, 1) != 0 || !ReferenceEquals(plan, expected))
            throw new ActivationException(ActivationFailure.Conflict);
    }
}

internal sealed record ActivationTargetBinding(
    string StageName,
    string Version,
    string SignerId,
    string ArchiveSha256,
    string ManifestSha256,
    string ReceiptSha256,
    int SettingsMinimumReader,
    int SettingsMaximumReader,
    string ExecutableRelativePath,
    long ExecutableBytes,
    string ExecutableSha256,
    long SelectionRevision,
    Guid? SelectionTransactionId,
    string SettingsRevision,
    int SettingsSchemaVersion);

internal sealed record ActivationEntry(
    ActivationTargetBinding Target,
    Guid TransitionId,
    string ReadinessResultSha256);

internal sealed record ActivationControlDocument(
    int FormatVersion,
    long Revision,
    string ActivationRoot,
    string SelectionRoot,
    string StagingRoot,
    string SettingsPath,
    Guid ProfileId,
    InstalledVersionFacts Bootstrap,
    ActivationEntry? Current,
    ActivationEntry? Previous,
    Guid? Pending,
    Guid? LastTransaction);

internal sealed record ActivationJournal(
    int FormatVersion,
    Guid TransactionId,
    ActivationTransitionKind Kind,
    string PlanDigest,
    string TargetDigest,
    ActivationControlDocument Before,
    ActivationControlDocument Pending,
    ActivationControlDocument After);

internal sealed record ActivationReadinessIntentDocument(
    int FormatVersion,
    Guid TransactionId,
    string PlanDigest,
    string ProbeId,
    string TargetDigest,
    DateTimeOffset StartedUtc,
    DateTimeOffset ExpiresUtc);

internal enum ActivationReadinessOutcome { Passed, Failed, TimedOut }

internal sealed record ActivationReadinessDocument(
    int FormatVersion,
    Guid TransactionId,
    string PlanDigest,
    string ProbeId,
    ActivationTransitionKind Kind,
    string TargetDigest,
    ActivationReadinessOutcome Outcome);

internal enum ActivationProbeOutcome { Ready, NotReady }

internal sealed class ActivationReadinessRequest
{
    internal Guid TransactionId { get; }
    internal ActivationTransitionKind Kind { get; }
    internal string StageDirectory { get; }
    internal string ExecutablePath { get; }
    internal string Version { get; }
    internal string ExecutableSha256 { get; }
    internal string ArchiveSha256 { get; }
    internal string ManifestSha256 { get; }
    internal long SelectionRevision { get; }
    internal string SettingsRevision { get; }
    internal int SettingsSchemaVersion { get; }
    internal DateTimeOffset ExpiresUtc { get; }

    internal ActivationReadinessRequest(ActivationPlan plan, string stageDirectory,
        string executablePath, DateTimeOffset expiresUtc)
    {
        TransactionId = plan.TransactionId;
        Kind = plan.Kind;
        StageDirectory = stageDirectory;
        ExecutablePath = executablePath;
        Version = plan.Target.Version;
        ExecutableSha256 = plan.Target.ExecutableSha256;
        ArchiveSha256 = plan.Target.ArchiveSha256;
        ManifestSha256 = plan.Target.ManifestSha256;
        SelectionRevision = plan.ExpectedSelectionRevision;
        SettingsRevision = plan.ExpectedSettingsRevision;
        SettingsSchemaVersion = plan.ExpectedSettingsSchemaVersion;
        ExpiresUtc = expiresUtc;
    }
}
