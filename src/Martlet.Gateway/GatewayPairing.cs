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
    public required GatewayCredentialLifetime Lifetime { get; init; }

    public override string ToString() => nameof(IssuedDeviceCredential);
}

public sealed record GatewayDeviceRegistration
{
    public required string CredentialId { get; init; }
    public required string DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public required IReadOnlyList<GatewayRole> Roles { get; init; }
    public required DateTimeOffset IssuedAt { get; init; }
    public required GatewayCredentialLifetime Lifetime { get; init; }
    public required bool Revoked { get; init; }
    public string? RotatedToCredentialId { get; init; }
}

public sealed class GatewayCredentialStore : IGatewayRequestCredentials
{
    public static readonly TimeSpan MaximumRotationOverlap = TimeSpan.FromMinutes(10);
    public const int MaximumRegistrations = 128;
    internal static readonly TimeSpan RequestClockSkew = TimeSpan.FromMinutes(2);
    internal const int MaximumNoncesPerCredential = 1024;

    private readonly object gate = new();
    private readonly Dictionary<string, CredentialRecord> credentials = new(StringComparer.Ordinal);
    private readonly GatewayHostIdentity identity;
    private readonly TimeProvider clock;
    private readonly IGatewayCrypto crypto;
    private DateTimeOffset? lastObservedTime;
    private bool clockInvalid;
    private bool closed;
    private bool stopping;
    private readonly IGatewayPersistence? persistence;
    private long? lastTimestamp;
    private long frequency;

    public GatewayCredentialStore(
        GatewayHostIdentity identity,
        TimeProvider? clock = null,
        IGatewayCrypto? crypto = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        identity.Validate();
        this.identity = identity;
        this.clock = clock ?? TimeProvider.System;
        this.crypto = crypto ?? new SystemGatewayCrypto();
    }

    internal GatewayCredentialStore(
        GatewayHostIdentity identity, TimeProvider clock, GatewayCheckpoint checkpoint,
        IGatewayPersistence persistence, bool sameBoot = true) : this(identity, clock)
    {
        this.persistence = persistence;
        try
        {
            var now = ObserveTimeLocked(clock);
            GatewayRules.Require(now >= checkpoint.ObservedAt &&
                (!sameBoot || checkpoint.Frequency == frequency && checkpoint.Timestamp <= lastTimestamp!.Value),
                "auth.clock_invalid");
            var offlineTicks = (decimal)(now - checkpoint.ObservedAt).Ticks;
            if (sameBoot)
                offlineTicks = Math.Max(offlineTicks, decimal.Ceiling(((decimal)lastTimestamp!.Value -
                    checkpoint.Timestamp) * TimeSpan.TicksPerSecond / frequency));
            foreach (var saved in checkpoint.Credentials)
            {
                var remaining = 0m;
                if (saved.Lifetime is RetiringCredentialLifetime retiring)
                {
                    remaining = Math.Min(saved.RemainingTicks - offlineTicks, (retiring.ExpiresAt - now).Ticks);
                    if (remaining <= 0)
                        continue;
                }
                var record = new CredentialRecord
                {
                    CredentialId = saved.CredentialId,
                    DeviceId = saved.DeviceId,
                    DisplayName = saved.DisplayName,
                    Roles = saved.Roles.ToArray(),
                    Verifier = saved.SigningKey.ToArray(),
                    IssuedAt = saved.IssuedAt,
                    Lifetime = saved.Lifetime,
                    RotatedToCredentialId = saved.RotatedToCredentialId,
                    StartedAt = lastTimestamp!.Value,
                    RemainingTicks = (long)remaining
                };
                foreach (var nonce in saved.Nonces.Where(item => item.ExpiresAt > now))
                    record.Nonces.Add(nonce.Nonce, nonce.ExpiresAt);
                credentials.Add(record.CredentialId, record);
            }
        }
        catch
        {
            CloseLocked();
            throw;
        }
    }

