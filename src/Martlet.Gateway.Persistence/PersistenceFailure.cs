using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Martlet.Gateway.Persistence.Tests")]

namespace Martlet.Gateway.Persistence;

public enum GatewayPersistenceFailure
{
    UnsupportedPlatform,
    InvalidPath,
    InsecureStorage,
    StoreBusy,
    StoreMissing,
    RecoveryRequired,
    InvalidState,
    KeyProtectionFailed,
    StorageFailed
}

public sealed class GatewayPersistenceException : Exception
{
    public GatewayPersistenceFailure Failure { get; }

    internal GatewayPersistenceException(GatewayPersistenceFailure failure)
        : base($"Gateway persistence rejected: {failure}.") => Failure = failure;
}

internal static class PersistenceFailure
{
    internal static GatewayPersistenceException Error(GatewayPersistenceFailure failure) => new(failure);
}
