using System.Collections.Immutable;
using System.Globalization;
using Martlet.HostArtifacts;

namespace Martlet.Host.Setup;

public enum ArtifactRightsDecision { No, Approve }
public enum ArtifactAcquisitionDecision { No, Approve }
public enum ArtifactAcquisitionDisposition { Reviewable, Blocked }
public enum ArtifactAcquisitionPreviewState
{
    Proposed,
    ResumeAvailable,
    VerificationPending,
    FinalizationPending,
    RecoveryRequired,
    Completed,
    Blocked
}
public enum ArtifactAcquisitionRunState { Refused, Blocked, Interrupted, Acquired, AlreadyAcquired }
public enum ArtifactAcquisitionProgressPhase { Connecting, Downloading, Verifying, Finalizing, Completed, Interrupted, Failed }

public sealed class ArtifactReviewedLicense
{
    public string ClaimId { get; }
    public string Terms { get; }
    public string EvidenceSha256 { get; }

    public ArtifactReviewedLicense(string claimId, string terms, string evidenceSha256)
    {
        AcquisitionGuard.Identifier(claimId, ArtifactAcquisitionFailure.RightsNotApproved);
        AcquisitionGuard.Text(terms, 512, ArtifactAcquisitionFailure.RightsNotApproved);
        AcquisitionGuard.Require(!string.IsNullOrWhiteSpace(terms) &&
            terms.Trim().ToLowerInvariant() is not ("unknown" or "unreviewed" or "pending" or "todo" or "n/a"),
            ArtifactAcquisitionFailure.RightsNotApproved);
        AcquisitionGuard.Fingerprint(evidenceSha256, ArtifactAcquisitionFailure.RightsNotApproved);
        ClaimId = claimId;
        Terms = terms;
        EvidenceSha256 = evidenceSha256;
    }
}

public sealed class ArtifactRightsReview
{
    private readonly ArtifactRightsSubject candidate;

    public string ArtifactIdentityFingerprint => candidate.IdentityFingerprint;
    public string ReviewRevision { get; }
    public string EvidenceSha256 { get; }
    public ImmutableArray<ArtifactLicenseClaim> Licenses => candidate.Licenses;
    public ImmutableArray<ArtifactReviewedLicense> ReviewedTerms { get; }
    public string? SelectionFingerprint { get; }
    private readonly TimeProvider clock;

    public ArtifactRightsReview(ArtifactAcquisitionSelection selection, string artifactId,
        string reviewRevision, string evidenceSha256,
        IEnumerable<ArtifactReviewedLicense>? reviewedTerms = null, TimeProvider? clock = null)
        : this(ArtifactRightsSubject.From(selection, artifactId),
            reviewRevision, evidenceSha256, reviewedTerms, clock)
    {
        SelectionFingerprint = selection.Fingerprint;
    }

    public ArtifactRightsReview(
        ArtifactAcquisitionCandidate candidate,
        string reviewRevision,
        string evidenceSha256,
        IEnumerable<ArtifactReviewedLicense>? reviewedTerms = null,
        TimeProvider? clock = null)
        : this(ArtifactRightsSubject.From(candidate), reviewRevision, evidenceSha256, reviewedTerms, clock)
    {
    }

    private ArtifactRightsReview(
        ArtifactRightsSubject candidate,
        string reviewRevision,
        string evidenceSha256,
        IEnumerable<ArtifactReviewedLicense>? reviewedTerms,
        TimeProvider? clock)
    {
        this.candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        this.clock = clock ?? TimeProvider.System;
        AcquisitionGuard.Identifier(reviewRevision, ArtifactAcquisitionFailure.InvalidPlan);
        AcquisitionGuard.Fingerprint(evidenceSha256, ArtifactAcquisitionFailure.InvalidPlan);
        ReviewRevision = reviewRevision;
        EvidenceSha256 = evidenceSha256;
        var ownedTerms = (reviewedTerms ?? []).Take(17).ToArray();
        AcquisitionGuard.Require(ownedTerms.Length <= 16 && ownedTerms.All(term => term is not null),
            ArtifactAcquisitionFailure.RightsNotApproved);
        ReviewedTerms = ownedTerms.OrderBy(term => term.ClaimId, StringComparer.Ordinal).ToImmutableArray();
        AcquisitionGuard.Require(
            ReviewedTerms.Select(term => term.ClaimId).Distinct(StringComparer.Ordinal).Count() == ReviewedTerms.Length &&
            ReviewedTerms.All(term => candidate.Licenses.Any(license => license.Id == term.ClaimId)),
            ArtifactAcquisitionFailure.RightsNotApproved);
    }

