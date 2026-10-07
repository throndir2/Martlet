using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Martlet.Gateway.Ocr;

/// <summary>
/// Gateway relay from a paired client to this host's loopback RapidOCR service (the <c>ocr</c> host role).
/// It accepts only bounded JPEG or PNG images and returns one JSON text event.
/// </summary>
public sealed class OcrRelayWorker : IOcrGatewayInferenceWorker, IAsyncDisposable
{
    public const string DefaultDestinationId = "ocr-host";
    public const string DefaultWorkerId = "ocr-relay";
    public const string DefaultModel = "rapidocr-ppocrv4";

    public static readonly IReadOnlyDictionary<string, (string Revision, string Sha256)> PinnedModels =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [DefaultModel] = ("rapidocr_onnxruntime-1.4.4",
                "971d7d5f223a7a808662229df1ef69893809d8457d834e6373d3854bc1782cbf")
        };

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly Uri endpoint;
    private readonly HttpClient http;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> running = new();

    public OcrRelayWorker(Uri endpoint, string model = DefaultModel, string destinationId = DefaultDestinationId,
        string workerId = DefaultWorkerId, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(model);
        if (endpoint.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) ||
            !IPAddress.IsLoopback(address) || endpoint.AbsolutePath != "/")
            throw new ArgumentException("The OCR relay only reaches a loopback http://127.0.0.1:<port>/ service.", nameof(endpoint));
        if (!PinnedModels.TryGetValue(model, out var pinned))
            throw new ArgumentException("The OCR model is not one the ocr role provisions.", nameof(model));
        this.endpoint = endpoint;
        Route = GatewayInferenceRoute.Ocr(destinationId, workerId, model, pinned.Revision, pinned.Sha256);
        http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            Credentials = null,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5)
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
        if (!ReferenceEquals(request.Route, Route) || principal.Role != GatewayRole.Voice || request.Payload is not GatewayOcrPayload)
            throw new GatewayInferenceWorkerException(GatewayInferenceWorkerFailure.PermissionDenied);
        return ValueTask.FromResult<GatewayInferencePermissionLease>(new Lease(request, principal, this));
    }

    public async IAsyncEnumerable<GatewayInferenceEvent> ExecuteAsync(
        GatewayInferenceRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = (GatewayOcrPayload)request.Payload;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        running[request.RequestId] = stop;
        try
        {
            yield return Event(request, GatewayInferenceEventKind.Started, 0);
            var (text, failure) = payload.Operation switch
            {
                GatewayOcrOperation.Status => await StatusAsync(stop.Token).ConfigureAwait(false),
                _ => await ReadAsync(payload, stop.Token).ConfigureAwait(false)
            };
            stop.Token.ThrowIfCancellationRequested();
            if (failure is not null)
            {
                yield return Event(request, GatewayInferenceEventKind.Failed, 1, errorCode: failure);
                yield break;
            }
            yield return Event(request, GatewayInferenceEventKind.TextDelta, 1, text);
            yield return Event(request, GatewayInferenceEventKind.Completed, 2);
        }
        finally
        {
            running.TryRemove(request.RequestId, out _);
        }
    }

    private async Task<(byte[] Text, string? Failure)> StatusAsync(CancellationToken token)
    {
        var response = await RawJsonAsync(HttpMethod.Get, "status", null, token, unavailableIsFailure: false).ConfigureAwait(false);
        if (response.Failure == "worker.unavailable" || response.Json is null)
            return (JsonSerializer.SerializeToUtf8Bytes(new { state = "loading" }), null);
        return response.Status == HttpStatusCode.OK && response.Json.RootElement.ValueKind == JsonValueKind.Object
            ? (SerializeElement(response.Json.RootElement), null)
            : ([], "worker.failed");
    }

    private async Task<(byte[] Text, string? Failure)> ReadAsync(GatewayOcrPayload payload, CancellationToken token)
    {
        var response = await RawJsonAsync(HttpMethod.Post, "read", payload, token).ConfigureAwait(false);
        if (response.Status == HttpStatusCode.BadRequest) return ([], "request.invalid");
        if (response.Failure is not null) return ([], response.Failure);
        return response.Status == HttpStatusCode.OK && response.Json is not null &&
            response.Json.RootElement.ValueKind == JsonValueKind.Object
            ? (SerializeElement(response.Json.RootElement), null)
            : ([], "worker.failed");
    }

    private async Task<(JsonDocument? Json, HttpStatusCode Status, string? Failure)> RawJsonAsync(
        HttpMethod method, string path, GatewayOcrPayload? payload, CancellationToken token, bool unavailableIsFailure = true)
    {
        using var request = new HttpRequestMessage(method, new Uri(endpoint, path));
        if (payload is not null)
        {
            request.Content = new ByteArrayContent(payload.Image.ToArray());
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(payload.MediaType!);
        }
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            token.ThrowIfCancellationRequested();
            return (null, 0, unavailableIsFailure ? "worker.unavailable" : "worker.unavailable");
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.BadRequest)
                return (null, response.StatusCode, null);
            if (response.Content.Headers.ContentType?.MediaType != "application/json")
                return (null, response.StatusCode,
                    response.StatusCode == HttpStatusCode.ServiceUnavailable ? "worker.unavailable" : "worker.failed");
            byte[]? json;
            try { json = await ReadBoundedAsync(response.Content, GatewayInferenceRoute.OcrMaximumResultBytes, token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or HttpRequestException)
            {
                token.ThrowIfCancellationRequested();
                return (null, response.StatusCode, "worker.failed");
            }
            if (json is null) return (null, response.StatusCode, "worker.failed");
            try
            {
                var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
                return (document, response.StatusCode, null);
            }
            catch (JsonException)
            {
                return (null, response.StatusCode, "worker.failed");
            }
        }
    }

    private static byte[] SerializeElement(JsonElement element) => JsonSerializer.SerializeToUtf8Bytes(element, Relaxed);

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
            RouteId = request.RouteId,
            RequestId = request.RequestId,
            LocalDiscardAcknowledged = true,
            ComputeCancellation = GatewayCancellationCapability.RequestAbort,
            WorkerMayContinue = true
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

    private sealed class Lease(GatewayInferenceRequest request, GatewayPrincipal principal, OcrRelayWorker owner)
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
