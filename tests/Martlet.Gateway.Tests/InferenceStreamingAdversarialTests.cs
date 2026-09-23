using System.Net;
using System.Text.Json;
using Martlet.Gateway;
using Martlet.Perception;

namespace Martlet.Gateway.Tests;

public sealed class InferenceStreamingAdversarialTests
{
    public static IEnumerable<object[]> StreamFaults()
    {
        yield return
        [
            SyntheticInferenceFault.Truncated,
            "stream.truncated"
        ];
        yield return
        [
            SyntheticInferenceFault.IdentityDrift,
            "worker.identity"
        ];
        yield return
        [
            SyntheticInferenceFault.LateAfterTerminal,
            "stream.late"
        ];
        yield return
        [
            SyntheticInferenceFault.OversizedEvent,
            "stream.limit"
        ];
        yield return
        [
            SyntheticInferenceFault.TooManyEvents,
            "stream.limit"
        ];
        yield return
        [
            SyntheticInferenceFault.WorkerFailed,
            "worker.failed"
        ];
    }

    [Theory]
    [MemberData(nameof(StreamFaults))]
    public async Task Malformed_oversized_truncated_and_late_streams_fail_redacted(
        SyntheticInferenceFault fault,
        string expectedCode)
    {
        var worker = new SyntheticInferenceWorker(
            GatewayInferenceTestData.OllamaRoute(),
            fault);
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
        using var request = host.SignedPost(
            worker.Route.Path,
            GatewayRole.Voice,
            signer,
            body);

        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await GatewayInferenceTestData.ReadEventsAsync(response);

        Assert.True(events.Length >= 2);
        Assert.Equal("started", events[0].GetProperty("type").GetString());
        Assert.Equal("failed", events[^1].GetProperty("type").GetString());
        Assert.Equal(
            Enumerable.Range(0, events.Length).Select(index => (long)index),
            events.Select(item =>
                item.GetProperty("sequence").GetInt64()));
        Assert.Equal(expectedCode,
            events[^1].GetProperty("code").GetString());
        Assert.False(events[^1].TryGetProperty("data_base64", out _));
        var bodyText = events[^1].GetRawText();
        Assert.DoesNotContain(nameof(GatewayInferenceWorkerException),
            bodyText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(worker.Route.WorkerId + "-drift",
            bodyText,
            StringComparison.Ordinal);
        var traceId = events[^1].GetProperty("trace_id").GetGuid();
        var audit = Assert.Single(host.Audit.Events);
        Assert.Equal(traceId, audit.TraceId);
        Assert.Equal(expectedCode, audit.Code);
    }

    [Fact]
    public async Task Identity_drift_quarantines_route_before_replacement_work()
    {
        var worker = new SyntheticInferenceWorker(
            GatewayInferenceTestData.OllamaRoute(),
            SyntheticInferenceFault.IdentityDrift);
        await using var host = await GatewayTestHost.StartAsync(
            inferenceWorkers: [worker]);
        var credential = await host.PairAsync(GatewayRole.Voice);
        var signer = new GatewayRequestSigner(
            host.Identity,
            credential,
            host.Clock);

        using (var firstRequest = host.SignedPost(
            worker.Route.Path,
            GatewayRole.Voice,
            signer,
            GatewayInferenceTestData.Request(
                worker.Route,
                host.Clock.GetUtcNow())))
        using (var firstResponse = await host.Client.SendAsync(firstRequest))
        {
            var events = await GatewayInferenceTestData.ReadEventsAsync(
                firstResponse);
            Assert.Equal("worker.identity",
                events[^1].GetProperty("code").GetString());
        }

        using var secondRequest = host.SignedPost(
            worker.Route.Path,
            GatewayRole.Voice,
            signer,
            GatewayInferenceTestData.Request(
                worker.Route,
                host.Clock.GetUtcNow()));
        using var secondResponse = await host.Client.SendAsync(secondRequest);
        Assert.Equal("worker.quarantined",
            await GatewayTestHost.FailureCode(secondResponse));
        Assert.Equal(1, worker.Calls);
    }

    [Fact]
    public async Task Explicit_cancel_is_credential_scoped_and_discards_late_output()
    {
        var worker = new SyntheticInferenceWorker(
            GatewayInferenceTestData.OllamaRoute(),
            SyntheticInferenceFault.BlockUntilCanceled);
        await using var host = await GatewayTestHost.StartAsync(
            inferenceWorkers: [worker]);
        var owner = await host.PairAsync(GatewayRole.Voice);
        var other = await host.PairAsync(GatewayRole.Voice);
        var ownerSigner = new GatewayRequestSigner(
            host.Identity,
            owner,
            host.Clock);
        var otherSigner = new GatewayRequestSigner(
            host.Identity,
            other,
            host.Clock);
        var body = GatewayInferenceTestData.Request(
            worker.Route,
            host.Clock.GetUtcNow());
        var requestId = GatewayInferenceTestData.RequestId(body);
        using var request = host.SignedPost(
            worker.Route.Path,
            GatewayRole.Voice,
            ownerSigner,
            body);
        using var response = await host.Client.SendAsync(request);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancelBody = GatewayInferenceTestData.Cancellation(
            worker.Route,
            requestId);
        using (var unauthorizedCancel = host.SignedPost(
            "/martlet/v1/inference/cancel",
            GatewayRole.Voice,
            otherSigner,
            cancelBody))
        using (var denied = await host.Client.SendAsync(unauthorizedCancel))
        {
            Assert.Equal("job.not_found",
                await GatewayTestHost.FailureCode(denied));
        }

        using (var cancel = host.SignedPost(
            "/martlet/v1/inference/cancel",
            GatewayRole.Voice,
            ownerSigner,
            cancelBody))
        using (var canceled = await host.Client.SendAsync(cancel))
        {
            Assert.Equal(HttpStatusCode.OK, canceled.StatusCode);
            using var receipt = JsonDocument.Parse(
                await canceled.Content.ReadAsStringAsync());
            Assert.True(receipt.RootElement
                .GetProperty("local_discard_acknowledged").GetBoolean());
            Assert.True(receipt.RootElement
                .GetProperty("worker_may_continue").GetBoolean());
        }

        var events = await GatewayInferenceTestData.ReadEventsAsync(response);
        Assert.False(events[^1].TryGetProperty("code", out var cancelFailure) &&
            cancelFailure.GetString() != "job.canceled",
            events[^1].GetRawText());
        Assert.Equal(["started", "canceled"],
            events.Select(item =>
                item.GetProperty("type").GetString()!).ToArray());
        Assert.Equal([0L, 1L],
            events.Select(item =>
                item.GetProperty("sequence").GetInt64()).ToArray());
        Assert.Equal(1, worker.CancelCalls);
        Assert.Equal(1, worker.Calls);
    }

    [Fact]
    public async Task Concurrent_requests_share_one_role_worker_budget_without_a_queue()
    {
        var worker = new SyntheticInferenceWorker(
            GatewayInferenceTestData.OllamaRoute(),
            SyntheticInferenceFault.BlockUntilCanceled);
        await using var host = await GatewayTestHost.StartAsync(
            inferenceWorkers: [worker]);
        var credential = await host.PairAsync(GatewayRole.Voice);
        var signer = new GatewayRequestSigner(
            host.Identity,
            credential,
            host.Clock);
        var firstBody = GatewayInferenceTestData.Request(
            worker.Route,
            host.Clock.GetUtcNow());
        var firstId = GatewayInferenceTestData.RequestId(firstBody);
        using var firstRequest = host.SignedPost(
            worker.Route.Path,
            GatewayRole.Voice,
            signer,
            firstBody);
        using var firstResponse = await host.Client.SendAsync(firstRequest);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondBody = GatewayInferenceTestData.Request(
            worker.Route,
            host.Clock.GetUtcNow());
        using (var secondRequest = host.SignedPost(
            worker.Route.Path,
            GatewayRole.Voice,
            signer,
            secondBody))
        using (var busy = await host.Client.SendAsync(secondRequest))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
            Assert.Equal("job.busy",
                await GatewayTestHost.FailureCode(busy));
        }
        Assert.Equal(1, worker.Calls);

        var cancelBody = GatewayInferenceTestData.Cancellation(
            worker.Route,
            firstId);
        using (var cancel = host.SignedPost(
            "/martlet/v1/inference/cancel",
            GatewayRole.Voice,
            signer,
            cancelBody))
        using (var canceled = await host.Client.SendAsync(cancel))
            Assert.Equal(HttpStatusCode.OK, canceled.StatusCode);
        _ = await GatewayInferenceTestData.ReadEventsAsync(firstResponse);
        Assert.Equal(1, worker.MaximumActive);
    }

