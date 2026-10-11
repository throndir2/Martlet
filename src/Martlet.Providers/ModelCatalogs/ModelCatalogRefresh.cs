using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using Martlet.Core.Planning;
using Martlet.Providers.LocalModels;

namespace Martlet.Providers.ModelCatalogs;

/// <summary>Where the daily refresh reads each source (docs/MODEL_CATALOG.md, Sources). None needs a key. ollama.com is not
/// read: its terms forbid automated access.</summary>
public sealed record ModelCatalogEndpoints(Uri OpenRouter, Uri ModelsDevModels, Uri ModelsDevApi, Uri NvidiaModels, Uri Vllm, Uri LmArenaRows)
{
    public static ModelCatalogEndpoints Public { get; } = new(
        new("https://openrouter.ai/api/v1/models"),
        new("https://models.dev/models.json"),
        new("https://models.dev/api.json"),
        new("https://build.nvidia.com/models.md"),
        new("https://raw.githubusercontent.com/vllm-project/vllm/main/docs/models/supported_models.md"),
        new("https://datasets-server.huggingface.co/rows?dataset=lmarena-ai/leaderboard-dataset"));
}

/// <summary>What one refresh did: the data now in use, the status saved, and whether a new daily copy was saved.</summary>
public sealed record ModelCatalogRefreshResult(ModelCatalogData Data, ModelCatalogStatus Status, bool Saved, int Models, int Routes,
    string? SaveProblem);

/// <summary>The model catalog's background refresh: reads each source (no key, a time limit and a size limit on every request),
/// works out the catalog and saves the daily copy. A source that fails keeps its last good part. It never runs while a reply
/// is being made: <c>busy</c> is asked before and during each request, and a request a reply interrupts is asked again after
/// it. Nothing here is on the reply path.</summary>
public sealed class ModelCatalogRefresh
{
    public const int NvidiaPageLimit = 200;
    private const int NvidiaConcurrency = 4;
    private const int LmArenaPages = 15;
    private static readonly TimeSpan BusyPoll = TimeSpan.FromMilliseconds(250);

    private readonly HttpClient client;
    private readonly ModelCatalogEndpoints endpoints;
    private readonly Func<bool> busy;
    private readonly TimeProvider clock;
    private readonly LocalModelFactsReader? local;

    /// <param name="local">Reads local model facts from Hugging Face and the Ollama registry; null uses
    /// <see cref="LocalModelFactsReader.Shared"/>.</param>
    public ModelCatalogRefresh(HttpClient client, ModelCatalogEndpoints? endpoints = null, Func<bool>? busy = null, TimeProvider? clock = null,
        LocalModelFactsReader? local = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.endpoints = endpoints ?? ModelCatalogEndpoints.Public;
        this.busy = busy ?? (() => false);
        this.clock = clock ?? TimeProvider.System;
        this.local = local;
    }

    /// <summary>How many open-weight models one refresh looks up on Hugging Face (Martlet's own first, then those never looked
    /// up, then the oldest): at most five requests each, within the reader's budget. The rest wait for the next day.</summary>
    public int LocalLookups { get; init; } = 16;
    /// <summary>Models larger than this (billions of parameters) aren't looked up for running locally.</summary>
    public double LocalLargest { get; init; } = 130;

    /// <summary>The time limit for one request (a NVIDIA model page has <see cref="PageTimeout"/>).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan PageTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>How long a reply may hold the refresh before it gives up for today.</summary>
    public TimeSpan LongestWait { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>A client for the public sources: no cookies or keys, a few redirects, compressed answers.</summary>
    public static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true, MaxAutomaticRedirections = 3, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.All
        }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        var version = typeof(ModelCatalogRefresh).Assembly.GetName().Version?.ToString(3) ?? "0";
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Martlet/{version} (+https://github.com/throndir2/Martlet)");
        return client;
    }

