using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Martlet.Core.Planning;
using Martlet.Providers.LocalModels;
using Martlet.Providers.ModelCatalogs;

namespace Martlet.Mcp;

/// <summary>model_catalog_status, model_catalog_lookup and model_catalog_refresh: Martlet's internal model catalog
/// (docs/MODEL_CATALOG.md). Status says which copy is in use (the snapshot shipped with Martlet or the daily copy in the PC
/// folder) and what the last daily refresh did. Lookup finds a model by any name and shows every fact with each source's answer
/// and the answer worked out from them, its routes and how smart it is (in words; LMArena's rating with its credit; never the
/// Artificial Analysis index). Refresh rehearses the production refresh against fixture sources on 127.0.0.1 (NOT the real
/// sources), or with live=true reads the real public sources (no key) and saves the daily copy, or writes the release snapshot.</summary>
internal static class ModelCatalogCheck
{
    internal static object Status(string dataDirectory)
    {
        var store = ModelCatalogStore.For(dataDirectory);
        var (data, from) = store.Current();
        var snapshot = ModelCatalogStore.Snapshot();
        var cached = store.Cached();
        var status = store.Status();
        var now = DateTimeOffset.UtcNow;
        var catalog = store.Load();
        return new
        {
            folder = store.Folder?.Where, sharedByEveryWindowsUser = store.Folder?.Shared, @using = from, built = data.Built,
            snapshot = new { built = snapshot.Built, sources = Blocks(snapshot) },
            dailyCopy = new
            {
                file = File.Exists(store.FilePath) ? cached is null ? "unreadable" : "loaded" : "none", built = cached?.Built,
                sources = cached is null ? null : Blocks(cached)
            },
            refresh = new
            {
                status.LastAttempt, status.LastFinished, status.LastSuccess, status.LastSaved, status.Problem,
                due = ModelCatalogStore.Due(status, now), nextDue = ModelCatalogStore.NextDue(status), every = "once a day",
                sources = CatalogSources.Fetched.Select(source => status.Sources.GetValueOrDefault(source) is { } s ? new
                {
                    source, name = CatalogSources.Describe(source), s.Tried, s.Read, s.Items, s.Bytes, s.Seconds, s.Problem
                } : (object)new { source, name = CatalogSources.Describe(source), tried = (DateTimeOffset?)null })
            },
            catalog = Counts(catalog)
        };
    }

    internal static object Lookup(string dataDirectory, string name)
    {
        var store = ModelCatalogStore.For(dataDirectory);
        var catalog = store.Load();
        var match = catalog.Find(name);
        if (match is null)
        {
            var guess = catalog.Smartness(name);
            return new
            {
                name, found = false, @using = store.Current().From,
                smartness = new { guess.Words, guess.Tier, guess.From }
            };
        }
        return new
        {
            name, found = true, how = match.How, @using = store.Current().From,
            model = Model(catalog, match.Model),
            routes = catalog.RoutesOf(match.Model).Select(route => Route(catalog, route))
        };
    }

    internal static object Model(ModelCatalog catalog, CatalogModel model)
    {
        var smart = catalog.Smartness(model);
        return new
        {
            model.Key, model.Name, huggingFace = model.HuggingFaceRepo, names = model.Names, model.Family, model.Size,
            inputs = new
            {
                text = Word(model.Fact(CatalogFacts.InputText)), image = Word(model.Fact(CatalogFacts.InputImage)),
                audio = Word(model.Fact(CatalogFacts.InputAudio)), video = Word(model.Fact(CatalogFacts.InputVideo)),
                videoAsFrames = Word(model.Fact(CatalogFacts.InputVideoFrames))
            },
            openWeights = Word(model.Fact(CatalogFacts.OpenWeights)), locallyHostable = model.LocallyHostable,
            local = Local(model),
            facts = CatalogFacts.ModelFacts.Where(model.Facts.ContainsKey).ToDictionary(k => k, k => Fact(catalog, model.Fact(k))),
            // The rank stays inside Martlet (it may come from Artificial Analysis's index): words, the tier and LMArena's rating only.
            smartness = new
            {
                smart.Words, smart.Tier, smart.From, lmArenaText = smart.Rating, lmArenaVision = smart.VisionRating, smart.Credit,
                lmArenaName = model.Names.LmArena
            }
        };
    }

