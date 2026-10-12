using System.Numerics;
using System.Text;
using Martlet.Core.Search;
using Martlet.Core.Text;

namespace Martlet.Memory;

internal readonly record struct LexicalMatch(Guid FactId, double Score, int MatchedTerms);

/// <summary>How memory searches its facts on the shared search engine (docs/SEARCH.md), for tools that show it.</summary>
public static class MemorySearch
{
    /// <summary>Memory's search profile: one `content` field, the reranker's weights and the penalty selection.</summary>
    public static SearchProfile Profile => LexicalIndex.Profile;
}

/// <summary>The words of a fact or a query as memory compares them: <see cref="SearchTerms"/> (lower case, common English grammar
/// words left out, light stems so "cats" finds "cat" and "running" finds "runs"), without terms that hold no letter or digit
/// (emoji).</summary>
internal static class MemoryTerms
{
    internal static IReadOnlyList<string> Of(string text)
    {
        var terms = SearchTerms.Of(text);
        for (var i = 0; i < terms.Count; i++)
            if (!HasLetterOrDigit(terms[i]))
                return terms.Where(HasLetterOrDigit).ToArray();
        return terms;
    }

    private static bool HasLetterOrDigit(string term)
    {
        foreach (var rune in term.EnumerateRunes())
            if (Rune.IsLetterOrDigit(rune))
                return true;
        return false;
    }
}

/// <summary>The deterministic in-memory index of one store revision's facts, rebuilt on every change, on the shared search engine
/// (<see cref="SearchIndex"/> and <see cref="SearchRanker"/> in Martlet.Core, docs/SEARCH.md). Retrieval has two stages, both in
/// this process and without a model: BM25 over <see cref="MemoryTerms"/> finds the candidates, then a reranker orders the best of
/// them by how much of the query they cover, how close and in what order the query's words stand in them, and a gentle recency
/// boost, and moves near-duplicates of a better match (of the same person) down. The order never depends on the number of results
/// asked for, so a shorter list is always the start of a longer one.</summary>
internal sealed class LexicalIndex
{
    // The reranker looks at this many of BM25's best candidates (more than the most results a query can ask for); the rest are
    // left out.
    internal const int RerankDepth = 64;

    /// <summary>How memory searches. BM25 (Robertson and Zaragoza, 2009) with its usual parameters over one field, the fact's
    /// content. Each reranker feature multiplies the BM25 score by at most 1 + its weight: coverage is the share of the query's IDF
    /// weight the fact holds; proximity is 1 / the smallest distance between two different query terms in it (Tao and Zhai, SIGIR
    /// 2007); phrases count query word pairs that stand next to each other in the query's order; recency halves every 30 days
    /// before the newest fact of the index (not the clock, so a cached result stays exact). As remembering skips a near-duplicate
    /// (Jaccard similarity of the words at least 0.8), recall counts one at a quarter of its score after a better match of the same
    /// person was chosen: it adds little that the first doesn't already say. The similarity is exact for facts of up to
    /// <see cref="SearchSketch.Size"/> different words and estimated from that many hashed words for longer ones, so the check
    /// costs the same for any fact.</summary>
    internal static SearchProfile Profile { get; } = new()
    {
        Fields = [new("content", 1, 0.75)],
        K1 = 1.2,
        Candidates = RerankDepth,
        Scoring = new()
        {
            Combination = SearchCombination.Multiply, Coverage = 0.5, Proximity = 0.25, Phrases = 0.25, Recency = 0.1,
            RecencyHalfLifeDays = 30
        },
        Diversity = new() { Selection = SearchSelection.Penalty, NearDuplicate = 0.8, Penalty = 0.25 }
    };

    private sealed record Document(Guid Id, DateTimeOffset UpdatedAtUtc, string? VoiceId, SearchSketch Sketch);

    private readonly SearchIndex index;
    private readonly Document[] documents;
    private readonly Dictionary<Guid, int> numbers;
    private readonly DateTimeOffset newest;

    private LexicalIndex(SearchIndex index, Document[] documents)
    {
        this.index = index;
        this.documents = documents;
        numbers = new(documents.Length);
        for (var i = 0; i < documents.Length; i++) numbers[documents[i].Id] = i;
        newest = documents.Length == 0 ? DateTimeOffset.MinValue : documents.Max(document => document.UpdatedAtUtc);
    }

