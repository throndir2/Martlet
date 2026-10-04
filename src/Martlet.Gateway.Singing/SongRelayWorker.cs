using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Martlet.Gateway.Singing;

/// <summary>
/// Gateway relay from a paired client to this host's own loopback singing service (the <c>singing</c> host role:
/// workers/singing/martlet_singing/host.py), which writes a song with ACE-Step 1.5, separates its vocals and matches them to
/// a voice of the shared voice library (SoulX-Singer-SVC, or VevoSing when the owner set it up). A song takes far longer
/// than one request, so each request is one job operation (<c>start</c>, <c>status</c>, <c>result</c> page, <c>cancel</c>)
/// answered with JSON text events. Enabling the role is the host owner's standing permission for paired <c>voice</c>
/// devices to send lyrics and a style and have songs sung in a voice of the owner's shared voice list; the gateway hands the
/// service that voice's recording (nothing else on the host is reachable through it, and no path or URL is accepted).
/// </summary>
public sealed class SongRelayWorker : ISongGatewayInferenceWorker, IAsyncDisposable
{
    public const string DefaultDestinationId = "singing-host";
    public const string DefaultWorkerId = "singing-relay";
    public const string DefaultModel = "ace-step-v15-soulx-svc";

    /// <summary>The models the singing role provisions (workers/singing/martlet_singing/pins.py): the ACE-Step 1.5 music
    /// model's Hugging Face revision and turbo DiT weights SHA-256 identify the route; the separator, voice matching and
    /// the rest are pinned with it and listed in the service's status.</summary>
    public static readonly IReadOnlyDictionary<string, (string Revision, string Sha256)> PinnedModels =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [DefaultModel] = ("19671f406d603126926c1b7e2adc169acbcade22",
                "3f6e0797fad420a39bd33979eb6e840e30989e34a3794e843d23b60ec6e422d7")
        };

    private const int MaximumWorkerJsonBytes = 128 * 1024;
    // Base64 written as is: the default encoder escapes every '+' as \u002B, pushing a page past the route's event bound.
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly Uri endpoint;
    private readonly HttpClient http;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> running = new();

    public SongRelayWorker(Uri endpoint, string model = DefaultModel, string destinationId = DefaultDestinationId,
        string workerId = DefaultWorkerId, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(model);
        if (endpoint.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) ||
            !IPAddress.IsLoopback(address) || endpoint.AbsolutePath != "/")
            throw new ArgumentException("The singing relay only reaches a loopback http://127.0.0.1:<port>/ service.", nameof(endpoint));
        if (!PinnedModels.TryGetValue(model, out var pinned))
            throw new ArgumentException("The singing model is not one the singing role provisions.", nameof(model));
        this.endpoint = endpoint;
        Route = GatewayInferenceRoute.Song(destinationId, workerId, model, pinned.Revision, pinned.Sha256);
        http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false, Credentials = null,
            AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(5)
        }, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
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
        if (!ReferenceEquals(request.Route, Route) || principal.Role != GatewayRole.Voice || request.Payload is not GatewaySongPayload)
            throw new GatewayInferenceWorkerException(GatewayInferenceWorkerFailure.PermissionDenied);
        return ValueTask.FromResult<GatewayInferencePermissionLease>(new Lease(request, principal, this));
    }

    public async IAsyncEnumerable<GatewayInferenceEvent> ExecuteAsync(
        GatewayInferenceRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = (GatewaySongPayload)request.Payload;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        running[request.RequestId] = stop;
        try
        {
            yield return Event(request, GatewayInferenceEventKind.Started, 0);
            var sequence = 1L;
            IReadOnlyList<byte[]> texts;
            string? failure;
            (texts, failure) = payload.Operation switch
            {
                GatewaySongOperation.Start => await StartAsync(request, payload, stop.Token).ConfigureAwait(false),
                GatewaySongOperation.Status => await JsonAsync(HttpMethod.Get,
                    payload.JobId is null ? "status" : $"jobs/{payload.JobId}", null, stop.Token).ConfigureAwait(false),
                GatewaySongOperation.Cancel => await JsonAsync(HttpMethod.Post, $"jobs/{payload.JobId}/cancel", "{}"u8.ToArray(),
                    stop.Token).ConfigureAwait(false),
                _ => await ResultAsync(payload, stop.Token).ConfigureAwait(false)
            };
            stop.Token.ThrowIfCancellationRequested();
            if (failure is not null)
            {
                yield return Event(request, GatewayInferenceEventKind.Failed, sequence, errorCode: failure);
                yield break;
            }
            foreach (var text in texts)
                yield return Event(request, GatewayInferenceEventKind.TextDelta, sequence++, text);
            yield return Event(request, GatewayInferenceEventKind.Completed, sequence);
        }
        finally
        {
            running.TryRemove(request.RequestId, out _);
        }
    }

    private async Task<(IReadOnlyList<byte[]>, string?)> StartAsync(GatewayInferenceRequest request, GatewaySongPayload payload,
        CancellationToken token)
    {
        var start = payload.Start!;
        var body = new JsonObject
        {
            ["request_id"] = request.RequestId.ToString("D"),
            ["lyrics"] = start.Lyrics,
            ["style"] = start.Style,
            ["duration_seconds"] = start.DurationSeconds,
            ["language"] = start.Language,
            ["quality"] = start.Quality,
            ["voice_match"] = start.VoiceMatch,
            ["voice_id"] = start.VoiceId,
            ["reference"] = new JsonObject
            {
                ["audio_sha256"] = start.ReferenceAudioSha256,
                ["audio_base64"] = Convert.ToBase64String(payload.ReferenceAudio.Span)
            }
        };
        if (start.Bpm is { } bpm) body["bpm"] = bpm;
        if (start.Key is { } key) body["key"] = key;
        if (start.Seed is { } seed) body["seed"] = seed;
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        try
        {
            return await JsonAsync(HttpMethod.Post, "jobs", bytes, token).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    // One page of a finished track as JSON text events of at most SongMaximumEventPcmBytes of PCM each.
    private async Task<(IReadOnlyList<byte[]>, string?)> ResultAsync(GatewaySongPayload payload, CancellationToken token)
    {
        var path = string.Create(CultureInfo.InvariantCulture,
            $"jobs/{payload.JobId}/tracks/{payload.Track}?offset={payload.OffsetFrames}&frames={payload.MaximumFrames}");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, path));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            token.ThrowIfCancellationRequested();
            return ([], "worker.unavailable");
        }
        using (response)
        {
            if (response.Content.Headers.ContentType?.MediaType == "application/json")
            {
                // A job-level answer (not finished, unknown or expired job): pass it on as one JSON event.
                var json = await ReadBoundedAsync(response.Content, MaximumWorkerJsonBytes, token).ConfigureAwait(false);
                return json is not null && IsJsonObject(json) ? ([json], null) : ([], "worker.failed");
            }
            if (response.StatusCode != HttpStatusCode.OK ||
                !TryHeader(response, "X-Song-Sample-Rate", out var sampleRate) || sampleRate is not (24_000 or 44_100 or 48_000) ||
                !TryHeader(response, "X-Song-Channels", out var channels) || channels is not (1 or 2) ||
                !TryHeader(response, "X-Song-Total-Frames", out var totalFrames) || totalFrames is < 0 or > 48_000L * 200)
                return ([], "worker.failed");
            var frameBytes = (int)channels * 2;
            var maximumBytes = Math.Min(GatewayInferenceRoute.SongMaximumPageBytes, payload.MaximumFrames * frameBytes);
            var pcm = await ReadBoundedAsync(response.Content, maximumBytes, token).ConfigureAwait(false);
            if (pcm is null || pcm.Length % frameBytes != 0 || payload.OffsetFrames + pcm.Length / frameBytes > totalFrames)
                return ([], "worker.failed");
            var texts = new List<byte[]>();
            var perEvent = GatewayInferenceRoute.SongMaximumEventPcmBytes / frameBytes * frameBytes;
            var offset = payload.OffsetFrames;
            var position = 0;
            do
            {
                var length = Math.Min(perEvent, pcm.Length - position);
                texts.Add(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
                {
                    ["track"] = payload.Track!,
                    ["sample_rate"] = sampleRate,
                    ["channels"] = channels,
                    ["total_frames"] = totalFrames,
                    ["offset_frames"] = offset,
                    ["frames"] = length / frameBytes,
                    ["pcm_base64"] = Convert.ToBase64String(pcm.AsSpan(position, length))
                }, Relaxed));
                position += length;
                offset += length / frameBytes;
            }
            while (position < pcm.Length);
            return (texts, null);
        }
    }

    private async Task<(IReadOnlyList<byte[]>, string?)> JsonAsync(HttpMethod method, string path, byte[]? body,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, new Uri(endpoint, path));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            token.ThrowIfCancellationRequested();
            return ([], "worker.unavailable");
        }
        using (response)
        {
            if (response.Content.Headers.ContentType?.MediaType != "application/json")
                return ([], response.StatusCode == HttpStatusCode.ServiceUnavailable ? "worker.unavailable" : "worker.failed");
            byte[]? json;
            try { json = await ReadBoundedAsync(response.Content, MaximumWorkerJsonBytes, token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or HttpRequestException)
            {
                token.ThrowIfCancellationRequested();
                return ([], "worker.failed");
            }
            return json is not null && IsJsonObject(json) ? ([json], null) : ([], "worker.failed");
        }
    }

    private static bool TryHeader(HttpResponseMessage response, string name, out long value)
    {
        value = 0;
        return response.Headers.TryGetValues(name, out var values) && values.SingleOrDefault() is { } text &&
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsJsonObject(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maximum) return null;
        }
        return buffer.ToArray();
    }

    public ValueTask<GatewayInferenceCancellationReceipt> CancelAsync(
        GatewayInferenceCancellationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (running.TryGetValue(request.RequestId, out var stop))
            try { stop.Cancel(); }
            catch (ObjectDisposedException) { }
        return ValueTask.FromResult(new GatewayInferenceCancellationReceipt
        {
            RouteId = request.RouteId, RequestId = request.RequestId, LocalDiscardAcknowledged = true,
            ComputeCancellation = GatewayCancellationCapability.RequestAbort, WorkerMayContinue = true
        });
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
        ReadOnlyMemory<byte> payload = default, string? errorCode = null) =>
        new(kind, sequence, Route.RouteId, Route.DestinationId, Route.WorkerId, Route.ModelId, Route.ModelRevision,
            Route.ModelSha256, Route.ArtifactIdentitySha256, request.SessionId, request.TurnId, request.RequestId,
            request.Epoch, payload, errorCode);

    private sealed class Lease(GatewayInferenceRequest request, GatewayPrincipal principal, SongRelayWorker owner)
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
