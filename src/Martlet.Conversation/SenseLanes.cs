using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>One request to the image or audio model (docs/SENSE_MODELS.md): its <see cref="Instructions"/> and <see cref="Text"/>
/// with one picture (an image job) or one recording (an audio job). The model answers in words; it never gets the persona, the
/// conversation's tools or its history, only what the caller puts in <see cref="Text"/> (for example the last lines said, so it
/// knows what matters). Its content is private: it never goes to logs or status files.</summary>
public sealed record SenseJob
{
    public const int MaximumOutputTokens = 1024;

    /// <summary>What it is for, in a few words, for the log and the status file ("reply picture", "glance", "your voice",
    /// "PC sounds", "screen summary").</summary>
    public required string Purpose { get; init; }

    /// <summary>A newer job with the same key replaces this one while it waits; this one then ends <see cref="SenseJobOutcome.Stale"/>.
    /// Only the newest picture of the screen is worth describing. Null: never replaced.</summary>
    public string? Key { get; init; }

    /// <summary>Higher goes first among waiting jobs (the next reply's picture before a summary).</summary>
    public int Priority { get; init; }

    public required string Instructions { get; init; }
    public required string Text { get; init; }
    public BoundedImage? Image { get; init; }
    public BoundedWaveAudio? Audio { get; init; }

    /// <summary>How long the request may run; with <see cref="DropWhenStale"/> also how long the job may wait for its lane.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Drop the job (<see cref="SenseJobOutcome.Stale"/>) when its lane isn't free within <see cref="Timeout"/>.</summary>
    public bool DropWhenStale { get; init; } = true;

    public int MaxOutputTokens { get; init; } = 400;

    /// <summary>Thinking steps: off by default (a description should start at once); null: the model's own default.</summary>
    public bool? Reasoning { get; init; } = false;

    /// <summary>Throws when the job doesn't fit <paramref name="kind"/>: an image job carries exactly one picture and no recording,
    /// an audio job exactly one recording and no picture.</summary>
    public void Validate(SenseKind kind)
    {
        ContractRules.Defined(kind);
        ContractRules.Require(Purpose is { Length: > 0 and <= 64 } && !Purpose.Any(char.IsControl), "A sense job needs a short purpose.");
        ContractRules.Require(Key is null or { Length: > 0 and <= 128 } && Key?.Any(char.IsControl) != true, "A sense job's key is short.");
        ContractRules.Require(Instructions is { Length: > 0 } && Text is { Length: > 0 }, "A sense job needs instructions and text.");
        ContractRules.Require(kind == SenseKind.Image ? Image is not null && Audio is null : Audio is not null && Image is null,
            "An image job carries one picture, an audio job one recording.");
        ContractRules.Require(Timeout > TimeSpan.Zero && Timeout <= TimeSpan.FromMinutes(2), "A sense job takes at most two minutes.");
        ContractRules.Require(MaxOutputTokens is > 0 and <= MaximumOutputTokens, $"A sense job writes at most {MaximumOutputTokens} tokens.");
    }

    public override string ToString() => $"{nameof(SenseJob)} {Purpose} (content omitted)";
}

/// <summary>How a sense job ended: <see cref="SenseJobOutcome.NoModel"/> when the kind has no model of its own that takes it now
/// (the text model takes it, or nothing does); <see cref="SenseJobOutcome.Stale"/> when a newer job with its key took its place,
/// or its lane (or the conversation's hold) didn't let it start in time; <see cref="SenseJobOutcome.Refused"/> when the model
/// refused the picture or recording (Martlet remembers that it can't see or hear); <see cref="SenseJobOutcome.Preempted"/> when a
/// reply's Thinking request started on the same computer and graphics card while it ran, so it was stopped.</summary>
public enum SenseJobOutcome { Succeeded, NoModel, Stale, Failed, TimedOut, Refused, Preempted }

/// <summary>The words a sense job got back, or why not. <see cref="Model"/> names the model ("Ollama on this PC (qwen2.5vl:7b)"),
/// <see cref="Took"/> how long the request ran (not the wait).</summary>
public sealed record SenseJobResult(SenseJobOutcome Outcome, string? Text, string? Model, string? Problem, TimeSpan Took)
{
    public bool Succeeded => Outcome == SenseJobOutcome.Succeeded && Text is { Length: > 0 };

    public static SenseJobResult NoModel(string why) => new(SenseJobOutcome.NoModel, null, null, why, TimeSpan.Zero);

