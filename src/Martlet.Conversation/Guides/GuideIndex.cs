using System.Buffers;
using Martlet.Core.Text;

namespace Martlet.Conversation.Guides;

/// <summary>Builds a guide's search index (docs/APP_GUIDES.md, "Searching a guide"). A search runs in two stages, in this process
/// and with no model: BM25F over each chunk's page title, section headings and text picks candidates, then a reranker
/// (<see cref="GuideReranker"/>) scores them again by how much of the question they cover, how close together and in what order
/// its words are, and whether a heading names them, gives each a calibrated relevance and leaves out near-duplicates. A search
/// over a few thousand chunks takes well under a millisecond.</summary>
public static class GuideIndex
{
    /// <summary>The index of <paramref name="chunks"/>.</summary>
    public static IGuideIndex Build(IReadOnlyList<GuideChunk> chunks) => Build(chunks, null);

    /// <summary>The index of <paramref name="guide"/>'s chunks; the app's name in a question ("... in Elden Ring") is left out.</summary>
    public static IGuideIndex Build(AppGuide guide)
    {
        ArgumentNullException.ThrowIfNull(guide);
        return Build(guide.Chunks, [guide.Name]);
    }

    /// <summary>The index of <paramref name="chunks"/>, for an app with <paramref name="appNames"/> (its name and program names):
    /// in a question, the app's full name is left out and a part of it ("in Stardew") never counts against a chunk.</summary>
    public static IGuideIndex Build(IReadOnlyList<GuideChunk> chunks, IEnumerable<string>? appNames) =>
        new LexicalGuideIndex(chunks ?? throw new ArgumentNullException(nameof(chunks)), appNames, GuideRanking.Default);

    internal static LexicalGuideIndex Build(IReadOnlyList<GuideChunk> chunks, IEnumerable<string>? appNames, GuideRanking ranking) =>
        new(chunks ?? throw new ArgumentNullException(nameof(chunks)), appNames, ranking);
}

/// <summary>The weights of a guide search, tuned on the evaluation fixture (tests\Martlet.Conversation.Tests\Fixtures).</summary>
internal sealed record GuideRanking
{
    public static GuideRanking Default { get; } = new();

    // BM25F: per-field weight and length normalization, then one saturation (k1) of the weighted term frequency.
    public double K1 { get; init; } = 1.2;
    public double TitleWeight { get; init; } = 2.5;
    public double SectionWeight { get; init; } = 3.5;
    public double BodyWeight { get; init; } = 1.0;
    public double TitleB { get; init; } = 0.3;
    public double SectionB { get; init; } = 0.4;
    public double BodyB { get; init; } = 0.6;
    /// <summary>A soft word's (question scaffolding, the app's name) share of a full word's score.</summary>
    public double SoftWeight { get; init; } = 0.3;
    /// <summary>A word the guide doesn't have weighs this many reference IDFs (the IDF of a word in one chunk in twenty).</summary>
    public double UnknownWeight { get; init; } = 1.0;
    /// <summary>How informative an everyday chat word ("bed", "morning") is when only a chunk's text has it.</summary>
    public double EverydayInformative { get; init; } = 0.35;
    /// <summary>How many first-stage chunks the reranker scores again.</summary>
    public int Candidates { get; init; } = 40;

    // The reranker's score: the first stage's (scaled to the best candidate), relevance, proximity, phrases, headings and the
    // share of the question's soft words a chunk has.
    public double FirstStageShare { get; init; } = 0.2;
    public double RelevanceShare { get; init; } = 0.3;
    public double ProximityShare { get; init; } = 0.1;
    public double PhraseShare { get; init; } = 0.05;
    public double HeadingShare { get; init; } = 0.05;
    public double SoftShare { get; init; } = 0.05;

    // Relevance = coverage^CoverageExponent × specificity × (ClosenessFloor + (1 − ClosenessFloor) × closeness).
    public double CoverageExponent { get; init; } = 1.25;
    public double ClosenessFloor { get; init; } = 0.7;

    // Diversity: maximal marginal relevance with this lambda, chunks of one page this similar, near-duplicates left out, and at
    // most PerPage chunks of a page before every other page has had its turn.
    public double Lambda { get; init; } = 0.8;
    public double SamePage { get; init; } = 0.35;
    public double NearDuplicate { get; init; } = 0.8;
    public int PerPage { get; init; } = 2;
}

