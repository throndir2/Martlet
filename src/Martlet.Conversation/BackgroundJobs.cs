using System.Globalization;
using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>Where a background job is: waiting to start (such as checking it fits beside the conversation), working, paused,
/// or finished one way or another.</summary>
public enum BackgroundJobState { Waiting, Running, Paused, Succeeded, Failed, TimedOut, Canceled }

/// <summary>A kind of background work Martlet starts during a conversation and brings up when it is done (think_longer's
/// "think"; later "song"): its <paramref name="Name"/> (lowercase, the job IDs' prefix: think-1, song-1), how many may run at
/// once, how many may start in any hour, how long one may take, whether its result is something to <paramref name="Offer"/>
/// (a song to play: Martlet asks first and a later tool acts on the user's yes) rather than simply share, and what the talk
/// window calls a running one (<paramref name="Doing"/>: "Thinking about", then its label).</summary>
public sealed record BackgroundJobKind(string Name, int MaxActive, int MaxPerHour, TimeSpan TimeLimit, bool Offer = false,
    string Doing = "Working on")
{
    public void Validate()
    {
        ContractRules.Require(Name is { Length: > 0 and <= 16 } && Name.All(c => c is >= 'a' and <= 'z'),
            "A background job kind is 1-16 lowercase letters.");
        ContractRules.Require(MaxActive is >= 1 and <= 8 && MaxPerHour is >= 1 and <= 60 &&
            TimeLimit >= TimeSpan.FromSeconds(1) && TimeLimit <= TimeSpan.FromMinutes(30),
            "A background job kind allows 1-8 at once, 1-60 an hour and at most 30 minutes each.");
        ContractRules.Require(Doing is { Length: > 0 and <= 40 }, "What a running job is called must be 1-40 characters.");
    }
}

/// <summary>What a job produced: its <paramref name="Result"/> for the conversation (the text the model gets back), or the
/// <paramref name="Problem"/> in a few plain words when it failed. <paramref name="Cut"/>: the result stopped early (an output
/// limit) but what came is usable.</summary>
public sealed record BackgroundJobOutcome(string? Result, string? Problem = null, bool Cut = false)
{
    public static BackgroundJobOutcome Done(string result, bool cut = false) => new(result, null, cut);
    public static BackgroundJobOutcome Failed(string problem) => new(null, problem);
    public override string ToString() => $"{nameof(BackgroundJobOutcome)} (problem: {Problem is not null})";
}

/// <summary>How a finished job reaches the conversation: <see cref="Pending"/> until Martlet brings it up (a reply of its own as
/// soon as it is free, or with the user's next message), <see cref="Reserved"/> while a reply carries it, then
/// <see cref="Delivered"/>; <see cref="Dropped"/> when the conversation it belonged to ended.</summary>
public enum BackgroundDeliveryState { NotFinished, Pending, Reserved, Delivered, Dropped }

/// <summary>One piece of background work. Only its runner and <see cref="BackgroundJobs"/> change it; everything here is safe
/// to read from any thread.</summary>
public sealed class BackgroundJob
{
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly long startedAt;
    private long finishedAt;
    private BackgroundJobState state = BackgroundJobState.Waiting;
    private string? progress, result, problem, canceledBy;
    private bool cut;
    private BackgroundDeliveryState delivery = BackgroundDeliveryState.NotFinished;
    internal readonly CancellationTokenSource Cancellation = new();
    internal string? cancelReason;
    internal Action? Changed;

    internal BackgroundJob(BackgroundJobKind kind, string id, string label, TimeProvider clock)
    {
        Kind = kind;
        Id = id;
        Label = label;
        this.clock = clock;
        startedAt = clock.GetTimestamp();
        StartedUtc = clock.GetUtcNow();
    }

