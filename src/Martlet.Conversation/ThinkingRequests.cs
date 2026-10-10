using Martlet.Core.Contracts;

namespace Martlet.Conversation;

/// <summary>Where a request in <see cref="ThinkingRequests"/> comes from: a Thinking pool job (<see cref="ThinkingJobBoard"/>), the
/// conversation's background work on the pool's slots (think_longer, research: <see cref="BackgroundJobs"/>), or a simulated one
/// (only to check the Thinking requests page; it sends nothing).</summary>
public enum ThinkingRequestSource { Pool, Conversation, Simulated }

/// <summary>Where a request is now. <see cref="Waiting"/>: in line for a slot; <see cref="Paused"/>: it let its slot go (the
/// conversation needed it, or its computer stopped answering) and waits for another. The others are how it ended.</summary>
public enum ThinkingRequestState { Waiting, Running, Paused, Succeeded, Failed, TimedOut, Stale, NoMember, Preempted, Canceled }

/// <summary>One try of a request on a member: where, with which model, when it started and ended, and how it ended in a few words
/// (never the request's text or answer).</summary>
public sealed record ThinkingRequestAttempt(string MemberId, string Member, string? Model, DateTimeOffset Started)
{
    public DateTimeOffset? Ended { get; init; }
    public string? Ending { get; init; }
    public TimeSpan Duration(DateTimeOffset now) => (Ended ?? now) - Started;
}

/// <summary>What a request is when it is posted: its kind, the few words that say what it is for (<see cref="Task"/>, never
/// private), and what it asks of the pool. <see cref="Topic"/> is private (what the conversation asked for, such as a think's
/// label): it shows on this PC's Thinking requests page only, never in logs or status files.</summary>
public sealed record ThinkingRequestStart(ThinkingJobKind Kind, ThinkingRequestSource Source, string Holder)
{
    public string? Task { get; init; }
    public string? Topic { get; init; }
    /// <summary>The companion that asked; null: <see cref="ThinkingRequests.Origin"/>.</summary>
    public string? Origin { get; init; }
    public int? Priority { get; init; }
    public ThinkingCapability Needs { get; init; } = ThinkingCapability.Text;
    public TimeSpan? Timeout { get; init; }
    public bool DropWhenStale { get; init; }
    public int? MaxOutputTokens { get; init; }
    public bool? Reasoning { get; init; }
    public int Tools { get; init; }
    /// <summary>The conversation's ID for it (think_longer-1), for <see cref="ThinkingRequestSource.Conversation"/>.</summary>
    public string? JobId { get; init; }
}

/// <summary>A request as it is now (see <see cref="ThinkingRequests"/>): what it is, where it is, every try and its timings. The
/// live line position and whether it waits for the conversation come from the board's places at the time of the snapshot.</summary>
public sealed record ThinkingRequestInfo(long Number, ThinkingRequestStart Start, ThinkingRequestState State, DateTimeOffset Posted,
    IReadOnlyList<ThinkingRequestAttempt> Attempts, DateTimeOffset Now)
{
    public string Id => $"tr-{Number}";
    public ThinkingJobKind Kind => Start.Kind;
    public string KindName => ThinkingJobKinds.Name(Kind);
    public int Priority => Start.Priority ?? (int)ThinkingJobKinds.Priority(Kind);
    public bool Fast => ThinkingJobKinds.IsFast(Kind);
    public string? Origin { get; init; }
    /// <summary>What it says now (what it waits for, or why it ended), in a few plain words.</summary>
    public string? Note { get; init; }
    public DateTimeOffset? Finished { get; init; }
    public int Preemptions { get; init; }
    public bool Cut { get; init; }
    /// <summary>How many characters its answer had.</summary>
    public int? AnswerLength { get; init; }
    /// <summary>What it answered (its first <see cref="ThinkingRequests.OutputKept"/> characters), so the owner sees what it adds
    /// to the conversation. Private, as <see cref="ThinkingRequestStart.Topic"/>: it shows on this PC's Thinking requests page
    /// only, never in logs, status files or MCP.</summary>
    public string? Output { get; init; }
    /// <summary>Its place in line (1 is next), or 0 when it isn't in line.</summary>
    public int Position { get; init; }
    /// <summary>A slot is free, but the live conversation needs it while the user talks.</summary>
    public bool HeldForConversation { get; init; }

    public bool Done => State is not (ThinkingRequestState.Waiting or ThinkingRequestState.Running or ThinkingRequestState.Paused);
    public bool Active => !Done;
    /// <summary>The tries after the first.</summary>
    public int Retries => Math.Max(0, Attempts.Count - 1);
    public ThinkingRequestAttempt? Last => Attempts.Count == 0 ? null : Attempts[^1];
    public DateTimeOffset? Started => Attempts.Count == 0 ? null : Attempts[0].Started;
    private DateTimeOffset End => Finished ?? Now;
    /// <summary>From posted to finished (or now).</summary>
    public TimeSpan Total => Max(End - Posted);
    /// <summary>Time on a member, every try together.</summary>
    public TimeSpan Ran => Max(TimeSpan.FromTicks(Attempts.Sum(attempt => attempt.Duration(End).Ticks)));
    /// <summary>Time in line, all waits together (the first, and each wait after a try).</summary>
    public TimeSpan Waited => Max(Total - Ran);
    /// <summary>From posted to its first try (or until now, or until it ended without one).</summary>
    public TimeSpan FirstWait => Max((Started ?? End) - Posted);

    private static TimeSpan Max(TimeSpan span) => span < TimeSpan.Zero ? TimeSpan.Zero : span;

    public override string ToString() => $"{nameof(ThinkingRequestInfo)} {Id} {KindName} {State}";
}

