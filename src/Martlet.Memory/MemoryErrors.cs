namespace Martlet.Memory;

public enum MemoryFailure
{
    InvalidData,
    UnsupportedVersion,
    InvalidPath,
    ConsentRequired,
    ConsentMismatch,
    ConsentConsumed,
    Busy,
    Closed,
    LimitExceeded,
    CorruptStore,
    AccessDenied,
    IoFailure,
    Conflict,
    NotFound,
    QueryInvalidated,
    DestinationExists,
    CleanupPending
}

public sealed class MemoryException(MemoryFailure failure) : Exception(MessageFor(failure))
{
    public MemoryFailure Failure { get; } = failure;

    public override string ToString() => $"{nameof(MemoryException)} ({Failure}): {Message}";

    private static string MessageFor(MemoryFailure failure) => failure switch
    {
        MemoryFailure.InvalidData => "Memory data is invalid.",
        MemoryFailure.UnsupportedVersion => "This memory store needs a newer Martlet.",
        MemoryFailure.InvalidPath => "Choose a local folder on this PC.",
        MemoryFailure.ConsentRequired => "Memory is off; review the location and turn it on first.",
        MemoryFailure.ConsentMismatch => "Memory changed; review it again before continuing.",
        MemoryFailure.ConsentConsumed => "This approval was already used; review and approve a fresh action.",
        MemoryFailure.Busy => "Memory is in use; try again in a moment.",
        MemoryFailure.Closed => "Memory was closed; reopen it and try again.",
        MemoryFailure.LimitExceeded => "Memory is too large for this action.",
        MemoryFailure.CorruptStore => "The memory store is damaged; keep it and choose another location if needed.",
        MemoryFailure.AccessDenied => "Memory storage access was denied; choose a folder you can write to.",
        MemoryFailure.Conflict => "Memory changed; reload and try again.",
        MemoryFailure.NotFound => "That memory fact was not found.",
        MemoryFailure.QueryInvalidated => "Memory changed while Martlet was reading it; try again.",
        MemoryFailure.DestinationExists => "The selected export already exists, so nothing was overwritten; choose a new filename.",
        MemoryFailure.CleanupPending => "A previous memory export still needs cleanup; try again after cleanup finishes.",
        _ => "Memory storage failed; check the location and try again."
    };
}

internal static class MemoryGuard
{
    internal static void Require(bool condition, MemoryFailure failure = MemoryFailure.InvalidData)
    {
        if (!condition)
            throw new MemoryException(failure);
    }

    internal static void Defined<T>(T value) where T : struct, Enum =>
        Require(Enum.IsDefined(value));

    internal static void Utc(DateTimeOffset value) =>
        Require(value.Offset == TimeSpan.Zero && value.Year is >= 2000 and <= 2100);
}
