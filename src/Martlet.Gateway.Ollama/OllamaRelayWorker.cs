using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Martlet.Providers;
using Martlet.Providers.Ollama;

namespace Martlet.Gateway.Ollama;

/// <summary>
/// Gateway relay from a paired client to this host's own loopback Ollama server. Enabling it in the host
/// configuration is the host owner's standing permission for paired <c>voice</c> devices to send bounded
/// conversation text to the one model the owner installed; nothing else on the host is reachable through it.
/// </summary>
public sealed class OllamaRelayWorker : IOllamaGatewayInferenceWorker, IAsyncDisposable
{
    public const string DefaultDestinationId = "ollama-host";
    public const string DefaultWorkerId = "ollama-relay";
    /// <summary>Context window requested from Ollama; larger windows cost VRAM on small GPUs.</summary>
    public const int MaximumContextTokens = 8_192;
    private const int MaximumLineBytes = 64 * 1024;
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, false);
    private readonly Uri chat;
    private readonly string model;
    private readonly HttpClient http;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> running = new();

    public OllamaRelayWorker(Uri endpoint, string model,
        string destinationId = DefaultDestinationId, string workerId = DefaultWorkerId, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) ||
            !IPAddress.IsLoopback(address) || endpoint.AbsolutePath != "/")
            throw new ArgumentException("The Ollama relay only reaches a loopback http://127.0.0.1:<port>/ server.", nameof(endpoint));
        chat = new Uri(endpoint, "api/chat");
        this.model = model;
        var selection = new OllamaChatModelSelection(Alias(model), model);
        Route = GatewayInferenceRoute.OllamaChat(destinationId, workerId, selection, "ollama",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(model))));
        http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false, Credentials = null,
            AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(5)
        }, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public GatewayInferenceRoute Route { get; }

    /// <summary>The route's model ID for an Ollama model tag, for example llama3.2:3b becomes llama3.2-3b.</summary>
    public static string Alias(string model)
    {
        var alias = new string((model ?? "").Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-').ToArray())
            .TrimStart('.', '_', '-');
        return alias.Length > 64 ? alias[..64] : alias;
    }

    public ValueTask<GatewayInferencePermissionLease> AcquirePermissionAsync(
        GatewayInferenceRequest request, GatewayPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(principal);
        cancellationToken.ThrowIfCancellationRequested();
        if (lifetime.IsCancellationRequested)
            throw new GatewayInferenceWorkerException(GatewayInferenceWorkerFailure.Unavailable);
        if (!ReferenceEquals(request.Route, Route) || principal.Role != GatewayRole.Voice ||
            request.Payload is not GatewayOllamaChatPayload)
            throw new GatewayInferenceWorkerException(GatewayInferenceWorkerFailure.PermissionDenied);
        return ValueTask.FromResult<GatewayInferencePermissionLease>(new Lease(request, principal, this));
    }

    public async IAsyncEnumerable<GatewayInferenceEvent> ExecuteAsync(
        GatewayInferenceRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = (GatewayOllamaChatPayload)request.Payload;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        running[request.RequestId] = stop;
        var sequence = 0L;
        try
        {
            yield return Event(request, GatewayInferenceEventKind.Started, sequence++);
            var (response, failure) = await SendAsync(payload, stop.Token).ConfigureAwait(false);
            if (response is null)
            {
                yield return Event(request, GatewayInferenceEventKind.Failed, sequence, errorCode: failure);
                yield break;
            }
            using (response)
            {
                stop.Token.ThrowIfCancellationRequested();
                await using var body = await response.Content.ReadAsStreamAsync(stop.Token).ConfigureAwait(false);
                var reader = new LineReader(body);
                var pending = new StringBuilder();
                var outputBytes = 0;
                var spoke = false;
                while (true)
                {
                    var (text, done, error) = await NextAsync(reader, stop.Token).ConfigureAwait(false);
                    stop.Token.ThrowIfCancellationRequested();
                    if (error is not null)
                    {
                        yield return Event(request, GatewayInferenceEventKind.Failed, sequence, errorCode: error);
                        yield break;
                    }
                    if (!string.IsNullOrEmpty(text))
                    {
                        var bytes = Utf8.GetByteCount(text);
                        // Output beyond the route's bound is dropped; num_predict normally stops well before it.
                        if (outputBytes + bytes > Route.MaximumOutputBytes) break;
                        outputBytes += bytes;
                        spoke |= !string.IsNullOrWhiteSpace(text);
                        // Keep two events for the final delta and the terminal event.
                        if (pending.Length > 0 || sequence >= Route.MaximumEvents - 3) pending.Append(text);
                        else yield return Event(request, GatewayInferenceEventKind.TextDelta, sequence++, Utf8.GetBytes(text));
                    }
                    if (done) break;
                }
                if (pending.Length > 0)
                    yield return Event(request, GatewayInferenceEventKind.TextDelta, sequence++, Utf8.GetBytes(pending.ToString()));
                yield return spoke
                    ? Event(request, GatewayInferenceEventKind.Completed, sequence)
                    : Event(request, GatewayInferenceEventKind.Failed, sequence, errorCode: "worker.failed");
            }
        }
        finally
        {
            running.TryRemove(request.RequestId, out _);
        }
    }

    private async Task<(HttpResponseMessage? Response, string? Failure)> SendAsync(
        GatewayOllamaChatPayload payload, CancellationToken token)
    {
        using var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteStartArray("messages");
            if (payload.System is { } system) Message(writer, "system", system);
            foreach (var message in payload.History)
                Message(writer, message.Role == TextHistoryRole.User ? "user" : "assistant", message.Text);
            Message(writer, "user", payload.Input);
            writer.WriteEndArray();
            writer.WriteBoolean("stream", true);
            // Keep the model loaded: the host is lent to the bot, and a cold load would stall the first reply.
            writer.WriteNumber("keep_alive", -1);
            writer.WriteStartObject("options");
            writer.WriteNumber("temperature", payload.Temperature);
            writer.WriteNumber("num_predict", payload.MaximumOutputTokens);
            writer.WriteNumber("num_ctx", Math.Min(payload.MaximumContextTokens, MaximumContextTokens));
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, chat) { Content = new ByteArrayContent(body.ToArray()) };
        request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            token.ThrowIfCancellationRequested();
            return (null, "worker.unavailable");
        }
        if (response.StatusCode == HttpStatusCode.OK) return (response, null);
        // 404 is Ollama's "model not found": the model was removed or never pulled on this host.
        var failure = response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable
            ? "worker.unavailable" : "worker.failed";
        response.Dispose();
        return (null, failure);
    }

    private static void Message(Utf8JsonWriter writer, string role, string content)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteString("content", content);
        writer.WriteEndObject();
    }

    // Reads one NDJSON chunk of Ollama's /api/chat stream: {"message":{"content":"..."},"done":false} or {"error":"..."}.
    private static async Task<(string? Text, bool Done, string? Error)> NextAsync(LineReader reader, CancellationToken token)
    {
        byte[]? line;
        try { line = await reader.ReadLineAsync(token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidDataException)
        {
            // After Stop the gateway expects cancellation, not a late failure event.
            token.ThrowIfCancellationRequested();
            return (null, false, "worker.failed");
        }
        if (line is null) return (null, false, "worker.failed");
        if (line.Length == 0) return (null, false, null);
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, false, "worker.failed");
            if (root.TryGetProperty("error", out _)) return (null, false, "worker.failed");
            string? text = null;
            if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
                message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                text = content.GetString();
            var done = root.TryGetProperty("done", out var flag) && flag.ValueKind == JsonValueKind.True;
            return (text, done, null);
        }
        catch (JsonException) { return (null, false, "worker.failed"); }
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

    private sealed class LineReader(Stream stream)
    {
        private readonly byte[] buffer = new byte[8192];
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
                    throw new InvalidDataException("The Ollama response exceeded its bounds.");
                if (newline >= 0) return line.ToArray();
            }
        }
    }

    private sealed class Lease(GatewayInferenceRequest request, GatewayPrincipal principal, OllamaRelayWorker owner)
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
