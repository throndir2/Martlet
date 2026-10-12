using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Martlet.Core.Search;

namespace Martlet.Providers.Ollama;

/// <summary>Why an embedding request to Ollama failed: <see cref="Missing"/> when Ollama doesn't have the model.</summary>
public sealed class OllamaEmbedException(string message, bool missing = false) : Exception(message)
{
    public bool Missing { get; } = missing;
}

/// <summary>An embedding model in Ollama (<c>/api/embed</c>, docs/SEARCH.md, "Finding by meaning"). It always runs on the
/// processor (<c>num_gpu</c> 0) with a few threads, so it never takes graphics memory from the conversation's model, and Ollama
/// runs it beside that model in a process of its own, so the conversation's prompt cache stays. Ollama keeps it loaded for an hour
/// after its last use. Texts too long for the model are cut by Ollama (<c>truncate</c>).</summary>
public sealed class OllamaEmbedder : ITextEmbedder
{
    public const string KeepAlive = "1h";
    private readonly HttpClient client;
    private readonly Uri origin;
    private readonly int threads;

    /// <param name="origin">Ollama's address, such as <c>http://127.0.0.1:11434/</c>.</param>
    /// <param name="threads">The processor threads Ollama gives the model (null: <see cref="DefaultThreads"/>).</param>
    public OllamaEmbedder(HttpClient client, Uri origin, string name, int? threads = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.origin = origin ?? throw new ArgumentNullException(nameof(origin));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        this.threads = Math.Clamp(threads ?? DefaultThreads, 1, 64);
    }

    /// <summary>A quarter of this PC's processor threads, from 1 to 4: enough for a question in tens of milliseconds, while the
    /// rest stay free for the conversation.</summary>
    public static int DefaultThreads => Math.Clamp(Environment.ProcessorCount / 4, 1, 4);

    /// <summary>The Ollama model's name.</summary>
    public string Name { get; }

    public string Model => "ollama:" + Name;

    public int MaximumBatch => 8;

    /// <summary>The text an embedding model of <paramref name="model"/>'s family wants before a question or a document (empty
    /// for models that want none).</summary>
    public static string Prefix(string model, EmbeddingUse use)
    {
        var name = (model ?? "").ToLowerInvariant();
        var query = use == EmbeddingUse.Query;
        if (name.Contains("nomic-embed", StringComparison.Ordinal)) return query ? "search_query: " : "search_document: ";
        if (name.Contains("embeddinggemma", StringComparison.Ordinal)) return query ? "task: search result | query: " : "title: none | text: ";
        if (name.Contains("e5", StringComparison.Ordinal) && !name.Contains("qwen", StringComparison.Ordinal)) return query ? "query: " : "passage: ";
        if (name.Contains("qwen3-embedding", StringComparison.Ordinal))
            return query ? "Instruct: Given a question, find the passages that answer it\nQuery: " : "";
        if (query && (name.Contains("mxbai-embed", StringComparison.Ordinal) || name.Contains("snowflake-arctic-embed", StringComparison.Ordinal) ||
                name.Contains("bge-", StringComparison.Ordinal) && !name.Contains("bge-m3", StringComparison.Ordinal)))
            return "Represent this sentence for searching relevant passages: ";
        return "";
    }

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUse use, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0) return [];
        var prefix = Prefix(Name, use);
        var body = JsonSerializer.Serialize(new
        {
            model = Name,
            input = texts.Select(text => prefix + (text ?? "")).ToArray(),
            truncate = true,
            keep_alive = KeepAlive,
            options = new { num_gpu = 0, num_thread = threads }
        });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try { response = await client.PostAsync(new Uri(origin, "api/embed"), content, token).ConfigureAwait(false); }
        catch (HttpRequestException error) { throw new OllamaEmbedException("Ollama didn't answer: " + error.Message); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new OllamaEmbedException("Ollama didn't answer in time."); }
        using (response)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            if (bytes.Length > 256 * 1024 * 1024) throw new OllamaEmbedException("Ollama's answer was too large.");
            if (!response.IsSuccessStatusCode)
            {
                var said = Error(bytes);
                var missing = response.StatusCode == HttpStatusCode.NotFound ||
                    said?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true;
                throw new OllamaEmbedException(said is null ? $"Ollama answered {(int)response.StatusCode}." : "Ollama said: " + said, missing);
            }
            try
            {
                using var document = JsonDocument.Parse(bytes);
                if (!document.RootElement.TryGetProperty("embeddings", out var list) || list.ValueKind != JsonValueKind.Array ||
                    list.GetArrayLength() != texts.Count)
                    throw new OllamaEmbedException("Ollama's answer had no embedding for each text.");
                var vectors = new float[texts.Count][];
                var i = 0;
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() is 0 or > 16_384)
                        throw new OllamaEmbedException("Ollama's answer had an empty embedding.");
                    var vector = new float[item.GetArrayLength()];
                    var j = 0;
                    foreach (var number in item.EnumerateArray()) vector[j++] = number.GetSingle();
                    if (i > 0 && vector.Length != vectors[0].Length) throw new OllamaEmbedException("Ollama's embeddings have different lengths.");
                    vectors[i++] = vector;
                }
                return vectors;
            }
            catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
            {
                throw new OllamaEmbedException("Ollama's answer couldn't be read.");
            }
        }
    }

    // Ollama's own explanation ({"error":"..."}), on one short line.
    private static string? Error(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String && error.GetString() is { Length: > 0 } text)
            {
                var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                return line.Length <= 200 ? line : line[..200] + "…";
            }
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>Why loading <paramref name="model"/> now could push a model off the graphics card or out of memory, or null when it
    /// is safe: it is loaded already, or Ollama has fewer than <paramref name="most"/> models loaded and all of them are on the
    /// graphics card (a model that runs on the processor, all or in part, already uses much of the computer's memory, and Ollama
    /// unloads a model when memory runs short).</summary>
    public static string? HoldBack(IReadOnlyList<OllamaLoadedModel> loaded, string model, int most = 3)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        if (OllamaSideBySide.Find(loaded, model) is not null) return null;
        if (loaded.Count >= most)
            return $"Ollama has {loaded.Count} models loaded, and loading {model} could make it unload one of them";
        if (loaded.FirstOrDefault(m => !m.OnGraphicsCard) is { } shared)
            return $"{shared.Name} runs on this PC's processor, so loading {model} beside it could make Ollama unload it";
        return null;
    }
}