    internal IssuedDeviceCredential Issue(string deviceId, string displayName, GatewayRole[] roles,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = IssueLocked(deviceId, displayName, roles, ObserveTimeLocked(clock));
            CommitLocked(cancellationToken);
            try { EnsureIssuedStillLiveLocked(result.CredentialId); }
            catch
            {
                CloseLocked();
                throw;
            }
            return result;
        }
    }

    public IssuedDeviceCredential Rotate(string credentialId, TimeSpan overlap) =>
        Rotate(credentialId, overlap, CancellationToken.None);

    public IssuedDeviceCredential Rotate(string credentialId, TimeSpan overlap,
        CancellationToken cancellationToken)
    {
        GatewayRules.Require(Base64Url.TryDecode(credentialId, 16, out _), "request.invalid");
        GatewayRules.Require(overlap >= TimeSpan.Zero && overlap <= MaximumRotationOverlap, "request.invalid");
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = ObserveTimeLocked(clock);
            if (!credentials.TryGetValue(credentialId, out var current) ||
                current.Revoked || ExpiredLocked(current, now))
                throw new GatewayProtocolException("auth.invalid");
            var replacement = IssueLocked(current.DeviceId, current.DisplayName, current.Roles, now);
            var remainingOverlap = current.Lifetime is RetiringCredentialLifetime
                ? Math.Min(RemainingLocked(current, now), overlap.Ticks) : overlap.Ticks;
            var retirement = now + overlap;
            if (current.Lifetime is RetiringCredentialLifetime previous)
                retirement = Min(previous.ExpiresAt, retirement);
            current.Lifetime = new RetiringCredentialLifetime { ExpiresAt = retirement };
            if (persistence is not null)
            {
                current.RemainingTicks = remainingOverlap;
                current.StartedAt = lastTimestamp!.Value;
            }
            current.RotatedToCredentialId = replacement.CredentialId;
            CommitLocked(cancellationToken);
            EnsureIssuedStillLiveLocked(replacement.CredentialId);
            cancellationToken.ThrowIfCancellationRequested();
            return replacement;
        }
    }

    public bool RevokeCredential(string credentialId) => RevokeCredential(credentialId, CancellationToken.None);

    public bool RevokeCredential(string credentialId, CancellationToken cancellationToken)
    {
        GatewayRules.Require(Base64Url.TryDecode(credentialId, 16, out _), "request.invalid");
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveTimeLocked(clock);
            if (!credentials.TryGetValue(credentialId, out var credential))
                return false;
            credential.Revoked = true;
            CryptographicOperations.ZeroMemory(credential.Verifier);
            credential.Nonces.Clear();
            CommitLocked(cancellationToken);
            if (persistence is not null)
                ObserveTimeLocked(clock);
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
    }

    public int RevokeDevice(string deviceId) => RevokeDevice(deviceId, CancellationToken.None);

    public int RevokeDevice(string deviceId, CancellationToken cancellationToken)
    {
        GatewayRules.Identifier(deviceId);
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveTimeLocked(clock);
            var count = 0;
            foreach (var credential in credentials.Values.Where(
                candidate => candidate.DeviceId == deviceId && !candidate.Revoked))
            {
                credential.Revoked = true;
                CryptographicOperations.ZeroMemory(credential.Verifier);
                credential.Nonces.Clear();
                count++;
            }
            if (count > 0)
                CommitLocked(cancellationToken);
            if (persistence is not null)
                ObserveTimeLocked(clock);
            cancellationToken.ThrowIfCancellationRequested();
            return count;
        }
    }

    public IReadOnlyList<GatewayDeviceRegistration> ListRegistrations()
    {
        lock (gate)
        {
            SweepInactiveLocked(ObserveTimeLocked(clock));
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
                    Lifetime = value.Lifetime,
                    Revoked = value.Revoked,
                    RotatedToCredentialId = value.RotatedToCredentialId
                }).ToList());
        }
    }

    GatewayPrincipal IGatewayRequestCredentials.Authenticate(GatewaySignedRequest request) =>
        Authenticate(request);

    internal GatewayPrincipal Authenticate(GatewaySignedRequest request)
    {
        lock (gate)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            var now = ObserveTimeLocked(clock);
            var found = credentials.TryGetValue(request.CredentialId, out var credential);
            SweepInactiveLocked(now);
            if (!found)
                throw new GatewayProtocolException("auth.invalid");
            if (credential!.Revoked)
                throw new GatewayProtocolException("auth.revoked");
            if (ExpiredLocked(credential, now))
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
            CommitLocked(request.CancellationToken);
            EnsureIssuedStillLiveLocked(credential.CredentialId);
            if (persistence is not null)
            {
                var afterCommit = ObserveTimeLocked(clock);
                if (request.Timestamp < afterCommit - RequestClockSkew ||
                    request.Timestamp > afterCommit + RequestClockSkew)
                    throw new GatewayProtocolException("auth.clock");
            }
            request.CancellationToken.ThrowIfCancellationRequested();

            return new()
            {
                HostId = identity.HostId,
                CredentialId = credential.CredentialId,
                DeviceId = credential.DeviceId,
                Role = request.Role,
                CredentialLifetime = credential.Lifetime
            };
        }
    }

    private IssuedDeviceCredential IssueLocked(
        string deviceId,
        string displayName,
        GatewayRole[] roles,
        DateTimeOffset now)
    {
        SweepInactiveLocked(now);
        GatewayRules.Require(credentials.Count < MaximumRegistrations, "auth.capacity");
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
        credentials.Add(credentialId!, new()
        {
            CredentialId = credentialId!,
            DeviceId = deviceId,
            DisplayName = displayName,
            Roles = roles.ToArray(),
            Verifier = verifier,
            IssuedAt = now,
            Lifetime = new PairedDeviceLifetime(),
            StartedAt = lastTimestamp ?? 0,
            RemainingTicks = 0
        });
        return new()
        {
            CredentialId = credentialId!,
            DeviceId = deviceId,
            Roles = Array.AsReadOnly(roles.ToArray()),
            Secret = new(secretText),
            Lifetime = new PairedDeviceLifetime()
        };
    }

    internal DateTimeOffset ObserveTime(TimeProvider source)
    {
        lock (gate)
            return ObserveTimeLocked(source);
    }

    private DateTimeOffset ObserveTimeLocked(TimeProvider source)
    {
        try { return ObserveTimeCoreLocked(source); }
        catch
        {
            if (!stopping)
                CloseLocked();
            throw;
        }
    }

    private DateTimeOffset ObserveTimeCoreLocked(TimeProvider source)
    {
        if (clockInvalid)
            throw new GatewayProtocolException("auth.clock_invalid");
        if (closed || stopping)
            throw new GatewayProtocolException("auth.closed");
        var now = source.GetUtcNow();
        var timestamp = persistence is null ? 0 : source.GetTimestamp();
        var currentFrequency = persistence is null ? 0 : source.TimestampFrequency;
        if (now.Offset != TimeSpan.Zero ||
            now <= DateTimeOffset.MinValue || now > DateTimeOffset.MaxValue - TimeSpan.FromDays(90) ||
            lastObservedTime is { } previous && now < previous ||
            persistence is not null && (currentFrequency <= 0 ||
                lastTimestamp is { } previousTimestamp &&
                (timestamp < previousTimestamp || frequency != currentFrequency)))
        {
            clockInvalid = true;
            CloseLocked();
            throw new GatewayProtocolException("auth.clock_invalid");
        }
        if (persistence is not null)
        {
            lastTimestamp = timestamp;
            frequency = currentFrequency;
        }
        lastObservedTime = now;
        return now;
    }

    private void SweepInactiveLocked(DateTimeOffset now)
    {
        foreach (var id in credentials.Where(item => item.Value.Revoked ||
            ExpiredLocked(item.Value, now)).Select(item => item.Key).ToArray())
        {
            var credential = credentials[id];
            CryptographicOperations.ZeroMemory(credential.Verifier);
            credential.Nonces.Clear();
            credentials.Remove(id);
        }
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;

    private long RemainingLocked(CredentialRecord record, DateTimeOffset now)
    {
        if (record.Lifetime is not RetiringCredentialLifetime retiring)
            return 0;
        var utc = (retiring.ExpiresAt - now).Ticks;
        if (persistence is null)
            return utc;
        var elapsed = ((decimal)lastTimestamp!.Value - record.StartedAt) *
            TimeSpan.TicksPerSecond / frequency;
        return (long)Math.Max(0, Math.Min(utc, decimal.Floor(record.RemainingTicks - elapsed)));
    }

    private bool ExpiredLocked(CredentialRecord record, DateTimeOffset now) =>
        record.Lifetime is RetiringCredentialLifetime retiring &&
        (retiring.ExpiresAt <= now || persistence is not null && RemainingLocked(record, now) <= 0);

    private GatewayCheckpoint CheckpointLocked(DateTimeOffset now)
    {
        SweepInactiveLocked(now);
        return new(now, credentials.Values.Select(record => new StoredGatewayCredential(
            record.CredentialId, record.DeviceId, record.DisplayName, record.Roles.ToArray(),
            record.Verifier.ToArray(), record.IssuedAt, record.Lifetime,
            RemainingLocked(record, now), record.RotatedToCredentialId,
            record.Nonces.Where(item => item.Value > now)
                .Select(item => new StoredGatewayNonce(item.Key, item.Value)).ToArray())).ToArray(),
            lastTimestamp!.Value, frequency);
    }

    private void CommitLocked(CancellationToken cancellationToken = default)
    {
        if (persistence is null)
            return;
        try
        {
            using var checkpoint = CheckpointLocked(ObserveTimeLocked(clock));
            cancellationToken.ThrowIfCancellationRequested();
            persistence.Commit(checkpoint);
        }
        catch (OperationCanceledException)
        {
            CloseLocked();
            throw;
        }
        catch (GatewayProtocolException)
        {
            CloseLocked();
            throw;
        }
        catch (Exception)
        {
            CloseLocked();
            throw new GatewayProtocolException("auth.storage");
        }
    }

    private void EnsureIssuedStillLiveLocked(string id)
    {
        if (persistence is null)
            return;
        var now = ObserveTimeLocked(clock);
        if (!credentials.TryGetValue(id, out var record) || ExpiredLocked(record, now))
            throw new GatewayProtocolException("auth.expired");
    }

    internal long? ObserveTimestamp()
    {
        lock (gate)
        {
            ObserveTimeLocked(clock);
            return lastTimestamp;
        }
    }

    internal bool BudgetExpired(long? start, TimeSpan duration)
    {
        lock (gate)
        {
            ObserveTimeLocked(clock);
            return start is { } value &&
                ((decimal)lastTimestamp!.Value - value) * TimeSpan.TicksPerSecond >=
                    (decimal)duration.Ticks * frequency;
        }
    }

    internal void StopAdmissions()
    {
        lock (gate)
            stopping = true;
    }

    internal void CommitMaintenance(Action<GatewayCheckpoint> commit)
    {
        lock (gate)
        {
            try
            {
                using var checkpoint = CheckpointLocked(ObserveTimeLocked(clock));
                commit(checkpoint);
                ObserveTimeLocked(clock);
            }
            catch
            {
                CloseLocked();
                throw;
            }
        }
    }

    internal void Complete(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            stopping = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var checkpoint = CheckpointLocked(ObserveTimeLocked(clock));
                persistence!.Complete(checkpoint, () =>
                {
                    ObserveTimeLocked(clock);
                    cancellationToken.ThrowIfCancellationRequested();
                });
            }
            finally
            {
                CloseLocked();
            }
        }
    }

    internal void Close()
    {
        lock (gate)
            CloseLocked();
    }

    private void CloseLocked()
    {
        closed = true;
        foreach (var record in credentials.Values)
        {
            CryptographicOperations.ZeroMemory(record.Verifier);
            record.Nonces.Clear();
        }
        credentials.Clear();
    }

    private sealed class CredentialRecord
    {
        internal required string CredentialId { get; init; }
        internal required string DeviceId { get; init; }
        internal required string DisplayName { get; init; }
        internal required GatewayRole[] Roles { get; init; }
        internal required byte[] Verifier { get; init; }
        internal required DateTimeOffset IssuedAt { get; init; }
        internal required GatewayCredentialLifetime Lifetime { get; set; }
        internal bool Revoked { get; set; }
        internal string? RotatedToCredentialId { get; set; }
        internal long StartedAt { get; set; }
        internal long RemainingTicks { get; set; }
        internal Dictionary<string, DateTimeOffset> Nonces { get; } = new(StringComparer.Ordinal);
    }
}

