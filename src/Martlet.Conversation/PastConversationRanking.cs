using Martlet.Core.Search;
using Martlet.Core.Text;

namespace Martlet.Conversation;

/// <summary>How <see cref="PastConversations"/> ranks earlier exchanges for a message or a search_conversations call, on the shared
/// search engine (<see cref="SearchIndex"/> and <see cref="SearchRanker"/> in Martlet.Core, docs/SEARCH.md): in memory, in about a
/// millisecond, with no model, embedding or other process. First BM25 over the <see cref="SearchTerms"/> stems of what was said
/// (<see cref="ConversationHistory.Candidates"/>: "plants" finds "plant", "baking" finds "baked"). Then a deterministic reranker
/// scores each candidate on:
/// <list type="bullet">
/// <item>coverage: how much of what was asked it has, each word weighted by how rare it is in the record (a word never said
/// counts as the rarest), with the speaker's name counting as said and words the exchanges around it in its conversation have
/// counting half (a follow-up such as "Where should we stay?" belongs to the trip it follows);</item>
/// <item>proximity: the asked words close together on one side of the exchange, and exact phrases (two asked words one after the
/// other, as in "Red Dragon");</item>
/// <item>time: inside the time the message names, or near it (people misremember "yesterday" by an evening), and a light
/// preference for what is recent.</item>
/// </list>
/// A message's recall keeps only exchanges that match enough of it (<see cref="Accepted"/>) and score at least half the best.
/// Picking the best (maximal marginal relevance) leaves out an exchange nearly the same as one already picked or already in the
/// request's notes, and lowers one that is like those, so several near-identical exchanges don't fill the notes. When there is
/// room left, the exchange right after a picked one in its conversation follows it when it takes up the reply (the user answers
/// with some of its words), since the answer often comes next.</summary>
internal static class PastConversationRanking
{
    /// <summary>What to rank for: the asked stems in order (for phrases), each once (at most
    /// <see cref="ConversationHistory.MaximumQueryTerms"/>), the time asked about, and whether it is a message's automatic recall
    /// (which must match enough of the message) or a search the model asked for.</summary>
    internal sealed record Query(IReadOnlyList<string> Sequence, IReadOnlyList<string> Terms, PastConversations.Span? Window, bool Recall);

    /// <summary>How a search_conversations call ranks. BM25 over one field, what the user and Martlet said (no positions are kept:
    /// a record holds up to 100,000 exchanges). The score adds the BM25 score as a share of the best (1.0), coverage (2.0),
    /// recency (0.2; it halves about every three weeks, only a tie-breaker between matches that are otherwise alike), the time
    /// asked about (1.0), and for the 48 best, proximity and phrases (0.5 each), read from their words. Picking: maximal marginal
    /// relevance over the best 30 (weight 0.3), near-identical exchanges (80% of the same words) left out.</summary>
    internal static SearchProfile Found { get; } = new()
    {
        Fields = [new("said", 1, 0.75)],
        K1 = 1.2,
        Positions = false,
        Candidates = ConversationHistory.MaximumCandidates,
        Refined = 48,
        Scoring = new()
        {
            FirstStage = 1.0, Coverage = 2.0, Proximity = 0.5, Phrases = 0.5, Recency = 0.2, RecencyHalfLifeDays = 30 * Math.Log(2),
            Time = 1.0
        },
        Diversity = new() { Selection = SearchSelection.Mmr, Weight = 0.3, NearDuplicate = 0.8, Pool = 30 }
    };

    /// <summary>How a message's automatic recall ranks: as <see cref="Found"/>, keeping only exchanges that score at least half the
    /// best.</summary>
    internal static SearchProfile Recalled { get; } = Found with { Cutoff = 0.5 };

    private const double NearbyShare = 0.5;
    private const double OwnCoverageAccepted = 0.6;
    private const int Followed = 2;
    private const int SideCharacters = 4_000;

    /// <summary>The exchanges <paramref name="query"/> brings back, best first: at most <paramref name="want"/> picked with
    /// diversity (and those following them), then, beyond that, the rest of the candidates by score. Leaves out the
    /// <paramref name="exclude"/> conversation, what <see cref="PastConversations.Recallable"/> leaves out and those
    /// <paramref name="skip"/> picks (already in the request's notes).</summary>
    internal static IReadOnlyList<HistoryExchange> Rank(ConversationHistory history, Query query, DateTimeOffset now, Guid? exclude, int want,
        Func<HistoryExchange, bool>? skip)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(query);
        want = Math.Max(1, want);
        if (query.Terms.Count == 0)
            return query.Window is { } span ? Latest(history, span, exclude, want, skip) : [];

        // Near the time named too, scored lower the further from it: at most half its length (at least a day) on each side.
        DateTimeOffset? from = null, to = null;
        var margin = TimeSpan.Zero;
        if (query.Window is { } window)
        {
            margin = TimeSpan.FromTicks(Math.Max(TimeSpan.FromDays(1).Ticks, (window.To - window.From).Ticks / 2));
            from = window.From - margin;
            to = window.To > now ? window.To : Min(window.To + margin, now.AddSeconds(1));
        }
        var found = history.Candidates(query.Terms, from, to, exclude, ConversationHistory.MaximumCandidates, PastConversations.Recallable);
        if (found.Hits.Count == 0) return [];

