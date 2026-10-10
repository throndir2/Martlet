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
}
