using System.Runtime.CompilerServices;

namespace Martlet.Core.Search;

/// <summary>How one source searches (docs/SEARCH.md): its index (fields and their weights, BM25's k1, whether positions are kept),
/// how many first-stage candidates the reranker reads, how the reranker's features make a score, which share of the best score a
/// hit must reach, and how the hits are picked (diversity). Each source (memory, app guides, past conversations) has its own.</summary>
public sealed record SearchProfile
{
    /// <summary>The fields of each document, in order (one field: plain BM25).</summary>
    public required IReadOnlyList<SearchField> Fields { get; init; }
    /// <summary>BM25's term frequency saturation.</summary>
    public double K1 { get; init; } = 1.2;
    /// <summary>Whether the index keeps each field's terms in order (for proximity, phrases and word overlap).</summary>
    public bool Positions { get; init; } = true;
    /// <summary>How many of the first stage's best documents the reranker reads.</summary>
    public int Candidates { get; init; } = 64;
    /// <summary>How many of the best after the first scoring get the costly features (<see cref="ISearchRules.Refine"/>); 0 for
    /// none.</summary>
    public int Refined { get; init; }
    public SearchScoring Scoring { get; init; } = new();
    /// <summary>A hit below this share of the best hit's score is left out (0 keeps every hit).</summary>
    public double Cutoff { get; init; }
    public SearchDiversity Diversity { get; init; } = new();
}

/// <summary>How a candidate's features make its score.</summary>
public enum SearchCombination
{
    /// <summary>The weighted sum of the features: <see cref="SearchScoring.FirstStage"/> × the first-stage score as a share of the
    /// best candidate's, plus each other feature times its weight.</summary>
    Add,
    /// <summary>The first-stage score multiplied by (1 + weight × feature) for each feature (first-stage weight and relevance
    /// don't count).</summary>
    Multiply
}

/// <summary>The weights of the reranker's features. Every feature is from 0 to 1. A source adds features of its own
/// (<see cref="SearchFeatures.Extra"/>) as they are.</summary>
public sealed record SearchScoring
{
    public SearchCombination Combination { get; init; } = SearchCombination.Add;
    public double FirstStage { get; init; }
    public double Relevance { get; init; }
    public double Coverage { get; init; }
    public double Proximity { get; init; }
    public double Phrases { get; init; }
    public double Recency { get; init; }
    /// <summary>How fast <see cref="SearchFeatures.Recency"/> falls: it halves every this many days (<see cref="SearchMeasures.Recency"/>).</summary>
    public double RecencyHalfLifeDays { get; init; } = 30;
    public double Time { get; init; }

    /// <summary>The score of <paramref name="features"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public double Combine(in SearchFeatures features) => Combination == SearchCombination.Multiply
        ? features.FirstStage * (1 + Coverage * features.Coverage) * (1 + Proximity * features.Proximity) *
            (1 + Phrases * features.Phrases) * (1 + Recency * features.Recency) * (1 + Time * features.Time) + features.Extra
        : FirstStage * features.FirstStageShare + Relevance * features.Relevance + Coverage * features.Coverage +
            Proximity * features.Proximity + Phrases * features.Phrases + Recency * features.Recency + Time * features.Time +
            features.Extra;
}

/// <summary>How the hits are picked from the reranked candidates.</summary>
public enum SearchSelection
{
    /// <summary>Best score first.</summary>
    None,
    /// <summary>Greedy: the best, then the best of the rest, where a near-duplicate of a hit already picked counts
    /// <see cref="SearchDiversity.Penalty"/> of its score (once). Scores only go down, so a shorter list is the start of a longer
    /// one.</summary>
    Penalty,
    /// <summary>Maximal marginal relevance (Carbonell and Goldstein, 1998): the next hit is the best by score ÷ best score −
    /// <see cref="SearchDiversity.Weight"/> × its greatest similarity to a hit already picked; a near-duplicate of a picked hit is
    /// left out; a group (a page) gives at most <see cref="SearchDiversity.PerGroup"/> hits until every other candidate has had its
    /// turn.</summary>
    Mmr
}

