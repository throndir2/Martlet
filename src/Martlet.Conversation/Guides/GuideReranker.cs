namespace Martlet.Conversation.Guides;

/// <summary>The second stage of a guide search: scores the first stage's candidates again and orders them for the notes. With
/// no model, in this process and in microseconds, it measures for each candidate:
/// <list type="bullet">
/// <item><b>coverage</b>: the IDF-weighted share of the question's words the chunk has (in any field). A word the guide doesn't
/// have at all counts as a fairly rare word the chunk is missing, so a question about something else stays unsure;</item>
/// <item><b>proximity</b>: how small the window of the chunk's text that holds all of its matched words is (the "minimum cover"
/// of proximity scoring);</item>
/// <item><b>phrases</b>: the share of neighbouring question words that are neighbours in the chunk too ("iron ore"), or that it
/// writes as one word ("fireball" for "fire ball");</item>
/// <item><b>headings</b>: the share of the question that the page title or section heading names;</item>
/// <item><b>specificity</b>: whether a matched word is rare enough in the guide (or named by a heading) to mean something.</item>
/// </list>
/// Relevance = coverage^e × specificity × (floor + (1 − floor) × closeness), closeness being the best of proximity, phrases and
/// headings (1 for a one-word question). The final order mixes the first stage's score with these, then maximal marginal
/// relevance leaves out near-duplicates and lets other pages in before a third chunk of one page.</summary>
internal static class GuideReranker
{
    private const int MaximumPositions = 64;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static IReadOnlyList<GuideHit> Rerank(LexicalGuideIndex index, GuideQuery query, (int Chunk, double FirstStage, int Mask)[] candidates,
        int max, Action<GuideChunk, string>? explain = null)
    {
        var ranking = index.Ranking;
        var termMasks = new Dictionary<int, int>(query.Terms.Length);
        foreach (var (term, _) in query.Terms) termMasks[term] = query.MaskOf(term);
        var contentConcepts = System.Numerics.BitOperations.PopCount((uint)query.ContentMask);
        var topScore = 0.0;
        foreach (var candidate in candidates) topScore = Math.Max(topScore, candidate.FirstStage);

        var scored = new (int Chunk, double Score, double Relevance)[candidates.Length];
        double softTotal = 0;
        for (var c = 0; c < query.Concepts.Length; c++)
            if ((query.SoftMask & (1 << c)) != 0) softTotal += query.Concepts[c].Weight;
        for (var i = 0; i < candidates.Length; i++)
        {
            var (chunk, firstStage, mask) = candidates[i];
            var matched = mask & query.ContentMask;
            var (titleMask, titleExact) = HeadingMask(index.Titles[chunk], termMasks);
            var (sectionMask, sectionExact) = HeadingMask(index.Sections[chunk], termMasks);
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
            var proximity = Proximity(index.Bodies[chunk], query, matched & ~headings);
            if (proximity == 0 && System.Numerics.BitOperations.PopCount((uint)matched) >= 2 &&
                System.Numerics.BitOperations.PopCount((uint)(matched & ~headings)) <= 1)
                proximity = 1; // the heading names the others: the text under it is about them
            var phrases = Phrases(index, chunk, query, matched);
            var closeness = contentConcepts <= 1 ? 1 : Math.Max(proximity, Math.Max(phrases, headingShare));
            var relevance = Math.Pow(coverage, ranking.CoverageExponent) * Math.Min(1, specific) *
                (ranking.ClosenessFloor + (1 - ranking.ClosenessFloor) * closeness);
            relevance = Math.Clamp(relevance, 0, 1);

            var score = ranking.FirstStageShare * (topScore > 0 ? firstStage / topScore : 0) + ranking.RelevanceShare * relevance +
                ranking.ProximityShare * (contentConcepts <= 1 ? 0 : proximity) + ranking.PhraseShare * phrases +
                ranking.HeadingShare * headingShare + ranking.SoftShare * softShare;
            scored[i] = (chunk, score, relevance);
            explain?.Invoke(index.Chunks[chunk], $"first {firstStage:0.00} cover {coverage:0.00} spec {Math.Min(1, specific):0.00} " +
                $"prox {proximity:0.00} phrase {phrases:0.00} head {headingShare:0.00} soft {softShare:0.00} → rel {relevance:0.00} score {score:0.000}");
        }
        Array.Sort(scored, (a, b) =>
        {
            var order = b.Score.CompareTo(a.Score);
            return order != 0 ? order : a.Chunk.CompareTo(b.Chunk);
        });
        return Diversify(index, scored, max);
    }

    private static (int Mask, bool Exact) HeadingMask(FieldTerms field, Dictionary<int, int> termMasks)
    {
        var mask = 0;
        var exact = field.Sequence.Length > 0;
        foreach (var term in field.Sequence)
            if (termMasks.TryGetValue(term, out var m)) mask |= m;
            else exact = false;
        return (mask, exact);
    }