    /// <summary>The job's ID in the conversation and the talk window, such as think-1.</summary>
    public string Id { get; }
    public BackgroundJobKind Kind { get; }
    /// <summary>A short description for the talk window and the conversation (what the job is about, from the conversation;
    /// never written to logs or status files).</summary>
    public string Label { get; }
    public DateTimeOffset StartedUtc { get; }
    public DateTimeOffset? FinishedUtc { get; private set; }
    public BackgroundJobState State { get { lock (gate) return state; } }
    /// <summary>What its runner says it is doing, in a few words (such as "checking it fits beside Thinking"), or null.</summary>
    public string? Progress { get { lock (gate) return progress; } }
    public string? Result { get { lock (gate) return result; } }
    public string? Problem { get { lock (gate) return problem; } }
    public bool Cut { get { lock (gate) return cut; } }
    /// <summary>Who or what stopped it ("you", "Martlet", "the conversation ended"), when it was canceled.</summary>
    public string? CanceledBy { get { lock (gate) return canceledBy; } }
    public BackgroundDeliveryState Delivery { get { lock (gate) return delivery; } }
    public bool Finished => State is BackgroundJobState.Succeeded or BackgroundJobState.Failed or BackgroundJobState.TimedOut or
        BackgroundJobState.Canceled;
    /// <summary>A job the user or Martlet stopped is only mentioned with the user's next message, never brought up on its own.</summary>
    public bool Quiet => State == BackgroundJobState.Canceled;

    /// <summary>How long it has run (until it finished).</summary>
    public TimeSpan Elapsed
    {
        get
        {
            long end;
            lock (gate) end = finishedAt;
            return end == 0 ? clock.GetElapsedTime(startedAt) : clock.GetElapsedTime(startedAt, end);
        }
    }

    /// <summary>The runner says what it is doing now: <see cref="BackgroundJobState.Waiting"/>, <see cref="BackgroundJobState.Running"/>
    /// or <see cref="BackgroundJobState.Paused"/>, with an optional few words.</summary>
    public void Report(BackgroundJobState now, string? note = null)
    {
        ContractRules.Require(now is BackgroundJobState.Waiting or BackgroundJobState.Running or BackgroundJobState.Paused,
            "A runner reports waiting, running or paused; finishing is the job list's.");
        lock (gate)
        {
            if (state is not (BackgroundJobState.Waiting or BackgroundJobState.Running or BackgroundJobState.Paused)) return;
            if (state == now && progress == note) return;
            state = now;
            progress = note;
        }
        Changed?.Invoke();
    }

    internal void Finish(BackgroundJobState end, BackgroundJobOutcome? outcome, string? by)
    {
        lock (gate)
        {
            if (finishedAt != 0) return;
            finishedAt = clock.GetTimestamp();
            FinishedUtc = clock.GetUtcNow();
            state = end;
            progress = null;
            result = outcome?.Result;
            problem = outcome?.Problem;
            cut = outcome?.Cut == true;
            canceledBy = end == BackgroundJobState.Canceled ? by ?? CanceledByYou : null;
            // Martlet stopped it itself (it knows), or the conversation ended: nothing to bring up.
            delivery = by is CanceledByMartlet ? BackgroundDeliveryState.Delivered
                : by is CanceledByClosing ? BackgroundDeliveryState.Dropped : BackgroundDeliveryState.Pending;
        }
    }

    internal bool MoveDelivery(BackgroundDeliveryState from, BackgroundDeliveryState to)
    {
        lock (gate)
        {
            if (delivery != from) return false;
            delivery = to;
            return true;
        }
    }

    /// <summary>Who stopped a job (<see cref="CanceledBy"/>): the user (the talk window's Cancel), Martlet itself (its cancel
    /// tool; nothing is brought up later), or the conversation ending (nothing is brought up any more).</summary>
    public const string CanceledByYou = "you", CanceledByMartlet = "Martlet", CanceledByClosing = "the conversation ended";

    public override string ToString() => $"{nameof(BackgroundJob)} {Id} ({State})";
}

/// <summary>Why a job didn't start: <c>busy</c> (as many of its kind as allowed are running), <c>hourly_limit</c>, <c>closed</c>
/// (Martlet is closing) or <c>off</c> (its feature is turned off), with what to tell the model.</summary>
public sealed record BackgroundJobStart(BackgroundJob? Job, string? Refusal = null, string? Message = null, BackgroundJob? Running = null)
{
    public bool Started => Job is not null;
}

/// <summary>Finished jobs on their way into the conversation: <see cref="Complete"/> once the reply that carries them was added
/// to it, or <see cref="Return"/> when that reply didn't happen, so the next one carries them.</summary>
public sealed class BackgroundDelivery
{
    private readonly BackgroundJobs owner;
    private int done;

    internal BackgroundDelivery(BackgroundJobs owner, IReadOnlyList<BackgroundJob> jobs)
    {
        this.owner = owner;
        Jobs = jobs;
    }

