using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Martlet.Gateway.Pictures;

/// <summary>
/// Gateway relay from a paired client to this host's loopback ComfyUI service (the <c>pictures</c> host role). The worker is
/// a thin validated pass-through for ComfyUI API-format workflows and output images: clients can submit prompts, inspect
/// history and queue, page finished images, cancel prompt IDs and read readiness. No client path or URL reaches ComfyUI.
/// </summary>
public sealed class PictureRelayWorker : IPictureGatewayInferenceWorker, IAsyncDisposable
{
    public const string DefaultDestinationId = "pictures-host";
    public const string DefaultWorkerId = "picture-relay";
    public const string DefaultModel = "z-image-turbo";

    public static readonly IReadOnlyDictionary<string, (string Revision, string Sha256)> PinnedModels =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [DefaultModel] = ("6fc90a3b1b653e935a0d175e260736de25b84df5",
                "2407613050b809ffdff18a4ac99af83ea6b95443ecebdf80e064a79c825574a6")
        };

    private const int MaximumWorkerJsonBytes = 512 * 1024;
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly string[] ModelFolders = ["diffusion_models", "checkpoints", "text_encoders", "vae", "loras"];
    private readonly Uri endpoint;
    private readonly HttpClient http;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> running = new();

    public PictureRelayWorker(Uri endpoint, string model = DefaultModel, string destinationId = DefaultDestinationId,
        string workerId = DefaultWorkerId, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(model);
        if (endpoint.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) ||
            !IPAddress.IsLoopback(address) || endpoint.AbsolutePath != "/")
            throw new ArgumentException("The picture relay only reaches a loopback http://127.0.0.1:<port>/ service.", nameof(endpoint));
        if (!PinnedModels.TryGetValue(model, out var pinned))
            throw new ArgumentException("The picture model is not one the pictures role provisions.", nameof(model));
        this.endpoint = endpoint;
        Route = GatewayInferenceRoute.Picture(destinationId, workerId, model, pinned.Revision, pinned.Sha256);
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
        if (!ReferenceEquals(request.Route, Route) || principal.Role != GatewayRole.Voice || request.Payload is not GatewayPicturePayload)
            throw new GatewayInferenceWorkerException(GatewayInferenceWorkerFailure.PermissionDenied);
        return ValueTask.FromResult<GatewayInferencePermissionLease>(new Lease(request, principal, this));
    }

    public async IAsyncEnumerable<GatewayInferenceEvent> ExecuteAsync(
        GatewayInferenceRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = (GatewayPicturePayload)request.Payload;
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
                GatewayPictureOperation.Status => await StatusAsync(stop.Token).ConfigureAwait(false),
                GatewayPictureOperation.Prompt => await PromptAsync(request, payload, stop.Token).ConfigureAwait(false),
                GatewayPictureOperation.History => await JsonAsync(HttpMethod.Get, $"history/{Escape(payload.PromptId!)}", null,
                    GatewayInferenceRoute.PictureMaximumHistoryBytes, stop.Token).ConfigureAwait(false),
                GatewayPictureOperation.Queue => await JsonAsync(HttpMethod.Get, "queue", null, MaximumWorkerJsonBytes,
                    stop.Token).ConfigureAwait(false),
                GatewayPictureOperation.View => await ViewAsync(payload, stop.Token).ConfigureAwait(false),
                GatewayPictureOperation.Free => await FreeAsync(stop.Token).ConfigureAwait(false),
                _ => await CancelPromptAsync(payload.PromptId!, stop.Token).ConfigureAwait(false)
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

    private async Task<(IReadOnlyList<byte[]>, string?)> StatusAsync(CancellationToken token)
    {
        var system = await RawJsonAsync(HttpMethod.Get, "system_stats", null, MaximumWorkerJsonBytes, token, unavailableIsFailure: false)
            .ConfigureAwait(false);
        if (system.Failure == "worker.unavailable" || system.Json is null)
            return ([JsonSerializer.SerializeToUtf8Bytes(new { state = "loading" })], null);
        var models = new JsonObject();
        foreach (var folder in ModelFolders)
        {
            var response = await RawJsonAsync(HttpMethod.Get, $"models/{folder}", null, MaximumWorkerJsonBytes, token,
                unavailableIsFailure: false).ConfigureAwait(false);
            models[folder] = response.Json is null ? new JsonArray() : CloneNode(response.Json.RootElement);
        }
        var queue = await RawJsonAsync(HttpMethod.Get, "queue", null, MaximumWorkerJsonBytes, token, unavailableIsFailure: false)
            .ConfigureAwait(false);
        var root = new JsonObject
        {
            ["state"] = Provisioned(models) ? "ready" : "not_provisioned",
            ["engine"] = "comfyui",
            ["comfyui_version"] = VersionFrom(system.Json.RootElement),
            ["models"] = models,
            ["devices"] = DevicesFrom(system.Json.RootElement),
            ["queue_running"] = queue.Json is null ? 0 : QueueCount(queue.Json.RootElement, "queue_running"),
            ["queue_pending"] = queue.Json is null ? 0 : QueueCount(queue.Json.RootElement, "queue_pending")
        };
        return ([JsonSerializer.SerializeToUtf8Bytes(root, Relaxed)], null);
    }

    private async Task<(IReadOnlyList<byte[]>, string?)> PromptAsync(
        GatewayInferenceRequest request, GatewayPicturePayload payload, CancellationToken token)
    {
        var body = new JsonObject
        {
            ["prompt"] = CloneNode(payload.Prompt!.Value),
            ["client_id"] = "martlet-" + request.RequestId.ToString("D")
        };
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString(Relaxed));
        var answer = await RawJsonAsync(HttpMethod.Post, "prompt", bytes, MaximumWorkerJsonBytes, token).ConfigureAwait(false);
        if (answer.Failure is not null) return ([], answer.Failure);
        if (answer.Status == HttpStatusCode.BadRequest && answer.Json is { } invalid)
            return ([PromptInvalid(invalid.RootElement)], null);
        return answer.Json is not null && answer.Status == HttpStatusCode.OK ? ([SerializeElement(answer.Json.RootElement)], null)
            : ([], "worker.failed");
    }

    private async Task<(IReadOnlyList<byte[]>, string?)> ViewAsync(GatewayPicturePayload payload, CancellationToken token)
    {
        var path = "view?filename=" + Escape(payload.Filename!) + "&subfolder=" + Escape(payload.Subfolder ?? "") +
            "&type=" + Escape(payload.ImageType!);
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
            if (response.StatusCode != HttpStatusCode.OK || MediaType(response.Content.Headers.ContentType) is not { } media)
                return ([], "worker.failed");
            var contentLength = response.Content.Headers.ContentLength;
            byte[] page;
            long total;
            if (contentLength is { } known)
            {
                if (known is < 0 or > GatewayInferenceRoute.PictureMaximumFileBytes)
                    return ([], "worker.failed");
                total = known;
                var count = payload.Offset >= total ? 0 : (int)Math.Min(payload.Maximum, total - payload.Offset);
                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await SkipAsync(stream, payload.Offset, token).ConfigureAwait(false);
                page = await ReadExactAtMostAsync(stream, count, token).ConfigureAwait(false);
            }
            else
            {
                var bytes = await ReadBoundedAsync(response.Content, GatewayInferenceRoute.PictureMaximumFileBytes, token).ConfigureAwait(false);
                if (bytes is null) return ([], "worker.failed");
                total = bytes.Length;
                if (payload.Offset > total) page = [];
                else
                {
                    var count = (int)Math.Min(payload.Maximum, total - payload.Offset);
                    page = bytes.AsSpan(checked((int)payload.Offset), count).ToArray();
                }
            }
            return (PageEvents(media, total, payload.Offset, page), null);
        }
    }

    private async Task<(IReadOnlyList<byte[]>, string?)> CancelPromptAsync(string promptId, CancellationToken token)
    {
        var queue = await RawJsonAsync(HttpMethod.Get, "queue", null, MaximumWorkerJsonBytes, token).ConfigureAwait(false);
        if (queue.Failure is not null) return ([], queue.Failure);
        var running = queue.Json is not null && PromptIsRunning(queue.Json.RootElement, promptId);
        var delete = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { delete = new[] { promptId } }));
        var removed = await RawJsonAsync(HttpMethod.Post, "queue", delete, MaximumWorkerJsonBytes, token).ConfigureAwait(false);
        if (removed.Failure is not null) return ([], removed.Failure);
        if (running)
        {
            var interrupt = await RawJsonAsync(HttpMethod.Post, "interrupt", "{}"u8.ToArray(), MaximumWorkerJsonBytes, token)
                .ConfigureAwait(false);
            if (interrupt.Failure is not null) return ([], interrupt.Failure);
        }
        return ([JsonSerializer.SerializeToUtf8Bytes(new { prompt_id = promptId, state = "canceled" })], null);
    }

    private async Task<(IReadOnlyList<byte[]>, string?)> FreeAsync(CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { unload_models = true, free_memory = true });
        var response = await RawJsonAsync(HttpMethod.Post, "free", body, MaximumWorkerJsonBytes, token).ConfigureAwait(false);
        if (response.Failure is not null) return ([], response.Failure);
        return ([JsonSerializer.SerializeToUtf8Bytes(new { state = "freed" })], null);
    }

    private async Task<(IReadOnlyList<byte[]>, string?)> JsonAsync(
        HttpMethod method, string path, byte[]? body, int maximum, CancellationToken token)
    {
        var response = await RawJsonAsync(method, path, body, maximum, token).ConfigureAwait(false);
        if (response.Failure is not null) return ([], response.Failure);
        return response.Json is not null && response.Json.RootElement.ValueKind == JsonValueKind.Object
            ? ([SerializeElement(response.Json.RootElement)], null)
            : ([], "worker.failed");
    }

    private async Task<(JsonDocument? Json, HttpStatusCode Status, string? Failure)> RawJsonAsync(
        HttpMethod method, string path, byte[]? body, int maximum, CancellationToken token, bool unavailableIsFailure = true)
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
            return (null, 0, unavailableIsFailure ? "worker.unavailable" : "worker.unavailable");
        }
        using (response)
        {
            if (response.Content.Headers.ContentType?.MediaType != "application/json")
                return (null, response.StatusCode, response.StatusCode == HttpStatusCode.ServiceUnavailable ? "worker.unavailable" : "worker.failed");
            byte[]? json;
            try { json = await ReadBoundedAsync(response.Content, maximum, token).ConfigureAwait(false); }
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

    private static byte[] PromptInvalid(JsonElement element)
    {
        string summary = "ComfyUI rejected the prompt.";
        if (element.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                summary = message.GetString() ?? summary;
            else if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.String)
                summary = details.GetString() ?? summary;
        }
        summary = new string(summary.Where(c => !char.IsControl(c) || c is '\t' or '\r' or '\n').Take(512).ToArray());
        var root = new JsonObject
        {
            ["error"] = new JsonObject { ["code"] = "prompt.invalid", ["summary"] = summary }
        };
        if (element.TryGetProperty("node_errors", out var nodes) &&
            Encoding.UTF8.GetByteCount(nodes.GetRawText()) <= 64 * 1024)
            root["node_errors"] = CloneNode(nodes);
        return JsonSerializer.SerializeToUtf8Bytes(root, Relaxed);
    }

    private static JsonArray DevicesFrom(JsonElement system)
    {
        var devices = new JsonArray();
        if (system.TryGetProperty("devices", out var source) && source.ValueKind == JsonValueKind.Array)
            foreach (var device in source.EnumerateArray().Take(16))
            {
                var item = new JsonObject
                {
                    ["name"] = Text(device, "name") ?? Text(device, "device_name") ?? "GPU",
                    ["vram_total"] = Number(device, "vram_total") ?? Number(device, "total_vram") ?? 0,
                    ["vram_free"] = Number(device, "vram_free") ?? Number(device, "free_vram") ?? 0
                };
                devices.Add(item);
            }
        return devices;
    }

    private static string VersionFrom(JsonElement system) =>
        Text(system, "comfyui_version") ??
        (system.TryGetProperty("system", out var inner) ? Text(inner, "comfyui_version") : null) ??
        "";

    private static int QueueCount(JsonElement queue, string property) =>
        queue.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : 0;

    private static bool Provisioned(JsonObject models) =>
        ModelListContains(models, "diffusion_models", "z_image_turbo_bf16.safetensors") &&
        ModelListContains(models, "text_encoders", "qwen_3_4b.safetensors") &&
        ModelListContains(models, "vae", "ae.safetensors");

    private static bool ModelListContains(JsonObject models, string folder, string file) =>
        models[folder] is JsonArray list &&
        list.OfType<JsonValue>().Select(value => value.TryGetValue<string>(out var text) ? text : "")
            .Any(text => string.Equals(text, file, StringComparison.Ordinal) ||
                text.EndsWith("/" + file, StringComparison.Ordinal) ||
                text.EndsWith("\\" + file, StringComparison.Ordinal));

    private static bool PromptIsRunning(JsonElement queue, string promptId) =>
        queue.TryGetProperty("queue_running", out var running) && ContainsString(running, promptId);

    private static bool ContainsString(JsonElement element, string value)
    {
        if (element.ValueKind == JsonValueKind.String) return element.GetString() == value;
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Any(item => ContainsString(item, value));
        if (element.ValueKind == JsonValueKind.Object)
            return element.EnumerateObject().Any(property => ContainsString(property.Value, value));
        return false;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number : null;

    private static JsonNode? CloneNode(JsonElement element) => JsonNode.Parse(element.GetRawText());

    private static byte[] SerializeElement(JsonElement element) => JsonSerializer.SerializeToUtf8Bytes(element, Relaxed);

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string? MediaType(MediaTypeHeaderValue? header) => header?.MediaType switch
    {
        "image/png" => "image/png",
        "image/jpeg" => "image/jpeg",
        "image/webp" => "image/webp",
        _ => null
    };

    private static IReadOnlyList<byte[]> PageEvents(string mediaType, long total, long offset, byte[] page)
    {
        var texts = new List<byte[]>();
        var position = 0;
        do
        {
            var length = Math.Min(GatewayInferenceRoute.PictureMaximumEventImageBytes, page.Length - position);
            texts.Add(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["media_type"] = mediaType,
                ["total_bytes"] = total,
                ["offset"] = offset + position,
                ["bytes"] = length,
                ["data_base64"] = Convert.ToBase64String(page.AsSpan(position, length))
            }, Relaxed));
            position += length;
        }
        while (position < page.Length);
        return texts;
    }

    private static async Task SkipAsync(Stream stream, long bytes, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        var remaining = bytes;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            remaining -= read;
        }
    }

    private static async Task<byte[]> ReadExactAtMostAsync(Stream stream, int maximum, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (buffer.Length < maximum)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, maximum - (int)buffer.Length)), token)
                .ConfigureAwait(false);
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
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

    private sealed class Lease(GatewayInferenceRequest request, GatewayPrincipal principal, PictureRelayWorker owner)
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