        var profile = query.Recall ? Recalled : Found;
        var rules = new Rules(found, query, now, margin, profile, skip);
        var candidates = new SearchCandidate[found.Hits.Count];
        for (var i = 0; i < candidates.Length; i++) candidates[i] = new(i, found.Hits[i].Score, found.Hits[i].Matched, i);
        var ranking = SearchRanker.Rank(candidates, profile, rules, want);
        return Follow(ranking, found, rules, profile, want, skip);
    }

    // An exchange matches enough of a message: about half its words (at most three), counting words the exchanges around it
    // have, or most of what the message's words weigh, so one common word alone brings back nothing.
    internal static bool Accepted(int own, int nearby, double ownShare, int needed) =>
        own >= 1 && (own + nearby >= needed || ownShare >= OwnCoverageAccepted);

    /// <summary>One ranking's features: coverage (own and nearby), recency and time for every candidate; proximity and phrases,
    /// from its words, for the best; equal scores go to the newer exchange; similarity is the overlap of the words.</summary>
    private sealed class Rules : ISearchRules
    {
        private readonly HistoryCandidates found;
        private readonly Query query;
        private readonly DateTimeOffset now;
        private readonly TimeSpan margin;
        private readonly SearchProfile profile;
        private readonly Func<HistoryExchange, bool>? skip;
        private readonly Dictionary<string, int> position = new(StringComparer.Ordinal);
        private readonly List<(int First, int Second)> pairs;
        private readonly int asked;
        private readonly double total;
        private readonly int needed;
        private readonly HashSet<string>[] stems;

        internal Rules(HistoryCandidates found, Query query, DateTimeOffset now, TimeSpan margin, SearchProfile profile,
            Func<HistoryExchange, bool>? skip)
        {
            this.found = found;
            this.query = query;
            this.now = now;
            this.margin = margin;
            this.profile = profile;
            this.skip = skip;
            var terms = query.Terms.Take(found.Idf.Length).ToArray();
            for (var i = 0; i < terms.Length; i++) position[terms[i]] = i;
            pairs = Pairs(query.Sequence, position);
            asked = terms.Length;
            total = found.Idf.Sum();
            needed = Math.Min(3, (terms.Length + 1) / 2);
            stems = new HashSet<string>[found.Hits.Count];
        }

        internal HashSet<string> Stems(int document) => stems[document] ?? [];

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public bool Score(in SearchCandidate candidate, ref SearchFeatures features)
        {
            var hit = found.Hits[candidate.Document];
            var own = hit.Matched | Speaker(hit.Exchange.Speaker, position);
            var nearby = hit.Nearby & ~own;
            var ownShare = SearchMeasures.Weight(own, found.Idf) / total;
            if (query.Recall && !Accepted(Count(own), Count(nearby), ownShare, needed)) return false;
            features.Coverage = ownShare + NearbyShare * SearchMeasures.Weight(nearby, found.Idf) / total;
            features.Recency = SearchMeasures.Recency((now - hit.Exchange.At).TotalDays, profile.Scoring.RecencyHalfLifeDays);
            if (query.Window is { } named) features.Time = SearchMeasures.TimeCloseness(hit.Exchange.At, named.From, named.To, margin);
            return true;
        }

        // Proximity and phrases read the words of the best only. They only add to a score, so the exchanges read stay ahead of
        // those not read, and picking (from the first 30) never needs words it didn't read.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public void Refine(in SearchCandidate candidate, ref SearchFeatures features)
        {
            var exchange = found.Hits[candidate.Document].Exchange;
            var user = SearchTerms.Of(Cut(exchange.User));
            var reply = SearchTerms.Of(Cut(exchange.Reply));
            if (asked >= 2)
            {
                int[] userTerms = Sequence(user), replyTerms = Sequence(reply);
                features.Proximity = Math.Max(SearchMeasures.Stretch(userTerms, asked), SearchMeasures.Stretch(replyTerms, asked));
                if (pairs.Count > 0)
                    features.Phrases = (double)pairs.Count(pair => SearchMeasures.Adjacent(userTerms, pair.First, pair.Second) ||
                        SearchMeasures.Adjacent(replyTerms, pair.First, pair.Second)) / pairs.Count;
            }
            var said = new HashSet<string>(user, StringComparer.Ordinal);
            said.UnionWith(reply);
            stems[candidate.Document] = said;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public int Tie(in SearchCandidate left, in SearchCandidate right) =>
            found.Hits[right.Document].Exchange.At.CompareTo(found.Hits[left.Document].Exchange.At);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public double Similarity(int left, int right) => SearchMeasures.Jaccard(Stems(left), Stems(right));

        public bool Skip(int document) => skip?.Invoke(found.Hits[document].Exchange) == true;

        private int[] Sequence(IReadOnlyList<string> side)
        {
            var terms = new int[side.Count];
            for (var at = 0; at < terms.Length; at++) terms[at] = position.GetValueOrDefault(side[at], -1);
            return terms;
        }
    }

    // When there is room, the exchange after one of the first picked follows it when it takes up the reply; then, beyond the
    // picked, the rest of the candidates by score.
    private static IReadOnlyList<HistoryExchange> Follow(SearchRanking ranking, HistoryCandidates found, Rules rules, SearchProfile profile,
        int want, Func<HistoryExchange, bool>? skip)
    {
        var result = ranking.Hits.Select(hit => found.Hits[hit.Document].Exchange).ToList();
        var shown = ranking.Seen.Select(rules.Stems).ToList();
        var taken = new HashSet<Guid>(ranking.Ranked.Select(hit => found.Hits[hit.Document].Exchange.Id));
        foreach (var leader in ranking.Hits.Take(Followed).Select(hit => found.Hits[hit.Document]).ToArray())
        {
            if (result.Count >= want) break;
            if (leader.Next is not { } next || !Continues(leader.Exchange, next) || taken.Contains(next.Id) || result.Any(e => e.Id == next.Id))
                continue;
            var stems = new HashSet<string>(SearchTerms.Of(Cut(next.User)), StringComparer.Ordinal);
            stems.UnionWith(SearchTerms.Of(Cut(next.Reply)));
            if (shown.Any(other => SearchMeasures.Jaccard(other, stems) >= profile.Diversity.NearDuplicate) || skip?.Invoke(next) == true)
                continue;
            result.Add(next);
            shown.Add(stems);
        }
        if (result.Count >= want) return result;
        var pool = Math.Min(ranking.Ranked.Length, profile.Diversity.Pool);
        var listed = new HashSet<Guid>(result.Select(exchange => exchange.Id));
        listed.UnionWith(ranking.Ranked.Take(pool).Select(hit => found.Hits[hit.Document].Exchange.Id));
        result.AddRange(ranking.Ranked.Skip(pool).Select(hit => found.Hits[hit.Document].Exchange)
            .Where(exchange => !listed.Contains(exchange.Id) && skip?.Invoke(exchange) != true));
        return result;
    }

    // The next exchange takes up the reply: the user answers with some of its words ("He'd love the apron" after "a grill brush
    // or a new apron"), not a new subject and not Martlet bringing something up on its own.
    private static bool Continues(HistoryExchange leader, HistoryExchange next)
    {
        if (next.Kind == HistoryInputKind.Report || next.User.Length == 0) return false;
        var reply = new HashSet<string>(SearchTerms.Of(Cut(leader.Reply)), StringComparer.Ordinal);
        return SearchTerms.Of(Cut(next.User)).Any(reply.Contains);
    }

    // With only a time: the latest exchanges of that time, newest first, an exchange nearly the same as a later one left out.
    private static IReadOnlyList<HistoryExchange> Latest(ConversationHistory history, PastConversations.Span span, Guid? exclude, int want,
        Func<HistoryExchange, bool>? skip)
    {
        var kept = new List<HistoryExchange>();
        var shown = new List<HashSet<string>>();
        foreach (var exchange in history.Between(span.From, span.To, exclude, ConversationHistory.MaximumResults, PastConversations.Recallable).Reverse())
        {
            if (kept.Count >= want) break;
            var stems = new HashSet<string>(SearchTerms.Of(Cut(exchange.User)), StringComparer.Ordinal);
            stems.UnionWith(SearchTerms.Of(Cut(exchange.Reply)));
            if (shown.Any(other => SearchMeasures.Jaccard(other, stems) >= Found.Diversity.NearDuplicate)) continue;
            shown.Add(stems);
            if (skip?.Invoke(exchange) != true) kept.Add(exchange);
        }
        return kept;
    }

    private static List<(int First, int Second)> Pairs(IReadOnlyList<string> sequence, Dictionary<string, int> position)
    {
        var pairs = new List<(int, int)>();
        for (var at = 0; at + 1 < sequence.Count; at++)
            if (position.TryGetValue(sequence[at], out var first) && position.TryGetValue(sequence[at + 1], out var second) && first != second &&
                !pairs.Contains((first, second)))
                pairs.Add((first, second));
        return pairs;
    }

    // The asked words the speaker's name is ("What did Ana want...?" and Ana said it).
    private static int Speaker(string? speaker, Dictionary<string, int> position)
    {
        if (string.IsNullOrEmpty(speaker)) return 0;
        var mask = 0;
        foreach (var stem in SearchTerms.Of(speaker))
            if (position.TryGetValue(stem, out var term)) mask |= 1 << term;
        return mask;
    }

    private static int Count(int mask) => System.Numerics.BitOperations.PopCount((uint)mask);

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static string Cut(string text) => text.Length <= SideCharacters ? text : text[..SideCharacters];
}
