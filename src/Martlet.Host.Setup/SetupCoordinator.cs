using System.Collections.Immutable;

namespace Martlet.Host.Setup;

public enum SetupObservationState { Pending, Satisfied, Conflict, Unknown }
public enum SetupExecutionOutcome { Completed, Failed, Refused, Canceled }
public enum SetupApprovalDecision { No, Approve }
public enum SetupPreviewState { Proposed, Approved, InProgress, Interrupted, Blocked, Completed, ReviewRecorded }
public enum SetupRunState { Refused, Blocked, Interrupted, Completed }

public sealed class SetupStepObservation
{
    public SetupObservationState State { get; }
    public string Code { get; }
    public string? EvidenceFingerprint { get; }

    public SetupStepObservation(SetupObservationState state, string code, string? evidenceFingerprint = null)
    {
        SetupGuard.Require(Enum.IsDefined(state), SetupFailure.ExternalStateUnknown);
        SetupGuard.Identifier(code, SetupFailure.ExternalStateUnknown);
        if (evidenceFingerprint is not null)
            SetupGuard.Fingerprint(evidenceFingerprint, SetupFailure.ExternalStateUnknown);
        SetupGuard.Require(state is SetupObservationState.Satisfied or SetupObservationState.Conflict
            ? evidenceFingerprint is not null
            : evidenceFingerprint is null, SetupFailure.ExternalStateUnknown);
        State = state;
        Code = code;
        EvidenceFingerprint = evidenceFingerprint;
    }
}

public sealed record SetupExecutionResult(SetupExecutionOutcome Outcome, string Code)
{
    public static SetupExecutionResult Completed(string code = "completed") => new(SetupExecutionOutcome.Completed, code);
    public static SetupExecutionResult Failed(string code = "failed") => new(SetupExecutionOutcome.Failed, code);
    public static SetupExecutionResult Refused(string code = "refused") => new(SetupExecutionOutcome.Refused, code);
    public static SetupExecutionResult Canceled(string code = "canceled") => new(SetupExecutionOutcome.Canceled, code);

    internal void Validate()
    {
        SetupGuard.Require(Enum.IsDefined(Outcome), SetupFailure.StepFailed);
        SetupGuard.Identifier(Code, SetupFailure.StepFailed);
    }
}

public interface ISetupStateProbe
{
    ValueTask<SetupStepObservation> ObserveAsync(SetupStep step, CancellationToken cancellationToken);
}

public interface ISetupStepExecutor
{
    ValueTask<SetupExecutionResult> ExecuteAsync(SetupCommand command, CancellationToken cancellationToken);
}

public sealed class UnknownSetupStateProbe : ISetupStateProbe
{
    public ValueTask<SetupStepObservation> ObserveAsync(SetupStep step, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new SetupStepObservation(SetupObservationState.Unknown, "probe-not-configured"));
    }
}

public sealed class RefusingSetupStepExecutor : ISetupStepExecutor
{
    public ValueTask<SetupExecutionResult> ExecuteAsync(SetupCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(SetupExecutionResult.Refused("production-default"));
    }
}

public sealed record SetupStepPreview(
    string Id,
    SetupStepExecution Execution,
    SetupPrivilege Privilege,
    SetupObservationState Observation,
    string ObservationCode,
    bool JournaledComplete,
    bool RollbackAvailable)
{
    public bool JournaledReview { get; init; }
}

public sealed class SetupPreview
{
    private readonly SetupPlan plan;
    private readonly string? expectedJournalVersion;
    private readonly bool reviewOnly;

    public string PlanFingerprint => plan.Fingerprint;
    public string ConfigurationFingerprint => plan.ConfigurationFingerprint;
    public string HostFactsFingerprint => plan.HostFactsFingerprint;
    public string ArtifactFactsFingerprint => plan.ArtifactFactsFingerprint;
    public string JournalPath { get; }
    public string? JournalVersion => expectedJournalVersion;
    public long? JournalRevision { get; }
    public SetupPreviewState State { get; }
    public bool CanApprove { get; }
    public bool RequiresJournalRollForward { get; }
    public SetupFailure? Failure { get; }
    public SetupRemedy? Remedy => Failure is { } failure ? SetupRemedies.For(failure) : null;
    public ImmutableArray<SetupConsentScope> RequiredConsentScopes =>
        reviewOnly ? [SetupConsentScope.LocalJournal] : plan.RequiredConsentScopes;
    public bool IsLocalReview => reviewOnly;
    public bool ExecutionAuthorized => false;
    public ImmutableArray<SetupStepPreview> Steps { get; }
    public bool RollbackAvailable => false;

