using System.Security.Cryptography;

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
    DateTimeOffset ExpiresAt,
    long RemainingTicks,
    string? RotatedToCredentialId,
    StoredGatewayNonce[] Nonces);

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

internal interface IGatewayPairingExchange
{
    IssuedDeviceCredential Exchange(GatewayPairingProof proof, CancellationToken cancellationToken);
}
