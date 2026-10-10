using System.Text;
using Martlet.Providers;

namespace Martlet.Conversation;

// The turn's Thinking trace (ThinkingTrace): one line when the turn starts, one per request and per tool round, Backup
// Thinking's choice, one when the turn ends, and "still waiting" lines while a step it waits for takes long. Nothing here waits:
// the trace's listener never blocks, and the turn's own work never waits for a line (the start line is made off its path, the
// waits are noticed by the supervisor).
public sealed partial class ConversationTurn
{
    // Where this turn writes (null: nowhere, and nothing is made), and its name in the lines ("Thinking turn 12 (reply)").
    private readonly Action<string>? trace;
    private readonly string traceName = "";
    // The requests sent so far, the one under way (guarded by Sync) and what the turn waits for now (written by the worker,
    // read by the supervisor).
    private int traceRequests;
    private RequestTrace? tracedRequest;
    private TraceWait? waitingFor;
    // 1 while the read loop waits for the voice to take its next piece (its queue is full), so a stall then is the voice's.
    private int staging;

    /// <summary>This turn's name in the Thinking trace ("Thinking turn 12 (reply)"), for a caller's own lines about it; null when
    /// nothing listened as it started.</summary>
    public string? TraceName => trace is null ? null : traceName;

    /// <summary>Writes one more line about this turn to where its trace goes (after its name and a colon); nothing when it isn't
    /// traced. Never blocks.</summary>
    public void Note(string words)
    {
        if (trace is not null && !string.IsNullOrWhiteSpace(words)) TraceLine(": " + words.Trim());
    }

    // One request's steps, in the turn's own time. Guarded by Sync once the request is under way.
    private sealed class RequestTrace(int number, string why, TimeSpan began)
    {
        internal int Number { get; } = number;
        internal string Why { get; } = why;
        internal TimeSpan Began { get; } = began;
        internal TimeSpan? Authorized { get; set; }
        internal ITextGenerationStream? Stream { get; set; }
        internal TimeSpan StreamAfter { get; set; }
        internal bool Backup { get; set; }
        internal TimeSpan? FirstWords { get; set; }
        internal long LastWordsAt { get; set; }
        internal int Characters { get; set; }
    }

    // What the turn waits for, since when, and (for the supervisor only) how many times it said so since Base.
    private sealed class TraceWait(string what, long since, bool answer = false)
    {
        internal string What { get; } = what;
        internal long Since { get; } = since;
        internal bool Answer { get; } = answer;
        internal long Base { get; set; } = since;
        internal int Notices { get; set; }
    }

    private static (Action<string>? Trace, string Name) TraceFor(ConversationRuntime owner, string? purpose)
    {
        var trace = owner.Trace ?? (ThinkingTrace.Enabled ? ThinkingTrace.Write : null);
        if (trace is null) return (null, "");
        var name = string.IsNullOrWhiteSpace(purpose) ? "Thinking" : purpose.Trim().ReplaceLineEndings(" ");
        if (name.Length > 80) name = name[..79] + "…";
        return (trace, $"Thinking turn {ThinkingTrace.NextTurn()} ({name})");
    }

