namespace Martlet.Gateway.Trust;

public sealed record GatewayHostIdentity
{
    public Guid HostId { get; }
    public string SpkiSha256 { get; }

    public GatewayHostIdentity(Guid hostId, string spkiSha256)
    {
        if (hostId == Guid.Empty)
            throw new ArgumentException("A nonempty host identity is required.", nameof(hostId));
        if (spkiSha256 is not { Length: 64 } ||
            spkiSha256.Any(c => !char.IsAsciiHexDigitUpper(c)))
            throw new ArgumentException("A canonical uppercase SHA-256 SPKI fingerprint is required.", nameof(spkiSha256));
        HostId = hostId;
        SpkiSha256 = spkiSha256;
    }
}

[Flags]
public enum GatewayScope
{
    None = 0,
    Status = 1,
    Transcription = 2,
    Generation = 4,
    Synthesis = 8,
    Perception = 16,
    MemoryRead = 32,
    MemoryWrite = 64
}

public enum GatewayTrustFailure
{
    HostMismatch,
    ScopeDenied,
    PairingRejected,
    CredentialRejected,
    DeviceConflict,
    ResourceLimit,
    RateLimited,
    ClockInvalid,
    AuthorityClosed
}

public sealed class GatewayTrustException : Exception
{
    public GatewayTrustFailure Failure { get; }

    internal GatewayTrustException(GatewayTrustFailure failure)
        : base($"Gateway trust operation rejected: {failure}.") => Failure = failure;
}

public static class GatewayTrustLimits
{
    public const int MaximumDevices = 128;
    public const int MaximumPendingApprovals = 16;
    public const int MaximumRedemptionsPerWindow = 32;
    public static TimeSpan PairingLifetime => TimeSpan.FromMinutes(5);
    public static TimeSpan CredentialLifetime => TimeSpan.FromDays(90);
    public static TimeSpan RotationOverlap => TimeSpan.FromMinutes(2);
    public static TimeSpan RedemptionWindow => TimeSpan.FromMinutes(1);
}

public sealed record GatewayPairingOffer(
    Guid ApprovalId, GatewayHostIdentity Host, Guid DeviceId, GatewayScope Scopes,
    DateTimeOffset ExpiresAt, GatewaySecret Token);

public sealed record GatewayDeviceCredential(
    GatewayHostIdentity Host, Guid DeviceId, GatewayScope Scopes,
    DateTimeOffset ExpiresAt, GatewaySecret Secret);

public sealed record GatewayDeviceInfo(Guid DeviceId, GatewayScope Scopes, DateTimeOffset ExpiresAt);

// A point-in-time decision, not a reusable dispatch permit or a provider authorization.
public sealed record GatewayAccessDecision(GatewayHostIdentity Host, Guid DeviceId, GatewayScope Scopes);