    internal SetupPreview(
        SetupPlan plan,
        string journalPath,
        string? expectedJournalVersion,
        long? journalRevision,
        SetupPreviewState state,
        bool canApprove,
        bool requiresJournalRollForward,
        SetupFailure? failure,
        ImmutableArray<SetupStepPreview> steps,
        bool reviewOnly = false)
    {
        this.plan = plan;
        this.expectedJournalVersion = expectedJournalVersion;
        this.reviewOnly = reviewOnly;
        JournalPath = journalPath;
        JournalRevision = journalRevision;
        State = state;
        CanApprove = canApprove;
        RequiresJournalRollForward = requiresJournalRollForward;
        Failure = failure;
        Steps = steps;
    }

    public SetupApproval Approve(
        SetupApprovalDecision decision = SetupApprovalDecision.No,
        IEnumerable<SetupConsentScope>? approvedScopes = null,
        SetupPrivilege maximumPrivilege = SetupPrivilege.None)
    {
        if (!Enum.IsDefined(decision) || !Enum.IsDefined(maximumPrivilege))
            return SetupApproval.Refused(plan, JournalPath, expectedJournalVersion,
                RequiresJournalRollForward, SetupFailure.ConsentRequired);
        if (decision != SetupApprovalDecision.Approve)
            return SetupApproval.Refused(plan, JournalPath, expectedJournalVersion,
                RequiresJournalRollForward, SetupFailure.ConsentRequired);
        if (!CanApprove)
            return SetupApproval.Refused(plan, JournalPath, expectedJournalVersion,
                RequiresJournalRollForward, Failure ?? SetupFailure.PlanBlocked);
        var scopes = (approvedScopes ?? []).Order().ToImmutableArray();
        if (scopes.Any(scope => !Enum.IsDefined(scope)) ||
            scopes.Distinct().Count() != scopes.Length ||
            !scopes.SequenceEqual(RequiredConsentScopes) ||
            reviewOnly && maximumPrivilege != SetupPrivilege.None)
            return SetupApproval.Refused(plan, JournalPath, expectedJournalVersion,
                RequiresJournalRollForward, SetupFailure.ConsentScopeMismatch);
        return SetupApproval.Allowed(plan, JournalPath, expectedJournalVersion,
            RequiresJournalRollForward, scopes, maximumPrivilege, reviewOnly);
    }
}

public sealed class SetupApproval
{
    private int consumed;
    internal string PlanFingerprint { get; }
    internal string ConfigurationFingerprint { get; }
    internal string HostFactsFingerprint { get; }
    internal string ArtifactFactsFingerprint { get; }
    internal string JournalPath { get; }
    internal bool RequiresJournalRollForward { get; }
    internal string? ExpectedJournalVersion { get; }
    internal bool ReviewOnly { get; }
    public bool IsApproved { get; }
    public SetupFailure? Failure { get; }
    public SetupRemedy? Remedy => Failure is { } failure ? SetupRemedies.For(failure) : null;
    public ImmutableArray<SetupConsentScope> ApprovedScopes { get; }
    public SetupPrivilege MaximumPrivilege { get; }

    private SetupApproval(
        SetupPlan plan,
        string journalPath,
        string? expectedJournalVersion,
        bool requiresJournalRollForward,
        bool approved,
        SetupFailure? failure,
        ImmutableArray<SetupConsentScope> scopes,
        SetupPrivilege maximumPrivilege,
        bool reviewOnly = false)
    {
        PlanFingerprint = plan.Fingerprint;
        ConfigurationFingerprint = plan.ConfigurationFingerprint;
        HostFactsFingerprint = plan.HostFactsFingerprint;
        ArtifactFactsFingerprint = plan.ArtifactFactsFingerprint;
        JournalPath = journalPath;
        RequiresJournalRollForward = requiresJournalRollForward;
        ExpectedJournalVersion = expectedJournalVersion;
        IsApproved = approved;
        Failure = failure;
        ApprovedScopes = scopes;
        MaximumPrivilege = maximumPrivilege;
        ReviewOnly = reviewOnly;
    }

    internal static SetupApproval Refused(
        SetupPlan plan,
        string journalPath,
        string? version,
        bool requiresJournalRollForward,
        SetupFailure failure) =>
        new(plan, journalPath, version, requiresJournalRollForward,
            false, failure, [], SetupPrivilege.None);

