using System.Net;
using System.Text.Json;
using Martlet.Gateway;
using Martlet.Gateway.Persistence;
using Martlet.Gateway.Tests;

namespace Martlet.Gateway.Persistence.Tests;

public sealed partial class OwnerTests
{
    [Theory]
    [InlineData("clean")]
    [InlineData("unclean")]
    [InlineData("renew")]
    public async Task Actual_durable_inference_body_replay_survives_owner_restart(string mode)
    {
        var origin = Origin();
        var worker = new SyntheticInferenceWorker(GatewayInferenceTestData.OllamaRoute());
        GatewayHostIdentity identity;
        IssuedDeviceCredential credential;
        HttpRequestMessage original;
        var bytes = GatewayInferenceTestData.Request(worker.Route, DateTimeOffset.UtcNow);
        await using (var host = DurableGatewayHost.CreateNew(Store, "fixture-host", origin, [], new Audit(),
            LocalGatewayDecision.Enable, inferenceWorkers: [worker]))
        {
            identity = host.Identity!;
            await host.StartAsync();
            using var client = PinnedGatewayClient.Create(origin, identity);
            credential = await Pair(host, client, origin);
            original = SignedPost(origin, identity, credential, worker.Route.Path, bytes);
            using var sent = GatewayTestHost.ClonePost(original, bytes);
            using var response = await client.SendAsync(sent);
            var events = await GatewayInferenceTestData.ReadEventsAsync(response);
            Assert.Equal("completed", events[^1].GetProperty("type").GetString());
            Assert.All(events, item => Assert.Equal(2, item.GetProperty("protocol_version").GetProperty("major").GetInt32()));
            if (mode != "unclean")
                await host.CloseCleanlyAsync();
        }
        using (original)
        await using (var host = mode == "renew"
            ? DurableGatewayHost.RenewCertificateForLocalHost(Store, origin, [], new Audit(),
                LocalGatewayDecision.Enable, inferenceWorkers: [worker])
            : DurableGatewayHost.OpenExisting(Store, origin, [], new Audit(),
                LocalGatewayDecision.Enable, inferenceWorkers: [worker]))
        {
            Assert.Equal(identity, host.Identity);
            Assert.IsType<PairedDeviceLifetime>(Assert.Single(host.ListRegistrations()).Lifetime);
            await host.StartAsync();
            using var client = PinnedGatewayClient.Create(origin, identity);
            using var replay = GatewayTestHost.ClonePost(original, bytes);
            using var rejected = await client.SendAsync(replay);
            Assert.Equal("auth.replay", await Failure(rejected));
            Assert.Equal(1, worker.Calls);
            using var next = SignedPost(origin, identity, credential, worker.Route.Path,
                GatewayInferenceTestData.Request(worker.Route, DateTimeOffset.UtcNow));
            using var accepted = await client.SendAsync(next);
            Assert.Equal("completed", (await GatewayInferenceTestData.ReadEventsAsync(accepted))[^1].GetProperty("type").GetString());
            Assert.Equal(2, worker.Calls);
            await host.CloseCleanlyAsync();
        }
    }

    [Theory]
    [InlineData((int)StoreStep.PendingFlushed)]
    [InlineData((int)StoreStep.CheckpointCommitted)]
    public async Task Failed_body_nonce_commit_never_dispatches_and_recovery_keeps_replay(int faultStep)
    {
        var clock = new Clock();
        var origin = Origin();
        var armed = false;
        var worker = new SyntheticInferenceWorker(GatewayInferenceTestData.OllamaRoute());
        GatewayHostIdentity identity;
        IssuedDeviceCredential credential;
        HttpRequestMessage original;
        var bytes = GatewayInferenceTestData.Request(worker.Route, clock.GetUtcNow());
        await using (var host = DurableGatewayHost.Open(Store, "fixture-host", origin, [], new Audit(),
            LocalGatewayDecision.Enable, HostOpenMode.Create, clock, step =>
            {
                if (armed && step == (StoreStep)faultStep)
                    throw new IOException("Controlled durable commit failure; NOT AI.");
            }, inferenceWorkers: [worker]))
        {
            identity = host.Identity!;
            await host.StartAsync();
            using var client = PinnedGatewayClient.Create(origin, identity, clock);
            credential = await Pair(host, client, origin);
            original = SignedPost(origin, identity, credential, worker.Route.Path, bytes, clock);
            armed = true;
            using var sent = GatewayTestHost.ClonePost(original, bytes);
            using var denied = await client.SendAsync(sent);
            Assert.Equal("auth.storage", await Failure(denied));
            Assert.Equal(0, worker.Calls);
        }
        using (original)
        await using (var recovered = DurableGatewayHost.Open(Store, null, origin, [], new Audit(),
            LocalGatewayDecision.Enable, HostOpenMode.Open, clock, inferenceWorkers: [worker]))
        {
            await recovered.StartAsync();
            using var client = PinnedGatewayClient.Create(origin, identity, clock);
            using var replay = GatewayTestHost.ClonePost(original, bytes);
            using var denied = await client.SendAsync(replay);
            Assert.Equal("auth.replay", await Failure(denied));
            Assert.Equal(0, worker.Calls);
            Assert.IsType<PairedDeviceLifetime>(Assert.Single(recovered.ListRegistrations()).Lifetime);
            await recovered.CloseCleanlyAsync();
        }
    }

