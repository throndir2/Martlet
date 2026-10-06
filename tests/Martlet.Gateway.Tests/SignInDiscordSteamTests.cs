using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

/// <summary>Discord (OAuth2 code + PKCE, identity from /users/@me) and Steam (OpenID 2.0, check_authentication) sign-in
/// against in-process fakes of their endpoints.</summary>
public sealed class SignInDiscordSteamTests
{
    private const string SteamId = "76561198000000042";

    private sealed class FakeEndpoints : HttpMessageHandler
    {
        internal string? TokenBody;
        internal string? TokenAuthorization;
        internal string? CheckBody;
        internal bool SteamValid = true;
        internal string UserJson = """{"id":"80351110224678912","username":"nelly","global_name":"Nelly"}""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.GetLeftPart(UriPartial.Path);
            if (url == GatewayDiscordProvider.TokenEndpoint)
            {
                TokenBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                TokenAuthorization = request.Headers.Authorization?.ToString();
                return TokenBody.Contains("code=good")
                    ? Json("""{"access_token":"discord-access","token_type":"Bearer","scope":"identify"}""")
                    : new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant"}""") };
            }
            if (url == GatewayDiscordProvider.UserEndpoint)
                return request.Headers.Authorization?.ToString() == "Bearer discord-access" ? Json(UserJson) : new HttpResponseMessage(HttpStatusCode.Unauthorized);
            if (url == GatewaySteamProvider.Endpoint && request.Method == HttpMethod.Post)
            {
                CheckBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"ns:http://specs.openid.net/auth/2.0\nis_valid:{(SteamValid ? "true" : "false")}\n")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class Memory : IGatewaySignInStorage
    {
        internal byte[]? Bytes;
        public byte[]? Load() => Bytes;
        public void Save(byte[] bytes) => Bytes = bytes;
    }

    private static string B64(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);

    private static (GatewaySignInService Service, FakeEndpoints Fake) Start()
    {
        var clock = TimeProvider.System;
        var identity = new GatewayHostIdentity { HostId = "home-host", SpkiFingerprint = "sha256:" + new string('a', 64) };
        var fake = new FakeEndpoints();
        var service = new GatewaySignInService(new GatewayCredentialStore(identity, clock), clock, new SystemGatewayCrypto(), (_, _) => { })
        {
            Providers = new GatewaySignInProviders(clock, fake).Create
        };
        service.Attach(new Memory());
        service.Change(new()
        {
            Action = "provider",
            ProviderConfig = new() { Id = "discord", Kind = "discord", Name = "Discord", ClientId = "1234", ClientSecret = "discord-secret", RedirectPort = 53682 }
        }, "home-pc", CancellationToken.None);
        service.Change(new() { Action = "provider", ProviderConfig = new() { Id = "steam", Kind = "steam", Name = "Steam" } }, "home-pc", CancellationToken.None);
        return (service, fake);
    }

    private static async Task<(GatewaySignInAttempt Attempt, string Verifier, string Url)> BeginAsync(GatewaySignInService service, string provider,
        string redirect = "http://127.0.0.1:53682/")
    {
        var verifier = B64(RandomNumberGenerator.GetBytes(32));
        var (attempt, url) = await service.BeginAsync(provider, B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), redirect, CancellationToken.None);
        return (attempt, verifier, url!);
    }

    private static JsonElement Proof(object query, string verifier) => JsonSerializer.SerializeToElement(new { query, code_verifier = verifier });

    [Fact]
    public async Task Discord_signs_in_with_pkce_on_its_registered_port_and_the_host_reads_the_user()
    {
        var (service, fake) = Start();
        // Discord takes only the registered redirect, so another port is refused before anything starts.
        await Assert.ThrowsAsync<GatewayProtocolException>(async () => await BeginAsync(service, "discord", "http://127.0.0.1:50000/"));
        Assert.Contains(service.Available(), p => p is { Id: "discord", RedirectPort: 53682 });
        var (attempt, verifier, url) = await BeginAsync(service, "discord");
        Assert.StartsWith("https://discord.com/oauth2/authorize?", url);
        Assert.Contains("scope=identify", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString("http://127.0.0.1:53682/"), url);
        Assert.DoesNotContain("discord-secret", url);

        var refused = await Assert.ThrowsAsync<GatewayProtocolException>(async () =>
            await service.CompleteAsync(attempt.Id, "laptop", "LAPTOP", Proof(new { code = "good", state = attempt.State }, verifier), CancellationToken.None));
        Assert.Equal("signin.not_allowed", refused.Failure.Code);
        Assert.StartsWith("Basic ", fake.TokenAuthorization);
        Assert.Contains("code_verifier=" + verifier, fake.TokenBody);
        var seen = Assert.Single(service.Refused());
        Assert.Equal(("80351110224678912", "Nelly (@nelly)"), (seen.Identity.Subject, seen.Identity.Label));

        service.Change(new() { Action = "allow", Provider = "discord", Subject = "80351110224678912" }, "home-pc", CancellationToken.None);
        (attempt, verifier, _) = await BeginAsync(service, "discord");
        var (_, who) = await service.CompleteAsync(attempt.Id, "laptop", "LAPTOP", Proof(new { code = "good", state = attempt.State }, verifier),
            CancellationToken.None);
        Assert.Equal("discord", who.Provider);

        (attempt, verifier, _) = await BeginAsync(service, "discord");
        Assert.Equal("signin.invalid", (await Assert.ThrowsAsync<GatewayProtocolException>(async () =>
            await service.CompleteAsync(attempt.Id, "laptop", "LAPTOP", Proof(new { code = "stolen", state = attempt.State }, verifier),
                CancellationToken.None))).Failure.Code);
        (attempt, verifier, _) = await BeginAsync(service, "discord");
        Assert.Equal("signin.invalid", (await Assert.ThrowsAsync<GatewayProtocolException>(async () =>
            await service.CompleteAsync(attempt.Id, "laptop", "LAPTOP", Proof(new { error = "access_denied", state = attempt.State }, verifier),
                CancellationToken.None))).Failure.Code);
    }

    private static Dictionary<string, string> SteamAssertion(GatewaySignInAttempt attempt, string? claimed = null, string? returnTo = null) => new()
    {
        ["state"] = attempt.State,
        ["openid.ns"] = "http://specs.openid.net/auth/2.0", ["openid.mode"] = "id_res",
        ["openid.op_endpoint"] = GatewaySteamProvider.Endpoint,
        ["openid.claimed_id"] = claimed ?? "https://steamcommunity.com/openid/id/" + SteamId,
        ["openid.identity"] = claimed ?? "https://steamcommunity.com/openid/id/" + SteamId,
        ["openid.return_to"] = returnTo ?? attempt.RedirectUri + "?state=" + Uri.EscapeDataString(attempt.State),
        ["openid.response_nonce"] = "2026-10-06T08:00:00Zabc", ["openid.assoc_handle"] = "1234567890",
        ["openid.signed"] = "signed,op_endpoint,claimed_id,identity,return_to,response_nonce,assoc_handle",
        ["openid.sig"] = "c2lnbmF0dXJl"
    };

    [Fact]
    public async Task Steam_assertions_must_answer_this_attempt_and_be_confirmed_by_steam()
    {
        var (service, fake) = Start();
        service.Change(new() { Action = "allow", Provider = "steam", Subject = SteamId }, "home-pc", CancellationToken.None);
        var (attempt, verifier, url) = await BeginAsync(service, "steam", "http://127.0.0.1:50123/");
        Assert.StartsWith(GatewaySteamProvider.Endpoint + "?", url);
        Assert.Contains("openid.mode=checkid_setup", url);
        Assert.Contains("openid.realm=" + Uri.EscapeDataString("http://127.0.0.1:50123/"), url);
        var (_, who) = await service.CompleteAsync(attempt.Id, "laptop", "LAPTOP", Proof(SteamAssertion(attempt), verifier), CancellationToken.None);
        Assert.Equal(("steam", SteamId), (who.Provider, who.Subject));
        Assert.Contains("openid.mode=check_authentication", fake.CheckBody);
        Assert.Contains("openid.sig=c2lnbmF0dXJl", fake.CheckBody);
        Assert.DoesNotContain("state=", fake.CheckBody);

        async Task<string> Fail(Func<GatewaySignInAttempt, Dictionary<string, string>> assertion)
        {
            var (next, nextVerifier, _) = await BeginAsync(service, "steam", "http://127.0.0.1:50123/");
            return (await Assert.ThrowsAsync<GatewayProtocolException>(async () =>
                await service.CompleteAsync(next.Id, "laptop", "LAPTOP", Proof(assertion(next), nextVerifier), CancellationToken.None))).Failure.Code;
        }

        Assert.Equal("signin.invalid", await Fail(a => SteamAssertion(a, returnTo: "http://127.0.0.1:50123/?state=another-attempt")));
        Assert.Equal("signin.invalid", await Fail(a => { var q = SteamAssertion(a); q["state"] = "another-attempt"; return q; }));
        Assert.Equal("signin.invalid", await Fail(a => SteamAssertion(a, claimed: "https://evil.example/openid/id/" + SteamId)));
        Assert.Equal("signin.invalid", await Fail(a => { var q = SteamAssertion(a); q["openid.signed"] = "signed,op_endpoint"; return q; }));
        Assert.Equal("signin.invalid", await Fail(a => { var q = SteamAssertion(a); q["openid.op_endpoint"] = "https://evil.example/openid/login"; return q; }));
        fake.SteamValid = false;
        Assert.Equal("signin.invalid", await Fail(a => SteamAssertion(a)));
    }
}
