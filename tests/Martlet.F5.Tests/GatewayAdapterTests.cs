using Martlet.F5;

namespace Martlet.F5.Tests;

public sealed class GatewayAdapterTests
{
    [Fact]
    public async Task Conversation_streams_contiguous_pcm_with_exact_identity_and_reference()
    {
        using var scope = new F5TestScope();
        F5TestData.WriteWav(scope.SourcePath);
        using var store = scope.OpenStore();
        var snapshot = await F5TestData.SnapshotAsync(store, scope.SourcePath);
        await F5TestData.ApplyAsync(store, snapshot);
        var identity = DeterministicF5FixtureIdentity.Create();
        var worker = new DeterministicF5WorkerTransport(identity);
        var adapter = new F5GatewayClientAdapter(
            store, worker, F5TestData.Destination, identity, F5TestData.Clock);
        var request = F5TestData.Conversation(identity, snapshot.ReferenceRevision);
        var sink = new RecordingPcmSink();

        var result = await adapter.SynthesizeAsync(request,
            request.Authorize(F5ConversationAuthorizationDecision.Allow,
                F5TestData.Now.AddSeconds(10)), sink);

        Assert.Equal(F5SynthesisOutcome.Completed, result.Outcome);
        Assert.Null(result.Failure);
        Assert.Equal(2, result.DeliveredFrames);
        Assert.Equal(960, result.DeliveredSamples);
        Assert.Equal([0L, 1L], sink.Frames.Select(frame => frame.Sequence));
        Assert.Equal([0L, 480L], sink.Frames.Select(frame => frame.SampleOffset));
        Assert.All(sink.Frames, frame => Assert.Equal(F5PcmFormat.Transport, frame.Format));
        Assert.Equal(1, worker.StreamCalls);
        Assert.Equal(1, worker.CacheMisses);
        Assert.Single(worker.InvalidationRequests);
    }

    [Fact]
    public async Task Preview_is_passive_until_separately_authorized_and_does_not_apply()
    {
        using var scope = new F5TestScope();
        F5TestData.WriteWav(scope.SourcePath);
        using var store = scope.OpenStore();
        var snapshot = await F5TestData.SnapshotAsync(store, scope.SourcePath);
        var identity = DeterministicF5FixtureIdentity.Create();
        var worker = new DeterministicF5WorkerTransport(identity);
        var adapter = new F5GatewayClientAdapter(
            store, worker, F5TestData.Destination, identity, F5TestData.Clock);
        var request = F5TestData.Preview(identity, snapshot);

        Assert.Throws<F5Exception>(() =>
            request.Authorize(F5PreviewAuthorizationDecision.No,
                F5TestData.Now.AddSeconds(5)));
        Assert.Equal(0, worker.StreamCalls);
        Assert.Null(store.Inspect().AppliedReferenceRevision);

        var result = await adapter.PreviewAsync(request,
            request.Authorize(F5PreviewAuthorizationDecision.Allow,
                F5TestData.Now.AddSeconds(5)), new RecordingPcmSink());
        Assert.Equal(F5SynthesisOutcome.Completed, result.Outcome);
        Assert.Equal(1, worker.StreamCalls);
        Assert.Null(store.Inspect().AppliedReferenceRevision);
    }

    [Fact]
    public async Task Authorization_is_exact_expiring_and_one_use()
    {
        using var scope = new F5TestScope();
        F5TestData.WriteWav(scope.SourcePath);
        using var store = scope.OpenStore();
        var snapshot = await F5TestData.SnapshotAsync(store, scope.SourcePath);
        await F5TestData.ApplyAsync(store, snapshot);
        var identity = DeterministicF5FixtureIdentity.Create();
        var worker = new DeterministicF5WorkerTransport(identity);
        var adapter = new F5GatewayClientAdapter(
            store, worker, F5TestData.Destination, identity, F5TestData.Clock);
        var first = F5TestData.Conversation(identity, snapshot.ReferenceRevision);
        var second = F5TestData.Conversation(identity, snapshot.ReferenceRevision);
        var authorization = first.Authorize(F5ConversationAuthorizationDecision.Allow,
            F5TestData.Now.AddSeconds(5));

        var mismatch = await adapter.SynthesizeAsync(second, authorization,
            new RecordingPcmSink());
        Assert.Equal(F5Failure.AuthorizationMismatch, mismatch.Failure);
        var success = await adapter.SynthesizeAsync(first, authorization,
            new RecordingPcmSink());
        Assert.Equal(F5SynthesisOutcome.Completed, success.Outcome);
        var consumed = await adapter.SynthesizeAsync(first, authorization,
            new RecordingPcmSink());
        Assert.Equal(F5Failure.AuthorizationConsumed, consumed.Failure);

        var expiredRequest = F5TestData.Conversation(identity, snapshot.ReferenceRevision);
        var expired = await adapter.SynthesizeAsync(expiredRequest,
            expiredRequest.Authorize(F5ConversationAuthorizationDecision.Allow,
                F5TestData.Now), new RecordingPcmSink());
        Assert.Equal(F5Failure.AuthorizationExpired, expired.Failure);
        Assert.Equal(1, worker.StreamCalls);
    }

