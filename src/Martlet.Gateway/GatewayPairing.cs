using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Martlet.Gateway;

public sealed class GatewaySecret
{
    private readonly string value;

    internal GatewaySecret(string value) => this.value = value;

    public string Reveal() => value;

    public override string ToString() => nameof(GatewaySecret);
}

public sealed record GatewayPairingApproval
{
    public required string DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public required IReadOnlyList<GatewayRole> Roles { get; init; }

    internal GatewayRole[] ValidateAndCopy()
    {
        GatewayRules.Identifier(DeviceId);
        GatewayRules.Token(DisplayName, 64);
        GatewayRules.Require(Roles is { Count: > 0 and <= 3 }, "request.invalid");
        var roles = Roles.ToArray();
        foreach (var role in roles)
            GatewayRules.Defined(role);
        GatewayRules.Require(roles.Distinct().Count() == roles.Length, "request.invalid");
        Array.Sort(roles);
        return roles;
    }
}

public sealed record GatewayPairingCard
{
    public required string PairingId { get; init; }
    public required string HostId { get; init; }
    public required string Origin { get; init; }
    public required string SpkiFingerprint { get; init; }
    public required GatewaySecret Token { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }

    public override string ToString() => nameof(GatewayPairingCard);
}

public sealed record IssuedDeviceCredential
{
    public required string CredentialId { get; init; }
    public required string DeviceId { get; init; }
    public required IReadOnlyList<GatewayRole> Roles { get; init; }
    public required GatewaySecret Secret { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }

    public override string ToString() => nameof(IssuedDeviceCredential);
}

public sealed record GatewayDeviceRegistration
{
    public required string CredentialId { get; init; }
    public required string DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public required IReadOnlyList<GatewayRole> Roles { get; init; }
    public required DateTimeOffset IssuedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required bool Revoked { get; init; }
    public string? RotatedToCredentialId { get; init; }
}

