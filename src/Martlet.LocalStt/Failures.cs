namespace Martlet.LocalStt;

public enum LocalSttOutcome
{
    Completed,
    NoSpeech,
    Failed,
    Canceled,
    DeadlineExceeded
}

public enum LocalSttFailureCode
{
    Busy,
    Quarantined,
    UnsupportedHost,
    PackageMissing,
    PackageChanged,
    PackageUnsafePath,
    PackageAccessDenied,
    PackageInvalid,
    AuthorizationMissing,
    AuthorizationMismatch,
    AuthorizationExpired,
    AuthorizationConsumed,
    AudioFormatUnsupported,
    AudioLimit,
    DiskFull,
    WorkspaceAccessDenied,
    WorkspaceIo,
    EgressUnavailable,
    EgressViolation,
    ProcessStartFailed,
    ProcessOutputLimit,
    ProcessOutputMalformed,
    ProcessFailed,
    ProcessCleanupFailed,
    TranscriptMissing,
    TranscriptMalformed,
    TranscriptLimit,
    DeadlineExceeded
}

public sealed record LocalSttFailure(LocalSttFailureCode Code, string Summary, string Remedy)
{
    internal static LocalSttFailure Create(LocalSttFailureCode code) => code switch
    {
        LocalSttFailureCode.Busy => new(code,
            "Another local transcription action still owns the adapter.",
            "Wait for owned cleanup, then authorize a fresh action."),
        LocalSttFailureCode.Quarantined => new(code,
            "A prior local transcription process or private workspace did not clean up.",
            "Close Martlet and inspect the named package outside this adapter before trying again."),
        LocalSttFailureCode.UnsupportedHost => new(code,
            "This package candidate is not supported on the current operating system or architecture.",
            "Use the exact declared Windows x64 candidate on a separately qualified CPU."),
        LocalSttFailureCode.PackageMissing => new(code,
            "A required local transcription package artifact is missing.",
            "Re-provision the exact pinned archive, runtime files and model with explicit approval."),
        LocalSttFailureCode.PackageChanged => new(code,
            "The local transcription executable, runtime archive, dependency or model changed.",
            "Stop and verify or re-provision the exact pinned package; no fallback was attempted."),
        LocalSttFailureCode.PackageUnsafePath => new(code,
            "The local transcription package uses a disallowed link, reparse point or non-local path.",
            "Move a freshly verified package to a normal local directory without links or aliases."),
        LocalSttFailureCode.PackageAccessDenied => new(code,
            "The local transcription package could not be read with the current user.",
            "Review the package location and current-user file permissions; do not elevate Martlet."),
        LocalSttFailureCode.PackageInvalid => new(code,
            "The local transcription package does not match its bounded manifest.",
            "Re-provision the exact package and keep all undeclared runtime files out of its runtime directory."),
        LocalSttFailureCode.AuthorizationMissing => new(code,
            "Fresh one-use authorization for local audio processing is required.",
            "Review the exact model, language, temporary-file and denied-egress boundaries, then authorize one action."),
        LocalSttFailureCode.AuthorizationMismatch => new(code,
            "The local audio authorization does not match this action, package, model, language or audio.",
            "Discard it and authorize the exact current action."),
        LocalSttFailureCode.AuthorizationExpired => new(code,
            "The local audio authorization or action deadline expired.",
            "Authorize a fresh bounded action."),
        LocalSttFailureCode.AuthorizationConsumed => new(code,
            "The one-use local audio authorization was already consumed.",
            "Authorize a fresh bounded action."),
        LocalSttFailureCode.AudioFormatUnsupported => new(code,
            "Local transcription requires canonical mono 16 kHz PCM16 WAV audio.",
            "Convert the bounded utterance through the canonical Martlet audio path."),
        LocalSttFailureCode.AudioLimit => new(code,
            "The local utterance exceeds the byte or duration limit.",
            "Capture a shorter utterance and authorize a fresh action."),
        LocalSttFailureCode.DiskFull => new(code,
            "The private temporary workspace could not be written because storage is full.",
            "Free space on the current user's temporary volume, then authorize a fresh action."),
        LocalSttFailureCode.WorkspaceAccessDenied => new(code,
            "The private temporary workspace is unavailable to the current user.",
            "Review current-user temporary-directory permissions; do not elevate Martlet."),
        LocalSttFailureCode.WorkspaceIo => new(code,
            "The private temporary workspace failed.",
            "Close Martlet, inspect the current user's temporary storage, and do not reuse this authorization."),
        LocalSttFailureCode.EgressUnavailable => new(code,
            "Denied-egress observation was not established before process launch.",
            "Keep local STT disabled until an approved process-tree egress guard is available."),
        LocalSttFailureCode.EgressViolation => new(code,
            "The denied-egress audit was incomplete or observed disallowed network activity.",
            "Keep the package disabled and review the exact process-tree privacy evidence."),
        LocalSttFailureCode.ProcessStartFailed => new(code,
            "The verified local transcription process could not start.",
            "Review the exact package and current-user execution policy; no alternate executable was tried."),
        LocalSttFailureCode.ProcessOutputLimit => new(code,
            "The local transcription process exceeded a bounded output pipe.",
            "Keep the package disabled and review the exact pinned runtime."),
        LocalSttFailureCode.ProcessOutputMalformed => new(code,
            "The local transcription process emitted malformed or unexpected diagnostic output.",
            "Keep the package disabled and review the exact pinned runtime."),
        LocalSttFailureCode.ProcessFailed => new(code,
            "The local transcription process failed.",
            "Review the safe exit metadata and exact package; no retry or provider fallback was attempted."),
        LocalSttFailureCode.ProcessCleanupFailed => new(code,
            "The owned local transcription process tree did not stop cleanly.",
            "Close Martlet before inspecting or retrying the local package."),
        LocalSttFailureCode.TranscriptMissing => new(code,
            "The local transcription process did not create its exact owned result file.",
            "Keep the package disabled and review the exact pinned command contract."),
        LocalSttFailureCode.TranscriptMalformed => new(code,
            "The local transcription result is not valid bounded UTF-8 text.",
            "Keep the package disabled and review the exact pinned runtime and model."),
        LocalSttFailureCode.TranscriptLimit => new(code,
            "The local transcription result exceeded its text bound.",
            "Use a shorter utterance; no partial transcript was accepted."),
        LocalSttFailureCode.DeadlineExceeded => new(code,
            "The local transcription deadline expired.",
            "Authorize a fresh action after reviewing CPU suitability; late output was discarded."),
        _ => throw new ArgumentOutOfRangeException(nameof(code))
    };
}