    internal int DocumentCount => documents.Length;
    internal int TermCount => index.TermCount;

    /// <summary>Whether every term of <paramref name="word"/> is in the index (a word stands for its stem).</summary>
    internal bool ContainsTerm(string word) =>
        MemoryTerms.Of(word) is { Count: > 0 } terms && terms.All(term => index.TryGetTerm(term, out _));

    internal static LexicalIndex Build(IEnumerable<MemoryFact> facts)
    {
        var index = new SearchIndex(Profile);
        var documents = new List<Document>();
        foreach (var fact in facts)
        {
            // History is never recalled as current.
            if (fact.IsHistory)
                continue;
            MemoryGuard.Require(fact.Content.Length <= MemoryLimits.MaximumContentCharacters);
            // A fact is found by its words and by its hidden search words, after its content in the same field; near-duplicates
            // compare only what it says.
            var content = MemoryTerms.Of(fact.Content);
            IReadOnlyList<string> terms = fact.SearchWords is { } words ? [.. content, .. MemoryTerms.Of(words)] : content;
            index.Add(terms);
            documents.Add(new(fact.Id, fact.UpdatedAtUtc, fact.VoiceId, SearchSketch.Of(content)));
        }
        return new(index.Freeze(), [.. documents]);
    }

    internal static string[] QueryTerms(string query)
    {
        MemoryGuard.Require(query is { Length: > 0 } && !string.IsNullOrWhiteSpace(query) && !query.Contains('\0'));
        MemoryGuard.Require(query.Length <= MemoryLimits.MaximumQueryCharacters, MemoryFailure.LimitExceeded);
        try
        {
            MemoryGuard.Require(new UTF8Encoding(false, true).GetByteCount(query) <= MemoryLimits.MaximumQueryUtf8Bytes,
                MemoryFailure.LimitExceeded);
        }
        catch (EncoderFallbackException)
        {
            throw new MemoryException(MemoryFailure.InvalidData);
        }
        var terms = MemoryTerms.Of(query)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        MemoryGuard.Require(terms is { Length: > 0 and <= MemoryLimits.MaximumQueryTerms }, MemoryFailure.LimitExceeded);
        return terms;
    }