/// <summary>The totals of one kind since Martlet started: how many ended which way, and how long they waited and ran.</summary>
public sealed record ThinkingRequestTotals(int Count, int Succeeded, int Problems, TimeSpan Waited, TimeSpan MaxWaited, TimeSpan Ran,
    TimeSpan MaxRan, int Attempts, int Preemptions)
{
    public static ThinkingRequestTotals Empty { get; } = new(0, 0, 0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0, 0);
    public TimeSpan AverageWait => Count == 0 ? TimeSpan.Zero : Waited / Count;
    public TimeSpan AverageRun => Count == 0 ? TimeSpan.Zero : Ran / Count;
    public int Retries => Math.Max(0, Attempts - Count);

    internal ThinkingRequestTotals Add(ThinkingRequestInfo request) => new(Count + 1,
        Succeeded + (request.State == ThinkingRequestState.Succeeded ? 1 : 0),
        Problems + (request.State is ThinkingRequestState.Succeeded or ThinkingRequestState.Canceled ? 0 : 1),
        Waited + request.Waited, request.Waited > MaxWaited ? request.Waited : MaxWaited, Ran + request.Ran,
        request.Ran > MaxRan ? request.Ran : MaxRan, Attempts + request.Attempts.Count, Preemptions + request.Preemptions);
}

/// <summary>Every request for the Thinking pool's slots, with its timings: Thinking pool jobs (the judges, summaries, check-ins,
/// memory, naming, touch zones) and the conversation's background work on the same slots (think_longer, research). It keeps every
/// request that hasn't ended and the last <see cref="Kept"/> that ended, plus totals by kind since Martlet started. It keeps no
/// request's text. The private parts, for this PC's page only, are <see cref="ThinkingRequestStart.Topic"/> and each ended
/// request's <see cref="ThinkingRequestInfo.Output"/> (its first <see cref="OutputKept"/> characters).
/// Recording is cheap (a lock and a list), so it never adds time to a reply. Thread-safe; <see cref="Changed"/> is raised on
/// any thread, outside the lock.</summary>
public sealed class ThinkingRequests
{
    /// <summary>How many ended requests are kept.</summary>
    public const int Kept = 200;
    /// <summary>How many characters of each request's output are kept (the rest is cut off).</summary>
    public const int OutputKept = 16_000;
    private readonly object gate = new();
    private readonly List<ThinkingRequest> requests = [];
    private readonly Dictionary<ThinkingJobKind, ThinkingRequestTotals> totals = [];
    private long number;

    public ThinkingRequests(TimeProvider? clock = null) => Clock = clock ?? TimeProvider.System;

    public TimeProvider Clock { get; }

    /// <summary>The companion that posts requests now (its name), when a request doesn't say.</summary>
    public Func<string?>? Origin { get; set; }