    internal static SetupApproval Allowed(
        SetupPlan plan,
        string journalPath,
        string? version,
        bool requiresJournalRollForward,
        ImmutableArray<SetupConsentScope> scopes,
        SetupPrivilege maximumPrivilege,
        bool reviewOnly = false) =>
        new(plan, journalPath, version, requiresJournalRollForward,
            true, null, scopes, maximumPrivilege, reviewOnly);

    internal bool TryConsume() => Interlocked.Exchange(ref consumed, 1) == 0;
}

public sealed record SetupRunResult(
    SetupRunState State,
    SetupFailure? Failure,
    string? StepId,
    ImmutableArray<string> ExecutedSteps,
    ImmutableArray<string> ReconciledSteps,
    ImmutableArray<string> CompletedSteps,
    bool RollbackAvailable,
    bool RollbackPerformed)
{
    public SetupRemedy? Remedy => Failure is { } failure ? SetupRemedies.For(failure) : null;
}

public sealed record SetupReviewResult(
    bool ReviewRecorded,
    SetupFailure? Failure,
    ImmutableArray<string> ReviewedSteps)
{
    public bool ExecutionAuthorized => false;
    public bool ExternalActionsCompleted => false;
    public SetupRemedy? Remedy => Failure is { } failure ? SetupRemedies.For(failure) : null;
}

public sealed class SetupCoordinator
{
    private readonly ISetupFileSystem fileSystem;
    private readonly ISetupStateProbe stateProbe;
    private readonly ISetupStepExecutor executor;
    private readonly TimeProvider clock;

    public SetupCoordinator(
        ISetupFileSystem fileSystem,
        TimeProvider? clock = null)
        : this(fileSystem, new UnknownSetupStateProbe(), new RefusingSetupStepExecutor(), clock) { }

    internal SetupCoordinator(
        ISetupFileSystem fileSystem,
        ISetupStateProbe? stateProbe,
        ISetupStepExecutor? executor = null,
        TimeProvider? clock = null)
    {
        this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        this.stateProbe = stateProbe ?? new UnknownSetupStateProbe();
        this.executor = executor ?? new RefusingSetupStepExecutor();
        this.clock = clock ?? TimeProvider.System;
        SetupGuard.Text(fileSystem.JournalPath, 1024, SetupFailure.InvalidConfiguration);
    }

    public async ValueTask<SetupPreview> PreviewReviewAsync(
        SetupPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = await fileSystem.ReadAsync(SetupJournalCodec.MaximumBytes, cancellationToken).ConfigureAwait(false);
        var journal = snapshot is null ? null : SetupJournalCodec.Read(snapshot.Content);
        if (journal is not null && journal.Purpose != SetupJournalPurpose.LocalReview)
            throw new SetupException(SetupFailure.JournalPurposeMismatch);
        var rollForward = journal is not null && ValidateJournalForPlan(journal, plan);
        var steps = plan.Steps.Select(step => new SetupStepPreview(
            step.Id, step.Execution, step.Privilege, SetupObservationState.Unknown,
            "not-observed", JournaledComplete: false, RollbackAvailable: false)
        {
            JournaledReview = !rollForward &&
                journal?.Steps.Single(s => s.Id == step.Id).Status == SetupJournalStepStatus.Reviewed
        }).ToImmutableArray();
        var now = clock.GetUtcNow();
        var stale = now < plan.CreatedAtUtc || now >= plan.ExpiresAtUtc;
        var recorded = !rollForward && journal?.State == SetupJournalState.ReviewRecorded;
        var state = stale ? SetupPreviewState.Blocked : recorded ? SetupPreviewState.ReviewRecorded :
            journal is not null ? SetupPreviewState.Interrupted : SetupPreviewState.Proposed;
        return new(plan, fileSystem.JournalPath, snapshot?.Version, journal?.Revision, state,
            !stale && !recorded, rollForward, stale ? SetupFailure.PlanStale : null, steps, reviewOnly: true);
    }

