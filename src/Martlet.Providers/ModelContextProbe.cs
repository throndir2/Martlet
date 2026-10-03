using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Martlet.Providers;

/// <summary>What checking a Thinking model's context found. <see cref="ContextTokens"/> is how much the server takes per request
/// (for Ollama on this PC, the context it gives the model), null when the server doesn't say; <see cref="ModelMaximum"/> is
/// the model's own maximum when the server reports that too. <see cref="Reached"/> is false when the server couldn't be asked.
/// <see cref="Summary"/> says it in words, naming the model but never a key or anything said.</summary>
public sealed record ModelContextReport(int? ContextTokens, int? ModelMaximum, string Source, string Summary, bool Reached = true);

/// <summary>Asks a Thinking model's server how much context the model takes, the way Companion › Replies and setting up a model
/// do. It reads only model metadata: an OpenAI-compatible model list (<c>GET {base}/models</c>: OpenRouter, Together and others
/// report <c>context_length</c>, vLLM <c>max_model_len</c>, Groq <c>context_window</c>, Mistral and LM Studio
/// <c>max_context_length</c>, llama.cpp <c>meta.n_ctx_train</c>), then, for a server on this PC that doesn't say there,
/// LM Studio's, llama.cpp's and Ollama's own endpoints. Ollama on this PC is asked through its API (<c>/api/show</c>,
/// <c>/api/ps</c>). Nothing anyone said is sent; a saved key goes only to its own base URL, and redirects aren't followed.</summary>
public static class ModelContextProbe
{
    private const int MaximumResponseBytes = 33_554_432;
    private const int Smallest = 256;
    private const int Largest = 100_000_000;

    /// <summary>A client for checks: no redirects (a key never follows one), and no proxy for a server on this PC.</summary>
    public static HttpClient CreateClient(bool loopback) =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = !loopback, ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

