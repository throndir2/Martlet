using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Martlet.Gateway;

/// <summary>
/// Builds the providers configured in signin.json and shares their HTTP client and the issuers' discovery documents and
/// signing keys. Provider calls leave the host only for the configured provider's own endpoints (https, no redirects).
/// </summary>
internal sealed class GatewaySignInProviders(TimeProvider clock, HttpMessageHandler? handler = null)
{
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);
    internal const int MaximumDocumentBytes = 256 * 1024;
    private readonly HttpClient http = new(handler ?? new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10),
        AutomaticDecompression = DecompressionMethods.All
    }, disposeHandler: handler is null) { Timeout = TimeSpan.FromSeconds(15) };
    private readonly ConcurrentDictionary<string, (JsonElement Value, DateTimeOffset At)> documents = new(StringComparer.Ordinal);

    internal TimeProvider Clock => clock;

    internal IGatewaySignInProvider? Create(GatewaySignInProviderConfig config) => config.Kind switch
    {
        "oidc" => new GatewayOidcProvider(config, this),
        _ => null
    };

    /// <summary>A JSON document from a provider, kept for <see cref="CacheLifetime"/> unless <paramref name="fresh"/>.</summary>
    internal async ValueTask<JsonElement> GetJsonAsync(string url, bool fresh, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (!fresh && documents.TryGetValue(url, out var cached) && now - cached.At < CacheLifetime) return cached.Value;
        using var request = new HttpRequestMessage(HttpMethod.Get, RequireHttps(url));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var value = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        documents[url] = (value, now);
        return value;
    }

    /// <summary>Posts a form to a provider and returns its JSON answer; a refusal (4xx) is the person's sign-in failing.</summary>
    internal async ValueTask<JsonElement> PostFormAsync(string url, IEnumerable<KeyValuePair<string, string>> form,
        AuthenticationHeaderValue? authorization, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RequireHttps(url)) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = authorization;
        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new GatewayProtocolException("signin.provider");
        }
        using (response)
        {
            if ((int)response.StatusCode is >= 400 and < 500) throw new GatewayProtocolException("signin.invalid");
            GatewayRules.Require(response.StatusCode == HttpStatusCode.OK, "signin.provider");
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            GatewayRules.Require(bytes.Length is > 0 and <= MaximumDocumentBytes, "signin.provider");
            try
            {
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
                return document.RootElement.Clone();
            }
            catch (JsonException) { throw new GatewayProtocolException("signin.provider"); }
        }
    }

    internal static string RequireHttps(string url)
    {
        GatewayRules.Require(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0,
            "signin.provider");
        return url;
    }

    /// <summary>A string member of a JSON object, or null.</summary>
    internal static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    /// <summary>The callback query the computer's loopback redirect received (proof.query), as name/value pairs.</summary>
    internal static IReadOnlyDictionary<string, string> Query(JsonElement proof)
    {
        if (proof.ValueKind != JsonValueKind.Object || !proof.TryGetProperty("query", out var query) || query.ValueKind != JsonValueKind.Object)
            throw new GatewayProtocolException("request.invalid");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in query.EnumerateObject().Take(64))
        {
            GatewayRules.Require(item.Value.ValueKind == JsonValueKind.String && item.Value.GetString()!.Length <= 4096, "request.invalid");
            result[item.Name] = item.Value.GetString()!;
        }
        return result;
    }

    /// <summary>Checks the PKCE verifier the computer kept against the challenge it sent when the attempt began (RFC 7636 S256).</summary>
    internal static string Verifier(JsonElement proof, GatewaySignInAttempt attempt)
    {
        var verifier = Text(proof, "code_verifier");
        GatewayRules.Require(verifier is { Length: >= 43 and <= 128 } && attempt.CodeChallenge is not null, "signin.invalid");
        var challenge = Base64Url.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier!)));
        GatewayRules.Require(CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(challenge), Encoding.ASCII.GetBytes(attempt.CodeChallenge!)),
            "signin.invalid");
        return verifier!;
    }

    /// <summary>The authorization code from the callback, after checking its state matches the attempt and it isn't an error.</summary>
    internal static string Code(IReadOnlyDictionary<string, string> query, GatewaySignInAttempt attempt)
    {
        GatewayRules.Require(!query.ContainsKey("error"), "signin.invalid");
        GatewayRules.Require(query.TryGetValue("state", out var state) &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(attempt.State)), "signin.invalid");
        GatewayRules.Require(query.TryGetValue("code", out var code) && code.Length is > 0 and <= 2048, "signin.invalid");
        return code!;
    }

    internal static string Url(string endpoint, IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        var query = string.Join("&", parameters.Where(p => p.Value is not null)
            .Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value!)));
        return endpoint + (endpoint.Contains('?') ? "&" : "?") + query;
    }
}

