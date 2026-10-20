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
public sealed partial class OllamaRelayWorker : IOllamaGatewayInferenceWorker, IGatewayOpenAiRelay, IAsyncDisposable
{
    public const string DefaultDestinationId = "ollama-host";
    public const string DefaultWorkerId = "ollama-relay";
    public const string DeepThinkingDestinationId = "deep-thinking-host";
    public const string DeepThinkingWorkerId = "deep-thinking-relay";
    /// <summary>Context window requested from Ollama unless the client asks for another; larger windows cost VRAM on small GPUs.</summary>
    public const int MaximumContextTokens = Martlet.Core.Settings.GenerationSettings.DefaultHostContextTokens;
    private const int MaximumLineBytes = 64 * 1024;
    /// <summary>The least text one joined event carries once the first half of a route's events went token by token.</summary>
    internal const int JoinedBytes = 256;
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, false);
    private readonly Uri chat;
    private readonly Uri create;
    private readonly Uri origin;
    /// <summary>The owner's own model app's Chat Completions address (deep-thinking-app), or null for Martlet's Ollama.</summary>
    private readonly Uri? app;
    private readonly string model;
    private readonly string roleKind;
    private readonly HttpClient http;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> running = new();

    public OllamaRelayWorker(Uri endpoint, string model,
        string destinationId = DefaultDestinationId, string workerId = DefaultWorkerId, HttpMessageHandler? handler = null,
        bool deepThinking = false, int slots = 1, int card = 1, string? appPath = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) ||
            !IPAddress.IsLoopback(address) || endpoint.AbsolutePath != "/")
            throw new ArgumentException("The Ollama relay only reaches a loopback http://127.0.0.1:<port>/ server.", nameof(endpoint));
        if (appPath is not null)
        {
            if (appPath is not ("/v1" or "/api/v1" or "/engines/v1") || !deepThinking)
                throw new ArgumentException("A model app's relay is a Thinking pool route on /v1, /api/v1 or /engines/v1.", nameof(appPath));
            app = new Uri(endpoint, appPath.TrimStart('/') + "/chat/completions");
        }
        chat = new Uri(endpoint, "api/chat");
        create = new Uri(endpoint, OllamaDraftHead.CreatePath.TrimStart('/'));
        origin = endpoint;
        this.model = model;
        roleKind = !deepThinking ? "ollama" : card <= 1 ? "deep-thinking" : $"deep-thinking-{card}";
        // An app's model name ("qwen/qwen3-32b", no Ollama tag) gets a route identity of its own; the request sends the real name.
        var selection = new OllamaChatModelSelection(Alias(model), app is null ? model : AppIdentity(model));
        Route = GatewayInferenceRoute.OllamaChat(destinationId, workerId, selection, app is null ? "ollama" : "openai-app",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(model))), deepThinking, slots, card);
        http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false, Credentials = null,
            AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(5)
        }, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public GatewayInferenceRoute Route { get; }

    /// <summary>The port of Martlet's own Ollama server; null when it relays to the owner's own app.</summary>
    public int? OwnPort => app is null ? origin.Port : null;

    /// <summary>The OpenAI-compatible endpoint is offered for Martlet's own Ollama only, not for the owner's app.</summary>
    public bool OffersOpenAi => app is null;

    /// <summary>The relay of the deep-thinking-app role: the Thinking pool route of card <paramref name="card"/>, relayed to a chat
    /// model that the owner's own model app on this host serves (LM Studio, llama.cpp, vLLM...) through its OpenAI-compatible
    /// API at <paramref name="endpoint"/> and <paramref name="path"/>. The app keeps the model loaded and manages its memory.</summary>
    public static OllamaRelayWorker App(Uri endpoint, string path, string model, HttpMessageHandler? handler = null, int card = 1) =>
        new(endpoint, model, card == 1 ? DeepThinkingDestinationId : $"deep-thinking-{card}-host",
            card == 1 ? "deep-thinking-app-relay" : $"deep-thinking-{card}-app-relay", handler, deepThinking: true, 1, card, path);

    /// <summary>What this role's Ollama has loaded now (<c>/api/ps</c>, at most two seconds), for the host's machine report; null
    /// when it doesn't answer. A read only: it never loads, unloads or waits for a reply.</summary>
    public async ValueTask<IReadOnlyList<GatewayLoadedModel>?> ReadLoadedAsync(CancellationToken cancellationToken)
    {
        if (lifetime.IsCancellationRequested || app is not null) return null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        limit.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var loaded = await Martlet.Providers.LocalModels.OllamaMeasuredMemory.ReadAsync(http, origin, null, limit.Token).ConfigureAwait(false);
            return loaded?.Select(m => new GatewayLoadedModel
            {
                Role = roleKind, Model = m.Model, Bytes = m.Bytes, GraphicsBytes = m.GraphicsBytes, ContextTokens = m.ContextTokens, Digest = m.Digest
            }).ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (ObjectDisposedException) { return null; }
    }

    /// <summary>The relay of a Thinking pool host role: its own Ollama (a second server beside the conversation model's), on
    /// Deep thinking's route, so a think there never waits for a reply or the other way round. <paramref name="slots"/> thinks
    /// run at once (the role's OLLAMA_NUM_PARALLEL), each in its own slot of the one loaded model. <paramref name="card"/> 2 to 4
    /// is the deep-thinking-2... role: another Ollama pinned to another graphics card, on a route, destination and worker of
    /// its own, so each card is a Thinking pool member of its own.</summary>
    public static OllamaRelayWorker DeepThinking(Uri endpoint, string model, HttpMessageHandler? handler = null, int slots = 1, int card = 1) =>
        new(endpoint, model, card == 1 ? DeepThinkingDestinationId : $"deep-thinking-{card}-host",
            card == 1 ? DeepThinkingWorkerId : $"deep-thinking-{card}-relay", handler, deepThinking: true, slots, card);

    /// <summary>An Ollama-shaped name for an app's model, for the route's identity: its alias in lower case with the tag "app".</summary>
    private static string AppIdentity(string model)
    {
        var name = new string(Alias(model).ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-').ToArray()).TrimStart('.', '_', '-');
        return (name.Length == 0 ? "model" : name.Length > 80 ? name[..80] : name) + ":app";
    }

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
                int outputBytes = 0, pendingBytes = 0;
                var spoke = false;
                // Ollama streams a token a line. Each token goes out as it comes for the first half of the route's events (the
                // start of a reply, which speech waits for); after that tokens are joined into pieces of at least JoinedBytes,
                // and the last events carry what is left in pieces an event takes, so a reply of the largest budget fits.
                var pieceBytes = Math.Max(64, Route.MaximumEventBytes / 4);
                var reserved = Route.MaximumOutputBytes / pieceBytes + 1;
                while (true)
                {
                    var (text, done, error) = app is null
                        ? await NextAsync(reader, payload.Audio is not null && !spoke, stop.Token).ConfigureAwait(false)
                        : await NextAppAsync(reader, payload.Audio is not null && !spoke, stop.Token).ConfigureAwait(false);
                    stop.Token.ThrowIfCancellationRequested();
                    if (error is not null)
                    {
                        yield return Event(request, GatewayInferenceEventKind.Failed, sequence, errorCode: error);
                        yield break;
                    }
                    if (!string.IsNullOrEmpty(text))
                    {
                        var bytes = Utf8.GetByteCount(text);
                        if (outputBytes + bytes > Route.MaximumOutputBytes)
                        {
                            // Longer than the route takes (num_predict normally stops well before it): what came so far goes,
                            // then the piece that goes over the bound, so the gateway ends the reply with stream.limit and the
                            // client says the answer was too long instead of taking a cut reply as complete.
                            foreach (var piece in Pieces(pending.ToString(), pieceBytes))
                                yield return Event(request, GatewayInferenceEventKind.TextDelta, sequence++, piece);
                            yield return Event(request, GatewayInferenceEventKind.TextDelta, sequence++, Utf8.GetBytes(text));
                            yield break;
                        }
                        outputBytes += bytes;
                        spoke |= !string.IsNullOrWhiteSpace(text);
                        pending.Append(text);
                        pendingBytes += bytes;
                        if (sequence < Route.MaximumEvents / 2 ||
                            pendingBytes >= JoinedBytes && sequence < Route.MaximumEvents - 2 - reserved)
                        {
                            yield return Event(request, GatewayInferenceEventKind.TextDelta, sequence++, Utf8.GetBytes(pending.ToString()));
                            pending.Clear();
                            pendingBytes = 0;
                        }
                    }
                    if (done) break;
                }
                foreach (var piece in Pieces(pending.ToString(), pieceBytes))
                    yield return Event(request, GatewayInferenceEventKind.TextDelta, sequence++, piece);
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
        if (app is not null) return await SendAppAsync(app, payload, token).ConfigureAwait(false);
        using var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteStartArray("messages");
            if (payload.System is { } system) Message(writer, "system", system);
            foreach (var message in payload.History)
                Message(writer, message.Role == TextHistoryRole.User ? "user" : "assistant", message.Text);
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", payload.Input);
            // Ollama's native chat takes base64 images on the message; text-only models answer with an error. A recording rides
            // there too: Ollama finds the WAV among the images and a model that hears (Gemma 4 E2B or E4B) hears it.
            if (payload.Images.Count > 0 || payload.Audio is not null)
            {
                writer.WriteStartArray("images");
                foreach (var image in payload.Images) writer.WriteStringValue(image);
                if (payload.Audio is { } audio) writer.WriteStringValue(audio);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteBoolean("stream", true);
            // Keep the model loaded: the host is lent to the bot, and a cold load would stall the first reply.
            writer.WriteNumber("keep_alive", -1);
            // Thinking steps: false answers without thinking first; unset keeps the model's default.
            if (payload.Sampling.Think is { } think) writer.WriteBoolean("think", think);
            writer.WriteStartObject("options");
            writer.WriteNumber("temperature", payload.Temperature);
            writer.WriteNumber("num_predict", payload.MaximumOutputTokens);
            var sampling = payload.Sampling;
            // The client's context size replaces the relay's VRAM-friendly default; older clients never send one.
            var context = sampling.ContextTokens ?? Math.Min(payload.MaximumContextTokens, MaximumContextTokens);
            // OpenAI-compatible requests use the same context size, so they never make Ollama load the model again.
            Volatile.Write(ref ownerContext, context);
            writer.WriteNumber("num_ctx", context);
            if (sampling.TopP is { } topP) writer.WriteNumber("top_p", topP);
            if (sampling.TopK is { } topK) writer.WriteNumber("top_k", topK);
            if (sampling.MinP is { } minP) writer.WriteNumber("min_p", minP);
            if (sampling.RepeatPenalty is { } repeat) writer.WriteNumber("repeat_penalty", repeat);
            if (sampling.FrequencyPenalty is { } frequency) writer.WriteNumber("frequency_penalty", frequency);
            if (sampling.PresencePenalty is { } presence) writer.WriteNumber("presence_penalty", presence);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        var bytes = body.ToArray();
        var response = await PostAsync(chat, bytes, token).ConfigureAwait(false);
        if (response is null) return (null, "worker.unavailable");
        var error = response.StatusCode == HttpStatusCode.OK ? "" : await ErrorAsync(response, token).ConfigureAwait(false);
        if (OllamaDraftHead.FailedToLoad(error))
        {
            // Gemma 4's bundled draft model didn't fit next to it on the GPU: save draft_num_predict 0 on the model and retry.
            response.Dispose();
            using (var repair = await PostAsync(create, Encoding.UTF8.GetBytes(OllamaDraftHead.DisableRequest(model)), token)
                .ConfigureAwait(false))
                if (repair is not { IsSuccessStatusCode: true }) return (null, "worker.failed");
            response = await PostAsync(chat, bytes, token).ConfigureAwait(false);
            if (response is null) return (null, "worker.unavailable");
            error = response.StatusCode == HttpStatusCode.OK ? "" : await ErrorAsync(response, token).ConfigureAwait(false);
        }
        if (response.StatusCode == HttpStatusCode.OK) return (response, null);
        var status = response.StatusCode;
        response.Dispose();
        // A model that doesn't hear refuses the recording: request.invalid tells the client to send the words alone. Ollama's
        // 400 "does not support thinking" is about Thinking steps, not the recording.
        if (payload.Audio is not null && (RefusesAudio(error) ||
            status == HttpStatusCode.BadRequest && !error.Contains("think", StringComparison.OrdinalIgnoreCase)))
            return (null, "request.invalid");
        // 404 is Ollama's "model not found": the model was removed or never pulled on this host.
        return (null, status is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable
            ? "worker.unavailable" : "worker.failed");
    }

    /// <summary><paramref name="text"/> as UTF-8 pieces of at most <paramref name="maximumBytes"/> each, split between
    /// characters; none for empty text.</summary>
    public static IEnumerable<byte[]> Pieces(string text, int maximumBytes)
    {
        var piece = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maximumBytes && piece.Length > 0)
            {
                yield return Utf8.GetBytes(piece.ToString());
                piece.Clear();
                bytes = 0;
            }
            piece.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        if (piece.Length > 0) yield return Utf8.GetBytes(piece.ToString());
    }

    /// <summary>Whether Ollama's error says the model can't take the recording (an audio, image or modality refusal).</summary>
    public static bool RefusesAudio(string error) =>
        !error.Contains("think", StringComparison.OrdinalIgnoreCase) && (
        error.Contains("audio", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("modalit", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("multimodal", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("image", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("does not support", StringComparison.OrdinalIgnoreCase));

    private async Task<HttpResponseMessage?> PostAsync(Uri target, byte[] body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            token.ThrowIfCancellationRequested();
            return null;
        }
    }

    // The start of Ollama's error body, read once (the draft repair and the refused-recording check both look at it).
    private static async Task<string> ErrorAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return text.Length <= MaximumLineBytes ? text : text[..MaximumLineBytes];
        }
        catch (Exception error) when (error is HttpRequestException or IOException) { return ""; }
    }

    private static void Message(Utf8JsonWriter writer, string role, string content)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteString("content", content);
        writer.WriteEndObject();
    }

    // Reads one NDJSON chunk of Ollama's /api/chat stream: {"message":{"content":"..."},"done":false} or {"error":"..."}.
    // With a recording (audio), an error that refuses it is request.invalid, so the client sends the words alone.
    private static async Task<(string? Text, bool Done, string? Error)> NextAsync(
        LineReader reader, bool audio, CancellationToken token)
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
            if (root.TryGetProperty("error", out var refusal))
                return (null, false, audio && refusal.ValueKind == JsonValueKind.String && RefusesAudio(refusal.GetString() ?? "")
                    ? "request.invalid" : "worker.failed");
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
