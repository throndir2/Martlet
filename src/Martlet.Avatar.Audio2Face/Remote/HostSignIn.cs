using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Network;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>A way to sign in to a host, as the host lists it: "owner" (the owner account: name, password and authenticator
/// code, typed into Martlet) or a provider that signs in in the browser ("oidc", "discord", "steam").</summary>
public sealed record HostSignInProvider(string Id, string Kind, string Name)
{
    public bool InBrowser => Kind != "owner";
}

/// <summary>Who a computer signed in as (shown to people; the host decides).</summary>
public sealed record HostSignInIdentity(string Provider, string Subject, string? Label)
{
    public override string ToString() => Label is { Length: > 0 } ? $"{Label} ({Provider})" : $"{Subject} ({Provider})";
}

/// <summary>What a host said when it listed join requests: the identity a desktop signed in as to pair there.</summary>
public sealed record HostSignInAttestation(string Provider, string Subject, string? Label, DateTimeOffset At);

/// <summary>A sign-in that <c>/signin/begin</c> started: send the proof for <see cref="AttemptId"/> within ten minutes. A
/// browser provider gives <see cref="AuthorizeUrl"/> to open.</summary>
public sealed record HostSignInAttempt(string Origin, string AttemptId, string Provider, string State, string Nonce, string? AuthorizeUrl);

/// <summary>
/// Joining from outside home by signing in, on the computer that is away: it takes the owner's <see cref="NetworkInvite"/>
/// (host ID, TLS pin, home and outside addresses), pins the host's key before sending anything, lists the ways to sign in,
/// and finishes with a device credential exactly like pairing. The pairing is kept under the host's home origin from the
/// invite; reaching it from outside afterwards uses the host's outside addresses.
/// </summary>
public static class HostSignInClient
{
    private const string SignInPath = "/martlet/v1/signin";
    private static readonly object Version = new { major = 2, minor = 0 };

    /// <summary>Lists the ways to sign in, trying the invite's outside addresses first and then its home address; returns
    /// the origin that answered with the pinned key.</summary>
    public static async Task<(string Origin, IReadOnlyList<HostSignInProvider> Providers)> ReadProvidersAsync(NetworkInvite invite,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invite);
        foreach (var origin in invite.Origins())
        {
            try
            {
                using var http = CreatePinnedClient(origin, invite.SpkiFingerprint);
                using var request = new HttpRequestMessage(HttpMethod.Get, origin + SignInPath);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
                using var document = await Audio2FaceHostClient.ReadJson(response, 16 * 1024, timeout.Token).ConfigureAwait(false);
                var root = document.RootElement;
                if (response.StatusCode == HttpStatusCode.BadRequest)
                    throw new Audio2FaceHostException("signin.unsupported",
                        $"{invite.HostId} runs an older Martlet without sign-in. Update it at home first.");
                if (response.StatusCode != HttpStatusCode.OK) throw Audio2FaceHostClient.Remote(root);
                if (root.GetProperty("host_id").GetString() != invite.HostId)
                    throw new Audio2FaceHostException("host.pin_mismatch", "A different Martlet host answered; check the invite.");
                var providers = root.GetProperty("providers").EnumerateArray().Take(16).Select(p => new HostSignInProvider(
                    p.GetProperty("id").GetString()!, p.GetProperty("kind").GetString()!, Clean(p.GetProperty("name").GetString()) ?? "Sign-in"))
                    .ToArray();
                return (origin, providers);
            }
            // Not answering (or answering with another key) here: try the invite's next address.
            catch (Audio2FaceHostException error) when (error.Code is "host.unreachable") { }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new Audio2FaceHostException("response.invalid", "The host's list of sign-ins was invalid.");
            }
        }
        throw new Audio2FaceHostException("host.unreachable",
            $"Couldn't reach {invite.HostId} at {string.Join(" or ", invite.Origins().Select(o => new Uri(o).Authority))} with the key in the " +
            "invite. Check that the host is running and reachable from here, or ask for a new invite.");
    }

    /// <summary>Starts a sign-in. For a browser provider pass the PKCE challenge (S256 of a verifier this computer keeps) and
    /// this computer's loopback redirect (http://127.0.0.1:&lt;port&gt;/).</summary>
    public static async Task<HostSignInAttempt> BeginAsync(NetworkInvite invite, string origin, string provider, string? codeChallenge = null,
        string? redirectUri = null, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["protocol_version"] = Version, ["provider"] = provider, ["code_challenge"] = codeChallenge, ["redirect_uri"] = redirectUri
        }.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value));
        using var document = await PostAsync(invite, origin, SignInPath + "/begin", body, HttpStatusCode.OK, cancellationToken).ConfigureAwait(false);
        try
        {
            var root = document.RootElement;
            var url = root.TryGetProperty("authorize_url", out var link) && link.ValueKind == JsonValueKind.String ? link.GetString() : null;
            if (url is not null && (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps))
                throw new FormatException();
            return new(origin, root.GetProperty("attempt_id").GetString()!, root.GetProperty("provider").GetString()!,
                root.GetProperty("state").GetString()!, root.GetProperty("nonce").GetString()!, url);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's answer to the sign-in was invalid.");
        }
    }

    /// <summary>Finishes a sign-in with its proof and returns the pairing (kept under the invite's home origin), its secret
    /// (for the OS vault) and who this computer signed in as.</summary>
    public static async Task<(Audio2FaceHostPairing Pairing, string Secret, HostSignInIdentity Identity)> CompleteAsync(NetworkInvite invite,
        HostSignInAttempt attempt, string deviceId, string displayName, JsonObject proof, CancellationToken cancellationToken = default)
    {
        Audio2FaceHostClient.RequireIdentifier(deviceId, "device ID");
        var name = new string(displayName.Where(c => c is >= ' ' and <= '~').Take(64).ToArray()).Trim();
        if (name.Length == 0) name = "Martlet desktop";
        var body = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["protocol_version"] = new JsonObject { ["major"] = 2, ["minor"] = 0 },
            ["attempt_id"] = attempt.AttemptId, ["device_id"] = deviceId, ["display_name"] = name, ["proof"] = proof.DeepClone()
        });
        try
        {
            using var document = await PostAsync(invite, attempt.Origin, SignInPath + "/complete", body, HttpStatusCode.Created, cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            var credentialId = root.GetProperty("credential_id").GetString()!;
            var secret = root.GetProperty("credential_secret").GetString()!;
            if (root.GetProperty("host_id").GetString() != invite.HostId || root.GetProperty("device_id").GetString() != deviceId ||
                root.GetProperty("lifetime").GetProperty("kind").GetString() != "paired" ||
                !root.GetProperty("roles").EnumerateArray().Any(role => role.GetString() == "voice") ||
                !Audio2FaceHostClient.TryBase64Url(credentialId, 16, out _) || !Audio2FaceHostClient.TryBase64Url(secret, 32, out var raw))
                throw new Audio2FaceHostException("pairing.invalid", "The host returned an unexpected pairing; sign in again.");
            CryptographicOperations.ZeroMemory(raw);
            var signedIn = root.GetProperty("signed_in");
            var who = new HostSignInIdentity(signedIn.GetProperty("provider").GetString()!, signedIn.GetProperty("subject").GetString()!,
                signedIn.TryGetProperty("label", out var label) ? Clean(label.GetString()) : null);
            var pairing = new Audio2FaceHostPairing
            {
                Origin = Audio2FaceHostClient.CanonicalOrigin(invite.Origin), HostId = invite.HostId, SpkiFingerprint = invite.SpkiFingerprint,
                DeviceId = deviceId, CredentialId = credentialId
            };
            return (pairing, secret, who);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's sign-in answer was invalid.");
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    /// <summary>Signs in with the owner account (name, password, a current authenticator code or an unused recovery code).</summary>
    public static async Task<(Audio2FaceHostPairing Pairing, string Secret, HostSignInIdentity Identity)> SignInAsOwnerAsync(NetworkInvite invite,
        string origin, string user, string password, string code, string deviceId, string displayName, CancellationToken cancellationToken = default)
    {
        var attempt = await BeginAsync(invite, origin, "owner", cancellationToken: cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(invite, attempt, deviceId, displayName,
            new JsonObject { ["user"] = user, ["password"] = password, ["code"] = code }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> PostAsync(NetworkInvite invite, string origin, string path, byte[] body, HttpStatusCode expected,
        CancellationToken cancellationToken)
    {
        using var http = CreatePinnedClient(origin, invite.SpkiFingerprint);
        using var request = new HttpRequestMessage(HttpMethod.Post, origin + path) { Content = Audio2FaceHostClient.JsonContent(body) };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        var document = await Audio2FaceHostClient.ReadJson(response, 16 * 1024, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == expected) return document;
        using (document)
        {
            var code = document.RootElement.TryGetProperty("code", out var value) ? value.GetString() : null;
            throw code switch
            {
                "signin.invalid" => new Audio2FaceHostException(code,
                    "That sign-in didn't work. Check the account name, password and authenticator code; several wrong tries lock sign-in for a while."),
                "signin.not_allowed" => new Audio2FaceHostException(code,
                    "You signed in, but the host doesn't allow that account. At home, add it under the host's sign-in settings."),
                "signin.unavailable" => new Audio2FaceHostException(code, "Signing in that way isn't set up on this host."),
                "signin.expired" => new Audio2FaceHostException(code, "That sign-in took too long. Start again."),
                "auth.throttled" or "auth.locked" or "auth.rate" => new Audio2FaceHostException(code,
                    "Too many sign-in attempts. Wait a few minutes, then try again." +
                    (response.Headers.RetryAfter?.Delta is { } wait ? $" (about {Math.Ceiling(wait.TotalMinutes)} min)" : "")),
                _ => Audio2FaceHostClient.Remote(document.RootElement)
            };
        }
    }

    /// <summary>A client for <paramref name="origin"/> (an outside name or address, or the home address) that trusts only the
    /// pinned key. The certificate names the host's home address, so a name mismatch is expected when connecting from
    /// outside; the pin is the authority, as for every pairing.</summary>
    internal static HttpClient CreatePinnedClient(string origin, string spkiFingerprint)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false, Credentials = null,
            AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(8)
        };
        handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        handler.SslOptions.CertificateChainPolicy = new() { RevocationMode = X509RevocationMode.NoCheck, DisableCertificateDownloads = true };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) return false;
            using var owned = new X509Certificate2(certificate);
            var now = DateTime.UtcNow;
            return now >= owned.NotBefore.ToUniversalTime() && now <= owned.NotAfter.ToUniversalTime() &&
                Audio2FaceHostClient.Fingerprint(owned) == spkiFingerprint;
        };
        return new HttpClient(handler, disposeHandler: true) { BaseAddress = new Uri(origin + "/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>A fresh PKCE verifier (RFC 7636: 32 random bytes, base64url) and its S256 challenge.</summary>
    public static (string Verifier, string Challenge) NewPkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url.EncodeToString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier))));
    }

    internal static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new string(value.Trim().Take(128).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}

