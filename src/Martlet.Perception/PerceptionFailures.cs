using System.Text;

namespace Martlet.Perception;

public enum PerceptionWorkerFailure
{
    InvalidData,
    UnsupportedVersion,
    LimitExceeded,
    InvalidImage,
    InvalidFrameReference,
    PermissionRequired,
    PermissionMismatch,
    PermissionExpired,
    PermissionConsumed,
    DestinationMismatch,
    IdentityMismatch,
    RoleMismatch,
    ClockSkew,
    StaleFrame,
    StaleResult,
    LateEpoch,
    Superseded,
    DeadlineExceeded,
    Canceled,
    ResourceBudgetExceeded,
    ResourceUnavailable,
    AuthenticationFailed,
    HostUnavailable,
    RedirectRejected,
    ProtocolViolation,
    UnknownOutput,
    WorkerFailed,
    ModelNotReady,
    ResourceExhausted,
    Closed
}

public sealed record PerceptionWorkerFailureDetails(
    string Code,
    string Summary,
    string Remedy);

public static class PerceptionWorkerFailureCatalog
{
    public static PerceptionWorkerFailureDetails Get(PerceptionWorkerFailure failure) => failure switch
    {
        PerceptionWorkerFailure.InvalidData => Details("perception.invalid_data",
            "The perception request contains invalid bounded data.",
            "Create a fresh request from the selected-window preview and the configured role."),
        PerceptionWorkerFailure.UnsupportedVersion => Details("perception.version",
            "The perception worker contract version is unsupported.",
            "Use the exact Martlet perception worker major version; do not use a generic compatibility route."),
        PerceptionWorkerFailure.LimitExceeded => Details("perception.limit",
            "A perception request or result exceeds its fixed bound.",
            "Reduce the selected frame or task output; never truncate or widen limits silently."),
        PerceptionWorkerFailure.InvalidImage => Details("perception.image_invalid",
            "The selected frame is not a supported bounded PNG image.",
            "Capture a fresh selected-window RGB/RGBA PNG; do not fall back to the full screen."),
        PerceptionWorkerFailure.InvalidFrameReference => Details("perception.frame_reference",
            "The ephemeral frame reference is malformed, expired, or path-like.",
            "Create a new bounded gateway frame reference; never supply a URL or filesystem path."),
        PerceptionWorkerFailure.PermissionRequired => Details("perception.permission_required",
            "Fresh explicit vision permission is required for this exact job.",
            "Review the selected window, destination, role, model, frame, and expiry, then authorize this job."),
        PerceptionWorkerFailure.PermissionMismatch => Details("perception.permission_mismatch",
            "The vision permission does not match this exact job.",
            "Discard the old permission and authorize the current destination, frame, epoch, role, and model."),
        PerceptionWorkerFailure.PermissionExpired => Details("perception.permission_expired",
            "The vision permission expired before dispatch.",
            "Preview the current selected window and issue a fresh bounded permission."),
        PerceptionWorkerFailure.PermissionConsumed => Details("perception.permission_consumed",
            "The one-use vision permission was already consumed.",
            "Issue a fresh permission for a new request; never replay an old job."),
        PerceptionWorkerFailure.DestinationMismatch => Details("perception.destination",
            "The job or gateway response does not match the configured host-2 destination.",
            "Use only the explicitly paired perception destination and reauthorize after any route change."),
        PerceptionWorkerFailure.IdentityMismatch => Details("perception.identity",
            "The worker, runtime, model, or artifact identity differs from the selected pin.",
            "Keep the route unavailable until the exact configured identities are restored or explicitly changed."),
        PerceptionWorkerFailure.RoleMismatch => Details("perception.role",
            "The requested OCR/VLM role does not match the selected worker or output.",
            "Select an exact worker for the requested role; do not reinterpret another role's output."),
        PerceptionWorkerFailure.ClockSkew => Details("perception.clock",
            "The perception request uses a creation or capture time too far in the future.",
            "Correct the local clock and create a fresh selected-window frame and permission."),
        PerceptionWorkerFailure.StaleFrame => Details("perception.frame_stale",
            "The selected frame is too old for this job.",
            "Capture and authorize a fresh frame; never describe an old frame as current state."),
        PerceptionWorkerFailure.StaleResult => Details("perception.result_stale",
            "The worker result expired before it could be accepted.",
            "Discard it and request a fresh observation only if vision permission is still active."),
        PerceptionWorkerFailure.LateEpoch => Details("perception.epoch_late",
            "The job epoch is older than the latest submitted frame.",
            "Discard the late job; never replay perception work from an earlier frame."),
        PerceptionWorkerFailure.Superseded => Details("perception.superseded",
            "A newer frame superseded this job.",
            "Use only the newest accepted observation; do not build a perception backlog."),
        PerceptionWorkerFailure.DeadlineExceeded => Details("perception.deadline",
            "The host-2 perception deadline expired.",
            "Continue without optional vision context and inspect host-2 readiness before a deliberate retry."),
        PerceptionWorkerFailure.Canceled => Details("perception.canceled",
            "The perception job was canceled and its output was discarded.",
            "Start a fresh authorized job only if vision context is still wanted."),
        PerceptionWorkerFailure.ResourceBudgetExceeded => Details("perception.resource_budget",
            "The selected worker cannot fit the configured perception budget.",
            "Choose an explicitly qualified smaller model or increase the budget only after measured hardware review."),
        PerceptionWorkerFailure.ResourceUnavailable => Details("perception.resource_busy",
            "The bounded perception budget is currently in use.",
            "Continue voice without vision context or submit one newer frame later; do not queue a backlog."),
        PerceptionWorkerFailure.AuthenticationFailed => Details("perception.auth",
            "The scoped host-2 perception authentication failed.",
            "Repair or re-pair the exact perception credential; never bypass TLS or role checks."),
        PerceptionWorkerFailure.HostUnavailable => Details("perception.host_unavailable",
            "The configured host-2 perception route is unavailable.",
            "Continue without optional vision context and repair only the selected paired host."),
        PerceptionWorkerFailure.RedirectRejected => Details("perception.redirect",
            "The perception transport attempted a redirect.",
            "Use the exact paired host-2 destination; never follow or authorize another origin implicitly."),
        PerceptionWorkerFailure.ProtocolViolation => Details("perception.protocol",
            "The worker returned a malformed or contradictory response.",
            "Keep that worker unavailable and repair its exact Martlet perception contract."),
        PerceptionWorkerFailure.UnknownOutput => Details("perception.output_unknown",
            "The worker returned an unknown or unsupported output shape.",
            "Reject the output and update the named adapter deliberately; never guess or coerce it."),
        PerceptionWorkerFailure.WorkerFailed => Details("perception.worker_failed",
            "The selected perception worker reported a bounded failure.",
            "Inspect the named worker state and retry deliberately only after it is ready."),
        PerceptionWorkerFailure.ModelNotReady => Details("perception.model_not_ready",
            "The exact selected model is not ready.",
            "Wait for that model or choose another explicitly qualified model; never download or fall back automatically."),
        PerceptionWorkerFailure.ResourceExhausted => Details("perception.worker_resource",
            "The worker exhausted its declared resources.",
            "Continue without vision context and review measured CPU/GPU limits before another attempt."),
        PerceptionWorkerFailure.Closed => Details("perception.closed",
            "The perception scheduler is closed.",
            "Create a fresh scheduler only for an explicitly enabled vision session."),
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    };