    // The smallest window of the text holding every matched content word, as a closeness from 0 to 1: 1 when they are side by
    // side, ½ when the window has about twice as many other words as matched ones. Each word's positions are already in order,
    // so the window is found by moving through the lists at once (no sort).
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static double Proximity(FieldTerms body, GuideQuery query, int matched)
    {
        Span<int> starts = stackalloc int[GuideQuery.MaximumConcepts];
        Span<int> ends = stackalloc int[GuideQuery.MaximumConcepts];
        int[]?[]? lists = null; // a concept whose several terms are in the text, merged into one list
        var k = 0;
        for (var c = 0; c < query.Concepts.Length; c++)
        {
            if ((matched & (1 << c)) == 0) continue;
            int found = 0, start = 0, end = 0;
            List<int>? merged = null;
            foreach (var term in query.Concepts[c].Terms)
            {
                var at = Array.BinarySearch(body.Ids, term);
                if (at < 0) continue;
                if (++found == 1)
                {
                    (start, end) = (body.Starts[at], body.Starts[at + 1]);
                    continue;
                }
                merged ??= [.. body.Positions.AsSpan(start, end - start)];
                merged.AddRange(body.Positions.AsSpan(body.Starts[at], body.Starts[at + 1] - body.Starts[at]));
            }
            if (found == 0) continue;
            if (merged is not null)
            {
                merged.Sort();
                (lists ??= new int[]?[GuideQuery.MaximumConcepts])[k] = [.. merged];
                (start, end) = (0, merged.Count);
            }
            starts[k] = start;
            ends[k] = Math.Min(end, start + MaximumPositions);
            k++;
        }
        if (k < 2) return 0;

        var window = int.MaxValue;
        while (true)
        {
            int lowest = 0, low = int.MaxValue, high = int.MinValue;
            for (var i = 0; i < k; i++)
            {
                var position = (lists?[i] ?? body.Positions)[starts[i]];
                if (position < low) (low, lowest) = (position, i);
                if (position > high) high = position;
            }
            window = Math.Min(window, high - low + 1);
            if (++starts[lowest] >= ends[lowest]) break;
        }
        var extra = Math.Max(0, window - k);
        return 1 / (1 + extra / (2.0 * k));
    }

    // The share of the question's neighbouring content words that are neighbours (in the question's order) in the chunk's text,
    // title or section, or that the chunk writes as one word.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static double Phrases(LexicalGuideIndex index, int chunk, GuideQuery query, int matched)
    {
        if (query.Pairs.Length == 0) return 0;
        var found = 0;
        foreach (var (a, b, compound) in query.Pairs)
        {
            if ((matched & (1 << a)) == 0 || (matched & (1 << b)) == 0) continue;
            if (compound >= 0 && (index.Bodies[chunk].Has(compound) || index.Titles[chunk].Has(compound) || index.Sections[chunk].Has(compound)))
            {
                found++;
                continue;
            }
            if (Adjacent(index.Bodies[chunk], query.Concepts[a].Terms, query.Concepts[b].Terms) ||
                Adjacent(index.Titles[chunk], query.Concepts[a].Terms, query.Concepts[b].Terms) ||
                Adjacent(index.Sections[chunk], query.Concepts[a].Terms, query.Concepts[b].Terms))
                found++;
        }
        return (double)found / query.Pairs.Length;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static bool Adjacent(FieldTerms field, int[] first, int[] second)
    {
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

    // Maximal marginal relevance over the reranked candidates: each next hit is the best by λ × score − (1 − λ) × its greatest
    // similarity to a hit already taken (word overlap, or being from the same page). Near-duplicates are left out, and a page
    // gives at most PerPage hits until every other candidate has had its turn.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static IReadOnlyList<GuideHit> Diversify(LexicalGuideIndex index, (int Chunk, double Score, double Relevance)[] scored, int max)
    {
        var ranking = index.Ranking;
        var top = scored.Length == 0 ? 0 : scored[0].Score;
        // Only the best few can be chosen at all: a candidate far down the order never wins its place back.
        var pool = Math.Min(scored.Length, 4 * max + 4);
        var remaining = new List<int>(Enumerable.Range(0, pool));
        var similarity = new double[pool];
        var overlap = new double[pool];
        var perPage = new Dictionary<int, int>();
        var hits = new List<GuideHit>(Math.Min(max, scored.Length));
        var deferred = new List<int>();
        var taken = new List<int>();
        while (hits.Count < max && remaining.Count > 0)
        {
            var pick = -1;
            var pickValue = double.NegativeInfinity;
            foreach (var r in remaining)
            {
                var value = ranking.Lambda * (top > 0 ? scored[r].Score / top : 0) - (1 - ranking.Lambda) * similarity[r];
                if (value > pickValue) (pick, pickValue) = (r, value);
            }
            remaining.Remove(pick);
            if (overlap[pick] >= ranking.NearDuplicate) continue;
            var chunk = scored[pick].Chunk;
            var page = index.Pages[chunk];
            if (perPage.GetValueOrDefault(page) >= ranking.PerPage)
            {
                deferred.Add(pick);
                continue;
            }
            perPage[page] = perPage.GetValueOrDefault(page) + 1;
            taken.Add(chunk);
            hits.Add(new(index.Chunks[chunk], scored[pick].Score, scored[pick].Relevance));
            foreach (var r in remaining)
            {
                var other = scored[r].Chunk;
                var words = Jaccard(index.Bodies[chunk].Ids, index.Bodies[other].Ids);
                overlap[r] = Math.Max(overlap[r], words);
                similarity[r] = Math.Max(similarity[r], Math.Max(words, index.Pages[other] == page ? ranking.SamePage : 0));
            }
        }
        foreach (var d in deferred)
        {
            if (hits.Count >= max) break;
            var chunk = scored[d].Chunk;
            if (taken.Any(t => Jaccard(index.Bodies[t].Ids, index.Bodies[chunk].Ids) >= ranking.NearDuplicate)) continue;
            taken.Add(chunk);
            hits.Add(new(index.Chunks[chunk], scored[d].Score, scored[d].Relevance));
        }
        return hits;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static double Jaccard(int[] a, int[] b)
    {
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
}