    /// <summary>Raised on any thread when a request is posted, changes or ends.</summary>
    public event Action? Changed;

    /// <summary>Records a new request, waiting in line.</summary>
    public ThinkingRequest Post(ThinkingRequestStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        ContractRules.Defined(start.Kind);
        ContractRules.Defined(start.Source);
        ContractRules.Require(!string.IsNullOrWhiteSpace(start.Holder), "A Thinking request needs its holder.");
        string? origin;
        try { origin = start.Origin ?? Origin?.Invoke(); }
        catch (Exception error) when (error is not OutOfMemoryException) { origin = null; }
        ThinkingRequest request;
        lock (gate)
        {
            request = new(this, ++number, start with { Task = Short(start.Task), Topic = Short(start.Topic) }, Short(origin), Clock.GetUtcNow());
            requests.Add(request);
            Trim();
        }
        Notify();
        return request;
    }

    /// <summary>Every request kept: the ones not ended first (oldest first), then the ended ones (newest first). With
    /// <paramref name="places"/>, each waiting one has its line position and whether it waits for the conversation.</summary>
    public IReadOnlyList<ThinkingRequestInfo> List(BackgroundPlaces? places = null)
    {
        ThinkingRequest[] all;
        lock (gate) all = [.. requests];
        var now = Clock.GetUtcNow();
        var infos = all.Select(request => request.Info(now)).ToArray();
        if (places is not null)
            infos = [.. infos.Select(info => info.State == ThinkingRequestState.Running || info.Done ? info : info with
            {
                Position = places.Position(info.Start.Holder), HeldForConversation = places.HeldForConversation(info.Start.Holder)
            })];
        return [.. infos.Where(info => info.Active).OrderBy(info => info.Number), .. infos.Where(info => info.Done).OrderByDescending(info => info.Number)];
    }

    /// <summary>The totals by kind of every request that ended since Martlet started (more than <see cref="Kept"/>).</summary>
    public IReadOnlyDictionary<ThinkingJobKind, ThinkingRequestTotals> Totals
    {
        get { lock (gate) return new Dictionary<ThinkingJobKind, ThinkingRequestTotals>(totals); }
    }

    /// <summary>How many requests haven't ended (in line, running or paused).</summary>
    public int ActiveCount { get { lock (gate) return requests.Count(request => !request.Ended); } }

    /// <summary>Forgets the ended requests kept (the totals stay).</summary>
    public void ClearFinished()
    {
        lock (gate) requests.RemoveAll(request => request.Ended);
        Notify();
    }

    internal void Ended(ThinkingRequestInfo info)
    {
        lock (gate)
        {
            totals[info.Kind] = totals.GetValueOrDefault(info.Kind, ThinkingRequestTotals.Empty).Add(info);
            Trim();
        }
    }