public sealed class LocalSttContractException : Exception
{
    public LocalSttFailure Failure { get; }

    internal LocalSttContractException(LocalSttFailureCode code)
        : base(LocalSttFailure.Create(code).Summary)
    {
        Failure = LocalSttFailure.Create(code);
    }
}

public sealed class LocalSttResult
{
    public Guid OperationId { get; }
    public LocalSttOutcome Outcome { get; }
    public string? Text { get; }
    public LocalSttFailure? Failure { get; }
    public LocalSttPrivacyEvidence? PrivacyEvidence { get; }
    public int StandardOutputBytes { get; }
    public int StandardErrorBytes { get; }
    public int? ProcessExitCode { get; }

    internal LocalSttResult(
        Guid operationId,
        LocalSttOutcome outcome,
        string? text = null,
        LocalSttFailure? failure = null,
        LocalSttPrivacyEvidence? privacyEvidence = null,
        int standardOutputBytes = 0,
        int standardErrorBytes = 0,
        int? processExitCode = null)
    {
        OperationId = operationId;
        Outcome = outcome;
        Text = text;
        Failure = failure;
        PrivacyEvidence = privacyEvidence;
        StandardOutputBytes = standardOutputBytes;
        StandardErrorBytes = standardErrorBytes;
        ProcessExitCode = processExitCode;
    }

    internal static LocalSttResult Failed(Guid operationId, LocalSttFailureCode code) =>
        new(operationId, code == LocalSttFailureCode.DeadlineExceeded
            ? LocalSttOutcome.DeadlineExceeded
            : LocalSttOutcome.Failed, failure: LocalSttFailure.Create(code));

    internal static LocalSttResult Canceled(Guid operationId) =>
        new(operationId, LocalSttOutcome.Canceled);

    internal LocalSttResult WithExecution(
        LocalSttPrivacyEvidence? privacyEvidence,
        int standardOutputBytes,
        int standardErrorBytes,
        int? processExitCode) =>
        new(
            OperationId,
            Outcome,
            Text,
            Failure,
            privacyEvidence,
            standardOutputBytes,
            standardErrorBytes,
            processExitCode);

    public override string ToString() => $"{nameof(LocalSttResult)}: {Outcome}";
}
