using System.Runtime.CompilerServices;

namespace Martlet.Core.Cluster;

/// <summary>Lip-sync's pool: the computers that run Audio2Face, in order, share the character's lip-sync. Each chunk of a
/// sentence (half a second, then one second of the voice) goes through <see cref="WorkQueue"/> at live priority: to the first
/// computer that takes it, so a free first computer costs nothing extra. One reply stays on the computer that took its last
/// chunk while that computer is free; the next reply chooses again from the order. A chunk waits for a busy computer at most
/// half its own length (<see cref="Wait"/>); after that its frames would come too late, so it is skipped and the voice's
/// loudness moves the mouth for it. Speech never waits for lip-sync. Thread-safe; one per companion PC.</summary>
public sealed class LipSyncSharing
{
    private readonly object gate = new();
    private Guid turn;
    private string? host;
    private long chunks, moved, skipped;

    /// <summary>The longest a chunk of <paramref name="samples"/> at <paramref name="sampleRate"/> waits for a busy computer:
    /// half its length, at least 100 ms.</summary>
    public static TimeSpan Wait(long samples, int sampleRate) =>
        TimeSpan.FromMilliseconds(Math.Max(100, samples * 500.0 / Math.Max(1, sampleRate)));

    /// <summary>The computer that took the last chunk (null before the first).</summary>
    public string? LastHost
    {
        get { lock (gate) return host; }
    }

    /// <summary>Chunks a computer took since this pool was made.</summary>
    public long Chunks => Interlocked.Read(ref chunks);

    /// <summary>Chunks a computer other than the first in the order took (the first was busy or didn't answer).</summary>
    public long Moved => Interlocked.Read(ref moved);

    /// <summary>Chunks no computer could take in time: the voice's loudness moved the mouth for them.</summary>
    public long Skipped => Interlocked.Read(ref skipped);

    /// <summary><paramref name="members"/> in the order a chunk of reply <paramref name="turnId"/> tries them: the computer that
    /// took this reply's last chunk first, then the rest in order.</summary>
    public IReadOnlyList<T> Order<T>(IReadOnlyList<T> members, Func<T, string> hostOf, Guid turnId)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(hostOf);
        string? sticky;
        lock (gate) sticky = turn == turnId ? host : null;
        if (sticky is null || members.Count < 2) return members;
        var index = -1;
        for (var i = 0; i < members.Count && index < 0; i++)
            if (string.Equals(hostOf(members[i]), sticky, StringComparison.Ordinal)) index = i;
        return index <= 0 ? members : [members[index], .. members.Where((_, i) => i != index)];
    }

    /// <summary>Streams one chunk of reply <paramref name="turnId"/> from the first of <paramref name="members"/> that takes it
    /// (<see cref="Order{T}"/>). When every computer stays busy past <paramref name="wait"/> (or a shared host keeps its card for
    /// its owner), the chunk ends with no frames and counts as <see cref="Skipped"/>. A computer that doesn't answer is passed
    /// over; when none answers, or one fails for real, the failure is thrown.</summary>
    public async IAsyncEnumerable<T> ChunkAsync<TTarget, T>(WorkQueue queue, Guid turnId, IReadOnlyList<TTarget> members,
        Func<TTarget, string> hostOf, Func<TTarget, CancellationToken, IAsyncEnumerable<T>> start, Func<Exception, WorkRefusal> classify,
        TimeSpan wait, TimeProvider? clock, [EnumeratorCancellation] CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(classify);
        if (members.Count == 0) throw new ArgumentException("Lip-sync's pool needs at least one computer.", nameof(members));
        var order = Order(members, hostOf, turnId);
        var first = hostOf(order[0]);
        var time = clock ?? TimeProvider.System;
        await using var items = queue.StreamAsync(WorkSharingJobs.LipSync, order, hostOf,
                (target, t) => Noted(target, hostOf, turnId, first, start(target, t)), classify, time.GetUtcNow() + wait, clock, token)
            .GetAsyncEnumerator(token);
        var started = false;
        while (true)
        {
            bool next;
            try { next = await items.MoveNextAsync().ConfigureAwait(false); }
            catch (Exception error) when (!started && !token.IsCancellationRequested &&
                classify(error) is WorkRefusal.Busy or WorkRefusal.Preempted or WorkRefusal.Owner)
            {
                Interlocked.Increment(ref skipped);
                yield break;
            }
            if (!next) yield break;
            started = true;
            yield return items.Current;
        }
    }

    // Records the computer that took the chunk when its first frame arrives.
    private async IAsyncEnumerable<T> Noted<TTarget, T>(TTarget target, Func<TTarget, string> hostOf, Guid turnId, string first,
        IAsyncEnumerable<T> source, [EnumeratorCancellation] CancellationToken token = default)
    {
        var noted = false;
        await foreach (var item in source.WithCancellation(token).ConfigureAwait(false))
        {
            if (!noted)
            {
                noted = true;
                var id = hostOf(target);
                lock (gate) (turn, host) = (turnId, id);
                Interlocked.Increment(ref chunks);
                if (id != first) Interlocked.Increment(ref moved);
            }
            yield return item;
        }
    }
}