    [Fact]
    public async Task Same_path_replacement_keeps_the_snapshot_voice_until_a_new_one_is_applied()
    {
        using var scope = new F5TestScope();
        F5TestData.WriteWav(scope.SourcePath, seed: 1);
        using var store = scope.OpenStore();
        var snapshot = await F5TestData.SnapshotAsync(store, scope.SourcePath);
        await F5TestData.ApplyAsync(store, snapshot);
        var identity = DeterministicF5FixtureIdentity.Create();
        var worker = new DeterministicF5WorkerTransport(identity);
        var adapter = new F5GatewayClientAdapter(
            store, worker, F5TestData.Destination, identity, F5TestData.Clock);
        var initial = F5TestData.Conversation(identity, snapshot.ReferenceRevision);
        Assert.Equal(F5SynthesisOutcome.Completed,
            (await adapter.SynthesizeAsync(initial,
                initial.Authorize(F5ConversationAuthorizationDecision.Allow,
                    F5TestData.Now.AddSeconds(5)), new RecordingPcmSink())).Outcome);
        F5TestData.WriteWav(scope.SourcePath, seed: 9);
        var request = F5TestData.Conversation(identity, snapshot.ReferenceRevision);

        var result = await adapter.SynthesizeAsync(request,
            request.Authorize(F5ConversationAuthorizationDecision.Allow,
                F5TestData.Now.AddSeconds(5)), new RecordingPcmSink());

        Assert.Equal(F5SynthesisOutcome.Completed, result.Outcome);
        Assert.Equal(2, worker.StreamCalls);
        var replacement = await F5TestData.SnapshotAsync(
            store, scope.SourcePath, snapshot.PresetId);
        var apply = await store.CreateApplyPreviewAsync(
            replacement.PresetId, replacement.ReferenceRevision);
        await adapter.ApplyReferenceAsync(apply, apply.Authorize(F5ApplyDecision.Allow));
        var replacementRequest = F5TestData.Conversation(
            identity, replacement.ReferenceRevision);
        var replacementResult = await adapter.SynthesizeAsync(replacementRequest,
            replacementRequest.Authorize(F5ConversationAuthorizationDecision.Allow,
                F5TestData.Now.AddSeconds(5)), new RecordingPcmSink());
        Assert.Equal(F5SynthesisOutcome.Completed, replacementResult.Outcome);
        Assert.Equal([replacement.ReferenceRevision], worker.CachedReferenceRevisions);
        Assert.Equal(3, worker.StreamCalls);
    }

    [Fact]
    public async Task Missing_original_does_not_block_the_stored_voice()
    {
        using var scope = new F5TestScope();
        F5TestData.WriteWav(scope.SourcePath);
        using var store = scope.OpenStore();
        var snapshot = await F5TestData.SnapshotAsync(store, scope.SourcePath);
        await F5TestData.ApplyAsync(store, snapshot);
        var identity = DeterministicF5FixtureIdentity.Create();
        var worker = new DeterministicF5WorkerTransport(identity);
        var adapter = new F5GatewayClientAdapter(
            store, worker, F5TestData.Destination, identity, F5TestData.Clock);
        File.Delete(scope.SourcePath);
        var request = F5TestData.Conversation(identity, snapshot.ReferenceRevision);

        var result = await adapter.SynthesizeAsync(request,
            request.Authorize(F5ConversationAuthorizationDecision.Allow,
                F5TestData.Now.AddSeconds(5)), new RecordingPcmSink());

        Assert.Equal(F5SynthesisOutcome.Completed, result.Outcome);
        Assert.Equal(1, worker.StreamCalls);
    }

