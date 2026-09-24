namespace Martlet.Host.Setup;

public sealed class HostDeploymentCoordinator
{
    private readonly LocalHostDeploymentStore store;
    private readonly ISetupFileSystem reviewStore;
    private readonly LocalArtifactAcquisitionStorage artifacts;
    private readonly TimeProvider clock;

    public HostDeploymentCoordinator(LocalHostDeploymentStore store, ISetupFileSystem localReviewStore,
        LocalArtifactAcquisitionStorage localArtifactStorage, TimeProvider? clock = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        reviewStore = localReviewStore ?? throw new ArgumentNullException(nameof(localReviewStore));
        artifacts = localArtifactStorage ?? throw new ArgumentNullException(nameof(localArtifactStorage));
        this.clock = clock ?? TimeProvider.System;
    }

    public async ValueTask<HostDeploymentPreview> PreviewAsync(HostDeploymentDefinition definition,
        SetupPreview recordedReview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(recordedReview);
        var inspectingStorage = false;
        try
        {
            var now = clock.GetUtcNow();
            RequireFresh(definition, now);
            DeploymentRules.Require(recordedReview.IsLocalReview &&
                recordedReview.State == SetupPreviewState.ReviewRecorded && recordedReview.Failure is null &&
                recordedReview.JournalPath == reviewStore.JournalPath &&
                recordedReview.PlanFingerprint == definition.Plan.Fingerprint &&
                recordedReview.JournalVersion is not null, HostDeploymentFailure.ReviewChanged);
            await CheckReviewAsync(definition, recordedReview.JournalVersion!, cancellationToken).ConfigureAwait(false);
            using var scope = await ArtifactAcquisitionCoordinator.OpenPublishedImagesAsync(artifacts,
                definition.Selection, cancellationToken).ConfigureAwait(false);
            var bundle = new HostComposeGenerator().Generate(definition, scope.Observation);
            inspectingStorage = true;
            var storage = await store.InspectAsync(bundle, cancellationToken).ConfigureAwait(false);
            inspectingStorage = false;
            await CheckReviewAsync(definition, recordedReview.JournalVersion!, cancellationToken).ConfigureAwait(false);
            var expires = new[] { now.AddMinutes(10), definition.Plan.ExpiresAtUtc }.Min();
            return new(bundle, storage, recordedReview.JournalVersion!, now, expires);
        }
        catch (ArtifactAcquisitionException error)
        { throw new HostDeploymentException(inspectingStorage ? HostDeploymentFailure.StorageConflict :
            HostDeploymentFailure.ContentChanged, error); }
        catch (SetupException error)
        { throw new HostDeploymentException(HostDeploymentFailure.ReviewChanged, error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new HostDeploymentException(HostDeploymentFailure.StorageFailure, error); }
    }

    public async ValueTask<HostDeploymentPublicationResult> PublishAsync(HostDeploymentDefinition definition,
        HostDeploymentPreview preview, HostDeploymentApproval approval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(approval);
        HostDeploymentPublicationResult Failed(HostDeploymentFailure failure) =>
            new(failure == HostDeploymentFailure.Canceled ? HostDeploymentPublicationState.Interrupted :
                HostDeploymentPublicationState.Refused, failure, null, preview.Bundle.Fingerprint);
        if (!approval.IsApproved) return Failed(HostDeploymentFailure.ConsentRequired);
        if (!approval.Consume()) return Failed(HostDeploymentFailure.ConsentConsumed);
        if (approval.Fingerprint != preview.Fingerprint || definition.Fingerprint != preview.Bundle.Definition.Fingerprint)
            return Failed(HostDeploymentFailure.PreviewChanged);
        var writingStorage = false;
        try
        {
            async ValueTask Check(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                var now = clock.GetUtcNow();
                DeploymentRules.Require(now >= preview.CreatedAtUtc && now < preview.ExpiresAtUtc,
                    HostDeploymentFailure.PreviewExpired);
                RequireFresh(definition, now);
                await CheckReviewAsync(definition, preview.ReviewVersion, token).ConfigureAwait(false);
            }
            await Check(cancellationToken).ConfigureAwait(false);
            using var observation = await ArtifactAcquisitionCoordinator.OpenPublishedImagesAsync(artifacts,
                definition.Selection, cancellationToken).ConfigureAwait(false);
            DeploymentRules.Require(observation.Observation.Fingerprint == preview.Bundle.ContentObservationFingerprint,
                HostDeploymentFailure.ContentChanged);
            writingStorage = true;
            var current = await store.InspectAsync(preview.Bundle, cancellationToken).ConfigureAwait(false);
            DeploymentRules.Require(current.Fingerprint == preview.StorageFingerprint, HostDeploymentFailure.PreviewChanged);
            await store.PublishAsync(preview.Bundle, current, Check, cancellationToken).ConfigureAwait(false);
            writingStorage = false;
            await observation.RevalidateAsync(cancellationToken).ConfigureAwait(false);
            await Check(cancellationToken).ConfigureAwait(false);
            return new(preview.AlreadyPublished ? HostDeploymentPublicationState.AlreadyPublished :
                HostDeploymentPublicationState.Published, null, preview.FinalPath, preview.Bundle.Fingerprint);
        }
        catch (HostDeploymentException error) { return Failed(error.Failure); }
        catch (ArtifactAcquisitionException)
        { return Failed(writingStorage ? HostDeploymentFailure.StorageConflict : HostDeploymentFailure.ContentChanged); }
        catch (SetupException) { return Failed(HostDeploymentFailure.StorageFailure); }
        catch (OperationCanceledException) { return Failed(HostDeploymentFailure.Canceled); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Failed(HostDeploymentFailure.StorageFailure); }
    }

    private async ValueTask CheckReviewAsync(HostDeploymentDefinition definition, string expectedVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await reviewStore.ReadAsync(SetupJournalCodec.MaximumBytes, cancellationToken).ConfigureAwait(false);
            DeploymentRules.Require(snapshot?.Version == expectedVersion, HostDeploymentFailure.ReviewChanged);
            var document = SetupJournalCodec.Read(snapshot!.Content);
            DeploymentRules.Require(document.Purpose == SetupJournalPurpose.LocalReview &&
                document.State == SetupJournalState.ReviewRecorded &&
                document.PlanFingerprint == definition.Plan.Fingerprint &&
                document.DesiredStateFingerprint == definition.Plan.DesiredStateFingerprint &&
                document.Steps.All(step => step.Status == SetupJournalStepStatus.Reviewed),
                HostDeploymentFailure.ReviewChanged);
        }
        catch (SetupException error) { throw new HostDeploymentException(HostDeploymentFailure.ReviewChanged, error); }
    }

    private static void RequireFresh(HostDeploymentDefinition definition, DateTimeOffset now) =>
        DeploymentRules.Require(now >= definition.Plan.CreatedAtUtc && now < definition.Plan.ExpiresAtUtc,
            HostDeploymentFailure.PreviewExpired);
}