/// <summary>
/// Any OpenID Connect issuer (Authentik, Authelia, Keycloak, Pocket ID, Google, ...): authorization code with PKCE in the
/// computer's browser and a loopback redirect (RFC 8252). The host exchanges the code itself (with the client secret it
/// keeps, when the client has one) and accepts the ID token only when its signature checks against the issuer's published
/// keys and its issuer, audience, expiry and nonce match this attempt. The identity is the issuer's stable <c>sub</c>; a
/// verified email (or the user name) is its label.
/// </summary>
internal sealed class GatewayOidcProvider(GatewaySignInProviderConfig config, GatewaySignInProviders shared) : IGatewaySignInProvider
{
    internal static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    private string Issuer => config.Issuer!.TrimEnd('/');

    private async ValueTask<JsonElement> DiscoverAsync(CancellationToken cancellationToken)
    {
        var document = await shared.GetJsonAsync(Issuer + "/.well-known/openid-configuration", false, cancellationToken).ConfigureAwait(false);
        GatewayRules.Require(SameIssuer(GatewaySignInProviders.Text(document, "issuer")), "signin.provider");
        return document;
    }

    /// <summary>Google's ID tokens name their issuer with or without the scheme.</summary>
    private bool SameIssuer(string? issuer) =>
        issuer is not null && (issuer.TrimEnd('/') == Issuer ||
            Issuer == "https://accounts.google.com" && issuer == "accounts.google.com");

    public async ValueTask<string?> AuthorizeAsync(GatewaySignInAttempt attempt, CancellationToken cancellationToken)
    {
        var discovery = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var endpoint = GatewaySignInProviders.RequireHttps(GatewaySignInProviders.Text(discovery, "authorization_endpoint") ??
            throw new GatewayProtocolException("signin.provider"));
        return GatewaySignInProviders.Url(endpoint, new Dictionary<string, string?>
        {
            ["response_type"] = "code", ["client_id"] = config.ClientId, ["redirect_uri"] = attempt.RedirectUri,
            ["scope"] = config.Scopes ?? "openid email profile", ["state"] = attempt.State, ["nonce"] = attempt.Nonce,
            ["code_challenge"] = attempt.CodeChallenge, ["code_challenge_method"] = "S256", ["prompt"] = "select_account"
        });
    }

    public async ValueTask<GatewaySignInIdentity> VerifyAsync(GatewaySignInAttempt attempt, JsonElement proof, CancellationToken cancellationToken)
    {
        var query = GatewaySignInProviders.Query(proof);
        var code = GatewaySignInProviders.Code(query, attempt);
        var verifier = GatewaySignInProviders.Verifier(proof, attempt);
        var discovery = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var tokenEndpoint = GatewaySignInProviders.Text(discovery, "token_endpoint") ?? throw new GatewayProtocolException("signin.provider");
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"), new("code", code), new("redirect_uri", attempt.RedirectUri!),
            new("client_id", config.ClientId!), new("code_verifier", verifier)
        };
        AuthenticationHeaderValue? basic = null;
        if (config.ClientSecret is { } secret)
        {
            var methods = discovery.TryGetProperty("token_endpoint_auth_methods_supported", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(m => m.GetString()).ToArray() : ["client_secret_basic"];
            if (methods.Contains("client_secret_post") || !methods.Contains("client_secret_basic"))
                form.Add(new("client_secret", secret));
            else
                basic = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    Uri.EscapeDataString(config.ClientId!) + ":" + Uri.EscapeDataString(secret))));
        }
        var tokens = await shared.PostFormAsync(tokenEndpoint, form, basic, cancellationToken).ConfigureAwait(false);
        var idToken = GatewaySignInProviders.Text(tokens, "id_token") ?? throw new GatewayProtocolException("signin.invalid");
        var jwksUri = GatewaySignInProviders.Text(discovery, "jwks_uri") ?? throw new GatewayProtocolException("signin.provider");
        var claims = await GatewayJwt.VerifyAsync(idToken, fresh => shared.GetJsonAsync(jwksUri, fresh, cancellationToken)).ConfigureAwait(false);

        var now = shared.Clock.GetUtcNow();
        GatewayRules.Require(SameIssuer(GatewaySignInProviders.Text(claims, "iss")), "signin.invalid");
        var audiences = claims.TryGetProperty("aud", out var aud)
            ? aud.ValueKind == JsonValueKind.Array ? aud.EnumerateArray().Select(a => a.GetString()).ToArray() : [aud.GetString()]
            : [];
        GatewayRules.Require(audiences.Contains(config.ClientId) &&
            (audiences.Length == 1 || GatewaySignInProviders.Text(claims, "azp") == config.ClientId), "signin.invalid");
        GatewayRules.Require(claims.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var expires) &&
            DateTimeOffset.FromUnixTimeSeconds(expires) + ClockSkew > now, "signin.invalid");
        GatewayRules.Require(!claims.TryGetProperty("iat", out var iat) || iat.TryGetInt64(out var issued) &&
            DateTimeOffset.FromUnixTimeSeconds(issued) - ClockSkew <= now, "signin.invalid");
        var nonce = GatewaySignInProviders.Text(claims, "nonce");
        GatewayRules.Require(nonce is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(nonce), Encoding.UTF8.GetBytes(attempt.Nonce)),
            "signin.invalid");
        var subject = GatewaySignInProviders.Text(claims, "sub");
        GatewayRules.Require(subject is { Length: > 0 and <= 255 }, "signin.invalid");
        var verified = !claims.TryGetProperty("email_verified", out var flag) || flag.ValueKind == JsonValueKind.True ||
            flag.ValueKind == JsonValueKind.String && flag.GetString() == "true";
        var label = (verified ? GatewaySignInProviders.Text(claims, "email") : null) ?? GatewaySignInProviders.Text(claims, "preferred_username") ??
            GatewaySignInProviders.Text(claims, "name");
        return new(config.Id, subject!, label is { Length: <= 128 } ? label : null);
    }
}

