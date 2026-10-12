using System.Diagnostics;
using Martlet.Conversation;
using Martlet.Conversation.Guides;
using Martlet.Core.Search;
using Martlet.Memory;

namespace Martlet.Mcp;

/// <summary>search_engine_check: the shared search engine that memory, app guides and past conversations use (docs/SEARCH.md).
/// It shows each source's search profile and checks the engine's parts on synthetic documents: the first stage's cut and filter,
/// BM25F field weights, a second field for extra words, an index that grows while it is searched, reciprocal rank fusion with a
/// second list, and the time of a first stage and ranking on a large index. The sources' own checks (memory_recall_check,
/// app_guides_check, conversation_history_check) rehearse their ranking end to end. Nothing is read or written; nothing leaves
/// this PC.</summary>
internal static class SearchEngineCheck
{
    private sealed class PlainRules : ISearchRules
    {
        public bool Score(in SearchCandidate candidate, ref SearchFeatures features) => true;
    }

    internal static object Run(CancellationToken cancellation)
    {
        var steps = new List<object>();
        var failures = new List<string>();
        void Step(string name, bool passed, object detail)
        {
            steps.Add(new { name, passed, detail });
            if (!passed) failures.Add(name);
        }

        var profiles = new
        {
            memory = Describe(MemorySearch.Profile),
            appGuides = Describe(GuideIndex.Profile),
            pastConversationsRecall = Describe(ConversationHistory.RecallProfile),
            pastConversationsFind = Describe(ConversationHistory.FindProfile)
        };
        SearchProfile[] all = [MemorySearch.Profile, GuideIndex.Profile, ConversationHistory.RecallProfile, ConversationHistory.FindProfile];
        Step("each source has its search profile (fields, candidates, scoring and selection)",
            all.All(p => p.Fields.Count > 0 && p.Candidates > 0), new { sources = all.Length });

        var random = new Random(7);
        var words = Enumerable.Range(0, 300).Select(i => "w" + i.ToString("x")).ToArray();
        string[] Words(int count) => [.. Enumerable.Range(0, count).Select(_ => words[(int)(words.Length * Math.Pow(random.NextDouble(), 2))])];

        // The first stage: the best `depth` (exactly the head of a full sort), best first, with a filter.
        var memory = new SearchIndex(MemorySearch.Profile);
        for (var i = 0; i < 2_000; i++) memory.Add(Words(4 + random.Next(12)));
        memory.Freeze();
        var query = SearchQuery.Of(memory, [words[0], words[3], words[9]]);
        var depth = MemorySearch.Profile.Candidates;
        var full = memory.Search(query, int.MaxValue, (document, _) => document % 4 != 0);
        var cut = memory.Search(query, depth, (document, _) => document % 4 != 0);
        Step($"the first stage keeps the best {depth}, best first, and leaves out what a filter refuses",
            cut.Length == Math.Min(depth, full.Length) && cut.SequenceEqual(full.Take(cut.Length)) && cut.All(c => c.Document % 4 != 0) &&
            cut.Zip(cut.Skip(1)).All(pair => pair.First.Score >= pair.Second.Score),
            new { documents = memory.Count, matched = full.Length, kept = cut.Length });

        // BM25F: a word in a page title counts more than the same word in the text.
        var guide = new SearchIndex(GuideIndex.Profile);
        guide.Add(["iron", "ore"], ["location"], ["found", "in", "cave"]);
        guide.Add(["cave"], ["location"], ["iron", "ore", "found", "here"]);
        guide.Freeze();
        var titled = guide.Search(SearchQuery.Of(guide, ["iron", "ore"]), 5);
        Step("BM25F weighs fields: the question's words in a page title rank above the same words in the text",
            titled.Length == 2 && titled[0].Document == 0, new { order = titled.Select(c => c.Document), scores = titled.Select(c => Math.Round(c.Score, 4)) });

        // A second field: extra words a document doesn't say (a memory fact's search words) find it.
        var extra = new SearchIndex(MemorySearch.Profile with { Fields = [new("content"), new("search words", 0.5)] });
        extra.Add(["user", "drink", "oat", "milk"], ["dairy", "free", "vegan"]);
        extra.Add(["user", "garden", "basil"], []);
        extra.Freeze();
        var vegan = extra.Search(SearchQuery.Of(extra, ["vegan"]), 5);
        Step("a second field finds a document by words it doesn't say (search words)", vegan.Length == 1 && vegan[0].Document == 0,
            new { found = vegan.Length });

