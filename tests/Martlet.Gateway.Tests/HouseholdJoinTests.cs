using System.Net;
using System.Text;
using System.Text.Json;
using Martlet.Core.Network;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

/// <summary>A new computer that joins by signing in to a household account (docs/ACCOUNTS.md, W13): the host's answer to
/// member desktops names the account the sign-in proves, so the member desktop lets it in as that person's computer.</summary>
public sealed class HouseholdJoinTests
{
    private static readonly Guid Sam = new("5a6e0000-0000-4000-8000-00000000005a");
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

    /// <summary>A provider whose proof <c>{"code": "&lt;subject&gt;"}</c> signs in as idp:&lt;subject&gt;.</summary>
    private sealed class SubjectProvider : IGatewaySignInProvider
    {
        public ValueTask<string?> AuthorizeAsync(GatewaySignInAttempt attempt, CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>("https://idp.example/authorize?state=" + attempt.State);

        public ValueTask<GatewaySignInIdentity> VerifyAsync(GatewaySignInAttempt attempt, JsonElement proof, CancellationToken cancellationToken)
        {
            var subject = proof.GetProperty("code").GetString()!;
            return ValueTask.FromResult(new GatewaySignInIdentity("idp", subject, subject + "@example.net"));
        }
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ReadAsync(HttpResponseMessage response)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return (response.StatusCode, document.RootElement.Clone());
    }

    private static HttpRequestMessage Post(GatewayTestHost host, string path, object body) =>
        new(HttpMethod.Post, host.Origin.CanonicalOrigin + path) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static async Task ChangeAsync(GatewayTestHost host, GatewayRequestSigner signer, object change)
    {
        using var response = await host.Client.SendAsync(host.SignedPost("/martlet/v1/signin/settings", GatewayRole.Voice, signer,
            JsonSerializer.SerializeToUtf8Bytes(change)));
        var (status, body) = await ReadAsync(response);
        Assert.True(status == HttpStatusCode.OK, body.GetRawText());
    }

    /// <summary>Signs <paramref name="device"/> in as idp:<paramref name="subject"/> and asks to join; returns the host's answer to
    /// the sign-in.</summary>
    private static async Task<JsonElement> SignInAndJoinAsync(GatewayTestHost host, string subject, string device)
    {
        using var begin = await host.Client.SendAsync(Post(host, "/martlet/v1/signin/begin", new
        {
            protocol_version = Version, provider = "idp", code_challenge = FriendAccessTests.Challenge, redirect_uri = "http://127.0.0.1:53111/"
        }));
        var attempt = (await ReadAsync(begin)).Body.GetProperty("attempt_id").GetString();
        using var complete = await host.Client.SendAsync(Post(host, "/martlet/v1/signin/complete", new
        {
            protocol_version = Version, attempt_id = attempt, device_id = device, display_name = device.ToUpperInvariant(), proof = new { code = subject }
        }));
        var (status, signedIn) = await ReadAsync(complete);
        Assert.True(status == HttpStatusCode.Created, signedIn.GetRawText());
        using var key = NetworkKey.Create(device);
        using var join = await host.Client.SendAsync(host.SignedPost("/martlet/v1/network/join", GatewayRole.Voice, FriendAccessTests.Signer(host, signedIn),
            JsonSerializer.SerializeToUtf8Bytes(new { protocol_version = Version, display_name = device.ToUpperInvariant(), key = key.PublicKey })));
        var (joinStatus, joined) = await ReadAsync(join);
        Assert.True(joinStatus == HttpStatusCode.OK, joined.GetRawText());
        return signedIn;
    }

