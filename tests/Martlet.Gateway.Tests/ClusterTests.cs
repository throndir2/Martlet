using System.Net;
using System.Text;
using System.Text.Json;
using Martlet.Core.Cluster;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

public sealed class ClusterTests
{
    private sealed class MemoryStorage(byte[]? initial = null) : IGatewayClusterStorage
    {
        internal byte[]? Saved { get; private set; } = initial;
        public byte[]? Load() => Saved;
        public void Save(byte[] bytes) => Saved = bytes;
    }

    private static async Task<ClusterPlan> PlanAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return ClusterPlan.Parse(JsonSerializer.SerializeToUtf8Bytes(document.RootElement.GetProperty("plan")));
    }

    [Fact]
    public async Task Paired_devices_share_and_merge_the_cluster_plan()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var now = host.Clock.GetUtcNow();
        var restored = ClusterPlan.Empty.Assign(ClusterJobs.LipSync, "gpu-b", false, false, null, "desktop-a", now.AddMinutes(-10));
        var storage = new MemoryStorage(restored.Write());
        host.Server.AttachClusterStorage(storage);
        var credential = await host.PairAsync(GatewayRole.Voice);
        var signer = new GatewayRequestSigner(host.Identity, credential, host.Clock);

        using (var read = host.SignedGet("/martlet/v1/cluster", GatewayRole.Voice, signer))
        using (var response = await host.Client.SendAsync(read))
            Assert.Equal("gpu-b", (await PlanAsync(response)).For(ClusterJobs.LipSync)!.HostId);

        var newer = ClusterPlan.Empty.Assign(ClusterJobs.Thinking, "gpu-a", false, true, null, "desktop-a", now);
        using (var post = host.SignedPost("/martlet/v1/cluster", GatewayRole.Voice, signer, newer.Write()))
        using (var response = await host.Client.SendAsync(post))
        {
            var merged = await PlanAsync(response);
            Assert.Equal("gpu-a", merged.For(ClusterJobs.Thinking)!.HostId);
            Assert.Equal("gpu-b", merged.For(ClusterJobs.LipSync)!.HostId);
            Assert.Equal(merged.Digest(), ClusterPlan.Parse(storage.Saved!).Digest());
        }

        var older = ClusterPlan.Empty.Assign(ClusterJobs.Thinking, "gpu-c", false, false, null, "desktop-b", now.AddMinutes(-5));
        using (var post = host.SignedPost("/martlet/v1/cluster", GatewayRole.Voice, signer, older.Write()))
        using (var response = await host.Client.SendAsync(post))
            Assert.Equal("gpu-a", (await PlanAsync(response)).For(ClusterJobs.Thinking)!.HostId);

        var malformed = Encoding.UTF8.GetBytes("{\"schema_version\":1,\"assignments\":[],\"nodes\":[],\"extra\":true}");
        using (var post = host.SignedPost("/martlet/v1/cluster", GatewayRole.Voice, signer, malformed))
        using (var response = await host.Client.SendAsync(post))
            Assert.Equal("request.invalid", await GatewayTestHost.FailureCode(response));

        using var anonymous = new HttpRequestMessage(HttpMethod.Get, host.Origin.CanonicalOrigin + "/martlet/v1/cluster");
        using var rejected = await host.Client.SendAsync(anonymous);
        Assert.NotEqual(HttpStatusCode.OK, rejected.StatusCode);
    }
}
