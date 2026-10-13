using System.Runtime.CompilerServices;

namespace Martlet.Core.Cluster;

/// <summary>One try of a request on one stop (<see cref="PoolRunner{TStop}"/>): the stop's position in the order, how many
/// tries found a stop busy or unable to take the request before this one, and, when the request waits in this stop's own line
/// (<see cref="PoolRunner{TStop}.RunOrWaitInLineAsync"/>), how many requests are before it there.</summary>
public sealed class PoolTry
{
    private readonly Action<int>? line;

    internal PoolTry(int position, int busy, int unavailable, int? ahead = null, Action<int>? line = null)
    {
        Position = position;
        Busy = busy;
        Unavailable = unavailable;
        Ahead = ahead;
        this.line = line;
    }

    /// <summary>The stop's position in the order (0 is the first).</summary>
    public int Position { get; }

    /// <summary>How many tries found a stop busy before this one.</summary>
    public int Busy { get; }

    /// <summary>How many tries found a stop that couldn't take the request before this one.</summary>
    public int Unavailable { get; }

    /// <summary>Null: the stop takes the request only when it is free now. A number: every stop was busy, so the request waits in
    /// this stop's own line, behind that many requests.</summary>
    public int? Ahead { get; }

    /// <summary>Says that the stop is busy with <paramref name="ahead"/> requests before a new one, so the request can wait in its
    /// line when every stop is busy (<see cref="PoolRunner{TStop}.RunOrWaitInLineAsync"/>). Call it, then throw the stop's busy
    /// error. The first line a stop gives is kept.</summary>
    public void Line(int ahead) => line?.Invoke(ahead);
}

