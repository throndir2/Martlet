using System.Net;
using System.Net.Http.Headers;

namespace Martlet.Host.Setup.Tests;

public sealed class AcquisitionTransactionTests
{
    [Fact]
    public async Task Complete_transaction_streams_verifies_fsyncs_and_atomically_finalizes()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        harness.Handler.Respond = (request, _) =>
            Task.FromResult(AcquisitionTestData.Response(
                request,
                harness.Payload));
        var coordinator = harness.Coordinator();
        var (preview, approval) = await harness.ApproveAsync(coordinator);

        var result = await coordinator.RunAsync(preview.Plan, approval);

        Assert.Equal(ArtifactAcquisitionRunState.Acquired, result.State);
        Assert.Null(result.Failure);
        Assert.Equal(harness.Payload.LongLength, result.PersistedBytes);
        Assert.True(result.PayloadVerified);
        Assert.True(result.Finalized);
        Assert.False(result.RuntimeEnabled);
        Assert.False(result.HostReady);
        Assert.Equal(harness.Candidate.ExpectedSha256, result.VerifiedSha256);
        Assert.Equal(harness.Payload, await File.ReadAllBytesAsync(preview.Plan.DestinationPath));
        Assert.False(File.Exists(preview.Plan.PartialPath));
        Assert.True(File.Exists(preview.Plan.JournalPath));
        Assert.Contains(harness.Root, harness.DirectoryCommitter.Calls);
        var journalBytes = await File.ReadAllBytesAsync(preview.Plan.JournalPath);
        var journal = ArtifactAcquisitionJournalCodec.Read(journalBytes);
        Assert.Equal(ArtifactAcquisitionJournalState.Finalized, journal.State);
        Assert.Equal(harness.SetupPlan.Fingerprint, journal.SetupPlanFingerprint);
        Assert.Equal(harness.SetupPreview.JournalVersion, journal.SetupJournalVersion);
        Assert.Equal(harness.Rights.Fingerprint, journal.RightsAuthorizationFingerprint);
        Assert.Equal(harness.Candidate.SourceUrl, journal.SourceUrl);
        Assert.Equal(harness.Candidate.ExpectedSha256, journal.ComputedSha256);
        Assert.True(journal.FinalOwned);
        Assert.False(journal.PartialOwned);
        Assert.DoesNotContain("ready", System.Text.Encoding.UTF8.GetString(journalBytes),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("enabled", System.Text.Encoding.UTF8.GetString(journalBytes),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Finalized_transaction_reconciles_idempotently_without_network_or_new_consent()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        harness.Handler.Respond = (request, _) =>
            Task.FromResult(AcquisitionTestData.Response(request, harness.Payload));
        var coordinator = harness.Coordinator();
        var (firstPreview, firstApproval) = await harness.ApproveAsync(coordinator);
        Assert.Equal(
            ArtifactAcquisitionRunState.Acquired,
            (await coordinator.RunAsync(firstPreview.Plan, firstApproval)).State);

        var secondPreview = await coordinator.PreviewAsync(
            harness.Manifest,
            harness.Candidate.ArtifactId,
            harness.SetupPlan,
            harness.SetupPreview,
            harness.Rights);
        var defaultNo = secondPreview.Approve();

        Assert.Equal(ArtifactAcquisitionPreviewState.Completed, secondPreview.State);
        Assert.False(secondPreview.CanApprove);
        Assert.False(defaultNo.IsApproved);
        Assert.Equal(1, harness.Handler.Calls);
        Assert.Equal(harness.Payload, await File.ReadAllBytesAsync(
            secondPreview.Plan.DestinationPath));
    }

    [Fact]
    public async Task Canceled_transfer_resumes_only_with_exact_range_and_if_range()
    {
        await using var harness = await AcquisitionHarness.CreateAsync(
            payloadBytes: 240_000);
        harness.Handler.Respond = (request, _) =>
            Task.FromResult(AcquisitionTestData.Response(request, harness.Payload));
        using var cancellation = new CancellationTokenSource();
        var cancelingProgress = new CancelingAcquisitionProgress(
            cancellation,
            65_536);
        var firstCoordinator = harness.Coordinator(cancelingProgress);
        var (firstPreview, firstApproval) = await harness.ApproveAsync(
            firstCoordinator);

        var interrupted = await firstCoordinator.RunAsync(
            firstPreview.Plan,
            firstApproval,
            cancellation.Token);

        Assert.Equal(ArtifactAcquisitionRunState.Interrupted, interrupted.State);
        Assert.Equal(ArtifactAcquisitionFailure.Canceled, interrupted.Failure);
        Assert.InRange(interrupted.PersistedBytes, 1, harness.Payload.LongLength - 1);
        Assert.True(File.Exists(firstPreview.Plan.PartialPath));
        Assert.False(File.Exists(firstPreview.Plan.DestinationPath));

        var secondCoordinator = harness.Coordinator();
        var (secondPreview, secondApproval) = await harness.ApproveAsync(
            secondCoordinator);
        Assert.Equal(
            ArtifactAcquisitionPreviewState.ResumeAvailable,
            secondPreview.State);
        Assert.Equal(interrupted.PersistedBytes, secondPreview.Plan.ExistingBytes);

        var completed = await secondCoordinator.RunAsync(
            secondPreview.Plan,
            secondApproval);

        Assert.Equal(ArtifactAcquisitionRunState.Acquired, completed.State);
        Assert.True(completed.Resumed);
        Assert.True(completed.PayloadVerified);
        Assert.Equal(2, harness.Handler.Calls);
        Assert.Null(harness.Handler.Requests[0].Range);
        Assert.Equal(
            $"bytes={interrupted.PersistedBytes}-",
            harness.Handler.Requests[1].Range);
        Assert.Equal(
            AcquisitionTestData.EntityTag,
            harness.Handler.Requests[1].IfRange);
        Assert.Equal(harness.Payload, await File.ReadAllBytesAsync(
            secondPreview.Plan.DestinationPath));
    }

    [Theory]
    [InlineData("etag")]
    [InlineData("last-modified")]
    public async Task Changed_source_identity_discards_only_owned_partial_and_never_auto_retries(
        string changed)
    {
        await using var harness = await AcquisitionHarness.CreateAsync(
            payloadBytes: 180_000);
        harness.Handler.Respond = (request, _) =>
            Task.FromResult(AcquisitionTestData.Response(request, harness.Payload));
        using var cancellation = new CancellationTokenSource();
        var first = harness.Coordinator(
            new CancelingAcquisitionProgress(cancellation, 65_536));
        var (firstPreview, firstApproval) = await harness.ApproveAsync(first);
        var interrupted = await first.RunAsync(
            firstPreview.Plan,
            firstApproval,
            cancellation.Token);
        Assert.Equal(ArtifactAcquisitionRunState.Interrupted, interrupted.State);

        harness.Handler.Respond = (request, _) =>
            Task.FromResult(AcquisitionTestData.Response(
                request,
                harness.Payload,
                entityTag: changed == "etag"
                    ? "\"fixture-etag-v2\""
                    : AcquisitionTestData.EntityTag,
                lastModified: changed == "last-modified"
                    ? AcquisitionTestData.LastModified.AddMinutes(1)
                    : AcquisitionTestData.LastModified,
                status: HttpStatusCode.OK));
        var second = harness.Coordinator();
        var (secondPreview, secondApproval) = await harness.ApproveAsync(second);
        var replaced = await second.RunAsync(secondPreview.Plan, secondApproval);

        Assert.Equal(ArtifactAcquisitionRunState.Blocked, replaced.State);
        Assert.Equal(ArtifactAcquisitionFailure.SourceChanged, replaced.Failure);
        Assert.True(replaced.CleanupPerformed);
        Assert.Equal(2, harness.Handler.Calls);
        Assert.False(File.Exists(secondPreview.Plan.PartialPath));
        Assert.False(File.Exists(secondPreview.Plan.DestinationPath));

        harness.Handler.Respond = (request, _) =>
            Task.FromResult(AcquisitionTestData.Response(request, harness.Payload));
        var third = harness.Coordinator();
        var (thirdPreview, thirdApproval) = await harness.ApproveAsync(third);
        Assert.Equal(ArtifactAcquisitionPreviewState.Proposed, thirdPreview.State);
        var completed = await third.RunAsync(thirdPreview.Plan, thirdApproval);

        Assert.Equal(ArtifactAcquisitionRunState.Acquired, completed.State);
        Assert.Equal(3, harness.Handler.Calls);
        Assert.Null(harness.Handler.Requests[2].Range);
    }

    [Fact]
    public async Task Wrong_payload_hash_is_read_back_rejected_and_owned_partial_is_cleaned()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        var corrupt = harness.Payload.ToArray();
        corrupt[^1] ^= 0xff;
        harness.Handler.Respond = (request, _) =>
            Task.FromResult(AcquisitionTestData.Response(request, corrupt));
        var coordinator = harness.Coordinator();
        var (preview, approval) = await harness.ApproveAsync(coordinator);

        var result = await coordinator.RunAsync(preview.Plan, approval);

        Assert.Equal(ArtifactAcquisitionRunState.Blocked, result.State);
        Assert.Equal(ArtifactAcquisitionFailure.IntegrityMismatch, result.Failure);
        Assert.True(result.CleanupPerformed);
        Assert.False(result.PayloadVerified);
        Assert.False(File.Exists(preview.Plan.PartialPath));
        Assert.False(File.Exists(preview.Plan.DestinationPath));
        var journal = ArtifactAcquisitionJournalCodec.Read(
            await File.ReadAllBytesAsync(preview.Plan.JournalPath));
        Assert.Equal(ArtifactAcquisitionJournalState.Failed, journal.State);
        Assert.Equal(ArtifactAcquisitionFailure.IntegrityMismatch, journal.LastFailure);
        Assert.NotEqual(journal.ExpectedSha256, journal.ComputedSha256);
    }