    public ArtifactRightsAuthorization Authorize(ArtifactRightsDecision decision = ArtifactRightsDecision.No)
    {
        if (!Enum.IsDefined(decision) || decision != ArtifactRightsDecision.Approve ||
            candidate.Licenses.IsEmpty || candidate.Licenses.Any(license =>
                license.Spdx == "unknown" && !ReviewedTerms.Any(term => term.ClaimId == license.Id)))
            return ArtifactRightsAuthorization.Refused(candidate, ReviewRevision, EvidenceSha256);
        return ArtifactRightsAuthorization.Approved(candidate, ReviewRevision, EvidenceSha256, ReviewedTerms,
            SelectionFingerprint, clock.GetUtcNow());
    }
}

public sealed class ArtifactRightsAuthorization
{
    public string ArtifactIdentityFingerprint { get; }
    public string ReviewRevision { get; }
    public string EvidenceSha256 { get; }
    public bool IsApproved { get; }
    public string Fingerprint { get; }
    public string? SelectionFingerprint { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset ExpiresAtUtc => CreatedAtUtc.AddMinutes(10);

    private ArtifactRightsAuthorization(
        ArtifactRightsSubject candidate,
        string reviewRevision,
        string evidenceSha256,
        bool approved,
        ImmutableArray<ArtifactReviewedLicense> terms = default,
        string? selectionFingerprint = null,
        DateTimeOffset createdAt = default)
    {
        ArtifactIdentityFingerprint = candidate.IdentityFingerprint;
        ReviewRevision = reviewRevision;
        EvidenceSha256 = evidenceSha256;
        IsApproved = approved;
        SelectionFingerprint = selectionFingerprint;
        CreatedAtUtc = createdAt;
        Fingerprint = FingerprintBuilder.Create(
            "artifact-rights-authorization-v1",
            ArtifactIdentityFingerprint,
            ReviewRevision,
            EvidenceSha256,
            SelectionFingerprint ?? "",
            approved.ToString(CultureInfo.InvariantCulture),
            terms.IsDefault ? "" : FingerprintBuilder.Create(terms.SelectMany(term =>
                new[] { term.ClaimId, term.Terms, term.EvidenceSha256 }).ToArray()));
    }

    internal static ArtifactRightsAuthorization Refused(
        ArtifactRightsSubject candidate,
        string reviewRevision,
        string evidenceSha256) =>
        new(candidate, reviewRevision, evidenceSha256, false);

    internal static ArtifactRightsAuthorization Approved(
        ArtifactRightsSubject candidate,
        string reviewRevision,
        string evidenceSha256,
        ImmutableArray<ArtifactReviewedLicense> terms, string? selectionFingerprint, DateTimeOffset createdAt) =>
        new(candidate, reviewRevision, evidenceSha256, true, terms, selectionFingerprint, createdAt);
}

internal sealed record ArtifactRightsSubject(
    string IdentityFingerprint, ImmutableArray<ArtifactLicenseClaim> Licenses)
{
    internal static ArtifactRightsSubject From(ArtifactAcquisitionCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return new(candidate.IdentityFingerprint, candidate.Licenses);
    }

    internal static ArtifactRightsSubject From(ArtifactAcquisitionSelection selection, string artifactId)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Artifacts.SingleOrDefault(artifact => artifact.ArtifactId == artifactId) is { } artifact)
            return From(artifact);
        if (selection.ImageCandidates.SingleOrDefault(image => image.ArtifactId == artifactId) is { } image)
            return new(image.IdentityFingerprint, image.Licenses);
        throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ArtifactNotSelected);
    }
}

