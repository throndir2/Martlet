using System.Globalization;
using System.Text;
using Martlet.Core.Contracts;
using Martlet.Providers;

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
    /// <summary>What it sends its model, when that is known when it is posted (a Thinking pool job's); a request that prepares
    /// its input later says it with <see cref="ThinkingRequest.Asked"/>. Private, as <see cref="Topic"/>.</summary>
    public ThinkingRequestInput? Input { get; init; }
}

/// <summary>What a request sent its model (its input), for this PC's Thinking requests page: the instructions (the system text),
/// the earlier messages, the message with its notes, the unfinished answer it goes on with, what went with it (a picture, a
/// recording) and the tools it could call. Private, as <see cref="ThinkingRequestInfo.Output"/>: never in logs, status files or
/// MCP (only its <see cref="Length"/>). It is made from references only, so recording it adds no time to a reply;
/// <see cref="Text"/> builds the readable text the first time it is read (at most <see cref="Kept"/> characters: the start and
/// the end, where the message is) and then lets the parts go. Thread-safe.</summary>
public sealed class ThinkingRequestInput
{
    /// <summary>How many characters of an input are kept: the first quarter from its start and the rest from its end.</summary>
    public const int Kept = 32_000;
    private readonly object gate = new();
    private Parts? parts;
    private string? text;
    private int length, leftOut;

    private sealed record Parts(string? Instructions, IReadOnlyList<TextHistoryMessage> History, string Message, string? Notes,
        string? Context, string? Continuation, (int Width, int Height, ImageMediaType Type)? Picture, TimeSpan? Recording,
        IReadOnlyList<TextToolDefinition> Tools);

    private ThinkingRequestInput(Parts parts) => this.parts = parts;

    /// <summary>A Thinking pool job's input: its instructions, its text, its picture or recording and its tools.</summary>
    public static ThinkingRequestInput Of(ThinkingJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new(new(job.Instructions, [], job.Text, null, null, null, Picture(job.Image), job.Audio?.Duration, job.Tools));
    }

    /// <summary>A conversation request's input (a think's, or a research step's).</summary>
    public static ThinkingRequestInput Of(BoundedTextInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new(new(input.Personality, input.History, input.UserText, input.Notes, input.Context, input.Continuation, Picture(input.Image),
            input.Audio?.Duration, input.Tools));
    }

    /// <summary>Instructions and a message only (a simulated request's).</summary>
    public static ThinkingRequestInput Of(string? instructions, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new(new(instructions, [], message, null, null, null, null, null, []));
    }

    private static (int, int, ImageMediaType)? Picture(BoundedImage? image) => image is null ? null : (image.Width, image.Height, image.MediaType);

    /// <summary>The input as readable text: each part under its name in brackets. A longer input keeps its first
    /// <see cref="Kept"/>/4 characters and its last ones, and says how many in the middle are left out.</summary>
    public string Text
    {
        get
        {
            lock (gate)
            {
                Build();
                return text!;
            }
        }
    }

    /// <summary>How many characters the whole input has as text (more than <see cref="Text"/> keeps when <see cref="LeftOut"/> > 0).</summary>
    public int Length
    {
        get
        {
            lock (gate)
            {
                Build();
                return length;
            }
        }
    }

    /// <summary>How many characters in the middle <see cref="Text"/> leaves out (0: none).</summary>
    public int LeftOut
    {
        get
        {
            lock (gate)
            {
                Build();
                return leftOut;
            }
        }
    }