    private static PerceptionWorkerFailureDetails Details(string code, string summary, string remedy) =>
        new(code, summary, remedy);
}

public sealed class PerceptionWorkerException : Exception
{
    internal PerceptionWorkerException(PerceptionWorkerFailure failure)
        : base(PerceptionWorkerFailureCatalog.Get(failure).Summary)
    {
        Failure = failure;
    }

    public PerceptionWorkerFailure Failure { get; }
}

internal static class PerceptionWorkerGuard
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static void Require(
        bool condition,
        PerceptionWorkerFailure failure = PerceptionWorkerFailure.InvalidData)
    {
        if (!condition)
            throw new PerceptionWorkerException(failure);
    }

    internal static void Defined<T>(T value) where T : struct, Enum =>
        Require(Enum.IsDefined(value));

    internal static void Utc(DateTimeOffset value) =>
        Require(value.Offset == TimeSpan.Zero);

    internal static void Identifier(string? value, int maximum = 64)
    {
        Require(value is { Length: > 0 } && value.Length <= maximum &&
            char.IsAsciiLetterOrDigit(value[0]) &&
            value.All(character => char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '_' or '.'));
    }

    internal static void ExactVersion(string? value)
    {
        Require(value is { Length: > 0 and <= 64 } &&
            value.All(character => char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '-' or '+'));
        Require(!value!.Equals("main", StringComparison.OrdinalIgnoreCase) &&
            !value.Equals("latest", StringComparison.OrdinalIgnoreCase) &&
            !value.Equals("unknown", StringComparison.OrdinalIgnoreCase) &&
            !value.Equals("unpinned", StringComparison.OrdinalIgnoreCase) &&
            !value.Equals("floating", StringComparison.OrdinalIgnoreCase));
    }

    internal static void Revision(string? value) =>
        Require(value is { Length: 40 } && value.All(IsLowerHex));

    internal static void Sha256(string? value) =>
        Require(value is { Length: 64 } && value.All(IsLowerHex));

    internal static int Utf8Text(
        string? value,
        int maximumCharacters,
        int maximumBytes,
        bool allowNewLines = true)
    {
        Require(value is { Length: > 0 } && value.Length <= maximumCharacters &&
            !string.IsNullOrWhiteSpace(value) && !value.Contains('\0'));
        Require(value!.All(character => !char.IsControl(character) ||
            (allowNewLines && character is '\r' or '\n' or '\t')));
        try
        {
            var bytes = StrictUtf8.GetByteCount(value!);
            Require(bytes <= maximumBytes, PerceptionWorkerFailure.LimitExceeded);
            return bytes;
        }
        catch (EncoderFallbackException)
        {
            throw new PerceptionWorkerException(PerceptionWorkerFailure.InvalidData);
        }
    }

    internal static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    private static bool IsLowerHex(char character) =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f';
}