    public override string ToString() => $"{nameof(SenseJobResult)} {Outcome} on {Model ?? "none"}";
}

/// <summary>What the desktop's runner made of one attempt on the model: the words, or a problem; <see cref="Refused"/> when the
/// model refused the picture or recording itself.</summary>
public sealed record SenseAnswer(string? Text, string? Problem, bool Refused = false)
{
    public static SenseAnswer Done(string text) => new(text, null);
    public static SenseAnswer Failed(string problem) => new(null, problem);
    public static SenseAnswer Rejected(string problem) => new(null, problem, true);
    public override string ToString() => $"{nameof(SenseAnswer)} (problem: {Problem is not null})";
}

/// <summary>One kind's lane now, for the status file and MCP (never a job's text): whether its model runs a job
/// (<see cref="Busy"/>), how many wait for it, how many wait for the conversation's reply (<see cref="Held"/>), and how the
/// last job ended.</summary>
public sealed record SenseLaneStatus(SenseKind Kind, bool Busy, int Waiting, int Held, int Runs, string? LastPurpose,
    SenseJobOutcome? LastOutcome, double? LastMilliseconds, DateTimeOffset? LastAt, string? LastModel, string? LastProblem);

/// <summary>The image and audio models' lanes (docs/SENSE_MODELS.md): one job at a time on each model of its own (both kinds
/// share one lane when they use the same model), so a model on this PC or a paired computer never gets two requests at once.
/// A job with a <see cref="SenseJob.Key"/> gives way to a newer job with the same key (Stale): only the newest picture is worth
/// describing. Waiting jobs start highest <see cref="SenseJob.Priority"/> first, then oldest. A kind whose route isn't
/// <see cref="SensePath.Described"/> answers <see cref="SenseJobOutcome.NoModel"/> at once.
/// <para>The conversation comes first: while <c>held</c> says a kind's model must leave the hardware to the conversation (it
/// shares the conversation's computer and graphics card, and a reply runs until its voice is all made), a job of that kind
/// doesn't start (it waits; with <see cref="SenseJob.DropWhenStale"/> it is Stale when it can't start within its timeout), and
/// one that runs is stopped (<see cref="SenseJobOutcome.Preempted"/>). The desktop's runner sends the request; this class never
/// touches a model, so MCP rehearses it with a simulated runner.</para></summary>
public sealed class SenseLanes
{
    /// <summary>How often a job that waits for the conversation, or runs on hardware it shares, looks at the hold again.</summary>
    public static TimeSpan HoldPoll => TimeSpan.FromMilliseconds(20);

    private readonly Func<SenseKind, SenseRoute> route;
    private readonly Func<SenseKind, DeepThinkingSettings, SenseJob, CancellationToken, Task<SenseAnswer>> run;
    private readonly Func<SenseKind, bool>? held;
    private readonly TimeProvider clock;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lane> lanes = new(StringComparer.Ordinal);
    private readonly Record[] records = [new(), new()];

