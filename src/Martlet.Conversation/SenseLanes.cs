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
/// (the text model takes it, or nothing does), <see cref="SenseJobOutcome.Stale"/> when a newer job replaced it or its lane
/// wasn't free in time, <see cref="SenseJobOutcome.Refused"/> when the model refused the picture or recording (Martlet remembers
/// that it can't see or hear).</summary>
public enum SenseJobOutcome { Succeeded, NoModel, Stale, Failed, TimedOut, Refused }

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

/// <summary>One sense's lane now, for the status file and MCP (never a job's text).</summary>
public sealed record SenseLaneStatus(SenseKind Kind, bool Busy, int Waiting, int Runs, string? LastPurpose, SenseJobOutcome? LastOutcome,
    double? LastMilliseconds, DateTimeOffset? LastAt, string? LastModel, string? LastProblem);

/// <summary>The image and audio models' lanes (docs/SENSE_MODELS.md): one job at a time on each kind's model, so a model on this
/// PC or a paired computer never gets two requests at once. A waiting job with the same <see cref="SenseJob.Key"/> as a newer one
/// is replaced (Stale); waiting jobs start highest <see cref="SenseJob.Priority"/> first, then oldest. A kind whose route isn't
/// <see cref="SensePath.Described"/> answers <see cref="SenseJobOutcome.NoModel"/> at once. The desktop's runner sends the
/// request; this class never touches a model, so MCP rehearses it with a simulated runner.</summary>
public sealed class SenseLanes
{
    private readonly Func<SenseKind, SenseRoute> route;
    private readonly Func<SenseKind, DeepThinkingSettings, SenseJob, CancellationToken, Task<SenseAnswer>> run;
    private readonly TimeProvider clock;
    private readonly Lane[] lanes = [new(SenseKind.Image), new(SenseKind.Audio)];

    /// <param name="route">Where each kind goes now (read for every job, so a new choice applies to the next job).</param>
    /// <param name="run">Sends one job to the kind's model of its own and returns its words or why not.</param>
    public SenseLanes(Func<SenseKind, SenseRoute> route, Func<SenseKind, DeepThinkingSettings, SenseJob, CancellationToken, Task<SenseAnswer>> run,
        TimeProvider? clock = null)
    {
        this.route = route ?? throw new ArgumentNullException(nameof(route));
        this.run = run ?? throw new ArgumentNullException(nameof(run));
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>Raised (off the caller's thread is possible) after a job starts or ends, for the status file.</summary>
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
        var lane = lanes[(int)kind];
        var turn = lane.Enter(job);
        if (turn is not null)
        {
            Changed?.Invoke();
            using var stale = job.DropWhenStale ? new CancellationTokenSource(job.Timeout, clock) : new CancellationTokenSource();
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token, stale.Token);
            bool started;
            try { started = await turn.Task.WaitAsync(wait.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // Started just as the wait ended: the lane is ours and must be passed on.
                if (!lane.Leave(turn)) Exit(lane);
                Changed?.Invoke();
                token.ThrowIfCancellationRequested();
                return Ended(lane, job, new(SenseJobOutcome.Stale, null, model.Describe(), "the model was busy with another job", TimeSpan.Zero));
            }
            if (!started)
            {
                Changed?.Invoke();
                return Ended(lane, job, new(SenseJobOutcome.Stale, null, model.Describe(), "a newer job took its place", TimeSpan.Zero));
            }
        }
        Changed?.Invoke();
        var began = clock.GetTimestamp();
        SenseJobResult result;
        try
        {
            using var timer = new CancellationTokenSource(job.Timeout, clock);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token, timer.Token);
            try
            {
                var answer = await run(kind, model, job, limit.Token).ConfigureAwait(false);
                var took = clock.GetElapsedTime(began);
                result = answer switch
                {
                    { Text: { } text } when !string.IsNullOrWhiteSpace(text) => new(SenseJobOutcome.Succeeded, text.Trim(), model.Describe(), null, took),
                    { Refused: true } => new(SenseJobOutcome.Refused, null, model.Describe(), answer.Problem, took),
                    _ => new(SenseJobOutcome.Failed, null, model.Describe(), answer.Problem ?? "it came back empty", took)
                };
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                result = new(SenseJobOutcome.TimedOut, null, model.Describe(), "it didn't answer in time", clock.GetElapsedTime(began));
            }
        }
        finally { Exit(lane); }
        return Ended(lane, job, result);
    }

    /// <summary>Each kind's lane now.</summary>
    public IReadOnlyList<SenseLaneStatus> Status() => [.. lanes.Select(lane => lane.Status())];

    private void Exit(Lane lane)
    {
        lane.Release();
        Changed?.Invoke();
    }

    private SenseJobResult Ended(Lane lane, SenseJob job, SenseJobResult result)
    {
        lane.Record(job.Purpose, result, clock.GetUtcNow());
        Changed?.Invoke();
        return result;
    }

    public override string ToString() => nameof(SenseLanes);

    private sealed class Lane(SenseKind kind)
    {
        private readonly object gate = new();
        private readonly List<Waiter> waiting = [];
        private long order;
        private bool busy;
        private int runs;
        private (string Purpose, SenseJobResult Result, DateTimeOffset At)? last;

        // Null: the lane was free and is now the job's. Otherwise the job waits for the task: true when it may start, false when
        // a newer job with its key replaced it.
        internal TaskCompletionSource<bool>? Enter(SenseJob job)
        {
            lock (gate)
            {
                if (!busy)
                {
                    busy = true;
                    return null;
                }
                if (job.Key is { } key)
                    foreach (var replaced in waiting.Where(w => w.Key == key).ToArray())
                    {
                        waiting.Remove(replaced);
                        replaced.Turn.TrySetResult(false);
                    }
                var turn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                waiting.Add(new(turn, job.Key, job.Priority, ++order));
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

        internal void Record(string purpose, SenseJobResult result, DateTimeOffset at)
        {
            lock (gate)
            {
                if (result.Outcome is SenseJobOutcome.Succeeded or SenseJobOutcome.Failed or SenseJobOutcome.Refused or SenseJobOutcome.TimedOut)
                    runs++;
                last = (purpose, result, at);
            }
        }

        internal SenseLaneStatus Status()
        {
            lock (gate)
                return new(kind, busy, waiting.Count, runs, last?.Purpose, last?.Result.Outcome,
                    last is { } done && done.Result.Took > TimeSpan.Zero ? Math.Round(done.Result.Took.TotalMilliseconds) : null,
                    last?.At, last?.Result.Model, last?.Result.Problem);
        }

        private sealed record Waiter(TaskCompletionSource<bool> Turn, string? Key, int Priority, long Order);
    }
}