    // Under the gate: the text, once.
    private void Build()
    {
        if (parts is not { } p) return;
        var all = new StringBuilder();
        StringBuilder Part(string title)
        {
            if (all.Length > 0) all.Append("\n\n");
            return all.Append('[').Append(title).Append("]\n");
        }
        if (!string.IsNullOrWhiteSpace(p.Instructions)) Part("Instructions").Append(p.Instructions);
        if (p.History.Count > 0)
        {
            Part(string.Create(CultureInfo.CurrentCulture, $"Earlier messages ({p.History.Count})"));
            for (var i = 0; i < p.History.Count; i++)
                all.Append(i == 0 ? "" : "\n\n").Append(p.History[i].Role == TextHistoryRole.User ? "User: " : "Assistant: ").Append(p.History[i].Text);
        }
        Part("Message").Append(p.Message);
        if (!string.IsNullOrWhiteSpace(p.Notes)) Part("Notes with the message").Append(p.Notes);
        if (!string.IsNullOrWhiteSpace(p.Context)) Part("Context with the message").Append(p.Context);
        if (!string.IsNullOrWhiteSpace(p.Continuation)) Part("Its unfinished answer, which it goes on with").Append(p.Continuation);
        List<string> with = [];
        if (p.Picture is { } picture)
            with.Add(string.Create(CultureInfo.CurrentCulture, $"A picture: {picture.Width} x {picture.Height} {(picture.Type == ImageMediaType.Jpeg ? "JPEG" : "PNG")}."));
        if (p.Recording is { } recording) with.Add(string.Create(CultureInfo.CurrentCulture, $"A recording: {recording.TotalSeconds:0.0} s."));
        if (p.Tools.Count > 0)
            with.Add(string.Create(CultureInfo.CurrentCulture, $"Tools it may call ({p.Tools.Count}): {string.Join(", ", p.Tools.Select(tool => tool.Name))}."));
        if (with.Count > 0) Part("Sent with it").AppendJoin('\n', with);
        length = all.Length;
        if (all.Length <= Kept) text = all.ToString();
        else
        {
            var head = Kept / 4;
            if (char.IsHighSurrogate(all[head - 1])) head--;
            var tail = all.Length - (Kept - head);
            if (char.IsLowSurrogate(all[tail])) tail++;
            leftOut = tail - head;
            text = string.Concat(all.ToString(0, head), string.Create(CultureInfo.CurrentCulture, $"\n\n[… {leftOut:N0} characters left out here …]\n\n"),
                all.ToString(tail, all.Length - tail));
        }
        parts = null;
    }

    public override string ToString() => $"{nameof(ThinkingRequestInput)} (content omitted)";
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
    /// <summary>What it sent its model (the newest of its <see cref="Asks"/>, or what it was posted with), or null when it isn't
    /// known yet. Private, as <see cref="Output"/>.</summary>
    public ThinkingRequestInput? Input { get; init; }
    /// <summary>How many times it said what it sends (<see cref="ThinkingRequest.Asked"/>): each attempt of a think, each step of
    /// research. 0 for a Thinking pool job, which sends what it was posted with on every try.</summary>
    public int Asks { get; init; }
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
/// request's text in a log or a status file. The private parts, for this PC's page only, are <see cref="ThinkingRequestStart.Topic"/>,
/// each request's <see cref="ThinkingRequestInfo.Input"/> (what it sent its model) and each ended request's
/// <see cref="ThinkingRequestInfo.Output"/> (its first <see cref="OutputKept"/> characters).
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

