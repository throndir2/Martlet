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
    /// <summary>What the device may use here (<see cref="GatewayAccess"/>); fixed for the credential's life.</summary>
    public GatewayAccess Access { get; init; }

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
    public GatewayAccess Access { get; init; }
}

/// <summary>A computer paired with this host: its name and newest live pairing, and when it last made a signed request
/// (null when it has not since the gateway started; this is kept in memory only), and what that pairing may use.</summary>
public sealed record GatewayPairedDevice(string DeviceId, string DisplayName, DateTimeOffset PairedAt, DateTimeOffset? LastSeen,
    GatewayAccess Access = GatewayAccess.Full);

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
    internal IGatewayPrincipalAuthority? Authority { get; init; }
    internal T WithAuthority<T>(Func<T> operation) =>
        (Authority ?? throw new GatewayProtocolException("auth.invalid")).WithAuthority(this, operation);
    /// <summary>The API key this principal presented; null for a paired device's signed request.</summary>
    internal Martlet.Core.Access.ApiKey? Key { get; init; }
    /// <summary>The API key scope this principal was admitted with when it isn't its role's: <c>openai</c> for the
    /// OpenAI-compatible endpoint (GatewayOpenAi.cs). Null for every other request.</summary>
    internal string? Scope { get; init; }
    /// <summary>Who made the request, for logs: the device ID (marked when it is a friend's) or the API key's name.</summary>
    internal string Caller => Key is { } key ? $"API key \"{key.Name}\"" : Access == GatewayAccess.Friend ? $"friend {DeviceId}" : DeviceId;
    /// <summary>What the credential may use here; a friend's reaches only the routes that admit friends.</summary>
    public GatewayAccess Access { get; init; }

    public required string HostId { get; init; }
    public required string CredentialId { get; init; }
    public required string DeviceId { get; init; }
    public required GatewayRole Role { get; init; }
    public required GatewayCredentialLifetime CredentialLifetime { get; init; }
}