    public async ValueTask<SetupReviewResult> RecordReviewAsync(
        SetupPlan plan,
        SetupApproval approval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(approval);
        if (!approval.IsApproved)
            return new(false, approval.Failure ?? SetupFailure.ConsentRequired, []);
        if (!approval.ReviewOnly || approval.MaximumPrivilege != SetupPrivilege.None ||
            !approval.ApprovedScopes.SequenceEqual([SetupConsentScope.LocalJournal]))
            return new(false, SetupFailure.ConsentScopeMismatch, []);
        if (!approval.TryConsume()) return new(false, SetupFailure.ConsentConsumed, []);
        if (ApprovalBindingFailure(plan, approval, fileSystem.JournalPath) is { } failure)
            return new(false, failure, []);
        var now = clock.GetUtcNow();
        if (now < plan.CreatedAtUtc || now >= plan.ExpiresAtUtc)
            return new(false, SetupFailure.PlanStale, []);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = await fileSystem.ReadAsync(SetupJournalCodec.MaximumBytes, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(snapshot?.Version, approval.ExpectedJournalVersion, StringComparison.Ordinal))
            return new(false, SetupFailure.JournalConcurrentChange, []);
        var document = snapshot is null
            ? SetupJournalDocument.Create(plan, approval, now) with { Purpose = SetupJournalPurpose.LocalReview }
            : SetupJournalCodec.Read(snapshot.Content);
        if (document.Purpose != SetupJournalPurpose.LocalReview)
            throw new SetupException(SetupFailure.JournalPurposeMismatch);
        var rollForward = snapshot is not null && ValidateJournalForPlan(document, plan);
        if (rollForward != approval.RequiresJournalRollForward)
            return new(false, SetupFailure.JournalConcurrentChange, []);
        var version = snapshot?.Version;
        if (rollForward)
        {
            if (document.Supersessions.Length >= 16) throw new SetupException(SetupFailure.JournalTooLarge);
            document = document with
            {
                PlanFingerprint = plan.Fingerprint,
                HostFactsFingerprint = plan.HostFactsFingerprint,
                State = SetupJournalState.Approved,
                Steps = document.Steps.Select(s => s with
                {
                    Status = SetupJournalStepStatus.Pending, ReviewFingerprint = null, ReviewedAtUtc = null
                }).ToArray(),
                Supersessions = [.. document.Supersessions, new SetupJournalSupersession
                {
                    PlanFingerprint = document.PlanFingerprint,
                    HostFactsFingerprint = document.HostFactsFingerprint,
                    JournalRevision = document.Revision,
                    AcceptedAtUtc = now
                }]
            };
        }
        if (snapshot is null || rollForward)
            (document, version) = await PersistAsync(document, version, cancellationToken).ConfigureAwait(false);
        document = document with { State = SetupJournalState.Running };
        (document, version) = await PersistAsync(document, version, cancellationToken).ConfigureAwait(false);
        var reviewed = ImmutableArray.CreateBuilder<string>();
        for (var index = 0; index < document.Steps.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            now = clock.GetUtcNow();
            if (now < plan.CreatedAtUtc || now >= plan.ExpiresAtUtc)
                return new(false, SetupFailure.PlanStale, reviewed.ToImmutable());
            var step = document.Steps[index];
            if (step.Status != SetupJournalStepStatus.Reviewed)
            {
                document = ReplaceStep(document, index, step with
                {
                    Status = SetupJournalStepStatus.Reviewed,
                    ReviewFingerprint = FingerprintBuilder.Create(plan.Fingerprint, step.Id),
                    ReviewedAtUtc = now
                });
                (document, version) = await PersistAsync(document, version, cancellationToken).ConfigureAwait(false);
            }
            reviewed.Add(step.Id);
        }
        cancellationToken.ThrowIfCancellationRequested();
        now = clock.GetUtcNow();
        if (now < plan.CreatedAtUtc || now >= plan.ExpiresAtUtc)
            return new(false, SetupFailure.PlanStale, reviewed.ToImmutable());
        document = document with { State = SetupJournalState.ReviewRecorded };
        await PersistAsync(document, version, cancellationToken).ConfigureAwait(false);
        return new(true, null, reviewed.ToImmutable());
    }

