using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Readiness;

public enum DesktopReadinessPurpose
{
    ActivationProbe,
    DesktopLaunch
}

public enum DesktopReadinessReporterFailure
{
    InvalidEnvironment,
    DeadlineExpired,
    AlreadyReported,
    Unavailable,
    Cancelled
}

public sealed class DesktopReadinessReporterException : Exception
{
    public DesktopReadinessReporterFailure Failure { get; }

    internal DesktopReadinessReporterException(DesktopReadinessReporterFailure failure)
        : base(failure switch
        {
            DesktopReadinessReporterFailure.InvalidEnvironment =>
                "The private launcher readiness environment is missing, malformed, or unsupported.",
            DesktopReadinessReporterFailure.DeadlineExpired =>
                "The private launcher readiness deadline has expired.",
            DesktopReadinessReporterFailure.AlreadyReported =>
                "This process has already made its single readiness report.",
            DesktopReadinessReporterFailure.Cancelled =>
                "The readiness report was cancelled before it was written.",
            _ => "The private launcher readiness channel is unavailable."
        }) => Failure = failure;
}

public sealed class DesktopReadinessReporter
{
    private const int MaximumMessageBytes = 4096;
    private readonly ReporterEnvironment environment;
    private int reported;

    public DesktopReadinessPurpose Purpose => environment.Purpose;
    public DateTimeOffset DeadlineUtc => environment.DeadlineUtc;

    private DesktopReadinessReporter(ReporterEnvironment environment) =>
        this.environment = environment;

    public static DesktopReadinessReporter CreateFromEnvironment() =>
        new(ReporterEnvironment.Read());

    public ValueTask ReportInitializedAsync(CancellationToken token = default) =>
        ReportAsync(ReadinessState.Initialized, token);

    public ValueTask ReportDegradedAsync(CancellationToken token = default) =>
        ReportAsync(ReadinessState.Degraded, token);

    public ValueTask ReportFailedAsync(CancellationToken token = default) =>
        ReportAsync(ReadinessState.Failed, token);

    private async ValueTask ReportAsync(ReadinessState state, CancellationToken token)
    {
        if (Interlocked.Exchange(ref reported, 1) != 0)
            throw new DesktopReadinessReporterException(
                DesktopReadinessReporterFailure.AlreadyReported);
        token.ThrowIfCancellationRequested();
        var remaining = environment.DeadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            throw new DesktopReadinessReporterException(
                DesktopReadinessReporterFailure.DeadlineExpired);
        var message = new ReadinessMessage(
            ReadinessProtocol.FormatVersion,
            environment.ProtocolVersion,
            environment.Nonce,
            environment.Purpose,
            environment.Version,
            environment.ProfileId,
            environment.SettingsRevision,
            environment.PayloadSha256,
            environment.ExecutableSha256,
            Environment.ProcessId,
            environment.DeadlineUtc,
            state);
        var bytes = ReadinessProtocol.Serialize(message);
        if (bytes.Length > MaximumMessageBytes)
            throw new DesktopReadinessReporterException(
                DesktopReadinessReporterFailure.InvalidEnvironment);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(remaining);
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                environment.PipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            var length = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            await pipe.WriteAsync(length, deadline.Token).ConfigureAwait(false);
            await pipe.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
            await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw new DesktopReadinessReporterException(
                DesktopReadinessReporterFailure.Cancelled);
        }
        catch (OperationCanceledException)
        {
            throw new DesktopReadinessReporterException(
                DesktopReadinessReporterFailure.DeadlineExpired);
        }
        catch (IOException)
        {
            throw new DesktopReadinessReporterException(
                DesktopReadinessReporterFailure.Unavailable);
        }
        catch (UnauthorizedAccessException)
        {
            throw new DesktopReadinessReporterException(
                DesktopReadinessReporterFailure.Unavailable);
        }
    }
}

