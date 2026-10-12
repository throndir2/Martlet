using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Martlet.Core.Search;

/// <summary>The reranker's measures, shared by every source (docs/SEARCH.md): how much of the query a document covers, how close
/// together its query words are, whether they stand in the query's order, how recent it is, how near a time it is, and how similar
/// two documents are. Pure and thread-safe.</summary>
public static class SearchMeasures
{
    /// <summary>The summed <paramref name="weights"/> of the bits set in <paramref name="mask"/> (bit i: weight i), in bit order. With
    /// IDF weights, a mask's share of the query's weight is its coverage.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static double Weight(int mask, ReadOnlySpan<double> weights)
    {
        var weight = 0.0;
        for (var bit = 0; bit < weights.Length; bit++)
            if ((mask & 1 << bit) != 0) weight += weights[bit];
        return weight;
    }

    /// <summary>How close two different query terms stand in <paramref name="field"/> (1 next to each other, 1/n n words apart;
    /// Tao and Zhai, 2007) and how many neighbouring occurrences are two query terms in the query's order (term i, then term i + 1).
    /// Reads only the positions of the query's terms that satisfy a concept of <paramref name="matched"/> (the concepts the
    /// document matched), so a long field costs no more than a short one. <paramref name="occurrences"/> is working space.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static (double Proximity, int Phrases) ClosestPair(FieldTerms field, SearchQuery query, int matched, List<int> occurrences)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(occurrences);
        // Each occurrence is position * QueryStride + query index: sorting the numbers sorts by position.
        const int QueryStride = 32;
        occurrences.Clear();
        var terms = query.Items;
        for (var q = 0; q < terms.Length; q++)
        {
            var term = terms[q].Term;
            if (term < 0 || (terms[q].Mask & matched) == 0) continue;
            foreach (var position in field.PositionsOf(term)) occurrences.Add(position * QueryStride + q);
        }
        var sorted = CollectionsMarshal.AsSpan(occurrences);
        sorted.Sort();
        var gap = int.MaxValue;
        var phrases = 0;
        for (var i = 1; i < sorted.Length; i++)
        {
            int previousQuery = sorted[i - 1] % QueryStride, current = sorted[i] % QueryStride;
            if (previousQuery == current) continue;
            var distance = sorted[i] / QueryStride - sorted[i - 1] / QueryStride;
            gap = Math.Min(gap, distance);
            if (distance == 1 && current == previousQuery + 1) phrases++;
        }
        return (gap == int.MaxValue ? 0 : 1d / gap, phrases);
    }

    /// <summary>The smallest window of <paramref name="field"/> that holds every concept of <paramref name="matched"/> (the "minimum
    /// cover" of proximity scoring; Büttcher, Clarke and Lushman, 2006), as a closeness from 0 to 1: 1 when they are side by side,
    /// ½ when the window has about twice as many other words as matched ones; 0 with fewer than two concepts in the field. A concept
    /// is satisfied by any of its terms (<paramref name="concepts"/>[c]); at most <paramref name="maximumPositions"/> occurrences of
    /// each are read. Each term's positions are already in order, so the window is found by moving through the lists at once.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static double MinimumCover(FieldTerms field, IReadOnlyList<int[]> concepts, int matched, int maximumPositions = 64)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(concepts);
        Span<int> starts = stackalloc int[SearchQuery.MaximumConcepts];
        Span<int> ends = stackalloc int[SearchQuery.MaximumConcepts];
        int[]?[]? lists = null; // a concept whose several terms are in the field, merged into one list
        var k = 0;
        for (var c = 0; c < Math.Min(concepts.Count, SearchQuery.MaximumConcepts); c++)
        {
            if ((matched & (1 << c)) == 0) continue;
            int found = 0, start = 0, end = 0;
            List<int>? merged = null;
            foreach (var term in concepts[c])
            {
                var at = Array.BinarySearch(field.Ids, term);
                if (at < 0) continue;
                if (++found == 1)
                {
                    (start, end) = (field.Starts[at], field.Starts[at + 1]);
                    continue;
                }
                merged ??= [.. field.Positions.AsSpan(start, end - start)];
                merged.AddRange(field.Positions.AsSpan(field.Starts[at], field.Starts[at + 1] - field.Starts[at]));
            }
            if (found == 0) continue;
            if (merged is not null)
            {
                merged.Sort();
                (lists ??= new int[]?[SearchQuery.MaximumConcepts])[k] = [.. merged];
                (start, end) = (0, merged.Count);
            }
            starts[k] = start;
            ends[k] = Math.Min(end, start + maximumPositions);
            k++;
        }
        if (k < 2) return 0;

        var window = int.MaxValue;
        while (true)
        {
            int lowest = 0, low = int.MaxValue, high = int.MinValue;
            for (var i = 0; i < k; i++)
            {
                var position = (lists?[i] ?? field.Positions)[starts[i]];
                if (position < low) (low, lowest) = (position, i);
                if (position > high) high = position;
            }
            window = Math.Min(window, high - low + 1);
            if (++starts[lowest] >= ends[lowest]) break;
        }
        var extra = Math.Max(0, window - k);
        return 1 / (1 + extra / (2.0 * k));
    }

    /// <summary>How close the query's words stand in <paramref name="sequence"/> (each word's query term number, -1 for another
    /// word): the shortest stretch that holds every query term the sequence has, as (distinct − 1) / (asked − 1) × distinct ÷ the
    /// stretch's length, so all <paramref name="asked"/> terms side by side is 1. 0 with fewer than two terms.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static double Stretch(ReadOnlySpan<int> sequence, int asked)
    {
        if (asked < 2) return 0;
        var hits = new List<(int At, int Term)>();
        for (var at = 0; at < sequence.Length; at++)
            if (sequence[at] >= 0) hits.Add((at, sequence[at]));
        var distinct = hits.Select(hit => hit.Term).Distinct().Count();
        if (distinct < 2) return 0;
        var counts = new Dictionary<int, int>();
        int covered = 0, left = 0, shortest = int.MaxValue;
        for (var right = 0; right < hits.Count; right++)
        {
            if ((counts[hits[right].Term] = counts.GetValueOrDefault(hits[right].Term) + 1) == 1) covered++;
            while (covered == distinct)
            {
                shortest = Math.Min(shortest, hits[right].At - hits[left].At + 1);
                if (--counts[hits[left].Term] == 0) covered--;
                left++;
            }
        }
        return (double)(distinct - 1) / (asked - 1) * distinct / shortest;
    }

    /// <summary>Whether query term <paramref name="first"/> stands right before <paramref name="second"/> in
    /// <paramref name="sequence"/> (each word's query term number, -1 for another word).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool Adjacent(ReadOnlySpan<int> sequence, int first, int second)
    {
        for (var at = 0; at + 1 < sequence.Length; at++)
            if (sequence[at] == first && sequence[at + 1] == second) return true;
        return false;
    }

    /// <summary>Whether a term of <paramref name="first"/> stands right before a term of <paramref name="second"/> in
    /// <paramref name="field"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool Adjacent(FieldTerms field, int[] first, int[] second)
    {
        ArgumentNullException.ThrowIfNull(field);
        foreach (var a in first)
        {
            var left = field.PositionsOf(a);
            if (left.Length == 0) continue;
            foreach (var b in second)
            {
                var right = field.PositionsOf(b);
                int i = 0, j = 0;
                while (i < left.Length && j < right.Length)
                {
                    var want = left[i] + 1;
                    if (right[j] == want) return true;
                    if (right[j] < want) j++;
                    else i++;
                }
            }
        }
        return false;
    }

    /// <summary>A recency from 1 (now) down to 0 that halves every <paramref name="halfLifeDays"/> days of
    /// <paramref name="ageDays"/> (a negative age counts as 0).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Recency(double ageDays, double halfLifeDays) => Math.Pow(0.5, Math.Max(0, ageDays) / halfLifeDays);

    /// <summary>How near <paramref name="at"/> is to the time from <paramref name="from"/> to <paramref name="to"/>: 1 inside it,
    /// falling to 0 at <paramref name="margin"/> before or after it.</summary>
    public static double TimeCloseness(DateTimeOffset at, DateTimeOffset from, DateTimeOffset to, TimeSpan margin)
    {
        if (at >= from && at < to) return 1;
        var distance = at < from ? (from - at).TotalDays : (at - to).TotalDays;
        return 1 - Math.Min(1, distance / margin.TotalDays);
    }

    /// <summary>The Jaccard similarity of two sets of term ids, each sorted ascending (<see cref="FieldTerms.Ids"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static double Jaccard(int[] a, int[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Length == 0 || b.Length == 0) return 0;
        int i = 0, j = 0, shared = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j]) { shared++; i++; j++; }
            else if (a[i] < b[j]) i++;
            else j++;
        }
        return (double)shared / (a.Length + b.Length - shared);
    }

    /// <summary>The Jaccard similarity of two sets.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static double Jaccard<T>(HashSet<T> a, HashSet<T> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count == 0 || b.Count == 0) return 0;
        var shared = a.Count <= b.Count ? a.Count(b.Contains) : b.Count(a.Contains);
        return (double)shared / (a.Count + b.Count - shared);
    }
}

