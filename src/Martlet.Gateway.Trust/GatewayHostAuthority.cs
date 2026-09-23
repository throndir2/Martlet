using System.Security.Cryptography;

namespace Martlet.Gateway.Trust;

// Keep this owner in the trusted host composition; give transport handlers only Devices.
public sealed class GatewayHostAuthority : IDisposable
{
    private readonly GatewayTrustState state;
    public GatewayDeviceAccess Devices { get; }

    public GatewayHostAuthority(GatewayHostIdentity identity, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        state = new(identity, clock ?? TimeProvider.System);
        Devices = new(state);
    }

    public GatewayPairingOffer ApprovePairing(Guid deviceId, GatewayScope scopes,
        CancellationToken cancellationToken = default) =>
        state.Approve(deviceId, scopes, rotation: false, cancellationToken);

    public GatewayPairingOffer ApproveRotation(Guid deviceId, CancellationToken cancellationToken = default) =>
        state.Approve(deviceId, GatewayScope.None, rotation: true, cancellationToken);

    public void CancelApproval(Guid approvalId, CancellationToken cancellationToken = default) =>
        state.CancelApproval(approvalId, cancellationToken);

    public void RevokeDevice(Guid deviceId, CancellationToken cancellationToken = default) =>
        state.Revoke(deviceId, cancellationToken);

    public IReadOnlyList<GatewayDeviceInfo> ListDevices(CancellationToken cancellationToken = default) =>
        state.ListDevices(cancellationToken);

    public void Dispose() => state.Dispose();
    public override string ToString() => nameof(GatewayHostAuthority);
}

public sealed class GatewayDeviceAccess
{
    private readonly GatewayTrustState state;
    internal GatewayDeviceAccess(GatewayTrustState state) => this.state = state;

    public GatewayDeviceCredential Redeem(GatewayHostIdentity host, Guid deviceId, GatewayScope scopes,
        Guid approvalId, GatewaySecret token, CancellationToken cancellationToken = default) =>
        state.Redeem(host, deviceId, scopes, approvalId, token, cancellationToken);

    public GatewayAccessDecision Authorize(GatewayHostIdentity host, Guid deviceId,
        GatewaySecret credential, GatewayScope requiredScopes, CancellationToken cancellationToken = default) =>
        state.Authorize(host, deviceId, credential, requiredScopes, cancellationToken);

    public override string ToString() => nameof(GatewayDeviceAccess);
}

internal sealed class GatewayTrustState : IDisposable
{
    private const GatewayScope KnownScopes = GatewayScope.Status | GatewayScope.Transcription |
        GatewayScope.Generation | GatewayScope.Synthesis | GatewayScope.Perception |
        GatewayScope.MemoryRead | GatewayScope.MemoryWrite;
    private readonly object gate = new();
    private readonly GatewayHostIdentity identity;
    private readonly TimeProvider clock;
    private readonly long frequency;
    private readonly Dictionary<Guid, Approval> approvals = [];
    private readonly Dictionary<Guid, Device> devices = [];
    private Moment? last;
    private Lifetime? redemptionWindow;
    private int redemptions;
    private GatewayTrustFailure? closed;

    internal GatewayTrustState(GatewayHostIdentity identity, TimeProvider clock)
    {
        this.identity = identity;
        this.clock = clock;
        frequency = clock.TimestampFrequency;
        if (frequency <= 0)
            throw new ArgumentException("The clock must provide a positive timestamp frequency.", nameof(clock));
    }

    internal GatewayPairingOffer Approve(Guid deviceId, GatewayScope scopes, bool rotation, CancellationToken ct)
    {
        lock (gate)
        {
            var now = Enter(ct);
            if (deviceId == Guid.Empty)
                throw new ArgumentException("A nonempty device identity is required.", nameof(deviceId));
            if (approvals.Count >= GatewayTrustLimits.MaximumPendingApprovals)
                throw Failure(GatewayTrustFailure.ResourceLimit);
            if (approvals.Values.Any(a => a.DeviceId == deviceId))
                throw Failure(GatewayTrustFailure.DeviceConflict);
            if (rotation)
            {
                if (!devices.TryGetValue(deviceId, out var device))
                    throw Failure(GatewayTrustFailure.CredentialRejected);
                if (device.Previous is not null)
                    throw Failure(GatewayTrustFailure.DeviceConflict);
                scopes = device.Scopes;
            }
            else
            {
                ValidateScopes(scopes);
                if (devices.ContainsKey(deviceId))
                    throw Failure(GatewayTrustFailure.DeviceConflict);
                if (devices.Count + approvals.Values.Count(a => !a.Rotation) >= GatewayTrustLimits.MaximumDevices)
                    throw Failure(GatewayTrustFailure.ResourceLimit);
            }
            var life = NewLifetime(now, GatewayTrustLimits.PairingLifetime);
            ct.ThrowIfCancellationRequested();
            var token = GatewaySecret.Generate();
            var id = Guid.NewGuid();
            approvals.Add(id, new(deviceId, scopes, rotation, life, token.Verifier()));
            return new(id, identity, deviceId, scopes, life.ExpiresAt, token);
        }
    }

