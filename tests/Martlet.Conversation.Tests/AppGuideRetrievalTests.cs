using System.Diagnostics;
using System.Text.Json;
using Martlet.Conversation.Guides;
using Martlet.Core.Text;
using Xunit.Abstractions;

namespace Martlet.Conversation.Tests;

public sealed class AppGuideRetrievalTests(ITestOutputHelper output)
{
    [Fact]
    public void TheRerankedSearchBeatsPlainBm25OnTheHeldOutFixture()
    {
        var fixture = GuideFixture.Load();
        var before = GuideEvaluation.Run(fixture, (chunks, _) => new PlainBm25(chunks));
        var after = GuideEvaluation.Run(fixture, (chunks, name) => GuideIndex.Build(chunks, [name]));
        output.WriteLine("Before (plain BM25 on main):");
        output.WriteLine(before.Report());
        output.WriteLine("After (BM25F + reranker):");
        output.WriteLine(after.Report());
        foreach (var miss in after.Misses) output.WriteLine("Miss: " + miss);

        Assert.True(after.All.MeanReciprocalRank >= before.All.MeanReciprocalRank);
        Assert.True(after.All.SectionMeanReciprocalRank > before.All.SectionMeanReciprocalRank);
        Assert.True(after.HeldOut.SectionMeanReciprocalRank > before.HeldOut.SectionMeanReciprocalRank);
        Assert.True(after.All.RecallAt3 >= before.All.RecallAt3);
        Assert.True(after.HeldOut.SectionRecallAt3 >= 0.85, $"held-out section recall@3 {after.HeldOut.SectionRecallAt3:0.00}");
        Assert.True(after.All.SectionRecallAt3 >= 0.9, $"section recall@3 {after.All.SectionRecallAt3:0.00}");
        // A sure answer reaches the notes: a right section in the first three hits at or above the threshold.
        Assert.True(after.All.Answered >= 0.85, $"answered {after.All.Answered:0.00}");
        Assert.True(after.OffTopicMaximum < GuideRecall.MinimumRelevance, $"off-topic relevance {after.OffTopicMaximum:0.00}");
        Assert.True(after.OffTopicMaximum < before.OffTopicMaximum);
    }

    [Theory]
    [InlineData("what's for dinner")]
    [InlineData("tell me a joke")]
    [InlineData("hey martlet what's up")]
    [InlineData("wtf is this??")]
    [InlineData("I love you martlet")]
    [InlineData("what's the weather like in Seattle")]
    public void OffTopicChatStaysBelowTheNotesThreshold(string chat)
    {
        foreach (var guide in GuideFixture.Load().Guides)
        {
            var hits = GuideIndex.Build(guide.Chunks, [guide.Name]).Search(chat, 5);
            Assert.All(hits, h => Assert.True(h.Relevance < GuideRecall.MinimumRelevance, $"{guide.Name}: {h.Chunk.Page} {h.Relevance:0.00}"));
            Assert.Null(GuideRecall.Notes(guide.Name, hits));
        }
    }

    [Fact]
    public void FillerAndTheAppsNameDoNotLowerRelevance()
    {
        var game = GuideFixture.Load().Guides.Single(g => g.Name == "Emberfall");
        var index = GuideIndex.Build(game.Chunks, [game.Name]);
        var plain = index.Search("where do I find iron ore", 3)[0];
        foreach (var question in new[]
        {
            "hey martlet where tf do I find iron ore?", "um where the hell do I find iron ore in Emberfall please",
            "yo wtf where do I find iron ore lol", "Martlet, where do I find iron ore in emberfall?"
        })
        {
            var hit = index.Search(question, 3)[0];
            Assert.Equal(plain.Chunk, hit.Chunk);
            Assert.Equal(plain.Relevance, hit.Relevance, 6);
        }
        Assert.Equal("Iron Ore › Locations", plain.Chunk.Section);
        Assert.True(plain.Relevance >= GuideRecall.MinimumRelevance);

        var photo = GuideFixture.Load().Guides.Single(g => g.Name == "Photoshop");
        var editor = GuideIndex.Build(photo.Chunks, ["Adobe Photoshop"]);
        var masked = editor.Search("how do I mask a layer in photoshop", 3)[0];
        Assert.Equal("Layer Masks", masked.Chunk.Page);
        Assert.Equal(editor.Search("how do I mask a layer", 3)[0].Relevance, masked.Relevance, 6);
        Assert.True(masked.Relevance >= GuideRecall.MinimumRelevance);
    }

