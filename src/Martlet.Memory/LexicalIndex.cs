using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Martlet.Core.Text;

namespace Martlet.Memory;

internal readonly record struct LexicalMatch(Guid FactId, double Score, int MatchedTerms);

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

/// <summary>The deterministic in-memory index of one store revision's facts, rebuilt on every change. Retrieval has two stages,
/// both in this process and without a model: BM25 over <see cref="MemoryTerms"/> finds the candidates, then a reranker orders the
/// best of them by how much of the query they cover, how close and in what order the query's words stand in them, and a gentle
/// recency boost, and moves near-duplicates of a better match (of the same person) down. The order never depends on the number
/// of results asked for, so a shorter list is always the start of a longer one.</summary>
internal sealed class LexicalIndex
{
    // BM25 (Robertson and Zaragoza, "The Probabilistic Relevance Framework: BM25 and Beyond", 2009) with its usual parameters.
    private const double K1 = 1.2;
    private const double B = 0.75;
    // The reranker looks at this many of BM25's best candidates (more than the most results a query can ask for); the rest are
    // left out.
    internal const int RerankDepth = 64;
    // Each feature multiplies the BM25 score by at most 1 + its weight. Coverage is the share of the query's IDF weight the fact
    // holds; proximity is 1 / the smallest distance between two different query terms in it (Tao and Zhai, SIGIR 2007); phrases
    // count query word pairs that stand next to each other in the query's order; recency halves every 30 days before the newest
    // fact of the index (not the clock, so a cached result stays exact).
    private const double CoverageWeight = 0.5;
    private const double ProximityWeight = 0.25;
    private const double PhraseWeight = 0.25;
    private const double RecencyWeight = 0.1;
    private const double RecencyHalfLifeDays = 30;
    // As remembering skips a near-duplicate (Jaccard similarity of the words at least 0.8), recall counts one at a quarter of
    // its score after a better match of the same person was chosen: it adds little that the first doesn't already say. The
    // similarity is exact for facts of up to SketchSize different words and estimated from that many hashed words (a bottom-k
    // MinHash sketch) for longer ones, so the check costs the same for any fact.
    private const double DuplicateSimilarity = 0.8;
    private const double DuplicatePenalty = 0.25;
    private const int SketchSize = 32;
    // Each fact's words set bits of a 256-bit signature. A bit set in only one of two signatures comes from a word in only one of
    // the facts, so more such bits than a near-duplicate can differ by rules the pair out without comparing sketches.
    private const int SignatureWords = 4;

    private sealed record Document(Guid Id, DateTimeOffset UpdatedAtUtc, string? VoiceId, int Length, int Words, ulong[] Sketch,
        ulong[] Signature);
    private readonly record struct Posting(int Document, int[] Positions);

    private readonly Document[] documents;
    private readonly Dictionary<string, int> termIds;
    private readonly Posting[][] postings;
    private readonly double averageLength;
    private readonly DateTimeOffset newest;

    private LexicalIndex(Document[] documents, Dictionary<string, int> termIds, Posting[][] postings)
    {
        this.documents = documents;
        this.termIds = termIds;
        this.postings = postings;
        averageLength = documents.Length == 0 ? 1 : Math.Max(1, documents.Average(document => document.Length));
        newest = documents.Length == 0 ? DateTimeOffset.MinValue : documents.Max(document => document.UpdatedAtUtc);
    }

    internal int DocumentCount => documents.Length;
    internal int TermCount => termIds.Count;

    /// <summary>Whether every term of <paramref name="word"/> is in the index (a word stands for its stem).</summary>
    internal bool ContainsTerm(string word) =>
        MemoryTerms.Of(word) is { Count: > 0 } terms && terms.All(termIds.ContainsKey);