public sealed class ArtifactAcquisitionPlan
{
    public ArtifactAcquisitionSelection Selection { get; }
    public ArtifactAcquisitionSource Source => Candidate.Source;
    public bool RecoverCorruptFinal { get; }
    public string? CorruptFinalSha256 { get; }
    public string? FinalIdentity { get; }
    public long FinalBytes { get; }
    public string Id { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public ArtifactAcquisitionDisposition Disposition { get; }
    public ArtifactAcquisitionFailure? Blocker { get; }
    public ArtifactAcquisitionCandidate Candidate { get; }
    public string RightsAuthorizationFingerprint { get; }
    public string RightsReviewRevision { get; }
    public string RightsEvidenceSha256 { get; }
    public string SetupPlanFingerprint { get; }
    public string SetupDesiredStateFingerprint { get; }
    public string HostFactsFingerprint { get; }
    public string ArtifactFactsFingerprint { get; }
    public string SetupJournalPath { get; }
    public string SetupJournalVersion { get; }
    public long SetupJournalRevision { get; }
    public Guid SetupJournalId { get; }
    public string ArtifactRootPath { get; }
    public string DestinationPath { get; }
    public string PartialPath { get; }
    public string JournalPath { get; }
    public long ExistingBytes { get; }
    public long ObservedAvailableBytes { get; }
    public long RequiredAvailableBytes { get; }
    public string StorageFactsFingerprint { get; }
    public string? AcquisitionJournalVersion { get; }
    public string? ExpectedEntityTag { get; }
    public DateTimeOffset? ExpectedLastModifiedUtc { get; }
    public TimeSpan TransferTimeout { get; }
    public ImmutableArray<SetupConsentScope> RequiredConsentScopes { get; }
    public string Fingerprint { get; }

    internal ArtifactAcquisitionPlan(
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        ArtifactAcquisitionDisposition disposition,
        ArtifactAcquisitionFailure? blocker,
        ArtifactAcquisitionCandidate candidate,
        ArtifactRightsAuthorization rights,
        SetupPlan setupPlan,
        SetupPreview setupPreview,
        ArtifactAcquisitionPaths paths,
        ArtifactAcquisitionStorageSnapshot storage,
        ArtifactAcquisitionJournalSnapshot? journal,
        long requiredAvailableBytes,
        TimeSpan transferTimeout,
        ArtifactAcquisitionSelection selection,
        Guid setupJournalId,
        bool recoverCorruptFinal = false,
        string? corruptFinalSha256 = null)
    {
        Selection = selection;
        SetupJournalId = setupJournalId;
        RecoverCorruptFinal = recoverCorruptFinal;
        CorruptFinalSha256 = corruptFinalSha256;
        FinalIdentity = storage.FinalIdentity;
        FinalBytes = storage.FinalBytes;
        Id = "h05b-" + candidate.ArtifactId;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        Disposition = disposition;
        Blocker = blocker;
        Candidate = candidate;
        RightsAuthorizationFingerprint = rights.Fingerprint;
        RightsReviewRevision = rights.ReviewRevision;
        RightsEvidenceSha256 = rights.EvidenceSha256;
        SetupPlanFingerprint = setupPlan.Fingerprint;
        SetupDesiredStateFingerprint = setupPlan.DesiredStateFingerprint;
        HostFactsFingerprint = setupPlan.HostFactsFingerprint;
        ArtifactFactsFingerprint = setupPlan.ArtifactFactsFingerprint;
        SetupJournalPath = setupPreview.JournalPath;
        SetupJournalVersion = setupPreview.JournalVersion
            ?? throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.SetupJournalMissing);
        SetupJournalRevision = setupPreview.JournalRevision
            ?? throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.SetupJournalMissing);
        ArtifactRootPath = paths.RootPath;
        DestinationPath = paths.DestinationPath;
        PartialPath = paths.PartialPath;
        JournalPath = paths.JournalPath;
        ExistingBytes = storage.PartialBytes;
        ObservedAvailableBytes = storage.AvailableBytes;
        RequiredAvailableBytes = requiredAvailableBytes;
        StorageFactsFingerprint = storage.Fingerprint;
        AcquisitionJournalVersion = storage.JournalVersion;
        ExpectedEntityTag = journal?.EntityTag;
        ExpectedLastModifiedUtc = journal?.LastModifiedUtc;
        TransferTimeout = transferTimeout;
        RequiredConsentScopes =
        [
            SetupConsentScope.LocalJournal,
            SetupConsentScope.ArtifactDownload,
            SetupConsentScope.ModelAndVoiceRights
        ];
        Fingerprint = BuildFingerprint();
    }

    private string BuildFingerprint() => FingerprintBuilder.Create(
        "artifact-acquisition-plan-v2",
        Selection.Fingerprint,
        Source.IdentityFingerprint,
        RecoverCorruptFinal.ToString(CultureInfo.InvariantCulture),
        CorruptFinalSha256 ?? "",
        FinalIdentity ?? "",
        FinalBytes.ToString(CultureInfo.InvariantCulture),
        Id,
        CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        ExpiresAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        Disposition.ToString(),
        Blocker?.ToString() ?? "null",
        Candidate.IdentityFingerprint,
        RightsAuthorizationFingerprint,
        RightsReviewRevision,
        RightsEvidenceSha256,
        SetupPlanFingerprint,
        SetupDesiredStateFingerprint,
        HostFactsFingerprint,
        ArtifactFactsFingerprint,
        SetupJournalPath,
        SetupJournalVersion,
        SetupJournalRevision.ToString(CultureInfo.InvariantCulture),
        SetupJournalId.ToString("D"),
        ArtifactRootPath,
        DestinationPath,
        PartialPath,
        JournalPath,
        ExistingBytes.ToString(CultureInfo.InvariantCulture),
        ObservedAvailableBytes.ToString(CultureInfo.InvariantCulture),
        RequiredAvailableBytes.ToString(CultureInfo.InvariantCulture),
        StorageFactsFingerprint,
        AcquisitionJournalVersion ?? "null",
        ExpectedEntityTag ?? "null",
        ExpectedLastModifiedUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "null",
        TransferTimeout.Ticks.ToString(CultureInfo.InvariantCulture),
        string.Join(",", RequiredConsentScopes));
}