    [Fact]
    public void WordsWrittenTogetherOrApartStillMatch()
    {
        var game = GuideFixture.Load().Guides.Single(g => g.Name == "Emberfall");
        var index = GuideIndex.Build(game.Chunks, [game.Name]);
        Assert.Equal("Fireball", index.Search("how do I cast fire ball", 3)[0].Chunk.Page);
        Assert.Equal("Moonlight Greatsword", index.Search("moonlight great sword", 3)[0].Chunk.Page);

        var apart = GuideIndex.Build([new("https://w/1", "Great Sword", "", "The great sword is a heavy blade found in the old tower.")]);
        var hit = Assert.Single(apart.Search("where is the greatsword", 3));
        Assert.True(hit.Relevance >= GuideRecall.MinimumRelevance);
    }

    [Fact]
    public void NearDuplicatesAreLeftOutAndOnePageDoesNotFillTheNotes()
    {
        const string text = "Iron Ore can be mined in the Northern Caves with any copper pickaxe or better.";
        var chunks = new List<GuideChunk>
        {
            new("https://a.wiki/Iron_Ore", "Iron Ore", "Iron Ore › Locations", text),
            new("https://b.wiki/Iron_Ore", "Iron Ore", "Iron Ore › Locations", text + " Updated."),
            new("https://a.wiki/Iron_Ore", "Iron Ore", "Iron Ore › Mining", "Mining Iron Ore veins gives mining experience; iron ore veins respawn."),
            new("https://a.wiki/Iron_Ore", "Iron Ore", "Iron Ore › Uses", "Smelt iron ore into ingots; iron ore also repairs tools made of iron ore."),
            new("https://a.wiki/Smelter", "Smelter", "Smelter › Recipes", "Iron Ingot: two iron ore and one coal at a smelter.")
        };
        var hits = GuideIndex.Build(chunks).Search("where do I mine iron ore", 5);
        Assert.Single(hits, h => h.Chunk.Text.StartsWith(text, StringComparison.Ordinal));
        var firstThree = hits.Take(3).ToArray();
        Assert.True(firstThree.Count(h => h.Chunk.Url == "https://a.wiki/Iron_Ore") <= 2);
        Assert.Contains(firstThree, h => h.Chunk.Page == "Smelter" || h.Chunk.Url.StartsWith("https://b.", StringComparison.Ordinal));
        Assert.Equal(4, hits.Count);
    }

    [Fact]
    public void SearchesAgreeWhenRunInParallel()
    {
        var fixture = GuideFixture.Load();
        var guide = fixture.Guides[0];
        var index = GuideIndex.Build(guide.Chunks, [guide.Name]);
        var questions = guide.Questions.Select(q => q.Text).ToArray();
        var expected = questions.Select(q => index.Search(q, 5)).ToArray();
        Parallel.For(0, 400, i =>
        {
            var q = i % questions.Length;
            var hits = index.Search(questions[q], 5);
            Assert.Equal(expected[q].Select(h => (h.Chunk, h.Relevance)), hits.Select(h => (h.Chunk, h.Relevance)));
        });
    }

