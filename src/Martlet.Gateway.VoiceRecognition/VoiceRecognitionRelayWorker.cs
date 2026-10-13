using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Martlet.Gateway.VoiceRecognition;

/// <summary>
/// Gateway relay from a paired client to this host's loopback voice recognition service (the <c>voice-recognition</c> host role:
/// the desktop's pyannote segmentation 3.0 and WeSpeaker ResNet34-LM in sherpa-onnx, on the host's NVIDIA graphics card or
/// processor). Each operation (status, analyze, voiceprint) returns one JSON text event. Audio is passed on in memory only.
/// </summary>
public sealed class VoiceRecognitionRelayWorker : IVoiceRecognitionGatewayInferenceWorker, IAsyncDisposable
{
    public const string DefaultDestinationId = "voice-recognition-host";
    public const string DefaultWorkerId = "voice-recognition-relay";
    /// <summary>The voice model's ID: the desktop's voice-recognition\wespeaker_en_voxceleb_resnet34_LM.onnx.</summary>
    public const string DefaultModel = "wespeaker-en-voxceleb-resnet34-lm";

    /// <summary>The voice model the role serves, by its release and SHA-256 (the same file as the desktop build's).</summary>
    public static readonly IReadOnlyDictionary<string, (string Revision, string Sha256)> PinnedModels =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [DefaultModel] = ("speaker-recongition-models", "e9848563da86f263117134dfd7ad63c92355b37de492b55e325400c9d9c39012")
        };

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly Uri endpoint;
    private readonly HttpClient http;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> running = new();

    public VoiceRecognitionRelayWorker(Uri endpoint, string model = DefaultModel, string destinationId = DefaultDestinationId,
        string workerId = DefaultWorkerId, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(model);
        if (endpoint.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) ||
            !IPAddress.IsLoopback(address) || endpoint.AbsolutePath != "/")
            throw new ArgumentException("The voice recognition relay only reaches a loopback http://127.0.0.1:<port>/ service.", nameof(endpoint));
        if (!PinnedModels.TryGetValue(model, out var pinned))
            throw new ArgumentException("The voice model is not one the voice-recognition role provisions.", nameof(model));
        this.endpoint = endpoint;
        Route = GatewayInferenceRoute.VoiceRecognition(destinationId, workerId, model, pinned.Revision, pinned.Sha256);
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
        if (!ReferenceEquals(request.Route, Route) || principal.Role != GatewayRole.Voice || request.Payload is not GatewayVoiceRecognitionPayload)
            throw new GatewayInferenceWorkerException(GatewayInferenceWorkerFailure.PermissionDenied);
        return ValueTask.FromResult<GatewayInferencePermissionLease>(new Lease(request, principal, this));
    }

    public async IAsyncEnumerable<GatewayInferenceEvent> ExecuteAsync(
        GatewayInferenceRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = (GatewayVoiceRecognitionPayload)request.Payload;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        running[request.RequestId] = stop;
        try
        {
            yield return Event(request, GatewayInferenceEventKind.Started, 0);
            var (text, failure) = payload.Operation switch
            {
                GatewayVoiceRecognitionOperation.Status => await StatusAsync(stop.Token).ConfigureAwait(false),
                GatewayVoiceRecognitionOperation.Analyze => await PostAsync("analyze", "speakers", payload, stop.Token).ConfigureAwait(false),
                _ => await PostAsync("voiceprint", "voiceprint", payload, stop.Token).ConfigureAwait(false)
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

    // The service listens as soon as it starts and says "loading" until the models are ready; before it listens (its container
    // starting, or restarting after a failed load) the relay says "loading" for it.
    private async Task<(byte[] Text, string? Failure)> StatusAsync(CancellationToken token)
    {
        var response = await SendAsync(HttpMethod.Get, "status", null, token).ConfigureAwait(false);
        using var json = response.Json;
        if (response.Failure == "worker.unavailable" || json is null)
            return (JsonSerializer.SerializeToUtf8Bytes(new { state = "loading", model = Route.ModelId }), null);
        return response.Status == HttpStatusCode.OK && json.RootElement.ValueKind == JsonValueKind.Object
            ? (JsonSerializer.SerializeToUtf8Bytes(json.RootElement, Relaxed), null)
            : ([], "worker.failed");
    }

    private async Task<(byte[] Text, string? Failure)> PostAsync(string path, string required, GatewayVoiceRecognitionPayload payload,
        CancellationToken token)
    {
        var response = await SendAsync(HttpMethod.Post, path, payload, token).ConfigureAwait(false);
        using var json = response.Json;
        if (response.Status == HttpStatusCode.BadRequest) return ([], "request.invalid");
        if (response.Status == HttpStatusCode.ServiceUnavailable) return ([], "worker.unavailable");
        if (response.Failure is not null) return ([], response.Failure);
        return response.Status == HttpStatusCode.OK && json is not null && json.RootElement.ValueKind == JsonValueKind.Object &&
            json.RootElement.TryGetProperty(required, out _)
            ? (JsonSerializer.SerializeToUtf8Bytes(json.RootElement, Relaxed), null)
            : ([], "worker.failed");
    }

    private async Task<(JsonDocument? Json, HttpStatusCode Status, string? Failure)> SendAsync(
        HttpMethod method, string path, GatewayVoiceRecognitionPayload? payload, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, new Uri(endpoint, path));
        byte[]? body = null;
        if (payload is not null)
        {
            body = payload.Pcm.ToArray();
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }
        try
        {
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                token.ThrowIfCancellationRequested();
                return (null, 0, "worker.unavailable");
            }
            using (response)
            {
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.ServiceUnavailable)
                    return (null, response.StatusCode, null);
                if (response.Content.Headers.ContentType?.MediaType != "application/json")
                    return (null, response.StatusCode, "worker.failed");
                byte[]? json;
                try { json = await ReadBoundedAsync(response.Content, GatewayInferenceRoute.VoiceRecognitionMaximumResultBytes, token).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or HttpRequestException)
                {
                    token.ThrowIfCancellationRequested();
                    return (null, response.StatusCode, "worker.failed");
                }
                if (json is null) return (null, response.StatusCode, "worker.failed");
                try
                {
                    return (JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 }), response.StatusCode, null);
                }
                catch (JsonException)
                {
                    return (null, response.StatusCode, "worker.failed");
                }
            }
        }
        finally
        {
            if (body is not null) CryptographicOperations.ZeroMemory(body);
        }
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

    private sealed class Lease(GatewayInferenceRequest request, GatewayPrincipal principal, VoiceRecognitionRelayWorker owner)
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