internal sealed record ReporterEnvironment(
    string PipeName,
    string Nonce,
    int ProtocolVersion,
    DesktopReadinessPurpose Purpose,
    string Version,
    Guid ProfileId,
    string SettingsRevision,
    string PayloadSha256,
    string ExecutableSha256,
    DateTimeOffset DeadlineUtc)
{
    internal static ReporterEnvironment Read()
    {
        var pipeName = Required(ReadinessEnvironment.PipeName);
        var nonce = Required(ReadinessEnvironment.Nonce);
        var protocol = Required(ReadinessEnvironment.ProtocolVersion);
        var purpose = Required(ReadinessEnvironment.Purpose);
        var version = Required(ReadinessEnvironment.Version);
        var profile = Required(ReadinessEnvironment.ProfileId);
        var settings = Required(ReadinessEnvironment.SettingsRevision);
        var payload = Required(ReadinessEnvironment.PayloadSha256);
        var executable = Required(ReadinessEnvironment.ExecutableSha256);
        var deadline = Required(ReadinessEnvironment.DeadlineUtc);
        if (!ReadinessProtocol.ValidPipeName(pipeName) ||
            !ReadinessProtocol.IsLowerHex(nonce, 64) ||
            protocol != ReadinessProtocol.ProtocolVersion.ToString(CultureInfo.InvariantCulture) ||
            !Enum.TryParse<DesktopReadinessPurpose>(purpose, ignoreCase: false, out var parsedPurpose) ||
            !Enum.IsDefined(parsedPurpose) || parsedPurpose.ToString() != purpose ||
            !ReadinessProtocol.ValidVersion(version) ||
            !Guid.TryParseExact(profile, "D", out var profileId) ||
            profileId == Guid.Empty ||
            !ReadinessProtocol.ValidSettingsRevision(settings) ||
            !ReadinessProtocol.IsLowerHex(payload, 64) ||
            !ReadinessProtocol.IsLowerHex(executable, 64) ||
            !DateTimeOffset.TryParseExact(deadline, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var deadlineUtc) ||
            deadlineUtc <= DateTimeOffset.UtcNow ||
            deadlineUtc - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(30))
            throw new DesktopReadinessReporterException(
                DesktopReadinessReporterFailure.InvalidEnvironment);
        return new(pipeName, nonce, ReadinessProtocol.ProtocolVersion, parsedPurpose,
            version, profileId, settings, payload, executable, deadlineUtc);
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 and <= 256 } value
            ? value
            : throw new DesktopReadinessReporterException(
                DesktopReadinessReporterFailure.InvalidEnvironment);
}

internal enum ReadinessState
{
    Initialized,
    Degraded,
    Failed
}

internal sealed record ReadinessMessage(
    int FormatVersion,
    int ProtocolVersion,
    string Nonce,
    DesktopReadinessPurpose Purpose,
    string Version,
    Guid ProfileId,
    string SettingsRevision,
    string PayloadSha256,
    string ExecutableSha256,
    int ProcessId,
    DateTimeOffset DeadlineUtc,
    ReadinessState State);

internal static class ReadinessEnvironment
{
    internal const string PipeName = "MARTLET_READINESS_PIPE";
    internal const string Nonce = "MARTLET_READINESS_NONCE";
    internal const string ProtocolVersion = "MARTLET_READINESS_PROTOCOL";
    internal const string Purpose = "MARTLET_READINESS_PURPOSE";
    internal const string Version = "MARTLET_READINESS_VERSION";
    internal const string ProfileId = "MARTLET_READINESS_PROFILE";
    internal const string SettingsRevision = "MARTLET_READINESS_SETTINGS_REVISION";
    internal const string PayloadSha256 = "MARTLET_READINESS_PAYLOAD_SHA256";
    internal const string ExecutableSha256 = "MARTLET_READINESS_EXECUTABLE_SHA256";
    internal const string DeadlineUtc = "MARTLET_READINESS_DEADLINE_UTC";
}

internal static class ReadinessProtocol
{
    internal const int FormatVersion = 1;
    internal const int ProtocolVersion = 1;
    internal const int MaximumMessageBytes = 4096;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        MaxDepth = 4
    };

    internal static byte[] Serialize(ReadinessMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, Json);

    internal static bool IsLowerHex(string? value, int length) =>
        value is not null && value.Length == length &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool ValidSettingsRevision(string? value) =>
        value is { Length: > 0 and <= 128 } && !value.Any(char.IsControl);

    internal static bool ValidVersion(string? value) =>
        value is { Length: > 0 and <= 48 } &&
        Version.TryParse(value, out var parsed) &&
        parsed.Revision >= 0 &&
        value == parsed.ToString(4);

    internal static bool ValidPipeName(string? value) =>
        value is { Length: >= 32 and <= 128 } &&
        value.StartsWith("martlet-rdy-", StringComparison.Ordinal) &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
