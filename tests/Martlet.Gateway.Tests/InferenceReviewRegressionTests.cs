using System.Net;
using Martlet.Gateway;
using Martlet.Perception;

namespace Martlet.Gateway.Tests;

public sealed class InferenceReviewRegressionTests
{
    [Fact]
    public async Task Deadline_expiring_after_admission_releases_worker_budget()
    {
        var clock = new SteppingGatewayClock(
            new DateTimeOffset(2026, 9, 21, 20, 0, 0, TimeSpan.Zero));
        var worker = new SyntheticInferenceWorker(
            GatewayInferenceTestData.OllamaRoute());
        await using var host = await GatewayTestHost.StartAsync(
            inferenceWorkers: [worker],
            clock: clock);
        var credential = await host.PairAsync(GatewayRole.Voice);
        var signer = new GatewayRequestSigner(
            host.Identity,
            credential,
            clock);
        var now = clock.GetUtcNow();
        var body = GatewayInferenceTestData.Request(
            worker.Route,
            now,
            deadline: now.AddMilliseconds(100));
        using var request = host.SignedPost(
            worker.Route.Path,
            GatewayRole.Voice,
            signer,
            body);
        clock.AdvanceOnRead(3, TimeSpan.FromSeconds(1));

        using (var expired = await host.Client.SendAsync(request))
            Assert.Equal("job.deadline",
                await GatewayTestHost.FailureCode(expired));

        using var next = host.SignedPost(
            worker.Route.Path,
            GatewayRole.Voice,
            signer,
            GatewayInferenceTestData.Request(
                worker.Route,
                clock.GetUtcNow()));
        using var accepted = await host.Client.SendAsync(next);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var events = await GatewayInferenceTestData.ReadEventsAsync(accepted);
        Assert.Equal("completed",
            events[^1].GetProperty("type").GetString());
        Assert.Equal(1, worker.Calls);
    }

    [Fact]
    public async Task Worker_cancel_that_ignores_token_is_bounded_and_quarantined()
    {
        var worker = new SyntheticInferenceWorker(
            GatewayInferenceTestData.OllamaRoute(),
            SyntheticInferenceFault.NonCompletingCancel);
        await using var host = await GatewayTestHost.StartAsync(
            inferenceWorkers: [worker]);
        var credential = await host.PairAsync(GatewayRole.Voice);
        var signer = new GatewayRequestSigner(
            host.Identity,
            credential,
            host.Clock);
        var body = GatewayInferenceTestData.Request(
            worker.Route,
            host.Clock.GetUtcNow());
        var requestId = GatewayInferenceTestData.RequestId(body);
        using var request = host.SignedPost(
            worker.Route.Path,
            GatewayRole.Voice,
            signer,
            body);
        using var response = await host.Client.SendAsync(request);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancelBody = GatewayInferenceTestData.Cancellation(
            worker.Route,
            requestId);
        using (var cancel = host.SignedPost(
            "/martlet/v1/inference/cancel",
            GatewayRole.Voice,
            signer,
            cancelBody))
        using (var receipt = await host.Client.SendAsync(cancel)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
        {
            Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
        }

        var events = await GatewayInferenceTestData.ReadEventsAsync(response)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("stream.cleanup",
            events[^1].GetProperty("code").GetString());

        using var replacement = host.SignedPost(
            worker.Route.Path,
            GatewayRole.Voice,
            signer,
            GatewayInferenceTestData.Request(
                worker.Route,
                host.Clock.GetUtcNow()));
        using var rejected = await host.Client.SendAsync(replacement);
        Assert.Equal("worker.quarantined",
            await GatewayTestHost.FailureCode(rejected));
        Assert.Equal(1, worker.CancelCalls);
        worker.ReleaseCancellation.TrySetCanceled();
    }

    [Fact]
    public async Task F5_audio_for_nonexistent_chunk_is_not_published()
    {
        var worker = new SyntheticInferenceWorker(
            GatewayInferenceTestData.F5Route(),
            SyntheticInferenceFault.F5ExtraChunk);
        await using var host = await GatewayTestHost.StartAsync(
            inferenceWorkers: [worker]);
        var credential = await host.PairAsync(GatewayRole.Voice);
        var signer = new GatewayRequestSigner(
            host.Identity,
            credential,
            host.Clock);
        using var request = host.SignedPost(
            worker.Route.Path,
            GatewayRole.Voice,
            signer,
            GatewayInferenceTestData.Request(
                worker.Route,
                host.Clock.GetUtcNow()));

        using var response = await host.Client.SendAsync(request);
        var events = await GatewayInferenceTestData.ReadEventsAsync(response);

        Assert.Equal(
            ["started", "audio_frame", "chunk_completed", "failed"],
            events.Select(item =>
                item.GetProperty("type").GetString()!).ToArray());
        Assert.Equal("stream.invalid",
            events[^1].GetProperty("code").GetString());
        Assert.Single(events, item =>
            item.GetProperty("type").GetString() == "audio_frame");
    }

    [Fact]
    public async Task Cancel_during_completion_is_rejected_before_gate_release()
    {
        var clock = new ManualGatewayClock(
            new DateTimeOffset(2026, 9, 21, 20, 0, 0, TimeSpan.Zero));
        var worker = new SyntheticInferenceWorker(
            GatewayInferenceTestData.OllamaRoute());
        var registry = new GatewayInferenceRouteRegistry([worker], clock);
        using var certificate = GatewayTestHost.CreateCertificate(clock.GetUtcNow());
        var identity = GatewayHostIdentity.FromCertificate("fixture-host", certificate);
        var credentials = new GatewayCredentialStore(identity, clock);
        var issued = credentials.Issue("fixture-device", "Fixture device", [GatewayRole.Voice]);
        var principal = Principal(worker.Route.RequiredRole) with
        {
            CredentialId = issued.CredentialId,
            Authority = credentials
        };
        var request = GatewayInferenceJson.ParseRequest(
            GatewayInferenceTestData.Request(
                worker.Route,
                clock.GetUtcNow()),
            worker.Route,
            clock.GetUtcNow());
        var job = registry.Begin(principal, request);
        var enumerator = new BlockingDisposeEnumerator();
        var completing = job.CompleteAsync(
            enumerator,
            cancelWorker: false).AsTask();
        await enumerator.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancellation = await Assert.ThrowsAsync<GatewayProtocolException>(
            () => registry.CancelAsync(
                principal,
                new()
                {
                    RouteId = worker.Route.RouteId,
                    RequestId = request.RequestId
                }).AsTask());
        Assert.Equal("job.not_found", cancellation.Failure.Code);

        enumerator.ReleaseDispose.TrySetResult();
        Assert.True(await completing.WaitAsync(TimeSpan.FromSeconds(5)));
        await job.ReleaseAsync();
        var next = GatewayInferenceJson.ParseRequest(
            GatewayInferenceTestData.Request(
                worker.Route,
                clock.GetUtcNow()),
            worker.Route,
            clock.GetUtcNow());
        var nextJob = registry.Begin(principal, next);
        Assert.True(await nextJob.CompleteAsync(
            enumerator: null,
            cancelWorker: false));
        await nextJob.ReleaseAsync();
    }

    [Fact]
    public async Task Output_arriving_with_cancel_quarantines_the_worker()
    {
        var worker = new SyntheticInferenceWorker(
            GatewayInferenceTestData.OllamaRoute(),
            SyntheticInferenceFault.LateOnCancel);
        await using var host = await GatewayTestHost.StartAsync(
            inferenceWorkers: [worker]);
        var credential = await host.PairAsync(GatewayRole.Voice);
        var signer = new GatewayRequestSigner(
            host.Identity,
            credential,
            host.Clock);
        var body = GatewayInferenceTestData.Request(
            worker.Route,
            host.Clock.GetUtcNow());
        var requestId = GatewayInferenceTestData.RequestId(body);
        using var request = host.SignedPost(
            worker.Route.Path,
            GatewayRole.Voice,
            signer,
            body);
        using var response = await host.Client.SendAsync(request);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using (var cancel = host.SignedPost(
            "/martlet/v1/inference/cancel",
            GatewayRole.Voice,
            signer,
            GatewayInferenceTestData.Cancellation(
                worker.Route,
                requestId)))
        using (var receipt = await host.Client.SendAsync(cancel))
            Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);

        var events = await GatewayInferenceTestData.ReadEventsAsync(response);
        Assert.Equal(["started", "failed"],
            events.Select(item =>
                item.GetProperty("type").GetString()!).ToArray());
        Assert.Equal("stream.late",
            events[^1].GetProperty("code").GetString());

        using var replacement = host.SignedPost(
            worker.Route.Path,
            GatewayRole.Voice,
            signer,
            GatewayInferenceTestData.Request(
                worker.Route,
                host.Clock.GetUtcNow()));
        using var rejected = await host.Client.SendAsync(replacement);
        Assert.Equal("worker.quarantined",
            await GatewayTestHost.FailureCode(rejected));
    }