    /// <summary>A query text within the query bounds made of <paramref name="source"/>'s own words as <see cref="SearchTerms.Words"/>
    /// reads them (the same words in every language search reads), first seen first, one for each new search term, so the query's terms are exactly the terms of those words: a stem is never stemmed again.
    /// Null when no word has a search term.</summary>
    internal static string? TryBoundedQueryText(string source)
    {
        MemoryGuard.Require(source is { Length: > 0 } &&
            source.Length <= MemoryLimits.MaximumContentCharacters &&
            !string.IsNullOrWhiteSpace(source) &&
            !source.Contains('\0'));
        try
        {
            _ = new UTF8Encoding(false, true).GetByteCount(source);
        }
        catch (EncoderFallbackException)
        {
            throw new MemoryException(MemoryFailure.InvalidData);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var text = new StringBuilder();
        var bytes = 0;
        foreach (var word in SearchTerms.Words(source))
        {
            if (seen.Count == MemoryLimits.MaximumQueryTerms)
                break;
            var terms = MemoryTerms.Of(word);
            var added = terms.Where(term => !seen.Contains(term)).Distinct(StringComparer.Ordinal).Count();
            if (added == 0 || seen.Count + added > MemoryLimits.MaximumQueryTerms)
                continue;
            var separator = text.Length == 0 ? 0 : 1;
            var wordBytes = Encoding.UTF8.GetByteCount(word);
            if (text.Length + separator + word.Length > MemoryLimits.MaximumQueryCharacters ||
                bytes + separator + wordBytes > MemoryLimits.MaximumQueryUtf8Bytes)
                continue;
            if (separator == 1)
                text.Append(' ');
            text.Append(word);
            bytes += separator + wordBytes;
            seen.UnionWith(terms);
        }
        return text.Length == 0 ? null : text.ToString();
    }

    /// <summary>The ranked matches for <paramref name="queryTerms"/>: at most <paramref name="maximumResults"/>, best first.
    /// <paramref name="include"/> leaves facts out before ranking (a snapshot's expired facts). <paramref name="similar"/> (facts an
    /// embedding search found, best first) is fused with the lexical order by the engine's reciprocal rank fusion; a fact only it
    /// found matched no term.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    internal LexicalMatch[] Search(string[] queryTerms, int maximumResults, CancellationToken token,
        Func<Guid, bool>? include = null, IReadOnlyList<Guid>? similar = null)
    {
        MemoryGuard.Require(maximumResults is >= 1 and <= MemoryLimits.MaximumResults);
        MemoryGuard.Require(queryTerms.Length <= MemoryLimits.MaximumQueryTerms, MemoryFailure.LimitExceeded);
        if (documents.Length == 0)
            return [];

        var query = SearchQuery.Of(index, queryTerms);
        var rules = new Rules(this, query);
        var candidates = index.Search(query, Profile.Candidates,
            include is null ? null : [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)] (document, _) => include(documents[document].Id),
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)] (left, right) => rules.Tie(left, right),
            ordered: false, token: token);
        List<int>? fuse = null;
        if (similar is { Count: > 0 })
        {
            fuse = new(similar.Count);
            foreach (var id in similar)
                if (numbers.TryGetValue(id, out var number) && (include is null || include(id)))
                    fuse.Add(number);
        }
        var ranking = SearchRanker.Rank(candidates, Profile, rules, maximumResults, fuse, token);
        var matches = new LexicalMatch[ranking.Hits.Length];
        for (var i = 0; i < matches.Length; i++)
        {
            var hit = ranking.Hits[i];
            matches[i] = new(documents[hit.Document].Id, hit.Score, BitOperations.PopCount((uint)hit.Mask));
        }
        return matches;
    }

    /// <summary>One query's reranker features: coverage, proximity, phrases and recency; equal scores go to more matched query
    /// terms, then the newer fact, then the fact ID; near-duplicates are facts of the same person (or of no one in particular).</summary>
    private sealed class Rules : ISearchRules
    {
        private readonly LexicalIndex owner;
        private readonly SearchQuery query;
        private readonly double[] idf;
        private readonly double queryWeight;
        private readonly List<int> occurrences = [];

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        internal Rules(LexicalIndex owner, SearchQuery query)
        {
            this.owner = owner;
            this.query = query;
            idf = new double[query.Terms.Count];
            for (var q = 0; q < idf.Length; q++)
            {
                if (query.Terms[q].Term < 0)
                    continue;
                idf[q] = owner.index.Idf(query.Terms[q].Term);
                queryWeight += idf[q];
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public bool Score(in SearchCandidate candidate, ref SearchFeatures features)
        {
            var (proximity, phrases) = BitOperations.PopCount((uint)candidate.Mask) < 2
                ? (0d, 0)
                : SearchMeasures.ClosestPair(owner.index.Terms(candidate.Document, 0), query, candidate.Mask, occurrences);
            features.Coverage = queryWeight > 0 ? SearchMeasures.Weight(candidate.Mask, idf) / queryWeight : 0;
            features.Proximity = proximity;
            features.Phrases = 1 - Math.Pow(0.5, phrases);
            features.Recency = SearchMeasures.Recency((owner.newest - owner.documents[candidate.Document].UpdatedAtUtc).TotalDays,
                Profile.Scoring.RecencyHalfLifeDays);
            return true;
        }

        /// <summary>More matched query terms, then the newer fact, then the fact ID.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public int Tie(in SearchCandidate left, in SearchCandidate right)
        {
            var order = BitOperations.PopCount((uint)right.Mask).CompareTo(BitOperations.PopCount((uint)left.Mask));
            Document a = owner.documents[left.Document], b = owner.documents[right.Document];
            if (order == 0)
                order = b.UpdatedAtUtc.CompareTo(a.UpdatedAtUtc);
            return order != 0 ? order : a.Id.CompareTo(b.Id);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public double Similarity(int left, int right)
        {
            Document a = owner.documents[left], b = owner.documents[right];
            if (a.VoiceId is not null && b.VoiceId is not null && !string.Equals(a.VoiceId, b.VoiceId, StringComparison.Ordinal))
                return 0;
            return a.Sketch.Similarity(b.Sketch, Profile.Diversity.NearDuplicate);
        }
    }
}
