using Martlet.F5;

namespace Martlet.F5.Tests;

public sealed class AdversarialBoundaryTests
{
    public static IEnumerable<object[]> NullCacheCases()
    {
        yield return [AdversarialTransportFault.NullCacheResponse];
        yield return [AdversarialTransportFault.NullCacheWorker];
        yield return [AdversarialTransportFault.NullCacheRevisions];
    }

    [Theory]
    [MemberData(nameof(NullCacheCases))]
    public async Task Malformed_cache_response_fails_closed_without_dispatch(
        AdversarialTransportFault fault)
    {
        using var scope = new F5TestScope();
        F5TestData.WriteWav(scope.SourcePath);
        using var store = scope.OpenStore();
        var snapshot = await F5TestData.SnapshotAsync(store, scope.SourcePath);
        await F5TestData.ApplyAsync(store, snapshot);
        var identity = DeterministicF5FixtureIdentity.Create();
        var transport = new AdversarialF5Transport(identity, fault);
        var adapter = new F5GatewayClientAdapter(
            store, transport, F5TestData.Destination, identity, F5TestData.Clock);
        var request = F5TestData.Conversation(identity, snapshot.ReferenceRevision);

        var result = await adapter.SynthesizeAsync(request,
            request.Authorize(F5ConversationAuthorizationDecision.Allow,
                F5TestData.Now.AddSeconds(5)), new RecordingPcmSink());

        Assert.Equal(F5SynthesisOutcome.Failed, result.Outcome);
        Assert.Equal(F5Failure.CacheInvalidationFailed, result.Failure);
        Assert.Equal(0, transport.StreamCalls);
    }

    [Theory]
    [InlineData(AdversarialTransportFault.NullStreamEnumerable)]
    [InlineData(AdversarialTransportFault.NullStreamEvent)]
    public async Task Null_stream_or_event_becomes_protocol_failure(
        AdversarialTransportFault fault)
    {
        var result = await RunSimpleFaultAsync(fault);
        Assert.Equal(F5SynthesisOutcome.Failed, result.Result.Outcome);
        Assert.Equal(F5Failure.ProtocolViolation, result.Result.Failure);
        Assert.True(result.Result.LocalOutputDiscarded);
        Assert.Equal(1, result.Transport.CancelCalls);
    }

    [Fact]
    public async Task Null_cancel_response_cannot_crash_truncation_handling()
    {
        var result = await RunSimpleFaultAsync(
            AdversarialTransportFault.NullCancelAfterTruncation);
        Assert.Equal(F5SynthesisOutcome.Failed, result.Result.Outcome);
        Assert.Equal(F5Failure.StreamTruncated, result.Result.Failure);
        Assert.True(result.Result.LocalOutputDiscarded);
        Assert.True(result.Result.WorkerMayContinue);
        Assert.Null(result.Result.ComputeCancellation);
    }

    [Theory]
    [InlineData(AdversarialTransportFault.StrongCancelAcknowledgement,
        F5CancellationCapability.DiscardOnly)]
    [InlineData(AdversarialTransportFault.RequestAbortStoppedAcknowledgement,
        F5CancellationCapability.RequestAbort)]
    public async Task Cancel_ack_cannot_exceed_identity_or_claim_request_abort_stopped(
        AdversarialTransportFault fault,
        F5CancellationCapability declared)
    {
        using var scope = new F5TestScope();
        F5TestData.WriteWav(scope.SourcePath);
        using var store = scope.OpenStore();
        var snapshot = await F5TestData.SnapshotAsync(store, scope.SourcePath);
        await F5TestData.ApplyAsync(store, snapshot);
        var identity = F5TestData.WithCancellation(
            DeterministicF5FixtureIdentity.Create(), declared);
        var transport = new AdversarialF5Transport(identity, fault);
        var adapter = new F5GatewayClientAdapter(
            store, transport, F5TestData.Destination, identity, F5TestData.Clock);
        var request = F5TestData.Conversation(identity, snapshot.ReferenceRevision);
        var sink = new RecordingPcmSink();
        using var stop = new CancellationTokenSource();
        var running = adapter.SynthesizeAsync(request,
            request.Authorize(F5ConversationAuthorizationDecision.Allow,
                F5TestData.Now.AddSeconds(5)), sink, stop.Token);
        await sink.FirstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stop.CancelAsync();

        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(F5SynthesisOutcome.Canceled, result.Outcome);
        Assert.Null(result.ComputeCancellation);
        Assert.True(result.WorkerMayContinue);
        Assert.True(result.LocalOutputDiscarded);
    }

    [Fact]
    public async Task Terminal_cancel_capability_cannot_exceed_worker_identity()
    {
        var result = await RunSimpleFaultAsync(
            AdversarialTransportFault.StrongCanceledTerminal);
        Assert.Equal(F5SynthesisOutcome.Failed, result.Result.Outcome);
        Assert.Equal(F5Failure.ProtocolViolation, result.Result.Failure);
        Assert.Equal(F5CancellationCapability.DiscardOnly,
            result.Result.ComputeCancellation);
        Assert.True(result.Result.WorkerMayContinue);
    }

    [Fact]
    public async Task Deadline_covers_reference_and_cache_pre_dispatch_work()
    {
        using var scope = new F5TestScope();
        F5TestData.WriteWav(scope.SourcePath);
        using var store = scope.OpenStore();
        var snapshot = await F5TestData.SnapshotAsync(store, scope.SourcePath);
        await F5TestData.ApplyAsync(store, snapshot);
        var identity = DeterministicF5FixtureIdentity.Create();
        var transport = new AdversarialF5Transport(
            identity, AdversarialTransportFault.DelayedCache);
        var adapter = new F5GatewayClientAdapter(
            store, transport, F5TestData.Destination, identity, F5TestData.Clock);
        var request = F5TestData.Conversation(
            identity, snapshot.ReferenceRevision,
            deadline: F5TestData.Now.AddMilliseconds(100));

        var result = await adapter.SynthesizeAsync(request,
            request.Authorize(F5ConversationAuthorizationDecision.Allow,
                F5TestData.Now.AddMilliseconds(100)), new RecordingPcmSink())
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(F5SynthesisOutcome.DeadlineExceeded, result.Outcome);
        Assert.Equal(F5Failure.DeadlineExceeded, result.Failure);
        Assert.Equal(0, transport.StreamCalls);
        var apply = await store.CreateApplyPreviewAsync(
            snapshot.PresetId, snapshot.ReferenceRevision);
        await store.ApplyAsync(apply, apply.Authorize(F5ApplyDecision.Allow));
    }

    private static async Task<(F5SynthesisResult Result, AdversarialF5Transport Transport)>
        RunSimpleFaultAsync(AdversarialTransportFault fault)
    {
        using var scope = new F5TestScope();
        F5TestData.WriteWav(scope.SourcePath);
        using var store = scope.OpenStore();
        var snapshot = await F5TestData.SnapshotAsync(store, scope.SourcePath);
        await F5TestData.ApplyAsync(store, snapshot);
        var identity = DeterministicF5FixtureIdentity.Create();
        var transport = new AdversarialF5Transport(identity, fault);
        var adapter = new F5GatewayClientAdapter(
            store, transport, F5TestData.Destination, identity, F5TestData.Clock);
        var request = F5TestData.Conversation(identity, snapshot.ReferenceRevision);
        var result = await adapter.SynthesizeAsync(request,
            request.Authorize(F5ConversationAuthorizationDecision.Allow,
                F5TestData.Now.AddSeconds(5)), new RecordingPcmSink());
        return (result, transport);
    }
}
