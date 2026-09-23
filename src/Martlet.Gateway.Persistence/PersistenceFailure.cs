namespace Martlet.Gateway.Persistence;

public enum GatewayPersistenceFailure
{
    UnsupportedPlatform,
    InvalidPath,
    InsecureStorage,
    StoreMissing,
    StoreBusy,
    InvalidState,
    KeyProtectionFailed,
    StorageFailed,
    RecoveryRequired,
    ClockUnavailable,
    MigrationRequired,
    Disabled,
    Closed,
    StorageBackendMismatch
}

public sealed class GatewayPersistenceException : Exception
{
    public GatewayPersistenceFailure Failure { get; }

    internal GatewayPersistenceException(GatewayPersistenceFailure failure)
        : base(failure switch
        {
            GatewayPersistenceFailure.UnsupportedPlatform => "The selected authority backend requires its supported native OS and local filesystem.",
            GatewayPersistenceFailure.InvalidPath => "Select a canonical, app-owned path with an existing private parent on the backend's supported local filesystem.",
            GatewayPersistenceFailure.InsecureStorage => "The owned storage permissions, ownership or file identity are unsafe.",
            GatewayPersistenceFailure.StoreMissing => "The protected authority does not exist; it was not automatically created.",
            GatewayPersistenceFailure.StoreBusy => "The protected authority already has an owner.",
            GatewayPersistenceFailure.InvalidState => "The protected authority format or protocol is invalid.",
            GatewayPersistenceFailure.KeyProtectionFailed => "The protected key material could not be accessed.",
            GatewayPersistenceFailure.StorageFailed => "The authority storage operation failed; its outcome may be uncertain.",
            GatewayPersistenceFailure.RecoveryRequired => "Access is blocked pending protected-state recovery. Pairing data was preserved; do not reset devices or restore stale backups.",
            GatewayPersistenceFailure.ClockUnavailable => "Correct the host clock and reopen the existing authority. Paired devices remain recorded.",
            GatewayPersistenceFailure.MigrationRequired => "This earlier authority format requires a deliberate migration. Original identity and pairing bytes were preserved; do not reset or overwrite the store.",
            GatewayPersistenceFailure.Disabled => "The local gateway remains disabled until explicitly enabled.",
            GatewayPersistenceFailure.StorageBackendMismatch => "The stored authority belongs to a different storage backend. Original bytes were preserved; no automatic custody migration is available.",
            _ => "The local gateway owner is closed."
        }) => Failure = failure;
}

internal static class PersistenceFailure
{
    internal static GatewayPersistenceException Error(GatewayPersistenceFailure failure) => new(failure);
}
