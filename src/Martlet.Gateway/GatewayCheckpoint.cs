using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Martlet.Gateway;

internal interface IGatewayPersistence
{
    void Commit(GatewayCheckpoint checkpoint);
    void Complete(GatewayCheckpoint checkpoint, Action validateBeforeClean);
}

internal sealed record StoredGatewayNonce(string Nonce, DateTimeOffset ExpiresAt);

internal sealed record StoredGatewayCredential(
    string CredentialId,
    string DeviceId,
    string DisplayName,
    GatewayRole[] Roles,
    byte[] SigningKey,
    DateTimeOffset IssuedAt,
    GatewayCredentialLifetime Lifetime,
    long RemainingTicks,
    string? RotatedToCredentialId,
    StoredGatewayNonce[] Nonces,
    // Only a friend's credential stores its access, so stores without friends stay exactly as before.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GatewayAccess? Access = null);

internal sealed record GatewayCheckpoint(
    DateTimeOffset ObservedAt,
    StoredGatewayCredential[] Credentials,
    long Timestamp,
    long Frequency) : IDisposable
{
    public void Dispose()
    {
        foreach (var credential in Credentials ?? [])
            if (credential?.SigningKey is { } key)
                CryptographicOperations.ZeroMemory(key);
    }
}

internal interface IGatewayRequestCredentials
{
    GatewayPrincipal Authenticate(GatewaySignedRequest request);
}

internal interface IGatewayAdmissionStatus
{
    bool AdmissionsOpen { get; }
}

internal interface IGatewayPairingExchange
{
    IssuedDeviceCredential Exchange(GatewayPairingProof proof, CancellationToken cancellationToken);
    GatewayCodePairingResult Exchange(GatewayCodePairingProof proof, CancellationToken cancellationToken);
}
