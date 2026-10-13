namespace Martlet.Core.Cluster;

/// <summary>One try of a request on one stop (<see cref="PoolRunner{TStop}"/>): the stop's position in the order and how many
/// tries found a stop busy or unable to take the request before this one.</summary>
public sealed class PoolTry
{
    internal PoolTry(int position, int busy, int unavailable)
    {
        Position = position;
        Busy = busy;
        Unavailable = unavailable;
    }

    /// <summary>The stop's position in the order (0 is the first).</summary>
    public int Position { get; }

    /// <summary>How many tries found a stop busy before this one.</summary>
    public int Busy { get; }

    /// <summary>How many tries found a stop that couldn't take the request before this one.</summary>
    public int Unavailable { get; }
}

/// <summary>Where a pool request went: the answer, the stop that took it, its position in the order, and how many tries found
/// a stop busy or unable to take it first.</summary>
public sealed record PoolRun<TStop, T>(T Result, TStop Stop, int Position, int Busy, int Unavailable);

/// <summary>
/// The one runner of Martlet's ordered pools (docs/CLUSTER.md, the pool contract). Each pool is an adapter: it gives its stops
/// in order (<see cref="PoolRouting.Stops(PoolArea, PoolList?, string, Func{PoolMember, PoolStop?})"/> or its own stop type),
/// its <see cref="WorkQueue"/> lane (the pool area's ID), each stop's key (<c>keyOf</c>), what a failure means
/// (<c>classify</c>), its priority, and how one stop does the request. The runner does the rest the same way for every pool,
/// through <see cref="WorkQueue"/>:
/// <list type="number">
/// <item>The first stop that takes the request does it. A free first stop costs nothing extra.</item>
/// <item>A stop that is busy (<see cref="WorkRefusal.Busy"/>) or can't take it now (<see cref="WorkRefusal.Unavailable"/>) is
/// passed over for the next.</item>
/// <item>When every stop is busy, the request waits for whichever frees first, until its wait ends.</item>
/// <item>A real failure (<see cref="WorkRefusal.None"/>) ends the request: it is not done again elsewhere.</item>
/// </list>
/// The result (<see cref="PoolRun{TStop, T}"/>) says which stop took the request, so no pool keeps its own "who took it" note.
/// </summary>
public sealed class PoolRunner<TStop>(string lane, Func<TStop, string> keyOf, Func<Exception, WorkRefusal> classify,
    WorkPriority priority = WorkPriority.Background, WorkQueue? queue = null, TimeProvider? clock = null)
{
    private readonly WorkQueue queue = queue ?? WorkQueue.Shared;
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    /// <summary>The <see cref="WorkQueue"/> lane of the pool's requests: its pool area's ID.</summary>
    public string Lane => lane;

    /// <summary>Runs one request on the first of <paramref name="stops"/> that takes it (see the class summary).
    /// <paramref name="attempt"/> does it on one stop. When every stop is busy, the request waits for the first to free, for
    /// <paramref name="wait"/> at most. Throws the last refusal when no stop took it.</summary>
    public async Task<PoolRun<TStop, T>> RunAsync<T>(IReadOnlyList<TStop> stops, Func<TStop, PoolTry, CancellationToken, Task<T>> attempt,
        TimeSpan wait, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stops);
        ArgumentNullException.ThrowIfNull(attempt);
        var run = new Run(stops, keyOf, classify);
        var result = await queue.RunAsync(lane, stops, keyOf, (stop, t) => run.TryAsync(stop, attempt, t), classify,
            clock.GetUtcNow() + wait, clock, token, priority).ConfigureAwait(false);
        return run.Taken(result);
    }

    // One request's tries: the busy and unavailable counts and the stop that took it. The queue tries one stop at a time.
    private sealed class Run(IReadOnlyList<TStop> stops, Func<TStop, string> keyOf, Func<Exception, WorkRefusal> classify)
    {
        private readonly object gate = new();
        private int busy, unavailable;
        private (TStop Stop, PoolTry Try)? taken;

        public async Task<T> TryAsync<T>(TStop stop, Func<TStop, PoolTry, CancellationToken, Task<T>> attempt, CancellationToken token)
        {
            var tried = Begin(stop);
            try
            {
                var result = await attempt(stop, tried, token).ConfigureAwait(false);
                lock (gate) taken = (stop, tried);
                return result;
            }
            catch (Exception error)
            {
                Count(error);
                throw;
            }
        }

        public PoolRun<TStop, T> Taken<T>(T result)
        {
            lock (gate)
            {
                var (stop, tried) = taken ?? throw new InvalidOperationException("No stop took the request.");
                return new(result, stop, tried.Position, busy, unavailable);
            }
        }

        private PoolTry Begin(TStop stop)
        {
            var key = keyOf(stop);
            var position = 0;
            while (position < stops.Count && keyOf(stops[position]) != key) position++;
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