/// <summary>A document's terms in short, for finding near-duplicates at the same cost for any length: how many different terms it
/// has, the hashes of the <see cref="Size"/> smallest (a bottom-k MinHash sketch, so the Jaccard similarity is exact for documents
/// of up to that many different terms and estimated for longer ones) and a 256-bit signature (each term sets one bit), whose
/// differing bits rule most pairs out without comparing sketches.</summary>
public sealed class SearchSketch
{
    public const int Size = 32;
    private const int SignatureWords = 4;

    private readonly ulong[] sketch;
    private readonly ulong[] signature;

    private SearchSketch(int words, ulong[] sketch, ulong[] signature)
    {
        Words = words;
        this.sketch = sketch;
        this.signature = signature;
    }

    /// <summary>How many different terms the document has.</summary>
    public int Words { get; }

    /// <summary>The sketch of the different terms of <paramref name="terms"/>.</summary>
    public static SearchSketch Of(IEnumerable<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        var hashes = terms.Distinct(StringComparer.Ordinal).Select(Hash).ToArray();
        var signature = new ulong[SignatureWords];
        foreach (var hash in hashes) signature[(int)((hash >> 6) % SignatureWords)] |= 1UL << (int)(hash & 63);
        return new(hashes.Length, [.. hashes.Order().Take(Size)], signature);
    }

