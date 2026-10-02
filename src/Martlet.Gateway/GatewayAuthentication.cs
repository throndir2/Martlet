using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace Martlet.Gateway;

public sealed class GatewayRequestSigner : IDisposable
{
    internal const string AuthorizationScheme = "Martlet-HMAC";
    internal const string NonceHeader = "X-Martlet-Nonce";
    internal const string TimestampHeader = "X-Martlet-Timestamp";
    internal const string RoleHeader = "X-Martlet-Role";
    private static readonly byte[] EmptyBodyHash = System.Security.Cryptography.SHA256.HashData([]);

    private readonly GatewayHostIdentity identity;
    private readonly string credentialId;
    private readonly byte[] verifier;
    private readonly TimeProvider clock;
    private readonly IGatewayCrypto crypto;
    private readonly object gate = new();
    private bool disposed;

    public GatewayRequestSigner(
        GatewayHostIdentity identity,
        IssuedDeviceCredential credential,
        TimeProvider? clock = null,
        IGatewayCrypto? crypto = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(credential);
        identity.Validate();
        GatewayRules.Require(credential.Lifetime is PairedDeviceLifetime, "protocol.unsupported");
        GatewayRules.Require(Base64Url.TryDecode(credential.CredentialId, 16, out _), "auth.invalid");
        this.crypto = crypto ?? new SystemGatewayCrypto();
        GatewayRules.Require(Base64Url.TryDecode(credential.Secret.Reveal(), 32, out var secret), "auth.invalid");
        verifier = this.crypto.Sha256(secret);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
        this.identity = identity;
        credentialId = credential.CredentialId;
        this.clock = clock ?? TimeProvider.System;
    }

