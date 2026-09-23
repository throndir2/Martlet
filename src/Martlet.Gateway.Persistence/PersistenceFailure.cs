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
    Disabled,
    Closed
}

public sealed class GatewayPersistenceException : Exception
{
    public GatewayPersistenceFailure Failure { get; }

    internal GatewayPersistenceException(GatewayPersistenceFailure failure)
        : base(failure switch
        {
            GatewayPersistenceFailure.UnsupportedPlatform => "This authority requires Windows and local NTFS storage.",
            GatewayPersistenceFailure.InvalidPath => "Select a canonical, app-owned local NTFS directory with an existing parent.",
            GatewayPersistenceFailure.InsecureStorage => "The owned storage permissions, ownership or file identity are unsafe.",
            GatewayPersistenceFailure.StoreMissing => "The protected authority does not exist; it was not automatically created.",
            GatewayPersistenceFailure.StoreBusy => "The protected authority already has an owner.",
            GatewayPersistenceFailure.InvalidState => "The protected authority format or protocol is invalid.",
            GatewayPersistenceFailure.KeyProtectionFailed => "The protected key material could not be accessed.",
            GatewayPersistenceFailure.StorageFailed => "The authority storage operation failed; its outcome may be uncertain.",
            GatewayPersistenceFailure.RecoveryRequired => "Explicit local device reset and re-pairing are required; never restore a backup.",
            GatewayPersistenceFailure.Disabled => "The local gateway remains disabled until explicitly enabled.",
            _ => "The local gateway owner is closed."
        }) => Failure = failure;
}

internal static class PersistenceFailure
{
    internal static GatewayPersistenceException Error(GatewayPersistenceFailure failure) => new(failure);
}
