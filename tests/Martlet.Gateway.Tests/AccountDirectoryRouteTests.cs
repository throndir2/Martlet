using System.Net;
using System.Text;
using System.Text.Json;
using Martlet.Core.Accounts;
using Martlet.Core.Network;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

public sealed class AccountDirectoryRouteTests
{
    private const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";

    private sealed class Storage(byte[]? initial = null) : IGatewayAccountStorage, IGatewayNetworkStorage
    {
        internal byte[]? Saved { get; private set; } = initial;
        internal int Saves { get; private set; }
        public byte[]? Load() => Saved;
        public void Save(byte[] bytes)
        {
            Saved = bytes;
            Saves++;
        }
    }

    private static async Task<(AccountDirectory Directory, string Digest, int Rejected)> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        return (AccountDirectory.Parse(JsonSerializer.SerializeToUtf8Bytes(root.GetProperty("accounts"))),
            root.GetProperty("digest").GetString()!, root.GetProperty("rejected").GetInt32());
    }

    private static Account Owner(string deviceId) => Account.Create("Sam", AccountRoles.Owner)
        .WithLogin(AccountLogin.For(AccountLoginKey.ForWindows(deviceId, Sid), "sam@example.net", DateTimeOffset.UtcNow))
        .WithDevice(AccountDevice.For(deviceId, AccountLoginKey.ForWindows(deviceId, Sid), DateTimeOffset.UtcNow));

    [Fact]
    public async Task A_bound_host_keeps_entries_its_member_desktops_signed_and_refuses_the_rest()
    {
        await using var host = await GatewayTestHost.StartAsync();
        using var founder = NetworkKey.Create("desktop-a");
        using var outsider = NetworkKey.Create("desktop-x");
        var now = host.Clock.GetUtcNow();
        var roster = NetworkRoster.Found(founder, "A", now)
            .AddHost(founder, host.Identity.HostId, "Fixture", host.Origin.CanonicalOrigin, host.Identity.SpkiFingerprint, now);
        host.Server.AttachNetworkStorage(new Storage(roster.Write()));
        var storage = new Storage();
        host.Server.AttachAccountStorage(storage);
        var signer = new GatewayRequestSigner(host.Identity, await host.PairAsync(GatewayRole.Voice, "desktop-a"), host.Clock);

        var sam = Owner("desktop-a");
        var mine = AccountDirectory.Empty.Put(founder, sam, now);
        using (var post = await host.Client.SendAsync(host.SignedPost(GatewayHttpApplication.AccountsPath, GatewayRole.Voice, signer, mine.Write())))
        {
            var (merged, digest, rejected) = await ReadAsync(post);
            Assert.Equal(0, rejected);
            Assert.Equal(mine.Digest(), digest);
            Assert.Equal("Sam", merged.Find(sam.Id)!.Name);
            Assert.Equal(mine.Digest(), AccountDirectory.Parse(storage.Saved!).Digest());
        }

        // A computer outside the network and a forged entry are refused; nothing is saved again.
        var forged = mine with { Accounts = [mine.Accounts[0] with { Role = AccountRoles.Member, Revision = mine.Accounts[0].Revision + 1 }] };
        var foreign = AccountDirectory.Merge(forged, AccountDirectory.Empty.Put(outsider, Account.Create("Eve", AccountRoles.Owner), now));
        using (var post = await host.Client.SendAsync(host.SignedPost(GatewayHttpApplication.AccountsPath, GatewayRole.Voice, signer, foreign.Write())))
        {
            var (merged, digest, rejected) = await ReadAsync(post);
            Assert.Equal(2, rejected);
            Assert.Equal(mine.Digest(), digest);
            Assert.Single(merged.Accounts);
            Assert.Equal(1, storage.Saves);
        }

        using (var get = await host.Client.SendAsync(host.SignedGet(GatewayHttpApplication.AccountsPath, GatewayRole.Voice, signer)))
        {
            var (read, digest, rejected) = await ReadAsync(get);
            Assert.Equal(0, rejected);
            Assert.Equal(mine.Digest(), digest);
            Assert.True(AccountDirectory.Verify(read.Accounts[0], roster));
        }
        using (var get = await host.Client.SendAsync(host.SignedGet(GatewayHttpApplication.AccountsDigestPath, GatewayRole.Voice, signer)))
        {
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            using var document = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
            Assert.Equal(mine.Digest(), document.RootElement.GetProperty("digest").GetString());
        }
        using (var own = await host.Client.SendAsync(host.SignedGet("/martlet/v1/logs?own_after=0", GatewayRole.Voice, signer)))
        {
            using var document = JsonDocument.Parse(await own.Content.ReadAsStringAsync());
            Assert.Contains(document.RootElement.GetProperty("entries").EnumerateArray(), e =>
                e.GetProperty("message").GetString()!.Contains("Refused 2 account entries from desktop-a", StringComparison.Ordinal));
        }

        // A restarted host loads its saved copy.
        await using var restarted = await GatewayTestHost.StartAsync();
        restarted.Server.AttachAccountStorage(new Storage(storage.Saved));
        var other = new GatewayRequestSigner(restarted.Identity, await restarted.PairAsync(GatewayRole.Voice, "desktop-a"), restarted.Clock);
        using var reread = await restarted.Client.SendAsync(restarted.SignedGet(GatewayHttpApplication.AccountsDigestPath, GatewayRole.Voice, other));
        using var reDocument = JsonDocument.Parse(await reread.Content.ReadAsStringAsync());
        Assert.Equal(mine.Digest(), reDocument.RootElement.GetProperty("digest").GetString());
    }

    [Fact]
    public async Task A_host_in_no_network_takes_no_entries()
    {
        await using var host = await GatewayTestHost.StartAsync();
        using var key = NetworkKey.Create("desktop-a");
        var storage = new Storage();
        host.Server.AttachAccountStorage(storage);
        var signer = new GatewayRequestSigner(host.Identity, await host.PairAsync(GatewayRole.Voice, "desktop-a"), host.Clock);
        var mine = AccountDirectory.Empty.Put(key, Owner("desktop-a"), host.Clock.GetUtcNow());
        using var post = await host.Client.SendAsync(host.SignedPost(GatewayHttpApplication.AccountsPath, GatewayRole.Voice, signer, mine.Write()));
        var (merged, digest, rejected) = await ReadAsync(post);
        Assert.Equal(1, rejected);
        Assert.Empty(merged.Accounts);
        Assert.Equal(AccountDirectory.Empty.Digest(), digest);
        Assert.Null(storage.Saved);
    }

    [Fact]
    public async Task Friends_unsigned_requests_and_malformed_copies_are_refused()
    {
        await using var host = await GatewayTestHost.StartAsync();
        host.Server.AttachAccountStorage(new Storage());
        var owner = await FriendAccessTests.SetUpSignInAsync(host);
        var friend = await FriendAccessTests.FriendAsync(host, owner);
        foreach (var path in new[] { GatewayHttpApplication.AccountsPath, GatewayHttpApplication.AccountsDigestPath })
        {
            using var refused = await host.Client.SendAsync(host.SignedGet(path, GatewayRole.Voice, friend));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("access.friend", await GatewayTestHost.FailureCode(refused));
        }
        using (var refused = await host.Client.SendAsync(host.SignedPost(GatewayHttpApplication.AccountsPath, GatewayRole.Voice, friend, "{}"u8.ToArray())))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("access.friend", await GatewayTestHost.FailureCode(refused));
        }

        using (var anonymous = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, host.Origin.CanonicalOrigin + GatewayHttpApplication.AccountsPath)))
            Assert.NotEqual(HttpStatusCode.OK, anonymous.StatusCode);

        using (var malformed = await host.Client.SendAsync(host.SignedPost(GatewayHttpApplication.AccountsPath, GatewayRole.Voice, owner,
            Encoding.UTF8.GetBytes("""{"schema_version":1,"accounts":[{"id":"not-a-guid"}]}"""))))
            Assert.Equal("request.invalid", await GatewayTestHost.FailureCode(malformed));
        using (var digestPost = await host.Client.SendAsync(host.SignedPost(GatewayHttpApplication.AccountsDigestPath, GatewayRole.Voice, owner, "{}"u8.ToArray())))
            Assert.Equal("request.invalid", await GatewayTestHost.FailureCode(digestPost));
    }
}