    internal GatewayDeviceCredential Redeem(GatewayHostIdentity host, Guid deviceId, GatewayScope scopes,
        Guid approvalId, GatewaySecret token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(token);
        lock (gate)
        {
            var now = Enter(ct);
            AdmitRedemption(now);
            RequireHost(host);
            ValidateScopes(scopes);
            if (!approvals.TryGetValue(approvalId, out var approval) ||
                approval.DeviceId != deviceId || approval.Scopes != scopes)
                throw Failure(GatewayTrustFailure.PairingRejected);
            if (!Matches(token, approval.Verifier))
                throw Failure(GatewayTrustFailure.PairingRejected);
            var life = NewLifetime(now, GatewayTrustLimits.CredentialLifetime);
            var overlap = NewLifetime(now, GatewayTrustLimits.RotationOverlap);
            ct.ThrowIfCancellationRequested();
            var secret = GatewaySecret.Generate();
            var current = new Credential(secret.Verifier(), life);
            if (approval.Rotation)
            {
                var device = devices[deviceId];
                device.Previous = device.Current;
                device.Overlap = overlap;
                device.Current = current;
            }
            else
            {
                devices.Add(deviceId, new(approval.Scopes, current));
            }
            RemoveApproval(approvalId);
            return new(identity, deviceId, approval.Scopes, life.ExpiresAt, secret);
        }
    }

    internal GatewayAccessDecision Authorize(GatewayHostIdentity host, Guid deviceId,
        GatewaySecret secret, GatewayScope required, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(secret);
        lock (gate)
        {
            Enter(ct);
            RequireHost(host);
            ValidateScopes(required);
            if (!devices.TryGetValue(deviceId, out var device))
                throw Failure(GatewayTrustFailure.CredentialRejected);
            var currentMatches = Matches(secret, device.Current.Verifier);
            var previousMatches = device.Previous is not null && Matches(secret, device.Previous.Verifier);
            if (!currentMatches && !previousMatches)
                throw Failure(GatewayTrustFailure.CredentialRejected);
            if ((device.Scopes & required) != required)
                throw Failure(GatewayTrustFailure.ScopeDenied);
            ct.ThrowIfCancellationRequested();
            return new(identity, deviceId, required);
        }
    }

    internal void CancelApproval(Guid approvalId, CancellationToken ct)
    {
        lock (gate)
        {
            Enter(ct);
            if (!approvals.ContainsKey(approvalId))
                throw Failure(GatewayTrustFailure.PairingRejected);
            ct.ThrowIfCancellationRequested();
            RemoveApproval(approvalId);
        }
    }

    internal void Revoke(Guid deviceId, CancellationToken ct)
    {
        lock (gate)
        {
            Enter(ct);
            if (!devices.ContainsKey(deviceId))
                throw Failure(GatewayTrustFailure.CredentialRejected);
            ct.ThrowIfCancellationRequested();
            RemoveDevice(deviceId);
        }
    }

    internal IReadOnlyList<GatewayDeviceInfo> ListDevices(CancellationToken ct)
    {
        lock (gate)
        {
            Enter(ct);
            ct.ThrowIfCancellationRequested();
            return Array.AsReadOnly(devices.Select(p =>
                new GatewayDeviceInfo(p.Key, p.Value.Scopes, p.Value.Current.Life.ExpiresAt)).ToArray());
        }
    }

