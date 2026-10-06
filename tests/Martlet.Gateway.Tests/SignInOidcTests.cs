using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

/// <summary>Generic OpenID Connect sign-in against an in-process fake issuer: discovery, a self-made JWKS (RSA and EC keys),
/// a token endpoint that issues ID tokens, and every check the host makes on them.</summary>
public sealed class SignInOidcTests
{
    private const string Issuer = "https://idp.example";
    private const string ClientId = "martlet-client";
    private const string Redirect = "http://127.0.0.1:53111/";

    private sealed class FakeIssuer : HttpMessageHandler
    {
        internal readonly RSA Rsa = RSA.Create(2048);
        internal readonly ECDsa Ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal string Kid = "k1";
        internal bool UseEc;
        internal bool PublishKey = true;
        internal Func<Dictionary<string, object?>, Dictionary<string, object?>> Claims = c => c;
        internal string? Nonce;
        internal string? LastTokenBody;
        internal string? LastAuthorization;
        internal int JwksReads;
        internal string[] AuthMethods = ["client_secret_post", "client_secret_basic"];
        internal string ExpectedCode = "the-code";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/.well-known/openid-configuration")
                return Json(new
                {
                    issuer = Issuer, authorization_endpoint = Issuer + "/authorize", token_endpoint = Issuer + "/token", jwks_uri = Issuer + "/jwks",
                    token_endpoint_auth_methods_supported = AuthMethods
                });
            if (path == "/jwks")
            {
                JwksReads++;
                var keys = new List<object>();
                if (PublishKey)
                {
                    if (UseEc)
                    {
                        var p = Ec.ExportParameters(false);
                        keys.Add(new { kty = "EC", kid = Kid, crv = "P-256", x = B64(p.Q.X!), y = B64(p.Q.Y!) });
                    }
                    else
                    {
                        var p = Rsa.ExportParameters(false);
                        keys.Add(new { kty = "RSA", kid = Kid, n = B64(p.Modulus!), e = B64(p.Exponent!) });
                    }
                }
                return Json(new { keys });
            }
            if (path == "/token")
            {
                LastTokenBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                LastAuthorization = request.Headers.Authorization?.ToString();
                var form = LastTokenBody.Split('&').Select(p => p.Split('=')).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
                if (form["code"] != ExpectedCode) return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\"}") };
                return Json(new { access_token = "x", token_type = "Bearer", id_token = Token() });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        internal string Token()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var claims = Claims(new Dictionary<string, object?>
            {
                ["iss"] = Issuer, ["aud"] = ClientId, ["sub"] = "user-123", ["email"] = "me@example.net", ["email_verified"] = true,
                ["iat"] = now, ["exp"] = now + 300, ["nonce"] = Nonce
            });
            var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = UseEc ? "ES256" : "RS256", kid = Kid, typ = "JWT" }));
            var payload = B64(JsonSerializer.SerializeToUtf8Bytes(claims));
            var input = Encoding.ASCII.GetBytes(header + "." + payload);
            var signature = UseEc
                ? Ec.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                : Rsa.SignData(input, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return header + "." + payload + "." + B64(signature);
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }

    private static string B64(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);

    private sealed class Memory : IGatewaySignInStorage
    {
        internal byte[]? Bytes;
        public byte[]? Load() => Bytes;
        public void Save(byte[] bytes) => Bytes = bytes;
    }

    private static (GatewaySignInService Service, GatewayCredentialStore Credentials, FakeIssuer Issuer) Start(string? secret = "s3cret")
    {
        var clock = TimeProvider.System;
        var identity = new GatewayHostIdentity { HostId = "home-host", SpkiFingerprint = "sha256:" + new string('a', 64) };
        var credentials = new GatewayCredentialStore(identity, clock);
        var issuer = new FakeIssuer();
        var providers = new GatewaySignInProviders(clock, issuer);
        var service = new GatewaySignInService(credentials, clock, new SystemGatewayCrypto(), (_, _) => { }) { Providers = providers.Create };
        service.Attach(new Memory());
        service.Change(new()
        {
            Action = "provider",
            ProviderConfig = new() { Id = "authentik", Kind = "oidc", Name = "Authentik", Issuer = Issuer, ClientId = ClientId, ClientSecret = secret }
        }, "home-pc", CancellationToken.None);
        return (service, credentials, issuer);
    }

    private static async Task<(GatewaySignInAttempt Attempt, string Verifier, Uri Url)> BeginAsync(GatewaySignInService service, FakeIssuer issuer)
    {
        var verifier = B64(RandomNumberGenerator.GetBytes(32));
        var challenge = B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var (attempt, url) = await service.BeginAsync("authentik", challenge, Redirect, CancellationToken.None);
        issuer.Nonce = attempt.Nonce;
        return (attempt, verifier, new Uri(url!));
    }

    private static JsonElement Proof(string state, string verifier, string code = "the-code") =>
        JsonSerializer.SerializeToElement(new { query = new { code, state }, code_verifier = verifier });

    private static async Task<string> FailureAsync(GatewaySignInService service, GatewaySignInAttempt attempt, JsonElement proof, string device = "laptop") =>
        (await Assert.ThrowsAsync<GatewayProtocolException>(async () =>
            await service.CompleteAsync(attempt.Id, device, "LAPTOP", proof, CancellationToken.None))).Failure.Code;

    [Fact]
    public async Task An_allowed_identity_signs_in_through_an_oidc_issuer_with_pkce_and_a_checked_id_token()
    {
        var (service, credentials, issuer) = Start();
        var (attempt, verifier, url) = await BeginAsync(service, issuer);
        Assert.Equal("idp.example", url.Host);
        Assert.Equal("/authorize", url.AbsolutePath);
        var query = url.Query.TrimStart('?').Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        Assert.Equal(ClientId, query["client_id"]);
        Assert.Equal(Redirect, query["redirect_uri"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(attempt.State, query["state"]);
        Assert.Equal(attempt.Nonce, query["nonce"]);
        Assert.Equal("openid email profile", query["scope"]);
        Assert.DoesNotContain("s3cret", url.ToString());

        // Signed in at the issuer, but not on the allow list yet: refused, and listed for the owner to allow.
        Assert.Equal("signin.not_allowed", await FailureAsync(service, attempt, Proof(attempt.State, verifier)));
        var waiting = Assert.Single(service.Refused());
        Assert.Equal(("authentik", "user-123", "me@example.net"), (waiting.Identity.Provider, waiting.Identity.Subject, waiting.Identity.Label));
        Assert.Contains("client_secret=s3cret", issuer.LastTokenBody);
        Assert.Contains("code_verifier=" + verifier, issuer.LastTokenBody);

        service.Change(new() { Action = "allow", Provider = "authentik", Subject = "user-123", Label = "me@example.net" }, "home-pc", CancellationToken.None);
        Assert.Empty(service.Refused());
        (attempt, verifier, _) = await BeginAsync(service, issuer);
        var (credential, who) = await service.CompleteAsync(attempt.Id, "laptop", "LAPTOP", Proof(attempt.State, verifier), CancellationToken.None);
        Assert.Equal(("authentik", "user-123", "me@example.net"), (who.Provider, who.Subject, who.Label));
        Assert.Contains(credentials.PairedDevices(), d => d.DeviceId == credential.DeviceId);

        // An EC-signed token after the issuer rotates its key (new kid): the host refreshes the key set once.
        issuer.UseEc = true;
        issuer.Kid = "k2";
        (attempt, verifier, _) = await BeginAsync(service, issuer);
        var reads = issuer.JwksReads;
        await service.CompleteAsync(attempt.Id, "laptop", "LAPTOP", Proof(attempt.State, verifier), CancellationToken.None);
        Assert.Equal(reads + 1, issuer.JwksReads);
    }

    [Fact]
    public async Task Id_tokens_that_do_not_match_the_attempt_or_the_issuer_are_refused()
    {
        var (service, _, issuer) = Start();
        service.Change(new() { Action = "allow", Provider = "authentik", Subject = "user-123" }, "home-pc", CancellationToken.None);

        async Task<string> Try(Func<Dictionary<string, object?>, Dictionary<string, object?>>? claims = null, string? state = null,
            string? verifier = null, string code = "the-code")
        {
            issuer.Claims = claims ?? (c => c);
            var (attempt, rightVerifier, _) = await BeginAsync(service, issuer);
            return await FailureAsync(service, attempt, Proof(state ?? attempt.State, verifier ?? rightVerifier, code));
        }

        Assert.Equal("signin.invalid", await Try(state: "not-the-state"));
        Assert.Equal("signin.invalid", await Try(verifier: B64(RandomNumberGenerator.GetBytes(32))));
        Assert.Equal("signin.invalid", await Try(code: "someone-elses-code"));
        Assert.Equal("signin.invalid", await Try(c => { c["nonce"] = "replayed"; return c; }));
        Assert.Equal("signin.invalid", await Try(c => { c["aud"] = "another-client"; return c; }));
        Assert.Equal("signin.invalid", await Try(c => { c["aud"] = new[] { ClientId, "other" }; return c; }));
        Assert.Equal("signin.invalid", await Try(c => { c["iss"] = "https://evil.example"; return c; }));
        Assert.Equal("signin.invalid", await Try(c => { c["exp"] = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds(); return c; }));
        Assert.Equal("signin.invalid", await Try(c => { c.Remove("sub"); return c; }));
        // A token signed with a key the issuer doesn't publish (even after refreshing its key set).
        issuer.PublishKey = false;
        issuer.Kid = "rogue";
        Assert.Equal("signin.invalid", await Try());
        issuer.PublishKey = true;
        issuer.Kid = "k1";
        // A multi-audience token is fine when the authorized party is this client.
        issuer.Claims = c => { c["aud"] = new[] { ClientId, "other" }; c["azp"] = ClientId; return c; };
        var (attempt, verifier, _) = await BeginAsync(service, issuer);
        await service.CompleteAsync(attempt.Id, "laptop", "LAPTOP", Proof(attempt.State, verifier), CancellationToken.None);
    }

    [Fact]
    public async Task The_client_secret_goes_by_basic_auth_when_the_issuer_only_takes_that_and_a_public_client_sends_none()
    {
        var (service, _, issuer) = Start();
        issuer.AuthMethods = ["client_secret_basic"];
        service.Change(new() { Action = "allow", Provider = "authentik", Subject = "user-123" }, "home-pc", CancellationToken.None);
        var (attempt, verifier, _) = await BeginAsync(service, issuer);
        await service.CompleteAsync(attempt.Id, "laptop", "LAPTOP", Proof(attempt.State, verifier), CancellationToken.None);
        Assert.StartsWith("Basic ", issuer.LastAuthorization);
        Assert.DoesNotContain("client_secret", issuer.LastTokenBody);

        var (open, _, publicIssuer) = Start(secret: null);
        open.Change(new() { Action = "allow", Provider = "authentik", Subject = "user-123" }, "home-pc", CancellationToken.None);
        (attempt, verifier, _) = await BeginAsync(open, publicIssuer);
        await open.CompleteAsync(attempt.Id, "laptop", "LAPTOP", Proof(attempt.State, verifier), CancellationToken.None);
        Assert.Null(publicIssuer.LastAuthorization);
        Assert.DoesNotContain("client_secret", publicIssuer.LastTokenBody);
    }

    [Fact]
    public async Task Google_tokens_name_their_issuer_without_the_scheme()
    {
        var clock = TimeProvider.System;
        var identity = new GatewayHostIdentity { HostId = "home-host", SpkiFingerprint = "sha256:" + new string('a', 64) };
        var credentials = new GatewayCredentialStore(identity, clock);
        var issuer = new GoogleIssuer();
        var service = new GatewaySignInService(credentials, clock, new SystemGatewayCrypto(), (_, _) => { })
        {
            Providers = new GatewaySignInProviders(clock, issuer).Create
        };
        service.Attach(new Memory());
        service.Change(new()
        {
            Action = "provider",
            ProviderConfig = new() { Id = "google", Kind = "oidc", Name = "Google", Issuer = "https://accounts.google.com", ClientId = ClientId, ClientSecret = "x" }
        }, "home-pc", CancellationToken.None);
        service.Change(new() { Action = "allow", Provider = "google", Subject = "user-123" }, "home-pc", CancellationToken.None);
        var verifier = B64(RandomNumberGenerator.GetBytes(32));
        var (attempt, url) = await service.BeginAsync("google", B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), Redirect, CancellationToken.None);
        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url);
        issuer.Inner.Nonce = attempt.Nonce;
        issuer.Inner.Claims = c => { c["iss"] = "accounts.google.com"; return c; };
        var (_, who) = await service.CompleteAsync(attempt.Id, "laptop", "LAPTOP", Proof(attempt.State, verifier), CancellationToken.None);
        Assert.Equal("google", who.Provider);
    }

    /// <summary>Google's discovery document shape on accounts.google.com, backed by the fake issuer's keys and tokens.</summary>
    private sealed class GoogleIssuer : HttpMessageHandler
    {
        internal readonly FakeIssuer Inner = new();
        private readonly HttpMessageInvoker inner;
        internal GoogleIssuer() => inner = new HttpMessageInvoker(Inner);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/.well-known/openid-configuration")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        issuer = "https://accounts.google.com", authorization_endpoint = "https://accounts.google.com/o/oauth2/v2/auth",
                        token_endpoint = "https://oauth2.googleapis.com/token", jwks_uri = "https://www.googleapis.com/oauth2/v3/certs",
                        token_endpoint_auth_methods_supported = new[] { "client_secret_post", "client_secret_basic" }
                    }), Encoding.UTF8, "application/json")
                });
            var path = request.RequestUri.AbsolutePath switch { "/oauth2/v3/certs" => "/jwks", "/token" => "/token", var other => other };
            request.RequestUri = new Uri(Issuer + path);
            return inner.SendAsync(request, cancellationToken);
        }
    }
}
