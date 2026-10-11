using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Martlet.Memory;

namespace Martlet.Mcp;

/// <summary>memory_recall_check: how recall ranks remembered facts (docs/MEMORY.md, "Lexical retrieval and reranking"). It
/// rehearses the production store and ranking (Martlet.Memory: BM25 over stems, then the reranker) on synthetic facts in a
/// disposable folder, times recall on a full store of 512 facts, and ranks an optional query against the sample facts or facts
/// the caller gives. It never opens the owner's memory. Nothing leaves this PC.</summary>
internal static class MemoryRecallCheck
{
    private static readonly (string Key, string Content)[] SampleFacts =
    [
        ("server-region", "The preferred game server region is Oregon west for lower latency."),
        ("tea", "Tea is taken without sugar and with oat milk."),
        ("raid-time", "The weekly raid starts Friday at 19:00 UTC."),
        ("headset", "The backup headset is the wired USB model."),
        ("basil", "The garden basil needs water every two days."),
        ("cat", "The user's cat is called Miso and loves sleeping on the radiator."),
        ("allergy", "The user is allergic to peanuts and tree nuts."),
        ("running", "The user runs five kilometers every Sunday morning."),
        ("house-plants", "The house plants are watered on Mondays."),
        ("server-backup", "Nightly backups of the home server start at 02:00."),
        ("tomatoes", "The garden tomatoes need staking before the weekend."),
        ("japanese", "The user studies Japanese with flashcards every evening."),
        ("study-music", "The user listens to lo-fi music while studying.")
    ];

    /// <summary>The caller's <c>facts</c> (strings), or null when none were given.</summary>
    internal static IReadOnlyList<string>? Facts(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("facts", out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is 0 or > MemoryLimits.MaximumFacts)
            throw new ArgumentException($"'facts' must be an array of 1-{MemoryLimits.MaximumFacts} strings.");
        return [.. value.EnumerateArray().Select(fact => fact.ValueKind == JsonValueKind.String
            ? fact.GetString()! : throw new ArgumentException("Each fact must be a string."))];
    }