    /// <param name="route">Where each kind goes now (read for every job, so a new choice applies to the next job).</param>
    /// <param name="run">Sends one job to the kind's model of its own and returns its words or why not.</param>
    /// <param name="held">Whether a kind's model must leave the hardware to the conversation's reply now (null: never).</param>
    public SenseLanes(Func<SenseKind, SenseRoute> route, Func<SenseKind, DeepThinkingSettings, SenseJob, CancellationToken, Task<SenseAnswer>> run,
        TimeProvider? clock = null, Func<SenseKind, bool>? held = null)
    {
        this.route = route ?? throw new ArgumentNullException(nameof(route));
        this.run = run ?? throw new ArgumentNullException(nameof(run));
        this.held = held;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>Raised (possibly off the caller's thread) when a job waits, starts or ends, for the status file.</summary>
    public event Action? Changed;

    /// <summary>Runs <paramref name="job"/> on <paramref name="kind"/>'s model of its own and returns its words, or why not.
    /// Canceling <paramref name="token"/> throws <see cref="OperationCanceledException"/>.</summary>
    public async Task<SenseJobResult> RunAsync(SenseKind kind, SenseJob job, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(job);
        job.Validate(kind);
        token.ThrowIfCancellationRequested();
        var now = route(kind);
        if (now is not { Path: SensePath.Described, Model: { } model }) return SenseJobResult.NoModel(now.Why);
        var record = records[(int)kind];
        var name = model.Describe();
        var lane = lanes.GetOrAdd(model.Key, _ => new Lane());
        var mine = lane.Arrive(job.Key);
        using var stale = job.DropWhenStale ? new CancellationTokenSource(job.Timeout, clock) : new CancellationTokenSource();
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token, stale.Token);
        if (lane.Enter(job, mine) is { } turn)
        {
            Changed?.Invoke();
            bool started;
            try { started = await turn.Task.WaitAsync(wait.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // Given the lane just as the wait ended: it is this job's, and must be passed on.
                if (!lane.Leave(turn)) Exit(lane);
                Changed?.Invoke();
                token.ThrowIfCancellationRequested();
                return Ended(record, job, new(SenseJobOutcome.Stale, null, name, "its model was busy with another job", TimeSpan.Zero));
            }
            if (!started) return Ended(record, job, new(SenseJobOutcome.Stale, null, name, "a newer job took its place", TimeSpan.Zero));
        }
        // The lane is this job's from here; Exit passes it on.
        SenseJobResult result;
        try
        {
            result = await StartAsync(kind, model, job, lane, mine, record, wait.Token, token).ConfigureAwait(false);
        }
        finally { Exit(lane); }
        return Ended(record, job, result);
    }

    // Waits while the conversation holds the hardware, then runs the job, stopping it when the conversation needs the hardware.
    private async Task<SenseJobResult> StartAsync(SenseKind kind, DeepThinkingSettings model, SenseJob job, Lane lane, long mine, Record record,
        CancellationToken wait, CancellationToken token)
    {
        var name = model.Describe();
        if (!lane.Newest(job.Key, mine)) return new(SenseJobOutcome.Stale, null, name, "a newer job took its place", TimeSpan.Zero);
        if (held?.Invoke(kind) == true)
        {
            Interlocked.Increment(ref record.Holding);
            Changed?.Invoke();
            try
            {
                while (held(kind))
                {
                    await Task.Delay(HoldPoll, clock, wait).ConfigureAwait(false);
                    if (!lane.Newest(job.Key, mine)) return new(SenseJobOutcome.Stale, null, name, "a newer job took its place", TimeSpan.Zero);
                }
            }
            catch (OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
                return new(SenseJobOutcome.Stale, null, name, "the conversation's reply kept its computer busy", TimeSpan.Zero);
            }
            finally
            {
                Interlocked.Decrement(ref record.Holding);
                Changed?.Invoke();
            }
        }
        Changed?.Invoke();
        var began = clock.GetTimestamp();
        using var timer = new CancellationTokenSource(job.Timeout, clock);
        using var preempt = new CancellationTokenSource();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token, timer.Token, preempt.Token);
        using var done = new CancellationTokenSource();
        var watching = held is null ? Task.CompletedTask : WatchAsync(kind, preempt, done.Token);
        try
        {
            var answer = await run(kind, model, job, limit.Token).ConfigureAwait(false);
            var took = clock.GetElapsedTime(began);
            return answer switch
            {
                { Text: { } text } when !string.IsNullOrWhiteSpace(text) => new(SenseJobOutcome.Succeeded, text.Trim(), name, null, took),
                { Refused: true } => new(SenseJobOutcome.Refused, null, name, answer.Problem, took),
                _ when preempt.IsCancellationRequested => Preempted(name, took),
                _ => new(SenseJobOutcome.Failed, null, name, answer.Problem ?? "it came back empty", took)
            };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            var took = clock.GetElapsedTime(began);
            return preempt.IsCancellationRequested ? Preempted(name, took)
                : new(SenseJobOutcome.TimedOut, null, name, "it didn't answer in time", took);
        }
        finally
        {
            done.Cancel();
            await watching.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static SenseJobResult Preempted(string name, TimeSpan took) =>
        new(SenseJobOutcome.Preempted, null, name, "a reply started on the same computer, so it was stopped", took);

    // While a job runs on hardware the conversation may need: stops it as soon as a reply needs the hardware.
    private async Task WatchAsync(SenseKind kind, CancellationTokenSource preempt, CancellationToken done)
    {
        try
        {
            while (!done.IsCancellationRequested)
            {
                await Task.Delay(HoldPoll, clock, done).ConfigureAwait(false);
                if (held!(kind))
                {
                    preempt.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Each kind's lane now: the lane of the model it goes to (busy and waiting), and its own jobs (held, runs, last).</summary>
    public IReadOnlyList<SenseLaneStatus> Status() =>
    [
        .. new[] { SenseKind.Image, SenseKind.Audio }.Select(kind =>
        {
            var lane = route(kind) is { Path: SensePath.Described, Model: { } model } && lanes.TryGetValue(model.Key, out var found) ? found : null;
            var (busy, waiting) = lane?.Load() ?? (false, 0);
            return records[(int)kind].Status(kind, busy, waiting);
        })
    ];

    private void Exit(Lane lane)
    {
        lane.Release();
        Changed?.Invoke();
    }

    private SenseJobResult Ended(Record record, SenseJob job, SenseJobResult result)
    {
        record.Note(job.Purpose, result, clock.GetUtcNow());
        Changed?.Invoke();
        return result;
    }

    public override string ToString() => nameof(SenseLanes);

    // One kind's own jobs: how many wait for the conversation, how many ran and how the last one ended.
    private sealed class Record
    {
        private readonly object gate = new();
        internal int Holding;
        private int runs;
        private (string Purpose, SenseJobResult Result, DateTimeOffset At)? last;

        internal void Note(string purpose, SenseJobResult result, DateTimeOffset at)
        {
            lock (gate)
            {
                if (result.Outcome is SenseJobOutcome.Succeeded or SenseJobOutcome.Failed or SenseJobOutcome.Refused or
                    SenseJobOutcome.TimedOut or SenseJobOutcome.Preempted)
                    runs++;
                last = (purpose, result, at);
            }
        }

        internal SenseLaneStatus Status(SenseKind kind, bool busy, int waiting)
        {
            lock (gate)
                return new(kind, busy, waiting, Volatile.Read(ref Holding), runs, last?.Purpose, last?.Result.Outcome,
                    last is { } done && done.Result.Took > TimeSpan.Zero ? Math.Round(done.Result.Took.TotalMilliseconds) : null,
                    last?.At, last?.Result.Model, last?.Result.Problem);
        }
    }

    // One model's line: one job at a time, the others waiting by priority, then age.
    private sealed class Lane
    {
        private readonly object gate = new();
        private readonly List<Waiter> waiting = [];
        private readonly Dictionary<string, long> newest = new(StringComparer.Ordinal);
        private long order;
        private bool busy;

        // A new job's place in line; with a key, it is now the newest job with that key.
        internal long Arrive(string? key)
        {
            lock (gate)
            {
                var mine = ++order;
                if (key is not null) newest[key] = mine;
                return mine;
            }
        }

        // Whether no newer job with the same key arrived since.
        internal bool Newest(string? key, long mine)
        {
            lock (gate) return key is null || !newest.TryGetValue(key, out var latest) || latest == mine;
        }

        // Null: the lane was free and is now the job's. Otherwise the job waits for the task: true when it may start, false when
        // a newer job with its key took its place.
        internal TaskCompletionSource<bool>? Enter(SenseJob job, long mine)
        {
            lock (gate)
            {
                if (job.Key is { } key)
                    foreach (var replaced in waiting.Where(w => w.Key == key).ToArray())
                    {
                        waiting.Remove(replaced);
                        replaced.Turn.TrySetResult(false);
                    }
                if (!busy)
                {
                    busy = true;
                    return null;
                }
                var turn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                waiting.Add(new(turn, job.Key, job.Priority, mine));
                return turn;
            }
        }

        // Leaves the line without starting. False when the lane was given to it meanwhile: then the caller passes it on.
        internal bool Leave(TaskCompletionSource<bool> turn)
        {
            lock (gate)
            {
                var index = waiting.FindIndex(w => w.Turn == turn);
                if (index < 0) return turn.Task is not { IsCompletedSuccessfully: true, Result: true };
                waiting.RemoveAt(index);
                return true;
            }
        }

        // Passes the lane to the waiting job with the highest priority (then the oldest), or frees it.
        internal void Release()
        {
            lock (gate)
            {
                var next = waiting.OrderByDescending(w => w.Priority).ThenBy(w => w.Order).FirstOrDefault();
                if (next is null)
                {
                    busy = false;
                    return;
                }
                waiting.Remove(next);
                next.Turn.TrySetResult(true);
            }
        }

        internal (bool Busy, int Waiting) Load()
        {
            lock (gate) return (busy, waiting.Count);
        }

        private sealed record Waiter(TaskCompletionSource<bool> Turn, string? Key, int Priority, long Order);
    }
}