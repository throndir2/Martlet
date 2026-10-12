using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Martlet.Gateway.Motion;

/// <summary>
/// Gateway relay from a paired client to this host's loopback HY-Motion service (the <c>motion</c> host role,
/// <c>workers/motion</c>). <c>status</c> passes the service's readiness through, <c>generate</c> sends only a prompt, a length in
/// seconds and an optional seed and returns the VRMA clip as base64 pieces followed by one summary object, and <c>free</c>
/// unloads the model. A failure the service reports (busy, not provisioned, out of memory) comes back as one JSON object with an
/// <c>error</c>. No client path, URL or model choice reaches the service.
/// </summary>
public sealed class MotionRelayWorker : IMotionGatewayInferenceWorker, IAsyncDisposable
{
    public const string DefaultDestinationId = "motion-host";
    public const string DefaultWorkerId = "motion-relay";
    public const string DefaultModel = "hy-motion-1.0-lite";
    public const string VrmaMediaType = "model/gltf-binary";

    /// <summary>The models the motion role provisions: tencent/HY-Motion-1.0's revision and each checkpoint's SHA-256
    /// (workers/motion/martlet_motion/pins.py).</summary>
    public static readonly IReadOnlyDictionary<string, (string Revision, string Sha256)> PinnedModels =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [DefaultModel] = ("620dd559f8d964aac2f82f1204fe6a35ad8ad14d",
                "d83f118f8d74db76249db86dcf9982a8229f43ef4e9fa11f683019d6230dd486"),
            ["hy-motion-1.0"] = ("620dd559f8d964aac2f82f1204fe6a35ad8ad14d",
                "4fa91c6085f30c49bccda150b28d6c8d0887dc07e6d5d4ec0055482732635f10")
        };

    private const int MaximumWorkerJsonBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly string[] ErrorCodes =
        ["motion.busy", "motion.unavailable", "motion.out_of_memory", "motion.failed", "request.invalid"];
    private readonly Uri endpoint;
    private readonly HttpClient http;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> running = new();

    public MotionRelayWorker(Uri endpoint, string model = DefaultModel, string destinationId = DefaultDestinationId,
        string workerId = DefaultWorkerId, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(model);
        if (endpoint.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) ||
            !IPAddress.IsLoopback(address) || endpoint.AbsolutePath != "/")
            throw new ArgumentException("The motion relay only reaches a loopback http://127.0.0.1:<port>/ service.", nameof(endpoint));
        if (!PinnedModels.TryGetValue(model, out var pinned))
            throw new ArgumentException("The motion model is not one the motion role provisions.", nameof(model));
        this.endpoint = endpoint;
        Route = GatewayInferenceRoute.Motion(destinationId, workerId, model, pinned.Revision, pinned.Sha256);
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
        if (!ReferenceEquals(request.Route, Route) || principal.Role != GatewayRole.Voice || request.Payload is not GatewayMotionPayload)
            throw new GatewayInferenceWorkerException(GatewayInferenceWorkerFailure.PermissionDenied);
        return ValueTask.FromResult<GatewayInferencePermissionLease>(new Lease(request, principal, this));
    }

    public async IAsyncEnumerable<GatewayInferenceEvent> ExecuteAsync(
        GatewayInferenceRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = (GatewayMotionPayload)request.Payload;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        running[request.RequestId] = stop;
        try
        {
            yield return Event(request, GatewayInferenceEventKind.Started, 0);
            var (texts, failure) = payload.Operation switch
            {
                GatewayMotionOperation.Status => await StatusAsync(stop.Token).ConfigureAwait(false),
                GatewayMotionOperation.Generate => await GenerateAsync(payload, stop.Token).ConfigureAwait(false),
                _ => await FreeAsync(stop.Token).ConfigureAwait(false)
            };
            stop.Token.ThrowIfCancellationRequested();
            if (failure is not null)
            {
                yield return Event(request, GatewayInferenceEventKind.Failed, 1, errorCode: failure);
                yield break;
            }
            var sequence = 1L;
            foreach (var text in texts)
                yield return Event(request, GatewayInferenceEventKind.TextDelta, sequence++, text);
            yield return Event(request, GatewayInferenceEventKind.Completed, sequence);
        }
        finally
        {
            running.TryRemove(request.RequestId, out _);
        }
    }

    private async Task<(IReadOnlyList<byte[]>, string?)> StatusAsync(CancellationToken token)
    {
        using var answer = await SendAsync(HttpMethod.Get, "status", null, token).ConfigureAwait(false);
        if (answer.Failure == "worker.unavailable")
            return ([JsonSerializer.SerializeToUtf8Bytes(new { state = "loading" })], null);
        if (answer.Failure is not null || answer.Status != HttpStatusCode.OK || answer.Json is null)
            return ([], "worker.failed");
        return ([JsonSerializer.SerializeToUtf8Bytes(answer.Json.RootElement, Relaxed)], null);
    }

    private async Task<(IReadOnlyList<byte[]>, string?)> FreeAsync(CancellationToken token)
    {
        using var answer = await SendAsync(HttpMethod.Post, "free", "{}"u8.ToArray(), token).ConfigureAwait(false);
        if (answer.Failure is not null) return ([], answer.Failure);
        if (ServiceError(answer) is { } error) return ([error], null);
        return answer.Status == HttpStatusCode.OK && answer.Json is not null
            ? ([JsonSerializer.SerializeToUtf8Bytes(answer.Json.RootElement, Relaxed)], null)
            : ([], "worker.failed");
    }

    private async Task<(IReadOnlyList<byte[]>, string?)> GenerateAsync(GatewayMotionPayload payload, CancellationToken token)
    {
        var body = new Dictionary<string, object> { ["prompt"] = payload.Prompt!, ["seconds"] = payload.Seconds };
        if (payload.Seed is { } seed) body["seed"] = seed;
        using var answer = await SendAsync(HttpMethod.Post, "generate", JsonSerializer.SerializeToUtf8Bytes(body, Relaxed), token)
            .ConfigureAwait(false);
        if (answer.Failure is not null) return ([], answer.Failure);
        if (ServiceError(answer) is { } error) return ([error], null);
        if (answer.Status != HttpStatusCode.OK || answer.Vrma is not { } vrma || !IsGlb(vrma)) return ([], "worker.failed");
        var texts = new List<byte[]>();
        for (var offset = 0; offset < vrma.Length; offset += GatewayInferenceRoute.MotionMaximumEventBytes)
        {
            var length = Math.Min(GatewayInferenceRoute.MotionMaximumEventBytes, vrma.Length - offset);
            texts.Add(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["media_type"] = VrmaMediaType, ["total_bytes"] = vrma.Length, ["offset"] = offset, ["bytes"] = length,
                ["data_base64"] = Convert.ToBase64String(vrma.AsSpan(offset, length))
            }, Relaxed));
        }
        texts.Add(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["state"] = "done", ["model"] = Route.ModelId, ["frames"] = answer.Header("Frames"), ["fps"] = answer.Header("Fps"),
            ["seed"] = answer.Header("Seed"), ["milliseconds"] = answer.Header("Milliseconds"),
            ["load_milliseconds"] = answer.Header("Load-Milliseconds"), ["total_bytes"] = vrma.Length,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(vrma))
        }, Relaxed));
        return (texts, null);
    }

    // A failure the service reports as {"error": {"code", "summary"}} with a known code, as one JSON object for the client.
    private static byte[]? ServiceError(Answer answer)
    {
        if (answer.Status == HttpStatusCode.OK || answer.Json is null ||
            !answer.Json.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            return null;
        var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        if (code is null || !ErrorCodes.Contains(code)) code = "motion.failed";
        var summary = error.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
        summary = new string(summary.Where(ch => !char.IsControl(ch)).Take(512).ToArray());
        return JsonSerializer.SerializeToUtf8Bytes(new { error = new { code, summary } }, Relaxed);
    }

    private static bool IsGlb(byte[] bytes) =>
        bytes.Length is >= 28 and <= GatewayInferenceRoute.MotionMaximumVrmaBytes && bytes.AsSpan(0, 4).SequenceEqual("glTF"u8) &&
        BitConverter.ToUInt32(bytes, 4) == 2 && BitConverter.ToUInt32(bytes, 8) == (uint)bytes.Length;

    private async Task<Answer> SendAsync(HttpMethod method, string path, byte[]? body, CancellationToken token)
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
            return new(null, null, 0, "worker.unavailable", new Dictionary<string, string>());
        }
        using (response)
        {
            var headers = response.Headers
                .Where(h => h.Key.StartsWith("X-Martlet-Motion-", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(h => h.Key["X-Martlet-Motion-".Length..], h => h.Value.FirstOrDefault() ?? "", StringComparer.OrdinalIgnoreCase);
            var media = response.Content.Headers.ContentType?.MediaType;
            try
            {
                if (media == VrmaMediaType && response.StatusCode == HttpStatusCode.OK)
                {
                    var vrma = await ReadBoundedAsync(response.Content, GatewayInferenceRoute.MotionMaximumVrmaBytes, token).ConfigureAwait(false);
                    return vrma is null ? new(null, null, response.StatusCode, "worker.failed", headers) : new(null, vrma, response.StatusCode, null, headers);
                }
                if (media != "application/json")
                    return new(null, null, response.StatusCode,
                        response.StatusCode == HttpStatusCode.ServiceUnavailable ? "worker.unavailable" : "worker.failed", headers);
                var json = await ReadBoundedAsync(response.Content, MaximumWorkerJsonBytes, token).ConfigureAwait(false);
                if (json is null) return new(null, null, response.StatusCode, "worker.failed", headers);
                var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    document.Dispose();
                    return new(null, null, response.StatusCode, "worker.failed", headers);
                }
                return new(document, null, response.StatusCode, null, headers);
            }
            catch (Exception error) when (error is IOException or HttpRequestException or JsonException)
            {
                token.ThrowIfCancellationRequested();
                return new(null, null, response.StatusCode, "worker.failed", headers);
            }
        }
    }

    private sealed record Answer(JsonDocument? Json, byte[]? Vrma, HttpStatusCode Status, string? Failure,
        IReadOnlyDictionary<string, string> Headers) : IDisposable
    {
        // A whole-number header the service sets (X-Martlet-Motion-<name>), or null.
        public long? Header(string name) => Headers.TryGetValue(name, out var text) &&
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

        public void Dispose() => Json?.Dispose();
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

    private sealed class Lease(GatewayInferenceRequest request, GatewayPrincipal principal, MotionRelayWorker owner)
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
