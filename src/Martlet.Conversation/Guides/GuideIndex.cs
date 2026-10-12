using Martlet.Core.Search;
using Martlet.Core.Text;

namespace Martlet.Conversation.Guides;

/// <summary>Builds a guide's search index (docs/APP_GUIDES.md, "Searching a guide") on the shared search engine
/// (<see cref="SearchIndex"/> and <see cref="SearchRanker"/> in Martlet.Core, docs/SEARCH.md). A search runs in two stages, in this
/// process and with no model: BM25F over each chunk's page title, section headings and text picks candidates, then a reranker
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

    /// <summary>The guide's search profile on the shared search engine (docs/SEARCH.md), for tools that show it.</summary>
    public static SearchProfile Profile => GuideRanking.Default.Search;
}

/// <summary>How a guide is searched, tuned on the evaluation fixture (tests\Martlet.Conversation.Tests\Fixtures): the search
/// engine's profile (<see cref="Search"/>) and the guide's own reranker values.</summary>
internal sealed record GuideRanking
{
    public static GuideRanking Default { get; } = new();

    /// <summary>The engine's part. BM25F over three fields (per-field weight and length normalization, then one saturation, k1, of
    /// the weighted term frequency): the page title, the section headings and the text. The best <see cref="SearchProfile.Candidates"/>
    /// go to the reranker, whose score mixes the first stage's (scaled to the best candidate), relevance, proximity and phrases
    /// (with headings and soft words, <see cref="HeadingShare"/> and <see cref="SoftShare"/>). Diversity: maximal marginal
    /// relevance (weight 0.25, which is λ 0.8), chunks of one page this similar, near-duplicates left out, at most two chunks of a
    /// page before every other page has had its turn, picked from the best 4 + 4 × the hits wanted.</summary>
    public SearchProfile Search { get; init; } = new()
    {
        Fields = [new("title", 2.5, 0.3), new("section", 3.5, 0.4), new("text", 1.0, 0.6)],
        K1 = 1.2,
        Candidates = 40,
        Scoring = new() { FirstStage = 0.2, Relevance = 0.3, Proximity = 0.1, Phrases = 0.05 },
        Diversity = new()
        {
            Selection = SearchSelection.Mmr, Weight = 0.25, SameGroup = 0.35, NearDuplicate = 0.8, PerGroup = 2, Pool = 4, PoolPerHit = 4
        }
    };

    /// <summary>A soft word's (question scaffolding, the app's name) share of a full word's score.</summary>
    public double SoftWeight { get; init; } = 0.3;
    /// <summary>A word the guide doesn't have weighs this many reference IDFs (the IDF of a word in one chunk in twenty).</summary>
    public double UnknownWeight { get; init; } = 1.0;
    /// <summary>How informative an everyday chat word ("bed", "morning") is when only a chunk's text has it.</summary>
    public double EverydayInformative { get; init; } = 0.35;
    /// <summary>The weight of the share of the question that a heading names.</summary>
    public double HeadingShare { get; init; } = 0.05;
    /// <summary>The weight of the share of the question's soft words a chunk has.</summary>
    public double SoftShare { get; init; } = 0.05;

    // Relevance = coverage^CoverageExponent × specificity × (ClosenessFloor + (1 − ClosenessFloor) × closeness).
    public double CoverageExponent { get; init; } = 1.25;
    public double ClosenessFloor { get; init; } = 0.7;
}

/// <summary>The in-memory index of a guide: read-only once built, so searches may run on any thread at once.</summary>
internal sealed class LexicalGuideIndex : IGuideIndex
{
    private const int TitleField = 0, SectionField = 1, BodyField = 2;

    public LexicalGuideIndex(IReadOnlyList<GuideChunk> source, IEnumerable<string>? appNames, GuideRanking ranking)
    {
        Ranking = ranking;
        Chunks = [.. source];
        AppWords = [.. (appNames ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => SearchTerms.Words(n).ToArray())
            .Where(w => w.Length > 0)];
        var n = Chunks.Length;
        Index = new SearchIndex(ranking.Search);
        Pages = new int[n];
        var titles = new Dictionary<string, (FieldTerms Terms, int Page)>(StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
        {
            var chunk = Chunks[i] ?? throw new ArgumentException("A guide chunk is missing.", nameof(source));
            var pageKey = chunk.Url + "\n" + chunk.Page;
            if (!titles.TryGetValue(pageKey, out var title))
                titles[pageKey] = title = (Index.Terms(SearchTerms.Of(chunk.Page)), titles.Count);
            Pages[i] = title.Page;
            var section = Index.Terms(SearchTerms.Of(SectionWithoutTitle(chunk.Section, chunk.Page)));
            Index.Add(title.Terms, section, Index.Terms(SearchTerms.Of(chunk.Text)));
        }
        Index.Freeze();
        ReferenceIdf = Index.InverseFrequency(Math.Max(1, (int)Math.Round(n * 0.05)));
    }

    public GuideRanking Ranking { get; }
    public GuideChunk[] Chunks { get; }
    public string[][] AppWords { get; }
    public SearchIndex Index { get; }
    /// <summary>The page of each chunk, numbered.</summary>
    public int[] Pages { get; }
    /// <summary>The IDF of a term in about one chunk in twenty.</summary>
    public double ReferenceIdf { get; }

    public FieldTerms Title(int chunk) => Index.Terms(chunk, TitleField);
    public FieldTerms Section(int chunk) => Index.Terms(chunk, SectionField);
    public FieldTerms Body(int chunk) => Index.Terms(chunk, BodyField);

    public int Count => Chunks.Length;

    public IReadOnlyList<GuideHit> Search(string query, int max) => Search(query, max, null);

    /// <summary>A search that tells <paramref name="explain"/> each candidate's reranking features (for tuning).</summary>
    // Fully optimized from the first call: a user sends too few messages for the runtime's tiered compilation to optimize it.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    internal IReadOnlyList<GuideHit> Search(string query, int max, Action<GuideChunk, string>? explain)
    {
        if (max <= 0 || Chunks.Length == 0) return [];
        var parsed = GuideQuery.Parse(query, Index.Vocabulary, Index.Idf, ReferenceIdf, AppWords, Ranking);
        if (parsed.IsEmpty) return [];

        var terms = new SearchQueryTerm[parsed.Terms.Length];
        for (var i = 0; i < terms.Length; i++)
            terms[i] = new(parsed.Terms[i].Term, parsed.Terms[i].Weight, parsed.MaskOf(parsed.Terms[i].Term));
        var candidates = Index.Search(new SearchQuery(terms), Ranking.Search.Candidates, any: parsed.ContentMask, ordered: false);
        if (candidates.Length == 0) return [];
        var ranking = SearchRanker.Rank(candidates, Ranking.Search, new GuideReranker(this, parsed, explain), max);
        var hits = new GuideHit[ranking.Hits.Length];
        for (var i = 0; i < hits.Length; i++)
        {
            var hit = ranking.Hits[i];
            hits[i] = new(Chunks[hit.Document], hit.Score, hit.Relevance);
        }
        return hits;
    }

    // "Iron Ore › Locations" on the page "Iron Ore" → "Locations": the page's own title counts once, in the title field.
    private static string SectionWithoutTitle(string section, string page)
    {
        if (string.IsNullOrEmpty(section)) return "";
        var parts = section.Split(" › ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts.Where(p => !string.Equals(p, page.Trim(), StringComparison.OrdinalIgnoreCase)));
    }
}
