using Martlet.Core.Search;

namespace Martlet.Conversation.Guides;

/// <summary>The guide's features for the second stage of a search (<see cref="SearchRanker"/>), which scores the first stage's
/// candidates again and orders them for the notes. With no model, in this process and in microseconds, it measures for each
/// candidate:
/// <list type="bullet">
/// <item><b>coverage</b>: the IDF-weighted share of the question's words the chunk has (in any field). A word the guide doesn't
/// have at all counts as a fairly rare word the chunk is missing, so a question about something else stays unsure;</item>
/// <item><b>proximity</b>: how small the window of the chunk's text that holds all of its matched words is (the "minimum cover"
/// of proximity scoring, <see cref="SearchMeasures.MinimumCover"/>);</item>
/// <item><b>phrases</b>: the share of neighbouring question words that are neighbours in the chunk too ("iron ore"), or that it
/// writes as one word ("fireball" for "fire ball");</item>
/// <item><b>headings</b>: the share of the question that the page title or section heading names;</item>
/// <item><b>specificity</b>: whether a matched word is rare enough in the guide (or named by a heading) to mean something.</item>
/// </list>
/// Relevance = coverage^e × specificity × (floor + (1 − floor) × closeness), closeness being the best of proximity, phrases and
/// headings (1 for a one-word question). The final order mixes the first stage's score with these (the guide's
/// <see cref="GuideRanking.Search"/> profile), then maximal marginal relevance leaves out near-duplicates (by the words of the
/// text) and lets other pages in before a third chunk of one page. Equal scores go to the earlier chunk.</summary>
internal sealed class GuideReranker(LexicalGuideIndex index, GuideQuery query, Action<GuideChunk, string>? explain) : ISearchRules
{
    private readonly GuideRanking ranking = index.Ranking;
    private readonly Dictionary<int, int> termMasks = TermMasks(query);
    private readonly int contentConcepts = System.Numerics.BitOperations.PopCount((uint)query.ContentMask);
    private readonly double softTotal = SoftTotal(query);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public bool Score(in SearchCandidate candidate, ref SearchFeatures features)
    {
        var (chunk, mask) = (candidate.Document, candidate.Mask);
        var matched = mask & query.ContentMask;
        var (titleMask, titleExact) = HeadingMask(index.Title(chunk));
        var (sectionMask, sectionExact) = HeadingMask(index.Section(chunk));
        var headings = (titleMask | sectionMask) & query.ContentMask;
        // A heading that is only the question's words ("Weather") names its subject; an everyday word in a longer heading
        // ("Your first day") is weak evidence, like one in the text.
        var exact = ((titleExact ? titleMask : 0) | (sectionExact ? sectionMask : 0)) & query.ContentMask;

        double covered = 0, named = 0, specific = 0, soft = 0;
        for (var c = 0; c < query.Concepts.Length; c++)
        {
            var bit = 1 << c;
            var concept = query.Concepts[c];
            if ((mask & query.SoftMask & bit) != 0) soft += concept.Weight;
            if ((matched & bit) == 0) continue;
            covered += concept.Weight;
            if ((headings & bit) != 0) named += concept.Weight;
            if ((headings & bit) != 0 && !concept.Weak && (!concept.Common || (exact & bit) != 0)) specific += 1;
            else specific += concept.Common ? Math.Min(concept.Informative, ranking.EverydayInformative) : concept.Informative;
        }
        var coverage = query.ContentWeight <= 0 ? 0 : covered / query.ContentWeight;
        var headingShare = query.ContentWeight <= 0 ? 0 : named / query.ContentWeight;
        var softShare = softTotal <= 0 ? 0 : soft / softTotal;
        var proximity = SearchMeasures.MinimumCover(index.Body(chunk), query.ConceptTerms, matched & ~headings);
        if (proximity == 0 && System.Numerics.BitOperations.PopCount((uint)matched) >= 2 &&
            System.Numerics.BitOperations.PopCount((uint)(matched & ~headings)) <= 1)
            proximity = 1; // the heading names the others: the text under it is about them
        var phrases = Phrases(chunk, matched);
        var closeness = contentConcepts <= 1 ? 1 : Math.Max(proximity, Math.Max(phrases, headingShare));
        var relevance = Math.Pow(coverage, ranking.CoverageExponent) * Math.Min(1, specific) *
            (ranking.ClosenessFloor + (1 - ranking.ClosenessFloor) * closeness);

        features.Relevance = Math.Clamp(relevance, 0, 1);
        features.Proximity = contentConcepts <= 1 ? 0 : proximity;
        features.Phrases = phrases;
        features.Extra = ranking.HeadingShare * headingShare + ranking.SoftShare * softShare;
        explain?.Invoke(index.Chunks[chunk], $"first {candidate.Score:0.00} cover {coverage:0.00} spec {Math.Min(1, specific):0.00} " +
            $"prox {proximity:0.00} phrase {phrases:0.00} head {headingShare:0.00} soft {softShare:0.00} → rel {features.Relevance:0.00} " +
            $"score {ranking.Search.Scoring.Combine(features):0.000}");
        return true;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public int Tie(in SearchCandidate left, in SearchCandidate right) => left.Document.CompareTo(right.Document);

    /// <summary>The overlap of two chunks' text words.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public double Similarity(int left, int right) => SearchMeasures.Jaccard(index.Body(left).Ids, index.Body(right).Ids);

    public int Group(int document) => index.Pages[document];

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private (int Mask, bool Exact) HeadingMask(FieldTerms field)
    {
        var mask = 0;
        var exact = field.Sequence.Length > 0;
        foreach (var term in field.Sequence)
            if (termMasks.TryGetValue(term, out var m)) mask |= m;
            else exact = false;
        return (mask, exact);
    }

    // The share of the question's neighbouring content words that are neighbours (in the question's order) in the chunk's text,
    // title or section, or that the chunk writes as one word.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private double Phrases(int chunk, int matched)
    {
        if (query.Pairs.Length == 0) return 0;
        FieldTerms body = index.Body(chunk), title = index.Title(chunk), section = index.Section(chunk);
        var found = 0;
        foreach (var (a, b, compound) in query.Pairs)
        {
            if ((matched & (1 << a)) == 0 || (matched & (1 << b)) == 0) continue;
            if (compound >= 0 && (body.Has(compound) || title.Has(compound) || section.Has(compound)))
            {
                found++;
                continue;
            }
            int[] first = query.Concepts[a].Terms, second = query.Concepts[b].Terms;
            if (SearchMeasures.Adjacent(body, first, second) || SearchMeasures.Adjacent(title, first, second) ||
                SearchMeasures.Adjacent(section, first, second))
                found++;
        }
        return (double)found / query.Pairs.Length;
    }

    private static Dictionary<int, int> TermMasks(GuideQuery query)
    {
        var masks = new Dictionary<int, int>(query.Terms.Length);
        foreach (var (term, _) in query.Terms) masks[term] = query.MaskOf(term);
        return masks;
    }

    private static double SoftTotal(GuideQuery query)
    {
        double total = 0;
        for (var c = 0; c < query.Concepts.Length; c++)
            if ((query.SoftMask & (1 << c)) != 0) total += query.Concepts[c].Weight;
        return total;
    }
}