    public static bool IsLoopback(Uri uri) =>
        IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);

    /// <summary>Checks <paramref name="modelId"/> on the OpenAI-compatible server at <paramref name="baseUrl"/>
    /// (<paramref name="serverName"/> names it in the summary: "OpenRouter").</summary>
    public static async Task<ModelContextReport> ChatCompletionsAsync(HttpClient client, string baseUrl, string modelId,
        string? apiKey, string serverName, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(client);
        var root = new Uri(baseUrl.TrimEnd('/') + "/");
        var models = await GetAsync(client, HttpMethod.Get, new Uri(root, "models"), null, apiKey, TimeSpan.FromSeconds(30), token)
            .ConfigureAwait(false);
        if (models.Failure is { } failure && !IsLoopback(root))
            return new(null, null, serverName, $"Couldn't ask {serverName} about {modelId}: {failure}.", Reached: false);
        int? tokens = null;
        var listed = false;
        using (models.Body)
            if (models.Body is { } document && Entry(document.RootElement, modelId) is { } entry)
            {
                listed = true;
                tokens = FromEntry(entry);
            }
        var source = $"{serverName}'s model list";
        if (tokens is null && IsLoopback(root))
        {
            // Servers on this PC often say it only through their own API.
            var origin = new Uri(root.GetLeftPart(UriPartial.Authority) + "/");
            (tokens, source) = await LoopbackAsync(client, origin, modelId, serverName, token).ConfigureAwait(false);
            if (tokens is null && models.Failure is { } local)
                return new(null, null, serverName, $"Couldn't ask {serverName} about {modelId}: {local}.", Reached: false);
        }
        if (tokens is { } found)
            return new(found, null, source, $"{Capital(source)} says {modelId} takes {found:N0} tokens.");
        return new(null, null, source, listed
            ? $"{Capital(source)} lists {modelId} but doesn't say how much context it takes."
            : $"{Capital(source)} doesn't list {modelId}, so Martlet can't tell how much context it takes.");
    }

    /// <summary>Checks <paramref name="model"/> in Ollama at <paramref name="origin"/> (this PC's: http://127.0.0.1:11434): the
    /// context Ollama gives it when loaded (<c>/api/ps</c>), its Modelfile's <c>num_ctx</c>, and the model's own maximum
    /// (<c>/api/show</c>). With <paramref name="load"/>, a model that isn't loaded is loaded first (an empty <c>/api/generate</c>,
    /// as a reply would), so the context Ollama gives it is known exactly.</summary>
    public static async Task<ModelContextReport> OllamaAsync(HttpClient client, Uri origin, string model, bool load,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(client);
        const string source = "Ollama on this PC";
        var show = await GetAsync(client, HttpMethod.Post, new Uri(origin, "api/show"), JsonSerializer.Serialize(new { model }), null,
            TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        int? maximum = null, modelfile = null;
        using (show.Body)
        {
            if (show.Status == HttpStatusCode.NotFound)
                return new(null, null, source, $"{model} isn't downloaded in Ollama on this PC.", Reached: true);
            if (show.Failure is { } failure)
                return new(null, null, source, $"Couldn't ask Ollama on this PC about {model}: {failure}.", Reached: false);
            if (show.Body is { } document)
            {
                maximum = ShowMaximum(document.RootElement);
                modelfile = ModelfileContext(document.RootElement);
            }
        }
        var loaded = await LoadedContextAsync(client, origin, model, token).ConfigureAwait(false);
        if (loaded is null && load)
        {
            using var started = await GetAsync(client, HttpMethod.Post, new Uri(origin, "api/generate"),
                JsonSerializer.Serialize(new { model }), null, TimeSpan.FromMinutes(5), token).ConfigureAwait(false);
            if (started.Failure is null) loaded = await LoadedContextAsync(client, origin, model, token).ConfigureAwait(false);
        }
        var of = maximum is { } most ? $" (the model holds up to {most:N0})" : "";
        if (loaded is { } given)
            return new(given, maximum, source, $"Ollama on this PC gives {model} {given:N0} tokens{of}. " +
                "Ollama's context length setting decides it.");
        if (modelfile is { } set)
            return new(set, maximum, source, $"{model}'s Modelfile gives it {set:N0} tokens in Ollama on this PC{of}.");
        return new(null, maximum, source, $"Ollama on this PC hasn't loaded {model} yet, so its context isn't known{of}. " +
            "Test the model or start talking, then check again.");
    }

    /// <summary>The context Ollama gave <paramref name="model"/> when it loaded it, or null when it isn't loaded.</summary>
    public static async Task<int?> LoadedContextAsync(HttpClient client, Uri origin, string model, CancellationToken token)
    {
        using var running = await GetAsync(client, HttpMethod.Get, new Uri(origin, "api/ps"), null, null, TimeSpan.FromSeconds(10), token)
            .ConfigureAwait(false);
        if (running.Body?.RootElement is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return null;
        foreach (var entry in models.EnumerateArray())
            if (SameOllamaModel(Text(entry, "name"), model) || SameOllamaModel(Text(entry, "model"), model))
                return Int(entry, "context_length");
        return null;
    }

    private static bool SameOllamaModel(string? name, string model) => name is not null &&
        (string.Equals(name, model, StringComparison.OrdinalIgnoreCase) ||
         !model.Contains(':', StringComparison.Ordinal) && string.Equals(name, model + ":latest", StringComparison.OrdinalIgnoreCase));

    // model_info carries "<architecture>.context_length" (for example "gemma3.context_length").
    private static int? ShowMaximum(JsonElement root)
    {
        if (!root.TryGetProperty("model_info", out var info) || info.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in info.EnumerateObject())
            if (property.Name.EndsWith(".context_length", StringComparison.Ordinal) && Number(property.Value) is { } value)
                return value;
        return null;
    }

    // "parameters" is the Modelfile's PARAMETER lines as text: "num_ctx                        32768\nstop ...".
    private static int? ModelfileContext(JsonElement root)
    {
        if (Text(root, "parameters") is not { } parameters) return null;
        foreach (var line in parameters.Split('\n'))
        {
            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] == "num_ctx" &&
                int.TryParse(parts[1].Trim().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
                value is >= Smallest and <= Largest)
                return value;
        }
        return null;
    }

    private static async Task<(int?, string)> LoopbackAsync(HttpClient client, Uri origin, string modelId, string serverName,
        CancellationToken token)
    {
        // LM Studio: the loaded context, else the model's maximum.
        using (var studio = await GetAsync(client, HttpMethod.Get, new Uri(origin, "api/v0/models"), null, null, TimeSpan.FromSeconds(5), token)
            .ConfigureAwait(false))
            if (studio.Body is { } document && Entry(document.RootElement, modelId) is { } entry &&
                (Int(entry, "loaded_context_length") ?? Int(entry, "max_context_length")) is { } studioTokens)
                return (studioTokens, $"{serverName}'s LM Studio API");
        // llama.cpp: the context the server was started with.
        using (var props = await GetAsync(client, HttpMethod.Get, new Uri(origin, "props"), null, null, TimeSpan.FromSeconds(5), token)
            .ConfigureAwait(false))
            if (props.Body?.RootElement is { ValueKind: JsonValueKind.Object } root &&
                (Nested(root, "default_generation_settings", "n_ctx") ?? Int(root, "n_ctx")) is { } llama)
                return (llama, $"{serverName}'s llama.cpp settings");
        // Ollama at its default address but entered as a custom server.
        var ollama = await OllamaAsync(client, origin, modelId, load: false, token).ConfigureAwait(false);
        return ollama.ContextTokens is { } given ? (given, "Ollama on this PC") : (null, $"{serverName}'s model list");
    }

    /// <summary>The model's entry in an OpenAI-style model list (<c>{"data":[...]}</c>, a bare array, or <c>{"models":[...]}</c>),
    /// by exact ID, then ignoring case.</summary>
    internal static JsonElement? Entry(JsonElement root, string modelId)
    {
        var list = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array ? models
            : (JsonElement?)null;
        if (list is not { } items) return null;
        JsonElement? caseless = null;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = Text(item, "id") ?? Text(item, "model") ?? Text(item, "name");
            if (string.Equals(id, modelId, StringComparison.Ordinal)) return item;
            if (caseless is null && string.Equals(id, modelId, StringComparison.OrdinalIgnoreCase)) caseless = item;
        }
        return caseless;
    }

    /// <summary>The context a model-list entry reports, most specific first: what the server actually serves (vLLM, LM Studio,
    /// llama.cpp), then the model's context (the smaller of OpenRouter's model and top provider), then other providers' names.</summary>
    internal static int? FromEntry(JsonElement entry)
    {
        var served = Int(entry, "max_model_len") ?? Int(entry, "loaded_context_length") ?? Nested(entry, "meta", "n_ctx");
        if (served is not null) return served;
        var model = Int(entry, "context_length");
        var provider = Nested(entry, "top_provider", "context_length");
        if (model is { } a && provider is { } b) return Math.Min(a, b);
        return model ?? provider ?? Int(entry, "context_window") ?? Int(entry, "max_context_length") ??
            Nested(entry, "metadata", "context_length") ?? Int(entry, "max_input_tokens") ?? Int(entry, "input_token_limit") ??
            Int(entry, "inputTokenLimit") ?? Nested(entry, "meta", "n_ctx_train");
    }

    private static int? Nested(JsonElement element, string outer, string inner) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(outer, out var value) ? Int(value, inner) : null;

    private static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? Number(value) : null;

    private static int? Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt64(out var whole) && whole is >= Smallest and <= Largest => (int)whole,
        JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var text) &&
            text is >= Smallest and <= Largest => (int)text,
        _ => null
    };

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private sealed class Answer(HttpStatusCode? status, JsonDocument? body, string? failure) : IDisposable
    {
        internal HttpStatusCode? Status { get; } = status;
        internal JsonDocument? Body { get; } = body;
        /// <summary>Why it didn't answer with a readable 2xx JSON body, in words; null when it did.</summary>
        internal string? Failure { get; } = failure;
        public void Dispose() => Body?.Dispose();
    }

    private static async Task<Answer> GetAsync(HttpClient client, HttpMethod method, Uri uri, string? body, string? apiKey,
        TimeSpan within, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(within);
        try
        {
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (apiKey is { Length: > 0 }) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new(response.StatusCode, null, (int)response.StatusCode switch
                {
                    401 or 403 => $"it refused the request (error {(int)response.StatusCode}); check the API key",
                    >= 300 and <= 399 => $"it redirected the request (error {(int)response.StatusCode})",
                    _ => $"it answered error {(int)response.StatusCode}"
                });
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                return new(response.StatusCode, null, "its answer was too large");
            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81_920];
            int read;
            while ((read = await stream.ReadAsync(chunk, limit.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaximumResponseBytes) return new(response.StatusCode, null, "its answer was too large");
                buffer.Write(chunk, 0, read);
            }
            try { return new(response.StatusCode, JsonDocument.Parse(buffer.ToArray()), null); }
            catch (JsonException) { return new(response.StatusCode, null, "its answer wasn't JSON"); }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(null, null, $"it didn't answer within {within.TotalSeconds:0} seconds");
        }
        catch (HttpRequestException error)
        {
            return new(null, null, error.HttpRequestError switch
            {
                HttpRequestError.ConnectionError => "nothing answered at its address",
                HttpRequestError.NameResolutionError => "its address couldn't be found",
                HttpRequestError.SecureConnectionError => "its secure connection failed",
                _ => "the connection failed"
            });
        }
        catch (IOException) { return new(null, null, "the connection dropped"); }
    }
}
