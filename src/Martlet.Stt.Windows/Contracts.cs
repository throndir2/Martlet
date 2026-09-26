using System.Collections.Immutable;

namespace Martlet.Stt.Windows;

public sealed record WindowsOfflineRecognizer(string Id, string Name, string Culture);

public sealed record WindowsOfflineSttRequest(
    Guid OperationId, DateTimeOffset Deadline, string RecognizerId);

public enum WindowsOfflineSttOutcome { Completed, NoSpeech, Canceled, Failed }
public enum WindowsOfflineSttProvenance { NotRun, InstalledWindowsRecognizer, Fixture }
public enum WindowsOfflineSttFailureCode
{
    Busy, Quarantined, UnsupportedHost, InvalidRequest, AuthorizationMissing,
    AuthorizationMismatch, AuthorizationConsumed, DeadlineExceeded, RecognizerUnavailable,
    RecognitionFailed, TranscriptInvalid, TranscriptLimit, CleanupFailed
}

public sealed record WindowsOfflineSttFailure(
    WindowsOfflineSttFailureCode Code, string Summary, string Remedy)
{
    internal static WindowsOfflineSttFailure Create(WindowsOfflineSttFailureCode code) => code switch
    {
        WindowsOfflineSttFailureCode.Busy => new(code, "Local recognition still owns an action.",
            "Wait for recognition and cleanup to finish before trying again."),
        WindowsOfflineSttFailureCode.Quarantined or WindowsOfflineSttFailureCode.CleanupFailed =>
            new(code, "The installed recognizer did not release its resources.",
                "Close Martlet before retrying local recognition."),
        WindowsOfflineSttFailureCode.UnsupportedHost => new(code, "Windows offline recognition is unavailable on this host.",
            "Use an installed Windows desktop speech recognizer; no cloud fallback was attempted."),
        WindowsOfflineSttFailureCode.InvalidRequest => new(code, "The recognition request is invalid.",
            "Select an installed recognizer and authorize a fresh bounded utterance."),
        WindowsOfflineSttFailureCode.AuthorizationMissing => new(code, "Fresh local recognition permission is required.",
            "Review the selected installed recognizer and allow this utterance to be processed locally."),
        WindowsOfflineSttFailureCode.AuthorizationMismatch => new(code, "The permission does not match the utterance or recognizer.",
            "Discard the old permission and authorize the exact current selection and audio."),
        WindowsOfflineSttFailureCode.AuthorizationConsumed => new(code, "This recognition permission was already used.",
            "Authorize a fresh utterance."),
        WindowsOfflineSttFailureCode.DeadlineExceeded => new(code, "The local recognition deadline expired.",
            "Try a shorter utterance after owned cleanup completes."),
        WindowsOfflineSttFailureCode.RecognizerUnavailable => new(code, "The selected installed offline recognizer is unavailable.",
            "Explicitly check installed Windows speech recognizers and select a matching language. Martlet does not install language packs."),
        WindowsOfflineSttFailureCode.TranscriptInvalid => new(code, "The recognizer returned invalid text.",
            "Check the selected recognizer and try a fresh utterance; no partial text was accepted."),
        WindowsOfflineSttFailureCode.TranscriptLimit => new(code, "The recognition text exceeded its bound.",
            "Use a shorter utterance; no partial text was accepted."),
        _ => new(code, "The installed offline recognizer failed.",
            "Check the selected Windows speech recognizer and language. No alternate recognizer or cloud service was tried.")
    };
}

public sealed record WindowsOfflineRecognizerDiscovery(
    ImmutableArray<WindowsOfflineRecognizer> Recognizers,
    WindowsOfflineSttFailure? Failure = null,
    bool Canceled = false);

public sealed class WindowsOfflineSttResult
{
    public Guid OperationId { get; }
    public WindowsOfflineSttOutcome Outcome { get; }
    public string? Text { get; }
    public WindowsOfflineSttFailure? Failure { get; }
    public WindowsOfflineSttProvenance Provenance { get; }

    internal WindowsOfflineSttResult(Guid operationId, WindowsOfflineSttOutcome outcome,
        string? text = null, WindowsOfflineSttFailureCode? failure = null,
        WindowsOfflineSttProvenance provenance = WindowsOfflineSttProvenance.NotRun)
    {
        OperationId = operationId;
        Outcome = outcome;
        Text = text;
        Failure = failure is { } code ? WindowsOfflineSttFailure.Create(code) : null;
        Provenance = provenance;
    }

    public override string ToString() => nameof(WindowsOfflineSttResult);
}

public sealed class WindowsOfflineSttAuthorization(
    Guid operationId, DateTimeOffset deadline, string recognizerId,
    string audioSha256, int audioBytes, bool allowLocalRecognition)
{
    public Guid OperationId { get; } = operationId;
    public DateTimeOffset Deadline { get; } = deadline;
    public string RecognizerId { get; } = recognizerId;
    public string AudioSha256 { get; } = audioSha256;
    public int AudioBytes { get; } = audioBytes;
    public bool AllowLocalRecognition { get; } = allowLocalRecognition;
    private int consumed;
    internal bool TryConsume() => Interlocked.CompareExchange(ref consumed, 1, 0) == 0;
    public override string ToString() => nameof(WindowsOfflineSttAuthorization);
}
