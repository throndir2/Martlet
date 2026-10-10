using System.Text.Json;
using Martlet.Core.Cluster;

namespace Martlet.Core.Singing;

/// <summary>What a computer's singing service says before a song goes there (its <c>status</c> operation): its state (ready,
/// loading, busy while it sings a song, or not_provisioned), how many songs wait in its line, and the voice matches set up
/// there.</summary>
public sealed record SingerState(string? State, int Waiting, IReadOnlyList<SongVoiceMatch> VoiceMatches)
{
    /// <summary>The songs before a new one there: the one it sings now and those waiting.</summary>
    public int Ahead => (State == "busy" ? 1 : 0) + Math.Max(0, Waiting);

    /// <summary>Reads a singing service's <c>status</c> answer. SoulX-Singer is always set up where Singing is.</summary>
    public static SingerState Parse(JsonElement status)
    {
        string? state = null;
        var waiting = 0;
        List<SongVoiceMatch> matches = [SongVoiceMatch.SoulX];
        if (status.ValueKind == JsonValueKind.Object)
        {
            if (status.TryGetProperty("state", out var value) && value.ValueKind == JsonValueKind.String) state = value.GetString();
            if (status.TryGetProperty("queue", out var queue) && queue.ValueKind == JsonValueKind.Number && queue.TryGetInt32(out var count))
                waiting = Math.Max(0, count);
            if (status.TryGetProperty("voice_matches", out var list) && list.ValueKind == JsonValueKind.Array &&
                list.EnumerateArray().Any(m => m.ValueKind == JsonValueKind.String && m.GetString() == "vevosing"))
                matches.Add(SongVoiceMatch.VevoSing);
        }
        return new(state, waiting, matches);
    }
}

/// <summary>A song made on the singing pool: the song, the computer that made it, whether it waited in that computer's line
/// because every computer that sings was busy (<see cref="Queued"/>), and how many songs were before it there.</summary>
public sealed record SungOn<T>(T Result, string HostId, bool Queued, int Ahead);

/// <summary>
/// Singing on a pool of the owner's own computers that run the singing role, instead of one computer. A song goes through
/// Martlet's queue for shared work (<see cref="WorkQueue"/>, lane <see cref="Lane"/>, background priority):
/// <list type="number">
/// <item>Each computer in order is asked for its singing status first. The first that sings nothing now and has the song's
/// voice match takes the song. While the first computer is free, nothing else is asked.</item>
/// <item>A computer that sings another song is passed over for the next. A computer that doesn't answer, doesn't sing,
/// isn't set up, or lacks the song's voice match (VevoSing is set up only on some) is passed over too.</item>
/// <item>When every computer that can sing it is busy, the song waits in line on the one with the fewest songs before it
/// (the first of those in order). The singing host keeps the line, so this PC doesn't ask again and again.</item>
/// <item>A computer whose line is full (<c>singing.busy</c>), or that stops answering during the song, is passed over for the
/// next; a song that fails on the computer (<c>song.failed</c>) is not made again elsewhere.</item>
/// </list>
/// Hosts that friends share are never in the pool: the caller gives only the owner's own computers.
/// </summary>
public static class SingingPool
{
    /// <summary>The <see cref="WorkQueue"/> lane of songs.</summary>
    public const string Lane = "singing";

    /// <summary>The order songs try the owner's own computers: <paramref name="saved"/> (the computer Martlet sings on,
    /// Companion › Singing) first, then those the shared plan says run Singing (<paramref name="singers"/>, fewest plan jobs
    /// first, then by ID), then the other paired computers in <paramref name="paired"/>'s order. Only computers in
    /// <paramref name="paired"/> (this PC's own pairings, without hosts friends share) are in it. No network.</summary>
    public static IReadOnlyList<string> Order(string? saved, IReadOnlyList<string> paired, IReadOnlyList<WorkPlace> singers)
    {
        ArgumentNullException.ThrowIfNull(paired);
        ArgumentNullException.ThrowIfNull(singers);
        List<string> order = [];
        void Add(string? id)
        {
            if (id is not null && paired.Contains(id, StringComparer.Ordinal) && !order.Contains(id, StringComparer.Ordinal)) order.Add(id);
        }
        Add(saved);
        foreach (var place in singers.OrderBy(p => p.Jobs).ThenBy(p => p.HostId, StringComparer.Ordinal)) Add(place.HostId);
        foreach (var id in paired) Add(id);
        return order;
    }

    /// <summary>Why <paramref name="hostId"/> can't sing a <paramref name="match"/> song at all now (null
    /// <paramref name="state"/>: it doesn't offer Singing), or null when it can (now, or after the songs in its line).</summary>
    public static SongException? Refusal(string hostId, SingerState? state, SongVoiceMatch match) => state switch
    {
        null => new(SongErrorCodes.Unavailable, $"{hostId} doesn't sing."),
        { State: "not_provisioned" } => new(SongErrorCodes.Unavailable, $"Singing isn't set up on {hostId} yet."),
        _ when !state.VoiceMatches.Contains(match) => new(SongErrorCodes.VoiceMatchUnavailable, $"{Name(match)} isn't set up on {hostId}."),
        _ => null
    };

    /// <summary>What the pool does with <paramref name="hostId"/> for a <paramref name="match"/> song now, in words.</summary>
    public static string Verdict(string hostId, SingerState? state, SongVoiceMatch match) =>
        Refusal(hostId, state, match) is { } refusal ? "passed over: " + refusal.Message
            : state!.Ahead > 0 ? $"busy: {state.Ahead} song{(state.Ahead == 1 ? "" : "s")} before a new one"
            : "takes a song now";

