using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Martlet.Providers.Ollama;

/// <summary>A model Ollama has loaded (<c>/api/ps</c>): its whole size and how much of it is on the graphics card, in bytes.</summary>
public sealed record OllamaLoadedModel(string Name, long Size, long SizeVram)
{
    public bool OnGraphicsCard => SizeVram >= Size;
}

/// <summary>This PC's graphics card memory: the total, and how much is in use right now when the driver says (nvidia-smi).</summary>
public sealed record GraphicsMemory(long TotalBytes, long? UsedBytes);

/// <summary>Whether Deep thinking's model fits beside Thinking's in the same Ollama, why, and the sizes compared.</summary>
public sealed record SideBySideFit(bool Fits, string Why, long? NeedBytes = null, long? RoomBytes = null);

/// <summary>Two models side by side in one Ollama (Deep thinking's beside Thinking's on this PC). Ollama runs each loaded model in
/// a process of its own, so two different models answer at the same time, but only while both fit on the graphics card: when
/// a model it is asked for doesn't fit beside the loaded ones, Ollama unloads another (Thinking's, when it is idle) or makes the
/// request wait until one finishes. Either would hold up a reply, so a think there starts only when both fit
/// (<see cref="Decide"/>), and stops if loading its model pushed Thinking's off the card (<see cref="PushedOut"/>). Loopback
/// only; nothing said in the conversation is sent.</summary>
public static class OllamaSideBySide
{
    /// <summary>Kept free on the graphics card besides the two models: the desktop and Ollama's own buffers.</summary>
    public const long ReserveBytes = 768L * 1024 * 1024;
    private const double LoadFactor = 1.2;
    private const long LoadExtraBytes = 512L * 1024 * 1024;

    /// <summary>About what a model that isn't loaded yet takes once loaded: its download, plus its context (the KV cache) and
    /// compute buffers. Ollama's own figure (<c>/api/ps</c>) replaces it once the model has loaded.</summary>
    public static long Estimate(long downloadBytes) => (long)(downloadBytes * LoadFactor) + LoadExtraBytes;

    /// <summary>Whether <paramref name="deep"/> fits beside <paramref name="thinking"/> on the graphics card: what both take
    /// (Ollama's figure when loaded, else what was seen before in <paramref name="learned"/>, else <see cref="Estimate"/>) against
    /// the card's memory less what everything else uses (other programs when the driver says, else Ollama's other models) and
    /// <see cref="ReserveBytes"/>.</summary>
    public static SideBySideFit Decide(string thinking, string deep, IReadOnlyList<OllamaLoadedModel> loaded,
        IReadOnlyDictionary<string, long> downloads, GraphicsMemory? memory, IReadOnlyDictionary<string, long>? learned = null)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(downloads);
        if (Same(thinking, deep))
            return new(false, $"{deep} is Thinking's own model, which can't think something over while it answers you. Choose another model.");
        var deepNow = Find(loaded, deep);
        var thinkingNow = Find(loaded, thinking);
        if (deepNow is { OnGraphicsCard: true } && thinkingNow is { OnGraphicsCard: true })
            return new(true, $"{deep} and {thinking} are both loaded on the graphics card.", deepNow.Size + thinkingNow.Size);
        // Ollama never moves a loaded model back onto the card, so one already partly on the processor stays there.
        if (thinkingNow is { OnGraphicsCard: false })
            return new(false, $"Part of {thinking} runs on the processor now, not the graphics card, so there's no room for {deep} beside " +
                "it. Close programs using the graphics card, then try again.");
        if (Size(deep, deepNow) is not { } deepSize) return new(false, $"{deep} isn't downloaded in Ollama on this PC. Download it first.");
        if (Size(thinking, thinkingNow) is not { } thinkingSize) return new(false, $"{thinking} isn't downloaded in Ollama on this PC.");
        if (memory is not { TotalBytes: > 0 })
            return new(false, $"Martlet couldn't read how much graphics memory this PC has, so it can't tell whether {deep} fits beside {thinking}.");
        var ours = (deepNow?.SizeVram ?? 0) + (thinkingNow?.SizeVram ?? 0);
        var others = memory.UsedBytes is { } used ? Math.Max(0, used - ours)
            : loaded.Where(m => !ReferenceEquals(m, deepNow) && !ReferenceEquals(m, thinkingNow)).Sum(m => m.SizeVram);
        var room = memory.TotalBytes - others - ReserveBytes;
        var need = deepSize + thinkingSize;
        if (need <= room)
            return new(true, $"{deep} (about {Gb(deepSize)} GB) fits beside {thinking} (about {Gb(thinkingSize)} GB): the graphics card " +
                $"has about {Gb(room)} GB for them.", need, room);
        var busy = others >= 1024L * 1024 * 1024 ? $", or close programs using the graphics card (about {Gb(others)} GB now)" : "";
        return new(false, $"{deep} (about {Gb(deepSize)} GB) doesn't fit beside {thinking} (about {Gb(thinkingSize)} GB): the graphics " +
            $"card has only about {Gb(Math.Max(0, room))} GB for them. Choose a smaller model{busy}.", need, room);

