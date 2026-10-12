using System.Net;
using System.Text;
using System.Text.Json;
using Martlet.Core.Network;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

/// <summary>Sharing hosts with a link (docs/NETWORK.md, "Sharing with a link"): the owner's desktop keeps a share link's verifier
/// on each host it covers, a friend's computer signs in with the link's secret and gets a friend's credential (engines only),
/// and reads which of the owner's hosts the link covers so it can sign in to hosts added later.</summary>
public sealed class ShareLinkSignInTests
{
    private static readonly object Version = new { major = 2, minor = 0 };

    private sealed class MemorySignInStorage : IGatewaySignInStorage
    {
        internal byte[]? Bytes;
        public byte[]? Load() => Bytes;
        public void Save(byte[] bytes) => Bytes = bytes;
    }

    private sealed class RosterStorage(byte[] bytes) : IGatewayNetworkStorage
    {
        private byte[]? bytes = bytes;
        public byte[]? Load() => bytes;
        public void Save(byte[] value) => bytes = value;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ReadAsync(HttpResponseMessage response)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return (response.StatusCode, document.RootElement.Clone());
    }

    private static HttpRequestMessage Post(GatewayTestHost host, string path, object body) =>
        new(HttpMethod.Post, host.Origin.CanonicalOrigin + path) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static async Task<JsonElement> ChangeAsync(GatewayTestHost host, GatewayRequestSigner owner, object change, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await host.Client.SendAsync(host.SignedPost("/martlet/v1/signin/settings", GatewayRole.Voice, owner,
            JsonSerializer.SerializeToUtf8Bytes(change)));
        var (status, body) = await ReadAsync(response);
        Assert.True(status == expected, body.GetRawText());
        return body;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SignInAsync(GatewayTestHost host, string link, string secret, string device)
    {
        using var begin = await host.Client.SendAsync(Post(host, "/martlet/v1/signin/begin", new { protocol_version = Version, provider = "link" }));
        var (beginStatus, beginBody) = await ReadAsync(begin);
        if (beginStatus != HttpStatusCode.OK) return (beginStatus, beginBody);
        using var complete = await host.Client.SendAsync(Post(host, "/martlet/v1/signin/complete", new
        {
            protocol_version = Version, attempt_id = beginBody.GetProperty("attempt_id").GetString(), device_id = device,
            display_name = device.ToUpperInvariant(), proof = new { link, secret }
        }));
        return await ReadAsync(complete);
    }

    private static ShareLink NewLink(GatewayTestHost host) => ShareLink.Create(
        [new NetworkInvite { HostId = host.Identity.HostId, SpkiFingerprint = host.Identity.SpkiFingerprint, Origin = host.Origin.CanonicalOrigin }],
        null, "Sam", "Ana");

    [Fact]
    public async Task A_share_link_signs_a_friend_in_for_the_engines_only_and_removing_it_revokes_them()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var storage = new MemorySignInStorage();
        host.Server.AttachSignInStorage(storage);
        var owner = new GatewayRequestSigner(host.Identity, await host.PairAsync(deviceId: "home-pc"), host.Clock);
        var link = NewLink(host);

        // Before any link, nobody signs in that way.
        Assert.Equal("signin.unavailable", (await SignInAsync(host, link.Id, link.Secret, "friend-pc")).Body.GetProperty("code").GetString());
        var settings = await ChangeAsync(host, owner, new
        {
            action = "share-link", id = link.Id, label = "Ana", verifier = ShareLink.Verifier(link.Id, link.Secret)
        });
        // A link alone makes sign-in usable here (so the host may be reached from outside), and the settings never show its verifier.
        Assert.True(settings.GetProperty("usable").GetBoolean());
        var listed = Assert.Single(settings.GetProperty("links").EnumerateArray());
        Assert.Equal((link.Id, "Ana"), (listed.GetProperty("id").GetString(), listed.GetProperty("label").GetString()));
        Assert.False(listed.TryGetProperty("verifier", out _));
        Assert.DoesNotContain(link.Secret, Encoding.UTF8.GetString(storage.Bytes!));
        using (var ways = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, host.Origin.CanonicalOrigin + "/martlet/v1/signin")))
            Assert.True((await ReadAsync(ways)).Body.GetProperty("share_links").GetBoolean());

        // A wrong secret, or another link's ID, is refused.
        var other = NewLink(host);
        Assert.Equal("signin.invalid", (await SignInAsync(host, link.Id, other.Secret, "friend-pc")).Body.GetProperty("code").GetString());
        Assert.Equal("signin.invalid", (await SignInAsync(host, other.Id, link.Secret, "friend-pc")).Body.GetProperty("code").GetString());
        // The link can't change its secret under the same ID (that would sign its friend out everywhere).
        await ChangeAsync(host, owner, new { action = "share-link", id = link.Id, verifier = ShareLink.Verifier(link.Id, other.Secret) },
            HttpStatusCode.BadRequest);
        // A link is no provider identity: it can't be allowed or configured as one.
        await ChangeAsync(host, owner, new { action = "allow", provider = "link", subject = link.Id, access = "friend" }, HttpStatusCode.BadRequest);

        var (status, body) = await SignInAsync(host, link.Id, link.Secret, "friend-pc");
        Assert.True(status == HttpStatusCode.Created, body.GetRawText());
        Assert.Equal("friend", body.GetProperty("access").GetString());
        Assert.Equal(("link", link.Id, "Ana"), (body.GetProperty("signed_in").GetProperty("provider").GetString(),
            body.GetProperty("signed_in").GetProperty("subject").GetString(), body.GetProperty("signed_in").GetProperty("label").GetString()));
        Assert.False(body.TryGetProperty("account_id", out _));
        var friend = FriendAccessTests.Signer(host, body);
        Assert.Contains(host.Server.Credentials.ListRegistrations(), r => r is { DeviceId: "friend-pc", Access: GatewayAccess.Friend });
        using (var version = await host.Client.SendAsync(host.SignedGet("/martlet/v1/version", GatewayRole.Voice, friend)))
            Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        using (var refused = await host.Client.SendAsync(host.SignedGet("/martlet/v1/signin/settings", GatewayRole.Voice, friend)))
            Assert.Equal("access.friend", await GatewayTestHost.FailureCode(refused));
        // The friend's computer shows as one of the link's.
        var enrolled = Assert.Single((await ChangeAsync(host, owner, new { action = "share-link", id = link.Id, label = "Ana B",
            verifier = ShareLink.Verifier(link.Id, link.Secret) })).GetProperty("enrolled").EnumerateArray());
        Assert.Equal(("friend-pc", "link", "friend"), (enrolled.GetProperty("device_id").GetString(), enrolled.GetProperty("provider").GetString(),
            enrolled.GetProperty("access").GetString()));

        // Removing the link revokes the friend's computer at once.
        var removed = await ChangeAsync(host, owner, new { action = "remove-share-link", id = link.Id });
        Assert.Empty(removed.GetProperty("links").EnumerateArray());
        Assert.Empty(removed.GetProperty("enrolled").EnumerateArray());
        using (var revoked = await host.Client.SendAsync(host.SignedGet("/martlet/v1/version", GatewayRole.Voice, friend)))
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal("signin.not_set_up", (await ChangeAsync(host, owner, new { action = "remove-share-link", id = link.Id })).GetProperty("blocked_reason").GetString());
        // A stopped link stays stopped: no computer of the owner's adds it again.
        Assert.Equal("signin.link_removed", (await ChangeAsync(host, owner, new
        {
            action = "share-link", id = link.Id, label = "Ana", verifier = ShareLink.Verifier(link.Id, link.Secret)
        }, HttpStatusCode.Conflict)).GetProperty("code").GetString());
        Assert.Contains(link.Id, (await ChangeAsync(host, owner, new { action = "remove-share-link", id = other.Id }))
            .GetProperty("removed_links").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task A_friend_reads_the_hosts_its_link_covers_from_the_roster()
    {
        await using var host = await GatewayTestHost.StartAsync();
        host.Server.AttachSignInStorage(new MemorySignInStorage());
        var owner = new GatewayRequestSigner(host.Identity, await host.PairAsync(deviceId: "desktop-home"), host.Clock);
        var now = host.Clock.GetUtcNow();
        using var founder = NetworkKey.Create("desktop-home");
        var roster = NetworkRoster.Found(founder, "HOME", now)
            .AddHost(founder, host.Identity.HostId, "Fixture", host.Origin.CanonicalOrigin, host.Identity.SpkiFingerprint, now)
            .AddHost(founder, "gpu-box", "GPU box", "https://192.168.1.20:9443", "sha256:" + new string('b', 64), now)
            .AddHost(founder, "lab", "Lab", "https://192.168.1.30:9443", "sha256:" + new string('c', 64), now);
        roster = roster.SetHostAddresses(founder, "gpu-box", ["home.example.net:9444"], now);
        host.Server.AttachNetworkStorage(new RosterStorage(roster.Write()));

        var all = NewLink(host);
        var some = NewLink(host);
        await ChangeAsync(host, owner, new { action = "share-link", id = all.Id, label = "Ana", verifier = ShareLink.Verifier(all.Id, all.Secret) });
        await ChangeAsync(host, owner, new
        {
            action = "share-link", id = some.Id, label = "Bo", verifier = ShareLink.Verifier(some.Id, some.Secret),
            hosts = new[] { host.Identity.HostId, "gpu-box" }
        });

        async Task<JsonElement> SharedAsync(GatewayRequestSigner signer)
        {
            using var response = await host.Client.SendAsync(host.SignedGet("/martlet/v1/signin/shared", GatewayRole.Voice, signer));
            var (status, body) = await ReadAsync(response);
            Assert.True(status == HttpStatusCode.OK, body.GetRawText());
            return body;
        }

        var ana = FriendAccessTests.Signer(host, (await SignInAsync(host, all.Id, all.Secret, "ana-pc")).Body);
        var shared = await SharedAsync(ana);
        Assert.True(shared.GetProperty("all_hosts").GetBoolean());
        Assert.Equal(roster.NetworkId, shared.GetProperty("network_id").GetString());
        Assert.Equal(new[] { host.Identity.HostId, "gpu-box", "lab" }.Order(), shared.GetProperty("hosts").EnumerateArray().Select(h => h.GetProperty("host_id").GetString()).Order());
        var gpu = shared.GetProperty("hosts").EnumerateArray().Single(h => h.GetProperty("host_id").GetString() == "gpu-box");
        Assert.Equal(("https://192.168.1.20:9443", "sha256:" + new string('b', 64), "home.example.net:9444"),
            (gpu.GetProperty("origin").GetString(), gpu.GetProperty("spki").GetString(), Assert.Single(gpu.GetProperty("addresses").EnumerateArray()).GetString()));

        var bo = FriendAccessTests.Signer(host, (await SignInAsync(host, some.Id, some.Secret, "bo-pc")).Body);
        shared = await SharedAsync(bo);
        Assert.False(shared.GetProperty("all_hosts").GetBoolean());
        Assert.Equal(new[] { host.Identity.HostId, "gpu-box" }.Order(), shared.GetProperty("hosts").EnumerateArray().Select(h => h.GetProperty("host_id").GetString()).Order());

        // Only a friend's computer asks this (the owner's computers have the roster).
        using var mine = await host.Client.SendAsync(host.SignedGet("/martlet/v1/signin/shared", GatewayRole.Voice, owner));
        Assert.Equal(HttpStatusCode.BadRequest, mine.StatusCode);
    }

    [Fact]
    public void Share_links_round_trip_and_refuse_damaged_text()
    {
        var hosts = new[]
        {
            new NetworkInvite { HostId = "home-pc", SpkiFingerprint = "sha256:" + new string('a', 64), Origin = "https://192.168.1.10:9443",
                Addresses = ["203.0.113.7:9443"], Label = "Home PC" },
            new NetworkInvite { HostId = "gpu-box", SpkiFingerprint = "sha256:" + new string('b', 64), Origin = "https://192.168.1.20:9443" }
        };
        var link = ShareLink.Create(hosts, "net-1", "Sam", "Ana");
        Assert.StartsWith("martlet://share/", link.Url());
        foreach (var text in new[] { link.Url(), link.Text(), "  " + link.Url() + "/ \n", "<" + link.Url() + ">" })
        {
            var parsed = ShareLink.Parse(text);
            Assert.Equal((link.Id, link.Secret, "net-1", "Sam", "Ana"), (parsed.Id, parsed.Secret, parsed.NetworkId, parsed.From, parsed.Label));
            Assert.Equal(["home-pc", "gpu-box"], parsed.Hosts.Select(h => h.HostId));
            Assert.Equal(["203.0.113.7:9443"], parsed.Hosts[0].Addresses);
            Assert.Equal("net-1", parsed.Hosts[1].NetworkId);
        }
        Assert.True(ShareLink.LooksLike(link.Text()));
        Assert.False(ShareLink.LooksLike("martlet-invite-v1.abc"));
        Assert.True(ShareLink.Matches(link.Id, link.Secret, ShareLink.Verifier(link.Id, link.Secret)));
        Assert.False(ShareLink.Matches(link.Id, link.Secret[..^1] + (link.Secret[^1] == 'A' ? 'B' : 'A'), ShareLink.Verifier(link.Id, link.Secret)));
        Assert.Throws<Martlet.Core.Contracts.ContractException>(() => ShareLink.Parse(link.Url()[..^4]));
        Assert.Throws<Martlet.Core.Contracts.ContractException>(() => ShareLink.Parse("martlet://share/"));
        Assert.Throws<Martlet.Core.Contracts.ContractException>(() => ShareLink.Create([], null, null, null).Url());
    }
}
