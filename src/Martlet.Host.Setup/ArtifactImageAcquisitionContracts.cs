using System.Collections.Immutable;
using System.Globalization;
using Martlet.HostArtifacts;

namespace Martlet.Host.Setup;

public sealed class ArtifactImageAcquisitionPlan
{
    public ArtifactAcquisitionSelection Selection { get; }
    public string Fingerprint { get; }
    public ArtifactAcquisitionDisposition Disposition { get; }
    public ArtifactAcquisitionFailure? Blocker { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public string DestinationPath => Paths.Destination;
    public string StagingPath => Paths.Staging;
    public string JournalPath => Paths.Journal;
    public string QuarantinePath => Paths.Quarantine;
    public string StorageFactsFingerprint => Storage.Fingerprint;
    public long KnownContentBytes => Selection.ImageContentInventory.KnownBytes;
    public long RemainingContentBytes { get; }
    public long MaximumContentResponseBytes { get; }
    public long MaximumControlResponseBytes { get; }
    public long MaximumResponseBodyBytes => checked(MaximumContentResponseBytes + MaximumControlResponseBytes);
    public int MaximumRequests { get; }
    public int MaximumTokenExchanges { get; }
    public int MaximumRedirects { get; }
    public long RequiredAvailableBytes { get; }
    public long ObservedAvailableBytes => Storage.AvailableBytes;
    public long RetainedQuarantineBytes => Storage.Quarantine?.Files.Sum(file => file.Bytes) ?? 0;
    public bool QuarantineAndReacquire { get; }
    public bool SharedAcrossSelections => false;
    public bool ExecutionAuthorized => false;
    public bool HostReady => false;
    public long? ExpandedBytes => null;
    public long? InstallationPeakBytes => null;
    public ImmutableArray<SetupConsentScope> RequiredConsentScopes { get; } =
        [SetupConsentScope.LocalJournal, SetupConsentScope.ArtifactDownload, SetupConsentScope.ModelAndVoiceRights];
    internal SetupPlan SetupPlan { get; }
    internal SetupPreview SetupPreview { get; }
    internal Guid SetupJournalId { get; }
    internal ImmutableArray<ArtifactRightsAuthorization> Rights { get; }
    internal string RightsFingerprint { get; }
    internal ArtifactImagePaths Paths { get; }
    internal ArtifactImageStorageSnapshot Storage { get; }
    internal ArtifactImageTree? RecoveryTree { get; }
    internal TimeSpan TransferTimeout { get; }
    internal long ReserveBytes { get; }
    internal const long MetadataAllowanceBytes = 2L * ArtifactImageJournalCodec.MaximumBytes + 2L * 65_536;

    internal ArtifactImageAcquisitionPlan(ArtifactAcquisitionSelection selection, SetupPlan setupPlan,
        SetupPreview setupPreview, Guid setupJournalId, ImmutableArray<ArtifactRightsAuthorization> rights,
        ArtifactImagePaths paths, ArtifactImageStorageSnapshot storage, ArtifactImageTree? recoveryTree,
        DateTimeOffset created, DateTimeOffset expires, TimeSpan timeout, long reserve,
        ArtifactAcquisitionFailure? blocker)
    {
        Selection = selection;
        SetupPlan = setupPlan;
        SetupPreview = setupPreview;
        SetupJournalId = setupJournalId;
        Rights = rights;
        RightsFingerprint = FingerprintBuilder.Create(rights.Select(right => right.Fingerprint).ToArray());
        Paths = paths;
        Storage = storage;
        RecoveryTree = recoveryTree;
        CreatedAtUtc = created;
        ExpiresAtUtc = expires;
        TransferTimeout = timeout;
        ReserveBytes = reserve;
        QuarantineAndReacquire = recoveryTree is not null ||
            storage.Journal?.State is ArtifactImageJournalState.Quarantining or ArtifactImageJournalState.Quarantined;
        var retained = QuarantineAndReacquire ? 0 : storage.Journal?.Contents.Sum(content => content.Bytes) ?? 0;
        RemainingContentBytes = checked(KnownContentBytes - retained);
        MaximumRequests = checked(selection.ImageContentInventory.Contents.Length * 4);
        MaximumTokenExchanges = selection.ImageContentInventory.Contents.Length;
        MaximumRedirects = selection.ImageContentInventory.Contents.Count(content =>
            content.Kind is ArtifactImageContentKind.Configuration or ArtifactImageContentKind.Layer);
        MaximumContentResponseBytes = checked(RemainingContentBytes + selection.ImageContentInventory.Contents.Length);
        MaximumControlResponseBytes = checked(selection.ImageContentInventory.Contents.Length *
            ((long)ArtifactImageRequestBudget.TokenBodyLimit + 1));
        RequiredAvailableBytes = checked(RemainingContentBytes + MetadataAllowanceBytes + reserve);
        Blocker = blocker ?? (storage.AvailableBytes < RequiredAvailableBytes
            ? ArtifactAcquisitionFailure.FreeSpaceInsufficient : null);
        Disposition = Blocker is null ? ArtifactAcquisitionDisposition.Reviewable : ArtifactAcquisitionDisposition.Blocked;
        Fingerprint = FingerprintBuilder.Create(
            "artifact-image-acquisition-plan-v1", selection.Fingerprint, selection.ImageContentInventory.Fingerprint,
            setupPlan.Fingerprint, setupPlan.DesiredStateFingerprint, setupPreview.JournalVersion ?? "",
            setupJournalId.ToString("D"), RightsFingerprint, paths.Destination, paths.Staging, paths.Quarantine,
            paths.Journal, storage.Fingerprint, ArtifactImageStorageSnapshot.TreeFingerprint(recoveryTree),
            created.ToString("O", CultureInfo.InvariantCulture), expires.ToString("O", CultureInfo.InvariantCulture),
            timeout.Ticks.ToString(CultureInfo.InvariantCulture), reserve.ToString(CultureInfo.InvariantCulture),
            RemainingContentBytes.ToString(CultureInfo.InvariantCulture),
            MaximumContentResponseBytes.ToString(CultureInfo.InvariantCulture),
            MaximumControlResponseBytes.ToString(CultureInfo.InvariantCulture),
            MaximumRequests.ToString(CultureInfo.InvariantCulture),
            MaximumTokenExchanges.ToString(CultureInfo.InvariantCulture),
            MaximumRedirects.ToString(CultureInfo.InvariantCulture),
            QuarantineAndReacquire.ToString(CultureInfo.InvariantCulture),
            RequiredAvailableBytes.ToString(CultureInfo.InvariantCulture), Blocker?.ToString() ?? "",
            string.Join(",", RequiredConsentScopes));
    }
}

public sealed class ArtifactImageAcquisitionPreview
{
    public ArtifactImageAcquisitionPlan Plan { get; }
    public ArtifactAcquisitionPreviewState State { get; }
    public ArtifactAcquisitionFailure? Failure => Plan.Blocker;
    public ArtifactAcquisitionRemedy? Remedy => Failure is { } failure ? ArtifactAcquisitionRemedies.For(failure) : null;
    public bool CanApprove => Plan.Disposition == ArtifactAcquisitionDisposition.Reviewable;

