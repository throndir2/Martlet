using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using Martlet.HostArtifacts;

namespace Martlet.Host.Setup;

public sealed class ArtifactAcquisitionCoordinator : IDisposable
{
    public static readonly TimeSpan DefaultPlanLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DefaultTransferTimeout = TimeSpan.FromMinutes(30);
    public const long DefaultFreeSpaceReserveBytes = 64L * 1024 * 1024;
    private const int BufferBytes = 65_536;
    private const long JournalCheckpointBytes = 8L * 1024 * 1024;

    private readonly ISetupFileSystem setupFileSystem;
    private readonly IArtifactAcquisitionMutationStorage storage;
    private readonly IArtifactDownloadTransport transport;
    private readonly IArtifactAcquisitionProgressSink progress;
    private readonly TimeProvider clock;
    private readonly TimeSpan planLifetime;
    private readonly TimeSpan transferTimeout;
    private readonly long freeSpaceReserveBytes;
    private readonly bool ownsTransport;

    public ArtifactAcquisitionCoordinator(
        ISetupFileSystem setupFileSystem,
        LocalArtifactAcquisitionStorage storage,
        TimeProvider? clock = null)
        : this(setupFileSystem, storage, new HttpsArtifactDownloadTransport(),
            clock: clock)
    {
        ownsTransport = true;
    }

    internal ArtifactAcquisitionCoordinator(
        ISetupFileSystem setupFileSystem,
        IArtifactAcquisitionMutationStorage storage,
        IArtifactDownloadTransport? transport = null,
        IArtifactAcquisitionProgressSink? progress = null,
        TimeProvider? clock = null,
        TimeSpan? planLifetime = null,
        TimeSpan? transferTimeout = null,
        long freeSpaceReserveBytes = DefaultFreeSpaceReserveBytes)
        : this(
            setupFileSystem,
            storage,
            transport,
            progress,
            clock,
            planLifetime,
            transferTimeout,
            freeSpaceReserveBytes,
            ownsTransport: false)
    {
    }

    private ArtifactAcquisitionCoordinator(
        ISetupFileSystem setupFileSystem,
        IArtifactAcquisitionMutationStorage storage,
        IArtifactDownloadTransport? transport,
        IArtifactAcquisitionProgressSink? progress,
        TimeProvider? clock,
        TimeSpan? planLifetime,
        TimeSpan? transferTimeout,
        long freeSpaceReserveBytes,
        bool ownsTransport)
    {
        this.setupFileSystem =
            setupFileSystem ?? throw new ArgumentNullException(nameof(setupFileSystem));
        this.storage =
            storage ?? throw new ArgumentNullException(nameof(storage));
        this.transport = transport ?? new RefusingArtifactDownloadTransport();
        this.progress = progress ?? new NullArtifactAcquisitionProgressSink();
        this.clock = clock ?? TimeProvider.System;
        this.planLifetime = planLifetime ?? DefaultPlanLifetime;
        this.transferTimeout = transferTimeout ?? DefaultTransferTimeout;
        this.freeSpaceReserveBytes = freeSpaceReserveBytes;
        this.ownsTransport = ownsTransport;
        AcquisitionGuard.Require(
            this.planLifetime > TimeSpan.Zero &&
            this.planLifetime <= TimeSpan.FromMinutes(30) &&
            this.transferTimeout > TimeSpan.Zero &&
            this.transferTimeout <= TimeSpan.FromHours(24) &&
            freeSpaceReserveBytes is >= 0 and <= 1_099_511_627_776L,
            ArtifactAcquisitionFailure.InvalidPlan);
    }

    internal static ArtifactAcquisitionCoordinator CreateForFixture(
        ISetupFileSystem setupFileSystem,
        IArtifactAcquisitionMutationStorage storage,
        IArtifactDownloadTransport? transport = null,
        IArtifactAcquisitionProgressSink? progress = null,
        TimeProvider? clock = null,
        TimeSpan? planLifetime = null,
        TimeSpan? transferTimeout = null,
        long freeSpaceReserveBytes = DefaultFreeSpaceReserveBytes) =>
        new(
            setupFileSystem,
            storage,
            transport,
            progress,
            clock,
            planLifetime,
            transferTimeout,
            freeSpaceReserveBytes,
            ownsTransport: false);

    public ValueTask<ArtifactAcquisitionPreview> PreviewAsync(
        ArtifactManifest manifest,
        string artifactId,
        SetupPlan setupPlan,
        SetupPreview setupPreview,
        ArtifactRightsAuthorization rightsAuthorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(setupPlan);
        var selection = manifest.DescribeAcquisition(
            setupPlan.ExpectedResources.Where(resource =>
                resource.Kind == SetupExpectedResourceKind.RoleArtifactSet).Select(resource => resource.Value),
            "ubuntu-24.04-x64", manifest.FormatVersion == 2 ? "linux/amd64" : null);
        return PreviewAsync(selection, artifactId, setupPlan, setupPreview, rightsAuthorization, cancellationToken);
    }

