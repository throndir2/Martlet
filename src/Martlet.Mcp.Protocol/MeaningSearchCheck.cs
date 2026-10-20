using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Martlet.Conversation.Guides;
using Martlet.Core.Search;
using Martlet.Mcp.Shared;
using Martlet.Memory;
using Martlet.Providers.Ollama;

namespace Martlet.Mcp;

/// <summary>meaning_search_check: finding by meaning (docs/SEARCH.md, "Finding by meaning") with the production code on synthetic
/// text in a disposable folder: the vector sets (search, a changed text embedded again, a deleted document dropped, saved and read
/// back, another model's file refused, 10,000 vectors searched in about a millisecond), memory's fusion through the search engine
/// (held-out questions in other words, names still found by their words), tidying memory grouping facts close in meaning, a
/// guide's fusion, and the Ollama embedder against a loopback fixture (its request, its answer, a model that isn't downloaded and
/// the rule that holds it back when loading it could unload another model). The embeddings are a FIXTURE (NOT AI) unless
/// <c>measure</c> asks this PC's Ollama for the real model. Never opens your data.</summary>
internal static class MeaningSearchCheck
{
    // Held-out questions that share no search term with the fact they ask about, and two that name something.
    private static readonly (string Question, string Fact)[] Paraphrases =
    [
        ("What should I cook tonight?", "The user is vegetarian."),
        ("Who needs a walk this evening?", "The user's dog is called Rex."),
        ("Which neighborhood is my apartment in?", "The user lives in Seattle."),
        ("Any concert tips for me?", "The user plays the guitar in a band."),
        ("How was the shift at the clinic?", "The user works as a nurse at the hospital."),
        ("Did I book the flight yet?", "The user is planning a trip to Japan in May."),
        ("Why am I so tired lately?", "The user often can't sleep before exams.")
    ];

    private static readonly string[] Others =
    [
        "The user's sister is called Ana.", "The user likes the color green.", "The user was born in March.",
        "The user's favorite number is seven.", "The user collects old maps.", "The user has a red bicycle.",
        "The user speaks some French.", "The user's car is a blue hatchback.", "The user reads mystery novels.",
        "The user's brother is called Tom.", "The user prefers tea to coffee.", "The user wears glasses."
    ];