public sealed class ArtifactAcquisitionPreview
{
    private readonly ArtifactAcquisitionPlan plan;

    public ArtifactAcquisitionPlan Plan => plan;
    public ArtifactAcquisitionPreviewState State { get; }
    public bool CanApprove { get; }
    public ArtifactAcquisitionFailure? Failure { get; }
    public ArtifactAcquisitionRemedy? Remedy => Failure is { } failure
        ? ArtifactAcquisitionRemedies.For(failure)
        : null;

    internal ArtifactAcquisitionPreview(
        ArtifactAcquisitionPlan plan,
        ArtifactAcquisitionPreviewState state,
        bool canApprove,
        ArtifactAcquisitionFailure? failure)
    {
        this.plan = plan;
        State = state;
        CanApprove = canApprove;
        Failure = failure;
    }

    public ArtifactAcquisitionApproval Approve(
        ArtifactAcquisitionDecision decision = ArtifactAcquisitionDecision.No,
        IEnumerable<SetupConsentScope>? approvedScopes = null)
    {
        if (!Enum.IsDefined(decision) || decision != ArtifactAcquisitionDecision.Approve)
            return ArtifactAcquisitionApproval.Refused(plan, ArtifactAcquisitionFailure.ConsentRequired);
        if (!CanApprove)
            return ArtifactAcquisitionApproval.Refused(
                plan,
                Failure ?? ArtifactAcquisitionFailure.InvalidPlan);
        var scopes = (approvedScopes ?? []).Order().ToImmutableArray();
        if (scopes.Any(value => !Enum.IsDefined(value)) ||
            scopes.Distinct().Count() != scopes.Length ||
            !scopes.SequenceEqual(plan.RequiredConsentScopes))
            return ArtifactAcquisitionApproval.Refused(
                plan,
                ArtifactAcquisitionFailure.ConsentScopeMismatch);
        return ArtifactAcquisitionApproval.Allowed(plan, scopes);
    }
}