    public async ValueTask<ArtifactAcquisitionPreview> PreviewAsync(
        ArtifactAcquisitionSelection selection,
        string artifactId,
        SetupPlan setupPlan,
        SetupPreview setupPreview,
        ArtifactRightsAuthorization rightsAuthorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(setupPlan);
        ArgumentNullException.ThrowIfNull(setupPreview);
        ArgumentNullException.ThrowIfNull(rightsAuthorization);
        cancellationToken.ThrowIfCancellationRequested();

        var candidate = selection.Artifacts.SingleOrDefault(artifact => artifact.ArtifactId == artifactId)
            ?? throw new ArtifactAcquisitionException(selection.Images.Any(image => image.Id == artifactId)
                ? ArtifactAcquisitionFailure.ProviderUnsupported : ArtifactAcquisitionFailure.ArtifactNotSelected);
        var paths = storage.GetPaths(candidate);
        ValidateSetupBoundary(setupPlan, setupPreview, candidate, paths);
        var selectedRoles = setupPlan.ExpectedResources.Where(resource =>
            resource.Kind == SetupExpectedResourceKind.RoleArtifactSet).Select(resource => resource.Value).Order(StringComparer.Ordinal);
        if (!selectedRoles.SequenceEqual(selection.RoleIds) || selection.DeclaredMismatch ||
            selection.Target != "ubuntu-24.04-x64" ||
            selection.FormatVersion == 2 && selection.Platform != "linux/amd64")
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.SetupPlanChanged);
        var setupJournal = await ReadCurrentSetupJournalAsync(
            setupPlan,
            setupPreview,
            cancellationToken).ConfigureAwait(false);
        var setupJournalId = SetupJournalCodec.Read(setupJournal.Content).JournalId;
        var storageSnapshot = await storage.InspectAsync(
            paths,
            cancellationToken).ConfigureAwait(false);
        var journal = ReadAcquisitionJournal(storageSnapshot);
        if (journal is not null)
        {
            ValidateJournalBinding(
                journal.Document,
                candidate,
                setupPlan,
                setupPreview,
                rightsAuthorization,
                paths);
            if (journal.Document.SetupJournalId != setupJournalId ||
                journal.Document.SelectionFingerprint != selection.Fingerprint ||
                journal.Document.SetupJournalVersion != setupPreview.JournalVersion && journal.Document.Supersessions >= 16)
                throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.SetupJournalChanged);
        }

        var now = clock.GetUtcNow();
        var setupExpired = now < setupPlan.CreatedAtUtc ||
            now >= setupPlan.ExpiresAtUtc;
        var expiresAt = now + planLifetime;
        if (!setupExpired && setupPlan.ExpiresAtUtc < expiresAt)
            expiresAt = setupPlan.ExpiresAtUtc;
        if (rightsAuthorization.IsApproved && rightsAuthorization.ExpiresAtUtc < expiresAt)
            expiresAt = rightsAuthorization.ExpiresAtUtc;
        if (expiresAt <= now)
            expiresAt = now.AddTicks(1);

        var (state, consistencyFailure) =
            await EvaluateStorageAsync(
                candidate,
                paths,
                storageSnapshot,
                journal,
                cancellationToken).ConfigureAwait(false);
        var recoverCorruptFinal = state == ArtifactAcquisitionPreviewState.RecoveryRequired;
        string? corruptFinalHash = null;
        if (recoverCorruptFinal && storageSnapshot.FinalExists)
            corruptFinalHash = await storage.ComputeFinalSha256Async(paths, storageSnapshot.FinalBytes, cancellationToken)
                .ConfigureAwait(false);
        var remainingBytes = state == ArtifactAcquisitionPreviewState.Completed
            ? 0
            : checked(candidate.ExpectedBytes - storageSnapshot.PartialBytes);
        var requiredAvailableBytes = state == ArtifactAcquisitionPreviewState.Completed
            ? 0
            : checked(remainingBytes + freeSpaceReserveBytes);

        ArtifactAcquisitionFailure? blocker = null;
        if (setupExpired)
            blocker = ArtifactAcquisitionFailure.PlanStale;
        else if (!setupPreview.IsLocalReview || setupPlan.InstallationMachine is null ||
            setupPreview.Failure is not null || setupPreview.State != SetupPreviewState.ReviewRecorded)
            blocker = ArtifactAcquisitionFailure.SetupPlanBlocked;
        else if (candidate.ExpectedSha256 is null)
            blocker = ArtifactAcquisitionFailure.ContentPinMissing;
        else if (candidate.Source.Provider != ArtifactAcquisitionProvider.GithubReleaseAsset)
            blocker = ArtifactAcquisitionFailure.ProviderUnsupported;
        else if (!rightsAuthorization.IsApproved ||
            rightsAuthorization.SelectionFingerprint != selection.Fingerprint ||
            now < rightsAuthorization.CreatedAtUtc || now >= rightsAuthorization.ExpiresAtUtc ||
            rightsAuthorization.ArtifactIdentityFingerprint !=
                candidate.IdentityFingerprint)
            blocker = ArtifactAcquisitionFailure.RightsNotApproved;
        else if (storageSnapshot.JournalPendingExists || storageSnapshot.LeaseExists)
            blocker = ArtifactAcquisitionFailure.JournalChanged;
        else if (consistencyFailure is not null)
            blocker = consistencyFailure;
        else if (storageSnapshot.AvailableBytes < requiredAvailableBytes)
            blocker = ArtifactAcquisitionFailure.FreeSpaceInsufficient;

        var disposition = blocker is null
            ? ArtifactAcquisitionDisposition.Reviewable
            : ArtifactAcquisitionDisposition.Blocked;
        var plan = new ArtifactAcquisitionPlan(
            now,
            expiresAt,
            disposition,
            blocker,
            candidate,
            rightsAuthorization,
            setupPlan,
            setupPreview,
            paths,
            storageSnapshot,
            journal,
            requiredAvailableBytes,
            transferTimeout, selection, setupJournalId, recoverCorruptFinal, corruptFinalHash);
        if (state == ArtifactAcquisitionPreviewState.Completed && blocker is null)
            return new(plan, state, canApprove: false, failure: null);
        return new(
            plan,
            blocker is null ? state : ArtifactAcquisitionPreviewState.Blocked,
            canApprove: blocker is null,
            blocker);
    }

    public async ValueTask<ArtifactAcquisitionResult> RunAsync(
        ArtifactAcquisitionPlan plan,
        ArtifactAcquisitionApproval approval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(approval);
        if (!approval.IsApproved)
            return Result(
                ArtifactAcquisitionRunState.Refused,
                approval.Failure ?? ArtifactAcquisitionFailure.ConsentRequired,
                plan);
        if (!approval.TryConsume())
            return Result(
                ArtifactAcquisitionRunState.Refused,
                ArtifactAcquisitionFailure.ConsentConsumed,
                plan);
        if (plan.Disposition != ArtifactAcquisitionDisposition.Reviewable)
            return Result(
                ArtifactAcquisitionRunState.Refused,
                plan.Blocker ?? ArtifactAcquisitionFailure.InvalidPlan,
                plan);
        if (!string.Equals(
                approval.PlanFingerprint,
                plan.Fingerprint,
                StringComparison.Ordinal))
            return Result(
                ArtifactAcquisitionRunState.Refused,
                ArtifactAcquisitionFailure.PlanChanged,
                plan);
        if (!approval.ApprovedScopes.SequenceEqual(plan.RequiredConsentScopes))
            return Result(
                ArtifactAcquisitionRunState.Refused,
                ArtifactAcquisitionFailure.ConsentScopeMismatch,
                plan);
        var now = clock.GetUtcNow();
        if (now < plan.CreatedAtUtc || now >= plan.ExpiresAtUtc)
            return Result(
                ArtifactAcquisitionRunState.Refused,
                ArtifactAcquisitionFailure.PlanStale,
                plan);

        var setupSnapshot = await setupFileSystem.ReadAsync(
            SetupJournalCodec.MaximumBytes,
            cancellationToken).ConfigureAwait(false);
        if (setupSnapshot is null ||
            !string.Equals(
                setupFileSystem.JournalPath,
                plan.SetupJournalPath,
                StringComparison.Ordinal) ||
            !string.Equals(
                setupSnapshot.Version,
                plan.SetupJournalVersion,
                StringComparison.Ordinal))
            return Result(
                ArtifactAcquisitionRunState.Refused,
                ArtifactAcquisitionFailure.SetupJournalChanged,
                plan);
        ValidateSetupDocument(
            SetupJournalCodec.Read(setupSnapshot.Content),
            plan);

        var paths = new ArtifactAcquisitionPaths(
            plan.ArtifactRootPath,
            plan.DestinationPath,
            plan.PartialPath,
            plan.JournalPath);
        await RequireSetupCurrentAsync(plan, cancellationToken).ConfigureAwait(false);
        await using var lease = await storage.AcquireLeaseAsync(paths, Guid.NewGuid(), cancellationToken)
            .ConfigureAwait(false);
        var currentStorage = await storage.InspectAsync(
            paths,
            cancellationToken).ConfigureAwait(false);
        if (currentStorage.JournalPendingExists ||
            !string.Equals(
                currentStorage.Fingerprint,
                plan.StorageFactsFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                currentStorage.JournalVersion,
                approval.AcquisitionJournalVersion,
                StringComparison.Ordinal))
            return Result(
                ArtifactAcquisitionRunState.Refused,
                ArtifactAcquisitionFailure.JournalChanged,
                plan);
        if (currentStorage.AvailableBytes < plan.RequiredAvailableBytes)
            return Result(
                ArtifactAcquisitionRunState.Refused,
                ArtifactAcquisitionFailure.FreeSpaceInsufficient,
                plan);
        await RequireSetupCurrentAsync(plan, cancellationToken).ConfigureAwait(false);

        var journal = ReadAcquisitionJournal(currentStorage);
        if (journal is not null)
            ValidateJournalForPlan(journal.Document, plan, allowReviewSupersession: true);
        var document = journal?.Document ??
            ArtifactAcquisitionJournalDocument.Create(plan, now);
        var journalVersion = currentStorage.JournalVersion;
        var cursor = new RunCursor(document, journalVersion);
        if (journal is not null && document.SetupJournalVersion != plan.SetupJournalVersion)
        {
            if (document.Supersessions >= 16)
                throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.SetupJournalChanged);
            document = document with
            {
                PlanFingerprint = plan.Fingerprint,
                SetupPlanFingerprint = plan.SetupPlanFingerprint,
                HostFactsFingerprint = plan.HostFactsFingerprint,
                SetupJournalVersion = plan.SetupJournalVersion,
                SetupJournalRevision = plan.SetupJournalRevision,
                Supersessions = document.Supersessions + 1
            };
            (document, journalVersion) = await PersistAsync(paths, document, journalVersion, cancellationToken, cursor)
                .ConfigureAwait(false);
        }

        if (document.State == ArtifactAcquisitionJournalState.Quarantining &&
            currentStorage.QuarantineExists && !currentStorage.FinalExists &&
            currentStorage.QuarantineIdentity == document.FinalIdentity)
        {
            document = document with
            {
                State = ArtifactAcquisitionJournalState.Quarantined,
                QuarantineIdentity = currentStorage.QuarantineIdentity,
                QuarantineBytes = currentStorage.QuarantineBytes,
                FinalOwned = false,
                FinalIdentity = null,
                PersistedBytes = 0,
                ComputedSha256 = null
            };
            (document, journalVersion) = await PersistAsync(paths, document, journalVersion, cancellationToken, cursor)
                .ConfigureAwait(false);
        }
        if (plan.RecoverCorruptFinal && currentStorage.FinalExists)
        {
            var ownedFinalIdentity = document.FinalIdentity ??
                (document.State == ArtifactAcquisitionJournalState.Verified ? document.PartialIdentity : null);
            if (ownedFinalIdentity != currentStorage.FinalIdentity || plan.FinalIdentity != currentStorage.FinalIdentity ||
                plan.CorruptFinalSha256 is null || currentStorage.QuarantineExists)
                throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.FinalConflict);
            document = document with
            {
                PlanFingerprint = plan.Fingerprint,
                State = ArtifactAcquisitionJournalState.Quarantining,
                PartialOwned = false,
                PartialIdentity = null,
                FinalOwned = true,
                FinalIdentity = ownedFinalIdentity,
                ComputedSha256 = plan.CorruptFinalSha256,
                LastFailure = null
            };
            (document, journalVersion) = await PersistAsync(paths, document, journalVersion, cancellationToken, cursor)
                .ConfigureAwait(false);
            var quarantined = await storage.QuarantineOwnedFinalAsync(paths, plan.FinalIdentity!,
                plan.FinalBytes, plan.CorruptFinalSha256, cancellationToken).ConfigureAwait(false);
            document = document with
            {
                State = ArtifactAcquisitionJournalState.Quarantined,
                QuarantineIdentity = quarantined.Identity,
                QuarantineBytes = quarantined.Bytes,
                FinalOwned = false,
                FinalIdentity = null,
                PersistedBytes = 0,
                ComputedSha256 = null
            };
            (document, journalVersion) = await PersistAsync(paths, document, journalVersion, cancellationToken, cursor)
                .ConfigureAwait(false);
            currentStorage = await storage.InspectAsync(paths, cancellationToken).ConfigureAwait(false);
        }

        if (journal is null)
        {
            (document, journalVersion) = await PersistAsync(
                paths,
                document,
                journalVersion,
                cancellationToken, cursor).ConfigureAwait(false);
        }
        else if (document.State == ArtifactAcquisitionJournalState.Finalized)
        {
            var finalHash = await storage.ComputeFinalSha256Async(
                paths,
                plan.Candidate.ExpectedBytes,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                finalHash,
                plan.Candidate.ExpectedSha256,
                StringComparison.Ordinal))
                return Result(
                    ArtifactAcquisitionRunState.Blocked,
                    ArtifactAcquisitionFailure.FinalConflict,
                    plan,
                    document.PersistedBytes,
                    document.PersistedBytes > 0,
                    cleanupPerformed: false,
                    verifiedSha256: finalHash);
            return Result(
                ArtifactAcquisitionRunState.AlreadyAcquired,
                null,
                plan,
                document.PersistedBytes,
                document.PersistedBytes > 0,
                payloadVerified: true,
                finalized: true,
                cleanupPerformed: false,
                verifiedSha256: finalHash);
        }
        else if (document.State == ArtifactAcquisitionJournalState.Verified &&
            currentStorage.FinalExists &&
            !currentStorage.PartialExists)
        {
            var finalHash = await storage.ComputeFinalSha256Async(
                paths,
                plan.Candidate.ExpectedBytes,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                finalHash,
                plan.Candidate.ExpectedSha256,
                StringComparison.Ordinal))
                return Result(
                    ArtifactAcquisitionRunState.Blocked,
                    ArtifactAcquisitionFailure.FinalConflict,
                    plan,
                    document.PersistedBytes,
                    resumed: true,
                    cleanupPerformed: false,
                    verifiedSha256: finalHash);
            document = document with
            {
                PlanFingerprint = plan.Fingerprint,
                State = ArtifactAcquisitionJournalState.Finalized,
                PartialOwned = false,
                FinalIdentity = document.PartialIdentity,
                PartialIdentity = null,
                FinalOwned = true,
                ComputedSha256 = finalHash,
                LastFailure = null
            };
            (document, _) = await PersistAsync(
                paths,
                document,
                journalVersion,
                cancellationToken, cursor).ConfigureAwait(false);
            await ReportAsync(
                plan,
                ArtifactAcquisitionProgressPhase.Completed,
                document.PersistedBytes,
                resumed: true,
                cancellationToken).ConfigureAwait(false);
            return Result(
                ArtifactAcquisitionRunState.AlreadyAcquired,
                null,
                plan,
                document.PersistedBytes,
                resumed: true,
                payloadVerified: true,
                finalized: true,
                cleanupPerformed: false,
                verifiedSha256: finalHash);
        }

        if (document.State is ArtifactAcquisitionJournalState.Failed or ArtifactAcquisitionJournalState.Quarantined &&
            !currentStorage.PartialExists &&
            !currentStorage.FinalExists)
        {
            document = document with
            {
                PlanFingerprint = plan.Fingerprint,
                State = ArtifactAcquisitionJournalState.Prepared,
                PersistedBytes = 0,
                EntityTag = null,
                LastModifiedUtc = null,
                RangeSupported = false,
                PartialOwned = false,
                PartialIdentity = null,
                FinalOwned = false,
                FinalIdentity = null,
                ComputedSha256 = null,
                LastFailure = null
            };
            (document, journalVersion) = await PersistAsync(
                paths,
                document,
                journalVersion,
                cancellationToken, cursor).ConfigureAwait(false);
        }
        else if (!string.Equals(
            document.PlanFingerprint,
            plan.Fingerprint,
            StringComparison.Ordinal))
        {
            document = document with { PlanFingerprint = plan.Fingerprint };
            (document, journalVersion) = await PersistAsync(
                paths,
                document,
                journalVersion,
                cancellationToken, cursor).ConfigureAwait(false);
        }

        var offset = document.PersistedBytes;
        var resumed = offset > 0;
        if (offset == plan.Candidate.ExpectedBytes)
            return await VerifyAndFinalizeAsync(
                plan,
                paths,
                document,
                journalVersion!,
                resumed: true,
                cancellationToken, cursor).ConfigureAwait(false);

        using var timeout = new CancellationTokenSource(
            plan.TransferTimeout,
            clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        ArtifactDownloadResponse? download = null;
        try
        {
            await ReportAsync(
                plan,
                ArtifactAcquisitionProgressPhase.Connecting,
                offset,
                resumed,
                cancellationToken).ConfigureAwait(false);
            await RequireSetupCurrentAsync(plan, cancellationToken).ConfigureAwait(false);
            download = await transport.SendAsync(
                new ArtifactDownloadRequest(
                    plan.Source,
                    offset,
                    offset > 0 ? document.EntityTag : null,
                    offset > 0 ? document.LastModifiedUtc : null,
                    async token =>
                    {
                        await RequireSetupCurrentAsync(plan, token).ConfigureAwait(false);
                        await storage.ValidateLeaseAsync(paths, token).ConfigureAwait(false);
                        RequireCurrentOwner(await storage.InspectAsync(paths, token).ConfigureAwait(false), cursor);
                    }),
                linked.Token).ConfigureAwait(false);
            var response = download.Response;
            if (download.SourceIdentityFingerprint != plan.Source.IdentityFingerprint ||
                download.LogicalSourceUrl != plan.Source.LogicalSourceUrl)
                throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.SourceChanged);
            if (offset > 0 && document.TerminalOrigin != download.TerminalOrigin)
                throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.SourceChanged);
            var responseIdentity = ValidateResponse(
                response,
                plan,
                document,
                offset);
            document = document with
            {
                State = ArtifactAcquisitionJournalState.Downloading,
                TerminalOrigin = download.TerminalOrigin,
                EntityTag = responseIdentity.EntityTag,
                LastModifiedUtc = responseIdentity.LastModifiedUtc,
                RangeSupported = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent,
                LastFailure = null,
                ComputedSha256 = null,
                FinalOwned = false
            };
            (document, journalVersion) = await PersistAsync(
                paths,
                document,
                journalVersion,
                linked.Token, cursor).ConfigureAwait(false);

            await using (var body = await response.Content.ReadAsStreamAsync(
                linked.Token).ConfigureAwait(false))
            {
                await using var writer = await storage.OpenPartialAsync(
                    paths,
                    offset,
                    document.PartialIdentity,
                    linked.Token).ConfigureAwait(false);
                document = document with { PartialOwned = true, PartialIdentity = writer.Identity };
                (document, journalVersion) = await PersistAsync(
                    paths,
                    document,
                    journalVersion,
                    linked.Token, cursor).ConfigureAwait(false);
                var transferred = await TransferAsync(
                    plan,
                    paths,
                    body,
                    writer,
                    document,
                    journalVersion,
                    resumed,
                    linked.Token, cursor).ConfigureAwait(false);
                document = transferred.Document;
                journalVersion = transferred.Version;
            }
            return await VerifyAndFinalizeAsync(
                plan,
                paths,
                document,
                journalVersion,
                resumed,
                linked.Token, cursor).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var failure = cancellationToken.IsCancellationRequested
                ? ArtifactAcquisitionFailure.Canceled
                : ArtifactAcquisitionFailure.Timeout;
            var interrupted = await PersistRunFailureAsync(
                plan,
                paths,
                document,
                failure,
                resumed, cursor).ConfigureAwait(false);
            if (interrupted.State != ArtifactAcquisitionRunState.Acquired)
                await ReportAsync(
                    plan,
                    ArtifactAcquisitionProgressPhase.Interrupted,
                    interrupted.PersistedBytes,
                    resumed,
                    CancellationToken.None).ConfigureAwait(false);
            return interrupted;
        }
        catch (ArtifactAcquisitionException error) when (
            error.Failure is ArtifactAcquisitionFailure.SourceChanged or
                ArtifactAcquisitionFailure.SizeExceeded)
        {
            var current = await storage.InspectAsync(
                paths,
                CancellationToken.None).ConfigureAwait(false);
            RequireCurrentOwner(current, cursor);
            document = cursor.Document;
            var cleanup = current.PartialExists && document.PartialOwned;
            if (cleanup)
                await storage.DeleteOwnedPartialAsync(
                    paths,
                    document.PartialIdentity!,
                    current.PartialBytes,
                    CancellationToken.None).ConfigureAwait(false);
            document = document with
            {
                State = ArtifactAcquisitionJournalState.Failed,
                PersistedBytes = cleanup ? 0 : document.PersistedBytes,
                PartialOwned = cleanup ? false : document.PartialOwned,
                PartialIdentity = cleanup ? null : document.PartialIdentity,
                FinalOwned = false,
                LastFailure = error.Failure
            };
            (document, _) = await PersistAsync(
                paths,
                document,
                current.JournalVersion,
                CancellationToken.None, cursor).ConfigureAwait(false);
            return Result(
                ArtifactAcquisitionRunState.Blocked,
                error.Failure,
                plan,
                document.PersistedBytes,
                resumed,
                cleanupPerformed: cleanup);
        }
        catch (ArtifactAcquisitionException error) when (
            error.Failure is ArtifactAcquisitionFailure.JournalCorrupt or
                ArtifactAcquisitionFailure.JournalTooLarge or
                ArtifactAcquisitionFailure.JournalChanged or
                ArtifactAcquisitionFailure.JournalIoFailure)
        {
            throw;
        }
        catch (ArtifactAcquisitionException error)
        {
            return await PersistRunFailureAsync(
                plan,
                paths,
                document,
                error.Failure,
                resumed, cursor).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or HttpRequestException)
        {
            return await PersistRunFailureAsync(
                plan,
                paths,
                document,
                ArtifactAcquisitionFailure.TransportFailed,
                resumed, cursor).ConfigureAwait(false);
        }
        finally
        {
            download?.Dispose();
        }
    }

    private async ValueTask<(ArtifactAcquisitionJournalDocument Document, string Version)>
        TransferAsync(
            ArtifactAcquisitionPlan plan,
            ArtifactAcquisitionPaths paths,
            Stream body,
            IArtifactPartialWriter writer,
            ArtifactAcquisitionJournalDocument document,
            string version,
            bool resumed,
            CancellationToken cancellationToken,
            RunCursor cursor)
    {
        var buffer = new byte[BufferBytes];
        var checkpoint = writer.Position;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RequireSetupCurrentAsync(plan, cancellationToken).ConfigureAwait(false);
                var remaining = plan.Candidate.ExpectedBytes - writer.Position;
                var maximumRead = (int)Math.Min(
                    buffer.Length,
                    checked(remaining + 1));
                int read;
                try
                {
                    read = await body.ReadAsync(
                        buffer.AsMemory(0, maximumRead),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error) when (error is IOException or HttpRequestException)
                {
                    throw new ArtifactAcquisitionException(
                        ArtifactAcquisitionFailure.TransportFailed,
                        error);
                }
                if (read == 0)
                    break;
                if (read > remaining)
                    throw new ArtifactAcquisitionException(
                        ArtifactAcquisitionFailure.SizeExceeded);
                await writer.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);
                await ReportAsync(
                    plan,
                    ArtifactAcquisitionProgressPhase.Downloading,
                    writer.Position,
                    resumed,
                    cancellationToken).ConfigureAwait(false);
                if (writer.Position - checkpoint >= JournalCheckpointBytes)
                {
                    await writer.FlushToDiskAsync(cancellationToken)
                        .ConfigureAwait(false);
                    document = document with
                    {
                        PersistedBytes = writer.Position,
                        PartialOwned = true
                    };
                    (document, version) = await PersistAsync(
                        paths,
                        document,
                        version,
                        cancellationToken, cursor).ConfigureAwait(false);
                    checkpoint = writer.Position;
                }
            }
            await writer.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);
            document = document with
            {
                PersistedBytes = writer.Position,
                PartialOwned = true
            };
            (document, version) = await PersistAsync(
                paths,
                document,
                version,
                cancellationToken, cursor).ConfigureAwait(false);
            if (writer.Position != plan.Candidate.ExpectedBytes)
                throw new ArtifactAcquisitionException(
                    ArtifactAcquisitionFailure.ContentLengthMismatch);
            return (document, version);
        }
        catch (OperationCanceledException)
        {
            await writer.FlushToDiskAsync(CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (ArtifactAcquisitionException)
        {
            await writer.FlushToDiskAsync(CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<ArtifactAcquisitionResult> PersistRunFailureAsync(
        ArtifactAcquisitionPlan plan,
        ArtifactAcquisitionPaths paths,
        ArtifactAcquisitionJournalDocument document,
        ArtifactAcquisitionFailure failure,
        bool resumed,
        RunCursor cursor)
    {
        var current = await storage.InspectAsync(
            paths,
            CancellationToken.None).ConfigureAwait(false);
        RequireCurrentOwner(current, cursor);
        document = cursor.Document;
        if (document.State == ArtifactAcquisitionJournalState.Finalized &&
            current.FinalExists &&
            !current.PartialExists)
        {
            var finalHash = await storage.ComputeFinalSha256Async(
                paths,
                plan.Candidate.ExpectedBytes,
                CancellationToken.None).ConfigureAwait(false);
            if (!string.Equals(
                finalHash,
                plan.Candidate.ExpectedSha256,
                StringComparison.Ordinal))
                return Result(
                    ArtifactAcquisitionRunState.Blocked,
                    ArtifactAcquisitionFailure.FinalConflict,
                    plan,
                    current.FinalBytes,
                    resumed,
                    cleanupPerformed: false,
                    verifiedSha256: finalHash);
            return Result(
                ArtifactAcquisitionRunState.Acquired,
                failure,
                plan,
                current.FinalBytes,
                resumed,
                payloadVerified: true,
                finalized: true,
                cleanupPerformed: false,
                verifiedSha256: finalHash);
        }
        if (document.State == ArtifactAcquisitionJournalState.Verified)
        {
            if (current.PartialExists && !current.FinalExists)
                return Result(
                    ArtifactAcquisitionRunState.Interrupted,
                    failure,
                    plan,
                    document.PersistedBytes,
                    resumed,
                    payloadVerified: true,
                    finalized: false,
                    cleanupPerformed: false,
                    verifiedSha256: document.ComputedSha256);
            if (current.FinalExists && !current.PartialExists)
            {
                var finalHash = await storage.ComputeFinalSha256Async(
                    paths,
                    plan.Candidate.ExpectedBytes,
                    CancellationToken.None).ConfigureAwait(false);
                if (!string.Equals(
                    finalHash,
                    plan.Candidate.ExpectedSha256,
                    StringComparison.Ordinal))
                    return Result(
                        ArtifactAcquisitionRunState.Blocked,
                        ArtifactAcquisitionFailure.FinalConflict,
                        plan,
                        current.FinalBytes,
                        resumed,
                        payloadVerified: false,
                        finalized: false,
                        cleanupPerformed: false,
                        verifiedSha256: finalHash);
                return Result(
                    ArtifactAcquisitionRunState.Interrupted,
                    failure,
                    plan,
                    document.PersistedBytes,
                    resumed,
                    payloadVerified: true,
                    finalized: false,
                    cleanupPerformed: false,
                    verifiedSha256: finalHash);
            }
        }
        var partialOwned = current.PartialExists && document.PartialOwned &&
            current.PartialIdentity == document.PartialIdentity;
        var resumable = partialOwned &&
            (document.EntityTag is not null ||
             document.LastModifiedUtc is not null);
        document = document with
        {
            State = resumable
                ? ArtifactAcquisitionJournalState.Interrupted
                : ArtifactAcquisitionJournalState.Failed,
            PersistedBytes = partialOwned
                ? current.PartialBytes
                : document.PersistedBytes,
            PartialOwned = partialOwned,
            PartialIdentity = partialOwned ? document.PartialIdentity : null,
            FinalOwned = current.FinalExists && document.FinalOwned,
            LastFailure = failure
        };
        (document, _) = await PersistAsync(
            paths,
            document,
            current.JournalVersion,
            CancellationToken.None, cursor).ConfigureAwait(false);
        var resultState = resumable ||
            failure is ArtifactAcquisitionFailure.Timeout or
                ArtifactAcquisitionFailure.Canceled
            ? ArtifactAcquisitionRunState.Interrupted
            : ArtifactAcquisitionRunState.Blocked;
        return Result(
            resultState,
            failure,
            plan,
            document.PersistedBytes,
            resumed);
    }

    private async ValueTask<ArtifactAcquisitionResult> VerifyAndFinalizeAsync(
        ArtifactAcquisitionPlan plan,
        ArtifactAcquisitionPaths paths,
        ArtifactAcquisitionJournalDocument document,
        string version,
        bool resumed,
        CancellationToken cancellationToken,
        RunCursor cursor)
    {
        await ReportAsync(
            plan,
            ArtifactAcquisitionProgressPhase.Verifying,
            document.PersistedBytes,
            resumed,
            cancellationToken).ConfigureAwait(false);
        await RequireSetupCurrentAsync(plan, cancellationToken).ConfigureAwait(false);
        RequireCurrentOwner(await storage.InspectAsync(paths, cancellationToken).ConfigureAwait(false), cursor);
        var computed = await storage.ComputePartialSha256Async(
            paths,
            plan.Candidate.ExpectedBytes,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
            computed,
            plan.Candidate.ExpectedSha256,
            StringComparison.Ordinal))
        {
            await RequireSetupCurrentAsync(plan, cancellationToken).ConfigureAwait(false);
            RequireCurrentOwner(await storage.InspectAsync(paths, cancellationToken).ConfigureAwait(false), cursor);
            await storage.DeleteOwnedPartialAsync(
                paths,
                document.PartialIdentity!,
                plan.Candidate.ExpectedBytes,
                CancellationToken.None).ConfigureAwait(false);
            document = document with
            {
                State = ArtifactAcquisitionJournalState.Failed,
                PersistedBytes = 0,
                PartialOwned = false,
                PartialIdentity = null,
                FinalOwned = false,
                ComputedSha256 = computed,
                LastFailure = ArtifactAcquisitionFailure.IntegrityMismatch
            };
            (document, _) = await PersistAsync(
                paths,
                document,
                version,
                CancellationToken.None, cursor).ConfigureAwait(false);
            return Result(
                ArtifactAcquisitionRunState.Blocked,
                ArtifactAcquisitionFailure.IntegrityMismatch,
                plan,
                0,
                resumed,
                cleanupPerformed: true,
                verifiedSha256: computed);
        }

        document = document with
        {
            State = ArtifactAcquisitionJournalState.Verified,
            PersistedBytes = plan.Candidate.ExpectedBytes,
            PartialOwned = true,
            FinalOwned = false,
            ComputedSha256 = computed,
            LastFailure = null
        };
        (document, version) = await PersistAsync(
            paths,
            document,
            version,
            cancellationToken, cursor).ConfigureAwait(false);
        await ReportAsync(
            plan,
            ArtifactAcquisitionProgressPhase.Finalizing,
            document.PersistedBytes,
            resumed,
            cancellationToken).ConfigureAwait(false);
        await RequireSetupCurrentAsync(plan, cancellationToken).ConfigureAwait(false);
        RequireCurrentOwner(await storage.InspectAsync(paths, cancellationToken).ConfigureAwait(false), cursor);
        await storage.FinalizeAsync(
            paths,
            document.PartialIdentity!,
            plan.Candidate.ExpectedBytes,
            cancellationToken).ConfigureAwait(false);
        var finalHash = await storage.ComputeFinalSha256Async(
            paths,
            plan.Candidate.ExpectedBytes,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
            finalHash,
            plan.Candidate.ExpectedSha256,
            StringComparison.Ordinal))
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.FinalConflict);
        document = document with
        {
            State = ArtifactAcquisitionJournalState.Finalized,
            PartialOwned = false,
            FinalIdentity = document.PartialIdentity,
            PartialIdentity = null,
            FinalOwned = true,
            ComputedSha256 = finalHash,
            LastFailure = null
        };
        (document, _) = await PersistAsync(
            paths,
            document,
            version,
            cancellationToken, cursor).ConfigureAwait(false);
        await ReportAsync(
            plan,
            ArtifactAcquisitionProgressPhase.Completed,
            document.PersistedBytes,
            resumed,
            cancellationToken).ConfigureAwait(false);
        return Result(
            ArtifactAcquisitionRunState.Acquired,
            null,
            plan,
            document.PersistedBytes,
            resumed,
            payloadVerified: true,
            finalized: true,
            cleanupPerformed: false,
            verifiedSha256: finalHash);
    }

    private ResponseIdentity ValidateResponse(
        HttpResponseMessage response,
        ArtifactAcquisitionPlan plan,
        ArtifactAcquisitionJournalDocument document,
        long offset)
    {
        if ((int)response.StatusCode is >= 300 and <= 399)
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.RedirectRejected);
        var encodings = response.Content.Headers.ContentEncoding.ToArray();
        if (encodings.Length > 1 ||
            encodings.Length == 1 &&
            !string.Equals(
                encodings[0],
                "identity",
                StringComparison.OrdinalIgnoreCase))
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.ResponseInvalid);
        var entityTag = response.Headers.ETag?.ToString();
        if (response.Headers.ETag?.IsWeak == true ||
            response.Headers.ETag?.Tag == "*" ||
            entityTag?.Length > 256)
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.ResponseInvalid);
        var lastModified = response.Content.Headers.LastModified;
        if (entityTag is null && lastModified is null)
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.ResponseInvalid);

        var remaining = plan.Candidate.ExpectedBytes - offset;
        if (offset == 0)
        {
            if (response.StatusCode != HttpStatusCode.OK ||
                response.Content.Headers.ContentRange is not null)
                throw new ArtifactAcquisitionException(
                    ArtifactAcquisitionFailure.ResponseInvalid);
            if (response.Content.Headers.ContentLength != remaining)
                throw new ArtifactAcquisitionException(
                    ArtifactAcquisitionFailure.ContentLengthMismatch);
        }
        else
        {
            var sourceChanged =
                !string.Equals(
                    entityTag,
                    document.EntityTag,
                    StringComparison.Ordinal) ||
                lastModified != document.LastModifiedUtc;
            if (response.StatusCode == HttpStatusCode.OK)
                throw new ArtifactAcquisitionException(
                    sourceChanged
                        ? ArtifactAcquisitionFailure.SourceChanged
                        : ArtifactAcquisitionFailure.RangeUnsupported);
            if (response.StatusCode != HttpStatusCode.PartialContent)
                throw new ArtifactAcquisitionException(
                    ArtifactAcquisitionFailure.ResponseInvalid);
            if (sourceChanged)
                throw new ArtifactAcquisitionException(
                    ArtifactAcquisitionFailure.SourceChanged);
            if (response.Content.Headers.ContentLength != remaining)
                throw new ArtifactAcquisitionException(
                    ArtifactAcquisitionFailure.ContentLengthMismatch);
            var range = response.Content.Headers.ContentRange;
            if (range is null ||
                !string.Equals(
                    range.Unit,
                    "bytes",
                    StringComparison.OrdinalIgnoreCase) ||
                range.From != offset ||
                range.To != plan.Candidate.ExpectedBytes - 1 ||
                range.Length != plan.Candidate.ExpectedBytes)
                throw new ArtifactAcquisitionException(
                    ArtifactAcquisitionFailure.ResumeStateMismatch);
        }
        return new(entityTag, lastModified);
    }

    private async ValueTask<(ArtifactAcquisitionPreviewState State, ArtifactAcquisitionFailure? Failure)>
        EvaluateStorageAsync(
            ArtifactAcquisitionCandidate candidate,
            ArtifactAcquisitionPaths paths,
            ArtifactAcquisitionStorageSnapshot storageSnapshot,
            ArtifactAcquisitionJournalSnapshot? journal,
            CancellationToken cancellationToken)
    {
        if (journal is null)
        {
            if (storageSnapshot.PartialExists)
                return (ArtifactAcquisitionPreviewState.Blocked,
                    ArtifactAcquisitionFailure.PartialConflict);
            if (storageSnapshot.FinalExists)
                return (ArtifactAcquisitionPreviewState.Blocked,
                    ArtifactAcquisitionFailure.FinalConflict);
            return (ArtifactAcquisitionPreviewState.Proposed, null);
        }

        var document = journal.Document;
        if (storageSnapshot.QuarantineIdentity != document.QuarantineIdentity &&
            !(document.State == ArtifactAcquisitionJournalState.Quarantining &&
              storageSnapshot.QuarantineIdentity == document.FinalIdentity && !storageSnapshot.FinalExists))
            return (ArtifactAcquisitionPreviewState.Blocked, ArtifactAcquisitionFailure.FinalConflict);
        if (document.State == ArtifactAcquisitionJournalState.Quarantining &&
            storageSnapshot.QuarantineExists && !storageSnapshot.FinalExists &&
            storageSnapshot.QuarantineIdentity == document.FinalIdentity)
            return (ArtifactAcquisitionPreviewState.RecoveryRequired, null);
        if (storageSnapshot.PartialIdentity != document.PartialIdentity &&
            !(document.State == ArtifactAcquisitionJournalState.Verified && !storageSnapshot.PartialExists &&
              storageSnapshot.FinalIdentity == document.PartialIdentity))
            return (ArtifactAcquisitionPreviewState.Blocked, ArtifactAcquisitionFailure.PartialConflict);
        if (storageSnapshot.FinalIdentity != document.FinalIdentity &&
            !(document.State == ArtifactAcquisitionJournalState.Verified && !storageSnapshot.PartialExists &&
              storageSnapshot.FinalIdentity == document.PartialIdentity))
            return (ArtifactAcquisitionPreviewState.Blocked, ArtifactAcquisitionFailure.FinalConflict);
        var ownedMovedFinal = document.State == ArtifactAcquisitionJournalState.Verified &&
            storageSnapshot.FinalExists && !storageSnapshot.PartialExists &&
            storageSnapshot.FinalIdentity == document.PartialIdentity;
        if (storageSnapshot.FinalExists && !storageSnapshot.PartialExists &&
            (document.FinalOwned && document.State is ArtifactAcquisitionJournalState.Finalized or ArtifactAcquisitionJournalState.Quarantining ||
             ownedMovedFinal))
        {
            if (storageSnapshot.FinalBytes > checked(candidate.ExpectedBytes + freeSpaceReserveBytes))
                return (ArtifactAcquisitionPreviewState.Blocked, ArtifactAcquisitionFailure.FinalConflict);
            var finalHash = await storage.ComputeFinalSha256Async(paths, storageSnapshot.FinalBytes, cancellationToken)
                .ConfigureAwait(false);
            if (storageSnapshot.FinalBytes != candidate.ExpectedBytes || finalHash != candidate.ExpectedSha256)
                return storageSnapshot.QuarantineExists
                    ? (ArtifactAcquisitionPreviewState.Blocked, ArtifactAcquisitionFailure.FinalConflict)
                    : (ArtifactAcquisitionPreviewState.RecoveryRequired, null);
        }
        var finalizedBeforeJournal =
            document.State == ArtifactAcquisitionJournalState.Verified &&
            storageSnapshot.FinalExists &&
            !storageSnapshot.PartialExists;
        if (!finalizedBeforeJournal &&
            (storageSnapshot.PartialExists != document.PartialOwned ||
            storageSnapshot.PartialExists &&
            storageSnapshot.PartialBytes != document.PersistedBytes))
            return (ArtifactAcquisitionPreviewState.Blocked,
                ArtifactAcquisitionFailure.ResumeStateMismatch);
        if (storageSnapshot.FinalExists != document.FinalOwned &&
            !(document.State == ArtifactAcquisitionJournalState.Verified &&
              storageSnapshot.FinalExists &&
              !storageSnapshot.PartialExists))
            return (ArtifactAcquisitionPreviewState.Blocked,
                ArtifactAcquisitionFailure.FinalConflict);
        if (storageSnapshot.FinalExists &&
            storageSnapshot.FinalBytes != candidate.ExpectedBytes)
            return (ArtifactAcquisitionPreviewState.Blocked,
                ArtifactAcquisitionFailure.FinalConflict);
        if (storageSnapshot.PartialExists &&
            storageSnapshot.FinalExists)
            return (ArtifactAcquisitionPreviewState.Blocked,
                ArtifactAcquisitionFailure.FinalConflict);

        if (document.State == ArtifactAcquisitionJournalState.Finalized)
        {
            var hash = await storage.ComputeFinalSha256Async(
                paths,
                candidate.ExpectedBytes,
                cancellationToken).ConfigureAwait(false);
            return string.Equals(
                hash,
                candidate.ExpectedSha256,
                StringComparison.Ordinal)
                ? (ArtifactAcquisitionPreviewState.Completed, null)
                : (ArtifactAcquisitionPreviewState.Blocked,
                    ArtifactAcquisitionFailure.FinalConflict);
        }
        if (document.State == ArtifactAcquisitionJournalState.Verified &&
            storageSnapshot.FinalExists &&
            !storageSnapshot.PartialExists)
            return (ArtifactAcquisitionPreviewState.FinalizationPending, null);
        if (storageSnapshot.PartialExists &&
            storageSnapshot.PartialBytes == candidate.ExpectedBytes)
            return (ArtifactAcquisitionPreviewState.VerificationPending, null);
        if (storageSnapshot.PartialExists)
        {
            if (document.EntityTag is null &&
                document.LastModifiedUtc is null)
                return (ArtifactAcquisitionPreviewState.Blocked,
                    ArtifactAcquisitionFailure.ResumeStateMismatch);
            return (ArtifactAcquisitionPreviewState.ResumeAvailable, null);
        }
        return (ArtifactAcquisitionPreviewState.Proposed, null);
    }

    private async ValueTask<SetupFileSnapshot> ReadCurrentSetupJournalAsync(
        SetupPlan setupPlan,
        SetupPreview setupPreview,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
            setupFileSystem.JournalPath,
            setupPreview.JournalPath,
            StringComparison.Ordinal))
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.SetupJournalChanged);
        var snapshot = await setupFileSystem.ReadAsync(
            SetupJournalCodec.MaximumBytes,
            cancellationToken).ConfigureAwait(false);
        if (snapshot is null ||
            setupPreview.JournalVersion is null ||
            setupPreview.JournalRevision is null)
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.SetupJournalMissing);
        if (!string.Equals(
            snapshot.Version,
            setupPreview.JournalVersion,
            StringComparison.Ordinal))
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.SetupJournalChanged);
        var document = SetupJournalCodec.Read(snapshot.Content);
        if (document.Revision != setupPreview.JournalRevision)
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.SetupJournalChanged);
        ValidateSetupDocument(document, setupPlan);
        return snapshot;
    }

    private static void ValidateSetupDocument(
        SetupJournalDocument document,
        SetupPlan setupPlan)
    {
        RequireLocalReview(document);
        if (!string.Equals(
                document.PlanFingerprint,
                setupPlan.Fingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                document.DesiredStateFingerprint,
                setupPlan.DesiredStateFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                document.HostFactsFingerprint,
                setupPlan.HostFactsFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                document.ArtifactFactsFingerprint,
                setupPlan.ArtifactFactsFingerprint,
                StringComparison.Ordinal))
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.SetupPlanChanged);
    }

    private static void ValidateSetupDocument(
        SetupJournalDocument document,
        ArtifactAcquisitionPlan plan)
    {
        RequireLocalReview(document);
        if (document.Revision != plan.SetupJournalRevision ||
            !string.Equals(
                document.PlanFingerprint,
                plan.SetupPlanFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                document.DesiredStateFingerprint,
                plan.SetupDesiredStateFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                document.HostFactsFingerprint,
                plan.HostFactsFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                document.ArtifactFactsFingerprint,
                plan.ArtifactFactsFingerprint,
                StringComparison.Ordinal))
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.SetupJournalChanged);
    }

    private void ValidateSetupBoundary(
        SetupPlan setupPlan,
        SetupPreview setupPreview,
        ArtifactAcquisitionCandidate candidate,
        ArtifactAcquisitionPaths paths)
    {
        if (!string.Equals(
                setupPlan.Fingerprint,
                setupPreview.PlanFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                setupPlan.ConfigurationFingerprint,
                setupPreview.ConfigurationFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                setupPlan.HostFactsFingerprint,
                setupPreview.HostFactsFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                setupPlan.ArtifactFactsFingerprint,
                setupPreview.ArtifactFactsFingerprint,
                StringComparison.Ordinal))
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.SetupPlanChanged);
        var artifactRoot = setupPlan.ExpectedResources.SingleOrDefault(
            resource => resource.Id == "artifact-directory");
        if (artifactRoot is null ||
            !string.Equals(paths.RootPath, storage.RootPath, PathComparison))
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.DestinationInvalid);
        var manifest = setupPlan.ExpectedResources.SingleOrDefault(
            resource => resource.Id == "artifact-manifest-input");
        if (manifest is null ||
            !string.Equals(
                manifest.Value,
                "sha256:" + candidate.ManifestSha256,
                StringComparison.Ordinal))
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.SetupPlanChanged);
        var selectedRoles = setupPlan.ExpectedResources
            .Where(resource =>
                resource.Kind == SetupExpectedResourceKind.RoleArtifactSet)
            .Select(resource => resource.Value)
            .ToHashSet(StringComparer.Ordinal);
        if (!candidate.RoleIds.Any(selectedRoles.Contains))
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.ArtifactNotSelected);
        foreach (var required in new[]
        {
            SetupConsentScope.LocalJournal,
            SetupConsentScope.ArtifactDownload,
            SetupConsentScope.ModelAndVoiceRights
        })
        {
            if (!setupPlan.RequiredConsentScopes.Contains(required))
                throw new ArtifactAcquisitionException(
                    ArtifactAcquisitionFailure.SetupPlanChanged);
        }
    }

    private static ArtifactAcquisitionJournalSnapshot? ReadAcquisitionJournal(
        ArtifactAcquisitionStorageSnapshot storageSnapshot)
    {
        if (storageSnapshot.JournalVersion is null)
            return null;
        if (storageSnapshot.JournalContent is not { } content)
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.JournalCorrupt);
        return new(
            ArtifactAcquisitionJournalCodec.Read(content),
            storageSnapshot.JournalVersion);
    }

    private static void ValidateJournalBinding(
        ArtifactAcquisitionJournalDocument document,
        ArtifactAcquisitionCandidate candidate,
        SetupPlan setupPlan,
        SetupPreview setupPreview,
        ArtifactRightsAuthorization rights,
        ArtifactAcquisitionPaths paths)
    {
        if (document.SourceIdentityFingerprint != candidate.Source.IdentityFingerprint ||
            !string.Equals(
                document.ArtifactIdentityFingerprint,
                candidate.IdentityFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                document.ManifestSha256,
                candidate.ManifestSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                document.ArtifactId,
                candidate.ArtifactId,
                StringComparison.Ordinal) ||
            !string.Equals(
                document.SourceUrl,
                candidate.SourceUrl,
                StringComparison.Ordinal) ||
            !string.Equals(
                document.SourceRevision,
                candidate.SourceRevision,
                StringComparison.Ordinal) ||
            document.ExpectedBytes != candidate.ExpectedBytes ||
            !string.Equals(
                document.ExpectedSha256,
                candidate.ExpectedSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                document.RightsAuthorizationFingerprint,
                rights.Fingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                document.SetupDesiredStateFingerprint,
                setupPlan.DesiredStateFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                document.ArtifactFactsFingerprint,
                setupPlan.ArtifactFactsFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                document.SetupJournalPath,
                setupPreview.JournalPath,
                StringComparison.Ordinal) ||
            document.SetupJournalRevision > setupPreview.JournalRevision ||
            !string.Equals(
                document.DestinationPath,
                paths.DestinationPath,
                PathComparison) ||
            !string.Equals(
                document.PartialPath,
                paths.PartialPath,
                PathComparison))
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.PlanChanged);
    }

    private static void ValidateJournalForPlan(
        ArtifactAcquisitionJournalDocument document,
        ArtifactAcquisitionPlan plan,
        bool allowReviewSupersession = false)
    {
        if (document.SourceIdentityFingerprint != plan.Source.IdentityFingerprint ||
            document.SelectionFingerprint != plan.Selection.Fingerprint ||
            document.SetupJournalId != plan.SetupJournalId ||
            document.SetupDesiredStateFingerprint != plan.SetupDesiredStateFingerprint ||
            document.ArtifactFactsFingerprint != plan.ArtifactFactsFingerprint ||
            document.SetupJournalPath != plan.SetupJournalPath ||
            !string.Equals(
                document.ArtifactIdentityFingerprint,
                plan.Candidate.IdentityFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                document.RightsAuthorizationFingerprint,
                plan.RightsAuthorizationFingerprint,
                StringComparison.Ordinal) ||
            !allowReviewSupersession && (!string.Equals(
                document.SetupPlanFingerprint,
                plan.SetupPlanFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                document.SetupJournalVersion,
                plan.SetupJournalVersion,
                StringComparison.Ordinal) ||
            document.SetupJournalRevision != plan.SetupJournalRevision) ||
            !string.Equals(
                document.DestinationPath,
                plan.DestinationPath,
                PathComparison))
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.PlanChanged);
    }

    private async ValueTask<(ArtifactAcquisitionJournalDocument Document, string Version)>
        PersistAsync(
            ArtifactAcquisitionPaths paths,
            ArtifactAcquisitionJournalDocument document,
            string? expectedVersion,
            CancellationToken cancellationToken,
            RunCursor? cursor = null)
    {
        var now = clock.GetUtcNow();
        var next = document with
        {
            Revision = checked(document.Revision + 1),
            UpdatedAtUtc = now < document.UpdatedAtUtc
                ? document.UpdatedAtUtc
                : now,
            IntegritySha256 = ""
        };
        var bytes = ArtifactAcquisitionJournalCodec.Write(next);
        var committed = await storage.WriteJournalAsync(
            paths,
            expectedVersion,
            bytes,
            cancellationToken).ConfigureAwait(false);
        if (cursor is not null)
        {
            cursor.Document = ArtifactAcquisitionJournalCodec.Read(committed.Content);
            cursor.Version = committed.Version;
        }
        return (
            ArtifactAcquisitionJournalCodec.Read(committed.Content),
            committed.Version);
    }

    private async ValueTask ReportAsync(
        ArtifactAcquisitionPlan plan,
        ArtifactAcquisitionProgressPhase phase,
        long persistedBytes,
        bool resumed,
        CancellationToken cancellationToken)
    {
        try
        {
            await progress.ReportAsync(
                new(
                    plan.Fingerprint,
                    plan.Candidate.ArtifactId,
                    phase,
                    persistedBytes,
                    plan.Candidate.ExpectedBytes,
                    resumed,
                    clock.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.ProgressFailed,
                error);
        }
    }

    private static ArtifactAcquisitionResult Result(
        ArtifactAcquisitionRunState state,
        ArtifactAcquisitionFailure? failure,
        ArtifactAcquisitionPlan plan,
        long? persistedBytes = null,
        bool resumed = false,
        bool payloadVerified = false,
        bool finalized = false,
        bool cleanupPerformed = false,
        string? verifiedSha256 = null) =>
        new(
            state,
            failure,
            persistedBytes ?? plan.ExistingBytes,
            resumed,
            payloadVerified,
            finalized,
            cleanupPerformed,
            verifiedSha256);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private sealed record ResponseIdentity(
        string? EntityTag,
        DateTimeOffset? LastModifiedUtc);

    private static void RequireLocalReview(SetupJournalDocument document)
    {
        if (document.Purpose != SetupJournalPurpose.LocalReview ||
            document.State != SetupJournalState.ReviewRecorded)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.SetupPlanBlocked);
    }

    private async ValueTask RequireSetupCurrentAsync(ArtifactAcquisitionPlan plan, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (now < plan.CreatedAtUtc || now >= plan.ExpiresAtUtc)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.PlanStale);
        var current = await setupFileSystem.ReadAsync(SetupJournalCodec.MaximumBytes, cancellationToken)
            .ConfigureAwait(false);
        if (current?.Version != plan.SetupJournalVersion)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.SetupJournalChanged);
        ValidateSetupDocument(SetupJournalCodec.Read(current.Content), plan);
    }

    private sealed class RunCursor(ArtifactAcquisitionJournalDocument document, string? version)
    {
        internal ArtifactAcquisitionJournalDocument Document { get; set; } = document;
        internal string? Version { get; set; } = version;
    }

    private static void RequireCurrentOwner(ArtifactAcquisitionStorageSnapshot snapshot, RunCursor cursor)
    {
        if (snapshot.JournalPendingExists || snapshot.JournalVersion != cursor.Version)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.JournalChanged);
        if (snapshot.PartialExists && snapshot.PartialIdentity != cursor.Document.PartialIdentity)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.PartialConflict);
        var expectedFinal = cursor.Document.FinalIdentity ??
            (cursor.Document.State == ArtifactAcquisitionJournalState.Verified ? cursor.Document.PartialIdentity : null);
        if (snapshot.FinalExists && snapshot.FinalIdentity != expectedFinal)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.FinalConflict);
    }

    public void Dispose()
    {
        if (ownsTransport && transport is IDisposable disposable) disposable.Dispose();
    }
}
