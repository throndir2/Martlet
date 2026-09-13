namespace Martlet.Support;

public enum SupportFailure
{
    InvalidData, UnsupportedVersion, InvalidPath, Busy, Closed, LimitExceeded,
    CorruptJournal, AccessDenied, IoFailure, Canceled, DeadlineExceeded,
    ConsentMismatch, ConsentConsumed, DestinationExists, CleanupPending
}

// Never retain an underlying exception: its message, Data or inner exception can contain local data.
public sealed class SupportException(SupportFailure failure) : Exception(MessageFor(failure))
{
    public SupportFailure Failure { get; } = failure;
    public override string ToString() => $"{nameof(SupportException)} ({Failure}): {Message}";

    private static string MessageFor(SupportFailure failure) => failure switch
    {
        SupportFailure.InvalidData => "Unsupported support metadata. Supply only the documented typed fields and catalog IDs.",
        SupportFailure.UnsupportedVersion => "Use a compatible support schema version; preserve the original evidence.",
        SupportFailure.InvalidPath => "Choose an explicit absolute local directory without links, alternate streams or network paths.",
        SupportFailure.Busy => "This support scope is in use. Wait for its owner to close; no record was queued.",
        SupportFailure.Closed => "This resource is closed or faulted. Close it and explicitly reopen the scope before retrying.",
        SupportFailure.LimitExceeded => "The support size or count limit was reached. Select a smaller range or a new owned scope.",
        SupportFailure.CorruptJournal => "Journal evidence is corrupt or incomplete. Preserve the scope; do not delete unrelated files. Only a truncated active tail can be recovered.",
        SupportFailure.AccessDenied => "Support storage access was denied. Choose an accessible local scope; do not elevate or disable protection.",
        SupportFailure.Canceled => "The action was canceled. No successful result is claimed; a journal write may need recovery.",
        SupportFailure.DeadlineExceeded => "The action exceeded its deadline. An in-flight IO call must finish before ownership is released.",
        SupportFailure.ConsentMismatch => "Preview this exact snapshot and destination again before explicitly approving export.",
        SupportFailure.ConsentConsumed => "This approval was already used. Review and approve a fresh local export.",
        SupportFailure.DestinationExists => "The selected output already exists. Choose a new filename; nothing was overwritten.",
        SupportFailure.CleanupPending => "An owned partial export still needs cleanup. Keep this snapshot alive and retry cleanup before another export or disposal.",
        _ => "Local support IO failed. Preserve existing evidence, check available storage and access, then retry explicitly."
    };
}

internal static class Guard
{
    internal static void Require(bool condition, SupportFailure failure = SupportFailure.InvalidData)
    {
        if (!condition) throw new SupportException(failure);
    }

    internal static void Defined<T>(T value) where T : struct, Enum => Require(Enum.IsDefined(value));
    internal static void Utc(DateTimeOffset value) =>
        Require(value.Offset == TimeSpan.Zero && value.Year is >= 2000 and <= 2100);
    internal static void Version(string value) =>
        Require(value is { Length: >= 5 and <= 32 } && value.All(c => char.IsAsciiDigit(c) || c == '.') &&
            System.Version.TryParse(value, out var version) && version.Build >= 0);
}

internal sealed class Operation(TimeProvider clock, CancellationToken token, TimeSpan? timeout)
{
    private readonly long started = clock.GetTimestamp();
    private readonly TimeSpan limit = Validate(timeout ?? TimeSpan.FromSeconds(30));
    private static TimeSpan Validate(TimeSpan value)
    {
        Guard.Require(value > TimeSpan.Zero && value <= TimeSpan.FromMinutes(2));
        return value;
    }

    internal void Check()
    {
        // Check the original token itself, not a linked token whose callbacks may be blocked.
        if (token.IsCancellationRequested) throw new SupportException(SupportFailure.Canceled);
        if (clock.GetElapsedTime(started) >= limit) throw new SupportException(SupportFailure.DeadlineExceeded);
    }
}
