using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>One route a paired host offers (one per installed role), as advertised by its signed capabilities.
/// <paramref name="MaximumConcurrency"/> is how many requests it runs at once: the Deep thinking role's thinks at once (its
/// slots), one for every other route and for hosts that don't say. <see cref="Gpus"/> names the graphics cards that serve it
/// and <see cref="Lane"/> whether it is a live route or a pool route, when the host says (older hosts don't).</summary>
public sealed record HostRoute(
    string RouteId, string Path, string ContractId, string ContractVersion, string DestinationId, string WorkerId,
    string AdapterVersion, string ModelId, string ModelRevision, string ModelSha256, string ArtifactIdentitySha256,
    int MaximumRequestBytes, int MaximumInputBytes, int MaximumOutputBytes, int MaximumEventBytes, int MaximumEvents,
    int MaximumStreamBytes, TimeSpan MaximumDuration, string Cancellation, int MaximumConcurrency = 1)
{
    public const string OllamaChatRouteId = SelfHostSetup.OllamaRouteId;
    public const string OllamaChatPath = SelfHostSetup.OllamaPath;
    /// <summary>The Deep thinking role's own Ollama: the same chat contract on a route and path of its own.</summary>
    public const string DeepThinkingRouteId = SelfHostSetup.DeepThinkingRouteId;
    public const string DeepThinkingPath = SelfHostSetup.DeepThinkingPath;
    public const string F5RouteId = "martlet.gateway.f5-synthesis.v1";
    public const string F5Path = "/martlet/v1/inference/f5-synthesis";
    public const string XttsRouteId = "martlet.gateway.xtts-synthesis.v1";
    public const string GptSovitsRouteId = "martlet.gateway.gpt-sovits-synthesis.v1";
    public const string DiaRouteId = "martlet.gateway.dia-synthesis.v1";
    /// <summary>The lanes a host gives its routes: live (the conversation's jobs) and pool (Thinking pool work).</summary>
    public const string LiveLane = "live", PoolLane = "pool";

    /// <summary>The graphics cards that serve the route, as the host names them; empty when it doesn't say (then the whole
    /// computer counts).</summary>
    public IReadOnlyList<string> Gpus { get; init; } = [];

    /// <summary>The route's lane: <see cref="LiveLane"/>, <see cref="PoolLane"/>, or empty when the host doesn't say.</summary>
    public string Lane { get; init; } = "";

    /// <summary>The route as this PC saves it when a job moves to the host: its advertised identity and limits, observed now.</summary>
    public GatewayRouteSnapshot Snapshot(SetupRouteType routeType) => new()
    {
        SchemaVersion = 1, RouteType = routeType, RegistryId = SelfHostSetup.RegistryId,
        RegistryVersion = SelfHostSetup.RegistryVersion, RouteId = RouteId, Path = Path, ContractId = ContractId,
        ContractVersion = ContractVersion, DestinationId = DestinationId, WorkerId = WorkerId,
        WorkerPackageRevision = AdapterVersion, AdapterVersion = AdapterVersion, ModelId = ModelId,
        ModelRevision = ModelRevision, ModelSha256 = ModelSha256, ArtifactIdentitySha256 = ArtifactIdentitySha256,
        MaximumRequestBytes = MaximumRequestBytes, MaximumInputBytes = MaximumInputBytes,
        MaximumOutputBytes = MaximumOutputBytes, MaximumEventBytes = MaximumEventBytes, MaximumEvents = MaximumEvents,
        MaximumStreamBytes = MaximumStreamBytes, MaximumDurationSeconds = (int)Math.Ceiling(MaximumDuration.TotalSeconds),
        Cancellation = Cancellation switch
        {
            "discard_only" => GatewayCancellationMode.DiscardOnly,
            "cooperative_compute_cancel" => GatewayCancellationMode.CooperativeComputeCancel,
            _ => GatewayCancellationMode.RequestAbort
        },
        ObservedAtUtc = DateTimeOffset.UtcNow, ProbeRevision = Guid.NewGuid()
    };
}

/// <summary>One earlier message of the conversation sent with a host chat request.</summary>
public sealed record HostChatMessage(bool Assistant, string Text);

