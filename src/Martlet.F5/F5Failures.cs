using System.Text;

namespace Martlet.F5;

public enum F5Failure
{
    InvalidData,
    UnsupportedVersion,
    LimitExceeded,
    InvalidPath,
    SourceMissing,
    SourceChanged,
    InvalidAudio,
    RightsRequired,
    AccessDenied,
    IoFailure,
    CorruptStore,
    NotFound,
    Conflict,
    Busy,
    Closed,
    AuthorizationRequired,
    AuthorizationMismatch,
    AuthorizationExpired,
    AuthorizationConsumed,
    DeadlineExceeded,
    Canceled,
    IdentityMismatch,
    ProtocolViolation,
    StreamTruncated,
    InvalidFrame,
    WorkerFailed,
    CacheInvalidationFailed,
    OutputFailed
}

public sealed class F5Exception : Exception
{
    internal F5Exception(F5Failure failure)
        : base(MessageFor(failure))
    {
        Failure = failure;
    }

    public F5Failure Failure { get; }

    private static string MessageFor(F5Failure failure) => failure switch
    {
        F5Failure.InvalidPath => "Select a bounded absolute path on an allowed local volume without links.",
        F5Failure.SourceMissing => "The selected reference source is missing. Select or reload it explicitly.",
        F5Failure.SourceChanged => "The reference source changed after validation. Snapshot and apply it again.",
        F5Failure.InvalidAudio => "The reference must be a supported, readable, bounded PCM WAV file.",
        F5Failure.RightsRequired => "Confirm the reference-voice rights and exact processing destination.",
        F5Failure.AuthorizationRequired => "Fresh explicit authorization is required for this exact action.",
        F5Failure.AuthorizationMismatch => "The authorization does not match this exact action.",
        F5Failure.AuthorizationExpired => "The authorization or action deadline expired.",
        F5Failure.AuthorizationConsumed => "The one-use authorization was already consumed.",
        F5Failure.IdentityMismatch => "The worker identity does not match the exact authorized runtime and artifacts.",
        F5Failure.StreamTruncated => "The worker stream ended without a valid terminal response.",
        F5Failure.InvalidFrame => "The worker emitted invalid or non-contiguous PCM metadata.",
        F5Failure.CacheInvalidationFailed => "The worker did not acknowledge the required conditioning-cache invalidation.",
        _ => "The F5 voice foundation rejected the operation."
    };
}

internal static class F5Guard
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static void Require(bool condition, F5Failure failure = F5Failure.InvalidData)
    {
        if (!condition)
            throw new F5Exception(failure);
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
            !value.Equals("unpinned", StringComparison.OrdinalIgnoreCase));
    }

    internal static void Revision(string? value) =>
        Require(value is { Length: 40 } && value.All(IsLowerHex));

    internal static void Sha256(string? value) =>
        Require(value is { Length: 64 } && value.All(IsLowerHex));

    internal static int Utf8Text(string? value, int maximumCharacters, int maximumBytes,
        bool allowNewLines = true)
    {
        Require(value is { Length: > 0 } && value.Length <= maximumCharacters &&
            !string.IsNullOrWhiteSpace(value) && !value.Contains('\0'));
        Require(value!.All(character => !char.IsControl(character) ||
            (allowNewLines && character is '\r' or '\n' or '\t')));
        try
        {
            var bytes = StrictUtf8.GetByteCount(value!);
            Require(bytes <= maximumBytes, F5Failure.LimitExceeded);
            return bytes;
        }
        catch (EncoderFallbackException)
        {
            throw new F5Exception(F5Failure.InvalidData);
        }
    }

    internal static bool FixedTimeEquals(string left, string right)
    {
        if (left.Length != right.Length)
            return false;
        var difference = 0;
        for (var index = 0; index < left.Length; index++)
            difference |= left[index] ^ right[index];
        return difference == 0;
    }

    private static bool IsLowerHex(char character) =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f';
}
