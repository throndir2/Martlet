using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers;

// Also built into Martlet's MCP server (audio_model_check), in its own namespace.
#if MARTLET_MCP
namespace Martlet.Mcp.Shared;
#else
namespace Martlet.Desktop;
#endif

/// <summary>The audio model's words about how the user sounded in one utterance (docs/SENSE_MODELS.md, Recordings: the audio
/// model). While an audio model of its own takes recordings (the audio path is Described), it hears each utterance beside
/// speech-to-text and puts what the words miss into words for Thinking (<see cref="VoiceNotes.Job"/>). A reply never waits for
/// them: its request takes them only when they are ready as it is built (<see cref="TryPeek"/>, then <see cref="TryTake"/> once
/// the request was sent), and the request marks the others late (<see cref="MarkLate"/>), so they go to the context board for
/// the next request once they come. On a model that shares the conversation's computer and graphics card (<see cref="Shared"/>)
/// the job starts only once the reply's request runs, or no reply follows (<see cref="Release"/>), so its words are always late.
/// The words never go to logs or status files.</summary>
internal sealed class VoiceNote
{
    private const int Open = 0, Taken = 1, Late = 2, Canceled = 3;
    private readonly object gate = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource mayStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource cancel = new();
    private readonly TimeProvider clock;
    private int state;
    private long readyAt;

    internal VoiceNote(TimeProvider clock, long endedAt, bool shared, Guid conversation, string model)
    {
        this.clock = clock;
        EndedAt = endedAt;
        Shared = shared;
        Conversation = conversation;
        Model = model;
    }

    /// <summary>The controller-clock timestamp the utterance ended at.</summary>
    internal long EndedAt { get; }
    /// <summary>The audio model shares the conversation's computer and graphics card: the job waits for <see cref="Release"/>.</summary>
    internal bool Shared { get; }
    /// <summary>The conversation the utterance belongs to: words that come late after it was cleared are dropped.</summary>
    internal Guid Conversation { get; }
    /// <summary>The audio model, in words ("Ollama on this PC (gemma3n:e4b)").</summary>
    internal string Model { get; }

    internal Task Ready => ready.Task;
    internal bool IsReady => ready.Task.IsCompleted;
    /// <summary>Completes once a job on a shared model may start (<see cref="Release"/>).</summary>
    internal Task MayStart => mayStart.Task;
    internal CancellationToken Token => cancel.Token;

    /// <summary>The first line of the audio model's words (what the conversation keeps), once ready; null for none or when the
    /// job didn't succeed.</summary>
    internal string? Summary { get; private set; }
    /// <summary>The rest of its words, or null.</summary>
    internal string? Details { get; private set; }
    /// <summary>How the job ended (null: it never ran, it was canceled).</summary>
    internal SenseJobOutcome? Outcome { get; private set; }
    /// <summary>Why there are no words, in a few words (never what was said).</summary>
    internal string? Problem { get; private set; }
    /// <summary>How long the audio model's request ran.</summary>
    internal TimeSpan Took { get; private set; }

    /// <summary>How long after the utterance ended the job was done; null until then.</summary>
    internal TimeSpan? ReadyAfter => Interlocked.Read(ref readyAt) is var at && at == 0 ? null : clock.GetElapsedTime(EndedAt, at);

    /// <summary>A request carried the words.</summary>
    internal bool WasTaken => Volatile.Read(ref state) == Taken;
    /// <summary>A request was sent without them: they go to the context board once they come.</summary>
    internal bool IsLate => Volatile.Read(ref state) == Late;
    internal bool IsCanceled => Volatile.Read(ref state) == Canceled;

    /// <summary>The reply's request runs now, or no reply follows: a job on a shared model may start (it still waits while a
    /// reply makes its voice, <c>SenseLanes</c>).</summary>
    internal void Release() => mayStart.TrySetResult();

    /// <summary>Nobody needs the words (what was said wasn't words, another voice, or it was let go): the job stops, and words
    /// that come anyway go nowhere. A note a request carried already stays as it was.</summary>
    internal void Cancel()
    {
        lock (gate)
        {
            if (state == Taken) return;
            state = Canceled;
        }
        mayStart.TrySetResult();
        cancel.Cancel();
    }