    internal ArtifactImageAcquisitionPreview(ArtifactImageAcquisitionPlan plan, ArtifactAcquisitionPreviewState state)
    {
        Plan = plan;
        State = CanApprove ? state : ArtifactAcquisitionPreviewState.Blocked;
    }

    public ArtifactImageAcquisitionApproval Approve(ArtifactAcquisitionDecision decision = ArtifactAcquisitionDecision.No,
        IEnumerable<SetupConsentScope>? approvedScopes = null)
    {
        ArtifactAcquisitionFailure? failure = null;
        if (decision != ArtifactAcquisitionDecision.Approve) failure = ArtifactAcquisitionFailure.ConsentRequired;
        else if (!CanApprove) failure = Failure ?? ArtifactAcquisitionFailure.InvalidPlan;
        var scopes = (approvedScopes ?? []).Take(4).Order().ToImmutableArray();
        if (failure is null && !scopes.SequenceEqual(Plan.RequiredConsentScopes))
            failure = ArtifactAcquisitionFailure.ConsentScopeMismatch;
        return new(Plan.Fingerprint, failure, scopes);
    }
}

public sealed class ArtifactImageAcquisitionApproval
{
    private int consumed;
    internal string PlanFingerprint { get; }
    public bool IsApproved => Failure is null;
    public ArtifactAcquisitionFailure? Failure { get; }
    public ImmutableArray<SetupConsentScope> ApprovedScopes { get; }
    internal ArtifactImageAcquisitionApproval(string fingerprint, ArtifactAcquisitionFailure? failure,
        ImmutableArray<SetupConsentScope> scopes)
    {
        PlanFingerprint = fingerprint;
        Failure = failure;
        ApprovedScopes = scopes;
    }
    internal bool TryConsume() => Interlocked.Exchange(ref consumed, 1) == 0;
}

public sealed record ArtifactImageAcquisitionReceipt(string ImageId, string ManifestDigest,
    string ManifestMediaType, string? VerifiedUpstreamIndexDigest, string Platform,
    string ConfigurationDigest, ImmutableArray<string> OrderedLayerDigests);

public sealed class ArtifactImageAcquisitionResult
{
    public ArtifactAcquisitionRunState State { get; }
    public ArtifactAcquisitionFailure? Failure { get; }
    public ArtifactAcquisitionRemedy? Remedy => Failure is { } failure ? ArtifactAcquisitionRemedies.For(failure) : null;
    public string? LayoutPath { get; }
    public ImmutableArray<ArtifactImageAcquisitionReceipt> Images { get; }
    public long PersistedContentBytes { get; }
    public bool Resumed { get; }
    public bool PayloadVerified => State is ArtifactAcquisitionRunState.Acquired or ArtifactAcquisitionRunState.AlreadyAcquired;
    public bool Published => PayloadVerified;
    public bool RuntimeEnabled => false;
    public bool HostReady => false;
    public bool ExecutionAuthorized => false;
    public bool AllUpstreamPlatformsAcquired => false;
    public bool UncompressedDiffIdsVerified => false;
    public bool CompleteRuntimeAcquired => false;

    internal ArtifactImageAcquisitionResult(ArtifactAcquisitionRunState state, ArtifactAcquisitionFailure? failure,
        string? layoutPath, ImmutableArray<ArtifactImageAcquisitionReceipt> images, long bytes, bool resumed)
    {
        State = state;
        Failure = failure;
        LayoutPath = layoutPath;
        Images = images;
        PersistedContentBytes = bytes;
        Resumed = resumed;
    }
}