    [Fact]
    public async Task Deadline_cancels_and_bounds_a_noncompleting_worker()
    {
        var worker = new SyntheticInferenceWorker(
            GatewayInferenceTestData.OllamaRoute(),
            SyntheticInferenceFault.BlockUntilCanceled);
        await using var host = await GatewayTestHost.StartAsync(
            inferenceWorkers: [worker]);
        var credential = await host.PairAsync(GatewayRole.Voice);
        var signer = new GatewayRequestSigner(
            host.Identity,
            credential,
            host.Clock);
        var body = GatewayInferenceTestData.Request(
            worker.Route,
            host.Clock.GetUtcNow(),
            deadline: host.Clock.GetUtcNow().AddMilliseconds(150));
        using var request = host.SignedPost(
            worker.Route.Path,
            GatewayRole.Voice,
            signer,
            body);

        using var response = await host.Client.SendAsync(request);
        var events = await GatewayInferenceTestData.ReadEventsAsync(response)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["started", "failed"],
            events.Select(item =>
                item.GetProperty("type").GetString()!).ToArray());
        Assert.Equal("job.deadline",
            events[^1].GetProperty("code").GetString());
        Assert.Equal(1, worker.CancelCalls);
    }

    [Fact]
    public async Task Host2_context_loss_is_bounded_and_voice_remains_independent()
    {
        var perception = new SyntheticInferenceWorker(
            GatewayInferenceTestData.PerceptionRoute(PerceptionRole.Ocr),
            SyntheticInferenceFault.OptionalContextUnavailable);
        var voice = new SyntheticInferenceWorker(
            GatewayInferenceTestData.OllamaRoute());
        await using var host = await GatewayTestHost.StartAsync(
            inferenceWorkers: [perception, voice]);

        var perceptionCredential = await host.PairAsync(
            GatewayRole.Perception);
        var perceptionSigner = new GatewayRequestSigner(
            host.Identity,
            perceptionCredential,
            host.Clock);
        using (var request = host.SignedPost(
            perception.Route.Path,
            GatewayRole.Perception,
            perceptionSigner,
            GatewayInferenceTestData.Request(
                perception.Route,
                host.Clock.GetUtcNow())))
        using (var response = await host.Client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable,
                response.StatusCode);
            Assert.Equal("context.unavailable",
                await GatewayTestHost.FailureCode(response));
        }

        var voiceCredential = await host.PairAsync(GatewayRole.Voice);
        var voiceSigner = new GatewayRequestSigner(
            host.Identity,
            voiceCredential,
            host.Clock);
        using var voiceRequest = host.SignedPost(
            voice.Route.Path,
            GatewayRole.Voice,
            voiceSigner,
            GatewayInferenceTestData.Request(
                voice.Route,
                host.Clock.GetUtcNow()));
        using var voiceResponse = await host.Client.SendAsync(voiceRequest);
        Assert.Equal(HttpStatusCode.OK, voiceResponse.StatusCode);
        var voiceEvents = await GatewayInferenceTestData.ReadEventsAsync(
            voiceResponse);
        Assert.Equal("completed",
            voiceEvents[^1].GetProperty("type").GetString());
        Assert.Equal(1, perception.Calls);
        Assert.Equal(1, voice.Calls);
    }

    [Fact]
    public async Task Slow_consumer_receives_a_serial_bounded_stream()
    {
        var worker = new SyntheticInferenceWorker(
            GatewayInferenceTestData.OllamaRoute(),
            dataEvents: 32);
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
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var sequence = 0L;
        while (await reader.ReadLineAsync() is { } line)
        {
            await Task.Delay(5);
            using var document = JsonDocument.Parse(line);
            Assert.Equal(sequence++,
                document.RootElement.GetProperty("sequence").GetInt64());
        }

        Assert.Equal(34, sequence);
        Assert.Equal(1, worker.MaximumActive);
        Assert.Equal(1, worker.Calls);
    }

    [Fact]
    public async Task Disconnect_cancels_owned_worker_and_never_admits_late_output()
    {
        var worker = new SyntheticInferenceWorker(
            GatewayInferenceTestData.OllamaRoute(),
            SyntheticInferenceFault.BlockUntilCanceled);
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
        var response = await host.Client.SendAsync(request);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        response.Dispose();

        var timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        while (worker.CancelCalls == 0 && DateTimeOffset.UtcNow < timeout)
            await Task.Delay(10);
        Assert.Equal(1, worker.CancelCalls);
        Assert.Equal(1, worker.MaximumActive);
    }
}