    /// <summary>Refreshes <paramref name="store"/> when a refresh is due (or <paramref name="force"/>); null when it isn't due.</summary>
    public async Task<ModelCatalogRefreshResult?> RunIfDueAsync(ModelCatalogStore store, bool force, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!force && !store.Due(clock.GetUtcNow())) return null;
        return await RunAsync(store, token).ConfigureAwait(false);
    }

    /// <summary>Reads every source and saves a new daily copy in <paramref name="store"/>. With <paramref name="save"/> false
    /// it only reads and works it out (the release snapshot).</summary>
    public async Task<ModelCatalogRefreshResult> RunAsync(ModelCatalogStore store, CancellationToken token, bool save = true)
    {
        ArgumentNullException.ThrowIfNull(store);
        var started = clock.GetUtcNow();
        var status = store.Status() with { LastAttempt = started };
        // Claim today's refresh first, so another Martlet sharing this folder doesn't start one too.
        if (save) store.SaveStatus(status);
        var data = store.Current().Data;
        var sources = new Dictionary<string, CatalogSourceStatus>(status.Sources, StringComparer.Ordinal);
        var read = 0;
        foreach (var source in CatalogSources.Fetched)
        {
            var timer = Stopwatch.StartNew();
            var previous = sources.GetValueOrDefault(source) ?? new();
            try
            {
                var (items, bytes, url) = await ReadAsync(source, data, token).ConfigureAwait(false);
                if (items.Count == 0) throw new InvalidDataException("it listed no models");
                data = data.With(source, new() { Read = clock.GetUtcNow(), Url = url, Items = items });
                sources[source] = new()
                {
                    Tried = clock.GetUtcNow(), Read = clock.GetUtcNow(), Items = items.Count, Bytes = bytes, Seconds = Math.Round(timer.Elapsed.TotalSeconds, 1)
                };
                read++;
            }
            catch (Exception error) when (!token.IsCancellationRequested && error is HttpRequestException or TimeoutException or IOException or
                InvalidDataException or FormatException or System.Text.Json.JsonException or OperationCanceledException or BusyException)
            {
                sources[source] = previous with
                {
                    Tried = clock.GetUtcNow(), Seconds = Math.Round(timer.Elapsed.TotalSeconds, 1),
                    Problem = Describe(error) + (data.Block(source) is { } kept ? $"; kept what it said on {kept.Read:yyyy-MM-dd}" : "")
                };
                // A reply held the refresh too long: the rest waits for tomorrow's refresh.
                if (error is BusyException) break;
            }
        }
        var finished = clock.GetUtcNow();
        var failed = CatalogSources.Fetched.Where(s => sources.GetValueOrDefault(s)?.Tried >= started && sources[s].Problem is not null).ToList();
        var catalog = ModelCatalog.Build(data with { Built = finished });
        string? saveProblem = null;
        var saved = false;
        if (read > 0 && catalog.Models.Count > 0)
        {
            data = data with { Built = finished };
            if (save)
            {
                saveProblem = store.Save(data);
                saved = saveProblem is null;
            }
        }
        status = status with
        {
            LastFinished = finished, LastSuccess = failed.Count == 0 && read > 0 ? finished : status.LastSuccess,
            LastSaved = saved ? finished : status.LastSaved, Sources = sources,
            Problem = saveProblem ?? (failed.Count == 0 ? null : $"Couldn't read {string.Join(", ", failed.Select(CatalogSources.Describe))}.")
        };
        if (save) store.SaveStatus(status);
        return new(data, status, saved, catalog.Models.Count, catalog.Routes.Count, saveProblem);
    }

    private async Task<(IReadOnlyList<CatalogObservation> Items, long Bytes, string Url)> ReadAsync(string source, ModelCatalogData data,
        CancellationToken token)
    {
        var previous = data.Block(source);
        switch (source)
        {
            case CatalogSources.OpenRouter:
            {
                var text = await GetAsync(endpoints.OpenRouter, 16, Timeout, token).ConfigureAwait(false);
                return (CatalogReaders.OpenRouter(text), Encoding.UTF8.GetByteCount(text), endpoints.OpenRouter.ToString());
            }
            case CatalogSources.ModelsDev:
            {
                var text = await GetAsync(endpoints.ModelsDevModels, 16, Timeout, token).ConfigureAwait(false);
                return (CatalogReaders.ModelsDevModels(text), Encoding.UTF8.GetByteCount(text), endpoints.ModelsDevModels.ToString());
            }
            case CatalogSources.ModelsDevRows:
            {
                var text = await GetAsync(endpoints.ModelsDevApi, 48, Timeout, token).ConfigureAwait(false);
                return (CatalogReaders.ModelsDevRows(text), Encoding.UTF8.GetByteCount(text), endpoints.ModelsDevApi.ToString());
            }
            case CatalogSources.NvidiaBuild:
                return await NvidiaAsync(previous, token).ConfigureAwait(false);
            case CatalogSources.Vllm:
            {
                var text = await GetAsync(endpoints.Vllm, 4, Timeout, token).ConfigureAwait(false);
                return (CatalogReaders.Vllm(text), Encoding.UTF8.GetByteCount(text), endpoints.Vllm.ToString());
            }
            case CatalogSources.LmArenaText or CatalogSources.LmArenaVision:
                return await LmArenaAsync(source == CatalogSources.LmArenaText ? "text" : "vision", token).ConfigureAwait(false);
            case CatalogSources.HuggingFace:
                return await LocalAsync(data, previous, token).ConfigureAwait(false);
            default:
                throw new InvalidDataException("unknown source");
        }
    }

    /// <summary>Local model facts for open-weight models the other sources list: <see cref="LocalLookups"/> a day, Martlet's own
    /// local models first, then those never looked up, then the oldest. The others keep what was read before.</summary>
    private async Task<(IReadOnlyList<CatalogObservation>, long, string)> LocalAsync(ModelCatalogData data, CatalogSourceBlock? previous,
        CancellationToken token)
    {
        var reader = local ?? LocalModelFactsReader.Shared;
        var catalog = ModelCatalog.Build(data);
        var kept = (previous?.Items ?? []).Where(i => i.HuggingFace is not null).GroupBy(i => i.HuggingFace!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var candidates = catalog.Models
            .Where(m => m.HuggingFaceRepo is not null && m.OpenWeights != false &&
                (m.Fact(CatalogFacts.ParametersTotal).Number ?? m.Fact(CatalogFacts.ParametersActive).Number ?? 0) <= LocalLargest)
            .OrderByDescending(m => m.Names.Ollama.Count > 0)
            .ThenBy(m => kept.TryGetValue(m.HuggingFaceRepo!, out var old) ? old.Local?.CheckedAt ?? DateTimeOffset.MinValue : DateTimeOffset.MinValue)
            .ThenByDescending(m => m.Rank ?? -1)
            .Take(LocalLookups).ToList();
        var looked = 0;
        foreach (var model in candidates)
        {
            await WaitWhileBusyAsync(token).ConfigureAwait(false);
            var facts = await reader.LookupAsync(model.HuggingFaceRepo, model.Names.Ollama.FirstOrDefault(), token).ConfigureAwait(false);
            // Out of the reader's request budget: the rest wait for the next refresh, and this half answer isn't kept.
            if (facts.Problems.Any(p => p.Contains("five minutes", StringComparison.Ordinal) || p.Contains("slow down", StringComparison.Ordinal))) break;
            if (CatalogReaders.Local(facts) is not { } item) continue;
            kept[model.HuggingFaceRepo!] = item with { Id = model.HuggingFaceRepo!, HuggingFace = model.HuggingFaceRepo };
            looked++;
        }
        if (looked == 0 && candidates.Count > 0) throw new InvalidDataException("Hugging Face answered none of the lookups");
        return (kept.Values.OrderBy(i => i.Id, StringComparer.OrdinalIgnoreCase).ToList(), 0, "https://huggingface.co");
    }

    private async Task<(IReadOnlyList<CatalogObservation>, long, string)> NvidiaAsync(CatalogSourceBlock? previous, CancellationToken token)
    {
        var index = await GetAsync(endpoints.NvidiaModels, 2, Timeout, token).ConfigureAwait(false);
        var links = CatalogReaders.NvidiaLinks(index).Take(NvidiaPageLimit).ToList();
        if (links.Count == 0) throw new InvalidDataException("models.md links no model pages");
        var kept = (previous?.Items ?? []).Where(i => i.Page is not null).GroupBy(i => i.Page!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var found = new CatalogObservation?[links.Count];
        long bytes = Encoding.UTF8.GetByteCount(index);
        var failures = 0;
        using var gate = new SemaphoreSlim(NvidiaConcurrency);
        await Task.WhenAll(links.Select(async (link, position) =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var page = await GetAsync(new Uri(endpoints.NvidiaModels, link), 2, PageTimeout, token).ConfigureAwait(false);
                Interlocked.Add(ref bytes, Encoding.UTF8.GetByteCount(page));
                found[position] = CatalogReaders.NvidiaPage(page, link);
            }
            catch (Exception error) when (!token.IsCancellationRequested && error is HttpRequestException or TimeoutException or IOException or
                InvalidDataException or OperationCanceledException or BusyException)
            {
                Interlocked.Increment(ref failures);
                found[position] = kept.GetValueOrDefault(link);
            }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        if (failures * 2 > links.Count) throw new InvalidDataException($"{failures} of {links.Count} model pages failed");
        return (found.Where(f => f is not null).Select(f => f!).ToList(), bytes, endpoints.NvidiaModels.ToString());
    }

    private async Task<(IReadOnlyList<CatalogObservation>, long, string)> LmArenaAsync(string config, CancellationToken token)
    {
        var items = new List<CatalogObservation>();
        long bytes = 0;
        var url = endpoints.LmArenaRows + $"&config={config}&split=latest";
        for (var page = 0; page < LmArenaPages; page++)
        {
            var text = await GetAsync(new Uri($"{url}&offset={page * 100}&length=100"), 4, Timeout, token).ConfigureAwait(false);
            bytes += Encoding.UTF8.GetByteCount(text);
            items.AddRange(CatalogReaders.LmArena(text, out var more));
            if (!more) break;
        }
        return (items, bytes, url);
    }

    /// <summary>One GET of at most <paramref name="megabytes"/> MB within <paramref name="timeout"/>. It waits while Martlet
    /// is busy, and a request a reply interrupts is asked again once the reply is done.</summary>
    private async Task<string> GetAsync(Uri uri, int megabytes, TimeSpan timeout, CancellationToken token)
    {
        var limit = megabytes * 1024L * 1024;
        for (var attempt = 0; ; attempt++)
        {
            await WaitWhileBusyAsync(token).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            using var interrupted = new CancellationTokenSource();
            using var watch = clock.CreateTimer(_ =>
            {
                try { if (busy()) interrupted.Cancel(); }
                catch (ObjectDisposedException) { }
            }, null, BusyPoll, BusyPoll);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, interrupted.Token);
            try
            {
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"{uri.Host} answered HTTP {(int)response.StatusCode}", null, response.StatusCode);
                if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException($"its answer is larger than {megabytes} MB");
                await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int count;
                while ((count = await stream.ReadAsync(chunk, linked.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + count > limit) throw new InvalidDataException($"its answer is larger than {megabytes} MB");
                    buffer.Write(chunk, 0, count);
                }
                return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            }
            catch (OperationCanceledException) when (interrupted.IsCancellationRequested && !token.IsCancellationRequested && !deadline.IsCancellationRequested)
            {
                if (attempt >= 5) throw new BusyException();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException($"{uri.Host} gave no full answer within {timeout.TotalSeconds:0} seconds");
            }
        }
    }

    private async Task WaitWhileBusyAsync(CancellationToken token)
    {
        var waited = TimeSpan.Zero;
        while (busy())
        {
            if (waited >= LongestWait) throw new BusyException();
            await Task.Delay(TimeSpan.FromSeconds(1), clock, token).ConfigureAwait(false);
            waited += TimeSpan.FromSeconds(1);
        }
    }

    private static string Describe(Exception error) => error switch
    {
        BusyException => "Martlet was busy replying",
        HttpRequestException { StatusCode: { } code } http => http.Message.Length > 0 ? http.Message : $"HTTP {(int)code}",
        HttpRequestException => "couldn't connect",
        TimeoutException or InvalidDataException => error.Message,
        FormatException or System.Text.Json.JsonException => "its answer wasn't in the expected form",
        _ => "it failed"
    };

    private sealed class BusyException : Exception
    {
        public BusyException() : base("Martlet was busy replying") { }
    }
}
