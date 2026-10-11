using System.Diagnostics;
using Xunit.Abstractions;

namespace Martlet.Memory.Tests;

/// <summary>The two retrieval stages on hand-made facts: BM25 over stems, then the reranker (coverage, proximity, phrases,
/// recency and near-duplicates).</summary>
public sealed class RerankingTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static MemoryFact Fact(string content, int day = 0, string? voiceId = null)
    {
        var at = Start.AddDays(day);
        var provenance = MemoryProvenance.UserEntry(Guid.NewGuid(), at);
        return new()
        {
            Id = Guid.NewGuid(),
            Revision = 1,
            Content = content,
            CreatedAtUtc = at,
            UpdatedAtUtc = at,
            CreatedFrom = provenance,
            LastModifiedBy = provenance,
            Retention = MemoryRetention.UntilDeleted(),
            VoiceId = voiceId
        };
    }

    private static LexicalMatch[] Search(IEnumerable<MemoryFact> facts, string source, int maximum = MemoryLimits.MaximumResults)
    {
        var query = Assert.IsType<MemoryQuery>(MemoryQuery.TryFromBoundedSource(source, maximum));
        return LexicalIndex.Build(facts).Search(LexicalIndex.QueryTerms(query.Text), query.MaximumResults, CancellationToken.None);
    }

    [Fact]
    public void OtherFormsOfAWordMatchAndGrammarWordsAreLeftOut()
    {
        var cats = Fact("The user's cats love sleeping on the radiator.");
        var tea = Fact("Tea is taken with oat milk.");

        Assert.Equal(cats.Id, Assert.Single(Search([cats, tea], "Where does my cat sleep?")).FactId);
        Assert.Equal(new[] { "cat", "sleep" }, LexicalIndex.QueryTerms("Where does my cat sleep?"));
        Assert.Null(MemoryQuery.TryFromBoundedSource("Is it that you were?"));
        Assert.Null(MemoryQuery.TryFromBoundedSource("😀 🎉"));
        MemoryFixtures.Failure(MemoryFailure.LimitExceeded, () => LexicalIndex.QueryTerms("what is it"));
    }

    [Theory]
    [InlineData("I was speeding again and got a ticket", "speed")]
    [InlineData("We keep making pancakes", "mak")]
    [InlineData("The berries and boxes were running late", "berry")]
    public void QueryTextFromConversationKeepsTheWordsSoStemsAreNeverStemmedTwice(string source, string stem)
    {
        var query = Assert.IsType<MemoryQuery>(MemoryQuery.TryFromBoundedSource(source));

        Assert.Equal(MemoryTerms.Of(source).Distinct(StringComparer.Ordinal), LexicalIndex.QueryTerms(query.Text));
        Assert.Contains(stem, LexicalIndex.QueryTerms(query.Text));
    }

    [Fact]
    public void QueryWordsNextToEachOtherAndInOrderRankFirst()
    {
        var phrase = Fact("Oat milk goes in the morning coffee.");
        var apart = Fact("Milk goes with the oat porridge.");

        var hits = Search([apart, phrase], "Do we have oat milk?");

        Assert.Equal(new[] { phrase.Id, apart.Id }, hits.Select(hit => hit.FactId));
        Assert.All(hits, hit => Assert.Equal(2, hit.MatchedTerms));
    }

    [Fact]
    public void FactsCoveringMoreOfTheQueryRankFirst()
    {
        var both = Fact("Ana's birthday is on March 3 and she likes chocolate cake.");
        var one = Fact("Birthday cards are kept in the desk.");

        var hits = Search([one, both], "When is Ana's birthday?");

        Assert.Equal(both.Id, hits[0].FactId);
        Assert.Equal(2, hits[0].MatchedTerms);
    }

    [Fact]
    public void NearDuplicatesOfTheSamePersonMoveBelowOtherMatches()
    {
        var first = Fact("The user likes green tea.", day: 0);
        var copy = Fact("User likes green tea!", day: 1);
        var other = Fact("Green tea is brewed at 80 degrees.", day: 0);

        var hits = Search([first, copy, other], "green tea");

        Assert.Equal(new[] { copy.Id, other.Id, first.Id }, hits.Select(hit => hit.FactId));
        Assert.True(hits[0].Score > hits[1].Score && hits[1].Score > hits[2].Score);

        // The same words for two people are two facts: neither moves down.
        var sams = Fact("The user likes green tea.", day: 0, voiceId: "sam");
        var alexs = Fact("User likes green tea!", day: 1, voiceId: "alex");
        var people = Search([sams, alexs, other], "green tea");
        Assert.Equal(new[] { alexs.Id, sams.Id, other.Id }, people.Select(hit => hit.FactId));

        // A fact about no one in particular covers anyone's same fact.
        var everyones = Fact("User likes green tea!", day: 1);
        Assert.Equal(other.Id, Search([sams, everyones, other], "green tea")[1].FactId);

        // Long facts (more words than the sketch holds) are compared the same way.
        var words = Enumerable.Range(0, 80).Select(i => $"word{i}").ToArray();
        var longFact = Fact("Green tea notes: " + string.Join(' ', words) + ".", day: 1);
        var longCopy = Fact("Green tea notes: " + string.Join(' ', words.Take(78)) + " extra more.", day: 0);
        var halfCopy = Fact("Green tea notes: " + string.Join(' ', words.Take(40)) + " " +
            string.Join(' ', Enumerable.Range(0, 40).Select(i => $"other{i}")) + ".", day: 0);
        var longHits = Search([longFact, longCopy, halfCopy, other], "green tea notes");
        Assert.Equal(longFact.Id, longHits[0].FactId);
        Assert.Equal(longCopy.Id, longHits[^1].FactId);
    }

    [Fact]
    public void RecencyIsAGentleBoost()
    {
        var older = Fact("Favorite color is blue.", day: 0);
        var newer = Fact("Favorite color is green.", day: 60);
        var hits = Search([older, newer], "favorite color");
        Assert.Equal(new[] { newer.Id, older.Id }, hits.Select(hit => hit.FactId));
        Assert.True(hits[0].Score > hits[1].Score);
        Assert.True(hits[0].Score < hits[1].Score * 1.11);

        // A year newer never beats a fact that answers more of the question.
        var answer = Fact("The cat Miso sleeps on the radiator.", day: 0);
        var recent = Fact("The cat eats at noon.", day: 365);
        Assert.Equal(answer.Id, Search([recent, answer], "Where does Miso the cat sleep?")[0].FactId);
    }

    [Fact]
    public void ShorterResultListsAreTheStartOfLongerOnes()
    {
        var facts = Enumerable.Range(0, 40)
            .Select(i => Fact($"Garden note {i % 7}: the tomatoes and the basil need water {(i % 3 == 0 ? "today" : "soon")}.", i % 5))
            .ToArray();
        var index = LexicalIndex.Build(facts);
        var terms = LexicalIndex.QueryTerms("water the garden tomatoes today");

        var full = index.Search(terms, MemoryLimits.MaximumResults, CancellationToken.None);
        Assert.Equal(MemoryLimits.MaximumResults, full.Length);
        for (var k = 1; k <= MemoryLimits.MaximumResults; k++)
            Assert.Equal(full.Take(k), index.Search(terms, k, CancellationToken.None));
        Assert.True(full.Zip(full.Skip(1)).All(pair => pair.First.Score >= pair.Second.Score));
        Assert.Equal(full, LexicalIndex.Build(Enumerable.Reverse(facts)).Search(terms, MemoryLimits.MaximumResults, CancellationToken.None));
    }

    /// <summary>Recall runs on the reply's path: ranking a full store of 512 facts must stay well under a millisecond.</summary>
    [Fact]
    public void RankingAFullStoreStaysWellUnderAMillisecond()
    {
        var random = new Random(7);
        var vocabulary = Enumerable.Range(0, 400).Select(i => Word(random, i)).ToArray();
        string Sentence(int words) => string.Join(' ', Enumerable.Range(0, words).Select(_ => vocabulary[random.Next(vocabulary.Length)]));
        var facts = Enumerable.Range(0, MemoryLimits.MaximumFacts).Select(i => Fact($"The user {Sentence(random.Next(6, 16))}.", i % 90)).ToArray();
        var index = LexicalIndex.Build(facts);
        var queries = Enumerable.Range(0, 200)
            .Select(_ => MemoryQuery.TryFromBoundedSource($"Hey, do you remember what I said about {Sentence(random.Next(4, 20))}?", 12)!)
            .ToArray();

        var timings = new List<double>();
        for (var round = 0; round < 2; round++)
            foreach (var query in queries)
            {
                var timer = Stopwatch.StartNew();
                var hits = index.Search(LexicalIndex.QueryTerms(query.Text), query.MaximumResults, CancellationToken.None);
                timer.Stop();
                if (round == 1)
                    timings.Add(timer.Elapsed.TotalMilliseconds);
                Assert.InRange(hits.Length, 0, query.MaximumResults);
            }
        timings.Sort();
        var median = timings[timings.Count / 2];
        output.WriteLine($"512 facts, {timings.Count} queries: median {median * 1000:0} µs, p95 {timings[timings.Count * 95 / 100] * 1000:0} µs, " +
            $"max {timings[^1] * 1000:0} µs");
        Assert.True(median < 1, $"median {median:0.000} ms");
    }

    private static string Word(Random random, int index)
    {
        const string letters = "abcdefghijklmnopqrstuvwxyz";
        var length = 3 + index % 7;
        return new string(Enumerable.Range(0, length).Select(_ => letters[random.Next(letters.Length)]).ToArray());
    }
}
