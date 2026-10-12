using Martlet.Core.Search;

namespace Martlet.Core.Tests;

/// <summary>The shared search engine (docs/SEARCH.md): the index and its first stage, the ranker's scoring, cutoff, selection and
/// fusion, and the measures. Each source's own evaluations (memory, app guides, past conversations) cover how well it ranks.</summary>
public sealed class SearchEngineTests
{
    private static readonly SearchProfile OneField = new() { Fields = [new("text")] };

    private static SearchIndex Index(SearchProfile profile, params string[] documents)
    {
        var index = new SearchIndex(profile);
        foreach (var document in documents) index.Add(document.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return index;
    }

    private sealed class Rules(Func<SearchCandidate, SearchFeatures, SearchFeatures>? score = null) : ISearchRules
    {
        public Func<int, int, double> Similar { get; init; } = (_, _) => 0;
        public Func<int, int> Groups { get; init; } = _ => -1;
        public Func<int, bool> Skipped { get; init; } = _ => false;
        public Action<SearchCandidate, SearchFeatures>? Refined { get; init; }
        public int RefineCalls { get; private set; }

        public bool Score(in SearchCandidate candidate, ref SearchFeatures features)
        {
            if (score is not null) features = score(candidate, features);
            return features.Relevance >= 0;
        }

        public void Refine(in SearchCandidate candidate, ref SearchFeatures features)
        {
            RefineCalls++;
            features.Proximity = 1;
        }

        public double Similarity(int left, int right) => Similar(left, right);
        public int Group(int document) => Groups(document);
        public bool Skip(int document) => Skipped(document);
    }

    [Fact]
    public void OneFieldIsPlainBm25AndTheBestComeFirst()
    {
        var index = Index(OneField, "cat sleeps", "cat cat dog", "dog bird fish fish fish", "bird");
        var query = SearchQuery.Of(index, ["cat", "dog", "lion"]);

        var found = index.Search(query, 10);

        Assert.Equal([1, 0, 2], found.Select(c => c.Document));
        Assert.Equal([0b11, 0b01, 0b10], found.Select(c => c.Mask));
        Assert.Equal(-1, query.Terms[2].Term);
        // BM25 by hand for "cat" in document 0: two of four documents hold it, the document is shorter than the average.
        var idf = Math.Log(1 + (4 - 2 + 0.5) / (2 + 0.5));
        var average = (2 + 3 + 5 + 1) / 4.0;
        Assert.Equal(idf * 1 * 2.2 / (1 + 1.2 * (1 - 0.75 + 0.75 * 2 / average)), found[1].Score, 12);
        Assert.Equal(Math.Log(1 + 4.5 / 0.5), index.Idf(-1), 12);
    }

    [Fact]
    public void TheCutKeepsExactlyWhatAFullSortKeepsAndFiltersComeFirst()
    {
        var random = new Random(3);
        var words = Enumerable.Range(0, 30).Select(i => "w" + i).ToArray();
        var documents = Enumerable.Range(0, 400)
            .Select(_ => string.Join(' ', Enumerable.Range(0, random.Next(1, 12)).Select(_ => words[random.Next(words.Length)]))).ToArray();
        var index = Index(OneField, documents);
        var query = SearchQuery.Of(index, ["w1", "w2", "w3"]);
        Comparison<SearchCandidate> newerFirst = (left, right) => right.Document.CompareTo(left.Document);

        var all = index.Search(query, int.MaxValue, (document, _) => document % 3 != 0, newerFirst);
        var best = index.Search(query, 25, (document, _) => document % 3 != 0, newerFirst);

        Assert.Equal(all.Take(25), best);
        Assert.DoesNotContain(all, c => c.Document % 3 == 0);
        Assert.True(all.Zip(all.Skip(1)).All(p => p.First.Score > p.Second.Score ||
            p.First.Score == p.Second.Score && p.First.Document > p.Second.Document));
    }

    [Fact]
    public void SeveralFieldsAreWeightedBeforeOneSaturationAndNeedAFrozenIndex()
    {
        var profile = new SearchProfile { Fields = [new("title", 3, 0), new("text", 1, 0)] };
        var index = new SearchIndex(profile);
        index.Add(["iron"], ["ore", "is", "here"]);
        index.Add(["copper"], ["iron", "ore", "is", "here"]);
        var query = SearchQuery.Of(index, ["iron"]);

        Assert.Throws<InvalidOperationException>(() => index.Search(query, 5));
        index.Freeze();
        Assert.Throws<InvalidOperationException>(() => index.Add(["tin"], ["ore"]));
        var found = index.Search(query, 5);

        Assert.Equal([0, 1], found.Select(c => c.Document));
        var idf = index.Idf(query.Terms[0].Term);
        Assert.Equal(idf * 3 * 2.2 / (3 + 1.2), found[0].Score, 6);
        Assert.Equal(idf * 1 * 2.2 / (1 + 1.2), found[1].Score, 6);
    }

    [Fact]
    public void ASecondFieldIndexesExtraWordsForADocument()
    {
        // A memory fact's search words: words the fact doesn't say, found through a lighter second field.
        var profile = new SearchProfile { Fields = [new("content"), new("search words", 0.5)] };
        var index = new SearchIndex(profile);
        index.Add(["user", "drink", "oat", "milk"], ["dairy", "free", "vegan"]);
        index.Add(["user", "vegan", "cookbook"], []);
        index.Freeze();

        var found = index.Search(SearchQuery.Of(index, ["vegan"]), 5);

        Assert.Equal([1, 0], found.Select(c => c.Document));
        Assert.Equal(["user", "drink", "oat", "milk"], index.Terms(0, 0).Sequence.Select(id => index.Vocabulary.First(v => v.Value == id).Key));
        Assert.Equal(3, index.Length(0, 1));
    }

    [Fact]
    public void AnIndexWithoutPositionsGrowsAndAnnotateSeesEveryMatch()
    {
        var index = new SearchIndex(OneField with { Positions = false });
        index.Add(["trip", "lisbon"]);
        index.Add(["hotel", "rooftop"]);
        index.Add(["lisbon", "food"]);
        Assert.Throws<InvalidOperationException>(() => index.Terms(0, 0));
        var query = SearchQuery.Of(index, ["lisbon", "hotel"]);
        var masks = new Dictionary<int, int>();

        var found = index.Search(query, 1, (document, _) => document != 0,
            annotate: (candidates, matched) => { for (var d = 0; d < 4; d++) masks[d] = matched(d); });

        Assert.Single(found);
        Assert.Equal(new Dictionary<int, int> { [0] = 1, [1] = 2, [2] = 1, [3] = 0 }, masks);
        index.Add(["lisbon"]);
        Assert.Equal(4, index.Count);
        Assert.Equal(3, index.DocumentFrequency(query.Terms[0].Term));
    }

    [Fact]
    public void MultiplyAndAddCombineTheFeatures()
    {
        var features = new SearchFeatures
        {
            FirstStage = 4, FirstStageShare = 0.5, Relevance = 0.8, Coverage = 1, Proximity = 0.5, Phrases = 0.5, Recency = 1, Time = 1,
            Extra = 0.01
        };
        var multiply = new SearchScoring { Combination = SearchCombination.Multiply, Coverage = 0.5, Proximity = 0.25, Phrases = 0.25, Recency = 0.1 };
        var add = new SearchScoring { FirstStage = 0.2, Relevance = 0.3, Proximity = 0.1, Time = 1 };

        Assert.Equal(4 * 1.5 * 1.125 * 1.125 * 1.1 + 0.01, multiply.Combine(features), 12);
        Assert.Equal(0.2 * 0.5 + 0.3 * 0.8 + 0.1 * 0.5 + 1 + 0.01, add.Combine(features), 12);
    }

    [Fact]
    public void RefineReadsOnlyTheBestAndTheCutoffLeavesOutTheWeak()
    {
        var candidates = Enumerable.Range(0, 10).Select(i => new SearchCandidate(i, 10 - i, 1, i)).ToArray();
        var rules = new Rules();
        var profile = new SearchProfile
        {
            Fields = [new("text")], Refined = 3, Cutoff = 0.5, Scoring = new() { FirstStage = 1, Proximity = 0.1 }
        };

        var ranking = SearchRanker.Rank(candidates, profile, rules, 20);

        Assert.Equal(3, rules.RefineCalls);
        Assert.Equal([0, 1, 2, 3, 4], ranking.Ranked.Select(h => h.Document));
        Assert.Equal(1.1, ranking.Hits[0].Score, 12);
        Assert.Equal(0.9 + 0.1, ranking.Hits[1].Score, 12);
    }

    [Fact]
    public void PenaltySelectionMovesNearDuplicatesDownAndShortListsStartLongOnes()
    {
        var candidates = new[] { new SearchCandidate(0, 10, 1, 0), new SearchCandidate(1, 9, 1, 1), new SearchCandidate(2, 5, 1, 2) };
        var rules = new Rules { Similar = (a, b) => a + b == 1 ? 0.9 : 0 };
        var profile = new SearchProfile
        {
            Fields = [new("text")], Scoring = new() { FirstStage = 10 },
            Diversity = new() { Selection = SearchSelection.Penalty, Penalty = 0.25 }
        };

        var full = SearchRanker.Rank(candidates, profile, rules, 3);

        Assert.Equal([0, 2, 1], full.Hits.Select(h => h.Document));
        Assert.Equal([10, 5, 2.25], full.Hits.Select(h => h.Score));
        Assert.Equal(full.Hits.Take(2), SearchRanker.Rank(candidates, profile, rules, 2).Hits);
    }

    [Fact]
    public void MaximalMarginalRelevanceSkipsNearDuplicatesCapsGroupsAndHonoursSkips()
    {
        // Documents 0-3 are one page, 4 another; 1 is a near-duplicate of 0; 5 is already shown elsewhere and 6 nearly repeats it.
        var candidates = Enumerable.Range(0, 7).Select(i => new SearchCandidate(i, 10 - i, 1, i)).ToArray();
        var rules = new Rules
        {
            Similar = (a, b) => (Math.Min(a, b), Math.Max(a, b)) is (0, 1) or (5, 6) ? 0.9 : 0,
            Groups = d => d <= 3 ? 0 : 1,
            Skipped = d => d == 5
        };
        var profile = new SearchProfile
        {
            Fields = [new("text")], Scoring = new() { FirstStage = 1 },
            Diversity = new() { Selection = SearchSelection.Mmr, Weight = 0.25, SameGroup = 0.35, NearDuplicate = 0.8, PerGroup = 2 }
        };

        var ranking = SearchRanker.Rank(candidates, profile, rules, 5);

        Assert.Equal([0, 2, 4, 3], ranking.Hits.Select(h => h.Document));
        Assert.Contains(5, ranking.Seen);
        Assert.DoesNotContain(1, ranking.Seen);
        Assert.DoesNotContain(6, ranking.Hits.Select(h => h.Document));
    }

    [Fact]
    public void ReciprocalRankFusionMergesASecondList()
    {
        var fused = SearchFusion.Reciprocal([[1, 2, 3], [3, 4]]);
        Assert.Equal(1.0 / 63 + 1.0 / 61, fused[3], 12);
        Assert.Equal(1.0 / 61, fused[1], 12);
        Assert.Equal([3, 1, 2, 4], SearchFusion.Fuse([[1, 2, 3], [3, 4]]));

        var candidates = new[] { new SearchCandidate(1, 5, 1, 0), new SearchCandidate(2, 4, 1, 1), new SearchCandidate(3, 3, 1, 2) };
        var profile = new SearchProfile { Fields = [new("text")], Scoring = new() { FirstStage = 1 } };
        var ranking = SearchRanker.Rank(candidates, profile, new Rules(), 4, fuse: [3, 9]);
        Assert.Equal([3, 1, 2, 9], ranking.Hits.Select(h => h.Document));
    }

    [Fact]
    public void TheMeasuresSeeCloseWordsPhrasesTimeAndSimilarity()
    {
        var index = Index(OneField, "the oat milk goes in coffee", "milk with oat porridge and more oat");
        var query = SearchQuery.Of(index, ["oat", "milk"]);
        var occurrences = new List<int>();

        Assert.Equal((1.0, 1), SearchMeasures.ClosestPair(index.Terms(0, 0), query, 0b11, occurrences));
        Assert.Equal((0.0, 0), SearchMeasures.ClosestPair(index.Terms(0, 0), query, 0b01, occurrences));
        Assert.Equal((0.5, 0), SearchMeasures.ClosestPair(index.Terms(1, 0), query, 0b11, occurrences));
        int[][] concepts = [[query.Terms[0].Term], [query.Terms[1].Term]];
        Assert.Equal(1.0, SearchMeasures.MinimumCover(index.Terms(0, 0), concepts, 0b11), 12);
        Assert.Equal(1 / (1 + 1 / 4.0), SearchMeasures.MinimumCover(index.Terms(1, 0), concepts, 0b11), 12);
        Assert.True(SearchMeasures.Adjacent(index.Terms(0, 0), concepts[0], concepts[1]));
        Assert.False(SearchMeasures.Adjacent(index.Terms(1, 0), concepts[0], concepts[1]));

        Assert.Equal(1.0, SearchMeasures.Stretch([0, 1, -1], 2), 12);
        Assert.Equal(2.0 / 3, SearchMeasures.Stretch([0, -1, 1], 2), 12);
        Assert.True(SearchMeasures.Adjacent([-1, 0, 1], 0, 1));
        Assert.Equal(0.25, SearchMeasures.Recency(60, 30), 12);
        Assert.Equal(1.0, SearchMeasures.Recency(-5, 30), 12);
        var day = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(1.0, SearchMeasures.TimeCloseness(day.AddHours(5), day, day.AddDays(1), TimeSpan.FromDays(1)), 12);
        Assert.Equal(0.5, SearchMeasures.TimeCloseness(day.AddDays(1.5), day, day.AddDays(1), TimeSpan.FromDays(1)), 12);
        Assert.Equal(0.5, SearchMeasures.Weight(0b101, [0.25, 9, 0.25]), 12);
        Assert.Equal(1.0 / 3, SearchMeasures.Jaccard(new[] { 1, 2 }, new[] { 2, 3 }), 12);
        Assert.Equal(1.0 / 3, SearchMeasures.Jaccard(new HashSet<string> { "a", "b" }, ["b", "c"]), 12);
    }

    [Fact]
    public void SketchesAreExactForShortDocumentsAndRuleOutDifferentOnes()
    {
        var tea = SearchSketch.Of(["user", "like", "green", "tea"]);
        var same = SearchSketch.Of(["green", "tea", "like", "user", "tea"]);
        var other = SearchSketch.Of(["tea", "brew", "80", "degree"]);
        Assert.Equal(4, same.Words);
        Assert.Equal(1.0, tea.Similarity(same, 0.8), 12);
        Assert.Equal(0, tea.Similarity(other, 0.8));
        var words = Enumerable.Range(0, 80).Select(i => $"word{i}").ToArray();
        var longFact = SearchSketch.Of(words);
        Assert.True(longFact.Similarity(SearchSketch.Of(words.Take(78).Append("extra").Append("more")), 0.8) >= 0.8);
        Assert.True(longFact.Similarity(SearchSketch.Of(words.Take(40).Concat(Enumerable.Range(0, 40).Select(i => $"other{i}"))), 0.8) < 0.8);
    }
}
