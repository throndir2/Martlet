using Martlet.Core.Cluster;

namespace Martlet.Core.Speakers;

/// <summary>One recognition: what it found, the place that did it, how long it took and why places before it were passed
/// over (plain words, no audio). <see cref="Raced"/>: no computer had answered within this PC's head start, so this PC
/// recognized too and the first answer won.</summary>
public sealed record VoiceRecognitionRun<T>(T Result, VoiceRecognitionTarget By, TimeSpan Took, IReadOnlyList<string> Passed, bool Raced);

/// <summary>What Martlet knows about one computer of the Voice recognition list: when it last answered and in how long, or
/// why it is left out for a while (<see cref="RestUntil"/>).</summary>
public sealed record VoiceRecognitionHost(string HostId, DateTimeOffset? AnsweredAt, double? Milliseconds, string? Problem,
    DateTimeOffset? RestUntil);

/// <summary>
/// Sends one utterance to the Voice recognition list's places in order (<see cref="VoiceRecognitionPool.Targets"/>,
/// docs/VOICES.md#where-voice-recognition-runs) and makes sure it is recognized whenever one place can:
/// <list type="bullet">
/// <item>The computers before this PC go through <see cref="WorkQueue"/> (lane <see cref="Lane"/>, live priority, each
/// computer's key): a busy one is passed over for the next at once, and nobody waits for a busy computer, because this PC can
/// always do it.</item>
/// <item>A computer has this PC's head start (<see cref="HeadStart"/>: about how long this PC would take, from its measured speed,
/// 200 ms to 1.5 s). After it, this PC recognizes too, on a copy of the samples, and the first answer wins, so a slow computer
/// never makes recognition much slower than this PC alone. A computer is given up after <see cref="Longest"/>.</item>
/// <item>When every computer before it refused, failed or gave a bad answer, this PC recognizes at once. When this PC can't (its
/// engine rests after failures, <see cref="VoiceEngineKeeper{T}"/>), the computers after it are tried.</item>
/// <item>A computer that failed, gave a bad answer or lost to this PC rests for <see cref="Rest"/> and is tried again after it.
/// A busy one doesn't rest.</item>
/// </list>
/// </summary>
public sealed class VoiceRecognitionRouter<T>
{
    public const string Lane = WorkSharingJobs.VoiceRecognition;
    public static TimeSpan MinimumHeadStart { get; } = TimeSpan.FromMilliseconds(200);
    public static TimeSpan MaximumHeadStart { get; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Recognizes on one computer: the analysis, or a refusal (the classify function says which) or an invalid answer.</summary>
    public delegate Task<T> Remote(VoiceRecognitionTarget target, float[] samples, CancellationToken token);

    private const int SampleRate = 16_000;
    private readonly VoiceEngineKeeper<T> here;
    private readonly Remote remote;
    private readonly WorkQueue queue;
    private readonly Func<Exception, WorkRefusal> classify;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly Dictionary<string, VoiceRecognitionHost> hosts = new(StringComparer.Ordinal);

    public VoiceRecognitionRouter(VoiceEngineKeeper<T> here, Remote remote, WorkQueue? queue = null,
        Func<Exception, WorkRefusal>? classify = null, TimeProvider? clock = null)
    {
        this.here = here ?? throw new ArgumentNullException(nameof(here));
        this.remote = remote ?? throw new ArgumentNullException(nameof(remote));
        this.queue = queue ?? WorkQueue.Shared;
        this.classify = classify ?? (_ => WorkRefusal.Unavailable);
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>The head start before this PC's speed is measured.</summary>
    public TimeSpan DefaultHeadStart { get; init; } = TimeSpan.FromMilliseconds(600);

    /// <summary>How long a computer may take at most.</summary>
    public TimeSpan Longest { get; init; } = TimeSpan.FromSeconds(2.5);

    /// <summary>How long a computer that failed or was slow is left out.</summary>
    public TimeSpan Rest { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Raised when a computer started or stopped resting.</summary>
    public event Action? Changed;

    /// <summary>What is known about each computer tried since Martlet started.</summary>
    public IReadOnlyList<VoiceRecognitionHost> Hosts
    {
        get { lock (gate) return [.. hosts.Values.OrderBy(h => h.HostId, StringComparer.Ordinal)]; }
    }

    /// <summary>How long a computer has for <paramref name="samples"/> samples before this PC recognizes too: about as long as
    /// this PC would take (its measured speed), at least <see cref="MinimumHeadStart"/> and at most <see cref="MaximumHeadStart"/>.</summary>
    public TimeSpan HeadStart(int samples)
    {
        if (here.Status.MillisecondsPerSecond is not { } rate) return DefaultHeadStart;
        var ms = rate * Math.Max(1.0, samples / (double)SampleRate);
        return TimeSpan.FromMilliseconds(Math.Clamp(ms, MinimumHeadStart.TotalMilliseconds, MaximumHeadStart.TotalMilliseconds));
    }

    /// <summary>Whether <paramref name="hostId"/> is left out now after it failed or was slow.</summary>
    public bool Resting(string hostId)
    {
        lock (gate) return hosts.TryGetValue(hostId, out var known) && known.RestUntil is { } until && clock.GetUtcNow() < until;
    }

    // One run's computers passed over and the computer being tried now (the one this PC outran, when it was slow).
    private sealed class Tries
    {
        private readonly List<string> passed = [];
        public volatile string? Trying;
        public void Add(string why)
        {
            lock (passed) passed.Add(why);
        }
        public string[] Passed
        {
            get { lock (passed) return [.. passed]; }
        }
    }

    /// <summary>Recognizes <paramref name="samples"/> at the first place of <paramref name="targets"/> that can (see the class
    /// summary). Throws when no place could: this PC's failure (<see cref="VoiceEngineException"/>), naming the computers'.</summary>
    public async Task<VoiceRecognitionRun<T>> RunAsync(IReadOnlyList<VoiceRecognitionTarget> targets, float[] samples, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(samples);
        var started = clock.GetTimestamp();
        var tries = new Tries();
        var at = targets.ToList().FindIndex(t => t.Here);
        var self = at < 0 ? new VoiceRecognitionTarget(PoolMember.ThisPcKey, null, PoolMember.ThisPc(), Fallback: true) : targets[at];
        VoiceRecognitionTarget[] Usable(IEnumerable<VoiceRecognitionTarget> part)
        {
            var usable = new List<VoiceRecognitionTarget>();
            foreach (var target in part.Where(t => !t.Here))
                if (Resting(target.HostId!)) tries.Add($"{target.HostId} rests after it failed or was slow");
                else usable.Add(target);
            return [.. usable];
        }
        var before = Usable(at < 0 ? targets : targets.Take(at));
        var after = Usable(at < 0 ? [] : targets.Skip(at + 1));
        VoiceRecognitionRun<T> Done(T result, VoiceRecognitionTarget by, bool raced) =>
            new(result, by, clock.GetElapsedTime(started), tries.Passed, raced);

        if (before.Length > 0)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            stop.CancelAfter(Longest);
            var first = HostsAsync(before, samples, tries, stop.Token);
            var head = HeadStart(samples.Length);
            using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var delay = Task.Delay(head, clock, waiting.Token);
                await Task.WhenAny(first, delay).ConfigureAwait(false);
                await waiting.CancelAsync().ConfigureAwait(false);
            }
            if (token.IsCancellationRequested)
            {
                Observe(first);
                token.ThrowIfCancellationRequested();
            }
            if (first.IsCompletedSuccessfully) return Done(first.Result.Result, first.Result.By, false);
            if (!first.IsCompleted)
            {
                // No computer answered within this PC's head start: this PC recognizes too, on its own copy (the caller clears
                // the samples once it has an answer, while the other may still run), and the first answer wins.
                var copy = (float[])samples.Clone();
                var local = Task.Run(() =>
                {
                    try { return here.Analyze(copy); }
                    finally { Array.Clear(copy); }
                }, CancellationToken.None);
                var winner = await FirstAnswerAsync(first, local).ConfigureAwait(false);
                if (winner == first)
                {
                    Observe(local);
                    return Done(first.Result.Result, first.Result.By, true);
                }
                if (winner == local)
                {
                    if (!first.IsCompleted)
                    {
                        Slow(tries, head);
                        await stop.CancelAsync().ConfigureAwait(false);
                    }
                    Observe(first);
                    return Done(local.Result, self, true);
                }
                if (first.IsCanceled && !token.IsCancellationRequested) Slow(tries, Longest);
                Observe(first);
                token.ThrowIfCancellationRequested();
                return await AfterAsync(Unwrap(local)).ConfigureAwait(false);
            }
            Observe(first);
        }

        // Every computer before it refused or failed at once, or there were none: this PC recognizes now.
        T result;
        try { result = await Task.Run(() => here.Analyze(samples), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) when (error is not (OperationCanceledException or ArgumentException))
        {
            return await AfterAsync(error).ConfigureAwait(false);
        }
        return Done(result, self, false);

        // This PC couldn't: the computers after it, each once, without waiting for a busy one.
        async Task<VoiceRecognitionRun<T>> AfterAsync(Exception failed)
        {
            if (failed is ArgumentException) throw failed;
            tries.Add("this PC: " + VoiceEngineKeeper<T>.Problem(failed));
            if (after.Length > 0)
            {
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
                stop.CancelAfter(Longest);
                try
                {
                    var (answer, by) = await HostsAsync(after, samples, tries, stop.Token).ConfigureAwait(false);
                    return Done(answer, by, false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { Slow(tries, Longest); }
                catch (Exception error) when (error is not OperationCanceledException) { }
            }
            var others = tries.Passed.Where(p => !p.StartsWith("this PC: ", StringComparison.Ordinal)).ToArray();
            throw new VoiceEngineException(VoiceEngineKeeper<T>.Problem(failed) +
                (others.Length == 0 ? "" : $" No computer in the Voice recognition list could either ({string.Join("; ", others)})."), failed);
        }
    }

    // The computers in order through the queue; a computer that refuses or fails passes it to the next at once.
    private async Task<(T Result, VoiceRecognitionTarget By)> HostsAsync(VoiceRecognitionTarget[] targets, float[] samples, Tries tries,
        CancellationToken token)
    {
        VoiceRecognitionTarget? by = null;
        var result = await queue.RunAsync(Lane, targets, t => t.Key, async (target, attempt) =>
        {
            var answer = await AttemptAsync(target, samples, tries, attempt).ConfigureAwait(false);
            by = target;
            return answer;
        }, error => classify(error) is var refusal && refusal == WorkRefusal.None ? WorkRefusal.Unavailable : refusal,
            clock.GetUtcNow(), clock, token).ConfigureAwait(false);
        return (result, by!);
    }

    private async Task<T> AttemptAsync(VoiceRecognitionTarget target, float[] samples, Tries tries, CancellationToken token)
    {
        var hostId = target.HostId!;
        tries.Trying = hostId;
        var started = clock.GetTimestamp();
        try
        {
            var answer = await remote(target, samples, token).ConfigureAwait(false);
            tries.Trying = null;
            Answered(hostId, clock.GetElapsedTime(started));
            return answer;
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            tries.Trying = null;
            var refusal = classify(error);
            var why = refusal is WorkRefusal.Busy or WorkRefusal.Preempted ? "busy"
                : refusal == WorkRefusal.Owner ? "busy with its owner's own work" : error.Message;
            tries.Add($"{hostId}: {why}");
            if (refusal is not (WorkRefusal.Busy or WorkRefusal.Preempted or WorkRefusal.Owner)) RestHost(hostId, why);
            throw;
        }
    }

    private void Slow(Tries tries, TimeSpan after)
    {
        if (tries.Trying is not { } hostId) return;
        tries.Trying = null;
        var why = $"didn't answer within {after.TotalMilliseconds:0} ms";
        tries.Add($"{hostId} {why}");
        RestHost(hostId, why);
    }

    private void Answered(string hostId, TimeSpan took)
    {
        bool changed;
        lock (gate)
        {
            changed = !hosts.TryGetValue(hostId, out var known) || known.Problem is not null;
            hosts[hostId] = new(hostId, clock.GetUtcNow(), Math.Round(took.TotalMilliseconds), null, null);
        }
        if (changed) Changed?.Invoke();
    }

    private void RestHost(string hostId, string why)
    {
        lock (gate)
        {
            var known = hosts.GetValueOrDefault(hostId);
            hosts[hostId] = new(hostId, known?.AnsweredAt, known?.Milliseconds, why, clock.GetUtcNow() + Rest);
        }
        Changed?.Invoke();
    }

    // The first of the two that answers; null when neither does.
    private static async Task<Task?> FirstAnswerAsync(Task one, Task other)
    {
        List<Task> pending = [one, other];
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            if (done.IsCompletedSuccessfully) return done;
            pending.Remove(done);
        }
        return null;
    }

    private static Exception Unwrap(Task task) => task.Exception?.InnerException ?? new OperationCanceledException();

    private static void Observe(Task task) => task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