    public IReadOnlyList<BackgroundJob> Jobs { get; }

    public void Complete()
    {
        if (Interlocked.Exchange(ref done, 1) != 0) return;
        foreach (var job in Jobs) job.MoveDelivery(BackgroundDeliveryState.Reserved, BackgroundDeliveryState.Delivered);
        owner.Notify();
    }

    public void Return()
    {
        if (Interlocked.Exchange(ref done, 1) != 0) return;
        foreach (var job in Jobs) job.MoveDelivery(BackgroundDeliveryState.Reserved, BackgroundDeliveryState.Pending);
        owner.Notify();
    }

    public override string ToString() => $"{nameof(BackgroundDelivery)} ({Jobs.Count} jobs)";
}

/// <summary>Martlet's background work during a conversation (think_longer is the first kind; a song is next): each job runs on
/// its own beside the conversation, within its kind's limits (how many at once, how many an hour, how long each), can be
/// canceled at any time, and once it finishes waits to be brought into the conversation (<see cref="Take"/>) as a note at the
/// end of it. A new kind registers by starting jobs with its own <see cref="BackgroundJobKind"/> and runner; the job list does
/// the limits, cancellation, time limit and delivery. Thread-safe; <see cref="Changed"/> is raised on any thread.</summary>
public sealed class BackgroundJobs : IDisposable
{
    /// <summary>How many finished jobs are kept for status and delivery.</summary>
    public const int Kept = 16;
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly List<BackgroundJob> jobs = [];
    private readonly List<(string Kind, DateTimeOffset At)> starts = [];
    private readonly Dictionary<string, int> numbers = new(StringComparer.Ordinal);
    private bool disposed;

    public BackgroundJobs(TimeProvider? clock = null) => this.clock = clock ?? TimeProvider.System;

    /// <summary>Raised on any thread when a job starts, changes state, finishes or is delivered.</summary>
    public event Action? Changed;

    public TimeProvider Clock => clock;

    /// <summary>Every job not finished yet, oldest first.</summary>
    public IReadOnlyList<BackgroundJob> Active { get { lock (gate) return [.. jobs.Where(job => !job.Finished)]; } }

    /// <summary>The finished jobs kept, newest first.</summary>
    public IReadOnlyList<BackgroundJob> Recent
    {
        get { lock (gate) return [.. jobs.Where(job => job.Finished).OrderByDescending(job => job.FinishedUtc)]; }
    }

    /// <summary>Finished jobs not yet brought into the conversation, oldest first.</summary>
    public IReadOnlyList<BackgroundJob> Undelivered
    {
        get { lock (gate) return [.. jobs.Where(job => job.Delivery == BackgroundDeliveryState.Pending).OrderBy(job => job.FinishedUtc)]; }
    }

    /// <summary>Whether a finished job is waiting that Martlet should bring up on its own (not only one the user stopped).</summary>
    public bool HasNews { get { lock (gate) return jobs.Any(job => job.Delivery == BackgroundDeliveryState.Pending && !job.Quiet); } }

    /// <summary>How many jobs of <paramref name="kind"/> started in the last hour.</summary>
    public int StartedWithinHour(string kind)
    {
        lock (gate)
        {
            Prune();
            return starts.Count(start => start.Kind == kind);
        }
    }

