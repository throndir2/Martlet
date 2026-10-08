using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Martlet.Gateway;

public enum GatewayRole
{
    Voice,
    Perception,
    Memory
}

/// <summary>What a paired device may use on this host, fixed when its credential is issued. <see cref="Full"/>: the owner's own
/// computers (pairing, member pairing, the owner account and identities allowed as the owner's computers). <see cref="Friend"/>:
/// a computer of someone the owner shares the host with (an identity allowed as a friend): it reaches only the routes that
/// admit friends (the host's engines, cancel, version, capabilities and status), never joins the network, and the owner's own
/// requests stop its work.</summary>
public enum GatewayAccess
{
    Full,
    Friend
}

public enum GatewayWorkerKind
{
    OllamaLlm,
    F5Tts,
    Stt,
    Vision,
    Memory
}

public enum GatewayWorkerState
{
    Disabled,
    Starting,
    Ready,
    Busy,
    Failed
}

public enum GatewayCancellationCapability
{
    DiscardOnly,
    RequestAbort,
    CooperativeComputeCancel
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GatewayProtocolVersion
{
    public required int Major { get; init; }
    public required int Minor { get; init; }

    public static GatewayProtocolVersion Current => new() { Major = 2, Minor = 0 };

    internal void Validate()
    {
        GatewayRules.Require(Major == 2, "protocol.unsupported");
        GatewayRules.Require(Minor is >= 0 and <= 9999, "request.invalid");
    }
}

public sealed record GatewayWorkerCapabilities
{
    public required string WorkerId { get; init; }
    public required GatewayWorkerKind Kind { get; init; }
    public required GatewayRole RequiredRole { get; init; }
    public required string AdapterVersion { get; init; }
    public required string ModelId { get; init; }
    public string? ModelRevision { get; init; }
    public required int MaxInputBytes { get; init; }
    public required int MaxConcurrency { get; init; }
    public required bool TextDeltas { get; init; }
    public required bool AudioTransport { get; init; }
    public required GatewayCancellationCapability Cancellation { get; init; }

    internal void Validate()
    {
        GatewayRules.Identifier(WorkerId);
        GatewayRules.Defined(Kind);
        GatewayRules.Defined(RequiredRole);
        GatewayRules.Identifier(AdapterVersion);
        GatewayRules.Token(ModelId, 128);
        if (ModelRevision is not null)
            GatewayRules.Token(ModelRevision, 128);
        GatewayRules.Require(MaxInputBytes is > 0 and <= 16_777_216, "worker.invalid");
        GatewayRules.Require(MaxConcurrency is > 0 and <= 64, "worker.invalid");
        GatewayRules.Defined(Cancellation);
        GatewayRules.Require(Kind == GatewayWorkerKind.OllamaLlm || !TextDeltas, "worker.invalid");
        GatewayRules.Require(Kind == GatewayWorkerKind.F5Tts || !AudioTransport, "worker.invalid");
        GatewayRules.Require(Kind switch
        {
            GatewayWorkerKind.OllamaLlm or GatewayWorkerKind.F5Tts or GatewayWorkerKind.Stt =>
                RequiredRole == GatewayRole.Voice,
            GatewayWorkerKind.Vision => RequiredRole == GatewayRole.Perception,
            GatewayWorkerKind.Memory => RequiredRole == GatewayRole.Memory,
            _ => false
        }, "worker.invalid");
    }
}

public sealed record GatewayWorkerStatus
{
    public required GatewayWorkerState State { get; init; }
    public required int QueueDepth { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public string? FailureCode { get; init; }

    internal void Validate(DateTimeOffset now)
    {
        GatewayRules.Defined(State);
        GatewayRules.Require(QueueDepth is >= 0 and <= 1024, "worker.invalid");
        GatewayRules.Require(ObservedAt >= now - TimeSpan.FromDays(7) &&
            ObservedAt <= now + TimeSpan.FromMinutes(2), "worker.invalid");
        GatewayRules.Require((State == GatewayWorkerState.Failed) == (FailureCode is not null), "worker.invalid");
        if (FailureCode is not null)
            GatewayRules.Identifier(FailureCode);
    }
}

public interface IGatewayWorker
{
    GatewayWorkerCapabilities Capabilities { get; }
    ValueTask<GatewayWorkerStatus> ReadStatusAsync(CancellationToken cancellationToken);
}

public sealed class GatewayWorkerUnavailableException : Exception
{
    public GatewayWorkerUnavailableException() : base("The private worker did not provide bounded status.")
    {
    }
}

public sealed class GatewayWorkerRegistry
{
    public const int MaximumWorkers = 16;
    private readonly WorkerRegistration[] workers;

