using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Martlet.Gateway.F5;

/// <summary>
/// Gateway relay from a paired client to this host's own loopback F5 voice service (the <c>f5</c> host role:
/// workers/f5/host/martlet_f5_host.py in front of the bounded martlet_f5_worker). Enabling it in the host configuration
/// is the host owner's standing permission for paired <c>voice</c> devices to send bounded reply text and the reference
/// recording they chose to the one model the owner installed; nothing else on the host is reachable through it.
/// The service streams the worker's contiguous 24 kHz mono PCM16 frames and chunk completions, which this relay turns
/// into gateway events after checking their request, reference and model identity.
/// </summary>
public sealed class F5RelayWorker : IF5GatewayInferenceWorker, IAsyncDisposable
{
    public const string DefaultDestinationId = "f5-host";
    public const string DefaultWorkerId = "f5-relay";
    public const string DefaultModel = "f5tts-v1-base";
    private const int MaximumLineBytes = 64 * 1024;
    private const int MaximumResponseBytes = 16 * 1024 * 1024;

    /// <summary>Model weights the f5 role provisions (workers/f5/host/martlet_f5_host.py PINNED_MODELS): revision, SHA-256.</summary>
    public static readonly IReadOnlyDictionary<string, (string Revision, string Sha256)> PinnedModels =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [DefaultModel] = ("84e5a410d9cead4de2f847e7c9369a6440bdfaca",
                "670900fd14e6c458b95da6e9ed317cdb20dbaf7a1c02ac06a05475a9d32b6a38")
        };

    private readonly Uri synthesize;
    private readonly Uri cancel;
    private readonly HttpClient http;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> running = new();
    private readonly IReadOnlyList<(string Role, string ArtifactId, string Revision, string Sha256)> requiredArtifacts;

    public F5RelayWorker(Uri endpoint, string model, string? modelRevision = null, string? modelSha256 = null,
        string destinationId = DefaultDestinationId, string workerId = DefaultWorkerId, HttpMessageHandler? handler = null)
        : this(endpoint, F5Route(model, modelRevision, modelSha256, destinationId, workerId), handler)
    {
    }

    /// <summary>A relay for any reference-voice engine's route (<see cref="GatewayInferenceRoute.ReferenceSpeechRelay"/>):
    /// the engine's loopback service speaks the same /synthesize and /cancel protocol and event stream as the f5 role.
    /// <paramref name="requiredArtifacts"/> are further artifacts (role, ID, revision, SHA-256) the service's worker must
    /// report besides the route's model weights, for engines whose model is a pair (GPT-SoVITS: GPT and SoVITS).</summary>
    public F5RelayWorker(Uri endpoint, GatewayInferenceRoute route, HttpMessageHandler? handler = null,
        IReadOnlyList<(string Role, string ArtifactId, string Revision, string Sha256)>? requiredArtifacts = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(route);
        if (route.Kind != GatewayInferenceKind.F5Synthesis)
            throw new ArgumentException("The relay serves reference-voice synthesis routes only.", nameof(route));
        if (endpoint.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) ||
            !IPAddress.IsLoopback(address) || endpoint.AbsolutePath != "/")
            throw new ArgumentException("The F5 relay only reaches a loopback http://127.0.0.1:<port>/ service.", nameof(endpoint));
        synthesize = new Uri(endpoint, "synthesize");
        cancel = new Uri(endpoint, "cancel");
        Route = route;
        this.requiredArtifacts = requiredArtifacts ?? [];
        http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false, Credentials = null,
            AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(5)
        }, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static GatewayInferenceRoute F5Route(string model, string? modelRevision, string? modelSha256,
        string destinationId, string workerId)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (modelRevision is null || modelSha256 is null)
        {
            if (!PinnedModels.TryGetValue(model, out var pinned))
                throw new ArgumentException("The F5 model is not one the f5 role provisions.", nameof(model));
            (modelRevision, modelSha256) = pinned;
        }
        return GatewayInferenceRoute.F5Relay(destinationId, workerId, model, modelRevision, modelSha256);
    }

    public GatewayInferenceRoute Route { get; }

    public ValueTask<GatewayInferencePermissionLease> AcquirePermissionAsync(
        GatewayInferenceRequest request, GatewayPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(principal);
        cancellationToken.ThrowIfCancellationRequested();
        if (lifetime.IsCancellationRequested)
            throw new GatewayInferenceWorkerException(GatewayInferenceWorkerFailure.Unavailable);
        if (!ReferenceEquals(request.Route, Route) || principal.Role != GatewayRole.Voice ||
            request.Payload is not GatewayF5SynthesisPayload)
            throw new GatewayInferenceWorkerException(GatewayInferenceWorkerFailure.PermissionDenied);
        return ValueTask.FromResult<GatewayInferencePermissionLease>(new Lease(request, principal, this));
    }

    public async IAsyncEnumerable<GatewayInferenceEvent> ExecuteAsync(
        GatewayInferenceRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = (GatewayF5SynthesisPayload)request.Payload;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        running[request.RequestId] = stop;
        var sequence = 0L;
        try
        {
            yield return Event(request, GatewayInferenceEventKind.Started, sequence++);
            var (response, failure) = await SendAsync(request, payload, stop.Token).ConfigureAwait(false);
            if (response is null)
            {
                yield return Event(request, GatewayInferenceEventKind.Failed, sequence, errorCode: failure);
                yield break;
            }
            using (response)
            {
                await using var body = await response.Content.ReadAsStreamAsync(stop.Token).ConfigureAwait(false);
                var reader = new LineReader(body);
                var identified = false;
                while (true)
                {
                    var (item, error) = await NextAsync(reader, request, payload, identified, sequence, stop.Token).ConfigureAwait(false);
                    stop.Token.ThrowIfCancellationRequested();
                    if (error is not null)
                    {
                        yield return Event(request, GatewayInferenceEventKind.Failed, sequence, errorCode: error);
                        yield break;
                    }
                    identified = true;
                    if (item is null) continue;
                    sequence++;
                    yield return item;
                    if (item.IsTerminal) yield break;
                }
            }
        }
        finally
        {
            running.TryRemove(request.RequestId, out _);
        }
    }

    private async Task<(HttpResponseMessage? Response, string? Failure)> SendAsync(
        GatewayInferenceRequest request, GatewayF5SynthesisPayload payload, CancellationToken token)
    {
        using var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("ids");
            writer.WriteString("session_id", request.SessionId.ToString("D"));
            writer.WriteString("turn_id", request.TurnId.ToString("D"));
            writer.WriteString("request_id", request.RequestId.ToString("D"));
            if (request.ParentRequestId is { } parent) writer.WriteString("parent_request_id", parent.ToString("D"));
            else writer.WriteNull("parent_request_id");
            writer.WriteEndObject();
            writer.WriteString("deadline_utc", request.DeadlineUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
            writer.WriteStartObject("reference");
            writer.WriteString("preset_id", payload.PresetId.ToString("D"));
            writer.WriteString("reference_revision", payload.ReferenceRevision);
            writer.WriteString("audio_sha256", payload.ReferenceAudioSha256);
            writer.WriteString("transcript", payload.Transcript);
            writer.WriteString("transcript_revision", payload.TranscriptRevision);
            writer.WriteString("audio_base64", Convert.ToBase64String(payload.ReferenceAudio.Span));
            if (payload.ReferenceLanguage is { } language) writer.WriteString("language", language);
            // A voice made from several recordings, for an engine that learns from each: where each lies in the recording
            // (samples at its rate) and its words. Other engines never get this and clone the joined recording.
            if (payload.ReferenceClips is { } clips)
            {
                writer.WriteStartArray("clips");
                foreach (var clip in clips)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("start_sample", clip.StartSample);
                    writer.WriteNumber("sample_count", clip.SampleCount);
                    writer.WriteString("transcript", clip.Transcript);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteStartArray("chunks");
            foreach (var chunk in payload.Chunks)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", chunk.Index);
                writer.WriteString("chunk_id", chunk.ChunkId);
                writer.WriteString("text", chunk.Text);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using var message = new HttpRequestMessage(HttpMethod.Post, synthesize) { Content = new ByteArrayContent(body.ToArray()) };
        message.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            token.ThrowIfCancellationRequested();
            return (null, "worker.unavailable");
        }
        if (response.StatusCode == HttpStatusCode.OK &&
            response.Content.Headers.ContentType?.MediaType == "application/x-ndjson")
            return (response, null);
        // 503: the model is still loading, not provisioned, or busy with another reply.
        var failure = response.StatusCode == HttpStatusCode.ServiceUnavailable ? "worker.unavailable" : "worker.failed";
        response.Dispose();
        return (null, failure);
    }

    // One worker event line -> the gateway event it maps to (null for the worker's own "started"), or a failure code.
    private async Task<(GatewayInferenceEvent? Item, string? Error)> NextAsync(LineReader reader, GatewayInferenceRequest request,
        GatewayF5SynthesisPayload payload, bool identified, long sequence, CancellationToken token)
    {
        byte[]? line;
        try { line = await reader.ReadLineAsync(token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidDataException)
        {
            // After Stop the gateway expects cancellation, not a late failure event.
            token.ThrowIfCancellationRequested();
            return (null, "worker.failed");
        }
        if (line is null || line.Length == 0) return (null, "worker.failed");
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.GetProperty("type").GetString() != "event" ||
                root.GetProperty("ids").GetProperty("request_id").GetString() != request.RequestId.ToString("D") ||
                root.GetProperty("reference_revision").GetString() != payload.ReferenceRevision)
                return (null, "worker.failed");
            if (!identified && !ModelMatches(root.GetProperty("worker")))
                return (null, "worker.failed");
            long? Long(string name) => root.GetProperty(name).ValueKind == JsonValueKind.Number ? root.GetProperty(name).GetInt64() : null;
            switch (root.GetProperty("kind").GetString())
            {
                case "started":
                    return (null, null);
                case "audio_frame":
                    var frame = root.GetProperty("frame");
                    var pcm = Convert.FromBase64String(frame.GetProperty("data_base64").GetString()!);
                    var samples = frame.GetProperty("sample_count").GetInt32();
                    if (pcm.Length != samples * 2) return (null, "worker.failed");
                    return (Event(request, GatewayInferenceEventKind.AudioFrame, sequence, pcm,
                        frameSequence: frame.GetProperty("sequence").GetInt64(), chunkIndex: frame.GetProperty("chunk_index").GetInt32(),
                        sampleOffset: frame.GetProperty("sample_offset").GetInt64(), sampleCount: samples), null);
                case "chunk_completed":
                    return (Event(request, GatewayInferenceEventKind.ChunkCompleted, sequence,
                        chunkIndex: root.GetProperty("chunk_index").GetInt32(), finalSampleCount: Long("final_sample_count")), null);
                case "completed":
                    return (Event(request, GatewayInferenceEventKind.Completed, sequence, finalSampleCount: Long("final_sample_count")), null);
                case "canceled":
                    return (Event(request, GatewayInferenceEventKind.Canceled, sequence, finalSampleCount: Long("final_sample_count")), null);
                case "failed":
                    var code = root.GetProperty("error").GetProperty("code").GetString();
                    return (null, code switch
                    {
                        "deadline_exceeded" => "job.deadline",
                        "model_not_ready" or "busy" => "worker.unavailable",
                        _ => "worker.failed"
                    });
                default:
                    return (null, "worker.failed");
            }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return (null, "worker.failed");
        }
    }

    // The worker names the exact model weights it verified; they must be the ones this route advertises, plus any further
    // artifacts the engine's model needs (GPT-SoVITS's GPT half).
    private bool ModelMatches(JsonElement worker)
    {
        var model = false;
        var required = new HashSet<string>(requiredArtifacts.Select(a => a.Role), StringComparer.Ordinal);
        foreach (var artifact in worker.GetProperty("artifacts").EnumerateArray())
        {
            var role = artifact.GetProperty("role").GetString();
            var id = artifact.GetProperty("artifact_id").GetString();
            var revision = artifact.GetProperty("revision").GetString();
            var sha256 = artifact.GetProperty("sha256").GetString();
            if (role == "model_weights")
                model = !model && id == Route.ModelId && revision == Route.ModelRevision && sha256 == Route.ModelSha256;
            else if (requiredArtifacts.FirstOrDefault(a => a.Role == role) is { Role: not null } pin &&
                     (pin.ArtifactId != id || pin.Revision != revision || pin.Sha256 != sha256 || !required.Remove(pin.Role)))
                return false;
        }
        return model && required.Count == 0;
    }

    public async ValueTask<GatewayInferenceCancellationReceipt> CancelAsync(
        GatewayInferenceCancellationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (running.TryGetValue(request.RequestId, out var stop))
        {
            // Tell the service to discard the job (the worker cannot stop GPU compute), then drop the stream here.
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
                timeout.CancelAfter(GatewayInferenceProtocol.MaximumCancellationDuration / 2);
                using var content = new StringContent($"{{\"request_id\":\"{request.RequestId:D}\"}}", Encoding.UTF8, "application/json");
                using var response = await http.PostAsync(cancel, content, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
            try { stop.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        return new GatewayInferenceCancellationReceipt
        {
            RouteId = request.RouteId, RequestId = request.RequestId, LocalDiscardAcknowledged = true,
            ComputeCancellation = GatewayCancellationCapability.DiscardOnly, WorkerMayContinue = true
        };
    }

    public ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        foreach (var stop in running.Values)
            try { stop.Cancel(); }
            catch (ObjectDisposedException) { }
        http.Dispose();
        return ValueTask.CompletedTask;
    }

    private GatewayInferenceEvent Event(GatewayInferenceRequest request, GatewayInferenceEventKind kind, long sequence,
        ReadOnlyMemory<byte> payload = default, string? errorCode = null, long? frameSequence = null, int? chunkIndex = null,
        long? sampleOffset = null, int? sampleCount = null, long? finalSampleCount = null) =>
        new(kind, sequence, Route.RouteId, Route.DestinationId, Route.WorkerId, Route.ModelId, Route.ModelRevision,
            Route.ModelSha256, Route.ArtifactIdentitySha256, request.SessionId, request.TurnId, request.RequestId,
            request.Epoch, payload, errorCode, frameSequence, chunkIndex, sampleOffset, sampleCount, finalSampleCount);

    private sealed class LineReader(Stream stream)
    {
        private readonly byte[] buffer = new byte[16 * 1024];
        private readonly MemoryStream line = new();
        private int offset, count;
        private long total;

        /// <summary>The next line without its newline (empty for a blank line), or null at the end of the stream.</summary>
        internal async Task<byte[]?> ReadLineAsync(CancellationToken token)
        {
            line.SetLength(0);
            while (true)
            {
                if (offset == count)
                {
                    count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                    offset = 0;
                    if (count == 0) return line.Length == 0 ? null : line.ToArray();
                }
                var newline = Array.IndexOf(buffer, (byte)'\n', offset, count - offset);
                var end = newline < 0 ? count : newline;
                line.Write(buffer, offset, end - offset);
                total += end - offset + (newline < 0 ? 0 : 1);
                offset = newline < 0 ? count : newline + 1;
                if (line.Length > MaximumLineBytes || total > MaximumResponseBytes)
                    throw new InvalidDataException("The F5 service response exceeded its bounds.");
                if (newline >= 0) return line.ToArray();
            }
        }
    }

    private sealed class Lease(GatewayInferenceRequest request, GatewayPrincipal principal, F5RelayWorker owner)
        : GatewayInferencePermissionLease(request, principal)
    {
        public override CancellationToken Revoked => owner.lifetime.Token;
        public override void Validate()
        {
            if (owner.lifetime.IsCancellationRequested)
                throw new GatewayInferenceWorkerException(GatewayInferenceWorkerFailure.PermissionDenied);
        }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