    /// <summary>Starts one job of <paramref name="kind"/> labeled <paramref name="label"/> (a few words about what it is), or
    /// says why not. <paramref name="run"/> does the work on a thread-pool thread with a token that is canceled by
    /// <see cref="Cancel"/>, <see cref="CancelAll"/> and the kind's time limit; it returns what the job produced (or throws
    /// <see cref="OperationCanceledException"/> once canceled). It returns at once.</summary>
    public BackgroundJobStart Start(BackgroundJobKind kind, string label, Func<BackgroundJob, CancellationToken, Task<BackgroundJobOutcome>> run)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(run);
        kind.Validate();
        ContractRules.Require(!string.IsNullOrWhiteSpace(label) && label.Length <= 200, "A background job needs a short label.");
        BackgroundJob job;
        lock (gate)
        {
            if (disposed) return new(null, "closed", "Martlet is closing, so it can't start anything now.");
            var running = jobs.Where(job => job.Kind.Name == kind.Name && !job.Finished).ToArray();
            if (running.Length >= kind.MaxActive)
                return new(null, "busy", running.Length == 1
                    ? $"{running[0].Id} is still running and only one {kind.Name} runs at a time." : $"{running.Length} are still running.",
                    running[0]);
            Prune();
            if (starts.Count(start => start.Kind == kind.Name) >= kind.MaxPerHour)
                return new(null, "hourly_limit", $"{kind.MaxPerHour} already started in the last hour, the most allowed.");
            var number = numbers[kind.Name] = numbers.GetValueOrDefault(kind.Name) + 1;
            job = new(kind, $"{kind.Name}-{number}", label.Trim(), clock) { Changed = Notify };
            jobs.Add(job);
            starts.Add((kind.Name, clock.GetUtcNow()));
            Trim();
        }
        _ = Task.Run(() => RunAsync(job, run));
        Notify();
        return new(job);
    }

    private async Task RunAsync(BackgroundJob job, Func<BackgroundJob, CancellationToken, Task<BackgroundJobOutcome>> run)
    {
        using var limit = new CancellationTokenSource(job.Kind.TimeLimit, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, limit.Token);
        try
        {
            var outcome = await run(job, linked.Token).ConfigureAwait(false);
            if (linked.IsCancellationRequested && outcome.Result is null) throw new OperationCanceledException(linked.Token);
            job.Finish(outcome.Result is not null ? BackgroundJobState.Succeeded : BackgroundJobState.Failed,
                outcome.Result is null && outcome.Problem is null ? outcome with { Problem = "it came back empty" } : outcome, null);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            if (job.Cancellation.IsCancellationRequested) job.Finish(BackgroundJobState.Canceled, null, Volatile.Read(ref job.cancelReason));
            else job.Finish(BackgroundJobState.TimedOut, BackgroundJobOutcome.Failed(
                $"it ran into the {Duration(job.Kind.TimeLimit)} time limit"), null);
        }
        catch (Exception)
        {
            // A runner can fail arbitrarily; the conversation only hears that it failed, never the exception text.
            job.Finish(BackgroundJobState.Failed, BackgroundJobOutcome.Failed("it failed on this PC"), null);
        }
        Notify();
    }

    /// <summary>Stops job <paramref name="id"/> (null: the newest of <paramref name="kind"/>) if it is still running;
    /// <paramref name="by"/> says who: "you" (the talk window's Cancel) or "Martlet" (its cancel tool; then nothing is brought
    /// up later, since it knows). Returns the job stopped, or null.</summary>
    public BackgroundJob? Cancel(string? id, string by, string? kind = null)
    {
        BackgroundJob? job;
        lock (gate)
            job = jobs.LastOrDefault(item => !item.Finished && (id is null ? kind is null || item.Kind.Name == kind : item.Id == id));
        if (job is null) return null;
        Volatile.Write(ref job.cancelReason, by);
        job.Cancellation.Cancel();
        return job;
    }

    /// <summary>The conversation ended (its window closed, Martlet is quitting): every running job stops, and nothing that
    /// finished is brought up any more.</summary>
    public void CancelAll()
    {
        BackgroundJob[] stopping;
        lock (gate)
        {
            stopping = [.. jobs.Where(job => !job.Finished)];
            foreach (var job in jobs) job.MoveDelivery(BackgroundDeliveryState.Pending, BackgroundDeliveryState.Dropped);
        }
        foreach (var job in stopping)
        {
            Volatile.Write(ref job.cancelReason, BackgroundJob.CanceledByClosing);
            job.Cancellation.Cancel();
        }
        Notify();
    }

    /// <summary>Takes the finished jobs waiting to be brought up, for one reply to carry. <paramref name="onItsOwn"/>: a reply
    /// Martlet starts by itself, which happens only when there is news (a job the user stopped waits for their next message).
    /// Null when nothing waits.</summary>
    public BackgroundDelivery? Take(bool onItsOwn)
    {
        BackgroundDelivery? taken = null;
        lock (gate)
        {
            var waiting = jobs.Where(job => job.Delivery == BackgroundDeliveryState.Pending).OrderBy(job => job.FinishedUtc).ToArray();
            if (waiting.Length > 0 && (!onItsOwn || waiting.Any(job => !job.Quiet)))
            {
                var moved = waiting.Where(job => job.MoveDelivery(BackgroundDeliveryState.Pending, BackgroundDeliveryState.Reserved)).ToArray();
                if (moved.Length > 0) taken = new(this, moved);
            }
        }
        if (taken is not null) Notify();
        return taken;
    }

    internal void Notify() => Changed?.Invoke();

    private void Prune()
    {
        var hourAgo = clock.GetUtcNow() - TimeSpan.FromHours(1);
        starts.RemoveAll(start => start.At <= hourAgo);
    }

    // Finished jobs that were delivered (or dropped) go first once more than Kept are finished.
    private void Trim()
    {
        while (jobs.Count(job => job.Finished) > Kept &&
            jobs.FirstOrDefault(job => job.Finished && job.Delivery is BackgroundDeliveryState.Delivered or BackgroundDeliveryState.Dropped) is { } old)
            jobs.Remove(old);
    }

    /// <summary>"1 min 42 s", "40 s", "5 minutes".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span.TotalSeconds < 60) return $"{Math.Max(0, (int)span.TotalSeconds)} s";
        if (span.Seconds == 0 && span.TotalMinutes == Math.Floor(span.TotalMinutes))
            return $"{(int)span.TotalMinutes} minute{((int)span.TotalMinutes == 1 ? "" : "s")}";
        return $"{(int)span.TotalMinutes} min {span.Seconds} s";
    }

    /// <summary>"0:12", "1:42": the talk window's clock for a running job.</summary>
    public static string Clockface(TimeSpan span) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}:{span.Seconds:00}");

    /// <summary>The finished jobs as the conversation is told about them (Martlet's note, never the user's words): each job's
    /// ID, label and how it ended, and its result (at most <paramref name="limit"/> characters of results in all, so the message
    /// carrying them stays sendable), which a kind that offers its result marks to be offered first.</summary>
    public static string Results(IEnumerable<BackgroundJob> finished, int limit = 10_000)
    {
        var jobs = finished.ToArray();
        var share = Math.Max(500, limit / Math.Max(1, jobs.Count(job => job.State == BackgroundJobState.Succeeded)));
        var text = new StringBuilder();
        foreach (var job in jobs)
        {
            if (text.Length > 0) text.Append("\n\n");
            text.Append("- ").Append(job.Id).Append(" (").Append(job.Label).Append("): ");
            switch (job.State)
            {
                case BackgroundJobState.Succeeded:
                    var result = job.Result ?? "";
                    var clipped = result.Length > share;
                    text.Append("done after ").Append(Duration(job.Elapsed))
                        .Append(job.Cut || clipped ? ", though it was cut off before the end" : "")
                        .Append(job.Kind.Offer ? ". It needs the user's go-ahead: offer it and ask before you use it." : ".")
                        .Append("\nResult:\n").Append(clipped ? result[..share].TrimEnd() + "…" : result);
                    break;
                case BackgroundJobState.Canceled:
                    text.Append(job.CanceledBy == BackgroundJob.CanceledByYou ? "the user canceled it, so there's no result." : "it was stopped, so there's no result.");
                    break;
                case BackgroundJobState.TimedOut:
                    text.Append("it ran out of time (").Append(job.Problem).Append("), so there's no result.");
                    break;
                default:
                    text.Append("it didn't work out: ").Append(job.Problem ?? "it failed").Append('.');
                    break;
            }
        }
        return text.ToString();
    }

    /// <summary>The message of the reply Martlet starts on its own to bring up <paramref name="finished"/> jobs (Companion ›
    /// Prompts › Background work finished): its note with the results, shortened until a request can carry it.</summary>
    public static BoundedTextInput ReportMessage(PromptSettings? prompts, IReadOnlyList<BackgroundJob> finished)
    {
        for (var limit = 10_000; ; limit /= 2)
        {
            var text = PromptSettings.Fill(prompts, PromptCatalog.BackgroundDone, ("results", Results(finished, limit)))!;
            try { return new(text); }
            catch (ContractException) when (limit > 500) { }
        }
    }

    /// <summary>What goes in the notes of the user's next message about <paramref name="finished"/> jobs not brought up yet
    /// (Companion › Prompts › Background work finished, with your message), or null when that prompt was emptied.</summary>
    public static string? ReportNotes(PromptSettings? prompts, IReadOnlyList<BackgroundJob> finished) =>
        PromptSettings.Fill(prompts, PromptCatalog.BackgroundDoneNotes, ("results", Results(finished, 6_000)));

    /// <summary>Stops every job for good (Martlet is closing).</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        CancelAll();
    }
}