    [Fact]
    public async Task A_join_by_an_account_login_names_that_account_for_member_desktops()
    {
        await using var host = await GatewayTestHost.StartAsync();
        host.Server.AttachSignInStorage(new MemorySignInStorage());
        host.Server.SignIn.Providers = config => config.Id == "idp" ? new SubjectProvider() : null;
        var home = new GatewayRequestSigner(host.Identity, await host.PairAsync(deviceId: "desktop-home"), host.Clock);
        var now = host.Clock.GetUtcNow();
        using var founder = NetworkKey.Create("desktop-home");
        var roster = NetworkRoster.Found(founder, "HOME", now)
            .AddHost(founder, host.Identity.HostId, "Fixture", host.Origin.CanonicalOrigin, host.Identity.SpkiFingerprint, now);
        host.Server.AttachNetworkStorage(new RosterStorage(roster.Write()));
        await ChangeAsync(host, home, new
        {
            action = "provider", provider_config = new { id = "idp", kind = "oidc", name = "IdP", issuer = "https://idp.example", client_id = "martlet" }
        });
        // Sam's provider login, as the Account page links it on every host; and an identity that is one of the owner's computers.
        await ChangeAsync(host, home, new { action = "allow", provider = "idp", subject = "sam-1", label = "sam@example.net", account_id = Sam });
        await ChangeAsync(host, home, new { action = "allow", provider = "idp", subject = "owner-1", label = "me@example.net" });

        var sams = await SignInAndJoinAsync(host, "sam-1", "sam-new-pc");
        Assert.Equal(Sam, sams.GetProperty("account_id").GetGuid());
        await SignInAndJoinAsync(host, "owner-1", "owner-laptop");

        using var network = await host.Client.SendAsync(host.SignedGet("/martlet/v1/network", GatewayRole.Voice, home));
        var (status, root) = await ReadAsync(network);
        Assert.Equal(HttpStatusCode.OK, status);
        var joins = root.GetProperty("joins").EnumerateArray().ToDictionary(j => j.GetProperty("device_id").GetString()!, j => j.GetProperty("sign_in"));
        Assert.Equal(Sam, joins["sam-new-pc"].GetProperty("account_id").GetGuid());
        Assert.Equal("idp", joins["sam-new-pc"].GetProperty("provider").GetString());
        // A member identity linked to no account of its own proves the owner's, derived from the network.
        Assert.Equal(Martlet.Core.Accounts.OwnerAccount.IdFor(roster.NetworkId), joins["owner-laptop"].GetProperty("account_id").GetGuid());
    }