/// <summary>How the hits are picked (<see cref="SearchSelection"/>).</summary>
public sealed record SearchDiversity
{
    public SearchSelection Selection { get; init; } = SearchSelection.None;
    /// <summary>Two candidates at least this similar (<see cref="ISearchRules.Similarity"/>) are near-duplicates.</summary>
    public double NearDuplicate { get; init; } = 0.8;
    /// <summary><see cref="SearchSelection.Penalty"/>: the share of its score a near-duplicate of a picked hit keeps.</summary>
    public double Penalty { get; init; } = 0.25;
    /// <summary><see cref="SearchSelection.Mmr"/>: how much the similarity to a picked hit lowers a candidate's value.</summary>
    public double Weight { get; init; }
    /// <summary><see cref="SearchSelection.Mmr"/>: the similarity of two candidates of one group (<see cref="ISearchRules.Group"/>).</summary>
    public double SameGroup { get; init; }
    /// <summary><see cref="SearchSelection.Mmr"/>: the most hits of one group before every other candidate has had its turn.</summary>
    public int PerGroup { get; init; } = int.MaxValue;
    /// <summary><see cref="SearchSelection.Mmr"/>: hits are picked from the best <see cref="Pool"/> + <see cref="PoolPerHit"/> ×
    /// the hits wanted (a candidate far down never wins its place back).</summary>
    public int Pool { get; init; } = int.MaxValue;
    public int PoolPerHit { get; init; }
}

/// <summary>A candidate's reranking features, each from 0 to 1 except <see cref="FirstStage"/> (the first stage's score) and
/// <see cref="Extra"/> (a source's own features, already weighted). <see cref="Relevance"/> is the source's calibrated "is this
/// an answer" value, given back with the hit.</summary>
public struct SearchFeatures
{
    public double FirstStage;
    public double FirstStageShare;
    public double Relevance;
    public double Coverage;
    public double Proximity;
    public double Phrases;
    public double Recency;
    public double Time;
    public double Extra;
}

/// <summary>What a source tells the ranker about its own documents (a candidate's <see cref="SearchCandidate.Document"/> is the
/// source's number for it). One instance serves one search.</summary>
public interface ISearchRules
{
    /// <summary>Fills in <paramref name="features"/> of <paramref name="candidate"/> (the first-stage values are set); false leaves
    /// it out.</summary>
    bool Score(in SearchCandidate candidate, ref SearchFeatures features);

    /// <summary>Fills in the costly features of one of the <see cref="SearchProfile.Refined"/> best.</summary>
    void Refine(in SearchCandidate candidate, ref SearchFeatures features)
    {
    }

    /// <summary>The order of two candidates with the same score (or value): negative when <paramref name="left"/> comes first, 0 to
    /// keep their order.</summary>
    int Tie(in SearchCandidate left, in SearchCandidate right) => 0;

    /// <summary>How similar two candidates are, from 0 to 1 (for diversity and near-duplicates).</summary>
    double Similarity(int left, int right) => 0;

    /// <summary>A candidate's group (its page) for <see cref="SearchDiversity.SameGroup"/> and <see cref="SearchDiversity.PerGroup"/>;
    /// -1 for none.</summary>
    int Group(int document) => -1;

    /// <summary>Whether a candidate is already shown elsewhere (in the request's notes): it is not picked, but its near-duplicates
    /// are left out too.</summary>
    bool Skip(int document) => false;
}

/// <summary>A hit: the candidate's document, score, relevance and matched concepts.</summary>
public readonly record struct SearchHit(int Document, double Score, double Relevance, int Mask);

/// <summary>A search's result: the hits picked, in order; the candidates the selection looked at and either picked or skipped
/// (<see cref="ISearchRules.Skip"/>), in order; and every scored candidate, best first.</summary>
public sealed record SearchRanking(SearchHit[] Hits, int[] Seen, SearchHit[] Ranked)
{
    public static SearchRanking Empty { get; } = new([], [], []);
}

/// <summary>The second stage of a search (docs/SEARCH.md): scores the first stage's candidates again with the source's features
/// (<see cref="ISearchRules"/>) and its <see cref="SearchProfile"/>, optionally fuses in a second ranked list (an embedding search,
/// by reciprocal rank fusion), leaves out what falls below the cutoff and picks the hits (<see cref="SearchSelection"/>).
/// Deterministic: equal scores go by <see cref="ISearchRules.Tie"/>, then by the candidates' order. The methods are compiled fully
/// optimized from the first call, because a user sends too few messages for the runtime's tiered compilation to optimize them.</summary>
public static class SearchRanker
{
    private sealed class Scored(SearchCandidate candidate, int index)
    {
        internal SearchCandidate Candidate = candidate;
        internal SearchFeatures Features;
        internal double Score;
        internal bool Penalized;
        internal readonly int Index = index;

