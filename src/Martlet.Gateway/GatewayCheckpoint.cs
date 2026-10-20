using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Martlet.Gateway;

internal interface IGatewayPersistence
{
    void Commit(GatewayCheckpoint checkpoint);
    void Complete(GatewayCheckpoint checkpoint, Action validateBeforeClean);

    /// <summary>Makes request nonces durable without a full save (one append and one flush). Returns false when the
    /// store cannot append now; the caller then saves the whole authority, which also holds these nonces.</summary>
    bool AppendNonces(IReadOnlyList<StoredGatewayNonceRecord> nonces) => false;
}

internal sealed record StoredGatewayNonce(string Nonce, DateTimeOffset ExpiresAt);

internal sealed record StoredGatewayNonceRecord(string CredentialId, string Nonce, DateTimeOffset ExpiresAt, DateTimeOffset ObservedAt);

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
    long Frequency,
    // After a damaged nonce journal, requests signed at or before this time are refused: their nonces can be lost.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ReplayFloor = null) : IDisposable
{
    /// <summary>The newest time the nonce journal recorded after this checkpoint was saved. It holds the clock's high-water
    /// mark on load; it is never saved (the next full save moves <see cref="ObservedAt"/> past it).</summary>
    [JsonIgnore]
    public DateTimeOffset? JournalObservedAt { get; init; }

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