    /// <summary>The words, when they are ready and no request carried them yet: what goes with the reply being built.</summary>
    internal bool TryPeek(out string summary, out string? details)
    {
        lock (gate)
        {
            summary = Summary ?? "";
            details = Details;
            return state == Open && IsReady && Summary is not null;
        }
    }

    /// <summary>The request that carried the words was sent: no other request takes them.</summary>
    internal bool TryTake()
    {
        lock (gate)
        {
            if (state != Open || Summary is null) return false;
            state = Taken;
            return true;
        }
    }

    /// <summary>A request was sent without the words (they weren't ready, or no reply follows): they go to the context board for
    /// the next request once they come. True when they are ready already, so the caller posts them now.</summary>
    internal bool MarkLate()
    {
        lock (gate)
        {
            if (state != Open) return false;
            state = Late;
            return IsReady && Summary is not null;
        }
    }

    /// <summary>The job ended with <paramref name="words"/> (null: nothing stands out, or it didn't succeed). True when the note
    /// is late and has words: the caller posts them to the context board.</summary>
    internal bool Finish(SenseJobOutcome? outcome, (string Summary, string? Details)? words, string? problem, TimeSpan took)
    {
        lock (gate)
        {
            if (IsReady) return false;
            Outcome = outcome;
            Summary = words?.Summary;
            Details = words?.Details;
            Problem = problem;
            Took = took;
            Interlocked.Exchange(ref readyAt, clock.GetTimestamp());
            ready.TrySetResult();
            return state == Late && Summary is not null;
        }
    }

    public override string ToString() => nameof(VoiceNote);
}

/// <summary>What the audio model gets for one utterance, how its answer is cleaned and how its words go to Thinking: a note sent
/// with one request only and a short line the conversation keeps after the message (docs/SENSE_MODELS.md).</summary>
internal static class VoiceNotes
{
    /// <summary>What an utterance's job is for, in the log and the status file.</summary>
    internal const string Purpose = "your voice";
    /// <summary>The context board source of words that came late.</summary>
    internal const string BoardSource = "voice";
    /// <summary>How long words that came late stay on the context board for the next request.</summary>
    internal static readonly TimeSpan LateAge = TimeSpan.FromMinutes(3);
    /// <summary>How long a job on a model that shares the conversation's hardware waits for its reply's request, at most.</summary>
    internal static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(30);
    /// <summary>How long such a job may then wait for the reply's voice to be made and run, at most.</summary>
    internal static readonly TimeSpan SharedPatience = TimeSpan.FromSeconds(60);
    /// <summary>The most late notes that go together.</summary>
    internal const int MaximumLate = 3;
    internal const int MaximumSummary = 160, MaximumDetails = 300;
    private const int MaximumContextLine = 200;

    /// <summary>The audio model's job for one utterance: the fixed instructions (Companion › Prompts › Describing your voice), a
    /// short context and the recording; on a <paramref name="shared"/> model it waits as long as the reply makes its voice.</summary>
    internal static SenseJob Job(string instructions, string context, BoundedWaveAudio recording, bool shared) => new()
    {
        Purpose = Purpose, Priority = 10, Instructions = instructions, Text = context, Audio = recording,
        Timeout = TimeSpan.FromSeconds(15), DropWhenStale = !shared, MaxOutputTokens = 120, Reasoning = false
    };

    /// <summary>The short context the audio model gets with the recording: what the user said last and Martlet's last words (never
    /// what this PC played or what Martlet saw), each cut short, so it knows what matters.</summary>
    internal static string Context(IReadOnlyList<TextHistoryMessage> history, string companion)
    {
        var user = Plain(history.LastOrDefault(m => m.Role == TextHistoryRole.User)?.Text);
        var said = Plain(history.LastOrDefault(m => m.Role == TextHistoryRole.Assistant)?.Text);
        // A [pass] said nothing.
        if (said is not null && said.StartsWith('[') && said.EndsWith(']')) said = null;
        if (user is null && said is null) return "The recording is the first thing the user says in this conversation.";
        var lines = new List<string> { "The conversation just before (for context only):" };
        if (user is not null) lines.Add("User: " + Cut(user, MaximumContextLine));
        if (said is not null) lines.Add($"{companion}: {Cut(said, MaximumContextLine)}");
        lines.Add("");
        lines.Add("The recording is what the user said next.");
        return string.Join("\n", lines);
    }