    // Preserved H05a reconciliation seam; no supported production composition exists.
    internal async ValueTask<SetupPreview> PreviewAsync(
        SetupPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = await fileSystem.ReadAsync(SetupJournalCodec.MaximumBytes, cancellationToken).ConfigureAwait(false);
        var journal = snapshot is null ? null : SetupJournalCodec.Read(snapshot.Content);
        if (journal is not null && journal.Purpose != SetupJournalPurpose.UnqualifiedExecution)
            throw new SetupException(SetupFailure.JournalPurposeMismatch);
        var requiresRollForward = journal is not null && ValidateJournalForPlan(journal, plan);

        var stepPreviews = ImmutableArray.CreateBuilder<SetupStepPreview>(plan.Steps.Length);
        SetupFailure? observationFailure = null;
        foreach (var step in plan.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = step.Execution == SetupStepExecution.Deferred
                ? new SetupStepObservation(SetupObservationState.Unknown, "step-deferred")
                : await ObserveAsync(step, cancellationToken).ConfigureAwait(false);
            var journalStep = journal?.Steps.Single(s => s.Id == step.Id);
            var journaledComplete = journalStep?.Status == SetupJournalStepStatus.Completed;
            if (journaledComplete && observation.State != SetupObservationState.Satisfied)
                observationFailure ??= SetupFailure.ExternalStateConflict;
            else if (observation.State == SetupObservationState.Conflict)
                observationFailure ??= SetupFailure.ExternalStateConflict;
            else if (observation.State == SetupObservationState.Unknown)
                observationFailure ??= SetupFailure.ExternalStateUnknown;
            else if (journaledComplete &&
                !string.Equals(journalStep!.ObservationFingerprint, observation.EvidenceFingerprint, StringComparison.Ordinal))
                observationFailure ??= SetupFailure.ExternalStateConflict;
            stepPreviews.Add(new(step.Id, step.Execution, step.Privilege, observation.State,
                observation.Code, journaledComplete, false));
        }

        var now = clock.GetUtcNow();
        var stale = now < plan.CreatedAtUtc || now >= plan.ExpiresAtUtc;
        var complete = observationFailure is null &&
            journal?.State == SetupJournalState.Completed &&
            stepPreviews.All(step => step.JournaledComplete && step.Observation == SetupObservationState.Satisfied);
        SetupFailure? failure = stale ? SetupFailure.PlanStale :
            plan.Disposition == SetupPlanDisposition.Blocked ? SetupFailure.PlanBlocked :
            observationFailure;
        var state = complete ? SetupPreviewState.Completed : journal?.State switch
        {
            SetupJournalState.Approved => SetupPreviewState.Approved,
            SetupJournalState.Running => SetupPreviewState.Interrupted,
            SetupJournalState.Interrupted => SetupPreviewState.Interrupted,
            SetupJournalState.Blocked => SetupPreviewState.Blocked,
            SetupJournalState.Completed => SetupPreviewState.Blocked,
            _ => SetupPreviewState.Proposed
        };
        return new(plan, fileSystem.JournalPath, snapshot?.Version, journal?.Revision, state,
            canApprove: !complete && failure is null, requiresRollForward,
            failure, stepPreviews.MoveToImmutable());
    }

