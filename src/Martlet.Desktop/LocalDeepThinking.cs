using System.Collections.Concurrent;
using System.Net.Http;
using Martlet.Logging;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

/// <summary>Deep thinking on a second model in Ollama on this PC, beside Thinking's (<see cref="OllamaSideBySide"/>): whether both
/// fit on the graphics card before a think, whether loading it pushed Thinking's off the card after all, and putting Thinking's
/// back when it did. What Ollama reports a model takes once loaded is remembered for the rest of the session, so later checks
/// use Ollama's own figure instead of an estimate. Loopback only; nothing said in the conversation is sent.</summary>
internal static class LocalDeepThinking
{
    private const double Gib = 1024d * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, long> Learned = new(StringComparer.OrdinalIgnoreCase);
    internal static TimeSpan Poll => TimeSpan.FromMilliseconds(500);

    private static HttpClient Client() => new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    /// <summary>Whether <paramref name="deep"/> fits beside <paramref name="thinking"/> in Ollama on this PC. With
    /// <paramref name="loadThinking"/>, Thinking's model is loaded first when it isn't (as the talk window's warm-up does), so the
    /// check counts what it really takes and a reply never has to load it beside a running think.</summary>
    internal static async Task<SideBySideFit> CheckAsync(string thinking, string deep, bool loadThinking, CancellationToken token)
    {
        using var client = Client();
        var origin = LocalOllama.OriginUri;
        var loaded = await OllamaSideBySide.LoadedAsync(client, origin, token).ConfigureAwait(false);
        if (loaded is null) return new(false, "Ollama on this PC isn't answering. Start it, then try again.");
        if (loadThinking && !OllamaSideBySide.Same(thinking, deep) && OllamaSideBySide.Find(loaded, thinking) is null &&
            await OllamaSideBySide.LoadAsync(client, origin, thinking, unload: false, LocalOllamaWarmup.LoadLimit, token).ConfigureAwait(false))
            loaded = await OllamaSideBySide.LoadedAsync(client, origin, token).ConfigureAwait(false) ?? loaded;
        Learn(loaded);
        var downloads = await OllamaSideBySide.DownloadsAsync(client, origin, token).ConfigureAwait(false) ?? new Dictionary<string, long>();
        var memory = await GraphicsMemoryAsync(token).ConfigureAwait(false);
        return OllamaSideBySide.Decide(thinking, deep, loaded, downloads, memory, Learned);
    }

    /// <summary>This PC's graphics card memory: the total and what is in use now from the NVIDIA driver, else the total Windows
    /// reports for the card; null when neither is known.</summary>
    internal static async Task<GraphicsMemory?> GraphicsMemoryAsync(CancellationToken token)
    {
        if (await ListeningAdvisor.ReadGpuAsync(token).ConfigureAwait(false) is { } now)
            return new((long)(now.TotalGb * Gib), (long)(now.UsedGb * Gib));
        return MachineInfo.Read().BestGpu?.MemoryGb is { } total && total > 0 ? new((long)(total * Gib), null) : null;
    }

    /// <summary>Watches Ollama while a think's model loads: once it is loaded, calls <paramref name="pushed"/> with why when
    /// Thinking's model is no longer loaded all on the graphics card, then stops watching.</summary>
    internal static async Task WatchAsync(string thinking, string deep, Action<string> pushed, CancellationToken token)
    {
        using var client = Client();
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(Poll, token).ConfigureAwait(false);
                if (await OllamaSideBySide.LoadedAsync(client, LocalOllama.OriginUri, token).ConfigureAwait(false) is not { } now ||
                    OllamaSideBySide.Find(now, deep) is null) continue;
                Learn(now);
                if (OllamaSideBySide.PushedOut(thinking, deep, now) is { } why) pushed(why);
                return;
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>After a think's model pushed Thinking's off the card: unloads the think's and loads Thinking's again, so the next
    /// reply doesn't wait for it.</summary>
    internal static async Task RecoverAsync(string thinking, string deep)
    {
        using var client = Client();
        await OllamaSideBySide.LoadAsync(client, LocalOllama.OriginUri, deep, unload: true, TimeSpan.FromSeconds(30), CancellationToken.None)
            .ConfigureAwait(false);
        if (await OllamaSideBySide.LoadAsync(client, LocalOllama.OriginUri, thinking, unload: false, LocalOllamaWarmup.LoadLimit,
                CancellationToken.None).ConfigureAwait(false))
            ErrorLog.Info($"Ollama on this PC unloaded {deep} and loaded {thinking} again for the conversation.");
    }

    private static void Learn(IEnumerable<OllamaLoadedModel> loaded)
    {
        foreach (var model in loaded) Learned[model.Name] = model.Size;
    }
}
