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
        MemoryFailure.InvalidData => "Memory data is invalid. Supply only the documented bounded fields.",
        MemoryFailure.UnsupportedVersion => "The memory store uses an unsupported version. Preserve it and use a compatible build.",
        MemoryFailure.InvalidPath => "Choose an explicit absolute local path without links, alternate streams or network storage.",
        MemoryFailure.ConsentRequired => "Memory remains off. Review the selected local scope and explicitly allow this action.",
        MemoryFailure.ConsentMismatch => "The approval does not match this exact scope, preview, revision or destination.",
        MemoryFailure.ConsentConsumed => "This approval was already used. Review and approve a fresh action.",
        MemoryFailure.Busy => "This memory scope is already in use. Wait for its current owner to finish.",
        MemoryFailure.Closed => "This memory resource is closed. Explicitly reopen the selected scope.",
        MemoryFailure.LimitExceeded => "A memory count, text, query or storage bound was exceeded.",
        MemoryFailure.CorruptStore => "The owned memory store is malformed or incomplete. Preserve it; no reset was performed.",
        MemoryFailure.AccessDenied => "Memory storage access was denied. Choose an accessible local scope; do not elevate or disable protection.",
        MemoryFailure.Conflict => "Memory changed since it was inspected. Reload and review the current fact before retrying.",
        MemoryFailure.NotFound => "The exact memory fact was not found. No successful edit or deletion is claimed.",
        MemoryFailure.QueryInvalidated => "Memory changed while the result was in flight. Run a fresh retrieval or export preview.",
        MemoryFailure.DestinationExists => "The selected export already exists. Choose a new filename; nothing was overwritten.",
        MemoryFailure.CleanupPending => "An owned staging file still needs cleanup before another write or export.",
        _ => "Local memory IO failed. Existing authoritative data was preserved; check storage and retry explicitly."
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