    /// <summary>The computer the next <paramref name="match"/> song goes to, as <see cref="RunAsync"/> chooses it from
    /// <paramref name="states"/> (null or missing: it doesn't sing or doesn't answer): the first in <paramref name="order"/>
    /// that sings nothing now, else the one with the fewest songs before it (the first of those); null when none can sing it.</summary>
    public static (string HostId, int Ahead)? Pick(IReadOnlyList<string> order, IReadOnlyDictionary<string, SingerState?> states,
        SongVoiceMatch match)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(states);
        var able = order.Select((id, index) => (id, index, state: states.GetValueOrDefault(id)))
            .Where(c => Refusal(c.id, c.state, match) is null).ToArray();
        if (able.Length == 0) return null;
        var best = able.OrderBy(c => c.state!.Ahead).ThenBy(c => c.index).First();
        return (best.id, best.state!.Ahead);
    }

    /// <summary>What a failure before a song is made means: <see cref="WorkRefusal.Busy"/> for a full line
    /// (<c>singing.busy</c>), <see cref="WorkRefusal.Unavailable"/> for a computer that can't sing it (not reachable, not set
    /// up, without the voice match or the voice's recording) or stopped answering, else <see cref="WorkRefusal.None"/>: a real
    /// failure, which isn't tried again elsewhere.</summary>
    public static WorkRefusal Classify(Exception error) => error switch
    {
        SongException { Code: SongErrorCodes.Busy } => WorkRefusal.Busy,
        SongException { Code: SongErrorCodes.Unavailable or SongErrorCodes.VoiceMatchUnavailable or SongErrorCodes.VoiceMissing } =>
            WorkRefusal.Unavailable,
        SongException => WorkRefusal.None,
        HttpRequestException or IOException => WorkRefusal.Unavailable,
        _ => WorkRefusal.None
    };

    /// <summary>Makes a <paramref name="match"/> song on the first of <paramref name="members"/> that takes it (see the class
    /// summary) through <paramref name="queue"/>. <paramref name="look"/> reads a computer's singing status (null: it doesn't
    /// sing); <paramref name="sing"/> makes the song there, waiting in its line when it has one. <paramref name="classify"/>
    /// says what other failures mean (a gateway's refusals); <see cref="SongException"/>s are read by <see cref="Classify"/>.
    /// Throws the last refusal when no computer took the song.</summary>
    public static async Task<SungOn<T>> RunAsync<TTarget, T>(WorkQueue queue, IReadOnlyList<TTarget> members, Func<TTarget, string> hostOf,
        Func<TTarget, CancellationToken, Task<SingerState?>> look, Func<TTarget, CancellationToken, Task<T>> sing, SongVoiceMatch match,
        Func<Exception, WorkRefusal>? classify, TimeProvider? clock, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(hostOf);
        ArgumentNullException.ThrowIfNull(look);
        ArgumentNullException.ThrowIfNull(sing);
        if (members.Count == 0) throw new SongException(SongErrorCodes.Unavailable, "No computer is set up for Singing.");
        var time = clock ?? TimeProvider.System;
        var index = members.Select((member, i) => (hostOf(member), i)).GroupBy(p => p.Item1, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().i, StringComparer.Ordinal);
        WorkRefusal Refuse(Exception error)
        {
            if (error is SongException || classify is null) return Classify(error);
            var refusal = classify(error);
            return refusal != WorkRefusal.None ? refusal : Classify(error);
        }

        // First: a computer that sings nothing now. Busy ones are remembered with their lines, for the second pass.
        var busy = new Dictionary<string, (TTarget Member, int Ahead)>(StringComparer.Ordinal);
        string? took = null;
        async Task<T> Free(TTarget member, CancellationToken t)
        {
            var id = hostOf(member);
            var state = await look(member, t).ConfigureAwait(false);
            if (Refusal(id, state, match) is { } refusal) throw refusal;
            if (state!.Ahead > 0)
            {
                lock (busy) busy[id] = (member, state.Ahead);
                throw new SongException(SongErrorCodes.Busy, $"{id} is singing another song.");
            }
            took = id;
            return await sing(member, t).ConfigureAwait(false);
        }
        try
        {
            var made = await queue.RunAsync(Lane, members, hostOf, Free, Refuse, time.GetUtcNow(), time, token, WorkPriority.Background)
                .ConfigureAwait(false);
            return new(made, took!, false, 0);
        }
        catch (Exception error) when (error is not OperationCanceledException && Refuse(error) != WorkRefusal.None && Waiting(busy) > 0) { }

        // Every computer that can sing it is busy: wait in line on the one with the fewest songs before it.
        (TTarget Member, int Ahead)[] line;
        lock (busy) line = [.. busy.Values.OrderBy(b => b.Ahead).ThenBy(b => index.GetValueOrDefault(hostOf(b.Member)))];
        async Task<T> Wait(TTarget member, CancellationToken t)
        {
            took = hostOf(member);
            return await sing(member, t).ConfigureAwait(false);
        }
        var queued = await queue.RunAsync(Lane, line.Select(b => b.Member).ToArray(), hostOf, Wait, Refuse, time.GetUtcNow(), time, token,
            WorkPriority.Background).ConfigureAwait(false);
        return new(queued, took!, true, line.First(b => hostOf(b.Member) == took).Ahead);
    }

    private static int Waiting<TTarget>(Dictionary<string, (TTarget, int)> busy)
    {
        lock (busy) return busy.Count;
    }

    private static string Name(SongVoiceMatch match) => match == SongVoiceMatch.VevoSing ? "VevoSing" : "SoulX-Singer";
}
