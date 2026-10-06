using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Access;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

public sealed class SignInTests
{
    private const string Password = "correct horse battery staple";

    private sealed class MemorySignInStorage : IGatewaySignInStorage
    {
        internal byte[]? Bytes;
        public byte[]? Load() => Bytes;
        public void Save(byte[] bytes) => Bytes = bytes;
    }

    private static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value);

    private static HttpRequestMessage Post(GatewayTestHost host, string path, object body) =>
        new(HttpMethod.Post, host.Origin.CanonicalOrigin + path) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement.Clone();
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SignInAsync(GatewayTestHost host, string user, string password, string code,
        string device = "laptop-away")
    {
        using var begin = await host.Client.SendAsync(Post(host, "/martlet/v1/signin/begin", new { protocol_version = new { major = 2, minor = 0 }, provider = "owner" }));
        if (begin.StatusCode != HttpStatusCode.OK) return (begin.StatusCode, await ReadAsync(begin));
        var attempt = (await ReadAsync(begin)).GetProperty("attempt_id").GetString();
        using var complete = await host.Client.SendAsync(Post(host, "/martlet/v1/signin/complete", new
        {
            protocol_version = new { major = 2, minor = 0 }, attempt_id = attempt, device_id = device, display_name = "LAPTOP",
            proof = new { user, password, code }
        }));
        return (complete.StatusCode, await ReadAsync(complete));
    }

    private static async Task<(GatewayRequestSigner Owner, string Secret, IReadOnlyList<string> Recovery)> SetUpOwnerAsync(GatewayTestHost host)
    {
        var credential = await host.PairAsync(deviceId: "home-pc");
        var signer = new GatewayRequestSigner(host.Identity, credential, host.Clock);
        var secret = Totp.NewSecret();
        var body = Json(new
        {
            action = "owner", user = "owner", password = Password, totp_secret = secret, code = Totp.Code(secret, host.Clock.GetUtcNow())
        });
        using var response = await host.Client.SendAsync(host.SignedPost("/martlet/v1/signin/settings", GatewayRole.Voice, signer, body));
        var root = await ReadAsync(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("owner", root.GetProperty("owner").GetProperty("user").GetString());
        var codes = root.GetProperty("recovery_codes").EnumerateArray().Select(c => c.GetString()!).ToArray();
        Assert.Equal(10, codes.Length);
        Assert.DoesNotContain(secret, root.GetRawText());
        Assert.DoesNotContain(Password, root.GetRawText());
        return (signer, secret, codes);
    }

    [Fact]
    public async Task Owner_account_with_authenticator_enrolls_a_computer_from_outside_and_each_code_works_once()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var storage = new MemorySignInStorage();
        host.Server.AttachSignInStorage(storage);
        using (var none = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, host.Origin.CanonicalOrigin + "/martlet/v1/signin")))
            Assert.Empty((await ReadAsync(none)).GetProperty("providers").EnumerateArray());
        var (owner, secret, recovery) = await SetUpOwnerAsync(host);
        var saved = Encoding.UTF8.GetString(storage.Bytes!);
        Assert.DoesNotContain(Password, saved);
        Assert.DoesNotContain(recovery[0], saved);

        using (var list = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, host.Origin.CanonicalOrigin + "/martlet/v1/signin")))
            Assert.Equal("owner", (await ReadAsync(list)).GetProperty("providers")[0].GetProperty("id").GetString());

        // The setup code is already used, so it can't sign in; a wrong password never does.
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(host, "owner", Password, Totp.Code(secret, host.Clock.GetUtcNow()))).Status);
        host.Clock.Advance(TimeSpan.FromSeconds(30));
        var code = Totp.Code(secret, host.Clock.GetUtcNow());
        var wrong = await SignInAsync(host, "owner", "not the password at all", code);
        Assert.Equal("signin.invalid", wrong.Body.GetProperty("code").GetString());

        var (status, body) = await SignInAsync(host, "OWNER", Password, code);
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("laptop-away", body.GetProperty("device_id").GetString());
        Assert.Equal("owner", body.GetProperty("signed_in").GetProperty("provider").GetString());
        Assert.Equal("voice", body.GetProperty("roles")[0].GetString());
        var laptop = new GatewayRequestSigner(host.Identity, new()
        {
            CredentialId = body.GetProperty("credential_id").GetString()!, DeviceId = "laptop-away", Roles = [GatewayRole.Voice],
            Secret = new(body.GetProperty("credential_secret").GetString()!), Lifetime = new PairedDeviceLifetime()
        }, host.Clock);
        using (var version = await host.Client.SendAsync(host.SignedGet("/martlet/v1/version", GatewayRole.Voice, laptop)))
            Assert.Equal(HttpStatusCode.OK, version.StatusCode);

        // The same authenticator code doesn't work twice; a recovery code works once.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(host, "owner", Password, code, "laptop-two")).Status);
        Assert.Equal(HttpStatusCode.Created, (await SignInAsync(host, "owner", Password, recovery[3].ToLowerInvariant(), "laptop-two")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(host, "owner", Password, recovery[3], "laptop-three")).Status);

        using (var read = await host.Client.SendAsync(host.SignedGet("/martlet/v1/signin/settings", GatewayRole.Voice, owner)))
        {
            var settings = await ReadAsync(read);
            Assert.Equal(9, settings.GetProperty("owner").GetProperty("recovery_codes_left").GetInt32());
            Assert.Equal(["laptop-away", "laptop-two"], settings.GetProperty("enrolled").EnumerateArray().Select(e => e.GetProperty("device_id").GetString()));
            Assert.False(settings.TryGetProperty("recovery_codes", out _));
        }

        // Removing the owner account takes access away from the computers it signed in.
        using (var remove = await host.Client.SendAsync(host.SignedPost("/martlet/v1/signin/settings", GatewayRole.Voice, owner, Json(new { action = "remove-owner" }))))
            Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
        using (var revoked = await host.Client.SendAsync(host.SignedGet("/martlet/v1/version", GatewayRole.Voice, laptop)))
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Contains(host.Server.Guard.Recent(), e => e is { RouteClass: "signin", Outcome: "success", Subject: "owner:owner" });
        Assert.Contains(host.Server.Guard.Recent(), e => e is { RouteClass: "signin", Outcome: "failure", Code: "signin.invalid" });
    }

    [Fact]
    public async Task Repeated_wrong_sign_ins_lock_the_account_out()
    {
        await using var host = await GatewayTestHost.StartAsync();
        host.Server.AttachSignInStorage(new MemorySignInStorage());
        var (_, secret, _) = await SetUpOwnerAsync(host);
        for (var i = 0; i < GatewayRequestGuard.FreeFailures; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(host, "owner", "wrong password " + i, "000000")).Status);
        var locked = await SignInAsync(host, "owner", Password, Totp.Code(secret, host.Clock.GetUtcNow()));
        Assert.Equal((HttpStatusCode)429, locked.Status);
        Assert.Equal("auth.throttled", locked.Body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Owner_setup_needs_a_matching_authenticator_code_and_a_long_password()
    {
        await using var host = await GatewayTestHost.StartAsync();
        host.Server.AttachSignInStorage(new MemorySignInStorage());
        var credential = await host.PairAsync(deviceId: "home-pc");
        var signer = new GatewayRequestSigner(host.Identity, credential, host.Clock);
        var secret = Totp.NewSecret();
        async Task<string> Fail(object change)
        {
            using var response = await host.Client.SendAsync(host.SignedPost("/martlet/v1/signin/settings", GatewayRole.Voice, signer, Json(change)));
            return await GatewayTestHost.FailureCode(response);
        }
        Assert.Equal("signin.invalid", await Fail(new { action = "owner", user = "owner", password = Password, totp_secret = secret, code = "123456" }));
        Assert.Equal("signin.weak_password", await Fail(new
        {
            action = "owner", user = "owner", password = "short", totp_secret = secret, code = Totp.Code(secret, host.Clock.GetUtcNow())
        }));
        using var unsigned = await host.Client.SendAsync(Post(host, "/martlet/v1/signin/settings", new { action = "remove-owner" }));
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
    }

    private sealed class FakeProvider(GatewaySignInIdentity identity) : IGatewaySignInProvider
    {
        public ValueTask<string?> AuthorizeAsync(GatewaySignInAttempt attempt, CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>("https://idp.example/authorize?state=" + attempt.State);

        public ValueTask<GatewaySignInIdentity> VerifyAsync(GatewaySignInAttempt attempt, JsonElement proof, CancellationToken cancellationToken) =>
            proof.GetProperty("code").GetString() == "good" ? ValueTask.FromResult(identity) : throw new GatewayProtocolException("signin.invalid");
    }

    [Fact]
    public async Task Provider_identities_need_the_allow_list_and_lose_access_when_removed()
    {
        var clock = new ManualGatewayClock(new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.Zero));
        var identity = new GatewayHostIdentity { HostId = "home-host", SpkiFingerprint = "sha256:" + new string('a', 64) };
        var credentials = new GatewayCredentialStore(identity, clock);
        var log = new List<string>();
        var service = new GatewaySignInService(credentials, clock, new SystemGatewayCrypto(), (_, message) => log.Add(message));
        var storage = new MemorySignInStorage();
        service.Attach(storage);
        service.Change(new() { Action = "provider", ProviderConfig = new() { Id = "idp", Kind = "oidc", Name = "Authentik", Issuer = "https://idp.example", ClientId = "martlet" } },
            "home-pc", CancellationToken.None);
        service.Providers = config => config.Id == "idp" ? new FakeProvider(new("idp", "user-123", "me@example.net")) : null;
        Assert.Contains(service.Available(), p => p.Id == "idp");
        var proof = JsonDocument.Parse("""{"code":"good"}""").RootElement;
        const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        // A bad redirect (not loopback) is refused before anything starts.
        await Assert.ThrowsAsync<GatewayProtocolException>(async () => await service.BeginAsync("idp", Challenge, "https://evil.example/", CancellationToken.None));
        var (attempt, url) = await service.BeginAsync("idp", Challenge, "http://127.0.0.1:53111/", CancellationToken.None);
        Assert.StartsWith("https://idp.example/authorize", url);
        var refused = await Assert.ThrowsAsync<GatewayProtocolException>(async () =>
            await service.CompleteAsync(attempt.Id, "laptop", "LAPTOP", proof, CancellationToken.None));
        Assert.Equal("signin.not_allowed", refused.Failure.Code);
        // Each attempt works once.
        Assert.Equal("signin.expired", (await Assert.ThrowsAsync<GatewayProtocolException>(async () =>
            await service.CompleteAsync(attempt.Id, "laptop", "LAPTOP", proof, CancellationToken.None))).Failure.Code);

        service.Change(new() { Action = "allow", Provider = "idp", Subject = "user-123", Label = "me@example.net" }, "home-pc", CancellationToken.None);
        (attempt, _) = await service.BeginAsync("idp", Challenge, "http://127.0.0.1:53111/", CancellationToken.None);
        var (credential, who) = await service.CompleteAsync(attempt.Id, "laptop", "LAPTOP", proof, CancellationToken.None);
        Assert.Equal("user-123", who.Subject);
        Assert.Equal("idp", service.Attestation("laptop")?.Identity.Provider);
        Assert.Contains(credentials.PairedDevices(), d => d.DeviceId == "laptop");

        // Expired attempts don't complete.
        (attempt, _) = await service.BeginAsync("idp", Challenge, "http://127.0.0.1:53111/", CancellationToken.None);
        clock.Advance(GatewaySignInService.AttemptLifetime);
        Assert.Equal("signin.expired", (await Assert.ThrowsAsync<GatewayProtocolException>(async () =>
            await service.CompleteAsync(attempt.Id, "laptop-2", "LAPTOP", proof, CancellationToken.None))).Failure.Code);

        service.Change(new() { Action = "disallow", Provider = "idp", Subject = "user-123" }, "home-pc", CancellationToken.None);
        Assert.Null(service.Attestation("laptop"));
        Assert.DoesNotContain(credentials.PairedDevices(), d => d.DeviceId == credential.DeviceId);

        // Removing it with martlet-host (signin.json edited behind the service) revokes on the next read too.
        service.Change(new() { Action = "allow", Provider = "idp", Subject = "user-123" }, "home-pc", CancellationToken.None);
        (attempt, _) = await service.BeginAsync("idp", Challenge, "http://127.0.0.1:53111/", CancellationToken.None);
        await service.CompleteAsync(attempt.Id, "laptop", "LAPTOP", proof, CancellationToken.None);
        var edited = GatewaySignInDocument.Parse(storage.Bytes!);
        GatewaySignInSettings.Apply(edited, new() { Action = "disallow", Provider = "idp", Subject = "user-123" }, clock.GetUtcNow());
        storage.Bytes = edited.Write();
        Assert.DoesNotContain(service.Available(), p => p.Id == "owner");
        Assert.DoesNotContain(credentials.PairedDevices(), d => d.DeviceId == "laptop");
        Assert.Contains(log, line => line.StartsWith("Revoked laptop"));
    }

    [Fact]
    public async Task Removing_an_identity_records_its_computers_with_their_join_keys_for_member_desktops()
    {
        var clock = new ManualGatewayClock(new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.Zero));
        var identity = new GatewayHostIdentity { HostId = "home-host", SpkiFingerprint = "sha256:" + new string('a', 64) };
        var credentials = new GatewayCredentialStore(identity, clock);
        var service = new GatewaySignInService(credentials, clock, new SystemGatewayCrypto(), (_, _) => { });
        var storage = new MemorySignInStorage();
        service.Attach(storage);
        service.Change(new() { Action = "provider", ProviderConfig = new() { Id = "idp", Kind = "oidc", Name = "IdP", Issuer = "https://idp.example", ClientId = "c" } },
            "home-pc", CancellationToken.None);
        service.Providers = config => new FakeProvider(new("idp", "user-123", "me@example.net"));
        service.Change(new() { Action = "allow", Provider = "idp", Subject = "user-123" }, "home-pc", CancellationToken.None);
        var proof = JsonDocument.Parse("""{"code":"good"}""").RootElement;
        var (attempt, _) = await service.BeginAsync("idp", "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", "http://127.0.0.1:53111/", CancellationToken.None);
        await service.CompleteAsync(attempt.Id, "laptop", "LAPTOP", proof, CancellationToken.None);
        service.RememberJoinKey("laptop", "laptop-network-key");
        service.RememberJoinKey("not-enrolled", "other-key");
        Assert.Equal("laptop-network-key", Assert.Single(service.Snapshot().Enrolled).Key);

        service.Change(new() { Action = "disallow", Provider = "idp", Subject = "user-123" }, "home-pc", CancellationToken.None);
        var removal = Assert.Single(service.Snapshot().Removed);
        Assert.Equal(("laptop", "laptop-network-key", "idp", "user-123"), (removal.DeviceId, removal.Key, removal.Provider, removal.Subject));
        Assert.Contains("\"removed\"", Encoding.UTF8.GetString(storage.Bytes!));
        Assert.DoesNotContain(credentials.PairedDevices(), d => d.DeviceId == "laptop");
        // Without a roster (the host in no network) there is nobody to remove it from, and nothing is forgotten.
        Assert.Empty(service.Removals(null));
        Assert.Single(service.Snapshot().Removed);
    }

    [Fact]
    public void Sign_in_is_usable_with_an_owner_account_or_a_provider_with_an_allowed_identity()
    {
        Assert.Equal("signin.not_set_up", GatewaySignInSettings.BlockedReason(null));
        var document = new GatewaySignInDocument();
        Assert.Equal("signin.not_set_up", GatewaySignInSettings.BlockedReason(document));
        document.Providers.Add(new() { Id = "idp", Kind = "oidc", Name = "IdP", Issuer = "https://idp.example", ClientId = "c" });
        Assert.Equal("signin.no_allowed_identity", GatewaySignInSettings.BlockedReason(document));
        document.Allowed.Add(new() { Provider = "other", Subject = "x" });
        Assert.Equal("signin.no_allowed_identity", GatewaySignInSettings.BlockedReason(document));
        document.Allowed.Add(new() { Provider = "idp", Subject = "user-123" });
        Assert.Null(GatewaySignInSettings.BlockedReason(document));
        var owner = new GatewaySignInDocument
        {
            Owner = new() { User = "owner", Password = GatewayAccounts.HashPassword("a long owner passphrase", 1000), TotpSecret = "JBSWY3DPEHPK3PXP" }
        };
        Assert.Null(GatewaySignInSettings.BlockedReason(owner));
        var detached = new GatewaySignInService(new GatewayCredentialStore(new GatewayHostIdentity
        {
            HostId = "home-host", SpkiFingerprint = "sha256:" + new string('a', 64)
        }), TimeProvider.System, new SystemGatewayCrypto(), (_, _) => { });
        Assert.Equal("signin.not_set_up", detached.BlockedReason());
    }
}