/// <summary>The terms of one field of a chunk with their positions: the distinct term ids in ascending order and, for each, its
/// positions in the field.</summary>
internal sealed class FieldTerms
{
    private static readonly int[] None = [];

    public FieldTerms(int[] sequence)
    {
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
    public int[] Ids { get; }
    public int[] Starts { get; }
    public int[] Positions { get; }

    public ReadOnlySpan<int> PositionsOf(int term)
    {
        var k = Array.BinarySearch(Ids, term);
        return k < 0 ? default : Positions.AsSpan(Starts[k], Starts[k + 1] - Starts[k]);
    }

    public bool Has(int term) => Array.BinarySearch(Ids, term) >= 0;
}

/// <summary>The in-memory index of a guide: read-only once built, so searches may run on any thread at once.</summary>
internal sealed class LexicalGuideIndex : IGuideIndex
{
    public LexicalGuideIndex(IReadOnlyList<GuideChunk> source, IEnumerable<string>? appNames, GuideRanking ranking)
    {
        Ranking = ranking;
        Chunks = [.. source];
        AppWords = [.. (appNames ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => SearchTerms.Words(n).ToArray())
            .Where(w => w.Length > 0)];
        var n = Chunks.Length;
        var vocabulary = new Dictionary<string, int>(StringComparer.Ordinal);
        Titles = new FieldTerms[n];
        Sections = new FieldTerms[n];
        Bodies = new FieldTerms[n];
        Pages = new int[n];
        var titles = new Dictionary<string, (FieldTerms Terms, int Page)>(StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
        {
            var chunk = Chunks[i] ?? throw new ArgumentException("A guide chunk is missing.", nameof(source));
            var pageKey = chunk.Url + "\n" + chunk.Page;
            if (!titles.TryGetValue(pageKey, out var title))
                titles[pageKey] = title = (new FieldTerms(Ids(SearchTerms.Of(chunk.Page))), titles.Count);
            Titles[i] = title.Terms;
            Pages[i] = title.Page;
            Sections[i] = new FieldTerms(Ids(SearchTerms.Of(SectionWithoutTitle(chunk.Section, chunk.Page))));
            Bodies[i] = new FieldTerms(Ids(SearchTerms.Of(chunk.Text)));
        }
        Vocabulary = vocabulary;

        double titleAverage = Average(Titles), sectionAverage = Average(Sections), bodyAverage = Average(Bodies);
        var postings = new List<(int Chunk, float Weight)>?[vocabulary.Count];
        var weights = new Dictionary<int, double>();
        for (var i = 0; i < n; i++)
        {
            weights.Clear();
            Add(Titles[i], ranking.TitleWeight, ranking.TitleB, titleAverage);
            Add(Sections[i], ranking.SectionWeight, ranking.SectionB, sectionAverage);
            Add(Bodies[i], ranking.BodyWeight, ranking.BodyB, bodyAverage);
            foreach (var (term, weight) in weights) (postings[term] ??= []).Add((i, (float)weight));
        }
        PostingChunks = new int[postings.Length][];
        PostingWeights = new float[postings.Length][];
        Idf = new double[postings.Length];
        for (var t = 0; t < postings.Length; t++)
        {
            var list = postings[t] ?? [];
            PostingChunks[t] = [.. list.Select(p => p.Chunk)];
            PostingWeights[t] = [.. list.Select(p => p.Weight)];
            Idf[t] = InverseFrequency(list.Count);
        }
        ReferenceIdf = InverseFrequency(Math.Max(1, (int)Math.Round(n * 0.05)));

        int[] Ids(IReadOnlyList<string> terms)
        {
            var ids = new int[terms.Count];
            for (var k = 0; k < terms.Count; k++)
            {
                if (!vocabulary.TryGetValue(terms[k], out var id)) vocabulary[terms[k]] = id = vocabulary.Count;
                ids[k] = id;
            }
            return ids;
        }

        void Add(FieldTerms field, double weight, double b, double average)
        {
            if (field.Ids.Length == 0) return;
            var norm = 1 - b + b * field.Sequence.Length / average;
            for (var k = 0; k < field.Ids.Length; k++)
            {
                var tf = field.Starts[k + 1] - field.Starts[k];
                weights[field.Ids[k]] = weights.GetValueOrDefault(field.Ids[k]) + weight * tf / norm;
            }
        }
    }

    public GuideRanking Ranking { get; }
    public GuideChunk[] Chunks { get; }
    public string[][] AppWords { get; }
    public Dictionary<string, int> Vocabulary { get; }
    public FieldTerms[] Titles { get; }
    public FieldTerms[] Sections { get; }
    public FieldTerms[] Bodies { get; }
    /// <summary>The page of each chunk, numbered.</summary>
    public int[] Pages { get; }
    public int[][] PostingChunks { get; }
    /// <summary>Each posting's BM25F weighted, length-normalized term frequency (before saturation).</summary>
    public float[][] PostingWeights { get; }
    public double[] Idf { get; }
    /// <summary>The IDF of a term in about one chunk in twenty.</summary>
    public double ReferenceIdf { get; }

    public int Count => Chunks.Length;

    public IReadOnlyList<GuideHit> Search(string query, int max) => Search(query, max, null);

    /// <summary>A search that tells <paramref name="explain"/> each candidate's reranking features (for tuning).</summary>
    // Fully optimized from the first call: a user sends too few messages for the runtime's tiered compilation to optimize it.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    internal IReadOnlyList<GuideHit> Search(string query, int max, Action<GuideChunk, string>? explain)
    {
        if (max <= 0 || Chunks.Length == 0) return [];
        var parsed = GuideQuery.Parse(query, Vocabulary, t => Idf[t], ReferenceIdf, AppWords, Ranking);
        if (parsed.IsEmpty) return [];

        var n = Chunks.Length;
        var scores = ArrayPool<double>.Shared.Rent(n);
        var masks = ArrayPool<int>.Shared.Rent(n);
        try
        {
            Array.Clear(scores, 0, n);
            Array.Clear(masks, 0, n);
            var touched = new List<int>();
            var k1 = Ranking.K1;
            foreach (var (term, queryWeight) in parsed.Terms)
            {
                var mask = parsed.MaskOf(term);
                var idf = Idf[term] * queryWeight;
                var postingChunks = PostingChunks[term];
                var postingWeights = PostingWeights[term];
                for (var p = 0; p < postingChunks.Length; p++)
                {
                    var c = postingChunks[p];
                    if (masks[c] == 0) touched.Add(c);
                    masks[c] |= mask;
                    var tf = postingWeights[p];
                    scores[c] += idf * tf * (k1 + 1) / (tf + k1);
                }
            }

            var best = new PriorityQueue<int, double>(Ranking.Candidates + 1);
            foreach (var c in touched)
            {
                if ((masks[c] & parsed.ContentMask) == 0) continue;
                if (best.Count >= Ranking.Candidates)
                {
                    if (best.TryPeek(out _, out var lowest) && scores[c] <= lowest) continue;
                    best.Dequeue();
                }
                best.Enqueue(c, scores[c]);
            }
            if (best.Count == 0) return [];
            var candidates = new (int Chunk, double FirstStage, int Mask)[best.Count];
            for (var i = 0; best.TryDequeue(out var c, out var score); i++) candidates[i] = (c, score, masks[c]);
            return GuideReranker.Rerank(this, parsed, candidates, max, explain);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(scores);
            ArrayPool<int>.Shared.Return(masks);
        }
    }

    private double InverseFrequency(int documentFrequency) =>
        Math.Log(1 + (Chunks.Length - documentFrequency + 0.5) / (documentFrequency + 0.5));

    private static double Average(FieldTerms[] fields) =>
        fields.Length == 0 ? 1 : Math.Max(1, fields.Average(f => (double)f.Sequence.Length));

    // "Iron Ore › Locations" on the page "Iron Ore" → "Locations": the page's own title counts once, in the title field.
    private static string SectionWithoutTitle(string section, string page)
    {
        if (string.IsNullOrEmpty(section)) return "";
        var parts = section.Split(" › ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts.Where(p => !string.Equals(p, page.Trim(), StringComparison.OrdinalIgnoreCase)));
    }
}