    internal GatewayRequestSigner(GatewayHostIdentity identity, string credentialId,
        ReadOnlySpan<char> secret, TimeProvider? clock)
    {
        identity.Validate();
        GatewayRules.Require(Base64Url.TryDecode(credentialId, 16, out _), "auth.invalid");
        crypto = new SystemGatewayCrypto();
        var valid = Base64Url.TryDecode(new string(secret), 32, out var bytes);
        try
        {
            GatewayRules.Require(valid, "auth.invalid");
            verifier = crypto.Sha256(bytes);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        this.identity = identity;
        this.credentialId = credentialId;
        this.clock = clock ?? TimeProvider.System;
    }

    public void Sign(HttpRequestMessage request, GatewayRole role)
    {
        ArgumentNullException.ThrowIfNull(request);
        GatewayRules.Require(request.Content is null, "request.invalid");
        SignCore(request, role, EmptyBodyHash);
    }

    public void Sign(HttpRequestMessage request, GatewayRole role, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(request);
        GatewayRules.Require(request.Method == HttpMethod.Post && request.Content is not null &&
            body.Length is > 0 and <= GatewayInferenceProtocol.MaximumRequestBytes, "request.invalid");
        SignCore(request, role, System.Security.Cryptography.SHA256.HashData(body));
    }

    private void SignCore(HttpRequestMessage request, GatewayRole role, ReadOnlySpan<byte> bodyHash)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            GatewayRules.Defined(role);
            GatewayRules.Require(request.RequestUri is { IsAbsoluteUri: true } &&
                request.Headers.Authorization is null &&
                !request.Headers.Contains(NonceHeader) &&
                !request.Headers.Contains(TimestampHeader) &&
                !request.Headers.Contains(RoleHeader), "request.invalid");
            var nonce = Base64Url.Encode(crypto.RandomBytes(24));
            var timestamp = clock.GetUtcNow().ToUnixTimeSeconds();
            var roleText = RoleText(role);
            var canonical = Canonical(
                identity.HostId,
                credentialId,
                request.Method.Method,
                request.RequestUri!.PathAndQuery,
                roleText,
                timestamp,
                nonce,
                bodyHash);
            var signature = Base64Url.Encode(crypto.HmacSha256(verifier, canonical));
            request.Headers.TryAddWithoutValidation("Authorization",
                $"{AuthorizationScheme} {credentialId}.{signature}");
            request.Headers.TryAddWithoutValidation(NonceHeader, nonce);
            request.Headers.TryAddWithoutValidation(TimestampHeader,
                timestamp.ToString(CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation(RoleHeader, roleText);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(verifier);
        }
    }

    internal static byte[] Canonical(
        string hostId,
        string credentialId,
        string method,
        string rawTarget,
        string role,
        long timestamp,
        string nonce,
        ReadOnlySpan<byte> bodyHash)
    {
        GatewayRules.Require(method is "GET" or "POST" or "DELETE" &&
            rawTarget is { Length: > 0 and <= 256 } &&
            rawTarget[0] == '/' &&
            rawTarget.All(char.IsAscii) &&
            role is "voice" or "perception" or "memory", "request.invalid");
        var hash = Convert.ToHexStringLower(bodyHash);
        return Encoding.ASCII.GetBytes(string.Join('\n',
            "martlet-request-v1",
            hostId,
            credentialId,
            method,
            rawTarget,
            role,
            timestamp.ToString(CultureInfo.InvariantCulture),
            nonce,
            hash));
    }

    internal static string RoleText(GatewayRole role) => role switch
    {
        GatewayRole.Voice => "voice",
        GatewayRole.Perception => "perception",
        GatewayRole.Memory => "memory",
        _ => throw new GatewayProtocolException("request.invalid")
    };

    internal static GatewayRole ParseRole(string value) => value switch
    {
        "voice" => GatewayRole.Voice,
        "perception" => GatewayRole.Perception,
        "memory" => GatewayRole.Memory,
        _ => throw new GatewayProtocolException("request.invalid")
    };
}

internal sealed record GatewaySignedRequest
{
    internal required string CredentialId { get; init; }
    internal required byte[] Signature { get; init; }
    internal required string Nonce { get; init; }
    internal required DateTimeOffset Timestamp { get; init; }
    internal required GatewayRole Role { get; init; }
    internal required byte[] CanonicalBytes { get; init; }
    internal CancellationToken CancellationToken { get; init; }
}

internal sealed class GatewayRequestAuthenticator(
    GatewayHostIdentity identity,
    IGatewayRequestCredentials credentials)
{
    private static readonly byte[] EmptyBodyHash = System.Security.Cryptography.SHA256.HashData([]);

    internal GatewayPrincipal Authenticate(HttpRequest request)
        => Authenticate(request, EmptyBodyHash);

    internal GatewayPrincipal Authenticate(HttpRequest request, ReadOnlySpan<byte> bodyHash)
    {
        // Endpoints that admit API keys route them to GatewayApiKeyStore first; the rest are for paired devices only.
        if (GatewayApiKeyStore.IsBearer(request)) throw new GatewayProtocolException("key.scope");
        var authorization = SingleHeader(request, "Authorization", 160, "auth.missing");
        var nonce = SingleHeader(request, GatewayRequestSigner.NonceHeader, 32, "auth.missing");
        var timestampText = SingleHeader(request, GatewayRequestSigner.TimestampHeader, 20, "auth.missing");
        var roleText = SingleHeader(request, GatewayRequestSigner.RoleHeader, 10, "auth.missing");
        if (!authorization.StartsWith(GatewayRequestSigner.AuthorizationScheme + " ", StringComparison.Ordinal))
            throw new GatewayProtocolException("auth.invalid");
        var parts = authorization[(GatewayRequestSigner.AuthorizationScheme.Length + 1)..].Split('.');
        if (parts.Length != 2 ||
            !Base64Url.TryDecode(parts[0], 16, out _) ||
            !Base64Url.TryDecode(parts[1], 32, out var signature) ||
            !Base64Url.TryDecode(nonce, 24, out _) ||
            !long.TryParse(timestampText, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp) ||
            timestamp <= 0 ||
            timestampText != timestamp.ToString(CultureInfo.InvariantCulture))
            throw new GatewayProtocolException("auth.invalid");
        DateTimeOffset timestampValue;
        try
        {
            timestampValue = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new GatewayProtocolException("auth.invalid");
        }

        var role = GatewayRequestSigner.ParseRole(roleText);
        var rawTarget = request.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget;
        GatewayRules.Require(rawTarget is not null, "request.invalid");
        var canonical = GatewayRequestSigner.Canonical(
            identity.HostId,
            parts[0],
            request.Method,
            rawTarget!,
            roleText,
            timestamp,
            nonce,
            bodyHash);
        return credentials.Authenticate(new()
        {
            CredentialId = parts[0],
            Signature = signature,
            Nonce = nonce,
            Timestamp = timestampValue,
            Role = role,
            CanonicalBytes = canonical,
            CancellationToken = request.HttpContext.RequestAborted
        });
    }

    private static string SingleHeader(HttpRequest request, string name, int maximum, string missingCode)
    {
        if (!request.Headers.TryGetValue(name, out StringValues values) ||
            values.Count != 1 ||
            values[0] is not { Length: > 0 } value)
            throw new GatewayProtocolException(missingCode);
        GatewayRules.Require(value.Length <= maximum && value.All(char.IsAscii), "auth.invalid");
        return value;
    }
}