    /// <summary>Where each request's Thinking trace lines go (<see cref="ThinkingTrace"/>: when it waits in line, starts on a
    /// member, lets it go, what it waits for and how it ended); null: to the trace's listeners. It must never block.</summary>
    public Action<string>? Trace { get; set; }

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
        Log(request, posted => $"waits in line: {posted.Source switch
        {
            ThinkingRequestSource.Conversation => "the conversation's background work",
            ThinkingRequestSource.Simulated => "simulated",
            _ => "a pool job"
        }}{(posted.Task is { } task ? $" ({task})" : "")}, priority {posted.Priority ?? (int)ThinkingJobKinds.Priority(posted.Kind)}, " +
            $"needs {posted.Needs}{(posted.Timeout is { } timeout ? $", within {ThinkingTrace.Ms(timeout)} ms" : "")}" +
            $"{(posted.DropWhenStale ? ", dropped when stale" : "")}.");
        Notify();
        return request;
    }

    // One line of the Thinking trace about request (its ID, kind and holder, then what words(start) says), when anything listens.
    internal void Log(ThinkingRequest request, Func<ThinkingRequestStart, string> words)
    {
        var write = Trace ?? (ThinkingTrace.Enabled ? ThinkingTrace.Write : null);
        if (write is null) return;
        try { write($"Thinking request {request.Id} ({ThinkingJobKinds.Name(request.Start.Kind)}, {request.Start.Holder}): {words(request.Start)}"); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
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
    private ThinkingRequestInput? input;
    private int asks;

    internal ThinkingRequest(ThinkingRequests owner, long number, ThinkingRequestStart start, string? origin, DateTimeOffset posted)
    {
        this.owner = owner;
        this.number = number;
        this.start = start;
        this.origin = origin;
        this.posted = posted;
    }

    public string Id => $"tr-{number}";
    internal ThinkingRequestStart Start => start;
    internal bool Ended { get { lock (gate) return finished is not null; } }

    /// <summary>A member took it: a try starts there.</summary>
    public void Begin(BackgroundPlace member)
    {
        ArgumentNullException.ThrowIfNull(member);
        var now = owner.Clock.GetUtcNow();
        TimeSpan waited;
        int tries;
        lock (gate)
        {
            if (finished is not null) return;
            Close(now, "let go");
            waited = now - (attempts.Count == 0 ? posted : attempts[^1].Ended ?? now);
            attempts.Add(new(member.Id, member.Name, member.Model, now));
            tries = attempts.Count;
            state = ThinkingRequestState.Running;
            note = null;
        }
        owner.Log(this, _ => $"started on {member.Name}{(member.Model is { } model ? $" ({model})" : "")} after " +
            $"{ThinkingTrace.Ms(waited)} ms {(tries == 1 ? "in line" : "waiting again")}{(tries == 1 ? "" : $" (try {tries})")}.");
        owner.Notify();
    }

    /// <summary>The try on its member ended (<paramref name="ending"/> in a few words) and it waits again: in line
    /// (<see cref="ThinkingRequestState.Waiting"/>) or <paramref name="paused"/> because it had to let its slot go.</summary>
    public void End(string ending, bool paused = false, bool preempted = false)
    {
        var now = owner.Clock.GetUtcNow();
        ThinkingRequestAttempt? closed;
        lock (gate)
        {
            if (finished is not null) return;
            Close(now, ending);
            closed = attempts.Count > 0 ? attempts[^1] : null;
            state = paused ? ThinkingRequestState.Paused : ThinkingRequestState.Waiting;
            note = ending;
            if (preempted) preemptions++;
        }
        owner.Log(this, _ => (closed is null ? "" : $"let go of {closed.Member} after {ThinkingTrace.Ms(closed.Duration(now))} ms: ") +
            $"{ending}; it waits {(paused ? "(paused)" : "in line")} again.");
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
        if (words is not null) owner.Log(this, _ => $"waits: {words}.");
        owner.Notify();
    }

    /// <summary>What it sends its model now (private: <see cref="ThinkingRequestInfo.Input"/>), for a request that prepares its
    /// input once it has a member (a think, each research step). The newest replaces the one before.</summary>
    public void Asked(ThinkingRequestInput what)
    {
        ArgumentNullException.ThrowIfNull(what);
        lock (gate)
        {
            if (finished is not null) return;
            input = what;
            asks++;
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
        owner.Log(this, _ => $"{info.State} after {ThinkingTrace.Ms(info.Total)} ms: waited {ThinkingTrace.Ms(info.Waited)} ms, " +
            $"ran {ThinkingTrace.Ms(info.Ran)} ms, {info.Attempts.Count} {(info.Attempts.Count == 1 ? "try" : "tries")}" +
            (info.Last is { } last ? $" (last on {last.Member})" : "") +
            (info.Preemptions > 0 ? $", stopped {info.Preemptions} time{(info.Preemptions == 1 ? "" : "s")} for other work" : "") +
            (info.AnswerLength is { } length ? $", an answer of {length} characters" + (info.Cut ? " (cut short)" : "") : "") +
            (info.Note is { } problem ? $": {problem}." : "."));
        owner.Ended(info);
        owner.Notify();
    }

    internal ThinkingRequestInfo Info(DateTimeOffset now)
    {
        lock (gate) return InfoLocked(now);
    }

    private ThinkingRequestInfo InfoLocked(DateTimeOffset now) => new(number, start, state, posted, [.. attempts], now)
    {
        Origin = origin, Note = note, Finished = finished, Preemptions = preemptions, Cut = cut, AnswerLength = answerLength, Output = output,
        Input = input ?? start.Input, Asks = asks
    };

    // Under the gate: ends a try still open.
    private void Close(DateTimeOffset now, string ending)
    {
        if (attempts.Count > 0 && attempts[^1].Ended is null) attempts[^1] = attempts[^1] with { Ended = now, Ending = ending };
    }

    public override string ToString() => $"{nameof(ThinkingRequest)} {Id}";
}