    internal void Notify()
    {
        try { Changed?.Invoke(); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    // Under the gate: the oldest ended requests go once more than Kept ended.
    private void Trim()
    {
        var ended = requests.Count(request => request.Ended);
        for (var i = 0; ended > Kept && i < requests.Count;)
        {
            if (requests[i].Ended) { requests.RemoveAt(i); ended--; }
            else i++;
        }
    }

    private static string? Short(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().ReplaceLineEndings(" ");
        return text.Length <= 200 ? text : text[..199] + "…";
    }

    public override string ToString() => nameof(ThinkingRequests);
}

/// <summary>One request in <see cref="ThinkingRequests"/>, for its poster to say what happens: <see cref="Begin"/> when a member
/// takes it, <see cref="End"/> when a try ends and it waits again, <see cref="Say"/> while it waits, and <see cref="Finish"/>
/// once (later calls do nothing). Thread-safe.</summary>
public sealed class ThinkingRequest
{
    private readonly ThinkingRequests owner;
    private readonly object gate = new();
    private readonly List<ThinkingRequestAttempt> attempts = [];
    private readonly long number;
    private readonly ThinkingRequestStart start;
    private readonly string? origin;
    private readonly DateTimeOffset posted;
    private ThinkingRequestState state = ThinkingRequestState.Waiting;
    private string? note;
    private DateTimeOffset? finished;
    private int preemptions;
    private bool cut;
    private int? answerLength;
    private string? output;

    internal ThinkingRequest(ThinkingRequests owner, long number, ThinkingRequestStart start, string? origin, DateTimeOffset posted)
    {
        this.owner = owner;
        this.number = number;
        this.start = start;
        this.origin = origin;
        this.posted = posted;
    }

    public string Id => $"tr-{number}";
    internal bool Ended { get { lock (gate) return finished is not null; } }

    /// <summary>A member took it: a try starts there.</summary>
    public void Begin(BackgroundPlace member)
    {
        ArgumentNullException.ThrowIfNull(member);
        var now = owner.Clock.GetUtcNow();
        lock (gate)
        {
            if (finished is not null) return;
            Close(now, "let go");
            attempts.Add(new(member.Id, member.Name, member.Model, now));
            state = ThinkingRequestState.Running;
            note = null;
        }
        owner.Notify();
    }

    /// <summary>The try on its member ended (<paramref name="ending"/> in a few words) and it waits again: in line
    /// (<see cref="ThinkingRequestState.Waiting"/>) or <paramref name="paused"/> because it had to let its slot go.</summary>
    public void End(string ending, bool paused = false, bool preempted = false)
    {
        var now = owner.Clock.GetUtcNow();
        lock (gate)
        {
            if (finished is not null) return;
            Close(now, ending);
            state = paused ? ThinkingRequestState.Paused : ThinkingRequestState.Waiting;
            note = ending;
            if (preempted) preemptions++;
        }
        owner.Notify();
    }

    /// <summary>What it waits for now, in a few words (null: nothing to say).</summary>
    public void Say(string? words)
    {
        lock (gate)
        {
            if (finished is not null || note == words) return;
            note = words;
        }
        owner.Notify();
    }

    /// <summary>It ended (<paramref name="end"/>, an ended state) with <paramref name="why"/>, the problem in a few words, if
    /// any, and <paramref name="text"/>, what it answered (private: <see cref="ThinkingRequestInfo.Output"/>; its length is the
    /// answer's when <paramref name="answer"/> is null). A try still open ends with it.</summary>
    public void Finish(ThinkingRequestState end, string? why = null, int? answer = null, bool wasCut = false, int? stops = null,
        string? text = null)
    {
        ContractRules.Require(end is not (ThinkingRequestState.Waiting or ThinkingRequestState.Running or ThinkingRequestState.Paused),
            "A Thinking request finishes with how it ended.");
        if (string.IsNullOrEmpty(text)) text = null;
        answer ??= text?.Length;
        // A reference when it fits, so the reply's path pays nothing for it.
        if (text is { Length: > ThinkingRequests.OutputKept }) text = text[..ThinkingRequests.OutputKept];
        var now = owner.Clock.GetUtcNow();
        ThinkingRequestInfo info;
        lock (gate)
        {
            if (finished is not null) return;
            Close(now, end switch
            {
                ThinkingRequestState.Succeeded => "answered",
                ThinkingRequestState.TimedOut => "ran out of time",
                ThinkingRequestState.Canceled => "canceled",
                ThinkingRequestState.Preempted => "stopped for the conversation",
                _ => why ?? "failed"
            });
            state = end;
            finished = now;
            note = why;
            answerLength = answer;
            output = text;
            cut = wasCut;
            if (stops is { } count) preemptions = Math.Max(preemptions, count);
            info = InfoLocked(now);
        }
        owner.Ended(info);
        owner.Notify();
    }

    internal ThinkingRequestInfo Info(DateTimeOffset now)
    {
        lock (gate) return InfoLocked(now);
    }

    private ThinkingRequestInfo InfoLocked(DateTimeOffset now) => new(number, start, state, posted, [.. attempts], now)
    {
        Origin = origin, Note = note, Finished = finished, Preemptions = preemptions, Cut = cut, AnswerLength = answerLength, Output = output
    };

    // Under the gate: ends a try still open.
    private void Close(DateTimeOffset now, string ending)
    {
        if (attempts.Count > 0 && attempts[^1].Ended is null) attempts[^1] = attempts[^1] with { Ended = now, Ending = ending };
    }

    public override string ToString() => $"{nameof(ThinkingRequest)} {Id}";
}