    internal static async Task<object> RunAsync(bool measure, string? model, CancellationToken cancellation)
    {
        var steps = new List<object>();
        var failures = new List<string>();
        void Step(string name, bool passed, object detail)
        {
            steps.Add(new { name, passed, detail });
            if (!passed) failures.Add(name);
        }
        var root = Path.Combine(Path.GetTempPath(), "martlet-meaning-search-check-" + Guid.NewGuid().ToString("N"));
        object? measured = null;
        try
        {
            Directory.CreateDirectory(root);
            var embedder = new FixtureEmbedder();

            // ---------- vectors ----------
            var set = new VectorSet(embedder.Model);
            await set.FillAsync(embedder, [("a", "The user is vegetarian."), ("b", "The user's dog is called Rex."), ("c", "The user lives in Seattle.")],
                null, cancellation);
            var query = FixtureEmbedder.Embed("What should I cook tonight?");
            var nearest = set.Nearest(query, 3);
            var missing = set.Missing([("a", "The user is vegan."), ("b", "The user's dog is called Rex.")]);
            set.Keep(["a", "b"]);
            var path = Path.Combine(root, "vectors", "set.vectors");
            set.Save(path);
            var loaded = VectorSet.Load(path, embedder.Model);
            var other = VectorSet.Load(path, "ollama:another-model");
            Step("vector sets: nearest first, a changed text embedded again, a deleted document dropped, saved and read back, another model's file refused",
                nearest[0].Id == "a" && nearest[0].Meaning > nearest[1].Meaning && missing.Count == 1 && missing[0].Id == "a" && set.Count == 2 &&
                loaded.Count == 2 && loaded.Vector("a")!.SequenceEqual(set.Vector("a")!) && other.Count == 0,
                new { order = nearest.Select(n => n.Id), meaning = nearest.Select(n => Math.Round(n.Meaning, 3)), missing = missing.Count, kept = set.Count, loaded = loaded.Count });

            var random = new Random(11);
            var large = new VectorSet("check:random");
            for (var i = 0; i < 10_000; i++)
                large.Put(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "",
                    [.. Enumerable.Range(0, 768).Select(_ => (float)(random.NextDouble() - 0.5))]);
            var probe = Enumerable.Range(0, 768).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray();
            large.Nearest(probe, 8);
            var times = new List<double>();
            for (var i = 0; i < 20; i++)
            {
                var watch = Stopwatch.StartNew();
                large.Nearest(probe, 8);
                times.Add(watch.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            Step("10,000 vectors of 768 numbers are searched in milliseconds, in this process", times[times.Count / 2] < 50,
                new { documents = large.Count, medianMilliseconds = Math.Round(times[times.Count / 2], 2) });

            // ---------- memory ----------
            var preview = MemoryStoreActivationPreview.Create(Path.Combine(root, "store"));
            using var store = MemoryStore.Open(preview, preview.Authorize(MemoryConsentDecision.Allow));
            var facts = new List<MemoryFact>();
            foreach (var content in Paraphrases.Select(p => p.Fact).Concat(Others))
                facts.Add((await store.SaveAsync(new()
                {
                    Content = content, Provenance = MemoryProvenance.Conversation(Guid.NewGuid(), DateTimeOffset.UtcNow),
                    Retention = MemoryRetention.UntilDeleted()
                }, cancellation)).Fact);
            var memory = new VectorSet(embedder.Model);
            await memory.FillAsync(embedder, [.. facts.Select(f => (f.Id.ToString("N"), f.Content))], null, cancellation);
            var evaluation = await EvaluateAsync(store, facts, memory, text => Task.FromResult(FixtureEmbedder.Embed(text)), cancellation);
            Step("held-out questions in other words: the fused search finds the fact in its first 3 where words alone don't",
                evaluation.Fused == Paraphrases.Length && evaluation.Lexical < Paraphrases.Length,
                new { questions = Paraphrases.Length, lexicalRecallAt3 = evaluation.Lexical, fusedRecallAt3 = evaluation.Fused });
            var rex = facts.First(f => f.Content.Contains("Rex", StringComparison.Ordinal));
            var named = await FusedAsync(store, memory, "Is Rex okay?", FixtureEmbedder.Embed("Is Rex okay?"), cancellation);
            var ana = facts.First(f => f.Content.Contains("Ana", StringComparison.Ordinal));
            var sister = await FusedAsync(store, memory, "What did Ana say?", FixtureEmbedder.Embed("What did Ana say?"), cancellation);
            Step("names stay strong: a question that names Rex or Ana finds that fact first after fusion",
                named.FirstOrDefault()?.Fact.Id == rex.Id && sister.FirstOrDefault()?.Fact.Id == ana.Id,
                new { rexFirst = named.FirstOrDefault()?.Fact.Id == rex.Id, anaFirst = sister.FirstOrDefault()?.Fact.Id == ana.Id });

            // Tidying memory: a fact that a newer one replaces, in other words.
            var seattle = facts.First(f => f.Content.Contains("Seattle", StringComparison.Ordinal));
            await Task.Delay(20, cancellation);
            var portland = (await store.SaveAsync(new()
            {
                Content = "The user moved to Portland last spring.", Provenance = MemoryProvenance.Conversation(Guid.NewGuid(), DateTimeOffset.UtcNow),
                Retention = MemoryRetention.UntilDeleted()
            }, cancellation)).Fact;
            await memory.FillAsync(embedder, [(portland.Id.ToString("N"), portland.Content)], null, cancellation);
            var snapshot = await store.SnapshotAsync(cancellation);
            IReadOnlyCollection<Guid> Neighbors(MemoryFact fact) => memory.Vector(fact.Id.ToString("N")) is { } vector
                ? [.. memory.Nearest(vector, 4, except: fact.Id.ToString("N")).Where(n => n.Meaning >= 0.5).Select(n => Guid.ParseExact(n.Id, "N"))]
                : [];
            bool Grouped(MemoryUpkeepPlan plan) => plan.Groups.Any(group =>
                group.Select(n => plan[n].Id).Contains(seattle.Id) && group.Select(n => plan[n].Id).Contains(portland.Id));
            var byWords = MemoryUpkeep.Plan(snapshot, DateTimeOffset.UtcNow, null);
            var byMeaning = MemoryUpkeep.Plan(snapshot, DateTimeOffset.UtcNow, null, similar: Neighbors);
            Step("tidying memory groups \"lives in Seattle\" with \"moved to Portland\" by meaning, which share too few words to group",
                !Grouped(byWords) && Grouped(byMeaning), new { groupsByWords = byWords.Groups.Count, groupsByMeaning = byMeaning.Groups.Count });

            // Forgetting a fact drops its vector at the next refresh.
            await store.DeleteManyAsync([new DeleteFactRequest { Id = rex.Id, ExpectedRevision = rex.Revision, ConsentId = Guid.NewGuid() }], cancellation);
            var live = (await store.InspectAsync(cancellation)).Facts.Where(f => !f.IsHistory).Select(f => f.Id.ToString("N")).ToHashSet();
            var dropped = memory.Keep(live);
            Step("a forgotten fact's vector is dropped at the next refresh (derived data, never a second copy)",
                dropped == 1 && memory.Vector(rex.Id.ToString("N")) is null, new { dropped, kept = memory.Count });

            // ---------- a guide ----------
            GuideChunk[] chunks =
            [
                new("https://wiki.example/Blacksmith", "Blacksmith", "Blacksmith › Upgrades", "Bring ore to Hilda at the forge to raise a sword's damage by one level."),
                new("https://wiki.example/Fishing", "Fishing", "Fishing › Spots", "Cast your line at dawn near the pier for the best catch."),
                new("https://wiki.example/Farming", "Farming", "Farming › Crops", "Plant turnips in spring and water them every morning."),
                new("https://wiki.example/Mines", "Mines", "Mines › Floors", "Every fifth floor of the mines has a ladder down and a chest."),
                new("https://wiki.example/Town", "Town", "Town › Shops", "The general store sells seeds and opens at nine.")
            ];
            var guide = GuideIndex.Build(chunks);
            var guideVectors = new VectorSet(embedder.Model);
            await guideVectors.FillAsync(embedder, [.. chunks.Select((c, i) => (i.ToString(System.Globalization.CultureInfo.InvariantCulture), c.Section + "\n" + c.Text))],
                null, cancellation);
            const string weapons = "How do I make my weapons stronger?";
            var lexicalGuide = guide.Search(weapons, 3);
            var nearChunks = guideVectors.Nearest(FixtureEmbedder.Embed(weapons), 20).Where(n => n.Meaning >= 0.2)
                .Select(n => (int.Parse(n.Id, System.Globalization.CultureInfo.InvariantCulture), n.Meaning)).ToArray();
            var fusedGuide = guide.Search(weapons, 3, nearChunks);
            const string pier = "fishing pier";
            var fishing = guide.Search(pier, 3, [.. guideVectors.Nearest(FixtureEmbedder.Embed(pier), 20).Where(n => n.Meaning >= 0.2)
                .Select(n => (int.Parse(n.Id, System.Globalization.CultureInfo.InvariantCulture), n.Meaning))]);
            Step("search_guide: \"How do I make my weapons stronger?\" finds the blacksmith's upgrades by meaning, with its meaning as relevance",
                lexicalGuide.All(h => h.Chunk.Page != "Blacksmith") && fusedGuide.FirstOrDefault()?.Chunk.Page == "Blacksmith" &&
                fusedGuide[0].Relevance >= nearChunks[0].Item2 - 1e-9 && fishing.FirstOrDefault()?.Chunk.Page == "Fishing",
                new { lexical = lexicalGuide.Select(h => h.Chunk.Page), fused = fusedGuide.Select(h => h.Chunk.Page),
                    relevance = fusedGuide.Select(h => Math.Round(h.Relevance, 2)), byWords = fishing.Select(h => h.Chunk.Page) });

            // ---------- the Ollama embedder ----------
            await using (var fixture = new EmbedFixture())
            {
                using var http = new HttpClient();
                var ollama = new OllamaEmbedder(http, new Uri(fixture.Origin + "/"), "nomic-embed-text", threads: 2);
                var vectors = await ollama.EmbedAsync(["What should I cook tonight?"], EmbeddingUse.Query, cancellation);
                using var sent = JsonDocument.Parse(fixture.LastBody ?? "{}");
                var body = sent.RootElement;
                var options = body.TryGetProperty("options", out var o) ? o : default;
                Step("the Ollama embedder asks /api/embed for the processor only (num_gpu 0, 2 threads), keeps the model an hour, lets Ollama cut long texts and starts a question as nomic-embed-text wants",
                    fixture.LastPath == "/api/embed" && vectors is [{ Length: 4 }] && body.GetProperty("model").GetString() == "nomic-embed-text" &&
                    options.ValueKind == JsonValueKind.Object && options.GetProperty("num_gpu").GetInt32() == 0 && options.GetProperty("num_thread").GetInt32() == 2 &&
                    body.GetProperty("keep_alive").GetString() == OllamaEmbedder.KeepAlive && body.GetProperty("truncate").GetBoolean() &&
                    body.GetProperty("input")[0].GetString() == "search_query: What should I cook tonight?",
                    new { path = fixture.LastPath, dimensions = vectors.FirstOrDefault()?.Length, prefix = "search_query: " });
                var absent = new OllamaEmbedder(http, new Uri(fixture.Origin + "/"), "not-downloaded", threads: 2);
                OllamaEmbedException? refused = null;
                try { await absent.EmbedAsync(["hello"], EmbeddingUse.Document, cancellation); }
                catch (OllamaEmbedException error) { refused = error; }
                Step("a model Ollama doesn't have is told apart (Download is offered; nothing is downloaded without asking)",
                    refused is { Missing: true }, new { missing = refused?.Missing, message = refused?.Message });
            }
            var hold = new
            {
                threeLoaded = OllamaEmbedder.HoldBack([new("a:1b", 10, 10), new("b:1b", 10, 10), new("c:1b", 10, 10)], "nomic-embed-text"),
                onProcessor = OllamaEmbedder.HoldBack([new("gemma4:e4b", 100, 40)], "nomic-embed-text"),
                alreadyLoaded = OllamaEmbedder.HoldBack([new("a:1b", 10, 10), new("b:1b", 10, 10), new("nomic-embed-text:latest", 5, 0)], "nomic-embed-text"),
                besideOneOnCard = OllamaEmbedder.HoldBack([new("gemma4:e4b", 100, 100)], "nomic-embed-text")
            };
            Step("loading the embedding model is held back while Ollama has 3 models loaded or one runs on the processor, so it never pushes the conversation's model out",
                hold.threeLoaded is not null && hold.onProcessor is not null && hold.alreadyLoaded is null && hold.besideOneOnCard is null, hold);

            // ---------- a real model (only when asked) ----------
            if (measure) measured = await MeasureAsync(model ?? Martlet.Core.Settings.MeaningSearchFile.DefaultModel, root, cancellation);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return new
        {
            passed = failures.Count == 0, failures, steps,
            measured = measured ?? (object)new { run = false, why = "NOT RUN: pass measure: true to time a real embedding model in this PC's Ollama." },
            note = "The embeddings are a FIXTURE (NOT AI) with the production vector sets, memory store, search engine fusion, tidying " +
                "plan, guide index and Ollama embedder (against a loopback fixture). Nothing leaves this PC."
        };
    }

    private sealed record Evaluation(int Lexical, int Fused, IReadOnlyList<double> QueryMilliseconds);

    private static async Task<Evaluation> EvaluateAsync(MemoryStore store, IReadOnlyList<MemoryFact> facts, VectorSet memory,
        Func<string, Task<float[]>> embed, CancellationToken token)
    {
        var lexical = 0;
        var fused = 0;
        var times = new List<double>();
        foreach (var (question, fact) in Paraphrases)
        {
            var target = facts.First(f => f.Content == fact).Id;
            var words = MemoryQuery.TryFromBoundedSource(question, 3) is { } q ? (await store.RetrieveAsync(q, token)).Hits : [];
            if (words.Any(h => h.Fact.Id == target)) lexical++;
            var watch = Stopwatch.StartNew();
            var vector = await embed(question);
            times.Add(watch.Elapsed.TotalMilliseconds);
            if ((await FusedAsync(store, memory, question, vector, token)).Any(h => h.Fact.Id == target)) fused++;
        }
        return new(lexical, fused, times);
    }

    // The fused search as remembering and the find tools run it: the facts nearest in meaning (at least 0.15) join the words'.
    private static async Task<IReadOnlyList<MemoryRetrievalHit>> FusedAsync(MemoryStore store, VectorSet memory, string question, float[] vector,
        CancellationToken token)
    {
        if (MemoryQuery.TryFromBoundedSource(question, 3) is not { } query) return [];
        var near = memory.Nearest(vector, 8).Where(n => n.Meaning >= 0.15).Select(n => Guid.ParseExact(n.Id, "N")).ToArray();
        return (await store.RetrieveAsync(query with { Similar = near }, token)).Hits;
    }

    // Times the real model in this PC's Ollama (loopback): the same held-out questions and facts, never a download.
    private static async Task<object> MeasureAsync(string model, string root, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var origin = new Uri("http://127.0.0.1:11434/");
        var tags = await OllamaSideBySide.DownloadsAsync(http, origin, token);
        if (tags is null) return new { run = false, why = "NOT RUN: Ollama isn't running on this PC." };
        if (!tags.Keys.Any(name => OllamaSideBySide.Same(name, model)))
            return new { run = false, why = $"NOT RUN: {model} isn't downloaded in Ollama on this PC (this check never downloads)." };
        var loadedModels = await OllamaSideBySide.LoadedAsync(http, origin, token) ?? [];
        if (OllamaEmbedder.HoldBack(loadedModels, model) is { } why) return new { run = false, why = $"NOT RUN: {why}." };
        var ollama = new OllamaEmbedder(http, origin, model);
        var load = Stopwatch.StartNew();
        await ollama.EmbedAsync(["warm up"], EmbeddingUse.Query, token);
        var firstMilliseconds = load.Elapsed.TotalMilliseconds;
        var preview = MemoryStoreActivationPreview.Create(Path.Combine(root, "measure"));
        using var store = MemoryStore.Open(preview, preview.Authorize(MemoryConsentDecision.Allow));
        var facts = new List<MemoryFact>();
        foreach (var content in Paraphrases.Select(p => p.Fact).Concat(Others))
            facts.Add((await store.SaveAsync(new()
            {
                Content = content, Provenance = MemoryProvenance.Conversation(Guid.NewGuid(), DateTimeOffset.UtcNow),
                Retention = MemoryRetention.UntilDeleted()
            }, token)).Fact);
        var memory = new VectorSet(ollama.Model);
        var documents = Stopwatch.StartNew();
        await memory.FillAsync(ollama, [.. facts.Select(f => (f.Id.ToString("N"), f.Content))], null, token);
        var documentMilliseconds = documents.Elapsed.TotalMilliseconds / facts.Count;
        var evaluation = await EvaluateAsync(store, facts, memory,
            async text => (await ollama.EmbedAsync([text], EmbeddingUse.Query, token))[0], token);
        var sorted = evaluation.QueryMilliseconds.Order().ToArray();
        return new
        {
            run = true, model, threads = OllamaEmbedder.DefaultThreads,
            firstRequestMilliseconds = Math.Round(firstMilliseconds),
            queryMedianMilliseconds = Math.Round(sorted[sorted.Length / 2], 1), queryMaximumMilliseconds = Math.Round(sorted[^1], 1),
            documentMilliseconds = Math.Round(documentMilliseconds, 1),
            questions = Paraphrases.Length, lexicalRecallAt3 = evaluation.Lexical, fusedRecallAt3 = evaluation.Fused
        };
    }

    /// <summary>A loopback stand-in for Ollama's /api/embed: 4-number vectors for a known model, 404 for any other.</summary>
    private sealed class EmbedFixture : IAsyncDisposable
    {
        private readonly LoopbackHttpServer server;

        internal EmbedFixture() => server = new(exchange =>
        {
            LastPath = exchange.Request.Target;
            LastBody = exchange.Request.Text;
            using var sent = JsonDocument.Parse(LastBody);
            var known = sent.RootElement.GetProperty("model").GetString() == "nomic-embed-text";
            var count = sent.RootElement.GetProperty("input").GetArrayLength();
            var (status, answer) = known
                ? (200, JsonSerializer.Serialize(new { model = "nomic-embed-text", embeddings = Enumerable.Range(0, count).Select(i => new[] { 0.1f, 0.2f, 0.3f, i }) }))
                : (404, "{\"error\":\"model \\\"not-downloaded\\\" not found, try pulling it first\"}");
            return exchange.RespondAsync(status, answer);
        });

        internal string Origin => server.Origin;
        internal string? LastPath { get; private set; }
        internal string? LastBody { get; private set; }

        public ValueTask DisposeAsync() => server.DisposeAsync();
    }
}