public sealed class GatewayPairingService : IGatewayPairingExchange
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
        lock (gate)
        {
            var now = credentials.ObserveTime(clock);
            foreach (var stale in windows.Where(item => item.Value.ExpiresAt <= now ||
                credentials.BudgetExpired(item.Value.StartedAt, windowLifetime))
                .Select(item => item.Key).ToArray())
                RemoveWindowLocked(stale);
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
                ExpiresAt = expiresAt,
                StartedAt = credentials.ObserveTimestamp()
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

    internal IssuedDeviceCredential Exchange(GatewayPairingProof proof,
        CancellationToken cancellationToken = default)
    {
        proof.Validate();
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = credentials.ObserveTime(clock);
            if (!windows.TryGetValue(proof.PairingId, out var window))
                throw new GatewayProtocolException("pairing.closed");
            if (window.ExpiresAt <= now || credentials.BudgetExpired(window.StartedAt, windowLifetime))
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

            var credential = credentials.Issue(window.DeviceId, window.DisplayName, window.Roles, cancellationToken);
            RemoveWindowLocked(proof.PairingId);
            var afterCommit = credentials.ObserveTime(clock);
            if (window.ExpiresAt <= afterCommit || credentials.BudgetExpired(window.StartedAt, windowLifetime))
                throw new GatewayProtocolException("pairing.expired");
            cancellationToken.ThrowIfCancellationRequested();
            return credential;
        }
    }

    IssuedDeviceCredential IGatewayPairingExchange.Exchange(GatewayPairingProof proof,
        CancellationToken cancellationToken) => Exchange(proof, cancellationToken);

    private void RemoveWindowLocked(string id)
    {
        if (windows.Remove(id, out var window))
            CryptographicOperations.ZeroMemory(window.TokenVerifier);
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
                CryptographicOperations.ZeroMemory(window.TokenVerifier);
            windows.Clear();
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
        internal long? StartedAt { get; init; }
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
    public required GatewayCredentialLifetime CredentialLifetime { get; init; }
}
