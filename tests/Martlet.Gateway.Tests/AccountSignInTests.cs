using System.Net;
using System.Text;
using System.Text.Json;
using Martlet.Core.Access;
using Martlet.Core.Accounts;
using Martlet.Core.Network;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

public sealed class AccountSignInTests
{
    private const string Password = "correct horse battery staple";
    private static readonly Guid Sam = new("5a6e0000-0000-4000-8000-00000000005a");
    private static readonly Guid Alex = new("a1e40000-0000-4000-8000-0000000000a1");
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

    private sealed class FakeProvider(GatewaySignInIdentity identity) : IGatewaySignInProvider
    {
        internal GatewaySignInIdentity Identity { get; set; } = identity;
        public ValueTask<string?> AuthorizeAsync(GatewaySignInAttempt attempt, CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>("https://idp.example/authorize?state=" + attempt.State);
        public ValueTask<GatewaySignInIdentity> VerifyAsync(GatewaySignInAttempt attempt, JsonElement proof, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Identity);
    }

    private static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value);

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ReadAsync(HttpResponseMessage response)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return (response.StatusCode, document.RootElement.Clone());
    }

    private static async Task<string> BeginAsync(GatewayTestHost host, string provider)
    {
        using var begin = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, host.Origin.CanonicalOrigin + "/martlet/v1/signin/begin")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { protocol_version = Version, provider }), Encoding.UTF8, "application/json")
        });
        var (status, body) = await ReadAsync(begin);
        Assert.True(status == HttpStatusCode.OK, body.GetRawText());
        return body.GetProperty("attempt_id").GetString()!;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ProveAsync(GatewayTestHost host, GatewayRequestSigner signer, string provider,
        object proof, int? lifetime = null)
    {
        var attempt = await BeginAsync(host, provider);
        using var response = await host.Client.SendAsync(host.SignedPost("/martlet/v1/signin/prove", GatewayRole.Voice, signer,
            Json(new { protocol_version = Version, attempt_id = attempt, proof, lifetime_seconds = lifetime })));
        return await ReadAsync(response);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> CompleteAsync(GatewayTestHost host, string provider, string device, object proof)
    {
        var attempt = await BeginAsync(host, provider);
        using var response = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, host.Origin.CanonicalOrigin + "/martlet/v1/signin/complete")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { protocol_version = Version, attempt_id = attempt, device_id = device, display_name = "LAPTOP", proof }),
                Encoding.UTF8, "application/json")
        });
        return await ReadAsync(response);
    }

    private static async Task<JsonElement> ChangeAsync(GatewayTestHost host, GatewayRequestSigner signer, object change, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await host.Client.SendAsync(host.SignedPost("/martlet/v1/signin/settings", GatewayRole.Voice, signer, Json(change)));
        var (status, body) = await ReadAsync(response);
        Assert.True(status == expected, body.GetRawText());
        return body;
    }

    [Theory]
    [InlineData("net-example", "8a58da66-fddc-5c5b-9282-fb119c84915f")]
    [InlineData("0f3a9c", "b9681809-5be4-5629-a701-10c7c878e749")]
    public void The_owners_account_follows_from_the_network_id(string networkId, string expected) =>
        Assert.Equal(new Guid(expected), GatewaySignInService.OwnerAccountIdFor(networkId));

    [Fact]
    public async Task Household_accounts_prove_themselves_and_get_attestations_the_roster_verifies()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var storage = new MemorySignInStorage();
        host.Server.AttachSignInStorage(storage);
        var desktop = new GatewayRequestSigner(host.Identity, await host.PairAsync(deviceId: "desktop-home"), host.Clock);
        var ownerSecret = Totp.NewSecret();
        var samSecret = Totp.NewSecret();
        var now = host.Clock.GetUtcNow();
        await ChangeAsync(host, desktop, new { action = "owner", user = "owner", password = Password, totp_secret = ownerSecret, code = Totp.Code(ownerSecret, now) });
        var samCodes = (await ChangeAsync(host, desktop, new
        {
            action = "account", account_id = Sam, user = "Sam", password = Password, totp_secret = samSecret, code = Totp.Code(samSecret, now)
        })).GetProperty("recovery_codes").EnumerateArray().Select(c => c.GetString()!).ToArray();
        Assert.Equal(10, samCodes.Length);
        var alex = await ChangeAsync(host, desktop, new { action = "account", account_id = Alex, user = "alex", password = Password });
        Assert.False(alex.TryGetProperty("recovery_codes", out _));
        // User names are one per host, whatever their case; the owner's account ID belongs to no other login.
        Assert.Equal("signin.user_taken", (await ChangeAsync(host, desktop, new { action = "account", account_id = Guid.NewGuid(), user = "OWNER", password = Password },
            HttpStatusCode.Conflict)).GetProperty("code").GetString());
        await ChangeAsync(host, desktop, new { action = "account", account_id = Guid.NewGuid(), user = "sam", password = Password }, HttpStatusCode.Conflict);
        await ChangeAsync(host, desktop, new { action = "account", account_id = Guid.NewGuid(), user = "newcomer" }, HttpStatusCode.BadRequest);
        await ChangeAsync(host, desktop, new { action = "account", account_id = Alex, user = "alex", password = "short" }, HttpStatusCode.BadRequest);

        // In no network yet, nobody can be vouched for: refused before the proof is used.
        Assert.Equal("signin.no_network", (await ProveAsync(host, desktop, "martlet", new { user = "alex", password = Password })).Body.GetProperty("code").GetString());

        using var founder = NetworkKey.Create("desktop-home");
        var roster = NetworkRoster.Found(founder, "HOME", now)
            .AddHost(founder, host.Identity.HostId, "Fixture", host.Origin.CanonicalOrigin, host.Identity.SpkiFingerprint, now);
        host.Server.AttachNetworkStorage(new RosterStorage(roster.Write()));
        var ownerAccount = GatewaySignInService.OwnerAccountIdFor(roster.NetworkId);

        AccountAttestation Attested(JsonElement body, Guid account, string device, string login)
        {
            Assert.Equal(account, body.GetProperty("account_id").GetGuid());
            var attestation = AccountAttestation.Parse(body.GetProperty("attestation"));
            Assert.Equal(AccountAttestationCheck.Valid, attestation.Check(roster, host.Clock.GetUtcNow()));
            Assert.Equal((account, device, login, host.Identity.HostId, AccountAttestation.RsaPss),
                (attestation.AccountId, attestation.DeviceId, attestation.Login.ToString(), attestation.HostId, attestation.Algorithm));
            return attestation;
        }

        // A password login without an authenticator proves its account; it may ask for a longer attestation.
        var (status, body) = await ProveAsync(host, desktop, "martlet", new { user = "ALEX", password = Password }, lifetime: 3600);
        Assert.True(status == HttpStatusCode.OK, body.GetRawText());
        var alexProof = Attested(body, Alex, "desktop-home", "martlet:martlet:alex");
        Assert.Equal(TimeSpan.FromHours(1), alexProof.ExpiresAt - alexProof.IssuedAt);
        Assert.Equal("martlet", body.GetProperty("signed_in").GetProperty("provider").GetString());

        // The owner login proves the owner's account, which comes from the network until a desktop names one.
        host.Clock.Advance(TimeSpan.FromSeconds(30));
        (status, body) = await ProveAsync(host, desktop, "owner", new { user = "owner", password = Password, code = Totp.Code(ownerSecret, host.Clock.GetUtcNow()) });
        Assert.True(status == HttpStatusCode.OK, body.GetRawText());
        Attested(body, ownerAccount, "desktop-home", "martlet:martlet:owner");
        Assert.Equal("owner", body.GetProperty("signed_in").GetProperty("provider").GetString());

        // Sam's login has an authenticator, so its code is needed; a wrong one, a wrong lifetime or an unsigned call get nothing.
        Assert.Equal("signin.invalid", (await ProveAsync(host, desktop, "martlet", new { user = "sam", password = Password, code = "000000" })).Body
            .GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await ProveAsync(host, desktop, "martlet", new { user = "alex", password = Password }, lifetime: 10)).Status);
        using (var unsigned = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, host.Origin.CanonicalOrigin + "/martlet/v1/signin/prove")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { protocol_version = Version, attempt_id = await BeginAsync(host, "martlet"), proof = new { } }),
                Encoding.UTF8, "application/json")
        }))
            Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        (status, body) = await ProveAsync(host, desktop, "martlet", new { user = "sam", password = Password, code = Totp.Code(samSecret, host.Clock.GetUtcNow()) });
        Assert.True(status == HttpStatusCode.OK, body.GetRawText());
        Attested(body, Sam, "desktop-home", "martlet:martlet:sam");

        // Adding a computer by signing in needs the authenticator; with it, the laptop gets its pairing and Sam's attestation.
        Assert.Equal("signin.needs_authenticator", (await CompleteAsync(host, "martlet", "alex-laptop", new { user = "alex", password = Password })).Body
            .GetProperty("code").GetString());
        (status, body) = await CompleteAsync(host, "martlet", "sam-laptop", new { user = "sam", password = Password, code = samCodes[0] });
        Assert.True(status == HttpStatusCode.Created, body.GetRawText());
        Attested(body, Sam, "sam-laptop", "martlet:martlet:sam");
        var laptop = new GatewayRequestSigner(host.Identity, new()
        {
            CredentialId = body.GetProperty("credential_id").GetString()!, DeviceId = "sam-laptop", Roles = [GatewayRole.Voice],
            Secret = new(body.GetProperty("credential_secret").GetString()!), Lifetime = new PairedDeviceLifetime()
        }, host.Clock);

        using (var list = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, host.Origin.CanonicalOrigin + "/martlet/v1/signin")))
        {
            var root = (await ReadAsync(list)).Body;
            Assert.True(root.GetProperty("martlet_sign_in").GetBoolean());
            Assert.DoesNotContain(root.GetProperty("providers").EnumerateArray(), p => p.GetProperty("id").GetString() == "martlet");
        }
        using (var read = await host.Client.SendAsync(host.SignedGet("/martlet/v1/signin/settings", GatewayRole.Voice, desktop)))
        {
            var settings = (await ReadAsync(read)).Body;
            Assert.Equal(ownerAccount, settings.GetProperty("owner_account_id").GetGuid());
            var accounts = settings.GetProperty("accounts").EnumerateArray().ToArray();
            Assert.Contains(accounts, a => a.GetProperty("user").GetString() == "Sam" && a.GetProperty("has_authenticator").GetBoolean() &&
                a.GetProperty("recovery_codes_left").GetInt32() == 9);
            Assert.Contains(accounts, a => a.GetProperty("user").GetString() == "alex" && !a.GetProperty("has_authenticator").GetBoolean());
            Assert.DoesNotContain(Password, settings.GetRawText());
            Assert.DoesNotContain(samSecret, settings.GetRawText());
        }
        Assert.DoesNotContain(Password, Encoding.UTF8.GetString(storage.Bytes!));

        // Removing Sam's login revokes the computer it added; removing an authenticator keeps the login.
        await ChangeAsync(host, desktop, new { action = "remove-account-authenticator", account_id = Sam });
        using (var still = await host.Client.SendAsync(host.SignedGet("/martlet/v1/version", GatewayRole.Voice, laptop)))
            Assert.Equal(HttpStatusCode.OK, still.StatusCode);
        await ChangeAsync(host, desktop, new { action = "remove-account", account_id = Sam });
        using (var revoked = await host.Client.SendAsync(host.SignedGet("/martlet/v1/version", GatewayRole.Voice, laptop)))
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal("signin.invalid", (await ProveAsync(host, desktop, "martlet", new { user = "sam", password = Password })).Body.GetProperty("code").GetString());
        Assert.Contains(host.Server.Guard.Recent(), e => e is { RouteClass: "signin", Outcome: "success", Code: "signin.proved", Subject: "martlet:alex" });
    }

    [Fact]
    public async Task Provider_identities_sign_in_as_their_account_and_friends_as_none()
    {
        var clock = new ManualGatewayClock(new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero));
        var identity = new GatewayHostIdentity { HostId = "home-host", SpkiFingerprint = "sha256:" + new string('a', 64) };
        var service = new GatewaySignInService(new GatewayCredentialStore(identity, clock), clock, new SystemGatewayCrypto(), (_, _) => { });
        service.Attach(new MemorySignInStorage());
        var provider = new FakeProvider(new("idp", "user-123", "sam@example.net"));
        service.Providers = _ => provider;
        void Change(GatewaySignInChange change) => service.Change(change, "desktop-home", CancellationToken.None);
        Change(new() { Action = "provider", ProviderConfig = new() { Id = "idp", Kind = "oidc", Name = "IdP", Issuer = "https://idp.example", ClientId = "c" } });
        var proof = JsonDocument.Parse("{}").RootElement;
        async Task<(Guid Account, string Login)> Prove()
        {
            var (attempt, _) = await service.BeginAsync("idp", "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", "http://127.0.0.1:53111/", CancellationToken.None);
            var (_, account, login) = await service.ProveAsync(attempt.Id, "desktop-home", proof, CancellationToken.None);
            return (account, login.ToString());
        }
        async Task<string> Refused() =>
            (await Assert.ThrowsAsync<GatewayProtocolException>(async () => await Prove())).Failure.Code;

        // Not allowed yet: refused and listed for the owner. A member identity before any owner account: no account to prove.
        Assert.Equal("signin.not_allowed", await Refused());
        Assert.Single(service.Refused());
        Change(new() { Action = "allow", Provider = "idp", Subject = "user-123" });
        Assert.Equal("signin.no_account", await Refused());
        // Member identities without an account of their own sign in as the owner's account (migration).
        var owner = Guid.NewGuid();
        Change(new() { Action = "owner-account", AccountId = owner });
        Assert.Equal((owner, "oidc:idp:user-123"), await Prove());
        // Linked to Sam's account it proves Sam's; allowing it again keeps the link, unlinking goes back to the owner's.
        Change(new() { Action = "link", Provider = "idp", Subject = "user-123", AccountId = Sam });
        Assert.Equal(Sam, (await Prove()).Account);
        Change(new() { Action = "allow", Provider = "idp", Subject = "user-123", Label = "Sam" });
        Assert.Equal(Sam, (await Prove()).Account);
        Change(new() { Action = "link", Provider = "idp", Subject = "user-123" });
        Assert.Equal(owner, (await Prove()).Account);
        // A friend is outside the household: never an account, and never linked to one.
        Change(new() { Action = "allow", Provider = "idp", Subject = "user-123", Access = "friend" });
        Assert.Equal("signin.no_account", await Refused());
        Assert.Equal("request.invalid", Assert.Throws<GatewayProtocolException>(() =>
            Change(new() { Action = "link", Provider = "idp", Subject = "user-123", AccountId = Sam })).Failure.Code);
        Assert.Equal("request.invalid", Assert.Throws<GatewayProtocolException>(() =>
            Change(new() { Action = "allow", Provider = "idp", Subject = "user-456", Access = "friend", AccountId = Sam })).Failure.Code);
        // Another login can't take the owner's account ID, and "martlet" is no provider ID.
        Assert.Equal("request.invalid", Assert.Throws<GatewayProtocolException>(() =>
            Change(new() { Action = "account", AccountId = owner, User = "sam", Password = Password })).Failure.Code);
        Assert.Equal("request.invalid", Assert.Throws<GatewayProtocolException>(() =>
            Change(new() { Action = "allow", Provider = "martlet", Subject = "sam" })).Failure.Code);
    }

    [Fact]
    public void Sign_in_settings_written_before_accounts_still_load_unchanged()
    {
        var legacy = """
            {"schema_version":1,"owner":{"user":"owner","password":{"salt":"c2FsdHNhbHRzYWx0c2FsdA","iterations":1000,"hash":"aGFzaGhhc2hoYXNoaGFzaGhhc2hoYXNoaGFzaGhhc2g"},
             "totp_secret":"JBSWY3DPEHPK3PXP","last_totp_step":5,"recovery_codes":["x"]},
             "providers":[],"allowed":[{"provider":"idp","subject":"user-123","access":null}],"enrolled":[],"removed":[]}
            """u8.ToArray();
        var document = GatewaySignInDocument.Parse(legacy);
        Assert.Empty(document.Accounts);
        Assert.Null(document.OwnerAccountId);
        Assert.Null(document.Allowed[0].AccountId);
        Assert.True(document.Allows(GatewaySignInService.OwnerProvider, "owner"));
        Assert.Null(document.AccountOf("idp", "user-123"));
        var owner = Guid.NewGuid();
        Assert.Equal(owner, document.AccountOf("idp", "user-123", owner));
        Assert.Equal(owner, document.AccountOf(GatewaySignInService.OwnerProvider, "owner", owner));
        Assert.Null(GatewaySignInSettings.BlockedReason(document));
        Assert.Equal(owner, GatewaySignInDocument.Parse(document.Write()).AccountOf("idp", "user-123", owner));
    }
}
