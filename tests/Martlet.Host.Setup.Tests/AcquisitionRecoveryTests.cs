using System.Text;

namespace Martlet.Host.Setup.Tests;

public sealed class AcquisitionRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owned_corrupt_final_requires_fresh_quarantine_consent_then_reacquires(bool empty)
    {
        await using var harness = await AcquisitionHarness.CreateAsync(payloadBytes: 8_000);
        harness.Handler.Respond = (request, _) => Task.FromResult(AcquisitionTestData.Response(request, harness.Payload));
        var coordinator = harness.Coordinator();
        var (preview, approval) = await harness.ApproveAsync(coordinator);
        Assert.Equal(ArtifactAcquisitionRunState.Acquired, (await coordinator.RunAsync(preview.Plan, approval)).State);
        var corrupt = empty ? [] : harness.Payload.Select(value => (byte)(value ^ 0x55)).ToArray();
        await File.WriteAllBytesAsync(preview.Plan.DestinationPath, corrupt);
        var (recovery, recoveryApproval) = await harness.ApproveAsync(coordinator);
        Assert.Equal(ArtifactAcquisitionPreviewState.RecoveryRequired, recovery.State);
        Assert.True(recovery.Plan.RecoverCorruptFinal);
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(preview.Plan.DestinationPath));
        Assert.Equal(1, harness.Handler.Calls);
        var result = await coordinator.RunAsync(recovery.Plan, recoveryApproval);
        Assert.Equal(ArtifactAcquisitionRunState.Acquired, result.State);
        Assert.Equal(harness.Payload, await File.ReadAllBytesAsync(preview.Plan.DestinationPath));
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(harness.Storage.GetPaths(harness.Candidate).QuarantinePath));
        var final = await coordinator.PreviewAsync(harness.Manifest, harness.Candidate.ArtifactId,
            harness.SetupPlan, harness.SetupPreview, harness.Rights);
        Assert.Equal(ArtifactAcquisitionPreviewState.Completed, final.State);
        Assert.Equal(2, harness.Handler.Calls);
    }

    [Fact]
    public async Task Replaced_final_is_foreign_even_when_length_matches()
    {
        await using var harness = await AcquisitionHarness.CreateAsync(payloadBytes: 8_000);
        harness.Handler.Respond = (request, _) => Task.FromResult(AcquisitionTestData.Response(request, harness.Payload));
        var coordinator = harness.Coordinator();
        var (preview, approval) = await harness.ApproveAsync(coordinator);
        Assert.Equal(ArtifactAcquisitionRunState.Acquired, (await coordinator.RunAsync(preview.Plan, approval)).State);
        File.Move(preview.Plan.DestinationPath, Path.Combine(harness.Root, "original.bin"));
        var foreign = new byte[harness.Payload.Length];
        await File.WriteAllBytesAsync(preview.Plan.DestinationPath, foreign);
        var conflict = await coordinator.PreviewAsync(harness.Manifest, harness.Candidate.ArtifactId,
            harness.SetupPlan, harness.SetupPreview, harness.Rights);
        Assert.Equal(ArtifactAcquisitionFailure.FinalConflict, conflict.Failure);
        Assert.False(conflict.CanApprove);
        Assert.Equal(foreign, await File.ReadAllBytesAsync(preview.Plan.DestinationPath));
        Assert.Equal(1, harness.Handler.Calls);
    }

    [Fact]
    public async Task Late_journal_change_prevents_cleanup_of_owned_partial()
    {
        await using var harness = await AcquisitionHarness.CreateAsync(payloadBytes: 180_000);
        var paths = harness.Storage.GetPaths(harness.Candidate);
        var changed = false;
        var observer = new CallbackProgress(progress =>
        {
            if (changed || progress.Phase != ArtifactAcquisitionProgressPhase.Downloading) return;
            changed = true;
            File.AppendAllText(paths.JournalPath, " ");
            throw new IOException("private observer failure");
        });
        harness.Handler.Respond = (request, _) => Task.FromResult(AcquisitionTestData.Response(request, harness.Payload));
        var coordinator = harness.Coordinator(observer);
        var (preview, approval) = await harness.ApproveAsync(coordinator);
        var error = await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () =>
            await coordinator.RunAsync(preview.Plan, approval));
        Assert.Equal(ArtifactAcquisitionFailure.JournalChanged, error.Failure);
        Assert.True(File.Exists(paths.PartialPath));
        Assert.InRange(new FileInfo(paths.PartialPath).Length, 1, harness.Payload.Length);
        Assert.EndsWith(" ", await File.ReadAllTextAsync(paths.JournalPath), StringComparison.Ordinal);
        Assert.False(File.Exists(paths.LeasePath));
    }

    [Fact]
    public async Task Concurrent_destination_lease_refuses_before_network_or_journal_write()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        var coordinator = harness.Coordinator();
        var (preview, approval) = await harness.ApproveAsync(coordinator);
        var paths = harness.Storage.GetPaths(harness.Candidate);
        var mutation = (IArtifactAcquisitionMutationStorage)harness.Storage;
        await using var lease = await mutation.AcquireLeaseAsync(paths, Guid.NewGuid(), default);
        var error = await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () =>
            await coordinator.RunAsync(preview.Plan, approval));
        Assert.Equal(ArtifactAcquisitionFailure.JournalChanged, error.Failure);
        Assert.False(File.Exists(paths.JournalPath));
        Assert.Equal(0, harness.Handler.Calls);
    }

    [Fact]
    public async Task Quarantine_move_before_journal_failure_reconciles_without_overwriting_retained_bytes()
    {
        await using var harness = await AcquisitionHarness.CreateAsync(payloadBytes: 8_000);
        harness.Handler.Respond = (request, _) => Task.FromResult(AcquisitionTestData.Response(request, harness.Payload));
        var coordinator = harness.Coordinator();
        var (first, consent) = await harness.ApproveAsync(coordinator);
        Assert.Equal(ArtifactAcquisitionRunState.Acquired, (await coordinator.RunAsync(first.Plan, consent)).State);
        var corrupt = new byte[harness.Payload.Length];
        await File.WriteAllBytesAsync(first.Plan.DestinationPath, corrupt);
        var fault = new FaultingAcquisitionStorage(harness.Storage) { JournalFailureAttempt = 2 };
        var broken = harness.Coordinator(storage: fault);
        var (repair, repairConsent) = await harness.ApproveAsync(broken);
        var error = await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () =>
            await broken.RunAsync(repair.Plan, repairConsent));
        Assert.Equal(ArtifactAcquisitionFailure.JournalIoFailure, error.Failure);
        var paths = harness.Storage.GetPaths(harness.Candidate);
        Assert.False(File.Exists(paths.DestinationPath));
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(paths.QuarantinePath));
        Assert.Equal(ArtifactAcquisitionJournalState.Quarantining,
            ArtifactAcquisitionJournalCodec.Read(await File.ReadAllBytesAsync(paths.JournalPath)).State);
        var (retry, retryConsent) = await harness.ApproveAsync(coordinator);
        Assert.Equal(ArtifactAcquisitionRunState.Acquired, (await coordinator.RunAsync(retry.Plan, retryConsent)).State);
        Assert.Equal(harness.Payload, await File.ReadAllBytesAsync(paths.DestinationPath));
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(paths.QuarantinePath));
    }

    [Fact]
    public async Task Fresh_compatible_local_review_rolls_forward_owned_partial_only_with_new_acquisition_consent()
    {
        await using var harness = await AcquisitionHarness.CreateAsync(payloadBytes: 180_000);
        harness.Handler.Respond = (request, _) => Task.FromResult(AcquisitionTestData.Response(request, harness.Payload));
        using var cancel = new CancellationTokenSource();
        var first = harness.Coordinator(new CancelingAcquisitionProgress(cancel, 65_536));
        var (preview, approval) = await harness.ApproveAsync(first);
        Assert.Equal(ArtifactAcquisitionRunState.Interrupted,
            (await first.RunAsync(preview.Plan, approval, cancel.Token)).State);
        harness.Clock.UtcNow += TimeSpan.FromMinutes(1);
        var updatedPlan = AcquisitionTestData.BoundPlan(harness.Manifest, harness.Clock.UtcNow);
        var reviewCoordinator = new SetupCoordinator(harness.SetupFileSystem, harness.Clock);
        var review = await reviewCoordinator.PreviewReviewAsync(updatedPlan);
        Assert.True(review.RequiresJournalRollForward);
        Assert.True((await reviewCoordinator.RecordReviewAsync(updatedPlan,
            review.Approve(SetupApprovalDecision.Approve, [SetupConsentScope.LocalJournal]))).ReviewRecorded);
        review = await reviewCoordinator.PreviewReviewAsync(updatedPlan);
        var second = harness.Coordinator();
        var resumed = await second.PreviewAsync(harness.Manifest, harness.Candidate.ArtifactId,
            updatedPlan, review, harness.Rights);
        Assert.Equal(ArtifactAcquisitionPreviewState.ResumeAvailable, resumed.State);
        Assert.False(resumed.Approve().IsApproved);
        Assert.Equal(ArtifactAcquisitionRunState.Acquired,
            (await second.RunAsync(resumed.Plan, resumed.Approve(ArtifactAcquisitionDecision.Approve,
                resumed.Plan.RequiredConsentScopes))).State);
        var journal = ArtifactAcquisitionJournalCodec.Read(await File.ReadAllBytesAsync(resumed.Plan.JournalPath));
        Assert.Equal(1, journal.Supersessions);
        Assert.Equal(review.JournalVersion, journal.SetupJournalVersion);
    }

    [Fact]
    public async Task Journal_change_during_hash_failure_preserves_partial_before_cleanup()
    {
        await using var harness = await AcquisitionHarness.CreateAsync(payloadBytes: 8_000);
        var corrupt = new byte[harness.Payload.Length];
        harness.Handler.Respond = (request, _) => Task.FromResult(AcquisitionTestData.Response(request, corrupt));
        var paths = harness.Storage.GetPaths(harness.Candidate);
        var storage = new FaultingAcquisitionStorage(harness.Storage)
        {
            AfterPartialHash = () => File.AppendAllText(paths.JournalPath, " ")
        };
        var coordinator = harness.Coordinator(storage: storage);
        var (preview, approval) = await harness.ApproveAsync(coordinator);
        var error = await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () =>
            await coordinator.RunAsync(preview.Plan, approval));
        Assert.Equal(ArtifactAcquisitionFailure.JournalChanged, error.Failure);
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(paths.PartialPath));
        Assert.False(File.Exists(paths.DestinationPath));
        Assert.EndsWith(" ", await File.ReadAllTextAsync(paths.JournalPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Corrupt_identity_proven_moved_verified_final_can_be_quarantined_and_reacquired()
    {
        await using var harness = await AcquisitionHarness.CreateAsync(payloadBytes: 8_000);
        harness.Handler.Respond = (request, _) => Task.FromResult(AcquisitionTestData.Response(request, harness.Payload));
        var paths = harness.Storage.GetPaths(harness.Candidate);
        var failed = false;
        harness.DirectoryCommitter.ShouldFail = () =>
        {
            if (failed || !File.Exists(paths.DestinationPath)) return false;
            failed = true;
            return true;
        };
        var coordinator = harness.Coordinator();
        var (preview, approval) = await harness.ApproveAsync(coordinator);
        Assert.Equal(ArtifactAcquisitionRunState.Interrupted, (await coordinator.RunAsync(preview.Plan, approval)).State);
        Assert.Equal(ArtifactAcquisitionJournalState.Verified,
            ArtifactAcquisitionJournalCodec.Read(await File.ReadAllBytesAsync(paths.JournalPath)).State);
        var corrupt = new byte[harness.Payload.Length];
        await File.WriteAllBytesAsync(paths.DestinationPath, corrupt);
        var (repair, consent) = await harness.ApproveAsync(coordinator);
        Assert.Equal(ArtifactAcquisitionPreviewState.RecoveryRequired, repair.State);
        Assert.Equal(ArtifactAcquisitionRunState.Acquired, (await coordinator.RunAsync(repair.Plan, consent)).State);
        Assert.Equal(harness.Payload, await File.ReadAllBytesAsync(paths.DestinationPath));
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(paths.QuarantinePath));
    }

    private sealed class CallbackProgress(Action<ArtifactAcquisitionProgress> callback) : IArtifactAcquisitionProgressSink
    {
        public ValueTask ReportAsync(ArtifactAcquisitionProgress progress, CancellationToken cancellationToken)
        {
            callback(progress);
            return ValueTask.CompletedTask;
        }
    }
}