public sealed class ArtifactAcquisitionApproval
{
    private int consumed;

    internal string PlanFingerprint { get; }
    internal string SetupJournalPath { get; }
    internal string SetupJournalVersion { get; }
    internal string? AcquisitionJournalVersion { get; }
    internal string StorageFactsFingerprint { get; }
    public bool IsApproved { get; }
    public ArtifactAcquisitionFailure? Failure { get; }
    public ArtifactAcquisitionRemedy? Remedy => Failure is { } failure
        ? ArtifactAcquisitionRemedies.For(failure)
        : null;
    public ImmutableArray<SetupConsentScope> ApprovedScopes { get; }

    private ArtifactAcquisitionApproval(
        ArtifactAcquisitionPlan plan,
        bool approved,
        ArtifactAcquisitionFailure? failure,
        ImmutableArray<SetupConsentScope> scopes)
    {
        PlanFingerprint = plan.Fingerprint;
        SetupJournalPath = plan.SetupJournalPath;
        SetupJournalVersion = plan.SetupJournalVersion;
        AcquisitionJournalVersion = plan.AcquisitionJournalVersion;
        StorageFactsFingerprint = plan.StorageFactsFingerprint;
        IsApproved = approved;
        Failure = failure;
        ApprovedScopes = scopes;
    }

    internal static ArtifactAcquisitionApproval Refused(
        ArtifactAcquisitionPlan plan,
        ArtifactAcquisitionFailure failure) =>
        new(plan, false, failure, []);

    internal static ArtifactAcquisitionApproval Allowed(
        ArtifactAcquisitionPlan plan,
        ImmutableArray<SetupConsentScope> scopes) =>
        new(plan, true, null, scopes);

    internal bool TryConsume() => Interlocked.Exchange(ref consumed, 1) == 0;
}

public sealed record ArtifactAcquisitionProgress(
    string PlanFingerprint,
    string ArtifactId,
    ArtifactAcquisitionProgressPhase Phase,
    long PersistedBytes,
    long ExpectedBytes,
    bool Resumed,
    DateTimeOffset ObservedAtUtc);

public interface IArtifactAcquisitionProgressSink
{
    ValueTask ReportAsync(ArtifactAcquisitionProgress progress, CancellationToken cancellationToken);
}

public sealed class NullArtifactAcquisitionProgressSink : IArtifactAcquisitionProgressSink
{
    public ValueTask ReportAsync(ArtifactAcquisitionProgress progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

public sealed record ArtifactAcquisitionResult(
    ArtifactAcquisitionRunState State,
    ArtifactAcquisitionFailure? Failure,
    long PersistedBytes,
    bool Resumed,
    bool PayloadVerified,
    bool Finalized,
    bool CleanupPerformed,
    string? VerifiedSha256)
{
    public bool RuntimeEnabled => false;
    public bool HostReady => false;
    public ArtifactAcquisitionRemedy? Remedy => Failure is { } failure
        ? ArtifactAcquisitionRemedies.For(failure)
        : null;
}

internal static class AcquisitionGuard
{
    internal static void Require(bool condition, ArtifactAcquisitionFailure failure)
    {
        if (!condition) throw new ArtifactAcquisitionException(failure);
    }

    internal static void Identifier(string? value, ArtifactAcquisitionFailure failure) =>
        Require(value is { Length: >= 1 and <= 64 } &&
            value[0] is >= 'a' and <= 'z' or >= '0' and <= '9' &&
            value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.'),
            failure);

    internal static void Fingerprint(string? value, ArtifactAcquisitionFailure failure) =>
        Require(value is { Length: 64 } &&
            value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            failure);

    internal static void Text(string? value, int maximum, ArtifactAcquisitionFailure failure) =>
        Require(value is { Length: >= 1 } && value.Length <= maximum &&
            !value.Any(char.IsControl), failure);
}
