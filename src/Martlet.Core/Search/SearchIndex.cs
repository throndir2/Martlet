using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Martlet.Core.Search;

/// <summary>The inverted index of one source's documents (memory facts, an app guide's chunks, past exchanges) and the first stage
/// of a search over it (docs/SEARCH.md). Each document has the fields of its <see cref="SearchProfile"/>, each a list of search
/// terms (<see cref="Text.SearchTerms"/>); with <see cref="SearchProfile.Positions"/> the index keeps each field's terms in order,
/// for proximity, phrases and word overlap. The first stage is BM25 (one field) or BM25F (several fields, each weighted and
/// length-normalized before one saturation): it scores every document that holds a query term, notes which query concepts each
/// matched, and keeps the best few for the reranker (<see cref="SearchRanker"/>).
/// <para>An index grows one document at a time (<see cref="Add(FieldTerms[])"/>) and is searched in memory, in this process,
/// without a model. Adding and searching must not run at the same time; an index that is built once and then shared is
/// <see cref="Freeze">frozen</see>, and frozen indexes may be searched on any number of threads at once. An index with several
/// fields must be frozen before it is searched (freezing computes each posting's BM25F weight). A search costs what it matches,
/// not the size of the index: its working arrays are reused and only the documents it touched are cleared.</para></summary>
public sealed class SearchIndex
{
    private struct Postings
    {
        internal int[] Documents;
        // Each posting's term frequency in each field ([posting * fields + field]); null once a several-field index is frozen.
        internal int[]? Counts;
        // Each posting's BM25F weighted, length-normalized term frequency before saturation (several fields, once frozen).
        internal float[]? Weights;
        internal int Length;
    }

    private readonly SearchField[] fields;
    private readonly double k1;
    private readonly bool positions;
    private readonly Dictionary<string, int> vocabulary = new(StringComparer.Ordinal);
    private readonly Dictionary<int, int> counting = [];
    private readonly long[] totals;
    private Postings[] postings = [];
    private int[] lengths = [];
    private FieldTerms[] terms = [];
    private int count;
    private bool frozen;