    [Fact]
    public async Task Default_No_does_not_enumerate_inference_workers_or_create_store()
    {
        await using var host = DurableGatewayHost.CreateNew(Store, "fixture-host", Origin(), [], new Audit(),
            inferenceWorkers: ThrowingWorkers());
        Assert.False(host.Enabled);
        Assert.False(Directory.Exists(Store));
    }

    [Fact]
    public void Duplicate_inference_configuration_is_rejected_before_durable_creation()
    {
        var worker = new SyntheticInferenceWorker(GatewayInferenceTestData.OllamaRoute());
        Code("worker.invalid", () => DurableGatewayHost.CreateNew(Store, "fixture-host", Origin(), [],
            new Audit(), LocalGatewayDecision.Enable, inferenceWorkers: [worker, worker]));
        Assert.False(Directory.Exists(Store));
    }

    private static IEnumerable<IGatewayInferenceWorker> ThrowingWorkers()
    {
        yield return ForbiddenWorker();
    }

    [Fact]
    public async Task Durable_owner_cannot_mark_clean_or_release_store_while_orphaned_worker_retires()
    {
        var origin = Origin();
        var worker = new ControlledInferenceWorker(GatewayInferenceTestData.OllamaRoute())
        {
            DataGate = ControlledInferenceWorker.NewGate()
        };
        await using var host = DurableGatewayHost.CreateNew(Store, "fixture-host", origin, [], new Audit(),
            LocalGatewayDecision.Enable, inferenceWorkers: [worker]);
        await host.StartAsync();
        using var client = PinnedGatewayClient.Create(origin, host.Identity!);
        var credential = await Pair(host, client, origin);
        using var request = SignedPost(origin, host.Identity!, credential, worker.Route.Path,
            GatewayInferenceTestData.Request(worker.Route, DateTimeOffset.UtcNow));
        using var response = await client.SendAsync(request);
        try
        {
            await worker.DataPending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancel = SignedPost(origin, host.Identity!, credential, "/martlet/v1/inference/cancel",
                GatewayInferenceTestData.Cancellation(worker.Route, worker.LastRequest!.RequestId));
            using var receipt = await client.SendAsync(cancel);
            Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
            var events = await GatewayInferenceTestData.ReadEventsAsync(response).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("stream.cleanup", events[^1].GetProperty("code").GetString());
            using var stop = new CancellationTokenSource();
            var closing = host.CloseCleanlyAsync(stop.Token).AsTask();
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => closing);
            Assert.True(File.Exists(Path.Combine(Store, "running")));
            Assert.Equal(GatewayPersistenceFailure.StoreBusy, Assert.Throws<GatewayPersistenceException>(() =>
                DurableGatewayHost.OpenExisting(Store, origin, [], new Audit(), LocalGatewayDecision.Enable)).Failure);
        }
        finally { worker.DataGate.TrySetResult(); }
        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await using var recovered = DurableGatewayHost.OpenExisting(Store, origin, [], new Audit(), LocalGatewayDecision.Enable);
        Assert.IsType<PairedDeviceLifetime>(Assert.Single(recovered.ListRegistrations()).Lifetime);
        await recovered.CloseCleanlyAsync();
    }

    private static IGatewayInferenceWorker ForbiddenWorker() =>
        throw new InvalidOperationException("No must not enumerate trusted workers.");

    private static HttpRequestMessage SignedPost(GatewayOrigin origin, GatewayHostIdentity identity,
        IssuedDeviceCredential credential, string path, byte[] body, TimeProvider? clock = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, origin.CanonicalOrigin + path)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        new GatewayRequestSigner(identity, credential, clock).Sign(request, GatewayRole.Voice, body);
        return request;
    }
}