    public static IEnumerable<object[]> InvalidPerceptionOutputs()
    {
        yield return
        [
            SyntheticInferenceFault.MalformedPerception,
            "stream.invalid"
        ];
        yield return
        [
            SyntheticInferenceFault.WrongPerceptionProvenance,
            "stream.invalid"
        ];
        yield return
        [
            SyntheticInferenceFault.OversizedPerceptionOutput,
            "stream.limit"
        ];
    }

    [Theory]
    [MemberData(nameof(InvalidPerceptionOutputs))]
    public async Task Invalid_perception_output_never_completes(
        SyntheticInferenceFault fault,
        string expectedCode)
    {
        var worker = new SyntheticInferenceWorker(
            GatewayInferenceTestData.PerceptionRoute(PerceptionRole.Ocr),
            fault);
        await using var host = await GatewayTestHost.StartAsync(
            inferenceWorkers: [worker]);
        var credential = await host.PairAsync(GatewayRole.Perception);
        var signer = new GatewayRequestSigner(
            host.Identity,
            credential,
            host.Clock);
        using var request = host.SignedPost(
            worker.Route.Path,
            GatewayRole.Perception,
            signer,
            GatewayInferenceTestData.Request(
                worker.Route,
                host.Clock.GetUtcNow()));

        using var response = await host.Client.SendAsync(request);
        var events = await GatewayInferenceTestData.ReadEventsAsync(response);

        Assert.Equal(["started", "failed"],
            events.Select(item =>
                item.GetProperty("type").GetString()!).ToArray());
        Assert.Equal(expectedCode,
            events[^1].GetProperty("code").GetString());
        Assert.DoesNotContain(events, item =>
            item.GetProperty("type").GetString() == "completed");
    }

    private static GatewayPrincipal Principal(GatewayRole role) => new()
    {
        HostId = "fixture-host",
        CredentialId = "fixture-credential",
        DeviceId = "fixture-device",
        Role = role,
        CredentialLifetime = new PairedDeviceLifetime()
    };

    private sealed class BlockingDisposeEnumerator :
        IAsyncEnumerator<GatewayInferenceEvent>
    {
        internal TaskCompletionSource DisposeEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseDispose { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GatewayInferenceEvent Current =>
            throw new InvalidOperationException();
        public ValueTask<bool> MoveNextAsync() =>
            ValueTask.FromResult(false);

        public async ValueTask DisposeAsync()
        {
            DisposeEntered.TrySetResult();
            await ReleaseDispose.Task;
        }
    }
}