    private void TraceLine(string text)
    {
        if (trace is null) return;
        try { trace(traceName + text); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    // Makes a line; a line that can't be made is left out, so the trace never changes what the turn does.
    private static string? Safely(Func<string?> make)
    {
        try { return make(); }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }

    private void TraceLine(Func<string?> make)
    {
        if (trace is not null && Safely(make) is { } text) TraceLine(text);
    }

    private static string Ms(TimeSpan span) => ThinkingTrace.Ms(span);
    private static string Ms(TimeSpan? span) => span is { } value ? ThinkingTrace.Ms(value) : "-";
    private static string Count(int count, string what) => $"{count} {what}{(count == 1 ? "" : "s")}";
    private static string Seconds(TimeSpan span) => span.TotalSeconds >= 1 && span.TotalSeconds % 1 == 0
        ? $"{span.TotalSeconds:0} s" : $"{span.TotalMilliseconds:0} ms";

    // What the turn waits for now; null: nothing (it works, or it is done with Thinking).
    private void TraceWaiting(string? what, bool answer = false)
    {
        if (trace is null) return;
        Volatile.Write(ref waitingFor, what is null ? null : new TraceWait(what, Clock.GetTimestamp(), answer));
    }

    // The start line: what the turn asks and where. Made on the thread pool, so the turn's first request never waits for it.
    private void TraceStart()
    {
        if (trace is null) return;
        ThreadPool.UnsafeQueueUserWorkItem(static turn => turn.TraceLine(turn.Started), this, preferLocal: false);
    }

    private string Started()
    {
        var input = request.Input;
        var parts = new List<string>
        {
            $"{Route()}, model {request.Model.UpstreamModelId}",
            $"input of about {input.InputTokenReservation} tokens ({input.Utf8Bytes} bytes) with {Count(input.History.Count, "earlier message")}" +
                (input.Tools.Count > 0 ? $", {Count(input.Tools.Count, "tool")}" : "") +
                (input.Audio is { } audio ? $", a recording of {audio.Duration.TotalSeconds:0.0} s" : "") +
                (input.Image is not null ? ", a picture" : ""),
            request.Speech is null ? "text only" : "spoken"
        };
        if (hold is not null) parts.Add(holdVoice ? "started early" : "started early, its first piece made meanwhile");
        if (request.Generation?.Reasoning is { } reasoning) parts.Add($"Thinking steps {(reasoning ? "on" : "off")}");
        if (request.Backup is { } backup) parts.Add($"Backup Thinking after {Ms(backup.Delay)} ms");
        if (request.Fallback is { } fallback)
            parts.Add($"Thinking fallback {ThinkingTrace.Endpoint(fallback.Chat.BaseUrl)}, model {fallback.Model.UpstreamModelId}");
        var limits = request.TextLimits;
        parts.Add($"at most {limits.MaxOutputTokens} output tokens; first words within {Seconds(limits.FirstDeltaTimeout)}, " +
            $"no words for at most {Seconds(limits.IdleTimeout)}, each request within {Seconds(limits.MaxRequestTime)}, " +
            $"the turn within {Seconds(request.Limits.TurnTimeout)}");
        return " started: " + string.Join("; ", parts) + ".";
    }

    private string Route() => request.Host is { } host
        ? $"Martlet host {host.HostId}" + (host.RouteId == Martlet.Core.Settings.SelfHostSetup.OllamaRouteId ? "" : $" ({host.RouteId})") +
            (host.Background ? ", as background work" : "")
        : request.Chat is { } chat ? $"Chat Completions {ThinkingTrace.Endpoint(chat.BaseUrl)}" + (chat.Keyless ? " (no key)" : "")
        : "OpenAI Responses";

    // A request begins: it waits for its authorization first.
    private RequestTrace? TraceRequest(string why)
    {
        if (trace is null) return null;
        var traced = new RequestTrace(++traceRequests, why, Clock.GetElapsedTime(startedAt));
        lock (Sync) tracedRequest = traced;
        TraceWaiting($"request {traced.Number}'s authorization");
        return traced;
    }

    // Under Sync: the request is authorized and its stream opened; it waits for its answer now.
    private void TraceAuthorized(TimeSpan at)
    {
        if (tracedRequest is not { } traced) return;
        traced.Authorized = at;
        TraceWaiting($"request {traced.Number}'s answer", answer: true);
    }

    // Under Sync: the stream that answers the request (the backup's, when it won the race).
    private void TraceStream(ITextGenerationStream stream, TimeSpan after, bool backup = false)
    {
        if (tracedRequest is not { } traced) return;
        traced.Stream = stream;
        traced.StreamAfter = after;
        traced.Backup = backup;
    }

    // Under Sync: words of the request arrived.
    private void TraceWords(int characters)
    {
        if (tracedRequest is not { } traced) return;
        traced.FirstWords ??= Clock.GetElapsedTime(startedAt);
        traced.LastWordsAt = Clock.GetTimestamp();
        traced.Characters += characters;
    }

    // The read loop hands words to the voice; it waits there while the voice's queue is full (a long reply, a reply started
    // early and not taken yet, or one paused for the user), which is no silence of the model's.
    private void TraceStaging(bool on)
    {
        if (trace is not null) Volatile.Write(ref staging, on ? 1 : 0);
    }

    // The request ended (with its result, or an error): one line with each of its steps.
    private void TraceRequestEnded(RequestTrace? traced, RoundResult? result, Exception? error)
    {
        if (traced is null) return;
        TraceWaiting(null);
        string? line;
        lock (Sync)
        {
            if (ReferenceEquals(tracedRequest, traced)) tracedRequest = null;
            line = Safely(() => RequestLine(traced, result, error, Clock.GetElapsedTime(startedAt)));
        }
        if (line is not null) TraceLine(line);
    }

    private static string RequestLine(RequestTrace traced, RoundResult? result, Exception? error, TimeSpan end)
    {
        var outcome = result switch
        {
            { End: RoundEnd.Completed, Calls.Count: > 0 } => $"answered with {Count(result.Calls.Count, "tool call")}",
            { End: RoundEnd.Completed } => "answered",
            { End: RoundEnd.Refused } => "was refused",
            { End: RoundEnd.Failed } => $"failed ({result.Failure?.ToString() ?? result.Issue?.ToString() ?? "no answer"})",
            { End: RoundEnd.Invalid } => $"ended unfinished ({result.Issue?.ToString() ?? result.Failure?.ToString() ?? "no answer"})",
            _ => error switch
            {
                OperationCanceledException => "was stopped",
                ConversationException failed => $"ended ({failed.Failure})",
                Martlet.Core.Contracts.ContractException => "ended (InvalidStream)",
                _ => $"failed ({error?.GetType().Name ?? "unknown"})"
            }
        };
        var line = new StringBuilder($": request {traced.Number} ({traced.Why}) {outcome} at {Ms(end)} ms");
        var steps = new List<string>();
        if (traced.Number > 1) steps.Add($"began at {Ms(traced.Began)} ms");
        if (traced.Authorized is { } authorized) steps.Add($"authorized at {Ms(authorized)} ms");
        if (traced.Stream is { } stream)
        {
            var after = traced.StreamAfter;
            if (traced.Backup) steps.Add($"Backup Thinking's stream from {Ms(after)} ms");
            if (stream.SentAfter is { } sent) steps.Add($"sent at {Ms(after + sent)} ms");
            steps.Add(stream.ResponseAfter is { } response ? $"response at {Ms(after + response)} ms" : "no response time");
            if (stream.FirstReasoningAfter is { } reasoning) steps.Add($"hidden reasoning from {Ms(after + reasoning)} ms");
        }
        steps.Add(traced.FirstWords is { } words ? $"first words at {Ms(words)} ms" : "no words");
        if (traced.Characters > 0) steps.Add(Count(traced.Characters, "character"));
        line.Append(": ").Append(string.Join(", ", steps));
        if (traced.Stream?.Result?.Usage is { } usage && (usage.InputTokens ?? usage.OutputTokens) is not null)
            line.Append($"; {usage.InputTokens?.ToString() ?? "?"} input tokens" +
                (usage.CachedInputTokens is { } cached ? $" ({cached} from the prompt cache)" : "") +
                $", {usage.OutputTokens?.ToString() ?? "?"} output tokens");
        return line.Append('.').ToString();
    }

    // A tool round ended: each call and how long it took.
    private void TraceTools(int round, IReadOnlyList<(string Name, TimeSpan Took, bool Failed)> calls, TimeSpan began)
    {
        if (trace is null || calls.Count == 0) return;
        TraceLine(() => $": tool round {round}: " + string.Join(", ", calls.Select(c => $"{c.Name} {Ms(c.Took)} ms{(c.Failed ? " (failed)" : "")}")) +
            $"; {Ms(Clock.GetElapsedTime(startedAt) - began)} ms in all.");
    }

    // Backup Thinking decided (what the desktop log also says once the reply ends).
    private void TraceBackup(ThinkingBackupResult result)
    {
        if (trace is null) return;
        TraceLine(() => ": " + (result.Describe() ?? $"Backup Thinking wasn't needed: the model answered within its {Ms(result.Delay)} ms."));
    }

    // Under Sync, from the supervisor: a line when what the turn waits for has lasted 2, 5, 10, 20, 30 s and every 30 s after.
    // Once words flow, what counts is how long since the last words.
    private string? DueNotice() => Safely(Notice);

    private string? Notice()
    {
        if (Volatile.Read(ref waitingFor) is not { } wait || invalidated || workFinished) return null;
        var now = Clock.GetTimestamp();
        var traced = wait.Answer ? tracedRequest : null;
        var since = traced is { LastWordsAt: > 0 } flowing && flowing.LastWordsAt > wait.Since ? flowing.LastWordsAt : wait.Since;
        if (since != wait.Base)
        {
            wait.Base = since;
            wait.Notices = 0;
        }
        var waited = Clock.GetElapsedTime(since, now);
        if (waited < ThinkingTrace.Notice(wait.Notices)) return null;
        wait.Notices++;
        var at = Ms(Clock.GetElapsedTime(startedAt, now));
        if (traced is { FirstWords: not null })
            return Volatile.Read(ref staging) != 0
                ? $": still waiting at {at} ms for the voice to take the next piece ({Ms(waited)} ms so far; request {traced.Number} " +
                    $"has {Count(traced.Characters, "character")} so far" +
                    (heldBack is not null ? "; the reply started early and isn't taken yet" : paused ? "; the reply is paused for you" : "") + ")."
                : $": still waiting at {at} ms: no new words from request {traced.Number} for {Ms(waited)} ms " +
                    $"({Count(traced.Characters, "character")} so far).";
        var detail = traced is null ? null : traced.Stream is not { } stream ? "its stream isn't open yet"
            : string.Join(", ", new[]
            {
                stream.SentAfter is { } sent ? $"sent at {Ms(traced.StreamAfter + sent)} ms" : null,
                stream.ResponseAfter is { } response ? $"response at {Ms(traced.StreamAfter + response)} ms" : "no response yet",
                stream.FirstReasoningAfter is { } reasoning ? $"hidden reasoning since {Ms(traced.StreamAfter + reasoning)} ms" : null,
                traced.Backup ? "Backup Thinking's stream" : null
            }.OfType<string>());
        return $": still waiting at {at} ms for {wait.What} ({Ms(waited)} ms so far{(detail is null ? "" : "; " + detail)}).";
    }

    // The end line, from the turn's final snapshot.
    private void TraceEnd(ConversationSnapshot final)
    {
        if (trace is null) return;
        Volatile.Write(ref waitingFor, null);
        TraceLine(() => Ended(final));
    }

    private string Ended(ConversationSnapshot final)
    {
        var line = new StringBuilder($" ended at {Ms(Clock.GetElapsedTime(startedAt))} ms: {final.State}");
        var why = new List<string>();
        if (final.Failure != ConversationFailure.None) why.Add(final.Failure.ToString());
        if (final.ProviderFailure is { } provider) why.Add($"provider {provider}" + (final.FailedProvider is { } role ? $" ({role})" : ""));
        if (final.SequenceFailure is { } sequence) why.Add($"stream {sequence.Issue}");
        if (why.Count > 0) line.Append(" (").Append(string.Join(", ", why)).Append(')');
        var parts = new List<string> { Count(traceRequests, "request") };
        if (final.ToolCalls > 0) parts.Add(Count(final.ToolCalls, "tool call"));
        parts.Add(final.FirstTextAfter is { } words ? $"first words at {Ms(words)} ms" : "no words");
        if (final.FirstAudioAfter is { } audio) parts.Add($"first audio at {Ms(audio)} ms");
        parts.Add(Count(final.TextCharacters, "character"));
        if (request.Speech is not null) parts.Add(Count(final.CommittedSegments, "spoken piece"));
        if (final.SpeechFailed) parts.Add($"the voice stopped ({final.SpeechFailure})");
        if (final.VoiceMuted) parts.Add("the voice was muted");
        if (final.FellBackAfter is { } fellBack) parts.Add($"the Thinking fallback was asked after {fellBack}");
        if (final.ToolsRejected) parts.Add("the model refused tools");
        if (final.AudioRejected) parts.Add("the model refused the recording");
        if (final.ImageRejected) parts.Add("the model refused the picture");
        if (final.ReasoningRejected) parts.Add("the model refused the Thinking steps choice");
        if (final.Timings?.StartedEarly == true)
            parts.Add(final.Timings.ReleasedAfter is { } taken ? $"started early, taken as the reply at {Ms(taken)} ms" : "started early, never taken");
        line.Append("; ").Append(string.Join(", ", parts)).Append('.');
        return line.ToString();
    }
}
