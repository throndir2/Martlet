namespace Martlet.Perception;

public enum PerceptionFailureCode
{
    NativePrivacyUnqualified,
    InvalidInput,
    InvalidBinding,
    AuthorizationRequired,
    AuthorizationConsumed,
    Canceled,
    DeadlineExceeded,
    Busy,
    Disposed,
    EnumerationFailed,
    SourceUnavailable,
    SourceClosed,
    SourceChanged,
    SourceSelectionChanged,
    AccessDenied,
    DestinationChanged,
    ConfigurationChanged,
    Paused,
    Locked,
    PayloadTooLarge,
    MalformedFrame,
    StaleFrame,
    FrameUnavailable,
    RateLimited,
    SessionTimeout,
    NativeCaptureFailed,
    CancellationFailed,
    CleanupFailed,
    InvalidState
}

public sealed record PerceptionFailure
{
    public PerceptionFailureCode Code { get; }

    public string Summary => Code switch
    {
        PerceptionFailureCode.NativePrivacyUnqualified =>
            "Production Windows window capture is unavailable until its source and privacy boundary is qualified.",
        PerceptionFailureCode.InvalidInput =>
            "The selected-window capture input or budget is invalid.",
        PerceptionFailureCode.InvalidBinding =>
            "The source, destination, session, epoch, configuration or frame does not match this action.",
        PerceptionFailureCode.AuthorizationRequired =>
            "A matching explicit authorization is required. Capture remains off.",
        PerceptionFailureCode.AuthorizationConsumed =>
            "This authorization was already used. Review and authorize a fresh action.",
        PerceptionFailureCode.Canceled =>
            "Selected-window capture was canceled and will not restart itself.",
        PerceptionFailureCode.DeadlineExceeded =>
            "The original authorization expired. Captured frame data was discarded.",
        PerceptionFailureCode.Busy =>
            "The previous capture still owns resources. No capture was queued.",
        PerceptionFailureCode.Disposed =>
            "This capture owner has been disposed.",
        PerceptionFailureCode.EnumerationFailed =>
            "Selected-window discovery failed. No source was selected or captured.",
        PerceptionFailureCode.SourceUnavailable =>
            "The exact enumerated window is unavailable. Enumerate and select it again.",
        PerceptionFailureCode.SourceClosed =>
            "The selected window closed. Capture stopped and retained frame data was discarded.",
        PerceptionFailureCode.SourceChanged =>
            "The native selected-window identity changed. Capture stopped without falling back.",
        PerceptionFailureCode.SourceSelectionChanged =>
            "The selected source changed. Capture stopped and requires fresh authorization.",
        PerceptionFailureCode.AccessDenied =>
            "Windows denied selected-window capture. Review privacy settings before retrying.",
        PerceptionFailureCode.DestinationChanged =>
            "The exact processing destination changed. Capture stopped and requires fresh authorization.",
        PerceptionFailureCode.ConfigurationChanged =>
            "The capture configuration changed. Capture stopped and requires fresh authorization.",
        PerceptionFailureCode.Paused =>
            "Perception was paused. Capture stopped and retained frame data was discarded.",
        PerceptionFailureCode.Locked =>
            "The session locked. Capture stopped and retained frame data was discarded.",
        PerceptionFailureCode.PayloadTooLarge =>
            "A frame exceeded its dimension or byte budget and was discarded.",
        PerceptionFailureCode.MalformedFrame =>
            "The native frame metadata or pixel length was malformed and was discarded.",
        PerceptionFailureCode.StaleFrame =>
            "The latest frame is no longer fresh and was discarded.",
        PerceptionFailureCode.FrameUnavailable =>
            "No matching fresh frame is available for this action.",
        PerceptionFailureCode.RateLimited =>
            "The hard selected-window capture rate has not elapsed. No capture was queued.",
        PerceptionFailureCode.SessionTimeout =>
            "The bounded capture session ended. It will not restart itself.",
        PerceptionFailureCode.NativeCaptureFailed =>
            "Selected-window capture failed. No desktop-wide or alternate-source fallback was used.",
        PerceptionFailureCode.CancellationFailed =>
            "Owned native cancellation failed. Keep this capture owner quarantined.",
        PerceptionFailureCode.CleanupFailed =>
            "Native resource cleanup could not be confirmed. Keep this capture owner quarantined.",
        PerceptionFailureCode.InvalidState =>
            "This capture or frame lease cannot accept that action.",
        _ => throw new ArgumentOutOfRangeException()
    };

    public string ActionId => Code switch
    {
        PerceptionFailureCode.NativePrivacyUnqualified => "perception.native_unqualified",
        PerceptionFailureCode.AuthorizationRequired or PerceptionFailureCode.AuthorizationConsumed =>
            "perception.review_consent",
        PerceptionFailureCode.SourceUnavailable or PerceptionFailureCode.SourceClosed or
            PerceptionFailureCode.SourceChanged or PerceptionFailureCode.SourceSelectionChanged =>
            "perception.select_window",
        PerceptionFailureCode.AccessDenied => "perception.check_privacy",
        PerceptionFailureCode.DestinationChanged or PerceptionFailureCode.ConfigurationChanged =>
            "perception.review_destination",
        PerceptionFailureCode.Busy or PerceptionFailureCode.CancellationFailed or
            PerceptionFailureCode.CleanupFailed => "perception.await_release",
        PerceptionFailureCode.RateLimited => "perception.wait_capture_interval",
        _ => "perception.review_capture"
    };

    public PerceptionFailure(PerceptionFailureCode code)
    {
        if (!Enum.IsDefined(code))
            throw new ArgumentOutOfRangeException(nameof(code));
        Code = code;
    }

    public override string ToString() =>
        $"{nameof(PerceptionFailure)} {{ Code = {Code}, ActionId = {ActionId} }}";
}

public sealed class PerceptionException : Exception
{
    public PerceptionFailure Failure { get; }

    public PerceptionException(PerceptionFailureCode code)
        : base(new PerceptionFailure(code).Summary) =>
        Failure = new(code);

    public override string ToString() =>
        $"{nameof(PerceptionException)} ({Failure.Code}): {Message}";
}

internal static class PerceptionGuard
{
    internal static void Require(
        bool condition,
        PerceptionFailureCode code = PerceptionFailureCode.InvalidInput)
    {
        if (!condition)
            throw new PerceptionException(code);
    }

    internal static void Defined<T>(T value) where T : struct, Enum =>
        Require(Enum.IsDefined(value));

    internal static void Utc(DateTimeOffset value) =>
        Require(value.Offset == TimeSpan.Zero && value.Year is >= 2000 and <= 2100);

    internal static void SafeLabel(string value, int maximumLength)
    {
        Require(value is { Length: > 0 } && value.Length <= maximumLength &&
            !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl));
    }
}
