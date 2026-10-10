using System.Net.Http;
using System.Text.Json;
using Martlet.Core.Planning;

namespace Martlet.Providers.LocalModels;

/// <summary>What models really take in an Ollama server, from its <c>/api/ps</c> (<c>size</c>, <c>size_vram</c>,
/// <c>context_length</c>, <c>digest</c>), kept in model-memory.json (<see cref="MeasuredModelMemory"/>) so a measured number
/// replaces the estimate. Asked only after a model loaded (the talk window's warm-up, Test model, Load model), never while a reply
/// is on its way; it reads only, so it never loads or unloads a model.</summary>
public static class OllamaMeasuredMemory
{
    /// <summary>The key a server's measurements are kept under: its origin, such as http://127.0.0.1:11434.</summary>
    public static string HostKey(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return origin.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>What Ollama at <paramref name="origin"/> has loaded now, or null when it doesn't answer.</summary>
    public static async Task<IReadOnlyList<MeasuredModelUse>?> ReadAsync(HttpClient client, Uri origin, TimeProvider? clock, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await client.GetAsync(new Uri(origin, "api/ps"), limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(limit.Token).ConfigureAwait(false);
            if (bytes.Length > 4 * 1024 * 1024) return null;
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("models", out var models) ||
                models.ValueKind != JsonValueKind.Array) return null;
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            var host = HostKey(origin);
            return [.. models.EnumerateArray().Take(64)
                .Select(m => (Name: Text(m, "name") ?? Text(m, "model"), Size: Long(m, "size"), Vram: Long(m, "size_vram") ?? 0,
                    Context: Long(m, "context_length"), Digest: Text(m, "digest")))
                .Where(m => m.Name is { Length: > 0 and <= 256 } && m.Size is > 0)
                .Select(m => new MeasuredModelUse
                {
                    Host = host, Model = m.Name!, Bytes = m.Size!.Value, GraphicsBytes = m.Vram,
                    ContextTokens = m.Context is > 0 and <= 100_000_000 ? (int)m.Context.Value : null,
                    Digest = m.Digest is { Length: <= 128 } digest ? digest : null, MeasuredAt = now
                })];
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is HttpRequestException or JsonException) { return null; }
    }

    /// <summary>Reads what <paramref name="model"/> takes in Ollama at <paramref name="origin"/> now and keeps it in
    /// <paramref name="dataDirectory"/>'s model-memory.json when it changed. Returns the measurement (null when the model isn't
    /// loaded or Ollama doesn't answer) and whether it was saved.</summary>
    public static async Task<(MeasuredModelUse? Use, bool Saved)> RecordAsync(HttpClient client, Uri origin, string model, string? dataDirectory,
        CancellationToken token, TimeProvider? clock = null)
    {
        if (await ReadAsync(client, origin, clock, token).ConfigureAwait(false) is not { } loaded) return (null, false);
        var use = loaded.FirstOrDefault(m => Same(m.Model, model));
        return use is null ? (null, false) : (use, MeasuredModelMemory.Record(dataDirectory, use));
    }

    private static bool Same(string name, string model) =>
        string.Equals(Tagged(name), Tagged(model), StringComparison.OrdinalIgnoreCase);

    private static string Tagged(string name) => name.Contains(':', StringComparison.Ordinal) ? name : name + ":latest";

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}