public sealed class GatewayCredentialStore
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(90);
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(90);
    public static readonly TimeSpan MaximumRotationOverlap = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan RequestClockSkew = TimeSpan.FromMinutes(2);
    internal const int MaximumNoncesPerCredential = 1024;

    private readonly object gate = new();
    private readonly Dictionary<string, CredentialRecord> credentials = new(StringComparer.Ordinal);
    private readonly GatewayHostIdentity identity;
    private readonly TimeProvider clock;
    private readonly IGatewayCrypto crypto;
    private readonly TimeSpan lifetime;

    public GatewayCredentialStore(
        GatewayHostIdentity identity,
        TimeProvider? clock = null,
        IGatewayCrypto? crypto = null,
        TimeSpan? credentialLifetime = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        identity.Validate();
        this.identity = identity;
        this.clock = clock ?? TimeProvider.System;
        this.crypto = crypto ?? new SystemGatewayCrypto();
        lifetime = credentialLifetime ?? DefaultLifetime;
        GatewayRules.Require(lifetime > TimeSpan.Zero && lifetime <= MaximumLifetime, "request.invalid");
    }

    internal IssuedDeviceCredential Issue(string deviceId, string displayName, GatewayRole[] roles)
    {
        var now = clock.GetUtcNow();
        lock (gate)
            return IssueLocked(deviceId, displayName, roles, now);
    }

    public IssuedDeviceCredential Rotate(string credentialId, TimeSpan overlap)
    {
        GatewayRules.Require(Base64Url.TryDecode(credentialId, 16, out _), "request.invalid");
        GatewayRules.Require(overlap >= TimeSpan.Zero && overlap <= MaximumRotationOverlap, "request.invalid");
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (!credentials.TryGetValue(credentialId, out var current) ||
                current.Revoked || current.ExpiresAt <= now)
                throw new GatewayProtocolException("auth.invalid");
            var replacement = IssueLocked(current.DeviceId, current.DisplayName, current.Roles, now);
            current.ExpiresAt = Min(current.ExpiresAt, now + overlap);
            current.RotatedToCredentialId = replacement.CredentialId;
            return replacement;
        }
    }

    public bool RevokeCredential(string credentialId)
    {
        GatewayRules.Require(Base64Url.TryDecode(credentialId, 16, out _), "request.invalid");
        lock (gate)
        {
            if (!credentials.TryGetValue(credentialId, out var credential))
                return false;
            credential.Revoked = true;
            credential.Nonces.Clear();
            return true;
        }
    }

    public int RevokeDevice(string deviceId)
    {
        GatewayRules.Identifier(deviceId);
        lock (gate)
        {
            var count = 0;
            foreach (var credential in credentials.Values.Where(
                candidate => candidate.DeviceId == deviceId && !candidate.Revoked))
            {
                credential.Revoked = true;
                credential.Nonces.Clear();
                count++;
            }
            return count;
        }
    }

    public IReadOnlyList<GatewayDeviceRegistration> ListRegistrations()
    {
        lock (gate)
        {
            return new ReadOnlyCollection<GatewayDeviceRegistration>(credentials.Values
                .OrderBy(value => value.DeviceId, StringComparer.Ordinal)
                .ThenBy(value => value.CredentialId, StringComparer.Ordinal)
                .Select(value => new GatewayDeviceRegistration
                {
                    CredentialId = value.CredentialId,
                    DeviceId = value.DeviceId,
                    DisplayName = value.DisplayName,
                    Roles = Array.AsReadOnly(value.Roles.ToArray()),
                    IssuedAt = value.IssuedAt,
                    ExpiresAt = value.ExpiresAt,
                    Revoked = value.Revoked,
                    RotatedToCredentialId = value.RotatedToCredentialId
                }).ToList());
        }
    }

    internal GatewayPrincipal Authenticate(GatewaySignedRequest request)
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (!credentials.TryGetValue(request.CredentialId, out var credential))
                throw new GatewayProtocolException("auth.invalid");
            if (credential.Revoked)
                throw new GatewayProtocolException("auth.revoked");
            if (credential.ExpiresAt <= now)
                throw new GatewayProtocolException("auth.expired");

            var expected = crypto.HmacSha256(credential.Verifier, request.CanonicalBytes);
            if (!crypto.FixedTimeEquals(expected, request.Signature))
                throw new GatewayProtocolException("auth.invalid");
            if (request.Timestamp < now - RequestClockSkew ||
                request.Timestamp > now + RequestClockSkew)
                throw new GatewayProtocolException("auth.clock");
            if (!credential.Roles.Contains(request.Role))
                throw new GatewayProtocolException("auth.role");

            foreach (var stale in credential.Nonces
                .Where(item => item.Value <= now).Select(item => item.Key).ToArray())
                credential.Nonces.Remove(stale);
            if (credential.Nonces.ContainsKey(request.Nonce))
                throw new GatewayProtocolException("auth.replay");
            if (credential.Nonces.Count >= MaximumNoncesPerCredential)
                throw new GatewayProtocolException("auth.rate");
            credential.Nonces.Add(request.Nonce,
                now + RequestClockSkew + RequestClockSkew + TimeSpan.FromTicks(1));

            return new()
            {
                HostId = identity.HostId,
                CredentialId = credential.CredentialId,
                DeviceId = credential.DeviceId,
                Role = request.Role,
                CredentialExpiresAt = credential.ExpiresAt
            };
        }
    }

    private IssuedDeviceCredential IssueLocked(
        string deviceId,
        string displayName,
        GatewayRole[] roles,
        DateTimeOffset now)
    {
        string? credentialId = null;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = Base64Url.Encode(crypto.RandomBytes(16));
            if (!credentials.ContainsKey(candidate))
            {
                credentialId = candidate;
                break;
            }
        }
        GatewayRules.Require(credentialId is not null, "gateway.internal");

        var secretBytes = crypto.RandomBytes(32);
        var secretText = Base64Url.Encode(secretBytes);
        var verifier = crypto.Sha256(secretBytes);
        CryptographicOperations.ZeroMemory(secretBytes);
        var expiresAt = now + lifetime;
        credentials.Add(credentialId!, new()
        {
            CredentialId = credentialId!,
            DeviceId = deviceId,
            DisplayName = displayName,
            Roles = roles.ToArray(),
            Verifier = verifier,
            IssuedAt = now,
            ExpiresAt = expiresAt
        });
        return new()
        {
            CredentialId = credentialId!,
            DeviceId = deviceId,
            Roles = Array.AsReadOnly(roles.ToArray()),
            Secret = new(secretText),
            ExpiresAt = expiresAt
        };
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;

    private sealed class CredentialRecord
    {
        internal required string CredentialId { get; init; }
        internal required string DeviceId { get; init; }
        internal required string DisplayName { get; init; }
        internal required GatewayRole[] Roles { get; init; }
        internal required byte[] Verifier { get; init; }
        internal required DateTimeOffset IssuedAt { get; init; }
        internal required DateTimeOffset ExpiresAt { get; set; }
        internal bool Revoked { get; set; }
        internal string? RotatedToCredentialId { get; set; }
        internal Dictionary<string, DateTimeOffset> Nonces { get; } = new(StringComparer.Ordinal);
    }
}