    [Fact]
    public async Task Applying_new_reference_invalidates_old_conditioning_cache()
    {
        using var scope = new F5TestScope();
        var secondPath = Path.Combine(scope.Root, "second.wav");
        F5TestData.WriteWav(scope.SourcePath, seed: 1);
        F5TestData.WriteWav(secondPath, seed: 2);
        using var store = scope.OpenStore();
        var first = await F5TestData.SnapshotAsync(store, scope.SourcePath, name: "A");
        var second = await F5TestData.SnapshotAsync(store, secondPath, name: "B");
        await F5TestData.ApplyAsync(store, first);
        var identity = DeterministicF5FixtureIdentity.Create();
        var worker = new DeterministicF5WorkerTransport(identity);
        var adapter = new F5GatewayClientAdapter(
            store, worker, F5TestData.Destination, identity, F5TestData.Clock);

        var firstRequest = F5TestData.Conversation(identity, first.ReferenceRevision);
        Assert.Equal(F5SynthesisOutcome.Completed,
            (await adapter.SynthesizeAsync(firstRequest,
                firstRequest.Authorize(F5ConversationAuthorizationDecision.Allow,
                    F5TestData.Now.AddSeconds(5)), new RecordingPcmSink())).Outcome);
        var apply = await store.CreateApplyPreviewAsync(second.PresetId, second.ReferenceRevision);
        await adapter.ApplyReferenceAsync(apply, apply.Authorize(F5ApplyDecision.Allow));
        var secondRequest = F5TestData.Conversation(identity, second.ReferenceRevision);
        Assert.Equal(F5SynthesisOutcome.Completed,
            (await adapter.SynthesizeAsync(secondRequest,
                secondRequest.Authorize(F5ConversationAuthorizationDecision.Allow,
                    F5TestData.Now.AddSeconds(5)), new RecordingPcmSink())).Outcome);

        Assert.Equal([second.ReferenceRevision], worker.CachedReferenceRevisions);
        Assert.Equal(2, worker.CacheMisses);
        Assert.Equal(2, worker.InvalidationRequests.Count);
        Assert.Contains(first.ReferenceRevision,
            worker.InvalidationRequests[1].ReferenceRevisions);
        Assert.Contains(second.ReferenceRevision,
            worker.InvalidationRequests[1].ReferenceRevisions);
    }

    [Fact]
    public async Task Truncated_stream_is_not_completed()
    {
        using var scope = new F5TestScope();
        F5TestData.WriteWav(scope.SourcePath);
        using var store = scope.OpenStore();
        var snapshot = await F5TestData.SnapshotAsync(store, scope.SourcePath);
        await F5TestData.ApplyAsync(store, snapshot);
        var identity = DeterministicF5FixtureIdentity.Create();
        var worker = new DeterministicF5WorkerTransport(identity,
            new() { TruncateAfterFrames = 1 });
        var adapter = new F5GatewayClientAdapter(
            store, worker, F5TestData.Destination, identity, F5TestData.Clock);
        var request = F5TestData.Conversation(identity, snapshot.ReferenceRevision);

        var result = await adapter.SynthesizeAsync(request,
            request.Authorize(F5ConversationAuthorizationDecision.Allow,
                F5TestData.Now.AddSeconds(5)), new RecordingPcmSink());

        Assert.Equal(F5SynthesisOutcome.Failed, result.Outcome);
        Assert.Equal(F5Failure.StreamTruncated, result.Failure);
        Assert.Equal(1, result.DeliveredFrames);
        Assert.True(result.LocalOutputDiscarded);
        Assert.True(result.WorkerMayContinue);
        Assert.Equal(1, worker.CancelCalls);
    }

    [Fact]
    public async Task Cancellation_discards_late_frames_and_does_not_claim_compute_stop()
    {
        using var scope = new F5TestScope();
        F5TestData.WriteWav(scope.SourcePath);
        using var store = scope.OpenStore();
        var snapshot = await F5TestData.SnapshotAsync(store, scope.SourcePath);
        await F5TestData.ApplyAsync(store, snapshot);
        var identity = DeterministicF5FixtureIdentity.Create();
        var worker = new DeterministicF5WorkerTransport(identity, new()
        {
            FramesPerChunk = 4,
            WaitForCancellationAfterFrames = 1,
            LateFramesAfterCancellation = 2
        });
        var adapter = new F5GatewayClientAdapter(
            store, worker, F5TestData.Destination, identity, F5TestData.Clock);
        var request = F5TestData.Conversation(identity, snapshot.ReferenceRevision);
        var sink = new RecordingPcmSink();
        using var stop = new CancellationTokenSource();
        var running = adapter.SynthesizeAsync(request,
            request.Authorize(F5ConversationAuthorizationDecision.Allow,
                F5TestData.Now.AddSeconds(10)), sink, stop.Token);
        await sink.FirstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stop.CancelAsync();

        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(F5SynthesisOutcome.Canceled, result.Outcome);
        Assert.Equal(F5Failure.Canceled, result.Failure);
        Assert.Single(sink.Frames);
        Assert.Equal(2, result.DiscardedLateFrames);
        Assert.True(result.LocalOutputDiscarded);
        Assert.Equal(F5CancellationCapability.DiscardOnly, result.ComputeCancellation);
        Assert.True(result.WorkerMayContinue);
        Assert.Equal(1, worker.CancelCalls);
    }

