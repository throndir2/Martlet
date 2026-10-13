namespace Martlet.Core.Cluster;

/// <summary>One place an ordered pool's request can go (<see cref="PoolPlaces.Order"/>): a member of the area's list, the paired
/// computer it runs on (<see cref="HostId"/>; null for this PC's own engine, an address or a cloud provider) and its key in
/// <see cref="WorkQueue"/> (the "computer" a request waits for; each pool keeps its own key rule).</summary>
public sealed record PoolPlace(PoolMember Member, string? HostId, string Key);

/// <summary>Turns an area's list into the places a request tries, first to last. Each pool gives its own rule for where a
/// member runs and whether it can take the request here now (paired here, runs the engine, has a key). The order and the
/// "each place once" rule are the same for every pool.</summary>
public static class PoolPlaces
{
    /// <summary>The paired computer <paramref name="member"/> runs on: for This PC, this PC's own host service
    /// (<paramref name="own"/>, null without one); for a computer or one of its cards, that computer; null for an address or a
    /// cloud provider.</summary>
    public static string? HostOf(PoolMember member, string? own)
    {
        ArgumentNullException.ThrowIfNull(member);
        return member.Kind switch
        {
            PoolMemberKind.ThisPc => own,
            PoolMemberKind.Computer or PoolMemberKind.Gpu => member.HostId,
            _ => null
        };
    }

    /// <summary>The places companion PC <paramref name="device"/> tries for <paramref name="area"/>, first to last: the members
    /// that <see cref="PoolRouting.Order"/> keeps (on, a kind the area takes, kept for this PC or every one, agreed to) and that
    /// <paramref name="place"/> puts somewhere (null: the member can't take the request here now). Each
    /// <see cref="PoolPlace.Key"/> is in the order once, at its first place. No network.</summary>
    public static IReadOnlyList<PoolPlace> Order(PoolArea area, PoolList? list, string device, Func<PoolMember, PoolPlace?> place)
    {
        ArgumentNullException.ThrowIfNull(place);
        List<PoolPlace> places = [];
        foreach (var member in PoolRouting.Order(area, list, device, m => place(m) is not null).Members)
            if (place(member) is { } found && places.All(p => p.Key != found.Key)) places.Add(found);
        return places;
    }
}

/// <summary>One try of a request on one place (<see cref="PoolRunner{TPlace}"/>): the place's position in the order and how
/// many places were busy or couldn't take the request before this try.</summary>
public sealed class PoolTry
{
    internal PoolTry(int position, int busy, int unavailable)
    {
        Position = position;
        Busy = busy;
        Unavailable = unavailable;
    }

    /// <summary>The place's position in the order (0 is the first).</summary>
    public int Position { get; }

    /// <summary>How many tries found a place busy before this one.</summary>
    public int Busy { get; }

    /// <summary>How many tries found a place that couldn't take the request before this one.</summary>
    public int Unavailable { get; }
}

/// <summary>Where a pool request went: the answer, the place that took it, its position in the order, and how many tries found a
/// place busy or unable to take it first.</summary>
public sealed record PoolRun<TPlace, T>(T Result, TPlace Place, int Position, int Busy, int Unavailable);

/// <summary>
/// The one runner of Martlet's ordered pools. Each pool is an adapter: it gives its places in order
/// (<see cref="PoolPlaces.Order"/>), its <see cref="WorkQueue"/> lane, each place's key (<c>keyOf</c>), what a failure means
/// (<c>classify</c>), its priority and how one place does the request. The runner does the rest the same way for every pool,
/// through <see cref="WorkQueue"/>:
/// <list type="number">
/// <item>The first place that takes the request does it. A free first place costs nothing extra.</item>
/// <item>A place that is busy (<see cref="WorkRefusal.Busy"/>) or can't take it now (<see cref="WorkRefusal.Unavailable"/>) is
/// passed over for the next.</item>
/// <item>When every place is busy, the request waits for whichever frees first, until its wait ends.</item>
/// <item>A real failure (<see cref="WorkRefusal.None"/>) ends the request: it is not done again elsewhere.</item>
/// </list>
/// The result says which place took the request (<see cref="PoolRun{TPlace, T}"/>), so no pool keeps its own "who took it" note.
/// </summary>
public sealed class PoolRunner<TPlace>(string lane, Func<TPlace, string> keyOf, Func<Exception, WorkRefusal> classify,
    WorkPriority priority = WorkPriority.Background, WorkQueue? queue = null, TimeProvider? clock = null)
{
    private readonly WorkQueue queue = queue ?? WorkQueue.Shared;
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    /// <summary>The <see cref="WorkQueue"/> lane of the pool's requests: its pool area's ID.</summary>
    public string Lane => lane;

    /// <summary>Runs one request on the first of <paramref name="places"/> that takes it (see the class summary).
    /// <paramref name="attempt"/> does it on one place. When every place is busy, the request waits for the first to free, for
    /// <paramref name="wait"/> at most. Throws the last refusal when no place took it.</summary>
    public async Task<PoolRun<TPlace, T>> RunAsync<T>(IReadOnlyList<TPlace> places, Func<TPlace, PoolTry, CancellationToken, Task<T>> attempt,
        TimeSpan wait, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(attempt);
        var run = new Run(places, keyOf, classify);
        var result = await queue.RunAsync(lane, places, keyOf, (place, t) => run.TryAsync(place, attempt, t), classify,
            clock.GetUtcNow() + wait, clock, token, priority).ConfigureAwait(false);
        return run.Taken(result);
    }

    // One request's tries: the busy and unavailable counts and the place that took it. The queue tries one place at a time.
    private sealed class Run(IReadOnlyList<TPlace> places, Func<TPlace, string> keyOf, Func<Exception, WorkRefusal> classify)
    {
        private readonly object gate = new();
        private int busy, unavailable;
        private (TPlace Place, PoolTry Try)? taken;

        public async Task<T> TryAsync<T>(TPlace place, Func<TPlace, PoolTry, CancellationToken, Task<T>> attempt, CancellationToken token)
        {
            var tried = Begin(place);
            try
            {
                var result = await attempt(place, tried, token).ConfigureAwait(false);
                lock (gate) taken = (place, tried);
                return result;
            }
            catch (Exception error)
            {
                Count(error);
                throw;
            }
        }

        public PoolRun<TPlace, T> Taken<T>(T result)
        {
            lock (gate)
            {
                var (place, tried) = taken ?? throw new InvalidOperationException("No place took the request.");
                return new(result, place, tried.Position, busy, unavailable);
            }
        }

        private PoolTry Begin(TPlace place)
        {
            var key = keyOf(place);
            var position = 0;
            while (position < places.Count && keyOf(places[position]) != key) position++;
            lock (gate) return new(position, busy, unavailable);
        }

        private void Count(Exception error)
        {
            var refusal = classify(error);
            lock (gate)
            {
                if (refusal == WorkRefusal.Busy) busy++;
                else if (refusal != WorkRefusal.None) unavailable++;
            }
        }
    }
}