public sealed class GatewayPairingService
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaximumWindow = TimeSpan.FromMinutes(5);
    public const int MaximumOpenWindows = 8;
    public const int MaximumFailedAttempts = 5;

    private readonly object gate = new();
    private readonly Dictionary<string, PairingWindow> windows = new(StringComparer.Ordinal);
    private readonly GatewayHostIdentity identity;
    private readonly GatewayOrigin origin;
    private readonly GatewayCredentialStore credentials;
    private readonly TimeProvider clock;
    private readonly IGatewayCrypto crypto;
    private readonly TimeSpan windowLifetime;

    public GatewayPairingService(
        GatewayHostIdentity identity,
        GatewayOrigin origin,
        GatewayCredentialStore credentials,
        TimeProvider? clock = null,
        IGatewayCrypto? crypto = null,
        TimeSpan? windowLifetime = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(credentials);
        identity.Validate();
        this.identity = identity;
        this.origin = origin;
        this.credentials = credentials;
        this.clock = clock ?? TimeProvider.System;
        this.crypto = crypto ?? new SystemGatewayCrypto();
        this.windowLifetime = windowLifetime ?? DefaultWindow;
        GatewayRules.Require(this.windowLifetime > TimeSpan.Zero &&
            this.windowLifetime <= MaximumWindow, "request.invalid");
    }

    public GatewayPairingCard OpenWindow(GatewayPairingApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var roles = approval.ValidateAndCopy();
        var now = clock.GetUtcNow();
        lock (gate)
        {
            foreach (var stale in windows.Where(item => item.Value.ExpiresAt <= now)
                .Select(item => item.Key).ToArray())
                windows.Remove(stale);
            GatewayRules.Require(windows.Count < MaximumOpenWindows, "pairing.closed");

            string? pairingId = null;
            for (var attempt = 0; attempt < 16; attempt++)
            {
                var candidate = Base64Url.Encode(crypto.RandomBytes(16));
                if (!windows.ContainsKey(candidate))
                {
                    pairingId = candidate;
                    break;
                }
            }
            GatewayRules.Require(pairingId is not null, "gateway.internal");
            var tokenBytes = crypto.RandomBytes(32);
            var tokenText = Base64Url.Encode(tokenBytes);
            var expiresAt = now + windowLifetime;
            windows.Add(pairingId!, new()
            {
                DeviceId = approval.DeviceId,
                DisplayName = approval.DisplayName,
                Roles = roles,
                TokenVerifier = crypto.Sha256(tokenBytes),
                ExpiresAt = expiresAt
            });
            CryptographicOperations.ZeroMemory(tokenBytes);
            return new()
            {
                PairingId = pairingId!,
                HostId = identity.HostId,
                Origin = origin.CanonicalOrigin,
                SpkiFingerprint = identity.SpkiFingerprint,
                Token = new(tokenText),
                ExpiresAt = expiresAt
            };
        }
    }

    internal IssuedDeviceCredential Exchange(GatewayPairingProof proof)
    {
        proof.Validate();
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (!windows.TryGetValue(proof.PairingId, out var window))
                throw new GatewayProtocolException("pairing.closed");
            if (window.ExpiresAt <= now)
            {
                windows.Remove(proof.PairingId);
                throw new GatewayProtocolException("pairing.expired");
            }

            var validToken = Base64Url.TryDecode(proof.PairingToken, 32, out var tokenBytes) &&
                crypto.FixedTimeEquals(crypto.Sha256(tokenBytes), window.TokenVerifier);
            CryptographicOperations.ZeroMemory(tokenBytes);
            var matches = validToken &&
                string.Equals(proof.HostId, identity.HostId, StringComparison.Ordinal) &&
                string.Equals(proof.SpkiFingerprint, identity.SpkiFingerprint, StringComparison.Ordinal) &&
                string.Equals(proof.DeviceId, window.DeviceId, StringComparison.Ordinal);
            if (!matches)
            {
                window.FailedAttempts++;
                if (window.FailedAttempts >= MaximumFailedAttempts)
                    windows.Remove(proof.PairingId);
                throw new GatewayProtocolException(
                    proof.SpkiFingerprint != identity.SpkiFingerprint ? "host.pin_mismatch" : "pairing.invalid");
            }

            windows.Remove(proof.PairingId);
            return credentials.Issue(window.DeviceId, window.DisplayName, window.Roles);
        }
    }

    private sealed class PairingWindow
    {
        internal required string DeviceId { get; init; }
        internal required string DisplayName { get; init; }
        internal required GatewayRole[] Roles { get; init; }
        internal required byte[] TokenVerifier { get; init; }
        internal required DateTimeOffset ExpiresAt { get; init; }
        internal int FailedAttempts { get; set; }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GatewayPairingProof
{
    public required GatewayProtocolVersion ProtocolVersion { get; init; }
    public required string PairingId { get; init; }
    public required string PairingToken { get; init; }
    public required string HostId { get; init; }
    public required string SpkiFingerprint { get; init; }
    public required string DeviceId { get; init; }

    internal void Validate()
    {
        GatewayRules.Require(ProtocolVersion is not null, "request.invalid");
        ProtocolVersion!.Validate();
        GatewayRules.Require(Base64Url.TryDecode(PairingId, 16, out _), "request.invalid");
        GatewayRules.Require(PairingToken is { Length: 43 }, "request.invalid");
        GatewayRules.Identifier(HostId);
        GatewayRules.Require(GatewayHostIdentity.IsFingerprint(SpkiFingerprint), "request.invalid");
        GatewayRules.Identifier(DeviceId);
    }
}

public sealed record GatewayPrincipal
{
    public required string HostId { get; init; }
    public required string CredentialId { get; init; }
    public required string DeviceId { get; init; }
    public required GatewayRole Role { get; init; }
    public required DateTimeOffset CredentialExpiresAt { get; init; }
}
