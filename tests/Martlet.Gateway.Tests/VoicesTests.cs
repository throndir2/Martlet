using System.Net;
using System.Text.Json;
using Martlet.Core.Speakers;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

public sealed class VoicesTests
{
    private sealed class MemoryStorage(byte[]? initial = null) : IGatewayVoiceStorage
    {
        internal byte[]? Saved { get; private set; } = initial;
        public byte[]? Load() => Saved;
        public void Save(byte[] bytes) => Saved = bytes;
    }

    private static float[] Print(int seed)
    {
        var random = new Random(seed);
        return VoicePrints.Normalize(Enumerable.Range(0, VoicePrints.Dimension).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray());
    }

    private static async Task<VoiceRoster> RosterAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return VoiceRoster.Parse(JsonSerializer.SerializeToUtf8Bytes(document.RootElement.GetProperty("roster")));
    }

    [Fact]
    public async Task Paired_devices_share_and_merge_the_voice_list()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var now = host.Clock.GetUtcNow();
        var (saved, sam) = VoiceRoster.Empty.Add(Print(1), 3, "desktop-a", now.AddMinutes(-10));
        saved = saved.AddHeardName(sam!.Id, "Sam", "desktop-a", now.AddMinutes(-10));
        var storage = new MemoryStorage(saved.Write());
        host.Server.AttachVoiceStorage(storage);
        var credential = await host.PairAsync(GatewayRole.Voice);
        var signer = new GatewayRequestSigner(host.Identity, credential, host.Clock);

        using (var read = host.SignedGet("/martlet/v1/voices", GatewayRole.Voice, signer))
        using (var response = await host.Client.SendAsync(read))
            Assert.Equal("Sam", (await RosterAsync(response)).Resolve(sam.Id)!.DisplayName);

        // A second computer learned another voice and forgot nothing: both survive the merge, and the host keeps the result.
        var (other, alex) = VoiceRoster.Empty.Add(Print(2), 3, "desktop-b", now);
        using (var post = host.SignedPost("/martlet/v1/voices", GatewayRole.Voice, signer, other.Write()))
        using (var response = await host.Client.SendAsync(post))
        {
            var merged = await RosterAsync(response);
            Assert.Equal(2, merged.Live.Count);
            Assert.NotNull(merged.Resolve(alex!.Id));
            Assert.Equal(merged.Digest(), VoiceRoster.Parse(storage.Saved!).Digest());
        }

        using var anonymous = new HttpRequestMessage(HttpMethod.Get, host.Origin.CanonicalOrigin + "/martlet/v1/voices");
        using var rejected = await host.Client.SendAsync(anonymous);
        Assert.NotEqual(HttpStatusCode.OK, rejected.StatusCode);
    }
}