    internal static async Task<object> RunAsync(string? query, IReadOnlyList<string>? callerFacts, CancellationToken cancellation)
    {
        var steps = new List<object>();
        var failures = new List<string>();
        void Step(string name, bool passed, object detail)
        {
            steps.Add(new { name, passed, detail });
            if (!passed) failures.Add(name);
        }
        object Hit(MemoryRetrievalHit hit) => new { fact = hit.Fact.Content, score = Math.Round(hit.Score, 4), matchedTerms = hit.MatchedTerms };
        object? ranked = null;
        var root = Path.Combine(Path.GetTempPath(), "martlet-memory-recall-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            using (var store = Open(Path.Combine(root, "sample")))
            {
                var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
                foreach (var (key, content) in SampleFacts)
                    ids[key] = (await store.SaveAsync(Save(content), cancellation)).Fact.Id;
                async Task<IReadOnlyList<MemoryRetrievalHit>> Recall(string text) =>
                    MemoryQuery.TryFromBoundedSource(text, 5) is { } bounded ? (await store.RetrieveAsync(bounded, cancellation)).Hits : [];
                if (query is not null && callerFacts is null)
                    ranked = (await Recall(query)).Select(Hit).ToArray();

                foreach (var (text, key, what) in new[]
                {
                    ("Do I have any cats?", "cat", "a plural finds the singular"),
                    ("Which nut allergies do I have?", "allergy", "a plural finds the plural of another word form"),
                    ("When do I usually go running?", "running", "a verb form finds another (\"running\" and \"runs\")"),
                    ("What am I studying in the evenings?", "japanese", "the fact holding more of the question's words wins"),
                    ("When is the server backup?", "server-backup", "both words in one fact beat each word alone"),
                    ("What is it that I do on a Sunday?", "running", "grammar words are left out")
                })
                {
                    var hits = await Recall(text);
                    Step($"\"{text}\" recalls \"{SampleFacts.First(f => f.Key == key).Content}\" first: {what}",
                        hits.Count > 0 && hits[0].Fact.Id == ids[key], new { first = hits.Select(Hit).FirstOrDefault(), results = hits.Count });
                }
                Step("a message of only grammar words has no words to look for (recall fills with the newest facts instead)",
                    MemoryQuery.TryFromBoundedSource("Is it that you were?") is null, new { });

                var snapshot = await store.SnapshotAsync(cancellation);
                var bounded = MemoryQuery.TryFromBoundedSource("garden water schedule", 5)!;
                var fromStore = (await store.RetrieveAsync(bounded, cancellation)).Hits;
                var fromSnapshot = snapshot.Search(bounded, DateTimeOffset.UtcNow, cancellation);
                Step("a snapshot of another memory space ranks exactly like the store",
                    fromStore.Select(h => (h.Fact.Id, h.Score)).SequenceEqual(fromSnapshot.Select(h => (h.Fact.Id, h.Score))),
                    new { results = fromStore.Select(Hit) });

                var cat = (await store.InspectAsync(cancellation)).Facts.Single(f => f.Id == ids["cat"]);
                await store.DeleteAsync(new() { Id = cat.Id, ExpectedRevision = cat.Revision, ConsentId = Guid.NewGuid() }, cancellation);
                var afterDelete = await Recall("Do I have any cats?");
                Step("a deleted fact is never recalled again", afterDelete.All(h => h.Fact.Id != cat.Id), new { results = afterDelete.Count });
            }

            using (var store = Open(Path.Combine(root, "rerank")))
            {
                var phrase = (await store.SaveAsync(Save("Oat milk goes in the morning coffee."), cancellation)).Fact.Id;
                var apart = (await store.SaveAsync(Save("Milk goes with the oat porridge."), cancellation)).Fact.Id;
                var hits = (await store.RetrieveAsync(MemoryQuery.TryFromBoundedSource("Do we have oat milk?", 5)!, cancellation)).Hits;
                Step("query words next to each other and in order rank first (\"oat milk\")",
                    hits.Select(h => h.Fact.Id).SequenceEqual([phrase, apart]), new { results = hits.Select(Hit) });

                var now = DateTimeOffset.UtcNow;
                var older = Imported("Favorite color is blue.", now.AddDays(-60), null);
                var newer = Imported("Favorite color is green.", now, null);
                var first = Imported("The user likes green tea.", now.AddDays(-1), null);
                var copy = Imported("User likes green tea!", now, null);
                var other = Imported("Green tea is brewed at 80 degrees.", now.AddDays(-1), null);
                var sams = Imported("Sam likes jasmine tea.", now.AddDays(-1), "sam");
                var alexs = Imported("Sam likes jasmine tea!", now, "alex");
                await store.MergeAsync(new()
                {
                    Facts = [.. new[] { older, newer, first, copy, other, sams, alexs }.Select(MemoryFactJson.Write)],
                    Forget = []
                }, cancellation);
                async Task<IReadOnlyList<MemoryRetrievalHit>> Recall(string text) =>
                    (await store.RetrieveAsync(MemoryQuery.TryFromBoundedSource(text, 10)!, cancellation)).Hits;

                var colors = await Recall("favorite color");
                Step("recency is a gentle boost: of two equal matches the newer comes first, by less than 11%",
                    colors.Count == 2 && colors[0].Fact.Id == newer.Id && colors[0].Score > colors[1].Score && colors[0].Score < colors[1].Score * 1.11,
                    new { results = colors.Select(Hit) });
                var tea = (await Recall("green tea")).Select(h => h.Fact.Id).ToList();
                Step("a near-duplicate of a better match (\"The user likes green tea.\") moves below a different match",
                    tea.FirstOrDefault() == copy.Id && tea.IndexOf(other.Id) >= 0 && tea.IndexOf(first.Id) > tea.IndexOf(other.Id),
                    new { results = (await Recall("green tea")).Select(Hit) });
                var jasmine = await Recall("jasmine tea");
                Step("the same words for two people are two facts: neither moves down",
                    jasmine.Count >= 2 && jasmine.Take(2).Select(h => h.Fact.Id).Order().SequenceEqual(new[] { sams.Id, alexs.Id }.Order()),
                    new { results = jasmine.Select(Hit) });
            }

            using (var store = Open(Path.Combine(root, "full")))
            {
                var random = new Random(7);
                var vocabulary = Enumerable.Range(0, 400).Select(i => Word(random, i)).ToArray();
                string Sentence(int words) => string.Join(' ', Enumerable.Range(0, words).Select(_ => vocabulary[random.Next(vocabulary.Length)]));
                var start = DateTimeOffset.UtcNow.AddDays(-90);
                await store.MergeAsync(new()
                {
                    Facts = [.. Enumerable.Range(0, MemoryLimits.MaximumFacts)
                        .Select(i => MemoryFactJson.Write(Imported($"The user {Sentence(random.Next(6, 16))}.", start.AddHours(i * 4), null)))],
                    Forget = []
                }, cancellation);
                var snapshot = await store.SnapshotAsync(cancellation);
                var queries = Enumerable.Range(0, 200)
                    .Select(_ => MemoryQuery.TryFromBoundedSource($"Do you remember what I said about {Sentence(random.Next(4, 20))}?", 12)!)
                    .ToArray();
                foreach (var warm in queries.Take(20))
                    _ = snapshot.Search(warm, DateTimeOffset.UtcNow, cancellation);
                var timings = new List<double>();
                foreach (var each in queries)
                {
                    var timer = Stopwatch.StartNew();
                    _ = snapshot.Search(each, DateTimeOffset.UtcNow, cancellation);
                    timings.Add(timer.Elapsed.TotalMicroseconds);
                }
                var storeTimings = new List<double>();
                foreach (var each in queries.Skip(20).Take(50))
                {
                    var timer = Stopwatch.StartNew();
                    _ = await store.RetrieveAsync(each, cancellation);
                    storeTimings.Add(timer.Elapsed.TotalMicroseconds);
                }
                timings.Sort();
                storeTimings.Sort();
                var median = timings[timings.Count / 2];
                Step($"{MemoryLimits.MaximumFacts} facts: ranking a query stays well under a millisecond (recall runs on the reply's path)",
                    snapshot.Facts.Count == MemoryLimits.MaximumFacts && median < 1000,
                    new
                    {
                        facts = snapshot.Facts.Count, queries = timings.Count, rankMedianMicroseconds = Math.Round(median),
                        rankP95Microseconds = Math.Round(timings[timings.Count * 95 / 100]), rankMaxMicroseconds = Math.Round(timings[^1]),
                        storeRetrieveMedianMicroseconds = Math.Round(storeTimings[storeTimings.Count / 2])
                    });
            }

            if (callerFacts is not null)
            {
                using var store = Open(Path.Combine(root, "caller"));
                await store.MergeAsync(new()
                {
                    Facts = [.. callerFacts.Select((content, i) =>
                        MemoryFactJson.Write(Imported(content, DateTimeOffset.UtcNow.AddSeconds(i - callerFacts.Count), null)))],
                    Forget = []
                }, cancellation);
                if (query is not null)
                    ranked = MemoryQuery.TryFromBoundedSource(query, MemoryLimits.MaximumResults) is { } bounded
                        ? (await store.RetrieveAsync(bounded, cancellation)).Hits.Select(Hit).ToArray()
                        : Array.Empty<object>();
            }
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return new
        {
            passed = failures.Count == 0,
            failures,
            steps,
            query = query is null ? null : new
            {
                against = callerFacts is null ? "sample facts" : "your facts (later in the list counts as newer)",
                words = query is not null && MemoryQuery.TryFromBoundedSource(query) is { } words ? words.Text : null,
                ranked
            },
            note = "Synthetic facts in a disposable store with the production memory code (Martlet.Memory). It never opens your " +
                "memory; a conversation that recalls your facts is checked by the desktop's tests."
        };
    }

    private static MemoryStore Open(string directory)
    {
        var preview = MemoryStoreActivationPreview.Create(directory);
        return MemoryStore.Open(preview, preview.Authorize(MemoryConsentDecision.Allow));
    }

    private static SaveFactRequest Save(string content) => new()
    {
        Content = content,
        Provenance = MemoryProvenance.UserEntry(Guid.NewGuid(), DateTimeOffset.UtcNow),
        Retention = MemoryRetention.UntilDeleted()
    };

    /// <summary>A fact as another computer would hand it over, with its own time (how the check gives facts different ages).</summary>
    private static MemoryFact Imported(string content, DateTimeOffset at, string? voiceId)
    {
        var utc = at.ToUniversalTime();
        var provenance = MemoryProvenance.UserEntry(Guid.NewGuid(), utc);
        return new()
        {
            Id = Guid.NewGuid(), Revision = 1, Content = content, CreatedAtUtc = utc, UpdatedAtUtc = utc,
            CreatedFrom = provenance, LastModifiedBy = provenance, Retention = MemoryRetention.UntilDeleted(), VoiceId = voiceId
        };
    }

    private static string Word(Random random, int index)
    {
        const string letters = "abcdefghijklmnopqrstuvwxyz";
        return new string(Enumerable.Range(0, 3 + index % 7).Select(_ => letters[random.Next(letters.Length)]).ToArray());
    }
}