    [Fact]
    public async Task Apply_is_rejected_while_adapter_holds_active_reference_then_succeeds_after_cleanup()
    {
        using var scope = new F5TestScope();
        var secondPath = Path.Combine(scope.Root, "second.wav");
        F5TestData.WriteWav(scope.SourcePath, seed: 1);
        F5TestData.WriteWav(secondPath, seed: 2);
        using var store = scope.OpenStore();
        var first = await F5TestData.SnapshotAsync(store, scope.SourcePath, name: "A");
        var second = await F5TestData.SnapshotAsync(store, secondPath, name: "B");
        await F5TestData.ApplyAsync(store, first);
        var identity = DeterministicF5FixtureIdentity.Create();
        var worker = new DeterministicF5WorkerTransport(identity, new()
        {
            FramesPerChunk = 2,
            WaitForCancellationAfterFrames = 1
        });
        var adapter = new F5GatewayClientAdapter(
            store, worker, F5TestData.Destination, identity, F5TestData.Clock);
        var request = F5TestData.Conversation(identity, first.ReferenceRevision);
        var sink = new RecordingPcmSink();
        using var stop = new CancellationTokenSource();
        var running = adapter.SynthesizeAsync(request,
            request.Authorize(F5ConversationAuthorizationDecision.Allow,
                F5TestData.Now.AddSeconds(10)), sink, stop.Token);
        await sink.FirstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var preview = await store.CreateApplyPreviewAsync(
            second.PresetId, second.ReferenceRevision);
        var authorization = preview.Authorize(F5ApplyDecision.Allow);

        await F5TestData.FailureAsync(F5Failure.Busy, async () =>
            await adapter.ApplyReferenceAsync(preview, authorization));
        Assert.Equal(first.ReferenceRevision, store.Inspect().AppliedReferenceRevision);
        await stop.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        await adapter.ApplyReferenceAsync(preview, authorization);
        Assert.Equal(second.ReferenceRevision, store.Inspect().AppliedReferenceRevision);
    }

    [Fact]
    public async Task Worker_deadline_error_is_not_reported_as_generic_success_or_failure()
    {
        using var scope = new F5TestScope();
        F5TestData.WriteWav(scope.SourcePath);
        using var store = scope.OpenStore();
        var snapshot = await F5TestData.SnapshotAsync(store, scope.SourcePath);
        await F5TestData.ApplyAsync(store, snapshot);
        var identity = DeterministicF5FixtureIdentity.Create();
        var worker = new DeterministicF5WorkerTransport(identity, new()
        {
            FailAfterFrames = 0,
            FailureCode = F5WorkerErrorCode.DeadlineExceeded
        });
        var adapter = new F5GatewayClientAdapter(
            store, worker, F5TestData.Destination, identity, F5TestData.Clock);
        var request = F5TestData.Conversation(identity, snapshot.ReferenceRevision);

        var result = await adapter.SynthesizeAsync(request,
            request.Authorize(F5ConversationAuthorizationDecision.Allow,
                F5TestData.Now.AddSeconds(5)), new RecordingPcmSink());

        Assert.Equal(F5SynthesisOutcome.DeadlineExceeded, result.Outcome);
        Assert.Equal(F5Failure.DeadlineExceeded, result.Failure);
        Assert.Equal(F5WorkerErrorCode.DeadlineExceeded, result.WorkerError?.Code);
    }

    [Fact]
    public async Task Expired_deadline_and_wrong_worker_never_dispatch()
    {
        using var scope = new F5TestScope();
        F5TestData.WriteWav(scope.SourcePath);
        using var store = scope.OpenStore();
        var snapshot = await F5TestData.SnapshotAsync(store, scope.SourcePath);
        await F5TestData.ApplyAsync(store, snapshot);
        var identity = DeterministicF5FixtureIdentity.Create();
        var worker = new DeterministicF5WorkerTransport(identity);
        var adapter = new F5GatewayClientAdapter(
            store, worker, F5TestData.Destination, identity, F5TestData.Clock);
        var expired = F5TestData.Conversation(identity, snapshot.ReferenceRevision,
            deadline: F5TestData.Now);
        var expiredResult = await adapter.SynthesizeAsync(expired,
            expired.Authorize(F5ConversationAuthorizationDecision.Allow,
                F5TestData.Now), new RecordingPcmSink());
        Assert.Equal(F5Failure.DeadlineExceeded, expiredResult.Failure);

        var otherIdentity = new F5WorkerIdentity(
            "other-fixture",
            identity.Evidence,
            identity.Runtime,
            identity.Artifacts,
            identity.Cancellation);
        var wrong = F5TestData.Conversation(otherIdentity, snapshot.ReferenceRevision);
        var wrongResult = await adapter.SynthesizeAsync(wrong,
            wrong.Authorize(F5ConversationAuthorizationDecision.Allow,
                F5TestData.Now.AddSeconds(5)), new RecordingPcmSink());
        Assert.Equal(F5Failure.IdentityMismatch, wrongResult.Failure);
        Assert.Equal(0, worker.StreamCalls);
    }
}
