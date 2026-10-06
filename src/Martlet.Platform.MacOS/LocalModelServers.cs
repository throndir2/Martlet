using System.Text.Json;

namespace Martlet.Platform.MacOS;

/// <summary>A model server answering on this computer's loopback address, with the Chat Completions base URL Martlet's
/// Thinking settings take.</summary>
public sealed record LocalModelServer(string Id, string Name, string ChatCompletionsBaseUrl, IReadOnlyList<string> Models);

/// <summary>Finds Ollama, LM Studio and Docker Model Runner on this computer (loopback only, never the network), so the
/// companion can offer them for thinking. On an Apple silicon Mac all three run models on the GPU through Metal; on an
/// Intel Mac they run on the CPU. Nothing is started or installed; a server that is not running is simply not listed.</summary>
public static class LocalModelServers
{
    public sealed record Candidate(string Id, string Name, Uri ModelsUrl, string ChatCompletionsBaseUrl, bool OllamaTags);

    public static IReadOnlyList<Candidate> Candidates { get; } =
    [
        new("ollama", "Ollama", new("http://127.0.0.1:11434/api/tags"), "http://127.0.0.1:11434/v1", OllamaTags: true),
        new("lm-studio", "LM Studio", new("http://127.0.0.1:1234/v1/models"), "http://127.0.0.1:1234/v1", OllamaTags: false),
        // Docker Model Runner with host-side TCP enabled (Docker Desktop > AI, default port 12434).
        new("docker-model-runner", "Docker Model Runner", new("http://127.0.0.1:12434/engines/v1/models"),
            "http://127.0.0.1:12434/engines/v1", OllamaTags: false)
    ];

    /// <summary>Asks every candidate at once, each for at most <paramref name="timeout"/> (default 1.5 s).</summary>
    public static async Task<IReadOnlyList<LocalModelServer>> DetectAsync(HttpMessageHandler? handler = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = timeout ?? TimeSpan.FromSeconds(1.5);
        var results = await Task.WhenAll(Candidates.Select(c => TryAsync(client, c, cancellationToken))).ConfigureAwait(false);
        return [.. results.OfType<LocalModelServer>()];
    }

    private static async Task<LocalModelServer?> TryAsync(HttpClient client, Candidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(candidate.ModelsUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var models = ParseModels(json, candidate.OllamaTags);
            return models is null ? null : new(candidate.Id, candidate.Name, candidate.ChatCompletionsBaseUrl, models);
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    /// <summary>Model names from Ollama's /api/tags ({"models":[{"name"}]}) or an OpenAI-style /v1/models
    /// ({"data":[{"id"}]}); null when the reply is neither (another program on that port).</summary>
    internal static IReadOnlyList<string>? ParseModels(string json, bool ollamaTags)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var (list, field) = ollamaTags ? ("models", "name") : ("data", "id");
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(list, out var items) || items.ValueKind != JsonValueKind.Array)
                return null;
            return [.. items.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(field, out var name) &&
                    name.ValueKind == JsonValueKind.String ? name.GetString() : null)
                .OfType<string>().Where(name => name.Length > 0)];
        }
        catch (JsonException) { return null; }
    }
}
