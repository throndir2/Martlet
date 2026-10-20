using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Martlet.Gateway;

/// <summary>How long saves of the credential authority took since the gateway started (kept in memory only).</summary>
public sealed record GatewayAuthorityCommitStats(long Commits, long SlowCommits, TimeSpan Last, TimeSpan Longest, TimeSpan Total)
{
    public TimeSpan Average => Commits == 0 ? TimeSpan.Zero : Total / Commits;
}

public sealed class GatewayCredentialStore : IGatewayRequestCredentials, IGatewayAdmissionStatus, IGatewayPrincipalAuthority
{
    public static readonly TimeSpan MaximumRotationOverlap = TimeSpan.FromMinutes(10);
    public const int MaximumRegistrations = 128;
    internal static readonly TimeSpan RequestClockSkew = TimeSpan.FromMinutes(2);
    /// <summary>How far the wall clock may step back (Windows' NTP sync steps it back about a second; a WSL2 or Docker VM
    /// follows its host) while the store holds its observed high-water mark instead of closing. Kept well below
    /// <see cref="RequestClockSkew"/> so held time still admits correctly signed requests.</summary>
    internal static readonly TimeSpan MaximumClockStepBack = TimeSpan.FromSeconds(30);
    internal const int MaximumNoncesPerCredential = 1024;

    /// <summary>A commit slower than this is written to the host log.</summary>
    internal static readonly TimeSpan SlowCommit = TimeSpan.FromMilliseconds(50);

    // Lock order: commitGate, then gate. A request's nonce commit holds only commitGate while it writes, so other
    // requests check their signatures meanwhile and one later commit saves all their nonces together.
    private readonly object commitGate = new();
    private readonly object gate = new();
    private readonly object statsGate = new();
    private readonly Dictionary<string, CredentialRecord> credentials = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> lastSeen = new(StringComparer.Ordinal);
    private long changeSequence;
    private long committedSequence;
    private GatewayAuthorityCommitStats commitStats = new(0, 0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
    private GatewayAuthorityCommitStats nonceStats = new(0, 0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
    // Nonces checked since the last save, for the next nonce append. A full save holds them all and clears this.
    private readonly List<StoredGatewayNonceRecord> unsavedNonces = [];
    private DateTimeOffset? replayFloor;
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
            // The nonce journal may have seen later times than the saved checkpoint; the newest is the high-water mark.
            var highWater = checkpoint.JournalObservedAt is { } journal && journal > checkpoint.ObservedAt
                ? journal : checkpoint.ObservedAt;
            GatewayRules.Require(highWater - now <= MaximumClockStepBack &&
                (!sameBoot || checkpoint.Frequency == frequency && checkpoint.Timestamp <= lastTimestamp!.Value),
                "auth.clock_invalid");
            if (now < highWater)
                lastObservedTime = now = highWater;
            replayFloor = checkpoint.ReplayFloor;
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
                    RemainingTicks = (long)remaining,
                    Access = saved.Access ?? GatewayAccess.Full
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
        CancellationToken cancellationToken = default, GatewayAccess access = GatewayAccess.Full)
    {
        lock (commitGate)
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = IssueLocked(deviceId, displayName, roles, ObserveTimeLocked(clock), access);
            CommitLocked();
            try { EnsureIssuedStillLiveLocked(result.CredentialId); }
            catch
            {
                CloseLocked();
                throw;
            }
            return result;
        }
    }