/// <summary>Checks a JSON Web Token's signature against a JSON Web Key Set (RS256/384/512, PS256/384/512, ES256/384) and
/// returns its claims. An unknown key ID refreshes the key set once (issuers rotate keys).</summary>
internal static class GatewayJwt
{
    internal static async ValueTask<JsonElement> VerifyAsync(string token, Func<bool, ValueTask<JsonElement>> keys)
    {
        var parts = token.Split('.');
        GatewayRules.Require(parts.Length == 3 && token.Length <= 16_384, "signin.invalid");
        JsonElement header, claims;
        byte[] signature;
        try
        {
            header = Parse(parts[0]);
            claims = Parse(parts[1]);
            signature = System.Buffers.Text.Base64Url.DecodeFromChars(parts[2]);
        }
        catch (Exception error) when (error is FormatException or JsonException) { throw new GatewayProtocolException("signin.invalid"); }
        var alg = GatewaySignInProviders.Text(header, "alg");
        var kid = GatewaySignInProviders.Text(header, "kid");
        GatewayRules.Require(alg is "RS256" or "RS384" or "RS512" or "PS256" or "PS384" or "PS512" or "ES256" or "ES384", "signin.invalid");
        var input = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        foreach (var fresh in new[] { false, true })
        {
            var set = await keys(fresh).ConfigureAwait(false);
            GatewayRules.Require(set.TryGetProperty("keys", out var list) && list.ValueKind == JsonValueKind.Array, "signin.provider");
            var candidates = list.EnumerateArray().Where(k => kid is null || GatewaySignInProviders.Text(k, "kid") == kid).ToArray();
            if (candidates.Length == 0 && !fresh) continue;
            if (candidates.Any(key => Verify(alg!, key, input, signature))) return claims;
            throw new GatewayProtocolException("signin.invalid");
        }
        throw new GatewayProtocolException("signin.invalid");
    }

    private static JsonElement Parse(string part)
    {
        using var document = JsonDocument.Parse(System.Buffers.Text.Base64Url.DecodeFromChars(part), new JsonDocumentOptions { MaxDepth = 16 });
        GatewayRules.Require(document.RootElement.ValueKind == JsonValueKind.Object, "signin.invalid");
        return document.RootElement.Clone();
    }

    private static bool Verify(string alg, JsonElement key, byte[] input, byte[] signature)
    {
        var hash = alg[2..] switch { "256" => HashAlgorithmName.SHA256, "384" => HashAlgorithmName.SHA384, _ => HashAlgorithmName.SHA512 };
        try
        {
            static byte[] Bytes(JsonElement key, string name) =>
                System.Buffers.Text.Base64Url.DecodeFromChars(GatewaySignInProviders.Text(key, name) ?? throw new FormatException());
            if (alg[0] is 'R' or 'P')
            {
                if (GatewaySignInProviders.Text(key, "kty") != "RSA") return false;
                using var rsa = RSA.Create(new RSAParameters { Modulus = Bytes(key, "n"), Exponent = Bytes(key, "e") });
                if (rsa.KeySize < 2048) return false;
                return rsa.VerifyData(input, signature, hash, alg[0] == 'R' ? RSASignaturePadding.Pkcs1 : RSASignaturePadding.Pss);
            }
            var curve = GatewaySignInProviders.Text(key, "crv");
            if (GatewaySignInProviders.Text(key, "kty") != "EC" || (alg, curve) is not (("ES256", "P-256") or ("ES384", "P-384"))) return false;
            using var ecdsa = ECDsa.Create(new ECParameters
            {
                Curve = curve == "P-256" ? ECCurve.NamedCurves.nistP256 : ECCurve.NamedCurves.nistP384,
                Q = new ECPoint { X = Bytes(key, "x"), Y = Bytes(key, "y") }
            });
            return ecdsa.VerifyData(input, signature, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception error) when (error is FormatException or CryptographicException or ArgumentException) { return false; }
    }
}