    public GatewayWorkerRegistry(IEnumerable<IGatewayWorker> workers)
    {
        ArgumentNullException.ThrowIfNull(workers);
        var supplied = workers.ToArray();
        GatewayRules.Require(supplied.Length <= MaximumWorkers, "worker.invalid");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var registrations = new List<WorkerRegistration>(supplied.Length);
        foreach (var worker in supplied)
        {
            ArgumentNullException.ThrowIfNull(worker);
            var capabilities = worker.Capabilities;
            ArgumentNullException.ThrowIfNull(capabilities);
            capabilities.Validate();
            GatewayRules.Require(ids.Add(capabilities.WorkerId), "worker.invalid");
            registrations.Add(new(worker, capabilities));
        }
        this.workers = registrations.ToArray();
    }

    internal GatewayWorkerCapabilities[] CapabilitiesFor(GatewayRole role) =>
        workers.Where(worker => worker.Capabilities.RequiredRole == role)
            .Select(worker => worker.Capabilities).ToArray();

    internal async ValueTask<GatewayWorkerStatusDocument[]> StatusForAsync(
        GatewayRole role,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var selected = workers.Where(worker => worker.Capabilities.RequiredRole == role).ToArray();
        var statuses = new GatewayWorkerStatusDocument[selected.Length];
        for (var index = 0; index < selected.Length; index++)
        {
            GatewayWorkerStatus status;
            try
            {
                status = await selected[index].Worker.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (GatewayWorkerUnavailableException)
            {
                throw new GatewayProtocolException("worker.unavailable");
            }

            status.Validate(now);
            statuses[index] = new()
            {
                WorkerId = selected[index].Capabilities.WorkerId,
                State = status.State,
                QueueDepth = status.QueueDepth,
                ObservedAt = status.ObservedAt,
                AgeMilliseconds = Math.Max(0, Convert.ToInt64(
                    (now - status.ObservedAt).TotalMilliseconds, CultureInfo.InvariantCulture)),
                FailureCode = status.FailureCode
            };
        }

        return statuses;
    }

    private sealed record WorkerRegistration(
        IGatewayWorker Worker,
        GatewayWorkerCapabilities Capabilities);
}

internal sealed record GatewayWorkerStatusDocument
{
    public required string WorkerId { get; init; }
    public required GatewayWorkerState State { get; init; }
    public required int QueueDepth { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public required long AgeMilliseconds { get; init; }
    public string? FailureCode { get; init; }
}

internal static partial class GatewayRules
{
    internal const int MaximumResponseBytes = 65_536;

    internal static void Require(bool condition, string code)
    {
        if (!condition)
            throw new GatewayProtocolException(code);
    }

    internal static void Defined<T>(T value) where T : struct, Enum =>
        Require(Enum.IsDefined(value), "request.invalid");

    internal static void Identifier(string? value) => Identifier(value, 64);

    internal static void Identifier(string? value, int maximum) =>
        Require(maximum is > 0 and <= 128 &&
            value is { Length: > 0 } &&
            value.Length <= maximum &&
            IdentifierPattern().IsMatch(value), "request.invalid");

    internal static void Sha256(string? value) =>
        Require(value is { Length: 64 } &&
            value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "request.invalid");

    internal static void Sha256Fingerprint(string? value)
    {
        Require(value is { Length: 71 } &&
            value.StartsWith("sha256:", StringComparison.Ordinal), "request.invalid");
        Sha256(value![7..]);
    }

    internal static void Token(string? value, int maximum)
    {
        Require(value is { Length: > 0 } && value.Length <= maximum, "request.invalid");
        var text = value!;
        try
        {
            Require(new UTF8Encoding(false, true).GetByteCount(text) <= maximum &&
                !text.Any(char.IsControl), "request.invalid");
        }
        catch (EncoderFallbackException)
        {
            throw new GatewayProtocolException("request.invalid");
        }
    }

    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9._-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