    internal async ValueTask<SetupRunResult> RunAsync(
        SetupPlan plan,
        SetupApproval approval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(approval);
        if (!approval.IsApproved)
            return Result(SetupRunState.Refused, approval.Failure ?? SetupFailure.ConsentRequired);
        if (approval.ReviewOnly)
            return Result(SetupRunState.Refused, SetupFailure.ConsentScopeMismatch);
        if (!approval.TryConsume())
            return Result(SetupRunState.Refused, SetupFailure.ConsentConsumed);
        var bindingFailure = ApprovalBindingFailure(plan, approval, fileSystem.JournalPath);
        if (bindingFailure is { } binding)
            return Result(SetupRunState.Refused, binding);
        var now = clock.GetUtcNow();
        if (now < plan.CreatedAtUtc || now >= plan.ExpiresAtUtc)
            return Result(SetupRunState.Refused, SetupFailure.PlanStale);
        if (plan.Disposition == SetupPlanDisposition.Blocked)
            return Result(SetupRunState.Refused, SetupFailure.PlanBlocked);
        if (!approval.ApprovedScopes.SequenceEqual(plan.RequiredConsentScopes))
            return Result(SetupRunState.Refused, SetupFailure.ConsentScopeMismatch);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = await fileSystem.ReadAsync(SetupJournalCodec.MaximumBytes, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(snapshot?.Version, approval.ExpectedJournalVersion, StringComparison.Ordinal))
            return Result(SetupRunState.Refused, SetupFailure.JournalConcurrentChange);
        var document = snapshot is null
            ? SetupJournalDocument.Create(plan, approval, now)
            : SetupJournalCodec.Read(snapshot.Content);
        if (document.Purpose != SetupJournalPurpose.UnqualifiedExecution)
            throw new SetupException(SetupFailure.JournalPurposeMismatch);
        var requiresRollForward = snapshot is not null && ValidateJournalForPlan(document, plan);
        if (requiresRollForward != approval.RequiresJournalRollForward)
            return Result(SetupRunState.Refused, SetupFailure.JournalConcurrentChange);
        var version = snapshot?.Version;

        if (snapshot is null)
            (document, version) = await PersistAsync(document, version, cancellationToken).ConfigureAwait(false);
        else if (requiresRollForward)
        {
            if (document.Supersessions.Length >= 16)
                throw new SetupException(SetupFailure.JournalTooLarge);
            document = document with
            {
                PlanId = plan.Id,
                PlanFingerprint = plan.Fingerprint,
                DesiredStateFingerprint = plan.DesiredStateFingerprint,
                HostFactsFingerprint = plan.HostFactsFingerprint,
                ApprovedScopes = approval.ApprovedScopes.ToArray(),
                MaximumPrivilege = approval.MaximumPrivilege,
                Supersessions =
                [
                    .. document.Supersessions,
                    new SetupJournalSupersession
                    {
                        PlanFingerprint = document.PlanFingerprint,
                        HostFactsFingerprint = document.HostFactsFingerprint,
                        JournalRevision = document.Revision,
                        AcceptedAtUtc = now
                    }
                ]
            };
            (document, version) = await PersistAsync(document, version, cancellationToken).ConfigureAwait(false);
        }
        document = document with
        {
            State = SetupJournalState.Running,
            ApprovedScopes = approval.ApprovedScopes.ToArray(),
            MaximumPrivilege = approval.MaximumPrivilege,
            LastFailure = null
        };
        (document, version) = await PersistAsync(document, version, cancellationToken).ConfigureAwait(false);

        var executed = ImmutableArray.CreateBuilder<string>();
        var reconciled = ImmutableArray.CreateBuilder<string>();
        var completed = ImmutableArray.CreateBuilder<string>();
        foreach (var step in plan.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stepIndex = Array.FindIndex(document.Steps, value => value.Id == step.Id);
            var journalStep = document.Steps[stepIndex];
            if (clock.GetUtcNow() >= plan.ExpiresAtUtc)
                return await BlockAsync(document, version, stepIndex, step.Id,
                    SetupFailure.PlanStale, SetupJournalStepStatus.Failed,
                    executed, reconciled, completed, cancellationToken).ConfigureAwait(false);
            var observation = await ObserveAsync(step, cancellationToken).ConfigureAwait(false);

            if (observation.State == SetupObservationState.Satisfied)
            {
                if (journalStep.Status == SetupJournalStepStatus.Completed)
                {
                    if (!string.Equals(journalStep.ObservationFingerprint, observation.EvidenceFingerprint, StringComparison.Ordinal))
                        return await BlockAsync(document, version, stepIndex, step.Id,
                            SetupFailure.ExternalStateConflict, SetupJournalStepStatus.Conflict,
                            executed, reconciled, completed, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    document = ReplaceStep(document, stepIndex, journalStep with
                    {
                        Status = SetupJournalStepStatus.Completed,
                        CompletionOrigin = SetupCompletionOrigin.Reconciled,
                        ObservationFingerprint = observation.EvidenceFingerprint,
                        CompletedAtUtc = clock.GetUtcNow(),
                        LastFailure = null
                    });
                    (document, version) = await PersistAsync(document, version, cancellationToken).ConfigureAwait(false);
                    reconciled.Add(step.Id);
                }
                completed.Add(step.Id);
                continue;
            }
            if (observation.State == SetupObservationState.Conflict ||
                journalStep.Status == SetupJournalStepStatus.Completed)
                return await BlockAsync(document, version, stepIndex, step.Id,
                    SetupFailure.ExternalStateConflict, SetupJournalStepStatus.Conflict,
                    executed, reconciled, completed, cancellationToken).ConfigureAwait(false);
            if (observation.State == SetupObservationState.Unknown)
                return await BlockAsync(document, version, stepIndex, step.Id,
                    SetupFailure.ExternalStateUnknown, SetupJournalStepStatus.Failed,
                    executed, reconciled, completed, cancellationToken).ConfigureAwait(false);
            if (step.Execution == SetupStepExecution.Deferred || step.Command is null)
                return await BlockAsync(document, version, stepIndex, step.Id,
                    SetupFailure.DeferredStep, SetupJournalStepStatus.Failed,
                    executed, reconciled, completed, cancellationToken).ConfigureAwait(false);
            if (step.Privilege > approval.MaximumPrivilege)
                return await BlockAsync(document, version, stepIndex, step.Id,
                    SetupFailure.PrivilegeRequired, SetupJournalStepStatus.Failed,
                    executed, reconciled, completed, cancellationToken).ConfigureAwait(false);
            if (step.ConsentScopes.Any(scope => !approval.ApprovedScopes.Contains(scope)))
                return await BlockAsync(document, version, stepIndex, step.Id,
                    SetupFailure.ConsentScopeMismatch, SetupJournalStepStatus.Failed,
                    executed, reconciled, completed, cancellationToken).ConfigureAwait(false);

            document = ReplaceStep(document, stepIndex, journalStep with
            {
                Status = SetupJournalStepStatus.Running,
                Attempts = checked(journalStep.Attempts + 1),
                CompletionOrigin = SetupCompletionOrigin.None,
                ObservationFingerprint = null,
                CompletedAtUtc = null,
                LastFailure = null
            });
            (document, version) = await PersistAsync(document, version, cancellationToken).ConfigureAwait(false);
            var execution = await executor.ExecuteAsync(step.Command, cancellationToken).ConfigureAwait(false)
                ?? throw new SetupException(SetupFailure.StepFailed);
            execution.Validate();
            if (execution.Outcome == SetupExecutionOutcome.Canceled)
            {
                document = ReplaceStep(document, stepIndex, document.Steps[stepIndex] with
                {
                    Status = SetupJournalStepStatus.Failed,
                    LastFailure = SetupFailure.Interrupted
                }) with
                {
                    State = SetupJournalState.Interrupted,
                    LastFailure = SetupFailure.Interrupted
                };
                (document, _) = await PersistAsync(document, version, CancellationToken.None).ConfigureAwait(false);
                return Result(SetupRunState.Interrupted, SetupFailure.Interrupted, step.Id,
                    executed, reconciled, completed);
            }
            if (execution.Outcome != SetupExecutionOutcome.Completed)
            {
                var failure = execution.Outcome == SetupExecutionOutcome.Refused
                    ? SetupFailure.ExecutorRefused
                    : SetupFailure.StepFailed;
                return await BlockAsync(document, version, stepIndex, step.Id, failure,
                    SetupJournalStepStatus.Failed, executed, reconciled, completed,
                    cancellationToken).ConfigureAwait(false);
            }
            executed.Add(step.Id);
            var after = await ObserveAsync(step, cancellationToken).ConfigureAwait(false);
            if (after.State != SetupObservationState.Satisfied)
            {
                var failure = after.State == SetupObservationState.Conflict
                    ? SetupFailure.ExternalStateConflict
                    : after.State == SetupObservationState.Unknown
                        ? SetupFailure.ExternalStateUnknown
                        : SetupFailure.StepFailed;
                return await BlockAsync(document, version, stepIndex, step.Id, failure,
                    after.State == SetupObservationState.Conflict
                        ? SetupJournalStepStatus.Conflict
                        : SetupJournalStepStatus.Failed,
                    executed, reconciled, completed, cancellationToken).ConfigureAwait(false);
            }
            document = ReplaceStep(document, stepIndex, document.Steps[stepIndex] with
            {
                Status = SetupJournalStepStatus.Completed,
                CompletionOrigin = SetupCompletionOrigin.Executed,
                ObservationFingerprint = after.EvidenceFingerprint,
                CompletedAtUtc = clock.GetUtcNow(),
                LastFailure = null
            });
            (document, version) = await PersistAsync(document, version, cancellationToken).ConfigureAwait(false);
            completed.Add(step.Id);
        }

        document = document with { State = SetupJournalState.Completed, LastFailure = null };
        (document, _) = await PersistAsync(document, version, cancellationToken).ConfigureAwait(false);
        return Result(SetupRunState.Completed, null, null, executed, reconciled, completed);
    }

    private async ValueTask<SetupRunResult> BlockAsync(
        SetupJournalDocument document,
        string? version,
        int stepIndex,
        string stepId,
        SetupFailure failure,
        SetupJournalStepStatus status,
        ImmutableArray<string>.Builder executed,
        ImmutableArray<string>.Builder reconciled,
        ImmutableArray<string>.Builder completed,
        CancellationToken cancellationToken)
    {
        document = ReplaceStep(document, stepIndex, document.Steps[stepIndex] with
        {
            Status = status,
            CompletionOrigin = SetupCompletionOrigin.None,
            ObservationFingerprint = null,
            CompletedAtUtc = null,
            LastFailure = failure
        }) with
        {
            State = SetupJournalState.Blocked,
            LastFailure = failure
        };
        await PersistAsync(document, version, cancellationToken).ConfigureAwait(false);
        return Result(SetupRunState.Blocked, failure, stepId, executed, reconciled, completed);
    }

    private async ValueTask<(SetupJournalDocument Document, string Version)> PersistAsync(
        SetupJournalDocument document,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var next = document with
        {
            Revision = checked(document.Revision + 1),
            UpdatedAtUtc = now < document.UpdatedAtUtc ? document.UpdatedAtUtc : now,
            IntegritySha256 = ""
        };
        var bytes = SetupJournalCodec.Write(next);
        var committed = await fileSystem.WriteAtomicAsync(expectedVersion, bytes, cancellationToken).ConfigureAwait(false);
        return (SetupJournalCodec.Read(committed.Content), committed.Version);
    }

    private static SetupJournalDocument ReplaceStep(
        SetupJournalDocument document,
        int index,
        SetupJournalStep replacement)
    {
        var steps = document.Steps.ToArray();
        steps[index] = replacement;
        return document with { Steps = steps };
    }

    private static bool ValidateJournalForPlan(SetupJournalDocument document, SetupPlan plan)
    {
        if (!string.Equals(document.ConfigurationFingerprint, plan.ConfigurationFingerprint, StringComparison.Ordinal))
            throw new SetupException(SetupFailure.ConfigurationChanged);
        if (!string.Equals(document.ArtifactFactsFingerprint, plan.ArtifactFactsFingerprint, StringComparison.Ordinal))
            throw new SetupException(SetupFailure.ArtifactFactsChanged);
        if (!string.Equals(document.DesiredStateFingerprint, plan.DesiredStateFingerprint, StringComparison.Ordinal))
            throw new SetupException(SetupFailure.PlanChanged);
        if (!document.Steps.Select(step => step.Id).SequenceEqual(plan.Steps.Select(step => step.Id), StringComparer.Ordinal))
            throw new SetupException(SetupFailure.JournalCorrupt);
        return !string.Equals(document.PlanFingerprint, plan.Fingerprint, StringComparison.Ordinal) ||
            !string.Equals(document.HostFactsFingerprint, plan.HostFactsFingerprint, StringComparison.Ordinal);
    }

    private async ValueTask<SetupStepObservation> ObserveAsync(
        SetupStep step,
        CancellationToken cancellationToken) =>
        await stateProbe.ObserveAsync(step, cancellationToken).ConfigureAwait(false)
            ?? throw new SetupException(SetupFailure.ExternalStateUnknown);

    private static SetupFailure? ApprovalBindingFailure(
        SetupPlan plan,
        SetupApproval approval,
        string journalPath)
    {
        if (!string.Equals(journalPath, approval.JournalPath, StringComparison.Ordinal))
            return SetupFailure.JournalDestinationChanged;
        if (!string.Equals(plan.ConfigurationFingerprint, approval.ConfigurationFingerprint, StringComparison.Ordinal))
            return SetupFailure.ConfigurationChanged;
        if (!string.Equals(plan.HostFactsFingerprint, approval.HostFactsFingerprint, StringComparison.Ordinal))
            return SetupFailure.HostFactsChanged;
        if (!string.Equals(plan.ArtifactFactsFingerprint, approval.ArtifactFactsFingerprint, StringComparison.Ordinal))
            return SetupFailure.ArtifactFactsChanged;
        return string.Equals(plan.Fingerprint, approval.PlanFingerprint, StringComparison.Ordinal)
            ? null
            : SetupFailure.PlanChanged;
    }

    private static SetupRunResult Result(
        SetupRunState state,
        SetupFailure? failure,
        string? stepId = null,
        IEnumerable<string>? executed = null,
        IEnumerable<string>? reconciled = null,
        IEnumerable<string>? completed = null) =>
        new(state, failure, stepId,
            executed?.ToImmutableArray() ?? [],
            reconciled?.ToImmutableArray() ?? [],
            completed?.ToImmutableArray() ?? [],
            RollbackAvailable: false,
            RollbackPerformed: false);
}
