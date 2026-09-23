using System.Runtime.CompilerServices;
using System.Security.Cryptography;

[assembly: InternalsVisibleTo("Martlet.Gateway.Persistence")]
[assembly: InternalsVisibleTo("Martlet.Gateway.Persistence.Tests")]

namespace Martlet.Gateway.Trust;

internal interface IDurableTrust
{
    void Commit(TrustCheckpoint checkpoint);
    void Complete(TrustCheckpoint checkpoint);
}

internal sealed record StoredLifetime(DateTimeOffset ExpiresAt, long RemainingTicks);
internal sealed record StoredCredential(byte[] Verifier, StoredLifetime Life);
internal sealed record StoredDevice(Guid DeviceId, GatewayScope Scopes,
    StoredCredential Current, StoredCredential? Previous, StoredLifetime? Overlap);

internal sealed record TrustCheckpoint(DateTimeOffset ObservedAt, StoredDevice[] Devices) : IDisposable
{
    public void Dispose()
    {
        foreach (var device in Devices ?? [])
        {
            if (device?.Current?.Verifier is { } current)
                CryptographicOperations.ZeroMemory(current);
            if (device?.Previous?.Verifier is { } previous)
                CryptographicOperations.ZeroMemory(previous);
        }
    }
}