        internal SearchHit Hit => new(Candidate.Document, Score, Features.Relevance, Candidate.Mask);
    }

    /// <summary>The hits of <paramref name="candidates"/> (the first stage's, any order): at most <paramref name="want"/>.
    /// <paramref name="fuse"/>, when given, is a second ranked list of documents (best first) fused with the reranked order by
    /// reciprocal rank fusion (<see cref="SearchFusion"/>) before the hits are picked; a document only it has is scored as a
    /// candidate that matched nothing.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static SearchRanking Rank(IReadOnlyList<SearchCandidate> candidates, SearchProfile profile, ISearchRules rules, int want,
        IReadOnlyList<int>? fuse = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(rules);
        if (want <= 0 || candidates.Count == 0 && fuse is not { Count: > 0 }) return SearchRanking.Empty;
        var found = candidates as SearchCandidate[] ?? [.. candidates];

        var top = 0.0;
        for (var i = 0; i < found.Length; i++) top = Math.Max(top, found[i].Score);
        var scoring = profile.Scoring;
        var scored = new List<Scored>(found.Length);
        for (var i = 0; i < found.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var item = new Scored(found[i], i);
            if (Score(item, top, scoring, rules)) scored.Add(item);
        }
        Comparison<Scored> better = [MethodImpl(MethodImplOptions.AggressiveOptimization)] (left, right) =>
        {
            var order = right.Score.CompareTo(left.Score);
            if (order == 0) order = rules.Tie(left.Candidate, right.Candidate);
            return order != 0 ? order : left.Index.CompareTo(right.Index);
        };
        if (profile.Refined > 0 && scored.Count > 0)
        {
            scored.Sort(better);
            for (var i = 0; i < Math.Min(profile.Refined, scored.Count); i++)
            {
                token.ThrowIfCancellationRequested();
                rules.Refine(scored[i].Candidate, ref scored[i].Features);
                scored[i].Score = scoring.Combine(scored[i].Features);
            }
        }
        scored.Sort(better);
        if (profile.Cutoff > 0 && scored.Count > 0)
        {
            var floor = profile.Cutoff * scored[0].Score;
            scored.RemoveAll(item => item.Score < floor);
        }
        if (fuse is { Count: > 0 })
        {
            var lexical = new int[scored.Count];
            for (var i = 0; i < lexical.Length; i++) lexical[i] = scored[i].Candidate.Document;
            var known = new HashSet<int>(lexical);
            foreach (var document in fuse)
            {
                if (!known.Add(document)) continue;
                var item = new Scored(new(document, 0, 0, found.Length + scored.Count), found.Length + scored.Count);
                if (Score(item, top, scoring, rules)) scored.Add(item);
            }
            var fused = SearchFusion.Reciprocal([lexical, fuse]);
            foreach (var item in scored) item.Score = fused.GetValueOrDefault(item.Candidate.Document);
            scored.Sort(better);
        }
        var ranked = new SearchHit[scored.Count];
        for (var i = 0; i < ranked.Length; i++) ranked[i] = scored[i].Hit;
        switch (profile.Diversity.Selection)
        {
            case SearchSelection.Penalty:
                return Penalize(scored, profile.Diversity, rules, want, ranked, better, token);
            case SearchSelection.Mmr:
                return Diversify(scored, profile.Diversity, rules, want, ranked, token);
            default:
                var hits = ranked[..Math.Min(want, ranked.Length)];
                var seen = new int[hits.Length];
                for (var i = 0; i < hits.Length; i++) seen[i] = hits[i].Document;
                return new(hits, seen, ranked);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Score(Scored item, double top, SearchScoring scoring, ISearchRules rules)
    {
        item.Features.FirstStage = item.Candidate.Score;
        item.Features.FirstStageShare = top > 0 ? item.Candidate.Score / top : 0;
        if (!rules.Score(item.Candidate, ref item.Features)) return false;
        item.Score = scoring.Combine(item.Features);
        return true;
    }

    // The best, then the best of the rest; a near-duplicate of a hit picked keeps a share of its score (once). `scored` is in
    // order and only penalized scores change, so the best left is the first one not penalized or one of the few penalized.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static SearchRanking Penalize(List<Scored> scored, SearchDiversity diversity, ISearchRules rules, int want, SearchHit[] ranked,
        Comparison<Scored> better, CancellationToken token)
    {
        var taken = new bool[scored.Count];
        var penalized = new List<int>();
        var hits = new SearchHit[Math.Min(want, scored.Count)];
        var seen = new int[hits.Length];
        var next = 0;
        for (var count = 0; count < hits.Length; count++)
        {
            token.ThrowIfCancellationRequested();
            while (next < scored.Count && (taken[next] || scored[next].Penalized)) next++;
            var best = next;
            var at = -1;
            for (var i = 0; i < penalized.Count; i++)
                if (best == scored.Count || better(scored[penalized[i]], scored[best]) < 0)
                    (best, at) = (penalized[i], i);
            if (at >= 0) penalized.RemoveAt(at);
            taken[best] = true;
            var chosen = scored[best];
            hits[count] = chosen.Hit;
            seen[count] = chosen.Candidate.Document;
            for (var i = next; i < scored.Count; i++)
            {
                var other = scored[i];
                if (taken[i] || other.Penalized ||
                    rules.Similarity(chosen.Candidate.Document, other.Candidate.Document) < diversity.NearDuplicate)
                    continue;
                other.Penalized = true;
                other.Score *= diversity.Penalty;
                penalized.Add(i);
            }
        }
        return new(hits, seen, ranked);
    }