/// <summary>A host's sign-in settings as a member desktop sees them (never a secret).</summary>
public sealed record HostSignInSettings(string HostId, string? OwnerUser, int RecoveryCodesLeft,
    IReadOnlyList<HostSignInProviderSettings> Providers, IReadOnlyList<HostSignInAllowed> Allowed, IReadOnlyList<HostSignInEnrolled> Enrolled,
    IReadOnlyList<string>? RecoveryCodes);

public sealed record HostSignInProviderSettings(string Id, string Kind, string Name, string? Issuer, string? ClientId, string? Scopes, bool HasClientSecret);

public sealed record HostSignInAllowed(string Provider, string Subject, string? Label);

public sealed record HostSignInEnrolled(string DeviceId, string Provider, string Subject, string? Label, DateTimeOffset EnrolledAt);

/// <summary>The host's sign-in settings, read and changed by a member desktop at home over its signed, pinned connection.</summary>
public sealed partial class Audio2FaceHostConnection
{
    private const string SignInSettingsPath = "/martlet/v1/signin/settings";

    public async Task<HostSignInSettings> ReadSignInSettingsAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + SignInSettingsPath);
        Sign(request, []);
        return await SignInSettingsAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies one change: <c>{"action":"owner","user","password","totp_secret","code"}</c>,
    /// <c>"recovery-codes"</c>, <c>"remove-owner"</c>, <c>{"action":"allow","provider","subject","label"}</c>,
    /// <c>{"action":"disallow","provider","subject"}</c>, <c>{"action":"provider","provider_config":{...}}</c> or
    /// <c>{"action":"remove-provider","id"}</c>. The answer carries new recovery codes once, when the change made some.</summary>
    public async Task<HostSignInSettings> ChangeSignInSettingsAsync(JsonObject change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var body = JsonSerializer.SerializeToUtf8Bytes(change);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + SignInSettingsPath) { Content = Audio2FaceHostClient.JsonContent(body) };
            Sign(request, body);
            return await SignInSettingsAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    private async Task<HostSignInSettings> SignInSettingsAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await Audio2FaceHostClient.ReadJson(response, 64 * 1024, timeout.Token).ConfigureAwait(false);
        var root = document.RootElement;
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var code = root.TryGetProperty("code", out var value) ? value.GetString() : null;
            throw code switch
            {
                "request.invalid" when response.StatusCode == HttpStatusCode.BadRequest && request.Method == HttpMethod.Get =>
                    new Audio2FaceHostException("signin.unsupported", $"{pairing.HostId} runs an older Martlet without sign-in. Update it first."),
                "signin.invalid" => new Audio2FaceHostException(code, "The authenticator code didn't match. Check the app shows this host's entry and try the current code."),
                "signin.weak_password" => new Audio2FaceHostException(code, "Use a password of at least 12 characters."),
                "signin.denied" => new Audio2FaceHostException(code, "Only a computer in your Martlet network can change this host's sign-in."),
                "signin.unavailable" => new Audio2FaceHostException(code, $"{pairing.HostId} doesn't keep sign-in settings yet; update its host service."),
                _ => Audio2FaceHostClient.Remote(root)
            };
        }
        try
        {
            if (root.GetProperty("host_id").GetString() != pairing.HostId) throw new FormatException();
            string? Text(JsonElement element, string name) =>
                element.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? HostSignInClient.Clean(item.GetString()) : null;
            var owner = root.TryGetProperty("owner", out var o) && o.ValueKind == JsonValueKind.Object ? o : (JsonElement?)null;
            return new(pairing.HostId, owner is { } account ? Text(account, "user") : null,
                owner is { } left ? left.GetProperty("recovery_codes_left").GetInt32() : 0,
                root.GetProperty("providers").EnumerateArray().Take(16).Select(p => new HostSignInProviderSettings(Text(p, "id")!, Text(p, "kind")!,
                    Text(p, "name") ?? "", Text(p, "issuer"), Text(p, "client_id"), Text(p, "scopes"), p.GetProperty("has_client_secret").GetBoolean())).ToArray(),
                root.GetProperty("allowed").EnumerateArray().Take(64).Select(a => new HostSignInAllowed(Text(a, "provider")!, a.GetProperty("subject").GetString()!,
                    Text(a, "label"))).ToArray(),
                root.GetProperty("enrolled").EnumerateArray().Take(64).Select(e => new HostSignInEnrolled(Text(e, "device_id")!, Text(e, "provider")!,
                    e.GetProperty("subject").GetString()!, Text(e, "label"), e.GetProperty("enrolled_at").GetDateTimeOffset())).ToArray(),
                root.TryGetProperty("recovery_codes", out var codes) && codes.ValueKind == JsonValueKind.Array
                    ? codes.EnumerateArray().Select(c => c.GetString()!).ToArray() : null);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's sign-in settings were invalid.");
        }
    }
}
