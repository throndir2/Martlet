using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Martlet.Gateway;

public sealed class GatewayPairingService : IGatewayPairingExchange
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaximumWindow = TimeSpan.FromMinutes(5);
    public const int MaximumOpenWindows = 8;
    public const int MaximumOpenCodeWindows = 2;
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
        lock (gate)
        {
            var now = credentials.ObserveTime(clock);
            RemoveStaleLocked(now);
            GatewayRules.Require(windows.Count < MaximumOpenWindows, "pairing.closed");
            var pairingId = NewPairingIdLocked();
            var tokenBytes = crypto.RandomBytes(32);
            var tokenText = Base64Url.Encode(tokenBytes);
            var expiresAt = now + windowLifetime;
            windows.Add(pairingId, new()
            {
                DeviceId = approval.DeviceId,
                DisplayName = approval.DisplayName,
                Roles = roles,
                TokenVerifier = crypto.Sha256(tokenBytes),
                ExpiresAt = expiresAt,
                StartedAt = credentials.ObserveTimestamp()
            });
            CryptographicOperations.ZeroMemory(tokenBytes);
            return new()
            {
                PairingId = pairingId,
                HostId = identity.HostId,
                Origin = origin.CanonicalOrigin,
                SpkiFingerprint = identity.SpkiFingerprint,
                Token = new(tokenText),
                ExpiresAt = expiresAt
            };
        }
    }

    /// <summary>Opens a one-use window redeemed with a short typed code (<see cref="GatewayPairingCode"/>) instead of a
    /// pasted card. Whichever desktop proves the code names itself and receives the approved roles. It has no deadline: it
    /// stays open until a desktop redeems it, <see cref="MaximumFailedAttempts"/> wrong proofs close it or the host closes
    /// pairing (the owner withdrew the code).</summary>
    public GatewayCodePairingCard OpenCodeWindow(GatewayCodePairingApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var roles = approval.ValidateAndCopy();
        lock (gate)
        {
            var now = credentials.ObserveTime(clock);
            RemoveStaleLocked(now);
            GatewayRules.Require(windows.Count < MaximumOpenWindows &&
                windows.Values.Count(window => window.Code is not null) < MaximumOpenCodeWindows, "pairing.closed");
            var code = GatewayPairingCode.Generate(crypto);
            var pairingId = NewPairingIdLocked();
            windows.Add(pairingId, new()
            {
                Code = Encoding.ASCII.GetBytes(code),
                Roles = roles,
                TokenVerifier = []
            });
            return new()
            {
                PairingId = pairingId,
                HostId = identity.HostId,
                Origin = origin.CanonicalOrigin,
                SpkiFingerprint = identity.SpkiFingerprint,
                Code = new(GatewayPairingCode.Format(code))
            };
        }
    }

    /// <summary>Whether the window <paramref name="pairingId"/> can still be redeemed: false once it was used, expired,
    /// closed by wrong proofs or withdrawn.</summary>
    public bool IsOpen(string pairingId)
    {
        lock (gate)
        {
            RemoveStaleLocked(credentials.ObserveTime(clock));
            return windows.ContainsKey(pairingId);
        }
    }

    private void RemoveStaleLocked(DateTimeOffset now)
    {
        foreach (var stale in windows.Where(item => ExpiredLocked(item.Value, now)).Select(item => item.Key).ToArray())
            RemoveWindowLocked(stale);
    }

    /// <summary>A card window ends at its deadline (wall clock or monotonic budget); a code window never expires.</summary>
    private bool ExpiredLocked(PairingWindow window, DateTimeOffset now) =>
        window.ExpiresAt is { } expiresAt && (expiresAt <= now || credentials.BudgetExpired(window.StartedAt, windowLifetime));

    private string NewPairingIdLocked()
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = Base64Url.Encode(crypto.RandomBytes(16));
            if (!windows.ContainsKey(candidate))
                return candidate;
        }
        throw new GatewayProtocolException("gateway.internal");
    }

    internal IssuedDeviceCredential Exchange(GatewayPairingProof proof,
        CancellationToken cancellationToken = default)
    {
        proof.Validate();
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = credentials.ObserveTime(clock);
            if (!windows.TryGetValue(proof.PairingId, out var window) || window.Code is not null)
                throw new GatewayProtocolException("pairing.closed");
            if (ExpiredLocked(window, now))
            {
                RemoveWindowLocked(proof.PairingId);
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
                    RemoveWindowLocked(proof.PairingId);
                throw new GatewayProtocolException(
                    proof.SpkiFingerprint != identity.SpkiFingerprint ? "host.pin_mismatch" : "pairing.invalid");
            }

            // Check the deadline before the credential is issued, so an expired window never leaves an unused pairing.
            if (ExpiredLocked(window, credentials.ObserveTime(clock)))
            {
                RemoveWindowLocked(proof.PairingId);
                throw new GatewayProtocolException("pairing.expired");
            }
            var credential = credentials.Issue(window.DeviceId!, window.DisplayName!, window.Roles, cancellationToken);
            RemoveWindowLocked(proof.PairingId);
            cancellationToken.ThrowIfCancellationRequested();
            return credential;
        }
    }

    IssuedDeviceCredential IGatewayPairingExchange.Exchange(GatewayPairingProof proof,
        CancellationToken cancellationToken) => Exchange(proof, cancellationToken);

    /// <summary>Redeems a short-code window: the desktop's proof must match one open code under this host's own key
    /// fingerprint. A miss counts against every open code window (each closes after
    /// <see cref="MaximumFailedAttempts"/>), so guessing stays bounded.</summary>
    internal GatewayCodePairingResult Exchange(GatewayCodePairingProof proof, CancellationToken cancellationToken = default)
    {
        proof.Validate();
        Base64Url.TryDecode(proof.ClientNonce, 16, out var nonce);
        Base64Url.TryDecode(proof.Proof, 32, out var presented);
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            credentials.ObserveTime(clock);
            var open = windows.Where(item => item.Value.Code is not null).ToArray();
            if (open.Length == 0)
                throw new GatewayProtocolException("pairing.closed");

            string? matchedId = null;
            PairingWindow? matched = null;
            byte[]? key = null;
            foreach (var (id, window) in open)
            {
                var candidate = GatewayPairingCode.DeriveKey(Encoding.ASCII.GetString(window.Code!), identity.SpkiFingerprint, nonce);
                var expected = GatewayPairingCode.ClientProof(candidate, proof.DeviceId, proof.DisplayName, nonce);
                if (matched is null && crypto.FixedTimeEquals(expected, presented))
                {
                    (matchedId, matched, key) = (id, window, candidate);
                    continue;
                }
                CryptographicOperations.ZeroMemory(candidate);
            }
            CryptographicOperations.ZeroMemory(presented);
            if (matched is null)
            {
                foreach (var (id, window) in open)
                    if (++window.FailedAttempts >= MaximumFailedAttempts)
                        RemoveWindowLocked(id);
                throw new GatewayProtocolException("pairing.invalid");
            }

            try
            {
                var credential = credentials.Issue(proof.DeviceId, proof.DisplayName, matched.Roles, cancellationToken);
                RemoveWindowLocked(matchedId!);
                cancellationToken.ThrowIfCancellationRequested();
                var hostProof = GatewayPairingCode.HostProof(key!, identity.HostId, credential.DeviceId, credential.CredentialId,
                    credential.Secret.Reveal(), nonce);
                return new(credential, Base64Url.Encode(hostProof));
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
    }

    GatewayCodePairingResult IGatewayPairingExchange.Exchange(GatewayCodePairingProof proof,
        CancellationToken cancellationToken) => Exchange(proof, cancellationToken);

    private void RemoveWindowLocked(string id)
    {
        if (windows.Remove(id, out var window))
        {
            CryptographicOperations.ZeroMemory(window.TokenVerifier);
            if (window.Code is { } code) CryptographicOperations.ZeroMemory(code);
        }
    }

    internal int RevokeDevice(string deviceId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GatewayRules.Identifier(deviceId);
            foreach (var id in windows.Where(item => item.Value.DeviceId == deviceId).Select(item => item.Key).ToArray())
                RemoveWindowLocked(id);
            return credentials.RevokeDevice(deviceId, cancellationToken);
        }
    }

    internal void Close()
    {
        lock (gate)
        {
            foreach (var window in windows.Values)
            {
                CryptographicOperations.ZeroMemory(window.TokenVerifier);
                if (window.Code is { } code) CryptographicOperations.ZeroMemory(code);
            }
            windows.Clear();
        }
    }

    /// <summary>A card window names its device (<see cref="DeviceId"/>, <see cref="DisplayName"/>), keeps the token's
    /// verifier and ends at <see cref="ExpiresAt"/>; a short-code window keeps the code itself (<see cref="Code"/>, ASCII),
    /// lets the desktop name itself and has no deadline.</summary>
    private sealed class PairingWindow
    {
        internal string? DeviceId { get; init; }
        internal string? DisplayName { get; init; }
        internal byte[]? Code { get; init; }
        internal required GatewayRole[] Roles { get; init; }
        internal required byte[] TokenVerifier { get; init; }
        internal DateTimeOffset? ExpiresAt { get; init; }
        internal int FailedAttempts { get; set; }
        internal long? StartedAt { get; init; }
    }
}