        // An index without positions grows while it is searched (the record of conversations).
        var record = new SearchIndex(ConversationHistory.FindProfile);
        record.Add(["trip", "lisbon", "may"]);
        var before = record.Search(SearchQuery.Of(record, ["lisbon"]), 10).Length;
        record.Add(["hotel", "lisbon", "rooftop"]);
        var after = record.Search(SearchQuery.Of(record, ["lisbon"]), 10).Length;
        Step("an index without positions grows while it is searched (past conversations)", before == 1 && after == 2,
            new { before, after });

        // Reciprocal rank fusion: a second ranked list (a future embedding search) joins the lexical order.
        var candidates = new[] { new SearchCandidate(1, 5, 1, 0), new SearchCandidate(2, 4, 1, 1), new SearchCandidate(3, 3, 1, 2) };
        var plain = new SearchProfile { Fields = [new("text")], Scoring = new() { FirstStage = 1 } };
        var fused = SearchRanker.Rank(candidates, plain, new PlainRules(), 4, fuse: [3, 9]);
        Step("reciprocal rank fusion: a document both lists have comes first, and one only the second list has joins",
            fused.Hits.Select(h => h.Document).SequenceEqual([3, 1, 2, 9]), new { order = fused.Hits.Select(h => h.Document) });

        // Time: the first stage and the ranking on a large index with memory's profile (every query word in many documents).
        var large = new SearchIndex(MemorySearch.Profile);
        for (var i = 0; i < 100_000; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            large.Add(Words(6 + random.Next(10)));
        }
        large.Freeze();
        var rules = new PlainRules();
        var timings = new List<double>();
        for (var round = 0; round < 2; round++)
            for (var q = 0; q < 100; q++)
            {
                var terms = Words(3 + random.Next(6)).Distinct().ToArray();
                var watch = Stopwatch.StartNew();
                var found = large.Search(SearchQuery.Of(large, terms), MemorySearch.Profile.Candidates, ordered: false);
                SearchRanker.Rank(found, MemorySearch.Profile, rules, 12);
                watch.Stop();
                if (round == 1) timings.Add(watch.Elapsed.TotalMicroseconds);
            }
        timings.Sort();
        var median = timings[timings.Count / 2];
        Step("100,000 documents: the first stage and the ranking of a query stay within a few milliseconds", median < 10_000,
            new { documents = large.Count, queries = timings.Count, medianMicroseconds = Math.Round(median),
                p95Microseconds = Math.Round(timings[timings.Count * 95 / 100]) });

        return new
        {
            passed = failures.Count == 0, failures, steps, profiles,
            note = "Synthetic documents with the production search engine (Martlet.Core.Search) and each source's own profile. " +
                "memory_recall_check, app_guides_check and conversation_history_check rehearse each source's ranking end to end."
        };
    }

    private static object Describe(SearchProfile profile) => new
    {
        fields = profile.Fields.Select(f => new { name = f.Name, weight = f.Weight, lengthNormalization = f.LengthNormalization }),
        k1 = profile.K1,
        positions = profile.Positions,
        candidates = profile.Candidates,
        refined = profile.Refined,
        scoring = new
        {
            combination = profile.Scoring.Combination.ToString(), firstStage = profile.Scoring.FirstStage, relevance = profile.Scoring.Relevance,
            coverage = profile.Scoring.Coverage, proximity = profile.Scoring.Proximity, phrases = profile.Scoring.Phrases,
            recency = profile.Scoring.Recency, recencyHalfLifeDays = Math.Round(profile.Scoring.RecencyHalfLifeDays, 2), time = profile.Scoring.Time
        },
        cutoff = profile.Cutoff,
        diversity = new
        {
            selection = profile.Diversity.Selection.ToString(), nearDuplicate = profile.Diversity.NearDuplicate, penalty = profile.Diversity.Penalty,
            weight = profile.Diversity.Weight, sameGroup = profile.Diversity.SameGroup,
            perGroup = profile.Diversity.PerGroup == int.MaxValue ? (int?)null : profile.Diversity.PerGroup,
            pool = profile.Diversity.Pool == int.MaxValue ? (int?)null : profile.Diversity.Pool, poolPerHit = profile.Diversity.PoolPerHit
        }
    };
}