    [Fact]
    public void ASearchOverFiveThousandChunksTakesWellUnderAMillisecond()
    {
        var chunks = GuideEvaluation.Synthetic(5_000, seed: 7);
        var index = GuideIndex.Build(chunks, ["Synthetic Game"]);
        Assert.Equal(5_000, index.Count);
        var random = new Random(11);
        var vocabulary = GuideEvaluation.Words;
        var questions = Enumerable.Range(0, 200).Select(_ =>
            "hey martlet where do I find the " + string.Join(' ', Enumerable.Range(0, 2 + random.Next(4)).Select(_ => vocabulary[random.Next(vocabulary.Length)])) + "?")
            .ToArray();
        foreach (var q in questions.Take(20)) index.Search(q, 5);

        var watch = Stopwatch.StartNew();
        var found = 0;
        foreach (var q in questions) found += index.Search(q, 5).Count;
        watch.Stop();
        var average = watch.Elapsed.TotalMilliseconds / questions.Length;
        output.WriteLine($"5,000 chunks: {average * 1000:0} µs a search on average ({found} hits).");
        Assert.True(found > 0);
        // On the reply's path: the goal is well under 1 ms; the bound is loose so that a busy test machine doesn't fail it.
        Assert.True(average < 5, $"{average:0.000} ms a search");
    }
}

/// <summary>The held-out fixture (Fixtures\app-guides-held-out.json): two guides, questions with what answers them (a page, or a
/// page's section), and off-topic chat.</summary>
internal sealed record GuideFixture(IReadOnlyList<GuideFixture.Guide> Guides, IReadOnlyList<string> OffTopic)
{
    public sealed record Guide(string Name, IReadOnlyList<GuideChunk> Chunks, IReadOnlyList<Question> Questions);
    public sealed record Answer(string Page, string? Section);
    public sealed record Question(string Text, IReadOnlyList<Answer> Expected, bool HeldOut)
    {
        public bool PageMatches(GuideChunk chunk) => Expected.Any(e => e.Page == chunk.Page);

        public bool SectionMatches(GuideChunk chunk) => Expected.Any(e => e.Page == chunk.Page &&
            (e.Section is null || chunk.Section == e.Section || chunk.Section.EndsWith(" › " + e.Section, StringComparison.Ordinal)));
    }

    private static readonly Lazy<GuideFixture> Loaded = new(Read);

    public static GuideFixture Load() => Loaded.Value;

    private static GuideFixture Read()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "app-guides-held-out.json")));
        var guides = new List<Guide>();
        foreach (var guide in document.RootElement.GetProperty("guides").EnumerateArray())
        {
            var pages = guide.GetProperty("pages").EnumerateArray().Select(p => new GuidePage(p.GetProperty("url").GetString()!,
                p.GetProperty("title").GetString()!, string.Join('\n', p.GetProperty("lines").EnumerateArray().Select(l => l.GetString())), 1_000));
            var questions = guide.GetProperty("questions").EnumerateArray().Select(q => new Question(q.GetProperty("text").GetString()!,
                [.. q.GetProperty("expected").EnumerateArray().Select(e => e.GetString()!.Split(" › ", 2)).Select(p => new Answer(p[0], p.Length > 1 ? p[1] : null))],
                q.GetProperty("split").GetString() == "held-out"));
            guides.Add(new(guide.GetProperty("name").GetString()!, [.. pages.SelectMany(p => GuideChunker.Chunk(p))], [.. questions]));
        }
        return new(guides, [.. document.RootElement.GetProperty("offTopic").EnumerateArray().Select(o => o.GetString()!)]);
    }
}

