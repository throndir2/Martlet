using Martlet.Core.Text;

namespace Martlet.Conversation;

/// <summary>How <see cref="PastConversations"/> ranks earlier exchanges for a message or a search_conversations call: in memory,
/// in about a millisecond, with no model, embedding or other process. First BM25 over the <see cref="SearchTerms"/> stems of what
/// was said (<see cref="ConversationHistory.Candidates"/>: "plants" finds "plant", "baking" finds "baked"). Then a deterministic
/// reranker scores each candidate on:
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

    private const double BestMatchWeight = 1.0, CoverageWeight = 2.0, ProximityWeight = 0.5, PhraseWeight = 0.5, RecencyWeight = 0.2,
        TimeWeight = 1.0;
    // Recency halves about every three weeks: only a tie-breaker between matches that are otherwise alike.
    private const double RecencyDays = 30;
    private const double NearbyShare = 0.5;
    private const double Cutoff = 0.5;
    private const double OwnCoverageAccepted = 0.6;
    private const double SameExchange = 0.8;
    private const double Diversity = 0.3;
    private const int Diversified = 30;
    // How many of the best have their words read for proximity, phrases and near-identical exchanges (at least Diversified).
    private const int Read = 48;
    private const int Followed = 2;
    private const int SideCharacters = 4_000;

    private sealed class Scored(HistoryCandidate hit)
    {
        internal HistoryCandidate Hit { get; } = hit;
        internal HistoryExchange Exchange => Hit.Exchange;
        internal HashSet<string> Stems { get; set; } = [];
        internal double Score { get; set; }
    }

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

        var terms = query.Terms.Take(found.Idf.Count).ToArray();
        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < terms.Length; i++) position[terms[i]] = i;
        var pairs = Pairs(query.Sequence, position);
        var idf = found.Idf;
        var total = idf.Sum();
        var needed = Math.Min(3, (terms.Length + 1) / 2);
        var bestScore = found.Hits.Max(hit => hit.Score);

        var scored = new List<Scored>(found.Hits.Count);
        foreach (var hit in found.Hits)
        {
            var own = hit.Matched | Speaker(hit.Exchange.Speaker, position);
            var nearby = hit.Nearby & ~own;
            var ownShare = Weight(own, idf) / total;
            if (query.Recall && !Accepted(Count(own), Count(nearby), ownShare, needed)) continue;
            var coverage = ownShare + NearbyShare * Weight(nearby, idf) / total;
            var recency = Math.Exp(-Math.Max(0, (now - hit.Exchange.At).TotalDays) / RecencyDays);
            var score = BestMatchWeight * hit.Score / bestScore + CoverageWeight * coverage + RecencyWeight * recency;
            if (query.Window is { } named)
                score += TimeWeight * (hit.Exchange.At >= named.From && hit.Exchange.At < named.To ? 1
                    : 1 - Math.Min(1, Distance(hit.Exchange.At, named) / margin.TotalDays));
            scored.Add(new(hit) { Score = score });
        }
        if (scored.Count == 0) return [];
        // Proximity and phrases read the words of the best only. They only add to a score, so the exchanges read stay ahead of
        // those not read, and picking (from the first Diversified) never needs words it didn't read.
        scored.Sort(Better);
        foreach (var candidate in scored.Take(Read))
        {
            var user = SearchTerms.Of(Cut(candidate.Exchange.User));
            var reply = SearchTerms.Of(Cut(candidate.Exchange.Reply));
            var (proximity, phrase) = Closeness(user, reply, position, pairs, terms.Length);
            candidate.Score += ProximityWeight * proximity + PhraseWeight * phrase;
            candidate.Stems = new HashSet<string>(user, StringComparer.Ordinal);
            candidate.Stems.UnionWith(reply);
        }
        scored.Sort(Better);
        if (query.Recall)
        {
            var floor = Cutoff * scored[0].Score;
            scored.RemoveAll(candidate => candidate.Score < floor);
        }
        return Pick(scored, want, skip);
    }

    private static int Better(Scored a, Scored b) =>
        b.Score != a.Score ? b.Score.CompareTo(a.Score) : b.Exchange.At.CompareTo(a.Exchange.At);

    // An exchange matches enough of a message: about half its words (at most three), counting words the exchanges around it
    // have, or most of what the message's words weigh, so one common word alone brings back nothing.
    internal static bool Accepted(int own, int nearby, double ownShare, int needed) =>
        own >= 1 && (own + nearby >= needed || ownShare >= OwnCoverageAccepted);

    private static IReadOnlyList<HistoryExchange> Pick(List<Scored> ranked, int want, Func<HistoryExchange, bool>? skip)
    {
        var pool = ranked.Take(Diversified).ToList();
        var similar = new double[pool.Count];
        var open = Enumerable.Range(0, pool.Count).ToList();
        var picked = new List<Scored>();
        var shown = new List<HashSet<string>>();
        var top = pool[0].Score;
        while (picked.Count < want && open.Count > 0)
        {
            var choice = open.MaxBy(i => (pool[i].Score - Diversity * top * similar[i], pool[i].Exchange.At));
            open.Remove(choice);
            var candidate = pool[choice];
            var skipped = skip?.Invoke(candidate.Exchange) == true;
            if (!skipped) picked.Add(candidate);
            shown.Add(candidate.Stems);
            for (var at = open.Count - 1; at >= 0; at--)
            {
                var sameness = Jaccard(pool[open[at]].Stems, candidate.Stems);
                if (sameness >= SameExchange) open.RemoveAt(at);
                else if (!skipped) similar[open[at]] = Math.Max(similar[open[at]], sameness);
            }
        }

        var result = picked.Select(candidate => candidate.Exchange).ToList();
        var taken = new HashSet<Guid>(ranked.Select(candidate => candidate.Exchange.Id));
        foreach (var leader in picked.Take(Followed).ToArray())
        {
            if (result.Count >= want) break;
            if (leader.Hit.Next is not { } next || !Continues(leader.Exchange, next) || taken.Contains(next.Id) || result.Any(e => e.Id == next.Id))
                continue;
            var stems = new HashSet<string>(SearchTerms.Of(Cut(next.User)), StringComparer.Ordinal);
            stems.UnionWith(SearchTerms.Of(Cut(next.Reply)));
            if (shown.Any(other => Jaccard(other, stems) >= SameExchange) || skip?.Invoke(next) == true) continue;
            result.Add(next);
            shown.Add(stems);
        }
        if (result.Count >= want) return result;
        var listed = new HashSet<Guid>(result.Select(exchange => exchange.Id));
        listed.UnionWith(pool.Select(candidate => candidate.Exchange.Id));
        result.AddRange(ranked.Skip(Diversified).Select(candidate => candidate.Exchange)
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
            if (shown.Any(other => Jaccard(other, stems) >= SameExchange)) continue;
            shown.Add(stems);
            if (skip?.Invoke(exchange) != true) kept.Add(exchange);
        }
        return kept;
    }

    // How close the asked words are on one side of the exchange (all of them next to each other: 1), and the share of the
    // asked word pairs that appear one right after the other.
    private static (double Proximity, double Phrase) Closeness(IReadOnlyList<string> user, IReadOnlyList<string> reply,
        Dictionary<string, int> position, IReadOnlyList<(int First, int Second)> pairs, int asked)
    {
        if (asked < 2) return (0, 0);
        var proximity = Math.Max(Proximity(user, position, asked), Proximity(reply, position, asked));
        if (pairs.Count == 0) return (proximity, 0);
        var found = pairs.Count(pair => Adjacent(user, position, pair) || Adjacent(reply, position, pair));
        return (proximity, (double)found / pairs.Count);
    }

    private static double Proximity(IReadOnlyList<string> side, Dictionary<string, int> position, int asked)
    {
        var hits = new List<(int At, int Term)>();
        for (var at = 0; at < side.Count; at++)
            if (position.TryGetValue(side[at], out var term)) hits.Add((at, term));
        var distinct = hits.Select(hit => hit.Term).Distinct().Count();
        if (distinct < 2) return 0;
        // The shortest stretch holding every asked word this side has.
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

    private static bool Adjacent(IReadOnlyList<string> side, Dictionary<string, int> position, (int First, int Second) pair)
    {
        for (var at = 0; at + 1 < side.Count; at++)
            if (position.GetValueOrDefault(side[at], -1) == pair.First && position.GetValueOrDefault(side[at + 1], -1) == pair.Second)
                return true;
        return false;
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

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var shared = a.Count <= b.Count ? a.Count(b.Contains) : b.Count(a.Contains);
        return (double)shared / (a.Count + b.Count - shared);
    }

    private static double Weight(int mask, IReadOnlyList<double> idf)
    {
        var weight = 0.0;
        for (var term = 0; term < idf.Count; term++)
            if ((mask & 1 << term) != 0) weight += idf[term];
        return weight;
    }

    private static int Count(int mask) => System.Numerics.BitOperations.PopCount((uint)mask);

    private static double Distance(DateTimeOffset at, PastConversations.Span window) =>
        at < window.From ? (window.From - at).TotalDays : (at - window.To).TotalDays;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static string Cut(string text) => text.Length <= SideCharacters ? text : text[..SideCharacters];
}