    /// <summary>What running the model locally takes, when the catalog looked it up: the default quantization's install name,
    /// download and memory at Martlet's 8,192-token context, and the other quantizations.</summary>
    private static object? Local(CatalogModel model)
    {
        if (model.Local is not { } local) return null;
        var chosen = local.Quantization();
        var memory = model.Memory();
        return new
        {
            local.OllamaTag, local.GgufRepo, local.CheckedAt, local.Gated,
            installName = chosen?.InstallName, quantization = chosen?.Name,
            downloadGb = chosen is null ? (double?)null : Math.Round(chosen.DownloadBytes / 1e9, 2),
            memoryAt8192 = memory?.Describe(),
            graphicsGb = memory is null ? (double?)null : Math.Round(memory.GraphicsBytes / 1e9, 2),
            systemMemoryGb = memory is null ? (double?)null : Math.Round(memory.SystemMemoryBytes / 1e9, 2),
            quantizations = local.Quantizations.Take(12).Select(q => new { q.Name, q.InstallName, gb = Math.Round(q.DownloadBytes / 1e9, 2) }),
            local.Problems
        };
    }

    private static object Route(ModelCatalog catalog, CatalogRoute route) => new
    {
        route.Provider, route.ModelId, route.BaseUrl, route.Free, route.FreeNote, route.Context, route.Expires, route.Retired, route.Deprecated,
        server = route.Server is { } server ? CatalogSources.Describe(server) : null,
        inputs = new
        {
            text = Word(route.Fact(CatalogFacts.InputText)), image = Word(route.Fact(CatalogFacts.InputImage)),
            audio = Word(route.Fact(CatalogFacts.InputAudio)), video = Word(route.Fact(CatalogFacts.InputVideo)),
            videoAsFrames = Word(route.Fact(CatalogFacts.InputVideoFrames))
        },
        tools = Word(route.Fact(CatalogFacts.Tools)), reasoning = Word(route.Fact(CatalogFacts.Reasoning)),
        facts = CatalogFacts.RouteFacts.Where(route.Facts.ContainsKey).ToDictionary(k => k, k => Fact(catalog, route.Fact(k)))
    };

    private static string Word(CatalogFact fact) => fact.Value ?? (fact.Disagree ? "unknown (sources disagree)" : "unknown");

    private static object Fact(ModelCatalog catalog, CatalogFact fact) => new
    {
        value = fact.Value, from = fact.From is { } from ? CatalogSources.Describe(from) : null, disagree = fact.Disagree,
        answers = fact.Answers.Select(a => new
        {
            source = CatalogSources.Describe(a.Source), a.Value, a.Note,
            @checked = a.Checked ?? (catalog.SourceDates.TryGetValue(a.Source, out var read) ? read : null)
        })
    };

    private static object Blocks(ModelCatalogData data) =>
        data.Sources.OrderBy(s => s.Key, StringComparer.Ordinal).ToDictionary(s => s.Key, s => new { s.Value.Read, items = s.Value.Items.Count });