    public SearchIndex(SearchProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Fields is not { Count: > 0 }) throw new ArgumentException("An index needs at least one field.", nameof(profile));
        fields = [.. profile.Fields];
        k1 = profile.K1;
        positions = profile.Positions;
        totals = new long[fields.Length];
    }

    /// <summary>How many documents the index holds.</summary>
    public int Count => count;
    /// <summary>How many different terms the index holds.</summary>
    public int TermCount => vocabulary.Count;
    public IReadOnlyList<SearchField> Fields => fields;
    /// <summary>The index's terms and their ids.</summary>
    public IReadOnlyDictionary<string, int> Vocabulary => vocabulary;
    public bool IsFrozen => frozen;

    public bool TryGetTerm(string term, out int id) => vocabulary.TryGetValue(term, out id);

    /// <summary>The terms of <paramref name="field"/> of <paramref name="document"/> in order, with positions (an index that keeps
    /// positions only).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public FieldTerms Terms(int document, int field)
    {
        if (!positions) throw new InvalidOperationException("This index doesn't keep positions.");
        return terms[Slot(document, field)];
    }

    /// <summary>How many terms <paramref name="field"/> of <paramref name="document"/> has.</summary>
    public int Length(int document, int field) => lengths[Slot(document, field)];

    /// <summary>How many documents hold <paramref name="term"/> (0 for -1).</summary>
    public int DocumentFrequency(int term) => term < 0 ? 0 : postings[term].Length;

    /// <summary>The inverse document frequency of <paramref name="term"/>: ln(1 + (N − df + 0.5) / (df + 0.5)). A term the index
    /// doesn't have (-1) is as rare as a term can be.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public double Idf(int term) => InverseFrequency(DocumentFrequency(term));

    /// <summary>The inverse document frequency of a term that <paramref name="documentFrequency"/> documents hold.</summary>
    public double InverseFrequency(int documentFrequency) =>
        Math.Log(1 + (count - documentFrequency + 0.5) / (documentFrequency + 0.5));

    /// <summary>The term ids of <paramref name="sequence"/> in order, as one field's terms with positions, adding new terms to the
    /// index's vocabulary. Several documents may share the result (<see cref="Add(FieldTerms[])"/>).</summary>
    public FieldTerms Terms(IReadOnlyList<string> sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        EnsureOpen();
        var ids = new int[sequence.Count];
        for (var i = 0; i < ids.Length; i++) ids[i] = Id(sequence[i]);
        return new(ids);
    }

    /// <summary>Adds a document with one list of search terms for each field, in the profile's order; returns its number.</summary>
    public int Add(params IReadOnlyList<string>[] documentFields)
    {
        ArgumentNullException.ThrowIfNull(documentFields);
        if (documentFields.Length != fields.Length) throw new ArgumentException($"A document has {fields.Length} fields.", nameof(documentFields));
        if (positions) return Add([.. documentFields.Select(field => Terms(field ?? []))]);
        EnsureOpen();
        var document = Grow();
        for (var f = 0; f < fields.Length; f++)
        {
            var field = documentFields[f] ?? [];
            counting.Clear();
            foreach (var term in field)
            {
                var id = Id(term);
                counting[id] = counting.GetValueOrDefault(id) + 1;
            }
            foreach (var (id, frequency) in counting) Post(id, document, f, frequency);
            lengths[document * fields.Length + f] = field.Count;
            totals[f] += field.Count;
        }
        count++;
        return document;
    }

    /// <summary>Adds a document with the terms of each field made by <see cref="Terms(IReadOnlyList{string})"/> (an index that
    /// keeps positions only); returns its number.</summary>
    public int Add(params FieldTerms[] documentFields)
    {
        ArgumentNullException.ThrowIfNull(documentFields);
        if (!positions) throw new InvalidOperationException("This index doesn't keep positions.");
        if (documentFields.Length != fields.Length) throw new ArgumentException($"A document has {fields.Length} fields.", nameof(documentFields));
        EnsureOpen();
        var document = Grow();
        for (var f = 0; f < fields.Length; f++)
        {
            var field = documentFields[f] ?? FieldTerms.Empty;
            for (var k = 0; k < field.Ids.Length; k++)
            {
                if ((uint)field.Ids[k] >= (uint)vocabulary.Count) throw new ArgumentException("A field's terms belong to another index.", nameof(documentFields));
                Post(field.Ids[k], document, f, field.Starts[k + 1] - field.Starts[k]);
            }
            terms[document * fields.Length + f] = field;
            lengths[document * fields.Length + f] = field.Length;
            totals[f] += field.Length;
        }
        count++;
        return document;
    }

    /// <summary>Makes the index read-only, so it may be searched on many threads at once, and trims its arrays. An index with
    /// several fields computes each posting's BM25F weight here.</summary>
    public SearchIndex Freeze()
    {
        if (frozen) return this;
        frozen = true;
        Array.Resize(ref postings, vocabulary.Count);
        Array.Resize(ref lengths, count * fields.Length);
        if (positions) Array.Resize(ref terms, count * fields.Length);
        var averages = new double[fields.Length];
        for (var f = 0; f < fields.Length; f++) averages[f] = Average(f);
        for (var t = 0; t < postings.Length; t++)
        {
            ref var list = ref postings[t];
            Array.Resize(ref list.Documents, list.Length);
            if (fields.Length == 1)
            {
                Array.Resize(ref list.Counts, list.Length);
                continue;
            }
            var weights = new float[list.Length];
            for (var i = 0; i < list.Length; i++)
            {
                var document = list.Documents[i];
                var weight = 0.0;
                for (var f = 0; f < fields.Length; f++)
                {
                    var frequency = list.Counts![i * fields.Length + f];
                    if (frequency == 0) continue;
                    var b = fields[f].LengthNormalization;
                    var norm = 1 - b + b * lengths[document * fields.Length + f] / averages[f];
                    weight += fields[f].Weight * frequency / norm;
                }
                weights[i] = (float)weight;
            }
            list.Weights = weights;
            list.Counts = null;
        }
        return this;
    }

    /// <summary>The first stage: every document that holds a term of <paramref name="query"/> is scored by BM25 (one field) or
    /// BM25F (several), each term's IDF times its weight; a document that matched none of the concepts of <paramref name="any"/>
    /// (0: no such rule) or that <paramref name="include"/> (a document and the concepts it matched) refuses is left out; and the
    /// best <paramref name="depth"/> come back, best first: a higher score first, then <paramref name="tie"/>, then the document
    /// matched first (or in no particular order when not <paramref name="ordered"/>, for a caller that orders them itself). <paramref name="annotate"/> runs before the search's working arrays are cleared, with the candidates and the
    /// concepts any document matched (0 for one the query didn't touch), for a source that looks at the documents around a
    /// candidate; it must not search again.</summary>
    // Fully optimized from the first call: a user sends too few messages for the runtime's tiered compilation to optimize it.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public SearchCandidate[] Search(SearchQuery query, int depth, Func<int, int, bool>? include = null,
        Comparison<SearchCandidate>? tie = null, Action<SearchCandidate[], Func<int, int>>? annotate = null, int any = 0,
        bool ordered = true, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var queryTerms = query.Items;
        if (count == 0 || depth <= 0 || queryTerms.Length == 0) return [];
        var several = fields.Length > 1;
        if (several && !frozen) throw new InvalidOperationException("Freeze an index with several fields before searching it.");
        var scratch = Scratch.Rent(count);
        try
        {
            var scores = scratch.Scores;
            var masks = scratch.Masks;
            var touched = scratch.Touched;
            var saturation = k1;
            var b = fields[0].LengthNormalization;
            var average = Average(0);
            foreach (var (term, weight, mask) in queryTerms)
            {
                token.ThrowIfCancellationRequested();
                if (term < 0) continue;
                var list = postings[term];
                var idf = Idf(term) * weight;
                var documents = list.Documents.AsSpan(0, list.Length);
                if (several)
                {
                    var weights = list.Weights.AsSpan(0, documents.Length);
                    for (var i = 0; i < documents.Length; i++)
                    {
                        var d = documents[i];
                        if (masks[d] == 0) touched.Add(d);
                        masks[d] |= mask;
                        double tf = weights[i];
                        scores[d] += idf * tf * (saturation + 1) / (tf + saturation);
                    }
                    continue;
                }
                var counts = list.Counts.AsSpan(0, documents.Length);
                for (var i = 0; i < documents.Length; i++)
                {
                    var d = documents[i];
                    if (masks[d] == 0) touched.Add(d);
                    masks[d] |= mask;
                    var frequency = counts[i];
                    scores[d] += idf * frequency * (saturation + 1) / (frequency + saturation * (1 - b + b * lengths[d] / average));
                }
            }

            // The best `depth` with a heap of places in `touched` whose top is the worst kept: exactly the set a full sort would
            // cut to. Popping it gives them worst first.
            var order = new Order(scores, masks, touched, tie);
            var heap = scratch.Heap(Math.Min(depth, touched.Count));
            var size = 0;
            // Once the heap is full, a document scoring below the worst kept can't get in: most are left out with one compare.
            var worst = double.NegativeInfinity;
            for (var o = 0; o < touched.Count; o++)
            {
                var d = touched[o];
                if (size == depth && scores[d] < worst) continue;
                if (any != 0 && (masks[d] & any) == 0) continue;
                if (include is not null && !include(d, masks[d])) continue;
                if (size < depth)
                {
                    heap[size] = o;
                    order.Up(heap, size++);
                }
                else if (order.Better(o, heap[0]))
                {
                    heap[0] = o;
                    order.Down(heap, 0, size);
                }
                else continue;
                if (size == depth) worst = scores[touched[heap[0]]];
            }
            var candidates = new SearchCandidate[size];
            if (!ordered)
                for (var at = 0; at < size; at++) candidates[at] = order.Candidate(heap[at]);
            else
                for (var at = size - 1; at >= 0; at--)
                {
                    candidates[at] = order.Candidate(heap[0]);
                    heap[0] = heap[at];
                    order.Down(heap, 0, at);
                }
            annotate?.Invoke(candidates, d => (uint)d < (uint)count ? masks[d] : 0);
            return candidates;
        }
        finally
        {
            scratch.Return();
        }
    }

    /// <summary>The order of the first stage's documents (by their place in the touched list): a higher score first, then the tie
    /// rule, then the one matched first; and a heap whose top is the worst.</summary>
    private readonly struct Order(double[] scores, int[] masks, List<int> touched, Comparison<SearchCandidate>? tie)
    {
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal SearchCandidate Candidate(int place)
        {
            var d = touched[place];
            return new(d, scores[d], masks[d], place);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal bool Better(int left, int right)
        {
            double a = scores[touched[left]], b = scores[touched[right]];
            if (a != b) return a > b;
            if (tie is not null && tie(Candidate(left), Candidate(right)) is var order and not 0) return order < 0;
            return left < right;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal void Up(int[] heap, int at)
        {
            while (at > 0)
            {
                var parent = (at - 1) / 2;
                if (!Better(heap[parent], heap[at])) break;
                (heap[parent], heap[at]) = (heap[at], heap[parent]);
                at = parent;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal void Down(int[] heap, int at, int size)
        {
            while (true)
            {
                int left = 2 * at + 1, worst = at;
                if (left < size && Better(heap[worst], heap[left])) worst = left;
                if (left + 1 < size && Better(heap[worst], heap[left + 1])) worst = left + 1;
                if (worst == at) return;
                (heap[worst], heap[at]) = (heap[at], heap[worst]);
                at = worst;
            }
        }
    }

    private double Average(int field) => count == 0 ? 1 : Math.Max(1, (double)totals[field] / count);

    private int Slot(int document, int field)
    {
        if ((uint)document >= (uint)count || (uint)field >= (uint)fields.Length) throw new ArgumentOutOfRangeException(nameof(document));
        return document * fields.Length + field;
    }

    private void EnsureOpen()
    {
        if (frozen) throw new InvalidOperationException("The index is frozen.");
    }

    private int Id(string term)
    {
        if (vocabulary.TryGetValue(term, out var id)) return id;
        id = vocabulary.Count;
        vocabulary.Add(term, id);
        if (id == postings.Length) Array.Resize(ref postings, Math.Max(16, 2 * postings.Length));
        postings[id] = new() { Documents = [], Counts = [] };
        return id;
    }

    private int Grow()
    {
        var needed = (count + 1) * fields.Length;
        if (lengths.Length < needed)
        {
            var length = Math.Max(needed, Math.Max(16, 2 * lengths.Length));
            Array.Resize(ref lengths, length);
            if (positions) Array.Resize(ref terms, length);
        }
        return count;
    }

    private void Post(int term, int document, int field, int frequency)
    {
        ref var list = ref postings[term];
        if (list.Length == 0 || list.Documents[list.Length - 1] != document)
        {
            if (list.Length == list.Documents.Length)
            {
                var length = Math.Max(4, 2 * list.Length);
                Array.Resize(ref list.Documents, length);
                Array.Resize(ref list.Counts, length * fields.Length);
            }
            list.Documents[list.Length++] = document;
        }
        list.Counts![(list.Length - 1) * fields.Length + field] += frequency;
    }

    /// <summary>A search's working arrays, at least as long as the index searched: each document's score and matched concepts, and
    /// the documents touched. Between searches every entry is zero, because a search clears what it touched. Kept in a pool, so
    /// there are only as many as searches that ran at the same time.</summary>
    private sealed class Scratch
    {
        private static readonly ConcurrentBag<Scratch> Pool = [];

        internal double[] Scores = [];
        internal int[] Masks = [];
        internal readonly List<int> Touched = [];
        private int[] heap = [];
        private int used;

        internal int[] Heap(int size)
        {
            if (heap.Length < size) heap = new int[Math.Max(size, 2 * heap.Length)];
            return heap;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static Scratch Rent(int documents)
        {
            if (!Pool.TryTake(out var scratch)) scratch = new();
            if (scratch.Scores.Length < documents)
            {
                var length = Math.Max(documents, (int)Math.Min(2L * scratch.Scores.Length, Array.MaxLength));
                scratch.Scores = new double[length];
                scratch.Masks = new int[length];
            }
            scratch.used = documents;
            return scratch;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal void Return()
        {
            // A search that touched much of the index clears the whole of it at once, which is faster than one by one.
            if (Touched.Count > used / 8)
            {
                Array.Clear(Scores, 0, used);
                Array.Clear(Masks, 0, used);
            }
            else
                foreach (var d in Touched)
                {
                    Scores[d] = 0;
                    Masks[d] = 0;
                }
            Touched.Clear();
            Pool.Add(this);
        }
    }
}