public sealed partial class Audio2FaceHostConnection
{
    private static readonly JsonSerializerOptions ChatJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Streams the reply of the host's own conversation model (its Ollama role, or its Deep thinking role's own Ollama)
    /// through the gateway relay.
    /// Only text deltas are yielded; failures throw <see cref="Audio2FaceHostException"/> with the gateway's code.
    /// <paramref name="images"/> (base64 JPEG/PNG, at most one) ride with the current input for a vision model.
    /// <paramref name="sampling"/> adds the optional Ollama sampling settings; unset values are not sent, so a request
    /// without any keeps the shape older hosts accept.</summary>
    public async IAsyncEnumerable<string> StreamChatAsync(HostRoute route, CorrelationIds ids, long epoch,
        DateTimeOffset deadline, string? system, IReadOnlyList<HostChatMessage> history, string input, double temperature,
        int maximumOutputTokens, int maximumContextTokens, IReadOnlyList<string>? images = null,
        GenerationSettings? sampling = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(history);
        ids.Validate();
        if (!(route.RouteId == HostRoute.OllamaChatRouteId && route.Path == HostRoute.OllamaChatPath ||
                route.RouteId == HostRoute.DeepThinkingRouteId && route.Path == HostRoute.DeepThinkingPath))
            throw new ArgumentException("The route is not one of the host's conversation models.", nameof(route));
        var now = clock.GetUtcNow();
        var latest = now + route.MaximumDuration - TimeSpan.FromSeconds(1);
        if (deadline > latest) deadline = latest;
        if (deadline <= now) throw new Audio2FaceHostException("job.deadline", "No time is left for the host's reply.");
        // A saved context size above what the host's gateway loads is capped there.
        int? contextTokens = sampling?.ContextTokens is { } saved ? Math.Min(saved, GenerationSettings.MaximumHostContextTokens) : null;
        var payload = new Dictionary<string, object>
        {
            ["input"] = ChatText(input),
            ["temperature"] = temperature,
            ["maximum_output_tokens"] = maximumOutputTokens,
            // The desktop's context bound covers tool schemas the host never gets; the gateway takes at most 32,768. The gateway
            // refuses a context window larger than this bound (request.invalid), and a Thinking pool job asks the host's Ollama
            // for its largest window (so it keeps its model loaded) with a smaller budget of its own: the bound covers the window.
            ["maximum_context_tokens"] = Math.Max(Math.Min(maximumContextTokens, GenerationSettings.MaximumHostContextTokens),
                contextTokens ?? 0)
        };
        if (!string.IsNullOrEmpty(system)) payload["system"] = ChatText(system);
        if (history.Count > 0)
            payload["history"] = history.Select(message => new Dictionary<string, string>
            {
                ["role"] = message.Assistant ? "assistant" : "user", ["text"] = ChatText(message.Text)
            }).ToArray();
        if (images is { Count: > 0 }) payload["images"] = images.ToArray();
        if (sampling is not null)
        {
            if (sampling.TopP is { } topP) payload["top_p"] = topP;
            if (sampling.TopK is { } topK) payload["top_k"] = topK;
            if (sampling.MinP is { } minP) payload["min_p"] = minP;
            if (sampling.RepeatPenalty is { } repeat) payload["repeat_penalty"] = repeat;
            if (sampling.FrequencyPenalty is { } frequency) payload["frequency_penalty"] = frequency;
            if (sampling.PresencePenalty is { } presence) payload["presence_penalty"] = presence;
            if (contextTokens is { } context) payload["context_tokens"] = context;
            // Thinking steps; a host older than it refuses the request (request.invalid).
            if (sampling.Reasoning is { } think) payload["think"] = think;
        }
        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["protocol_version"] = new Dictionary<string, int> { ["major"] = 2, ["minor"] = 0 },
            ["route_id"] = route.RouteId, ["contract_id"] = route.ContractId, ["contract_version"] = route.ContractVersion,
            ["destination_id"] = route.DestinationId, ["worker_id"] = route.WorkerId, ["adapter_version"] = route.AdapterVersion,
            ["model_id"] = route.ModelId, ["model_revision"] = route.ModelRevision, ["model_sha256"] = route.ModelSha256,
            ["artifact_identity_sha256"] = route.ArtifactIdentitySha256,
            ["session_id"] = ids.SessionId, ["turn_id"] = ids.TurnId, ["request_id"] = ids.RequestId, ["epoch"] = epoch,
            ["deadline_utc"] = deadline.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["payload"] = payload
        }, ChatJson);
        if (body.Length > route.MaximumRequestBytes)
            throw new Audio2FaceHostException("request.too_large", images is { Count: > 0 }
                ? "The screen image is too large for the host's model route; update the host so it accepts screen images."
                : "The conversation is too long for the host's model route.");
        using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + route.Path)
        {
            Content = Audio2FaceHostClient.JsonContent(body)
        };
        Sign(request, body);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline - clock.GetUtcNow() + TimeSpan.FromSeconds(1));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            using var failure = await Audio2FaceHostClient.ReadJson(response, 64 * 1024, timeout.Token).ConfigureAwait(false);
            throw Audio2FaceHostClient.Remote(failure.RootElement);
        }
        if (response.Content.Headers.ContentType?.MediaType != "application/x-ndjson")
            throw new Audio2FaceHostException("response.invalid", "The host returned an invalid reply stream.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        var total = 0L;
        while (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
        {
            total += line.Length + 1;
            if (line.Length > route.MaximumEventBytes * 2 || total > route.MaximumStreamBytes)
                throw new Audio2FaceHostException("stream.limit", "The host's reply stream exceeded its bounds.");
            var (text, terminal) = ParseChatEvent(line, ids);
            if (text is not null) yield return text;
            if (terminal) yield break;
        }
        throw new Audio2FaceHostException("stream.truncated", "The host's reply stream ended early.");
    }

    // The gateway accepts line breaks and tabs in chat text but no other control characters.
    private static string ChatText(string text) =>
        text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))
            ? new string(text.Select(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t') ? ' ' : c).ToArray())
            : text;

    private static (string? Text, bool Terminal) ParseChatEvent(string line, CorrelationIds ids)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.GetProperty("request_id").GetGuid() != ids.RequestId) throw new FormatException();
            return root.GetProperty("type").GetString() switch
            {
                "started" => (null, false),
                "text_delta" => (root.GetProperty("text").GetString() ?? throw new FormatException(), false),
                "completed" => (null, true),
                "canceled" => throw new Audio2FaceHostException("job.canceled", "The host canceled the reply."),
                "failed" => throw Audio2FaceHostClient.Remote(root),
                _ => throw new FormatException()
            };
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new Audio2FaceHostException("stream.invalid", "The host's reply stream was invalid.");
        }
    }
}