/// <summary>For pages and for sections: recall@3 and mean reciprocal rank (of the first right hit within ten); the share answered
/// (a right section in the first three hits at or above <see cref="GuideRecall.MinimumRelevance"/>, so the notes carry it); and
/// the highest relevance any off-topic chat got.</summary>
internal sealed record GuideEvaluation(GuideEvaluation.Scores All, GuideEvaluation.Scores Tune, GuideEvaluation.Scores HeldOut,
    double OffTopicMaximum, IReadOnlyList<string> Misses)
{
    public sealed record Scores(int Questions, double RecallAt3, double MeanReciprocalRank, double SectionRecallAt3,
        double SectionMeanReciprocalRank, double Answered);

    private sealed record Result(bool HeldOut, int PageRank, int SectionRank, bool Answered);

    public static readonly string[] Words =
    [
        "iron", "ore", "copper", "gold", "silver", "sword", "shield", "bow", "arrow", "potion", "health", "mana", "stamina", "dragon",
        "scale", "wolf", "bear", "giant", "fire", "frost", "cave", "tower", "village", "market", "smith", "forge", "anvil", "ingot",
        "coal", "wood", "stone", "crystal", "lantern", "torch", "quest", "ring", "amulet", "boss", "chest", "key", "door", "bridge",
        "river", "lake", "fish", "trout", "salmon", "bread", "wheat", "farm", "seed", "horse", "saddle", "map", "shrine", "spell",
        "tome", "mage", "armor", "helmet", "boots", "gloves", "cloak", "pickaxe", "axe", "hammer", "skill", "level", "point", "night",
        "day", "rain", "snow", "storm", "peak", "valley", "desert", "temple", "ruin", "crypt", "ghost", "spider", "bat", "rat",
        "snake", "goblin", "orc", "troll", "knight", "king", "queen", "merchant", "inn", "bed", "gem", "ruby", "emerald", "sapphire"
    ];

    public static GuideEvaluation Run(GuideFixture fixture, Func<IReadOnlyList<GuideChunk>, string, IGuideIndex> build)
    {
        var results = new List<Result>();
        var misses = new List<string>();
        var offTopic = 0.0;
        foreach (var guide in fixture.Guides)
        {
            var index = build(guide.Chunks, guide.Name);
            foreach (var question in guide.Questions)
            {
                var hits = index.Search(question.Text, 10);
                var page = 1 + hits.ToList().FindIndex(h => question.PageMatches(h.Chunk));
                var section = 1 + hits.ToList().FindIndex(h => question.SectionMatches(h.Chunk));
                var answered = hits.Take(3).Any(h => question.SectionMatches(h.Chunk) && h.Relevance >= GuideRecall.MinimumRelevance);
                results.Add(new(question.HeldOut, page, section, answered));
                if (section is 0 or > 3 || !answered)
                    misses.Add($"{guide.Name}: \"{question.Text}\" section rank {section}, top " +
                        (hits.Count > 0 ? $"{hits[0].Chunk.Section} {hits[0].Relevance:0.00}" : "none"));
            }
            foreach (var chat in fixture.OffTopic)
            {
                var hits = index.Search(chat, 3);
                if (hits.Count > 0) offTopic = Math.Max(offTopic, hits.Max(h => h.Relevance));
            }
        }
        return new(Score(results), Score([.. results.Where(r => !r.HeldOut)]), Score([.. results.Where(r => r.HeldOut)]), offTopic, misses);
    }

    public string Report() =>
        $"| Questions | page recall@3 | page MRR | section recall@3 | section MRR | answered (≥ {GuideRecall.MinimumRelevance}) |\n" +
        "| --- | --- | --- | --- | --- | --- |\n" +
        Line("All", All) + Line("Tune", Tune) + Line("Held-out", HeldOut) + $"Highest off-topic relevance: {OffTopicMaximum:0.00}\n";

    private static string Line(string name, Scores s) =>
        $"| {name} ({s.Questions}) | {s.RecallAt3:0.00} | {s.MeanReciprocalRank:0.00} | {s.SectionRecallAt3:0.00} | " +
        $"{s.SectionMeanReciprocalRank:0.00} | {s.Answered:0.00} |\n";

    private static Scores Score(IReadOnlyList<Result> results) =>
        results.Count == 0
            ? new(0, 0, 0, 0, 0, 0)
            : new(results.Count, Recall(results.Select(r => r.PageRank)), Reciprocal(results.Select(r => r.PageRank)),
                Recall(results.Select(r => r.SectionRank)), Reciprocal(results.Select(r => r.SectionRank)),
                results.Count(r => r.Answered) / (double)results.Count);

    private static double Recall(IEnumerable<int> ranks) => ranks.Average(r => r is > 0 and <= 3 ? 1.0 : 0);

    private static double Reciprocal(IEnumerable<int> ranks) => ranks.Average(r => r == 0 ? 0 : 1.0 / r);

    /// <summary>A large made-up guide: <paramref name="count"/> chunks of 60 to 180 words drawn with a skewed frequency.</summary>
    public static IReadOnlyList<GuideChunk> Synthetic(int count, int seed)
    {
        var random = new Random(seed);
        var filler = Enumerable.Range(0, 3_000).Select(i => "w" + i.ToString("x")).ToArray();
        var chunks = new List<GuideChunk>(count);
        for (var i = 0; i < count; i++)
        {
            var words = new List<string>();
            var length = 60 + random.Next(120);
            for (var w = 0; w < length; w++)
                words.Add(random.NextDouble() < 0.25 ? Words[(int)(Words.Length * Math.Pow(random.NextDouble(), 2))] : filler[(int)(filler.Length * Math.Pow(random.NextDouble(), 3))]);
            var page = i / 6;
            chunks.Add(new($"https://synthetic.wiki/{page}", Words[page % Words.Length] + " " + Words[(page * 7) % Words.Length],
                "Section " + (i % 6), string.Join(' ', words) + "."));
        }
        return chunks;
    }
}