    private static object Counts(ModelCatalog catalog) => new
    {
        models = catalog.Models.Count, routes = catalog.Routes.Count,
        openWeights = catalog.Models.Count(m => m.OpenWeights == true),
        sees = catalog.Models.Count(m => m.Sees == true), hears = catalog.Models.Count(m => m.Hears == true),
        takesVideo = catalog.Models.Count(m => m.TakesVideo == true), callsTools = catalog.Models.Count(m => m.CallsTools == true),
        disagreeOnAnInput = catalog.Models.Count(m => new[] { CatalogFacts.InputImage, CatalogFacts.InputAudio, CatalogFacts.InputVideo }
            .Any(k => m.Fact(k).Disagree)),
        ranked = catalog.Models.Count(m => m.Rank is not null), lmArenaRated = catalog.Models.Count(m => m.Rating is not null),
        freeRoutes = catalog.Routes.Count(r => r.Free == true), retiredRoutes = catalog.Routes.Count(r => r.Retired),
        routesByProvider = catalog.Routes.GroupBy(r => r.Provider).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count())
    };

    /// <summary>live: the real sources into the data folder's daily copy (or, with snapshotPath, the release snapshot: written
    /// only when every source was read). Otherwise the fixture rehearsal.</summary>
    internal static async Task<object> RefreshAsync(string dataDirectory, bool live, string? snapshotPath, bool force, CancellationToken cancellation)
    {
        if (!live)
        {
            if (snapshotPath is not null) throw new ArgumentException("snapshotPath needs live=true.");
            return await RehearseAsync(cancellation);
        }
        if (snapshotPath is not null && !Path.IsPathFullyQualified(snapshotPath)) throw new ArgumentException("snapshotPath must be an absolute path.");
        var store = ModelCatalogStore.For(dataDirectory);
        using var client = ModelCatalogRefresh.CreateClient();
        var refresh = new ModelCatalogRefresh(client);
        var timer = Stopwatch.StartNew();
        if (snapshotPath is null)
        {
            var result = await refresh.RunIfDueAsync(store, force, cancellation);
            if (result is null) return new { ran = false, why = "Not due: the last refresh started less than a day ago (use force=true).", status = Status(dataDirectory) };
            return new { ran = true, seconds = Math.Round(timer.Elapsed.TotalSeconds), result.Saved, result.SaveProblem, result.Models, result.Routes, status = Status(dataDirectory) };
        }
        var read = await refresh.RunAsync(store, cancellation, save: false);
        var failed = read.Status.Sources.Where(s => s.Value.Problem is not null).Select(s => new { source = s.Key, s.Value.Problem }).ToList();
        if (failed.Count > 0)
            return new { ran = true, snapshotWritten = false, why = "A source failed; the snapshot is written only when every source was read.", failed };
        await File.WriteAllTextAsync(snapshotPath, read.Data.Write(), cancellation);
        return new
        {
            ran = true, snapshotWritten = true, bytes = new FileInfo(snapshotPath).Length, seconds = Math.Round(timer.Elapsed.TotalSeconds),
            sources = read.Status.Sources.ToDictionary(s => s.Key, s => new { s.Value.Items, s.Value.Seconds }),
            catalog = Counts(ModelCatalog.Build(read.Data))
        };
    }

    // ---- Fixture rehearsal ----------------------------------------------------------------------------------------------

    private static async Task<object> RehearseAsync(CancellationToken cancellation)
    {
        var steps = new List<object>();
        var passed = true;
        void Step(string name, bool ok, string detail)
        {
            steps.Add(new { name, ok, detail });
            passed &= ok;
        }
        var folder = Path.Combine(Path.GetTempPath(), "Martlet.ModelCatalogCheck." + Guid.NewGuid().ToString("N"));
        var failOpenRouter = false;
        var hugeVllm = false;
        var arrivals = new List<(DateTimeOffset At, string Path)>();
        await using var server = new FixtureServer(path =>
        {
            lock (arrivals) arrivals.Add((DateTimeOffset.UtcNow, path));
            if (path.StartsWith("/api/v1/models", StringComparison.Ordinal))
                return failOpenRouter ? (500, "{\"error\":\"fixture failure\"}") : (200, Fixtures.OpenRouter);
            if (path.StartsWith("/models.json", StringComparison.Ordinal)) return (200, Fixtures.ModelsDevModels);
            if (path.StartsWith("/api.json", StringComparison.Ordinal)) return (200, Fixtures.ModelsDevApi);
            if (path.StartsWith("/models.md", StringComparison.Ordinal)) return (200, Fixtures.NvidiaIndex);
            if (path.StartsWith("/x/gemma-4-31b-it.md", StringComparison.Ordinal)) return (200, Fixtures.NvidiaGemma);
            if (path.StartsWith("/x/flux.md", StringComparison.Ordinal)) return (200, Fixtures.NvidiaFlux);
            if (path.StartsWith("/vllm.md", StringComparison.Ordinal)) return (200, hugeVllm ? Fixtures.Vllm + new string(' ', 5 * 1024 * 1024) : Fixtures.Vllm);
            if (path.StartsWith("/rows", StringComparison.Ordinal))
                return (200, path.Contains("config=vision", StringComparison.Ordinal) ? Fixtures.ArenaVision : Fixtures.ArenaText);
            return (404, "{\"error\":\"not found\"}");
        });
        var origin = new Uri(server.Origin + "/");
        var endpoints = new ModelCatalogEndpoints(new(origin, "api/v1/models"), new(origin, "models.json"), new(origin, "api.json"),
            new(origin, "models.md"), new(origin, "vllm.md"), new(origin, "rows?dataset=lmarena-ai/leaderboard-dataset"));
        try
        {
            var store = new ModelCatalogStore(folder);
            using var client = ModelCatalogRefresh.CreateClient();
            var localFacts = LocalModelFactsFixture.Reader();
            var first = await new ModelCatalogRefresh(client, endpoints, local: localFacts).RunAsync(store, cancellation);
            Step("reads-every-source", first.Saved && first.Status.Problem is null &&
                    CatalogSources.Fetched.All(s => first.Status.Sources.GetValueOrDefault(s) is { Problem: null, Items: > 0 }),
                $"saved {first.Saved}; {string.Join(", ", CatalogSources.Fetched.Select(s => $"{s}: {first.Status.Sources.GetValueOrDefault(s)?.Items} items" +
                    (first.Status.Sources.GetValueOrDefault(s)?.Problem is { } p ? $" ({p})" : "")))}; {first.Models} models, {first.Routes} routes");
            var saved = File.ReadAllText(store.FilePath);
            Step("no-analysis-index-kept", !saved.Contains("intelligence_index", StringComparison.Ordinal) && !saved.Contains("16.7", StringComparison.Ordinal),
                "the daily copy keeps only where Artificial Analysis's index ranks a model (0 to 100), never the index itself (16.7 for Gemma 4 26B A4B)");
            var once = store.Due(DateTimeOffset.UtcNow);
            var tomorrow = ModelCatalogStore.Due(store.Status(), DateTimeOffset.UtcNow + TimeSpan.FromHours(25));
            Step("once-a-day", !once && tomorrow, $"due right after a refresh: {once}; due 25 hours later: {tomorrow}");

            var catalog = store.Load();
            var gemma26 = catalog.Find("google/gemma-4-26b-a4b-it")?.Model;
            var video = gemma26?.Fact(CatalogFacts.InputVideo);
            Step("gemma-4-26b-video", video is { Value: CatalogValues.Yes, From: CatalogSources.ConfigJson or CatalogSources.Vllm or CatalogSources.VllmFamily } &&
                    video.Answers.Any(a => a.Source == CatalogSources.ModelsDev && a.Value == CatalogValues.No),
                $"video: {video?.Value ?? "unknown"} from {video?.From}; answers: {Answers(video)}");
            var qwen = catalog.Find("qwen/qwen3.5-122b-a10b")?.Model;
            var audio = qwen?.Fact(CatalogFacts.InputAudio);
            Step("qwen3.5-122b-audio", audio is { Value: CatalogValues.No, From: CatalogSources.ConfigJson or CatalogSources.Vllm or CatalogSources.VllmFamily } &&
                    audio.Answers.Any(a => a.Source == CatalogSources.ModelsDev && a.Value == CatalogValues.Yes),
                $"audio: {audio?.Value ?? "unknown"} from {audio?.From}; answers: {Answers(audio)}");
            var gemma31 = catalog.Find("google/gemma-4-31b-it")?.Model;
            var groq = catalog.Route("groq", "google/gemma-4-31b-it");
            var together = catalog.Route("https://api.together.xyz/v1", "google/gemma-4-31b-it");
            var groqRows = groq?.Fact(CatalogFacts.InputImage).Answers.Where(a => a.Source == CatalogSources.ModelsDevRows).ToList();
            Step("gemma-4-31b-provider-split", gemma31 is { Hears: false, TakesVideo: true, Sees: true } && groq is not null && together is not null &&
                    groqRows is [{ Value: CatalogValues.No }] && together.Fact(CatalogFacts.InputAudio).Answers.Count(a => a.Source == CatalogSources.ModelsDevRows) == 1 &&
                    together.Fact(CatalogFacts.InputAudio).Value == CatalogValues.No,
                $"model: hears {gemma31?.Hears}, sees {gemma31?.Sees}, video {gemma31?.TakesVideo}; one provider listing audio doesn't make it hear " +
                $"({Answers(gemma31?.Fact(CatalogFacts.InputAudio))}); the groq route keeps only groq's row ({Answers(groq?.Fact(CatalogFacts.InputImage))}); " +
                $"the together route: audio {together?.Fact(CatalogFacts.InputAudio).Value}");
            var nvidia = catalog.Route("nvidia-build", "google/gemma-4-31b-it");
            Step("nvidia-route", nvidia is { Free: true, Context: 262144 } && nvidia.Fact(CatalogFacts.Tools).Value == CatalogValues.Yes &&
                    catalog.Routes.All(r => r.ModelId != "black-forest-labs/flux_1-dev"),
                $"NVIDIA Build: free {nvidia?.Free} ({nvidia?.FreeNote}), context {nvidia?.Context}, tools {nvidia?.Fact(CatalogFacts.Tools).Value}; " +
                "a page without type: \"endpoint\" gives no route");
            var retired = catalog.Route("openrouter", "old/retired-model");
            Step("expired-route-retired", retired is { Retired: true }, $"old/retired-model expired {retired?.Expires}: retired {retired?.Retired}");
            (string Name, string Key)[] names =
            [
                ("gemma4:26b", "google/gemma-4-26B-A4B-it"), ("hf.co/unsloth/gemma-4-26B-A4B-it-GGUF:Q4_K_M", "google/gemma-4-26B-A4B-it"),
                ("google/gemma-4-26b-a4b-it:free", "google/gemma-4-26B-A4B-it"), ("gemma-4-31b", "google/gemma-4-31B-it"),
                ("openai/gpt-5", "openai/gpt-5")
            ];
            var found = names.Select(n => (n.Name, n.Key, Found: catalog.Find(n.Name))).ToList();
            Step("find-by-any-name", found.All(f => string.Equals(f.Found?.Model.Key, f.Key, StringComparison.OrdinalIgnoreCase)),
                string.Join("; ", found.Select(f => $"{f.Name} -> {f.Found?.Model.Key ?? "none"} ({f.Found?.How})")));
            var smart = gemma26 is null ? null : catalog.Smartness(gemma26);
            var e2b = catalog.Find("gemma4:e2b")?.Model;
            var fallback = e2b is null ? null : catalog.Smartness(e2b);
            Step("smartness", smart is { Credit: CatalogSources.LmArenaCredit, Rating: not null, From: "its own scores" } && fallback is not null,
                $"Gemma 4 26B A4B: {smart?.Words} (tier {smart?.Tier}, from {smart?.From}; LMArena {smart?.Rating} with credit \"{smart?.Credit}\"); " +
                $"Gemma 4 E2B, which nothing scores: {fallback?.Words} (from {fallback?.From})");
            var e2bAudio = e2b?.Fact(CatalogFacts.InputAudio);
            var gemma26Audio = gemma26?.Fact(CatalogFacts.InputAudio);
            Step("config-json-decides-by-size", e2bAudio is { Value: CatalogValues.Yes, From: CatalogSources.ConfigJson } &&
                    gemma26Audio is { Value: CatalogValues.No, From: CatalogSources.ConfigJson },
                $"Gemma 4 E2B audio: {e2bAudio?.Value} from {e2bAudio?.From} ({Answers(e2bAudio)}); Gemma 4 26B A4B audio: {gemma26Audio?.Value} " +
                $"from {gemma26Audio?.From} ({Answers(gemma26Audio)})");
            var memory = e2b?.Memory();
            var measured = LocalModelFactsFixture.MeasuredGraphicsGb["gemma4:e2b"];
            Step("local-memory", e2b is { LocallyHostable: true } && memory is not null &&
                    Math.Abs(memory.GraphicsBytes / 1e9 - measured) / measured <= 0.15 && e2b.Local?.Quantization()?.InstallName is { Length: > 0 },
                $"Gemma 4 E2B: locally hostable {e2b?.LocallyHostable}; installs as {e2b?.Local?.Quantization()?.InstallName}; {memory?.Describe()} " +
                $"(measured {measured} GB on an RTX 4070)");

            failOpenRouter = true;
            hugeVllm = true;
            var second = await new ModelCatalogRefresh(client, endpoints, local: localFacts).RunAsync(store, cancellation);
            var kept = store.Cached();
            Step("keeps-last-good", second.Saved && second.Status.Sources[CatalogSources.OpenRouter].Problem is { } orProblem &&
                    orProblem.Contains("500", StringComparison.Ordinal) && second.Status.Sources[CatalogSources.Vllm].Problem is { } vProblem &&
                    vProblem.Contains("larger than", StringComparison.Ordinal) &&
                    kept?.Block(CatalogSources.OpenRouter)?.Read == first.Data.Block(CatalogSources.OpenRouter)?.Read &&
                    kept?.Block(CatalogSources.ModelsDev)?.Read > first.Data.Block(CatalogSources.ModelsDev)?.Read &&
                    store.Load().Find("google/gemma-4-26b-a4b-it")?.Model.TakesVideo == true,
                $"OpenRouter: {second.Status.Sources[CatalogSources.OpenRouter].Problem}; vLLM: {second.Status.Sources[CatalogSources.Vllm].Problem}; " +
                $"status problem: {second.Status.Problem}");
            failOpenRouter = hugeVllm = false;

            var holdUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            lock (arrivals) arrivals.Clear();
            var third = await new ModelCatalogRefresh(client, endpoints, busy: () => DateTimeOffset.UtcNow < holdUntil, local: localFacts)
                .RunAsync(store, cancellation);
            List<(DateTimeOffset At, string Path)> asked;
            lock (arrivals) asked = arrivals.ToList();
            Step("waits-while-replying", third.Status.Problem is null && asked.Count > 0 && asked.All(a => a.At >= holdUntil),
                $"Martlet was busy replying for 2 seconds: {asked.Count} requests, the first {(asked.Count > 0 ? (asked.Min(a => a.At) - holdUntil).TotalMilliseconds : 0):0} ms " +
                "after the reply ended");
        }
        finally
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return new
        {
            exitCode = passed ? 0 : 1,
            report = new
            {
                passed, total = steps.Count, steps,
                notCovered = "FIXTURE sources on 127.0.0.1 shaped like the real ones; live=true reads the real public sources."
            }
        };
    }

    private static string Answers(CatalogFact? fact) => fact is null ? "none"
        : string.Join(", ", fact.Answers.Select(a => $"{a.Source} {a.Value}"));

    /// <summary>A one-request-per-connection HTTP server on 127.0.0.1 that answers GETs with <c>answer(path)</c>.</summary>
    private sealed class FixtureServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task serving;

        internal FixtureServer(Func<string, (int Status, string Body)> answer)
        {
            listener.Start();
            serving = ServeAsync(answer);
        }

        internal string Origin => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";

        private async Task ServeAsync(Func<string, (int Status, string Body)> answer)
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            await using var stream = client.GetStream();
                            var head = new StringBuilder();
                            var one = new byte[1];
                            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one, stop.Token) == 1)
                                head.Append((char)one[0]);
                            var path = head.ToString().Split(' ') is { Length: > 1 } parts ? parts[1] : "/";
                            var (status, body) = answer(path);
                            var payload = Encoding.UTF8.GetBytes(body);
                            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\n" +
                                $"Content-Type: text/plain; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
                            await stream.WriteAsync(payload, stop.Token);
                        }
                        catch (Exception error) when (error is IOException or OperationCanceledException or SocketException) { }
                    }
                });
            }
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            try { await serving; } catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
            stop.Dispose();
        }
    }

    /// <summary>Small copies of each source, shaped like the real ones on 2026-10-10 (FIXTURE).</summary>
    internal static class Fixtures
    {
        internal const string OpenRouter = """
            {"data":[
            {"id":"google/gemma-4-26b-a4b-it","hugging_face_id":"google/gemma-4-26B-A4B-it","name":"Google: Gemma 4 26B A4B ","context_length":262144,"architecture":{"input_modalities":["image","text","video"],"output_modalities":["text"]},"pricing":{"prompt":"0.0000000675","completion":"0.000000225"},"supported_parameters":["tools","reasoning","temperature"],"benchmarks":{"artificial_analysis":{"intelligence_index":16.7,"coding_index":39.3}}},
            {"id":"google/gemma-4-26b-a4b-it:free","hugging_face_id":"google/gemma-4-26B-A4B-it","name":"Google: Gemma 4 26B A4B (free)","context_length":131072,"architecture":{"input_modalities":["image","text","video"],"output_modalities":["text"]},"pricing":{"prompt":"0","completion":"0"},"supported_parameters":["tools"],"benchmarks":{"artificial_analysis":{"intelligence_index":16.7}}},
            {"id":"qwen/qwen3.5-122b-a10b","hugging_face_id":"Qwen/Qwen3.5-122B-A10B","name":"Qwen: Qwen3.5-122B-A10B","context_length":262144,"architecture":{"input_modalities":["text","image","video"],"output_modalities":["text"]},"pricing":{"prompt":"0.00000026","completion":"0.00000208"},"supported_parameters":["tools","reasoning"],"benchmarks":{"artificial_analysis":{"intelligence_index":17.7}}},
            {"id":"google/gemma-4-31b-it","hugging_face_id":"google/gemma-4-31B-it","name":"Google: Gemma 4 31B","context_length":262144,"architecture":{"input_modalities":["image","text","video"],"output_modalities":["text"]},"pricing":{"prompt":"0.0000001","completion":"0.0000003"},"supported_parameters":["tools","reasoning"],"benchmarks":{"artificial_analysis":{"intelligence_index":14.7}}},
            {"id":"openai/gpt-5","hugging_face_id":"","name":"OpenAI: GPT-5","context_length":400000,"architecture":{"input_modalities":["text","image","file"],"output_modalities":["text"]},"pricing":{"prompt":"0.00000125","completion":"0.00001"},"supported_parameters":["tools","reasoning"],"knowledge_cutoff":"2024-09-30","benchmarks":{"artificial_analysis":{"intelligence_index":23}}},
            {"id":"old/retired-model","hugging_face_id":"","name":"Old: Retired","context_length":8192,"architecture":{"input_modalities":["text"],"output_modalities":["text"]},"pricing":{"prompt":"0.000001","completion":"0.000001"},"supported_parameters":[],"expiration_date":"2026-01-01"}
            ]}
            """;

        internal const string ModelsDevModels = """
            {
            "google/gemma-4-26b-a4b-it":{"id":"google/gemma-4-26b-a4b-it","name":"Gemma 4 26B A4B IT","family":"gemma","tool_call":true,"reasoning":true,"release_date":"2026-04-02","modalities":{"input":["text","image"],"output":["text"]},"open_weights":true,"limit":{"context":262144,"output":32768},"weights":[{"label":"Hugging Face","url":"https://huggingface.co/google/gemma-4-26B-A4B-it"}]},
            "alibaba/qwen3.5-122b-a10b":{"id":"alibaba/qwen3.5-122b-a10b","name":"Qwen3.5 122B-A10B","family":"qwen","tool_call":true,"reasoning":true,"release_date":"2026-02-23","modalities":{"input":["text","image","video","audio"],"output":["text"]},"open_weights":true,"limit":{"context":262144,"output":65536},"weights":[{"label":"Hugging Face","url":"https://huggingface.co/Qwen/Qwen3.5-122B-A10B"}]},
            "google/gemma-4-31b-it":{"id":"google/gemma-4-31b-it","name":"Gemma 4 31B IT","family":"gemma","tool_call":true,"reasoning":true,"release_date":"2026-04-02","modalities":{"input":["text","image"],"output":["text"]},"open_weights":true,"limit":{"context":262144,"output":32768},"weights":[{"label":"Hugging Face","url":"https://huggingface.co/google/gemma-4-31B-it"}]},
            "openai/gpt-5":{"id":"openai/gpt-5","name":"GPT-5","family":"gpt","tool_call":true,"reasoning":true,"knowledge":"2024-09","release_date":"2025-08-07","modalities":{"input":["text","image"],"output":["text"]},"open_weights":false,"limit":{"context":400000,"output":128000}}
            }
            """;

        internal const string ModelsDevApi = """
            {
            "openrouter":{"id":"openrouter","api":"https://openrouter.ai/api/v1","models":{
              "google/gemma-4-26b-a4b-it":{"id":"google/gemma-4-26b-a4b-it","canonical_model_id":"google/gemma-4-26b-a4b-it","modalities":{"input":["text","image","video"],"output":["text"]},"limit":{"context":262144}},
              "openai/gpt-5":{"id":"openai/gpt-5","canonical_model_id":"openai/gpt-5","modalities":{"input":["text","image"],"output":["text"]},"limit":{"context":400000}}}},
            "deepinfra":{"id":"deepinfra","models":{
              "google/gemma-4-31B-it":{"id":"google/gemma-4-31B-it","canonical_model_id":"google/gemma-4-31b-it","modalities":{"input":["text","image","video"],"output":["text"]},"cost":{"input":0.1,"output":0.3},"limit":{"context":262144}},
              "Qwen/Qwen3.5-122B-A10B":{"id":"Qwen/Qwen3.5-122B-A10B","canonical_model_id":"alibaba/qwen3.5-122b-a10b","modalities":{"input":["text","image","video","audio"],"output":["text"]},"limit":{"context":262144}}}},
            "groq":{"id":"groq","models":{
              "google/gemma-4-31b-it":{"id":"google/gemma-4-31b-it","canonical_model_id":"google/gemma-4-31b-it","modalities":{"input":["text"],"output":["text"]},"cost":{"input":0,"output":0},"limit":{"context":131072}}}},
            "togetherai":{"id":"togetherai","models":{
              "google/gemma-4-31b-it":{"id":"google/gemma-4-31b-it","canonical_model_id":"google/gemma-4-31b-it","status":"deprecated","modalities":{"input":["text","image","audio"],"output":["text"]},"limit":{"context":262144}}}}
            }
            """;

        internal const string NvidiaIndex = """
            # Models

            - [gemma-4-31b-it](/x/gemma-4-31b-it.md) — Dense 31B model.
            - [FLUX.1-dev](/x/flux.md) — Image generation.
            - [gemma-4-31b-it](/x/gemma-4-31b-it.md) — Dense 31B model.
            """;

        internal const string NvidiaGemma = """
            ---
            title: "gemma-4-31b-it"
            publisher: "google"
            type: "endpoint"
            canonical: "https://build.nvidia.com/google/gemma-4-31b-it"
            ---

            # Gemma 4 31B IT

            ## Release Date:
            **Huggingface:** 04/02/2026 via [link](https://huggingface.co/google/gemma-4-31B-it)

            ### Training Dataset
            **Data Modality:** Text, Image, Audio, Other (Code)

            ## Specifications

            - **Context Length:** 262,144 tokens
            - **Parameters:** 32,682,372,656
            - **Input:** Text, Image, Video
            - **Output:** Text

            ## Capabilities

            - **Function Calling:** Supported
            - **Structured Output:** Not supported
            - **Reasoning:** Supported
            """;

        internal const string NvidiaFlux = """
            ---
            title: "FLUX.1-dev"
            publisher: "black-forest-labs"
            canonical: "https://build.nvidia.com/black-forest-labs/flux_1-dev"
            ---

            # Overview
            """;

        internal const string Vllm = """
            # Supported Models

            ## List of Text-only Language Models

            | Architecture | Models | Example HF Models | [LoRA](../features/lora.md) | [PP](../serving/parallelism_scaling.md) |
            | ------------ | ------ | ----------------- | --------------------------- | --------------------------------------- |
            | `Gemma4ForCausalLM` | Gemma 4 | `google/gemma-4-E2B-it`, etc. | ✅︎ | ✅︎ |

            ## List of Multimodal Language Models

            | Architecture | Models | Inputs | Example HF Models | [LoRA](../features/lora.md) | [PP](../serving/parallelism_scaling.md) |
            | ------------ | ------ | ------ | ----------------- | --------------------------- | --------------------------------------- |
            | `Gemma4ForConditionalGeneration` | Gemma 4 | T + I<sup>+</sup> + V + A<sup>*</sup> | `google/gemma-4-E2B-it`, etc. | ✅︎ | ✅︎ |
            | `Gemma4UnifiedForConditionalGeneration` | Gemma 4 Unified | T + I<sup>+</sup> + V + A | `google/gemma-4-12B-it`, etc. | | ✅︎ |
            | `Qwen3_5MoeForConditionalGeneration` | Qwen3.5-MOE | T + I<sup>E+</sup> + V<sup>E+</sup> | `Qwen/Qwen3.5-35B-A3B-Instruct`, etc. | ✅︎ | ✅︎ |
            """;

        internal static readonly string ArenaText = Rows(
            ("gpt-5-high", 1480, 30000, "overall"), ("gpt-5-low", 1460, 10000, "overall"), ("gemma-4-31b", 1450, 8000, "overall"),
            ("gemma-4-26b-a4b", 1440, 7000, "overall"), ("qwen3.5-122b-a10b", 1435, 6000, "overall"), ("gpt-5-high", 1500, 900, "chinese"));

        internal static readonly string ArenaVision = Rows(("gemma-4-31b", 1300, 2000, "overall"), ("gemma-4-31b", 1310, 100, "chinese"));

        private static string Rows(params (string Name, double Rating, int Votes, string Category)[] rows) =>
            "{\"rows\":[" + string.Join(",", rows.Select((r, i) => $"{{\"row_idx\":{i},\"row\":{{\"model_name\":\"{r.Name}\",\"organization\":\"x\"," +
                $"\"license\":\"Proprietary\",\"rating\":{r.Rating},\"vote_count\":{r.Votes},\"rank\":{i + 1},\"category\":\"{r.Category}\"," +
                "\"leaderboard_publish_date\":\"2026-10-08\"},\"truncated_cells\":[]}")) + "],\"num_rows_total\":" + rows.Length + "}";
    }
}