        long? Size(string model, OllamaLoadedModel? now) =>
            now?.Size ?? Lookup(learned, model) ?? (Lookup(downloads, model) is { } bytes ? Estimate(bytes) : null);
    }

    /// <summary>Why the think must stop, or null: once <paramref name="deep"/> has loaded, <paramref name="thinking"/> (loaded
    /// on the card before the think) must still be loaded and all on the card.</summary>
    public static string? PushedOut(string thinking, string deep, IReadOnlyList<OllamaLoadedModel> now)
    {
        ArgumentNullException.ThrowIfNull(now);
        if (Find(now, deep) is null) return null;
        return Find(now, thinking) switch
        {
            null => $"Ollama unloaded {thinking} to make room for {deep}, so Martlet stopped thinking it over. Choose a smaller model for Deep thinking.",
            { OnGraphicsCard: false } => $"Loading {deep} pushed part of {thinking} off the graphics card, so Martlet stopped thinking it over. " +
                "Choose a smaller model for Deep thinking.",
            _ => null
        };
    }

    /// <summary>Whether Ollama's name for a model is <paramref name="model"/>; a name without a tag means its ":latest".</summary>
    public static bool Same(string? name, string? model) => name is not null && model is not null &&
        string.Equals(Tagged(name), Tagged(model), StringComparison.OrdinalIgnoreCase);

    public static OllamaLoadedModel? Find(IEnumerable<OllamaLoadedModel> loaded, string model) => loaded.FirstOrDefault(m => Same(m.Name, model));

    /// <summary>What Ollama at <paramref name="origin"/> has loaded (<c>/api/ps</c>), or null when it doesn't answer.</summary>
    public static async Task<IReadOnlyList<OllamaLoadedModel>?> LoadedAsync(HttpClient client, Uri origin, CancellationToken token)
    {
        using var document = await GetAsync(client, new Uri(origin, "api/ps"), token).ConfigureAwait(false);
        if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return null;
        return [.. models.EnumerateArray().Select(entry => (Name: Text(entry, "name") ?? Text(entry, "model"), Size: Long(entry, "size"),
                Vram: Long(entry, "size_vram") ?? 0))
            .Where(m => m.Name is { Length: > 0 and <= 256 } && m.Size is > 0).Take(64)
            .Select(m => new OllamaLoadedModel(m.Name!, m.Size!.Value, m.Vram))];
    }

    /// <summary>The download size of each model Ollama at <paramref name="origin"/> has (<c>/api/tags</c>), or null when it
    /// doesn't answer.</summary>
    public static async Task<IReadOnlyDictionary<string, long>?> DownloadsAsync(HttpClient client, Uri origin, CancellationToken token)
    {
        using var document = await GetAsync(client, new Uri(origin, "api/tags"), token).ConfigureAwait(false);
        if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return null;
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in models.EnumerateArray().Take(500))
            if (Text(entry, "name") is { Length: > 0 and <= 256 } name && Long(entry, "size") is { } size && size > 0)
                sizes[Tagged(name)] = size;
        return sizes;
    }

    /// <summary>Has Ollama load <paramref name="model"/> (an empty <c>/api/generate</c>, as the talk window's warm-up does; nothing is
    /// generated) or, with <paramref name="unload"/>, unload it at once. False when Ollama refused or didn't answer.</summary>
    public static async Task<bool> LoadAsync(HttpClient client, Uri origin, string model, bool unload, TimeSpan within, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(within);
        var body = unload ? JsonSerializer.Serialize(new { model, keep_alive = 0 }) : JsonSerializer.Serialize(new { model });
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(new Uri(origin, "api/generate"), content, limit.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (HttpRequestException) { return false; }
    }

    private static string Tagged(string name) => name.Contains(':', StringComparison.Ordinal) ? name : name + ":latest";

    private static long? Lookup(IReadOnlyDictionary<string, long>? sizes, string model) =>
        sizes is null ? null : sizes.TryGetValue(Tagged(model), out var size) || sizes.TryGetValue(model, out size) ? size : null;

    private static string Gb(long bytes) => (bytes / 1073741824d).ToString("0.#", CultureInfo.InvariantCulture);

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Long(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static async Task<JsonDocument?> GetAsync(HttpClient client, Uri uri, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await client.GetAsync(uri, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(limit.Token).ConfigureAwait(false);
            return bytes.Length > 4 * 1024 * 1024 ? null : JsonDocument.Parse(bytes);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is HttpRequestException or JsonException) { return null; }
    }
}