/// <summary>The first version of the index (main before the reranker): BM25 over title, section and text together, with
/// relevance as the share of the question's terms a chunk has. Kept here only as the evaluation's baseline.</summary>
internal sealed class PlainBm25 : IGuideIndex
{
    private const double K1 = 1.2, B = 0.75;
    private readonly GuideChunk[] chunks;
    private readonly Dictionary<string, int>[] frequencies;
    private readonly int[] lengths;
    private readonly Dictionary<string, int> documentFrequency = new(StringComparer.Ordinal);
    private readonly double averageLength;

    public PlainBm25(IReadOnlyList<GuideChunk> source)
    {
        chunks = [.. source];
        frequencies = new Dictionary<string, int>[chunks.Length];
        lengths = new int[chunks.Length];
        long total = 0;
        for (var i = 0; i < chunks.Length; i++)
        {
            var terms = SearchTerms.Of(chunks[i].Page + "\n" + chunks[i].Section + "\n" + chunks[i].Text);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var term in terms) counts[term] = counts.GetValueOrDefault(term) + 1;
            foreach (var term in counts.Keys) documentFrequency[term] = documentFrequency.GetValueOrDefault(term) + 1;
            frequencies[i] = counts;
            lengths[i] = terms.Count;
            total += terms.Count;
        }
        averageLength = chunks.Length == 0 ? 1 : Math.Max(1, (double)total / chunks.Length);
    }

    public int Count => chunks.Length;

    public IReadOnlyList<GuideHit> Search(string query, int max)
    {
        if (max <= 0 || chunks.Length == 0) return [];
        var terms = SearchTerms.Distinct(query).Where(documentFrequency.ContainsKey).ToArray();
        var asked = SearchTerms.Distinct(query).Count;
        if (terms.Length == 0) return [];
        var hits = new List<GuideHit>();
        for (var i = 0; i < chunks.Length; i++)
        {
            double score = 0;
            var matched = 0;
            foreach (var term in terms)
            {
                if (!frequencies[i].TryGetValue(term, out var tf)) continue;
                matched++;
                var df = documentFrequency[term];
                var idf = Math.Log(1 + (chunks.Length - df + 0.5) / (df + 0.5));
                score += idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * lengths[i] / averageLength));
            }
            if (matched > 0) hits.Add(new(chunks[i], score, (double)matched / Math.Max(1, asked)));
        }
        return hits.OrderByDescending(h => h.Score).Take(max).ToArray();
    }
}