    // A message without the lines that aren't the user's or Martlet's words, on one line; null when nothing is left.
    private static string? Plain(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var own = text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !Marked(line));
        var joined = string.Join(" ", own);
        return joined.Length == 0 ? null : joined;
    }

    private static bool Marked(string line) =>
        line.StartsWith("[PC audio]", StringComparison.Ordinal) || line.StartsWith("[Screen]", StringComparison.Ordinal) ||
        line.StartsWith("[Camera]", StringComparison.Ordinal) || line.StartsWith("(voice", StringComparison.Ordinal) ||
        line.StartsWith("(touch", StringComparison.Ordinal);

    /// <summary>The audio model's answer as its first line (the summary the conversation keeps) and the rest (the details), or
    /// null for "none" or an empty answer.</summary>
    internal static (string Summary, string? Details)? Clean(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var lines = reply.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Line).Where(line => line.Length > 0).ToArray();
        if (lines.Length == 0 || None(lines[0])) return null;
        var details = lines.Length > 1 ? Cut(string.Join(" ", lines.Skip(1).Take(3)), MaximumDetails) : null;
        return (Cut(lines[0], MaximumSummary), details is null || None(details) ? null : details);
    }

    private static bool None(string line) => line.TrimEnd('.', '!').Equals("none", StringComparison.OrdinalIgnoreCase);

    // One line without quotes, list marks, emphasis, labels or control characters.
    private static string Line(string line)
    {
        var plain = new string(line.Where(c => !char.IsControl(c)).ToArray()).Trim();
        plain = plain.TrimStart('-', '*', '\u2022', ' ').Trim('"', '\'', '`', '*', ' ', '\u201C', '\u201D');
        foreach (var label in (string[])["Summary:", "Details:", "Detail:"])
            if (plain.StartsWith(label, StringComparison.OrdinalIgnoreCase)) plain = plain[label.Length..].Trim();
        return plain;
    }

    private static string Cut(string text, int maximum) => text.Length <= maximum ? text : text[..(maximum - 1)].TrimEnd() + "\u2026";

    /// <summary>The note a request carries (Companion › Prompts › How you sounded): how the user sounded saying this message, or,
    /// <paramref name="late"/>, in what they said before; null when there are no words or the owner emptied the prompt.</summary>
    internal static string? Note(PromptSettings? prompts, IReadOnlyList<VoiceNote> notes, bool late)
    {
        var said = notes.Where(note => note.Summary is not null)
            .Select(note => note.Details is null ? note.Summary! : $"{note.Summary!.TrimEnd('.')}. {note.Details}").ToArray();
        if (said.Length == 0) return null;
        return PromptSettings.Fill(prompts, PromptCatalog.HeardVoiceNote,
            ("when", late ? "in what they said before this message" : "saying this message"), ("voice", string.Join("; then ", said)));
    }

    /// <summary>The short line the conversation keeps after the message that carried the note: "(voice: sighs, sounds tired)", or
    /// for words that came late "(voice, earlier: ...)".</summary>
    internal static string? Kept(IReadOnlyList<VoiceNote> notes, bool late)
    {
        var said = notes.Where(note => note.Summary is not null).Select(note => note.Summary!.TrimEnd('.')).ToArray();
        return said.Length == 0 ? null : $"({(late ? "voice, earlier" : "voice")}: {string.Join("; then ", said)})";
    }

    /// <summary>Whether a recording sent to <paramref name="model"/> stays on this PC: Ollama on this PC with a model it doesn't
    /// send to its cloud (<see cref="HearingModelCatalog.StaysOnThisPc"/>). Only then may the audio model hear the user's voice
    /// without their tick.</summary>
    internal static bool StaysOnThisPc(DeepThinkingSettings model) =>
        model.Place == DeepThinkingPlace.Endpoint &&
        HearingModelCatalog.StaysOnThisPc(SetupRouteType.ChatCompletions, model.Origin, model.ModelId);

    /// <summary>Let ... hear my voice (Companion › Listening): your own choice wins; never chosen, it is on only while the recording
    /// stays on this PC.</summary>
    internal static bool MayHear(bool? choice, bool staysOnThisPc) => choice ?? staysOnThisPc;
}
