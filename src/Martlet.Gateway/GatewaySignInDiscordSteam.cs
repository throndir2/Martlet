using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Martlet.Gateway;

/// <summary>
/// Discord (OAuth2 without an ID token): authorization code with PKCE in the computer's browser, redirected to the loopback
/// port registered for the Discord application. The host exchanges the code with the client secret only it keeps (HTTP
/// basic) and asks Discord who the token belongs to (<c>/users/@me</c>, scope <c>identify</c>). The identity is the Discord
/// user ID; the user name is its label.
/// </summary>
internal sealed class GatewayDiscordProvider(GatewaySignInProviderConfig config, GatewaySignInProviders shared) : IGatewaySignInProvider
{
    internal const string AuthorizeEndpoint = "https://discord.com/oauth2/authorize";
    internal const string TokenEndpoint = "https://discord.com/api/oauth2/token";
    internal const string UserEndpoint = "https://discord.com/api/users/@me";

    public ValueTask<string?> AuthorizeAsync(GatewaySignInAttempt attempt, CancellationToken cancellationToken) =>
        ValueTask.FromResult<string?>(GatewaySignInProviders.Url(AuthorizeEndpoint, new Dictionary<string, string?>
        {
            ["response_type"] = "code", ["client_id"] = config.ClientId, ["redirect_uri"] = attempt.RedirectUri,
            ["scope"] = config.Scopes ?? "identify", ["state"] = attempt.State, ["code_challenge"] = attempt.CodeChallenge,
            ["code_challenge_method"] = "S256", ["prompt"] = "consent"
        }));

    public async ValueTask<GatewaySignInIdentity> VerifyAsync(GatewaySignInAttempt attempt, JsonElement proof, CancellationToken cancellationToken)
    {
        var query = GatewaySignInProviders.Query(proof);
        var code = GatewaySignInProviders.Code(query, attempt);
        var verifier = GatewaySignInProviders.Verifier(proof, attempt);
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
            Uri.EscapeDataString(config.ClientId!) + ":" + Uri.EscapeDataString(config.ClientSecret ?? ""))));
        var tokens = await shared.PostFormAsync(TokenEndpoint,
        [
            new("grant_type", "authorization_code"), new("code", code), new("redirect_uri", attempt.RedirectUri!), new("code_verifier", verifier)
        ], basic, cancellationToken).ConfigureAwait(false);
        var access = GatewaySignInProviders.Text(tokens, "access_token") ?? throw new GatewayProtocolException("signin.invalid");
        GatewayRules.Require(GatewaySignInProviders.Text(tokens, "token_type")?.Equals("Bearer", StringComparison.OrdinalIgnoreCase) != false,
            "signin.invalid");
        using var request = new HttpRequestMessage(HttpMethod.Get, UserEndpoint);
        request.Headers.Authorization = new("Bearer", access);
        var user = await shared.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var id = GatewaySignInProviders.Text(user, "id");
        GatewayRules.Require(id is { Length: > 0 and <= 32 } && id.All(char.IsAsciiDigit), "signin.invalid");
        var name = GatewaySignInProviders.Text(user, "username");
        var shown = GatewaySignInProviders.Text(user, "global_name");
        var label = name is null ? shown : shown is null || shown == name ? name : $"{shown} (@{name})";
        return new(config.Id, id!, label is { Length: <= 128 } ? label : null);
    }
}

/// <summary>
/// Steam (OpenID 2.0): the computer's browser signs in at Steam, which sends it back to the loopback redirect with a signed
/// positive assertion. The host checks that the assertion answers this attempt (its <c>return_to</c> carries the attempt's
/// state, the realm is the computer's loopback address) and names a Steam account, then asks Steam to confirm it
/// (<c>check_authentication</c>). The identity is the SteamID64.
/// </summary>
internal sealed partial class GatewaySteamProvider(GatewaySignInProviderConfig config, GatewaySignInProviders shared) : IGatewaySignInProvider
{
    internal const string Endpoint = "https://steamcommunity.com/openid/login";
    private const string Namespace = "http://specs.openid.net/auth/2.0";
    private const string IdentifierSelect = "http://specs.openid.net/auth/2.0/identifier_select";

    [GeneratedRegex("^https://steamcommunity\\.com/openid/id/(7656119[0-9]{10})$")]
    private static partial Regex ClaimedId();

    private static string ReturnTo(GatewaySignInAttempt attempt) => attempt.RedirectUri + "?state=" + Uri.EscapeDataString(attempt.State);

    public ValueTask<string?> AuthorizeAsync(GatewaySignInAttempt attempt, CancellationToken cancellationToken) =>
        ValueTask.FromResult<string?>(GatewaySignInProviders.Url(Endpoint, new Dictionary<string, string?>
        {
            ["openid.ns"] = Namespace, ["openid.mode"] = "checkid_setup", ["openid.return_to"] = ReturnTo(attempt),
            ["openid.realm"] = attempt.RedirectUri, ["openid.identity"] = IdentifierSelect, ["openid.claimed_id"] = IdentifierSelect
        }));

    public async ValueTask<GatewaySignInIdentity> VerifyAsync(GatewaySignInAttempt attempt, JsonElement proof, CancellationToken cancellationToken)
    {
        var query = GatewaySignInProviders.Query(proof);
        _ = GatewaySignInProviders.Verifier(proof, attempt);
        string Field(string name) => query.TryGetValue(name, out var value) ? value : throw new GatewayProtocolException("signin.invalid");
        GatewayRules.Require(Field("openid.ns") == Namespace && Field("openid.mode") == "id_res" && Field("openid.op_endpoint") == Endpoint,
            "signin.invalid");
        GatewayRules.Require(CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Field("state")), Encoding.UTF8.GetBytes(attempt.State)) &&
            Field("openid.return_to") == ReturnTo(attempt), "signin.invalid");
        var match = ClaimedId().Match(Field("openid.claimed_id"));
        GatewayRules.Require(match.Success && Field("openid.identity") == Field("openid.claimed_id"), "signin.invalid");
        var signed = Field("openid.signed").Split(',');
        GatewayRules.Require(new[] { "op_endpoint", "claimed_id", "identity", "return_to", "response_nonce", "assoc_handle" }
            .All(signed.Contains), "signin.invalid");
        var form = query.Where(p => p.Key.StartsWith("openid.", StringComparison.Ordinal) && p.Key != "openid.mode")
            .Append(new("openid.mode", "check_authentication"));
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new FormUrlEncodedContent(form) };
        var answer = await shared.SendTextAsync(request, cancellationToken).ConfigureAwait(false);
        GatewayRules.Require(answer.Split('\n').Any(line => line.Trim() == "is_valid:true"), "signin.invalid");
        return new(config.Id, match.Groups[1].Value, "Steam " + match.Groups[1].Value);
    }
}
