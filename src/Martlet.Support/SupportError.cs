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
        SupportFailure.InvalidData => "Troubleshooting data is invalid.",
        SupportFailure.UnsupportedVersion => "This troubleshooting data needs a newer Martlet.",
        SupportFailure.InvalidPath => "Choose a local folder on this PC.",
        SupportFailure.Busy => "Troubleshooting is busy. Try again in a moment.",
        SupportFailure.Closed => "Troubleshooting was closed. Reopen it and try again.",
        SupportFailure.LimitExceeded => "The selected troubleshooting range is too large.",
        SupportFailure.CorruptJournal => "Troubleshooting records are damaged. Keep the folder for review.",
        SupportFailure.AccessDenied => "Troubleshooting storage access was denied. Choose a folder you can write to.",
        SupportFailure.Canceled => "The action was canceled.",
        SupportFailure.DeadlineExceeded => "The action took too long. Try again.",
        SupportFailure.ConsentMismatch => "Preview the export again before approving it.",
        SupportFailure.ConsentConsumed => "This approval was already used. Review and approve a fresh local export.",
        SupportFailure.DestinationExists => "The selected output already exists. Choose a new filename; nothing was overwritten.",
        SupportFailure.CleanupPending => "A previous troubleshooting export still needs cleanup.",
        _ => "Troubleshooting storage failed. Check the location and try again."
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