    private Moment Enter(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (closed is { } reason)
            throw Failure(reason);
        var now = new Moment(clock.GetUtcNow(), clock.GetTimestamp());
        ct.ThrowIfCancellationRequested();
        if (clock.TimestampFrequency != frequency ||
            last is { } previous && (now.Utc < previous.Utc || now.Timestamp < previous.Timestamp))
        {
            Close(GatewayTrustFailure.ClockInvalid);
            throw Failure(GatewayTrustFailure.ClockInvalid);
        }
        last = now;
        foreach (var id in approvals.Where(p => Expired(p.Value.Life, now)).Select(p => p.Key).ToArray())
            RemoveApproval(id);
        foreach (var id in devices.Where(p => Expired(p.Value.Current.Life, now)).Select(p => p.Key).ToArray())
            RemoveDevice(id);
        foreach (var device in devices.Values)
        {
            if (device.Previous is { } old && (Expired(old.Life, now) || Expired(device.Overlap!, now)))
            {
                CryptographicOperations.ZeroMemory(old.Verifier);
                device.Previous = null;
                device.Overlap = null;
            }
        }
        return now;
    }

    private void AdmitRedemption(Moment now)
    {
        if (redemptionWindow is null || Expired(redemptionWindow, now))
        {
            redemptionWindow = NewLifetime(now, GatewayTrustLimits.RedemptionWindow);
            redemptions = 0;
        }
        if (redemptions >= GatewayTrustLimits.MaximumRedemptionsPerWindow)
            throw Failure(GatewayTrustFailure.RateLimited);
        redemptions++;
    }

    private Lifetime NewLifetime(Moment now, TimeSpan duration)
    {
        if (now.Utc > DateTimeOffset.MaxValue - duration)
        {
            Close(GatewayTrustFailure.ClockInvalid);
            throw Failure(GatewayTrustFailure.ClockInvalid);
        }
        return new(now, duration, now.Utc + duration);
    }

    private bool Expired(Lifetime life, Moment now) =>
        now.Utc >= life.ExpiresAt ||
        ((decimal)now.Timestamp - life.Start.Timestamp) * TimeSpan.TicksPerSecond / frequency >= life.Duration.Ticks;

    private void RequireHost(GatewayHostIdentity host)
    {
        if (host != identity)
            throw Failure(GatewayTrustFailure.HostMismatch);
    }

    private static void ValidateScopes(GatewayScope scopes)
    {
        if (scopes == GatewayScope.None || (scopes & ~KnownScopes) != 0)
            throw Failure(GatewayTrustFailure.ScopeDenied);
    }

    private static bool Matches(GatewaySecret secret, byte[] expected)
    {
        var actual = secret.Verifier();
        try { return CryptographicOperations.FixedTimeEquals(actual, expected); }
        finally { CryptographicOperations.ZeroMemory(actual); }
    }

    private void RemoveApproval(Guid id)
    {
        CryptographicOperations.ZeroMemory(approvals[id].Verifier);
        approvals.Remove(id);
    }

    private void RemoveDevice(Guid id)
    {
        var device = devices[id];
        CryptographicOperations.ZeroMemory(device.Current.Verifier);
        if (device.Previous is { } previous)
            CryptographicOperations.ZeroMemory(previous.Verifier);
        devices.Remove(id);
        foreach (var pending in approvals.Where(p => p.Value.DeviceId == id).Select(p => p.Key).ToArray())
            RemoveApproval(pending);
    }

    private void Close(GatewayTrustFailure reason)
    {
        foreach (var id in devices.Keys.ToArray())
            RemoveDevice(id);
        foreach (var id in approvals.Keys.ToArray())
            RemoveApproval(id);
        closed = reason;
    }

    public void Dispose()
    {
        lock (gate)
            Close(GatewayTrustFailure.AuthorityClosed);
    }

    private static GatewayTrustException Failure(GatewayTrustFailure failure) => new(failure);
    private readonly record struct Moment(DateTimeOffset Utc, long Timestamp);
    private sealed record Lifetime(Moment Start, TimeSpan Duration, DateTimeOffset ExpiresAt);
    private sealed record Approval(Guid DeviceId, GatewayScope Scopes, bool Rotation, Lifetime Life, byte[] Verifier);
    private sealed record Credential(byte[] Verifier, Lifetime Life);
    private sealed class Device(GatewayScope scopes, Credential current)
    {
        internal GatewayScope Scopes { get; } = scopes;
        internal Credential Current { get; set; } = current;
        internal Credential? Previous { get; set; }
        internal Lifetime? Overlap { get; set; }
    }
}
