using Martlet.Providers;

// Also built into Martlet's MCP server (straight_voice_check), in its own namespace.
#if MARTLET_MCP
namespace Martlet.Mcp.Shared;
#else
namespace Martlet.Desktop;
#endif

/// <summary>The words of an utterance that went straight to a Thinking model that hears, as the recording alone (Companion ›
/// Listening › When Thinking can hear you): speech-to-text runs beside the reply, off its path, and the transcript arrives here
/// for the talk window, the conversation, the record of conversations and memory. <see cref="Text"/> stays null when nothing
/// usable came back (<see cref="Problem"/> says why); <see cref="Ignored"/> is set when the word check wouldn't have counted
/// it as words, which only labels the turn (Thinking already heard it and decided).</summary>
internal sealed class SpokenWords(TimeProvider clock)
{
    /// <summary>The text of a message that goes straight to a Thinking model that hears as the user's recording alone (a request
    /// needs some text): it only marks the recording. Later requests carry the transcript in its place.</summary>
    internal const string StandIn = "(spoken: listen to the recording)";

    /// <summary>What stands in, in the conversation and its record, for a message that went straight to Thinking when speech-to-text
    /// couldn't transcribe it.</summary>
    internal const string NotTranscribed = "(spoken; speech-to-text couldn't transcribe it)";

    private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource go = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long replyStartedAt, readyAt;

    /// <summary>Speech-to-text may start now: the reply carrying the recording is under way (its first audio, or its first words
    /// without a voice) or over, or something needs the words now. Where speech-to-text and Thinking both run on this PC,
    /// transcribing waits for this (or a few seconds at most), so it never competes with the reply for the processor.</summary>
    internal Task MayTranscribe => go.Task;
    internal void Release() => go.TrySetResult();

    internal string? Text { get; private set; }
    internal double? Confidence { get; private set; }
    internal UtteranceDecision? Ignored { get; private set; }
    /// <summary>Why there are no words: the outcome code of speech-to-text (stt.*, conversation.*).</summary>
    internal string? Problem { get; private set; }
    /// <summary>How long speech-to-text took.</summary>
    internal TimeSpan? Took { get; private set; }
    internal Task Ready => done.Task;
    internal bool IsReady => done.Task.IsCompleted;

    /// <summary>The words as the conversation keeps them: the transcript, marked when the word check wouldn't count it as words;
    /// null when there are none.</summary>
    internal string? Said => Text is null ? null : Ignored is { } ignored ? $"({ignored.Reason}) {Text}" : Text;

    internal void Heard(string text, double? confidence, UtteranceDecision? ignored, TimeSpan took)
    {
        if (IsReady) return;
        Text = text.Trim();
        Confidence = confidence;
        Ignored = ignored;
        Took = took;
        Finish();
    }

    internal void Failed(string problem)
    {
        if (IsReady) return;
        Problem = problem;
        Finish();
    }

    private void Finish()
    {
        Interlocked.CompareExchange(ref readyAt, clock.GetTimestamp(), 0);
        done.TrySetResult();
    }

    /// <summary>The reply that carried the recording started now (controller clock); the first one counts.</summary>
    internal void ReplyStarted(long at) => Interlocked.CompareExchange(ref replyStartedAt, at, 0);

    private int noted;
    /// <summary>True the first time only: the desktop log notes the words once, even when a reply restarts with them.</summary>
    internal bool TryNote() => Interlocked.Exchange(ref noted, 1) == 0;

    /// <summary>How long after the reply started the words were ready (negative: before it); null until both happened.</summary>
    internal TimeSpan? ReadyAfterReply
    {
        get
        {
            var started = Interlocked.Read(ref replyStartedAt);
            var ready = Interlocked.Read(ref readyAt);
            return started == 0 || ready == 0 ? null : TimeSpan.FromSeconds((ready - started) / (double)clock.TimestampFrequency);
        }
    }

    /// <summary>The words of utterances answered together, in order, as the conversation keeps them; null when none has any.</summary>
    internal static string? Join(IEnumerable<SpokenWords> words) =>
        words.Select(w => w.Said).OfType<string>().Where(s => s.Length > 0).ToArray() is { Length: > 0 } said ? string.Join(" ", said) : null;

    /// <summary>The plain transcript of utterances answered together (no labels), for a request without the recording; null when
    /// none has words.</summary>
    internal static async Task<string?> TranscriptAsync(IReadOnlyList<SpokenWords> words, CancellationToken token)
    {
        foreach (var spoken in words) spoken.Release();
        await Task.WhenAll(words.Select(w => w.Ready)).WaitAsync(token).ConfigureAwait(false);
        return words.Select(w => w.Text).OfType<string>().Where(s => s.Length > 0).ToArray() is { Length: > 0 } text
            ? string.Join(" ", text) : null;
    }

    /// <summary>A message that went straight to Thinking as the conversation keeps it once its words are ready: what the user
    /// said (after <paramref name="prefix"/>, who said it) and the message as later requests send it, the words in place of the
    /// recording with the notes it went with (<paramref name="sent"/>, the request's input), so the requests after it start the
    /// same. <c>Words</c> is what was said, <see cref="NotTranscribed"/> when there are none.</summary>
    internal static (string User, BoundedTextInput? Sent, string Words) Kept(IReadOnlyList<SpokenWords> words, string prefix,
        BoundedTextInput? sent)
    {
        var said = Join(words) ?? NotTranscribed;
        BoundedTextInput? withWords = null;
        try { withWords = sent?.WithTranscript(said); }
        catch (Martlet.Core.Contracts.ContractException) { }
        return (prefix + said, withWords, said);
    }

    public override string ToString() => nameof(SpokenWords);
}