    /// <summary>Issues a new credential to <paramref name="deviceId"/> and revokes its older ones in the same commit. When
    /// the issue fails (capacity, storage), the older credentials stay as they were.</summary>
    internal IssuedDeviceCredential IssueReplacing(string deviceId, string displayName, GatewayRole[] roles,
        CancellationToken cancellationToken = default, GatewayAccess access = GatewayAccess.Full)
    {
        GatewayRules.Identifier(deviceId);
        lock (commitGate)
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = ObserveTimeLocked(clock);
            SweepInactiveLocked(now);
            var older = credentials.Values.Where(candidate => candidate.DeviceId == deviceId && !candidate.Revoked).ToArray();
            var result = IssueLocked(deviceId, displayName, roles, now, access, releasing: older.Length);
            foreach (var credential in older)
            {
                credential.Revoked = true;
                CryptographicOperations.ZeroMemory(credential.Verifier);
                credential.Nonces.Clear();
            }
            CommitLocked();
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
        lock (commitGate)
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = ObserveTimeLocked(clock);
            if (!credentials.TryGetValue(credentialId, out var current) ||
                current.Revoked || ExpiredLocked(current, now))
                throw new GatewayProtocolException("auth.invalid");
            var replacement = IssueLocked(current.DeviceId, current.DisplayName, current.Roles, now, current.Access);
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
            CommitLocked();
            EnsureIssuedStillLiveLocked(replacement.CredentialId);
            cancellationToken.ThrowIfCancellationRequested();
            return replacement;
        }
    }

    public bool RevokeCredential(string credentialId) => RevokeCredential(credentialId, CancellationToken.None);

    public bool RevokeCredential(string credentialId, CancellationToken cancellationToken)
    {
        GatewayRules.Require(Base64Url.TryDecode(credentialId, 16, out _), "request.invalid");
        lock (commitGate)
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveTimeLocked(clock);
            if (!credentials.TryGetValue(credentialId, out var credential))
                return false;
            credential.Revoked = true;
            CryptographicOperations.ZeroMemory(credential.Verifier);
            credential.Nonces.Clear();
            CommitLocked();
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
        lock (commitGate)
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
                CommitLocked();
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
                    RotatedToCredentialId = value.RotatedToCredentialId,
                    Access = value.Access
                }).ToList());
        }
    }

    /// <summary>The computers with a live pairing here, one entry per device (its newest credential), with when each last
    /// made a signed request since the gateway started. Nonsecret.</summary>
    public IReadOnlyList<GatewayPairedDevice> PairedDevices()
    {
        lock (gate)
        {
            SweepInactiveLocked(ObserveTimeLocked(clock));
            foreach (var gone in lastSeen.Keys.Where(id => credentials.Values.All(c => c.DeviceId != id)).ToArray()) lastSeen.Remove(gone);
            return credentials.Values
                .GroupBy(value => value.DeviceId, StringComparer.Ordinal)
                .Select(group =>
                {
                    var newest = group.OrderByDescending(value => value.IssuedAt).First();
                    return new GatewayPairedDevice(group.Key, newest.DisplayName, newest.IssuedAt,
                        lastSeen.TryGetValue(group.Key, out var seen) ? seen : null, newest.Access);
                })
                .OrderBy(device => device.DeviceId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    GatewayPrincipal IGatewayRequestCredentials.Authenticate(GatewaySignedRequest request) =>
        Authenticate(request);

    /// <summary>Checks a signed request and records its nonce. With durable storage the nonce is saved (one append to the
    /// nonce journal) before the request is admitted, but outside the main lock: requests that arrive while one save writes
    /// share the next one. A cancellation never closes the authority; only a storage failure does.</summary>
    internal GatewayPrincipal Authenticate(GatewaySignedRequest request)
    {
        CredentialRecord credential;
        long ticket;
        lock (gate)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            credential = CheckSignedLocked(request);
            ticket = ++changeSequence;
        }
        if (persistence is not null)
            PersistThrough(ticket);
        lock (gate)
        {
            var now = ObserveTimeLocked(clock);
            if (!credentials.TryGetValue(credential.CredentialId, out var live) || !ReferenceEquals(live, credential))
                throw new GatewayProtocolException("auth.invalid");
            if (credential.Revoked)
                throw new GatewayProtocolException("auth.revoked");
            if (ExpiredLocked(credential, now))
                throw new GatewayProtocolException("auth.expired");
            if (request.Timestamp < now - RequestClockSkew || request.Timestamp > now + RequestClockSkew)
                throw new GatewayProtocolException("auth.clock");
            request.CancellationToken.ThrowIfCancellationRequested();
            lastSeen[credential.DeviceId] = now;

            return new()
            {
                HostId = identity.HostId,
                CredentialId = credential.CredentialId,
                DeviceId = credential.DeviceId,
                Role = request.Role,
                CredentialLifetime = credential.Lifetime,
                Authority = this,
                Access = credential.Access
            };
        }
    }

    private CredentialRecord CheckSignedLocked(GatewaySignedRequest request)
    {
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
        if (replayFloor is { } floor && request.Timestamp <= floor)
            throw new GatewayProtocolException("auth.replay");

        foreach (var stale in credential.Nonces
            .Where(item => item.Value <= now).Select(item => item.Key).ToArray())
            credential.Nonces.Remove(stale);
        if (credential.Nonces.ContainsKey(request.Nonce))
            throw new GatewayProtocolException("auth.replay");
        if (credential.Nonces.Count >= MaximumNoncesPerCredential)
            throw new GatewayProtocolException("auth.rate");
        // A nonce must outlive only the time its signed timestamp still passes the clock check.
        var expires = request.Timestamp + RequestClockSkew + TimeSpan.FromTicks(1);
        credential.Nonces.Add(request.Nonce, expires);
        if (persistence is not null)
            unsavedNonces.Add(new(credential.CredentialId, request.Nonce, expires, now));
        return credential;
    }

    T IGatewayPrincipalAuthority.WithAuthority<T>(GatewayPrincipal principal, Func<T> operation) =>
        WithAuthority(principal, operation);

    internal T WithAuthority<T>(GatewayPrincipal principal, Func<T> operation)
    {
        lock (gate)
        {
            var now = ObserveTimeLocked(clock);
            GatewayRules.Require(ReferenceEquals(principal.Authority, this) &&
                principal.HostId == identity.HostId, "auth.invalid");
            GatewayRules.Require(credentials.TryGetValue(principal.CredentialId, out var credential),
                "auth.invalid");
            GatewayRules.Require(!credential!.Revoked, "auth.revoked");
            GatewayRules.Require(!ExpiredLocked(credential, now), "auth.expired");
            GatewayRules.Require(credential.DeviceId == principal.DeviceId &&
                credential.Roles.Contains(principal.Role) && credential.Access == principal.Access, "auth.role");
            return operation();
        }
    }

    private IssuedDeviceCredential IssueLocked(
        string deviceId,
        string displayName,
        GatewayRole[] roles,
        DateTimeOffset now,
        GatewayAccess access = GatewayAccess.Full,
        int releasing = 0)
    {
        GatewayRules.Defined(access);
        SweepInactiveLocked(now);
        GatewayRules.Require(credentials.Count - releasing < MaximumRegistrations, "auth.capacity");
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
            RemainingTicks = 0,
            Access = access
        });
        return new()
        {
            CredentialId = credentialId!,
            DeviceId = deviceId,
            Roles = Array.AsReadOnly(roles.ToArray()),
            Secret = new(secretText),
            Lifetime = new PairedDeviceLifetime(),
            Access = access
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
            lastObservedTime is { } previous && previous - now > MaximumClockStepBack ||
            persistence is not null && (currentFrequency <= 0 ||
                lastTimestamp is { } previousTimestamp &&
                (timestamp < previousTimestamp || frequency != currentFrequency)))
        {
            clockInvalid = true;
            CloseLocked();
            throw new GatewayProtocolException("auth.clock_invalid");
        }
        // A small step back (time sync) holds the high-water mark, so observed time never moves backwards.
        if (lastObservedTime is { } highWater && now < highWater)
            now = highWater;
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
        // The checkpoint holds every live nonce, so the nonces waiting for an append are saved with it.
        unsavedNonces.Clear();
        if (replayFloor is { } floor && floor < now - RequestClockSkew)
            replayFloor = null;
        return new(now, credentials.Values.Select(record => new StoredGatewayCredential(
            record.CredentialId, record.DeviceId, record.DisplayName, record.Roles.ToArray(),
            record.Verifier.ToArray(), record.IssuedAt, record.Lifetime,
            RemainingLocked(record, now), record.RotatedToCredentialId,
            record.Nonces.Where(item => item.Value > now)
                .Select(item => new StoredGatewayNonce(item.Key, item.Value)).ToArray(),
            record.Access == GatewayAccess.Friend ? GatewayAccess.Friend : null)).ToArray(),
            lastTimestamp!.Value, frequency, replayFloor);
    }

    /// <summary>Saves the authority with the caller's change in it. The caller holds <c>commitGate</c> and <c>gate</c>.
    /// A failure closes the authority, because the disk state is then unknown.</summary>
    private void CommitLocked()
    {
        if (persistence is null)
            return;
        try
        {
            using var checkpoint = CheckpointLocked(ObserveTimeLocked(clock));
            var sequence = ++changeSequence;
            TimedCommit(checkpoint);
            committedSequence = sequence;
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

    /// <summary>Makes every change up to <paramref name="ticket"/> durable. Holds <c>commitGate</c> while it writes, and
    /// <c>gate</c> only to copy the state, so one write can save the nonces of many requests. Nonces go to the store's
    /// nonce journal (one append and one flush); when the store cannot append, the whole authority is saved.</summary>
    private void PersistThrough(long ticket)
    {
        lock (commitGate)
        {
            StoredGatewayNonceRecord[] nonces;
            long sequence;
            lock (gate)
            {
                if (committedSequence >= ticket)
                    return;
                nonces = unsavedNonces.ToArray();
                unsavedNonces.Clear();
                sequence = changeSequence;
            }
            try
            {
                if (TimedAppend(nonces))
                {
                    committedSequence = sequence;
                    return;
                }
            }
            catch (Exception error)
            {
                lock (gate)
                    CloseLocked();
                if (error is GatewayProtocolException) throw;
                throw new GatewayProtocolException("auth.storage");
            }
            GatewayCheckpoint checkpoint;
            lock (gate)
            {
                checkpoint = CheckpointLocked(ObserveTimeLocked(clock));
                sequence = changeSequence;
            }
            using (checkpoint)
            {
                try { TimedCommit(checkpoint); }
                catch (Exception error)
                {
                    lock (gate)
                        CloseLocked();
                    if (error is GatewayProtocolException) throw;
                    throw new GatewayProtocolException("auth.storage");
                }
            }
            committedSequence = sequence;
        }
    }

    private bool TimedAppend(StoredGatewayNonceRecord[] nonces)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!persistence!.AppendNonces(nonces))
            return false;
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        lock (statsGate)
            nonceStats = Add(nonceStats, elapsed);
        if (elapsed >= SlowCommit)
            SlowCommitObserved?.Invoke(elapsed);
        return true;
    }

    private void TimedCommit(GatewayCheckpoint checkpoint)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        persistence!.Commit(checkpoint);
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        lock (statsGate)
            commitStats = Add(commitStats, elapsed);
        if (elapsed >= SlowCommit)
            SlowCommitObserved?.Invoke(elapsed);
    }

    private static GatewayAuthorityCommitStats Add(GatewayAuthorityCommitStats stats, TimeSpan elapsed) => stats with
    {
        Commits = stats.Commits + 1,
        SlowCommits = stats.SlowCommits + (elapsed >= SlowCommit ? 1 : 0),
        Last = elapsed,
        Total = stats.Total + elapsed,
        Longest = elapsed > stats.Longest ? elapsed : stats.Longest
    };

    /// <summary>How long full saves of the authority take (pairing changes, the first request after a restart, and a
    /// request whose nonce cannot be appended to the nonce journal).</summary>
    internal GatewayAuthorityCommitStats CommitStats
    {
        get
        {
            lock (statsGate)
                return commitStats;
        }
    }

    /// <summary>How long nonce appends take; each signed request waits for one (requests that arrive together share it).</summary>
    internal GatewayAuthorityCommitStats NonceStats
    {
        get
        {
            lock (statsGate)
                return nonceStats;
        }
    }

    /// <summary>Called after a commit that took <see cref="SlowCommit"/> or longer, for the host log.</summary>
    internal Action<TimeSpan>? SlowCommitObserved { get; set; }

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

    /// <summary>True once this authority closed (its clock stepped back too far, its storage failed, or it was closed):
    /// it refuses every request until the same store is reopened.</summary>
    internal bool Closed
    {
        get
        {
            lock (gate)
                return closed || clockInvalid;
        }
    }

    bool IGatewayAdmissionStatus.AdmissionsOpen => AdmissionsOpen;

    internal bool AdmissionsOpen
    {
        get
        {
            lock (gate)
            {
                if (closed || stopping || clockInvalid) return false;
                var now = clock.GetUtcNow();
                if (now.Offset != TimeSpan.Zero || now <= DateTimeOffset.MinValue ||
                    now > DateTimeOffset.MaxValue - TimeSpan.FromDays(90) ||
                    lastObservedTime is { } previous && previous - now > MaximumClockStepBack)
                    return false;
                return persistence is null || clock.TimestampFrequency > 0 &&
                    clock.TimestampFrequency == frequency &&
                    (lastTimestamp is null || clock.GetTimestamp() >= lastTimestamp.Value);
            }
        }
    }

    internal void CommitMaintenance(Action<GatewayCheckpoint> commit)
    {
        lock (commitGate)
        lock (gate)
        {
            try
            {
                using var checkpoint = CheckpointLocked(ObserveTimeLocked(clock));
                var sequence = ++changeSequence;
                commit(checkpoint);
                committedSequence = sequence;
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
        lock (commitGate)
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
        internal GatewayAccess Access { get; init; }
        internal Dictionary<string, DateTimeOffset> Nonces { get; } = new(StringComparer.Ordinal);
    }
}