/// <summary>Where a pool request went: the answer, the stop that took it, its position in the order, how many tries found a
/// stop busy or unable to take it first, and, when it waited in that stop's own line, how many requests were before it there
/// (<see cref="Ahead"/>; null when the stop took it without that line).</summary>
public sealed record PoolRun<TStop, T>(T Result, TStop Stop, int Position, int Busy, int Unavailable, int? Ahead = null);

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
/// <item>When every stop is busy, the request waits for whichever frees first, until its wait ends
/// (<see cref="RunAsync"/>). A pool whose stops keep their own line (a singing host, a ComfyUI) waits in the shortest of
/// those lines instead (<see cref="RunOrWaitInLineAsync"/>).</item>
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
    public Task<PoolRun<TStop, T>> RunAsync<T>(IReadOnlyList<TStop> stops, Func<TStop, PoolTry, CancellationToken, Task<T>> attempt,
        TimeSpan wait, CancellationToken token) => RunAsync(stops, attempt, clock.GetUtcNow() + wait, token);

    /// <summary>Runs one request as <see cref="RunAsync{T}(IReadOnlyList{TStop}, Func{TStop, PoolTry, CancellationToken, Task{T}}, TimeSpan, CancellationToken)"/>
    /// does, waiting for a busy stop until <paramref name="until"/> at most (a job with its own end time).</summary>
    public async Task<PoolRun<TStop, T>> RunAsync<T>(IReadOnlyList<TStop> stops, Func<TStop, PoolTry, CancellationToken, Task<T>> attempt,
        DateTimeOffset until, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stops);
        ArgumentNullException.ThrowIfNull(attempt);
        var run = new Run(stops, keyOf, classify);
        var result = await queue.RunAsync(lane, stops, keyOf, (stop, t) => run.TryAsync(stop, null, attempt, t), classify,
            until, clock, token, priority).ConfigureAwait(false);
        return run.Taken(result);
    }

    /// <summary>Runs one request on a pool whose stops keep their own line (a singing host, a ComfyUI). First, the first stop
    /// that is free now takes it, with no wait in <see cref="WorkQueue"/>'s line: asking a busy stop again and again only adds
    /// requests. A busy stop says how many requests are before a new one there (<see cref="PoolTry.Line"/>) and refuses. When
    /// every stop that can take it is busy, the request waits in the line of the busy stop with the fewest requests before it
    /// (the first in the order on a tie): <paramref name="attempt"/> runs there again with <see cref="PoolTry.Ahead"/> set.
    /// <paramref name="lines"/> is how many busy stops' lines it tries, shortest first, when the one before turns it away.
    /// Throws the last refusal when no stop took it.</summary>
    public async Task<PoolRun<TStop, T>> RunOrWaitInLineAsync<T>(IReadOnlyList<TStop> stops,
        Func<TStop, PoolTry, CancellationToken, Task<T>> attempt, int lines, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stops);
        ArgumentNullException.ThrowIfNull(attempt);
        var run = new Run(stops, keyOf, classify);
        try
        {
            var free = await queue.RunAsync(lane, stops, keyOf, (stop, t) => run.TryAsync(stop, null, attempt, t), classify,
                DateTimeOffset.MinValue, clock, token, priority).ConfigureAwait(false);
            return run.Taken(free);
        }
        catch (Exception error) when (error is not OperationCanceledException && classify(error) != WorkRefusal.None && run.Lines(lines).Count > 0) { }

        // Every stop that can take it is busy: wait in the shortest line.
        var line = run.Lines(lines);
        var queued = await queue.RunAsync(lane, [.. line.Select(l => l.Stop)], keyOf,
            (stop, t) => run.TryAsync(stop, line.First(l => keyOf(l.Stop) == keyOf(stop)).Ahead, attempt, t), classify,
            DateTimeOffset.MinValue, clock, token, priority).ConfigureAwait(false);
        return run.Taken(queued);
    }

    /// <summary>Streams one request from the first of <paramref name="stops"/> that takes it (see the class summary): a stop has
    /// taken it when its first item arrives. <paramref name="start"/> starts it on one stop. When every stop is busy, the request
    /// waits for the first to free, for <paramref name="wait"/> at most. <paramref name="taken"/> is called once, with the stop
    /// and its try, when the first item arrives. Throws the last refusal when no stop took it.</summary>
    public IAsyncEnumerable<T> StreamAsync<T>(IReadOnlyList<TStop> stops, Func<TStop, PoolTry, CancellationToken, IAsyncEnumerable<T>> start,
        TimeSpan wait, Action<TStop, PoolTry>? taken, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stops);
        ArgumentNullException.ThrowIfNull(start);
        var run = new Run(stops, keyOf, classify);
        return queue.StreamAsync(lane, stops, keyOf, (stop, t) => run.Stream(stop, start, taken, t), classify, clock.GetUtcNow() + wait,
            clock, token, priority);
    }

    // One request's tries: the busy and unavailable counts, the busy stops' lines and the stop that took it. The queue tries one
    // stop at a time.
    private sealed class Run(IReadOnlyList<TStop> stops, Func<TStop, string> keyOf, Func<Exception, WorkRefusal> classify)
    {
        private readonly object gate = new();
        private readonly Dictionary<string, (TStop Stop, int Position, int Ahead)> lines = new(StringComparer.Ordinal);
        private int busy, unavailable;
        private (TStop Stop, PoolTry Try)? taken;

        public async Task<T> TryAsync<T>(TStop stop, int? ahead, Func<TStop, PoolTry, CancellationToken, Task<T>> attempt,
            CancellationToken token)
        {
            var tried = Begin(stop, ahead);
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
                return new(result, stop, tried.Position, busy, unavailable, tried.Ahead);
            }
        }

        // One stop's stream: started at once, as WorkQueue asks for it; counted when it refuses before its first item, and
        // taken when its first item arrives.
        public IAsyncEnumerable<T> Stream<T>(TStop stop, Func<TStop, PoolTry, CancellationToken, IAsyncEnumerable<T>> start,
            Action<TStop, PoolTry>? noted, CancellationToken token)
        {
            var tried = Begin(stop, null);
            return Tracked(stop, tried, start(stop, tried, token), noted);
        }

        private async IAsyncEnumerable<T> Tracked<T>(TStop stop, PoolTry tried, IAsyncEnumerable<T> source, Action<TStop, PoolTry>? noted,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            var items = source.GetAsyncEnumerator(token);
            try
            {
                bool first;
                try { first = await items.MoveNextAsync().ConfigureAwait(false); }
                catch (Exception error)
                {
                    Count(error);
                    throw;
                }
                if (!first) yield break;
                lock (gate) taken = (stop, tried);
                noted?.Invoke(stop, tried);
                yield return items.Current;
                while (await items.MoveNextAsync().ConfigureAwait(false)) yield return items.Current;
            }
            finally { await items.DisposeAsync().ConfigureAwait(false); }
        }

        // The busy stops' lines, shortest first (the first in the order on a tie), at most count of them.
        public IReadOnlyList<(TStop Stop, int Position, int Ahead)> Lines(int count)
        {
            lock (gate) return [.. lines.Values.OrderBy(l => l.Ahead).ThenBy(l => l.Position).Take(count)];
        }

        private PoolTry Begin(TStop stop, int? ahead)
        {
            var key = keyOf(stop);
            var position = 0;
            while (position < stops.Count && keyOf(stops[position]) != key) position++;
            void Line(int length)
            {
                lock (gate) lines.TryAdd(key, (stop, position, length));
            }
            lock (gate) return new(position, busy, unavailable, ahead, Line);
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
