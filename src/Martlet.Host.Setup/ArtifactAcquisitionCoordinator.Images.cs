using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Martlet.HostArtifacts;

namespace Martlet.Host.Setup;

public sealed partial class ArtifactAcquisitionCoordinator
{
    private Func<HttpsArtifactImageTransport>? imageTransportFactory;

    internal static ArtifactAcquisitionCoordinator CreateForImageFixture(ISetupFileSystem setupFileSystem,
        LocalArtifactAcquisitionStorage storage, Func<HttpMessageHandler> handlerFactory,
        TimeProvider? clock = null, IArtifactAcquisitionProgressSink? progress = null, long reserveBytes = 0)
    {
        var result = CreateForFixture(setupFileSystem, storage, progress: progress, clock: clock,
            freeSpaceReserveBytes: reserveBytes);
        result.imageTransportFactory = () => new HttpsArtifactImageTransport(handlerFactory(), clock);
        return result;
    }

    public async ValueTask<ArtifactImageAcquisitionPreview> PreviewImagesAsync(
        ArtifactAcquisitionSelection selection, SetupPlan setupPlan, SetupPreview setupPreview,
        IEnumerable<ArtifactRightsAuthorization> imageRights, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(setupPlan);
        ArgumentNullException.ThrowIfNull(setupPreview);
        ArgumentNullException.ThrowIfNull(imageRights);
        cancellationToken.ThrowIfCancellationRequested();
        var local = ImageStorage();
        var paths = local.GetImagePaths(selection);
        ValidateSetupBoundary(setupPlan, setupPreview, selection.ManifestSha256, selection.RoleIds, paths.Parent);
        var expectedRoles = setupPlan.ExpectedResources.Where(resource =>
            resource.Kind == SetupExpectedResourceKind.RoleArtifactSet).Select(resource => resource.Value)
            .Order(StringComparer.Ordinal);
        if (!expectedRoles.SequenceEqual(selection.RoleIds) || selection.DeclaredMismatch ||
            selection.Target != "ubuntu-24.04-x64" || selection.Platform != "linux/amd64")
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.SetupPlanChanged);
        var setupBytes = await ReadCurrentSetupJournalAsync(setupPlan, setupPreview, cancellationToken).ConfigureAwait(false);
        var setupId = SetupJournalCodec.Read(setupBytes.Content).JournalId;
        var suppliedRights = imageRights.Take(5).ToArray();
        if (suppliedRights.Any(right => right is null))
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.RightsNotApproved);
        var rights = suppliedRights.OrderBy(right => right.ArtifactIdentityFingerprint, StringComparer.Ordinal).ToImmutableArray();
        var now = clock.GetUtcNow();
        ArtifactAcquisitionFailure? blocker = ImageEligibility(selection);
        if (!setupPreview.IsLocalReview || setupPlan.InstallationMachine is null ||
            setupPreview.Failure is not null || setupPreview.State != SetupPreviewState.ReviewRecorded)
            blocker ??= ArtifactAcquisitionFailure.SetupPlanBlocked;
        if (now < setupPlan.CreatedAtUtc || now >= setupPlan.ExpiresAtUtc)
            blocker ??= ArtifactAcquisitionFailure.PlanStale;
        if (!ImageRightsCurrent(selection, rights, now))
            blocker ??= ArtifactAcquisitionFailure.RightsNotApproved;
        var expires = new[] { now + planLifetime, setupPlan.ExpiresAtUtc }
            .Concat(rights.Where(right => right.IsApproved).Select(right => right.ExpiresAtUtc)).Min();
        if (expires <= now) expires = now.AddTicks(1);
        var snapshot = await local.InspectImageAsync(paths, selection, async token =>
        {
            var current = await setupFileSystem.ReadAsync(SetupJournalCodec.MaximumBytes, token).ConfigureAwait(false);
            if (current?.Version != setupPreview.JournalVersion)
                throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.SetupJournalChanged);
        }, cancellationToken).ConfigureAwait(false);
        if (snapshot.Journal is { } journal &&
            (journal.SetupJournalId != setupId || journal.SetupDesiredStateFingerprint != setupPlan.DesiredStateFingerprint ||
             journal.Supersessions >= 16 && journal.SetupJournalVersion != setupPreview.JournalVersion))
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.SetupPlanChanged);
        if (snapshot.LeaseBusy) blocker ??= ArtifactAcquisitionFailure.ImageLeaseUnavailable;
        if (snapshot.Pending) blocker ??= ArtifactAcquisitionFailure.JournalChanged;
        var needsRecovery = snapshot.Corrupt || snapshot.Journal?.LastFailure is
            ArtifactAcquisitionFailure.RangeUnsupported or ArtifactAcquisitionFailure.ResumeStateMismatch or
            ArtifactAcquisitionFailure.ContentLengthMismatch or ArtifactAcquisitionFailure.SizeExceeded or
            ArtifactAcquisitionFailure.IntegrityMismatch || snapshot.Journal?.Contents.Any(progress =>
        {
            var content = selection.ImageContentInventory.Contents.Single(item => item.Digest == progress.Digest);
            return progress.Identity is not null && progress.Bytes == content.ExpectedBytes && progress.Sha256 != content.Digest[7..] ||
                content.Kind is ArtifactImageContentKind.Index or ArtifactImageContentKind.Manifest &&
                progress.Bytes > 0 && progress.Bytes < content.ExpectedBytes;
        }) == true;
        var recovery = needsRecovery ? snapshot.Published ?? snapshot.Staging : null;
        if (needsRecovery && (recovery is null || snapshot.Quarantine is not null))
            blocker ??= ArtifactAcquisitionFailure.FinalConflict;
        if (snapshot.Journal?.LastFailure is ArtifactAcquisitionFailure.ImageMetadataInvalid or
            ArtifactAcquisitionFailure.ImageInventoryMismatch or ArtifactAcquisitionFailure.ImagePlatformMismatch)
            blocker ??= snapshot.Journal.LastFailure;
        var plan = new ArtifactImageAcquisitionPlan(selection, setupPlan, setupPreview, setupId, rights,
            paths, snapshot, recovery, now, expires, transferTimeout, freeSpaceReserveBytes, blocker);
        var state = recovery is not null || snapshot.Journal?.State is ArtifactImageJournalState.Quarantining or ArtifactImageJournalState.Quarantined
            ? ArtifactAcquisitionPreviewState.RecoveryRequired
            : snapshot.Published is not null
                ? snapshot.Journal?.State == ArtifactImageJournalState.Published
                    ? ArtifactAcquisitionPreviewState.Completed : ArtifactAcquisitionPreviewState.FinalizationPending
                : snapshot.Journal?.Contents.Any(content => content.Bytes > 0) == true
                    ? ArtifactAcquisitionPreviewState.ResumeAvailable : ArtifactAcquisitionPreviewState.Proposed;
        return new(plan, state);
    }

    public async ValueTask<ArtifactImageAcquisitionResult> RunImagesAsync(
        ArtifactImageAcquisitionPlan plan, ArtifactImageAcquisitionApproval approval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(approval);
        if (!approval.IsApproved) return ImageResult(plan, ArtifactAcquisitionRunState.Refused, approval.Failure);
        if (!approval.TryConsume()) return ImageResult(plan, ArtifactAcquisitionRunState.Refused, ArtifactAcquisitionFailure.ConsentConsumed);
        if (approval.PlanFingerprint != plan.Fingerprint)
            return ImageResult(plan, ArtifactAcquisitionRunState.Refused, ArtifactAcquisitionFailure.PlanChanged);
        if (!approval.ApprovedScopes.SequenceEqual(plan.RequiredConsentScopes))
            return ImageResult(plan, ArtifactAcquisitionRunState.Refused, ArtifactAcquisitionFailure.ConsentScopeMismatch);
        if (plan.Blocker is { } blocker) return ImageResult(plan, ArtifactAcquisitionRunState.Refused, blocker);
        var local = ImageStorage();
        ImageRunCursor? cursor = null;
        LocalArtifactAcquisitionStorage.ImageLease? lease = null;
        using var timeout = new CancellationTokenSource(plan.TransferTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await RequireImageCurrentAsync(plan, null, null, linked.Token).ConfigureAwait(false);
            var current = await local.InspectImageAsync(plan.Paths, plan.Selection,
                token => RequireImageCurrentAsync(plan, null, null, token), linked.Token).ConfigureAwait(false);
            if (current.Fingerprint != plan.StorageFactsFingerprint)
                return ImageResult(plan, ArtifactAcquisitionRunState.Refused, ArtifactAcquisitionFailure.PlanChanged);
            if (current.AvailableBytes < plan.RequiredAvailableBytes)
                return ImageResult(plan, ArtifactAcquisitionRunState.Refused, ArtifactAcquisitionFailure.FreeSpaceInsufficient);
            lease = await local.AcquireImageLeaseAsync(plan.Paths, current, linked.Token).ConfigureAwait(false);
            var latest = await local.ReadImageJournalAsync(plan.Paths, linked.Token).ConfigureAwait(false);
            if (latest?.Version != current.JournalVersion)
                throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.JournalChanged);
            cursor = new(current.Journal ?? NewImageJournal(plan, lease), current.JournalVersion);
            if (current.Staging is { } stage && !stage.Directories.ContainsKey("partials") &&
                cursor.Document.Contents.All(row => row.State == ArtifactAcquisitionJournalState.Finalized))
                cursor.Document = cursor.Document with { Directories = new(stage.Directories, StringComparer.Ordinal) };
            if (current.Journal is not null &&
                (cursor.Document.SetupJournalVersion != plan.SetupPreview.JournalVersion ||
                 cursor.Document.RightsFingerprint != plan.RightsFingerprint))
            {
                if (cursor.Document.Supersessions >= 16)
                    throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.SetupJournalChanged);
                cursor.Document = cursor.Document with { Supersessions = cursor.Document.Supersessions + 1 };
            }
            await PersistImageAsync(plan, cursor, lease, linked.Token).ConfigureAwait(false);
            await RecoverImageTreeAsync(plan, cursor, lease, linked.Token).ConfigureAwait(false);

            if (current.Published is not null && plan.RecoveryTree is null)
            {
                var graph = await VerifyImageGraphAsync(plan, cursor, lease, plan.Paths.Destination, linked.Token).ConfigureAwait(false);
                await VerifyImageTreeForPublicationAsync(plan, cursor, lease, plan.Paths.Destination, graph, linked.Token).ConfigureAwait(false);
                cursor.Document = cursor.Document with { State = ArtifactImageJournalState.Published, LastFailure = null };
                await PersistImageAsync(plan, cursor, lease, linked.Token).ConfigureAwait(false);
                return ImageSuccess(plan, cursor, graph, alreadyAcquired: true);
            }

            await PrepareImageTreeAsync(plan, cursor, lease, linked.Token).ConfigureAwait(false);
            using var transport = imageTransportFactory?.Invoke() ?? new HttpsArtifactImageTransport();
            var budget = new ArtifactImageRequestBudget(plan);
            cursor.Document = cursor.Document with { State = ArtifactImageJournalState.AcquiringMetadata, LastFailure = null };
            await PersistImageAsync(plan, cursor, lease, linked.Token).ConfigureAwait(false);
            foreach (var content in plan.Selection.ImageContentInventory.Contents.Where(content => content.Kind != ArtifactImageContentKind.Layer))
            {
                await AcquireImageContentAsync(plan, cursor, lease, content, transport, budget, linked.Token).ConfigureAwait(false);
                await VerifyAcquiredImageMetadataAsync(plan, cursor, lease, content, linked.Token).ConfigureAwait(false);
            }
            var verified = await VerifyImageGraphAsync(plan, cursor, lease, plan.Paths.Staging, linked.Token).ConfigureAwait(false);
            cursor.Document = cursor.Document with { State = ArtifactImageJournalState.AcquiringContent };
            await PersistImageAsync(plan, cursor, lease, linked.Token).ConfigureAwait(false);
            foreach (var content in plan.Selection.ImageContentInventory.Contents.Where(content => content.Kind == ArtifactImageContentKind.Layer))
                await AcquireImageContentAsync(plan, cursor, lease, content, transport, budget, linked.Token).ConfigureAwait(false);
            cursor.Document = cursor.Document with { State = ArtifactImageJournalState.ClosureVerified };
            await PersistImageAsync(plan, cursor, lease, linked.Token).ConfigureAwait(false);
            await PublishImageTreeAsync(plan, cursor, lease, verified, linked.Token).ConfigureAwait(false);
            return ImageSuccess(plan, cursor, verified, alreadyAcquired: false);
        }
        catch (OperationCanceledException)
        {
            var failure = cancellationToken.IsCancellationRequested ? ArtifactAcquisitionFailure.Canceled : ArtifactAcquisitionFailure.Timeout;
            if (cursor is not null && lease is not null)
                await RecordImageFailureAsync(plan, cursor, lease, failure).ConfigureAwait(false);
            return ImageResult(plan, ArtifactAcquisitionRunState.Interrupted, failure, cursor);
        }
        catch (ArtifactAcquisitionException error)
        {
            if (cursor is not null && lease is not null &&
                error.Failure is not (ArtifactAcquisitionFailure.JournalChanged or ArtifactAcquisitionFailure.JournalIoFailure or
                    ArtifactAcquisitionFailure.JournalCorrupt or ArtifactAcquisitionFailure.JournalTooLarge or
                    ArtifactAcquisitionFailure.ImageLeaseUnavailable or ArtifactAcquisitionFailure.DestinationInvalid))
                await RecordImageFailureAsync(plan, cursor, lease, error.Failure).ConfigureAwait(false);
            return ImageResult(plan, ArtifactAcquisitionRunState.Blocked, error.Failure, cursor);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SetupException)
        {
            var failure = error is UnauthorizedAccessException ? ArtifactAcquisitionFailure.AccessDenied : ArtifactAcquisitionFailure.StorageFailed;
            if (cursor is not null && lease is not null)
                await RecordImageFailureAsync(plan, cursor, lease, failure).ConfigureAwait(false);
            return ImageResult(plan, ArtifactAcquisitionRunState.Blocked, failure, cursor);
        }
        finally { lease?.Dispose(); }
    }

    private static ArtifactAcquisitionFailure? ImageEligibility(ArtifactAcquisitionSelection selection)
    {
        if (selection.ImageCandidates.IsEmpty || selection.ImageCandidates.Any(image =>
            image.Source.Provider == ArtifactImageAcquisitionProvider.Unsupported))
            return ArtifactAcquisitionFailure.ProviderUnsupported;
        if (selection.ImageCandidates.Any(image => image.Platform != "linux/amd64"))
            return ArtifactAcquisitionFailure.ImagePlatformMismatch;
        if (selection.ImageCandidates.Any(image => !image.BlobInventoryComplete) ||
            selection.ImageContentInventory.UnknownBytesCount != 0)
            return ArtifactAcquisitionFailure.ImageInventoryMismatch;
        if (selection.ImageContentInventory.Contents.Any(content =>
            content.Kind != ArtifactImageContentKind.Layer && content.ExpectedBytes > ArtifactImageMetadata.MaximumBytes))
            return ArtifactAcquisitionFailure.ImageMetadataInvalid;
        return null;
    }

    private static bool ImageRightsCurrent(ArtifactAcquisitionSelection selection,
        ImmutableArray<ArtifactRightsAuthorization> rights, DateTimeOffset now) =>
        rights.Length == selection.ImageCandidates.Length &&
        rights.Select(right => right.ArtifactIdentityFingerprint).Distinct(StringComparer.Ordinal).Count() == rights.Length &&
        selection.ImageCandidates.All(image => rights.Any(right =>
            right.ArtifactIdentityFingerprint == image.IdentityFingerprint && right.IsApproved &&
            right.SelectionFingerprint == selection.Fingerprint && now >= right.CreatedAtUtc && now < right.ExpiresAtUtc));

    private LocalArtifactAcquisitionStorage ImageStorage() => storage as LocalArtifactAcquisitionStorage ??
        throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.DestinationInvalid);

    private async ValueTask RequireImageCurrentAsync(ArtifactImageAcquisitionPlan plan, ImageRunCursor? cursor,
        LocalArtifactAcquisitionStorage.ImageLease? lease, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = clock.GetUtcNow();
        if (now < plan.CreatedAtUtc || now >= plan.ExpiresAtUtc)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.PlanStale);
        if (!ImageRightsCurrent(plan.Selection, plan.Rights, now))
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.RightsNotApproved);
        var current = await setupFileSystem.ReadAsync(SetupJournalCodec.MaximumBytes, cancellationToken).ConfigureAwait(false);
        if (current is null || setupFileSystem.JournalPath != plan.SetupPreview.JournalPath || current.Version != plan.SetupPreview.JournalVersion)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.SetupJournalChanged);
        var review = SetupJournalCodec.Read(current.Content);
        ValidateSetupDocument(review, plan.SetupPlan);
        if (review.JournalId != plan.SetupJournalId)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.SetupJournalChanged);
        if (lease is not null) lease.Validate();
        if (cursor is not null)
        {
            ImageStorage().ValidateImageJournalLocation(plan.Paths, cursor.Document);
            if (ImageStorage().ImageAvailableBytes(plan.Paths) < plan.ReserveBytes)
                throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.FreeSpaceInsufficient);
            var image = await ImageStorage().ReadImageJournalAsync(plan.Paths, cancellationToken).ConfigureAwait(false);
            if (image?.Version != cursor.Version ||
                ArtifactOwnedJournalIO.InspectFile(plan.Paths.Journal + ".pending") is not null)
                throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.JournalChanged);
        }
    }

    private async ValueTask PersistImageAsync(ArtifactImageAcquisitionPlan plan, ImageRunCursor cursor,
        LocalArtifactAcquisitionStorage.ImageLease lease, CancellationToken cancellationToken, bool failureOnly = false)
    {
        if (!failureOnly) await RequireImageCurrentAsync(plan, cursor, lease, cancellationToken).ConfigureAwait(false);
        else lease.Validate();
        var next = cursor.Document with
        {
            Revision = checked(cursor.Document.Revision + 1), UpdatedAtUtc = clock.GetUtcNow(),
            PlanFingerprint = plan.Fingerprint, RightsFingerprint = plan.RightsFingerprint,
            SetupJournalVersion = plan.SetupPreview.JournalVersion!
        };
        var written = await ImageStorage().WriteImageJournalAsync(plan.Paths, next, cursor.Version, lease, cancellationToken).ConfigureAwait(false);
        cursor.Document = next;
        cursor.Version = written.Version;
    }

    private ValueTask RecordImageFailureAsync(ArtifactImageAcquisitionPlan plan, ImageRunCursor cursor,
        LocalArtifactAcquisitionStorage.ImageLease lease, ArtifactAcquisitionFailure failure)
    {
        if (cursor.Document.State is not (ArtifactImageJournalState.Publishing or ArtifactImageJournalState.Published or ArtifactImageJournalState.Quarantining))
            cursor.Document = cursor.Document with
            {
                State = failure is ArtifactAcquisitionFailure.Canceled or ArtifactAcquisitionFailure.Timeout or ArtifactAcquisitionFailure.PlanStale
                    ? ArtifactImageJournalState.Interrupted : ArtifactImageJournalState.Failed
            };
        cursor.Document = cursor.Document with { LastFailure = failure };
        return PersistImageAsync(plan, cursor, lease, CancellationToken.None, failureOnly: true);
    }

    private ArtifactImageJournalDocument NewImageJournal(ArtifactImageAcquisitionPlan plan,
        LocalArtifactAcquisitionStorage.ImageLease lease) => new()
    {
        FormatVersion = 3, Purpose = ArtifactImageJournalPurpose.OciImageAcquisition,
        JournalId = Guid.NewGuid(), Revision = 0, SelectionFingerprint = plan.Selection.Fingerprint,
        InventoryFingerprint = plan.Selection.ImageContentInventory.Fingerprint, PlanFingerprint = plan.Fingerprint,
        RightsFingerprint = plan.RightsFingerprint, SetupDesiredStateFingerprint = plan.SetupPlan.DesiredStateFingerprint,
        SetupJournalId = plan.SetupJournalId, SetupJournalVersion = plan.SetupPreview.JournalVersion!,
        Supersessions = 0, ParentIdentity = plan.Storage.ParentIdentity,
        LeaseIdentity = lease.Identity, LeaseNonce = lease.Nonce, RootIdentity = null, Directories = [],
        Contents = EmptyImageProgress(plan.Selection), PublicationFiles = [], Quarantine = null,
        RecoveryTree = null, RecoveryWasPublished = false, State = ArtifactImageJournalState.Prepared,
        CreatedAtUtc = clock.GetUtcNow(), UpdatedAtUtc = clock.GetUtcNow(), LastFailure = null, IntegritySha256 = ""
    };

    private static ArtifactImageContentProgress[] EmptyImageProgress(ArtifactAcquisitionSelection selection) =>
        selection.ImageContentInventory.Contents.Select(content => new ArtifactImageContentProgress
        {
            Digest = content.Digest, State = ArtifactAcquisitionJournalState.Prepared, Identity = null,
            Bytes = 0, Sha256 = null, MediaType = null
        }).ToArray();

    private static ArtifactImageAcquisitionResult ImageResult(ArtifactImageAcquisitionPlan plan,
        ArtifactAcquisitionRunState state, ArtifactAcquisitionFailure? failure, ImageRunCursor? cursor = null) =>
        new(state, failure, null, [], cursor?.Document.Contents.Sum(content => content.Bytes) ?? 0,
            plan.Storage.Journal?.Contents.Any(content => content.Bytes > 0) == true);

    private static ArtifactImageAcquisitionResult ImageSuccess(ArtifactImageAcquisitionPlan plan,
        ImageRunCursor cursor, Dictionary<string, VerifiedImageManifest> graph, bool alreadyAcquired) =>
        new(alreadyAcquired ? ArtifactAcquisitionRunState.AlreadyAcquired : ArtifactAcquisitionRunState.Acquired,
            null, plan.DestinationPath, plan.Selection.ImageCandidates.Select(image =>
                new ArtifactImageAcquisitionReceipt(image.ArtifactId, image.Digest, graph[image.ArtifactId].MediaType,
                    image.IndexDigest, image.Platform!, graph[image.ArtifactId].ConfigurationDigest,
                    graph[image.ArtifactId].LayerDigests.ToImmutableArray())).ToImmutableArray(),
            cursor.Document.Contents.Sum(content => content.Bytes),
            plan.Storage.Journal?.Contents.Any(content => content.Bytes > 0) == true);

    private sealed class ImageRunCursor(ArtifactImageJournalDocument document, string? version)
    {
        internal ArtifactImageJournalDocument Document { get; set; } = document;
        internal string? Version { get; set; } = version;
    }
}
