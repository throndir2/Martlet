using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Martlet.Gateway;
using Martlet.Gateway.Persistence;
using Martlet.Gateway.Tests;

namespace Martlet.Gateway.Persistence.Portable.Tests;

public sealed class InferenceTests
{
    [Fact]
    public async Task Linux_nonce_commit_failure_cannot_dispatch_worker_and_reopen_keeps_replay()
    {
        using var fs = new FakeLinuxFileSystem();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var origin = new GatewayOrigin($"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        listener.Stop();
        var worker = new SyntheticInferenceWorker(GatewayInferenceTestData.OllamaRoute());
        var body = GatewayInferenceTestData.Request(worker.Route, DateTimeOffset.UtcNow);
        var armed = false;
        HttpRequestMessage original;
        GatewayHostIdentity identity;
        await using (var owner = Open(HostOpenMode.Create))
        {
            identity = owner.Identity!;
            await owner.StartAsync();
            using var client = PinnedGatewayClient.Create(origin, identity);
            var credential = await OwnerScenario.Pair(owner, client, origin);
            original = new(HttpMethod.Post, origin.CanonicalOrigin + worker.Route.Path) { Content = new ByteArrayContent(body) };
            original.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
            new GatewayRequestSigner(identity, credential).Sign(original, GatewayRole.Voice, body);
            armed = true;
            using var sent = GatewayTestHost.ClonePost(original, body);
            using var response = await client.SendAsync(sent);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("auth.storage", json.RootElement.GetProperty("code").GetString());
            Assert.Equal(0, worker.Calls);
        }
        armed = false;
        using (original)
        await using (var owner = Open(HostOpenMode.Open))
        {
            await owner.StartAsync();
            using var client = PinnedGatewayClient.Create(origin, identity);
            using var sent = GatewayTestHost.ClonePost(original, body);
            using var response = await client.SendAsync(sent);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("auth.replay", json.RootElement.GetProperty("code").GetString());
            Assert.Equal(0, worker.Calls);
            Assert.Single(owner.ListRegistrations());
            await owner.CloseCleanlyAsync();
        }

        DurableGatewayHost Open(HostOpenMode mode) => DurableGatewayHost.Open(Authority.Path, "fixture-host", origin,
            [], new OwnerScenario.Audit(), LocalGatewayDecision.Enable, mode, TimeProvider.System,
            step => { if (armed && step == StoreStep.PendingFlushed) throw new IOException("Synthetic interruption."); },
            inferenceWorkers: [worker], storageBackend: GatewayStorageBackend.LinuxServicePermissions, linuxFileSystem: fs);
    }
}