    // Maximal marginal relevance over the best few.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static SearchRanking Diversify(List<Scored> scored, SearchDiversity diversity, ISearchRules rules, int want, SearchHit[] ranked,
        CancellationToken token)
    {
        if (scored.Count == 0) return new([], [], ranked);
        var top = scored[0].Score;
        var pool = (int)Math.Min(scored.Count, (long)diversity.Pool + (long)diversity.PoolPerHit * want);
        var open = new List<int>(pool);
        for (var i = 0; i < pool; i++) open.Add(i);
        var similar = new double[pool];
        var perGroup = new Dictionary<int, int>();
        var picked = new List<int>(Math.Min(want, pool));
        var seen = new List<int>();
        var deferred = new List<int>();
        while (picked.Count < want && open.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var at = 0;
            var value = Value(open[0]);
            for (var i = 1; i < open.Count; i++)
            {
                var other = Value(open[i]);
                if (other > value || other == value && rules.Tie(scored[open[i]].Candidate, scored[open[at]].Candidate) < 0)
                    (at, value) = (i, other);
            }
            var choice = open[at];
            open.RemoveAt(at);
            var document = scored[choice].Candidate.Document;
            var skipped = rules.Skip(document);
            var group = skipped ? -1 : rules.Group(document);
            if (!skipped && group >= 0 && perGroup.GetValueOrDefault(group) >= diversity.PerGroup)
            {
                deferred.Add(choice);
                continue;
            }
            seen.Add(document);
            if (!skipped)
            {
                if (group >= 0) perGroup[group] = perGroup.GetValueOrDefault(group) + 1;
                picked.Add(choice);
            }
            for (var i = open.Count - 1; i >= 0; i--)
            {
                var other = scored[open[i]].Candidate.Document;
                var similarity = rules.Similarity(document, other);
                if (similarity >= diversity.NearDuplicate)
                {
                    open.RemoveAt(i);
                    continue;
                }
                if (skipped) continue;
                if (group >= 0 && rules.Group(other) == group) similarity = Math.Max(similarity, diversity.SameGroup);
                similar[open[i]] = Math.Max(similar[open[i]], similarity);
            }
        }
        foreach (var choice in deferred)
        {
            if (picked.Count >= want) break;
            var document = scored[choice].Candidate.Document;
            var duplicate = false;
            foreach (var p in picked)
                duplicate |= rules.Similarity(scored[p].Candidate.Document, document) >= diversity.NearDuplicate;
            if (duplicate) continue;
            picked.Add(choice);
            seen.Add(document);
        }
        var hits = new SearchHit[picked.Count];
        for (var i = 0; i < hits.Length; i++) hits[i] = scored[picked[i]].Hit;
        return new(hits, [.. seen], ranked);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        double Value(int i) => (top > 0 ? scored[i].Score / top : 0) - diversity.Weight * similar[i];
    }
}