    internal static LexicalIndex Build(IEnumerable<MemoryFact> facts)
    {
        var termIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var hashes = new List<ulong>();
        var lists = new List<List<Posting>>();
        var documents = new List<Document>();
        var positions = new Dictionary<int, List<int>>();
        foreach (var fact in facts)
        {
            MemoryGuard.Require(fact.Content.Length <= MemoryLimits.MaximumContentCharacters);
            var terms = MemoryTerms.Of(fact.Content);
            positions.Clear();
            for (var i = 0; i < terms.Count; i++)
            {
                if (!termIds.TryGetValue(terms[i], out var id))
                {
                    id = termIds.Count;
                    termIds.Add(terms[i], id);
                    hashes.Add(Hash(terms[i]));
                    lists.Add([]);
                }
                if (!positions.TryGetValue(id, out var at))
                    positions.Add(id, at = []);
                at.Add(i);
            }
            foreach (var (id, at) in positions)
                lists[id].Add(new(documents.Count, [.. at]));
            var sketch = positions.Keys.Select(id => hashes[id]).Order().Take(SketchSize).ToArray();
            var signature = new ulong[SignatureWords];
            foreach (var id in positions.Keys)
                signature[(int)((hashes[id] >> 6) % SignatureWords)] |= 1UL << (int)(hashes[id] & 63);
            documents.Add(new(fact.Id, fact.UpdatedAtUtc, fact.VoiceId, terms.Count, positions.Count, sketch, signature));
        }
        return new([.. documents], termIds, [.. lists.Select(list => list.ToArray())]);
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

    /// <summary>A query text within the query bounds made of <paramref name="source"/>'s own words (lower case), first seen first,
    /// one for each new search term, so the query's terms are exactly the terms of those words: a stem is never stemmed again.
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
        foreach (var word in Words(source))
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
    /// <paramref name="include"/> leaves facts out before ranking (a snapshot's expired facts).</summary>
    internal LexicalMatch[] Search(string[] queryTerms, int maximumResults, CancellationToken token,
        Func<Guid, bool>? include = null)
    {
        MemoryGuard.Require(maximumResults is >= 1 and <= MemoryLimits.MaximumResults);
        MemoryGuard.Require(queryTerms.Length <= MemoryLimits.MaximumQueryTerms, MemoryFailure.LimitExceeded);
        if (documents.Length == 0)
            return [];

        // Stage 1: BM25 over the inverted index. Each fact also keeps a list of its hits (which query term, at which positions)
        // for stage 2.
        var ids = new int[queryTerms.Length];
        var bm25 = new double[documents.Length];
        var covered = new double[documents.Length];
        var matched = new int[documents.Length];
        var hits = new Hits(documents.Length, queryTerms.Sum(term => termIds.TryGetValue(term, out var id) ? postings[id].Length : 0));
        var queryWeight = 0d;
        for (var q = 0; q < queryTerms.Length; q++)
        {
            token.ThrowIfCancellationRequested();
            if (!termIds.TryGetValue(queryTerms[q], out ids[q]))
            {
                ids[q] = -1;
                continue;
            }
            var list = postings[ids[q]];
            var idf = Math.Log(1 + (documents.Length - list.Length + 0.5) / (list.Length + 0.5));
            queryWeight += idf;
            foreach (var posting in list)
            {
                var frequency = posting.Positions.Length;
                bm25[posting.Document] += idf * frequency * (K1 + 1) /
                    (frequency + K1 * (1 - B + B * documents[posting.Document].Length / averageLength));
                covered[posting.Document] += idf;
                matched[posting.Document]++;
                hits.Add(posting.Document, q, posting.Positions);
            }
        }

        var candidates = new List<int>();
        for (var d = 0; d < documents.Length; d++)
            if (matched[d] > 0 && (include is null || include(documents[d].Id)))
                candidates.Add(d);
        if (candidates.Count == 0)
            return [];
        // Only a cut needs BM25's order; the selection below doesn't depend on the candidates' order.
        if (candidates.Count > RerankDepth)
        {
            candidates.Sort((left, right) => Compare(left, bm25[left], right, bm25[right], matched));
            candidates.RemoveRange(RerankDepth, candidates.Count - RerankDepth);
        }

        // Stage 2: coverage, proximity, phrases and recency.
        var score = new double[documents.Length];
        var occurrences = new List<int>();
        foreach (var d in candidates)
        {
            token.ThrowIfCancellationRequested();
            var (proximity, phrases) = matched[d] < 2 ? (0d, 0) : Closeness(hits, d, occurrences);
            var coverage = queryWeight > 0 ? covered[d] / queryWeight : 0;
            var age = Math.Max(0, (newest - documents[d].UpdatedAtUtc).TotalDays);
            var recency = Math.Pow(0.5, age / RecencyHalfLifeDays);
            score[d] = bm25[d] *
                (1 + CoverageWeight * coverage) *
                (1 + ProximityWeight * proximity) *
                (1 + PhraseWeight * (1 - Math.Pow(0.5, phrases))) *
                (1 + RecencyWeight * recency);
        }

        // Greedy selection: a near-duplicate of a fact already chosen (of the same person) counts a quarter. Scores only go down,
        // so the chosen scores never rise and the result is the same at any length.
        var remaining = candidates;
        var penalized = new HashSet<int>();
        var results = new List<LexicalMatch>(Math.Min(maximumResults, remaining.Count));
        while (results.Count < maximumResults && remaining.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var best = 0;
            for (var i = 1; i < remaining.Count; i++)
                if (Compare(remaining[i], score[remaining[i]], remaining[best], score[remaining[best]], matched) < 0)
                    best = i;
            var chosen = remaining[best];
            remaining.RemoveAt(best);
            results.Add(new(documents[chosen].Id, score[chosen], matched[chosen]));
            foreach (var other in remaining)
                if (!penalized.Contains(other) && NearDuplicates(documents[chosen], documents[other]))
                {
                    penalized.Add(other);
                    score[other] *= DuplicatePenalty;
                }
        }
        return [.. results];
    }

    /// <summary>Score first, then more matched query terms, then the newer fact, then the fact ID.</summary>
    private int Compare(int left, double leftScore, int right, double rightScore, int[] matched)
    {
        var order = rightScore.CompareTo(leftScore);
        if (order == 0)
            order = matched[right].CompareTo(matched[left]);
        if (order == 0)
            order = documents[right].UpdatedAtUtc.CompareTo(documents[left].UpdatedAtUtc);
        return order != 0 ? order : documents[left].Id.CompareTo(documents[right].Id);
    }

    /// <summary>How close two different query terms stand in fact <paramref name="document"/> (1 next to each other, 1/n n words
    /// apart) and how many of the query's word pairs it holds next to each other in the query's order. Reads only the positions
    /// of the query's terms, so a long fact costs no more than a short one.</summary>
    private static (double Proximity, int Phrases) Closeness(Hits hits, int document, List<int> occurrences)
    {
        // Each occurrence is position * QueryStride + query index: sorting the numbers sorts by position.
        const int QueryStride = 32;
        occurrences.Clear();
        for (var hit = hits.First(document); hit >= 0; hit = hits.Next(hit))
        {
            var query = hits.Query(hit);
            foreach (var position in hits.Positions(hit))
                occurrences.Add(position * QueryStride + query);
        }
        var sorted = CollectionsMarshal.AsSpan(occurrences);
        sorted.Sort();
        var gap = int.MaxValue;
        var phrases = 0;
        for (var i = 1; i < sorted.Length; i++)
        {
            int previousQuery = sorted[i - 1] % QueryStride, query = sorted[i] % QueryStride;
            if (previousQuery == query)
                continue;
            var distance = sorted[i] / QueryStride - sorted[i - 1] / QueryStride;
            gap = Math.Min(gap, distance);
            if (distance == 1 && query == previousQuery + 1)
                phrases++;
        }
        return (gap == int.MaxValue ? 0 : 1d / gap, phrases);
    }

    /// <summary>One query's hits, a linked list for each fact: which query term, at which positions.</summary>
    private sealed class Hits
    {
        private readonly int[] first;
        private readonly int[] next;
        private readonly int[] queries;
        private readonly int[][] positions;
        private int count;

        internal Hits(int documents, int capacity)
        {
            first = new int[documents];
            Array.Fill(first, -1);
            next = new int[capacity];
            queries = new int[capacity];
            positions = new int[capacity][];
        }

        internal void Add(int document, int query, int[] at)
        {
            next[count] = first[document];
            queries[count] = query;
            positions[count] = at;
            first[document] = count++;
        }

        internal int First(int document) => first[document];
        internal int Next(int hit) => next[hit];
        internal int Query(int hit) => queries[hit];
        internal int[] Positions(int hit) => positions[hit];
    }

    private static bool NearDuplicates(Document left, Document right)
    {
        if (left.VoiceId is not null && right.VoiceId is not null && !string.Equals(left.VoiceId, right.VoiceId, StringComparison.Ordinal))
            return false;
        int a = left.Words, b = right.Words;
        if (a == 0 || b == 0 || Math.Min(a, b) < DuplicateSimilarity * Math.Max(a, b))
            return false;
        // Jaccard of at least 0.8 leaves at most 20% of the two facts' words in only one of them.
        var differing = 0;
        for (var w = 0; w < SignatureWords; w++)
            differing += BitOperations.PopCount(left.Signature[w] ^ right.Signature[w]);
        if (differing > (1 - DuplicateSimilarity) * (a + b))
            return false;
        // Jaccard similarity from the smallest hashes of the union (exact when both facts fit in the sketch).
        int i = 0, j = 0, taken = 0, shared = 0;
        while (taken < SketchSize && (i < left.Sketch.Length || j < right.Sketch.Length))
        {
            if (j == right.Sketch.Length || (i < left.Sketch.Length && left.Sketch[i] < right.Sketch[j]))
                i++;
            else if (i == left.Sketch.Length || right.Sketch[j] < left.Sketch[i])
                j++;
            else
            {
                shared++;
                i++;
                j++;
            }
            taken++;
        }
        return shared >= DuplicateSimilarity * taken;
    }

    /// <summary>FNV-1a with a final mix: the same for a word in every process (unlike string.GetHashCode).</summary>
    private static ulong Hash(string term)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in term)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        hash ^= hash >> 33;
        hash *= 0xff51afd7ed558ccdUL;
        hash ^= hash >> 33;
        hash *= 0xc4ceb9fe1a85ec53UL;
        return hash ^ (hash >> 33);
    }
    /// <summary>The runs of letters and digits of <paramref name="text"/> in lower case, as <see cref="SearchTerms"/> reads words.</summary>
    private static IEnumerable<string> Words(string text)
    {
        var word = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c) || char.IsSurrogate(c))
            {
                word.Append(char.ToLowerInvariant(c));
                continue;
            }
            if (word.Length > 0)
                yield return word.ToString();
            word.Clear();
        }
        if (word.Length > 0)
            yield return word.ToString();
    }
}