    /// <summary>The Jaccard similarity of the two documents' terms (exact when both fit in the sketch, estimated from the smallest
    /// hashes of the union otherwise), or 0 when it is surely below <paramref name="atLeast"/>: their sizes are too far apart, or
    /// their signatures differ in more bits than such documents can.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public double Similarity(SearchSketch other, double atLeast)
    {
        ArgumentNullException.ThrowIfNull(other);
        int a = Words, b = other.Words;
        if (a == 0 || b == 0 || Math.Min(a, b) < atLeast * Math.Max(a, b)) return 0;
        // A Jaccard similarity of at least s leaves at most (1 − s) of the two documents' terms in only one of them.
        var differing = 0;
        for (var w = 0; w < SignatureWords; w++) differing += BitOperations.PopCount(signature[w] ^ other.signature[w]);
        if (differing > (1 - atLeast) * (a + b)) return 0;
        int i = 0, j = 0, taken = 0, shared = 0;
        while (taken < Size && (i < sketch.Length || j < other.sketch.Length))
        {
            if (j == other.sketch.Length || (i < sketch.Length && sketch[i] < other.sketch[j])) i++;
            else if (i == sketch.Length || other.sketch[j] < sketch[i]) j++;
            else
            {
                shared++;
                i++;
                j++;
            }
            taken++;
        }
        return (double)shared / taken;
    }

    /// <summary>FNV-1a with a final mix: the same for a term in every process (unlike string.GetHashCode).</summary>
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
}

/// <summary>Reciprocal rank fusion (Cormack, Clarke and Büttcher, 2009): several ranked lists of the same documents (a lexical
/// search and an embedding search) made one, each document scoring Σ 1 / (k + its rank) over the lists that have it (rank 1 for
/// the first). It needs no score calibration between the lists, so a lexical and an embedding search fuse without tuning.</summary>
public static class SearchFusion
{
    /// <summary>The usual constant: it keeps one list's top document from deciding alone.</summary>
    public const int K = 60;

    /// <summary>Each document's fused score over <paramref name="lists"/> (each best first; a document listed twice counts at its
    /// first place).</summary>
    public static Dictionary<int, double> Reciprocal(IReadOnlyList<IReadOnlyList<int>> lists, int k = K)
    {
        ArgumentNullException.ThrowIfNull(lists);
        ArgumentOutOfRangeException.ThrowIfNegative(k);
        var scores = new Dictionary<int, double>();
        foreach (var list in lists)
        {
            var seen = new HashSet<int>();
            for (var rank = 0; rank < list.Count; rank++)
                if (seen.Add(list[rank]))
                    scores[list[rank]] = scores.GetValueOrDefault(list[rank]) + 1.0 / (k + rank + 1);
        }
        return scores;
    }

    /// <summary>The documents of <paramref name="lists"/> by fused score, best first; equal scores keep the order in which the
    /// documents first appear.</summary>
    public static IReadOnlyList<int> Fuse(IReadOnlyList<IReadOnlyList<int>> lists, int k = K)
    {
        var scores = Reciprocal(lists, k);
        var first = new Dictionary<int, int>();
        foreach (var list in lists)
            foreach (var document in list)
                first.TryAdd(document, first.Count);
        return [.. scores.Keys.OrderByDescending(document => scores[document]).ThenBy(document => first[document])];
    }
}