    [Fact]
    public async Task Response_exceeding_manifest_size_is_bounded_and_owned_partial_is_cleaned()
    {
        await using var harness = await AcquisitionHarness.CreateAsync(
            payloadBytes: 80_000);
        harness.Handler.Respond = (request, _) =>
        {
            byte[] oversized = [.. harness.Payload, 0x42];
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(new MemoryStream(
                    oversized,
                    writable: false))
            };
            response.Headers.AcceptRanges.Add("bytes");
            response.Headers.ETag =
                EntityTagHeaderValue.Parse(AcquisitionTestData.EntityTag);
            response.Content.Headers.LastModified =
                AcquisitionTestData.LastModified;
            response.Content.Headers.ContentLength =
                harness.Payload.LongLength;
            return Task.FromResult(response);
        };
        var coordinator = harness.Coordinator();
        var (preview, approval) = await harness.ApproveAsync(coordinator);

        var result = await coordinator.RunAsync(preview.Plan, approval);

        Assert.Equal(ArtifactAcquisitionRunState.Blocked, result.State);
        Assert.Equal(ArtifactAcquisitionFailure.SizeExceeded, result.Failure);
        Assert.True(result.CleanupPerformed);
        Assert.False(File.Exists(preview.Plan.PartialPath));
        Assert.False(File.Exists(preview.Plan.DestinationPath));
        Assert.Equal(1, harness.Handler.Calls);
    }

    [Fact]
    public async Task Early_eof_retains_exact_verified_prefix_for_explicit_resume()
    {
        await using var harness = await AcquisitionHarness.CreateAsync(
            payloadBytes: 200_000);
        var shortLength = 70_000;
        harness.Handler.Respond = (request, _) =>
        {
            if (request.Headers.Range is not null)
                return Task.FromResult(AcquisitionTestData.Response(
                    request,
                    harness.Payload));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(new MemoryStream(
                    harness.Payload[..shortLength],
                    writable: false))
            };
            response.Headers.AcceptRanges.Add("bytes");
            response.Headers.ETag =
                EntityTagHeaderValue.Parse(AcquisitionTestData.EntityTag);
            response.Content.Headers.LastModified =
                AcquisitionTestData.LastModified;
            response.Content.Headers.ContentLength =
                harness.Payload.LongLength;
            return Task.FromResult(response);
        };
        var first = harness.Coordinator();
        var (firstPreview, firstApproval) = await harness.ApproveAsync(first);

        var truncated = await first.RunAsync(firstPreview.Plan, firstApproval);

        Assert.Equal(ArtifactAcquisitionRunState.Interrupted, truncated.State);
        Assert.Equal(
            ArtifactAcquisitionFailure.ContentLengthMismatch,
            truncated.Failure);
        Assert.Equal(shortLength, truncated.PersistedBytes);
        Assert.Equal(shortLength, new FileInfo(firstPreview.Plan.PartialPath).Length);

        var second = harness.Coordinator();
        var (secondPreview, secondApproval) = await harness.ApproveAsync(second);
        var completed = await second.RunAsync(secondPreview.Plan, secondApproval);

        Assert.Equal(ArtifactAcquisitionRunState.Acquired, completed.State);
        Assert.Equal($"bytes={shortLength}-", harness.Handler.Requests[1].Range);
        Assert.Equal(harness.Payload, await File.ReadAllBytesAsync(
            secondPreview.Plan.DestinationPath));
    }

    [Fact]
    public async Task Range_ignored_on_resume_is_refused_without_fallback_or_partial_loss()
    {
        await using var harness = await AcquisitionHarness.CreateAsync(
            payloadBytes: 180_000);
        harness.Handler.Respond = (request, _) =>
            Task.FromResult(AcquisitionTestData.Response(request, harness.Payload));
        using var cancellation = new CancellationTokenSource();
        var first = harness.Coordinator(
            new CancelingAcquisitionProgress(cancellation, 65_536));
        var (firstPreview, firstApproval) = await harness.ApproveAsync(first);
        var interrupted = await first.RunAsync(
            firstPreview.Plan,
            firstApproval,
            cancellation.Token);
        var partialBefore = await File.ReadAllBytesAsync(firstPreview.Plan.PartialPath);

        harness.Handler.Respond = (request, _) =>
            Task.FromResult(AcquisitionTestData.Response(
                request,
                harness.Payload,
                status: HttpStatusCode.OK));
        var second = harness.Coordinator();
        var (secondPreview, secondApproval) = await harness.ApproveAsync(second);
        var refused = await second.RunAsync(secondPreview.Plan, secondApproval);

        Assert.Equal(ArtifactAcquisitionRunState.Interrupted, refused.State);
        Assert.Equal(ArtifactAcquisitionFailure.RangeUnsupported, refused.Failure);
        Assert.Equal(2, harness.Handler.Calls);
        Assert.Equal(partialBefore, await File.ReadAllBytesAsync(
            secondPreview.Plan.PartialPath));
        Assert.Equal(interrupted.PersistedBytes, refused.PersistedBytes);
    }

    [Fact]
    public async Task Timeout_is_durable_and_does_not_retry()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        harness.Handler.Respond = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        };
        var coordinator = harness.Coordinator(
            transferTimeout: TimeSpan.FromMilliseconds(20));
        var (preview, approval) = await harness.ApproveAsync(coordinator);

        var result = await coordinator.RunAsync(preview.Plan, approval);

        Assert.Equal(ArtifactAcquisitionRunState.Interrupted, result.State);
        Assert.Equal(ArtifactAcquisitionFailure.Timeout, result.Failure);
        Assert.Equal(1, harness.Handler.Calls);
        Assert.False(File.Exists(preview.Plan.PartialPath));
        Assert.False(File.Exists(preview.Plan.DestinationPath));
        var journal = ArtifactAcquisitionJournalCodec.Read(
            await File.ReadAllBytesAsync(preview.Plan.JournalPath));
        Assert.Equal(ArtifactAcquisitionJournalState.Failed, journal.State);
        Assert.Equal(ArtifactAcquisitionFailure.Timeout, journal.LastFailure);
    }

    [Fact]
    public async Task Timeout_before_first_body_byte_reuses_exact_owned_empty_partial()
    {
        await using var harness = await AcquisitionHarness.CreateAsync(
            payloadBytes: 80_000);
        harness.Handler.Respond = (request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(new BlockingReadStream())
            };
            response.Headers.AcceptRanges.Add("bytes");
            response.Headers.ETag =
                EntityTagHeaderValue.Parse(AcquisitionTestData.EntityTag);
            response.Content.Headers.LastModified =
                AcquisitionTestData.LastModified;
            response.Content.Headers.ContentLength =
                harness.Payload.LongLength;
            return Task.FromResult(response);
        };
        var first = harness.Coordinator(
            transferTimeout: TimeSpan.FromSeconds(1));
        var (firstPreview, firstApproval) = await harness.ApproveAsync(first);

        var timedOut = await first.RunAsync(firstPreview.Plan, firstApproval);

        Assert.Equal(ArtifactAcquisitionRunState.Interrupted, timedOut.State);
        Assert.Equal(ArtifactAcquisitionFailure.Timeout, timedOut.Failure);
        Assert.Equal(0, timedOut.PersistedBytes);
        Assert.True(File.Exists(firstPreview.Plan.PartialPath));
        Assert.Equal(0, new FileInfo(firstPreview.Plan.PartialPath).Length);

        harness.Handler.Respond = (request, _) =>
            Task.FromResult(AcquisitionTestData.Response(
                request,
                harness.Payload));
        var second = harness.Coordinator();
        var (secondPreview, secondApproval) = await harness.ApproveAsync(second);
        Assert.Equal(
            ArtifactAcquisitionPreviewState.ResumeAvailable,
            secondPreview.State);
        var completed = await second.RunAsync(secondPreview.Plan, secondApproval);

        Assert.Equal(ArtifactAcquisitionRunState.Acquired, completed.State);
        Assert.False(completed.Resumed);
        Assert.Equal(2, harness.Handler.Calls);
        Assert.Null(harness.Handler.Requests[1].Range);
        Assert.Equal(harness.Payload, await File.ReadAllBytesAsync(
            secondPreview.Plan.DestinationPath));
    }

    [Theory]
    [InlineData(ArtifactAcquisitionFailure.DiskFull, true)]
    [InlineData(ArtifactAcquisitionFailure.AccessDenied, false)]
    public async Task Storage_failures_are_explicit_and_never_report_success(
        ArtifactAcquisitionFailure failure,
        bool afterPartial)
    {
        await using var harness = await AcquisitionHarness.CreateAsync(
            payloadBytes: 180_000);
        harness.Handler.Respond = (request, _) =>
            Task.FromResult(AcquisitionTestData.Response(request, harness.Payload));
        var faulting = new FaultingAcquisitionStorage(harness.Storage);
        if (afterPartial)
        {
            faulting.WriteFailure = failure;
            faulting.WriteFailureAtBytes = 70_000;
        }
        else
        {
            faulting.OpenFailure = failure;
        }
        var coordinator = harness.Coordinator(storage: faulting);
        var (preview, approval) = await harness.ApproveAsync(coordinator);

        var result = await coordinator.RunAsync(preview.Plan, approval);

        Assert.Equal(
            afterPartial
                ? ArtifactAcquisitionRunState.Interrupted
                : ArtifactAcquisitionRunState.Blocked,
            result.State);
        Assert.Equal(failure, result.Failure);
        Assert.False(result.PayloadVerified);
        Assert.False(result.Finalized);
        Assert.False(File.Exists(preview.Plan.DestinationPath));
        Assert.Equal(afterPartial, File.Exists(preview.Plan.PartialPath));
    }

    [Fact]
    public async Task Finalize_failure_leaves_verified_partial_and_fresh_approval_reconciles()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        harness.Handler.Respond = (request, _) =>
            Task.FromResult(AcquisitionTestData.Response(request, harness.Payload));
        var faulting = new FaultingAcquisitionStorage(harness.Storage)
        {
            FinalizeFailure = ArtifactAcquisitionFailure.StorageFailed
        };
        var first = harness.Coordinator(storage: faulting);
        var (firstPreview, firstApproval) = await harness.ApproveAsync(first);

        var failed = await first.RunAsync(firstPreview.Plan, firstApproval);

        Assert.Equal(ArtifactAcquisitionRunState.Interrupted, failed.State);
        Assert.Equal(ArtifactAcquisitionFailure.StorageFailed, failed.Failure);
        Assert.True(File.Exists(firstPreview.Plan.PartialPath));
        Assert.False(File.Exists(firstPreview.Plan.DestinationPath));

        faulting.FinalizeFailure = null;
        var second = harness.Coordinator(storage: faulting);
        var (secondPreview, secondApproval) = await harness.ApproveAsync(second);
        Assert.Equal(
            ArtifactAcquisitionPreviewState.VerificationPending,
            secondPreview.State);
        var completed = await second.RunAsync(secondPreview.Plan, secondApproval);

        Assert.Equal(ArtifactAcquisitionRunState.Acquired, completed.State);
        Assert.True(completed.Resumed);
        Assert.Equal(1, harness.Handler.Calls);
        Assert.Equal(harness.Payload, await File.ReadAllBytesAsync(
            secondPreview.Plan.DestinationPath));
    }

    [Fact]
    public async Task Post_move_directory_commit_failure_reconciles_verified_final_without_redownload()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        harness.Handler.Respond = (request, _) =>
            Task.FromResult(AcquisitionTestData.Response(request, harness.Payload));
        var paths = harness.Storage.GetPaths(harness.Candidate);
        var failedCommit = false;
        harness.DirectoryCommitter.ShouldFail = () =>
        {
            if (failedCommit || !File.Exists(paths.DestinationPath)) return false;
            failedCommit = true;
            return true;
        };
        var first = harness.Coordinator();
        var (firstPreview, firstApproval) = await harness.ApproveAsync(first);

        var uncertain = await first.RunAsync(firstPreview.Plan, firstApproval);

        Assert.Equal(ArtifactAcquisitionRunState.Interrupted, uncertain.State);
        Assert.Equal(ArtifactAcquisitionFailure.StorageFailed, uncertain.Failure);
        Assert.True(uncertain.PayloadVerified);
        Assert.False(uncertain.Finalized);
        Assert.False(File.Exists(firstPreview.Plan.PartialPath));
        Assert.Equal(harness.Payload, await File.ReadAllBytesAsync(
            firstPreview.Plan.DestinationPath));
        var durable = ArtifactAcquisitionJournalCodec.Read(
            await File.ReadAllBytesAsync(firstPreview.Plan.JournalPath));
        Assert.Equal(ArtifactAcquisitionJournalState.Verified, durable.State);

        var second = harness.Coordinator();
        var (secondPreview, secondApproval) = await harness.ApproveAsync(second);
        Assert.Equal(
            ArtifactAcquisitionPreviewState.FinalizationPending,
            secondPreview.State);
        var reconciled = await second.RunAsync(
            secondPreview.Plan,
            secondApproval);

        Assert.Equal(
            ArtifactAcquisitionRunState.AlreadyAcquired,
            reconciled.State);
        Assert.True(reconciled.PayloadVerified);
        Assert.True(reconciled.Finalized);
        Assert.Equal(1, harness.Handler.Calls);
        Assert.Equal(
            ArtifactAcquisitionJournalState.Finalized,
            ArtifactAcquisitionJournalCodec.Read(
                await File.ReadAllBytesAsync(secondPreview.Plan.JournalPath)).State);
    }

    [Fact]
    public async Task Concurrent_journal_change_after_resume_preview_refuses_without_request()
    {
        await using var harness = await AcquisitionHarness.CreateAsync(
            payloadBytes: 180_000);
        harness.Handler.Respond = (request, _) =>
            Task.FromResult(AcquisitionTestData.Response(request, harness.Payload));
        using var cancellation = new CancellationTokenSource();
        var first = harness.Coordinator(
            new CancelingAcquisitionProgress(cancellation, 65_536));
        var (firstPreview, firstApproval) = await harness.ApproveAsync(first);
        _ = await first.RunAsync(
            firstPreview.Plan,
            firstApproval,
            cancellation.Token);

        var second = harness.Coordinator();
        var (secondPreview, secondApproval) = await harness.ApproveAsync(second);
        await File.AppendAllTextAsync(
            secondPreview.Plan.JournalPath,
            " ");
        var callsBefore = harness.Handler.Calls;

        var refused = await second.RunAsync(secondPreview.Plan, secondApproval);

        Assert.Equal(ArtifactAcquisitionRunState.Refused, refused.State);
        Assert.Equal(ArtifactAcquisitionFailure.JournalChanged, refused.Failure);
        Assert.Equal(callsBefore, harness.Handler.Calls);
    }

    [Fact]
    public async Task Progress_failure_is_explicit_and_no_request_occurs()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        harness.Handler.Respond = (request, _) =>
            Task.FromResult(AcquisitionTestData.Response(request, harness.Payload));
        var coordinator = harness.Coordinator(new ThrowingAcquisitionProgress());
        var (preview, approval) = await harness.ApproveAsync(coordinator);

        var result = await coordinator.RunAsync(preview.Plan, approval);

        Assert.Equal(ArtifactAcquisitionRunState.Blocked, result.State);
        Assert.Equal(ArtifactAcquisitionFailure.ProgressFailed, result.Failure);
        Assert.Equal(0, harness.Handler.Calls);
        Assert.False(File.Exists(preview.Plan.PartialPath));
        Assert.False(File.Exists(preview.Plan.DestinationPath));
    }

    [Fact]
    public async Task Journal_write_failure_is_surfaced_before_network()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        var faulting = new FaultingAcquisitionStorage(harness.Storage)
        {
            JournalFailureAttempt = 1
        };
        var coordinator = harness.Coordinator(storage: faulting);
        var (preview, approval) = await harness.ApproveAsync(coordinator);

        var error = await Assert.ThrowsAsync<ArtifactAcquisitionException>(
            async () => await coordinator.RunAsync(preview.Plan, approval));

        Assert.Equal(ArtifactAcquisitionFailure.JournalIoFailure, error.Failure);
        Assert.Equal(0, harness.Handler.Calls);
        Assert.False(File.Exists(preview.Plan.PartialPath));
        Assert.False(File.Exists(preview.Plan.DestinationPath));
    }
}