    private static readonly Guid Alex = new("a1e40000-0000-4000-8000-0000000000a1");

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ProveLinkAsync(GatewayTestHost host, GatewayRequestSigner signer, string subject,
        Guid? link)
    {
        using var begin = await host.Client.SendAsync(Post(host, "/martlet/v1/signin/begin", new
        {
            protocol_version = Version, provider = "idp", code_challenge = FriendAccessTests.Challenge, redirect_uri = "http://127.0.0.1:53111/"
        }));
        var attempt = (await ReadAsync(begin)).Body.GetProperty("attempt_id").GetString();
        using var response = await host.Client.SendAsync(host.SignedPost("/martlet/v1/signin/prove", GatewayRole.Voice, signer,
            JsonSerializer.SerializeToUtf8Bytes(new { protocol_version = Version, attempt_id = attempt, proof = new { code = subject }, link_account_id = link })));
        return await ReadAsync(response);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> TryChangeAsync(GatewayTestHost host, GatewayRequestSigner signer, object change)
    {
        using var response = await host.Client.SendAsync(host.SignedPost("/martlet/v1/signin/settings", GatewayRole.Voice, signer,
            JsonSerializer.SerializeToUtf8Bytes(change)));
        return await ReadAsync(response);
    }

    private static async Task<GatewayTestHost> HouseholdHostAsync(string hostId)
    {
        var host = await GatewayTestHost.StartAsync(hostId: hostId);
        host.Server.AttachSignInStorage(new MemorySignInStorage());
        host.Server.SignIn.Providers = config => config.Id == "idp" ? new SubjectProvider() : null;
        return host;
    }

    [Fact]
    public async Task A_provider_login_links_to_an_account_on_one_host_and_the_others_take_its_attestation()
    {
        await using var a = await HouseholdHostAsync("host-a");
        await using var b = await HouseholdHostAsync("host-b");
        var homeA = new GatewayRequestSigner(a.Identity, await a.PairAsync(deviceId: "desktop-home"), a.Clock);
        var homeB = new GatewayRequestSigner(b.Identity, await b.PairAsync(deviceId: "desktop-home"), b.Clock);
        var now = a.Clock.GetUtcNow();
        using var founder = NetworkKey.Create("desktop-home");
        var roster = NetworkRoster.Found(founder, "HOME", now)
            .AddHost(founder, a.Identity.HostId, "A", a.Origin.CanonicalOrigin, a.Identity.SpkiFingerprint, now)
            .AddHost(founder, b.Identity.HostId, "B", b.Origin.CanonicalOrigin, b.Identity.SpkiFingerprint, now);
        a.Server.AttachNetworkStorage(new RosterStorage(roster.Write()));
        b.Server.AttachNetworkStorage(new RosterStorage(roster.Write()));
        var provider = new { action = "provider", provider_config = new { id = "idp", kind = "oidc", name = "IdP", issuer = "https://idp.example", client_id = "martlet" } };
        await ChangeAsync(a, homeA, provider);
        await ChangeAsync(b, homeB, provider);
        await ChangeAsync(a, homeA, new { action = "allow", provider = "idp", subject = "ana-7", label = "Ana", access = "friend" });

        // On host A a Prove sign-in that asks to link: the host checks the identity, allows it as Sam's login and attests Sam with it.
        var (status, proved) = await ProveLinkAsync(a, homeA, "sam-1", Sam);
        Assert.True(status == HttpStatusCode.OK, proved.GetRawText());
        Assert.Equal(Sam, proved.GetProperty("account_id").GetGuid());
        var attestation = Martlet.Core.Accounts.AccountAttestation.Parse(proved.GetProperty("attestation"));
        Assert.Equal(("oidc", "idp", "sam-1", "desktop-home"), (attestation.Login.Kind, attestation.Login.Provider, attestation.Login.Subject, attestation.DeviceId));
        Assert.Equal(Martlet.Core.Accounts.AccountAttestationCheck.Valid, attestation.Check(roster, a.Clock.GetUtcNow()));
        // Host B takes it with that attestation; then the login proves Sam there too, and a computer joins B as Sam's.
        var linkLogin = new { action = "link-login", attestation = proved.GetProperty("attestation"), label = "sam@example.net" };
        Assert.Equal(HttpStatusCode.OK, (await TryChangeAsync(b, homeB, linkLogin)).Status);
        Assert.Equal(Sam, (await ProveLinkAsync(b, homeB, "sam-1", null)).Body.GetProperty("account_id").GetGuid());
        // The same identity can't become another account's, a friend's can't be anyone's, and a changed attestation fails.
        Assert.Equal("signin.login_taken", (await ProveLinkAsync(a, homeA, "sam-1", Alex)).Body.GetProperty("code").GetString());
        Assert.Equal("signin.login_taken", (await ProveLinkAsync(a, homeA, "ana-7", Sam)).Body.GetProperty("code").GetString());
        var forged = JsonDocument.Parse((attestation with { AccountId = Alex }).Write()).RootElement.Clone();
        Assert.Equal("signin.invalid", (await TryChangeAsync(b, homeB, new { action = "link-login", attestation = forged })).Body.GetProperty("code").GetString());

        // With accounts in the household, a computer bound only to Sam (a member) may link and unlink Sam's logins, nobody else's.
        var windows = Martlet.Core.Accounts.AccountLoginKey.ForWindows("desktop-home", "S-1-5-21-1");
        var directory = Martlet.Core.Accounts.AccountDirectory.Empty
            .Put(founder, Martlet.Core.Accounts.OwnerAccount.Create(roster, "Owner"), now)
            .Put(founder, Martlet.Core.Accounts.Account.Create("Sam", Martlet.Core.Accounts.AccountRoles.Member, Sam)
                .WithLogin(Martlet.Core.Accounts.AccountLogin.For(windows, null, now))
                .WithDevice(Martlet.Core.Accounts.AccountDevice.For("desktop-home", windows, now)), now);
        using (var accounts = await a.Client.SendAsync(a.SignedPost("/martlet/v1/accounts", GatewayRole.Voice, homeA, directory.Write())))
            Assert.Equal(0, (await ReadAsync(accounts)).Body.GetProperty("rejected").GetInt32());
        Assert.Equal("signin.denied", (await ProveLinkAsync(a, homeA, "alex-1", Alex)).Body.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await ProveLinkAsync(a, homeA, "sam-2", Sam)).Status);
        Assert.Equal("signin.denied", (await TryChangeAsync(a, homeA, new { action = "disallow", provider = "idp", subject = "ana-7" })).Body.GetProperty("code").GetString());
        Assert.Equal("signin.denied", (await TryChangeAsync(a, homeA, new { action = "allow", provider = "idp", subject = "x-1", account_id = Sam })).Body
            .GetProperty("code").GetString());
        var (unlinked, after) = await TryChangeAsync(a, homeA, new { action = "disallow", provider = "idp", subject = "sam-2" });
        Assert.Equal(HttpStatusCode.OK, unlinked);
        Assert.DoesNotContain(after.GetProperty("allowed").EnumerateArray(), x => x.GetProperty("subject").GetString() == "sam-2");
    }
}
