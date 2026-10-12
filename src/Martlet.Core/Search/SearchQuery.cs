namespace Martlet.Core.Search;

/// <summary>One field of an index's documents (a page title, a section heading, a text, a fact's search words): its BM25F weight and
/// its length normalization (b, from 0 for none to 1 for full). An index with one field is plain BM25.</summary>
public sealed record SearchField(string Name, double Weight = 1, double LengthNormalization = 0.75);

/// <summary>The terms of one field of a document with their positions: the terms in order (<see cref="Sequence"/>), the distinct
/// term ids in ascending order (<see cref="Ids"/>) and, for each, its positions in the field. Read-only; several documents may share
/// one (the title of a page that has many chunks).</summary>
public sealed class FieldTerms
{
    private static readonly int[] None = [];

    public static FieldTerms Empty { get; } = new([]);

    public FieldTerms(int[] sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        Sequence = sequence;
        if (sequence.Length == 0)
        {
            Ids = Starts = Positions = None;
            return;
        }
        var packed = new long[sequence.Length];
        for (var i = 0; i < sequence.Length; i++) packed[i] = ((long)sequence[i] << 32) | (uint)i;
        Array.Sort(packed);
        var ids = new List<int>();
        var starts = new List<int>();
        Positions = new int[packed.Length];
        for (var i = 0; i < packed.Length; i++)
        {
            var id = (int)(packed[i] >> 32);
            if (ids.Count == 0 || ids[^1] != id)
            {
                ids.Add(id);
                starts.Add(i);
            }
            Positions[i] = (int)(packed[i] & 0xffffffff);
        }
        starts.Add(packed.Length);
        Ids = [.. ids];
        Starts = [.. starts];
    }

    /// <summary>The field's term ids in order.</summary>
    public int[] Sequence { get; }
    /// <summary>The distinct term ids, ascending.</summary>
    public int[] Ids { get; }
    /// <summary>Where each distinct term's positions start in <see cref="Positions"/> (one more entry than <see cref="Ids"/>).</summary>
    public int[] Starts { get; }
    /// <summary>The positions of each distinct term, in the order of <see cref="Ids"/>, each term's ascending.</summary>
    public int[] Positions { get; }

    public int Length => Sequence.Length;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public ReadOnlySpan<int> PositionsOf(int term)
    {
        var k = Array.BinarySearch(Ids, term);
        return k < 0 ? default : Positions.AsSpan(Starts[k], Starts[k + 1] - Starts[k]);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public bool Has(int term) => Array.BinarySearch(Ids, term) >= 0;
}

/// <summary>One term of a query: the index's term id (-1 for a word the index doesn't have), how much it counts (1 for a full word)
/// and the bits of the query's concepts it satisfies (a concept is one word of the query; a concept may have several terms, such
/// as "colour" and "color").</summary>
public readonly record struct SearchQueryTerm(int Term, double Weight, int Mask);

/// <summary>What a search looks for: its terms in order, each with its concept bits (at most <see cref="MaximumConcepts"/>
/// concepts, so a document's matched concepts fit in a bit mask).</summary>
public sealed class SearchQuery
{
    public const int MaximumConcepts = 31;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public SearchQuery(IReadOnlyList<SearchQueryTerm> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        Items = terms as SearchQueryTerm[] ?? [.. terms];
        foreach (var term in Items)
            if (term.Mask <= 0)
                throw new ArgumentException("A query term must satisfy at least one of the first 31 concepts.", nameof(terms));
    }

    public IReadOnlyList<SearchQueryTerm> Terms => Items;

    internal SearchQueryTerm[] Items { get; }

    /// <summary>A query of <paramref name="terms"/> (search terms, each once) where each term is its own concept: term i sets bit i
    /// and counts fully. A term <paramref name="index"/> doesn't have stays in the query as -1, so the bits keep the terms' order.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static SearchQuery Of(SearchIndex index, IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(terms);
        if (terms.Count > MaximumConcepts) throw new ArgumentException($"A query has at most {MaximumConcepts} terms.", nameof(terms));
        var query = new SearchQueryTerm[terms.Count];
        for (var i = 0; i < terms.Count; i++)
            query[i] = new(index.TryGetTerm(terms[i], out var id) ? id : -1, 1, 1 << i);
        return new(query);
    }
}

/// <summary>A document the first stage found: its number in the index (or in the source's own list), its first-stage score, the
/// query concepts it matched (bits) and its place among the documents found (the first matched is 0).</summary>
public readonly record struct SearchCandidate(int Document, double Score, int Mask, int Order);
