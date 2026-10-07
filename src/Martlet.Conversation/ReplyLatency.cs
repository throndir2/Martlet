using System.Globalization;
using System.Text;

namespace Martlet.Conversation;

/// <summary>When each step before a reply happened, from the moment that counts for the user (they stopped talking, let go of
/// the talk button or sent their message) to the reply's start. Each step names the wait that ended when it was marked, so the
/// steps add up to the whole wait. The desktop's always listening fills it for each utterance and the reply it is answered with
/// continues it; the desktop log's reply latency line (<see cref="ReplyLatency"/>) adds the reply's own steps.
/// Diagnostics only: timestamps of the controller's clock, no content.</summary>
public sealed class ReplyTimeline
{
    private readonly object gate = new();
    private readonly List<(string Step, long At)> steps = [];
    private string origin;
    private long originAt;

    public ReplyTimeline(TimeProvider clock, string origin, long? at = null)
    {
        Clock = clock;
        this.origin = origin;
        originAt = at ?? clock.GetTimestamp();
    }

    public TimeProvider Clock { get; }
    /// <summary>What the wait is counted from, as the log says it: "you stopped talking", "you sent your message"...</summary>
    public string Origin { get { lock (gate) return origin; } }
    public long OriginAt { get { lock (gate) return originAt; } }
    public IReadOnlyList<(string Step, long At)> Steps { get { lock (gate) return steps.ToArray(); } }

    public void Mark(string step, long? at = null)
    {
        lock (gate) steps.Add((step, at ?? Clock.GetTimestamp()));
    }

    /// <summary>Counts from a later moment instead (push-to-talk: when the talk button was let go), forgetting earlier steps.</summary>
    public void Restart(string from, long? at = null)
    {
        lock (gate)
        {
            origin = from;
            originAt = at ?? Clock.GetTimestamp();
            steps.Clear();
        }
    }

    /// <summary>A copy for the reply that answers what was heard, so a restarted reply starts again from the same moment.</summary>
    public ReplyTimeline Copy()
    {
        lock (gate)
        {
            var copy = new ReplyTimeline(Clock, origin, originAt);
            copy.steps.AddRange(steps);
            return copy;
        }
    }

    public const string YouStopped = "you stopped talking";
    public const string YouLetGo = "you let go of the talk button";
    public const string YouPressed = "you pressed the talk button";
    public const string YouSent = "you sent your message";
    public const string Asked = "Martlet was asked";
}

/// <summary>The desktop log's per-reply latency line: how long from when the user stopped talking (or sent their message) to
/// the first audio, and each step it took, so a slow stage stands out. One line per reply, in this form (steps that didn't
/// happen are left out; their numbers add up to the total):
/// <c>Reply latency: first audio 6620 ms after you stopped talking (end of speech 800, recording 12, ..., speakers 31). First
/// words after 3300 ms and first audio after 5585 ms from the reply's start, 2 spoken pieces. First piece: 1.20 s of speech made
/// in 1069 ms. The voice paused 2 times for 3120 ms in all, waiting for its next audio. Models: Thinking x-ai/grok-4.3, voice
/// chatterbox-turbo, speech-to-text parakeet-tdt-0.6b-v3-int8.</c>
/// The pauses are said only when the speakers ran dry mid-reply because the voice was made slower than real time.
/// MCP's latency_report reads these lines; the models are the desktop's list of model IDs.</summary>
public static class ReplyLatency
{
    public const string Prefix = "Reply latency: ";

    // Always listening's first steps when the end-of-turn judge decided: the pause before it was asked, then its answer (the
    // plain pause rule's step is "end of speech").
    public const string EndOfTurnWait = "end-of-turn wait";
    public const string EndOfTurnJudge = "end-of-turn judge";
    public const string EndOfSpeech = "end of speech";

    // The reply's own steps, in order, each named for the wait that ended there.
    public const string ThinkingAuthorization = "Thinking authorization";
    public const string ThinkingConnection = "Thinking connection";
    public const string ThinkingBeforeReasoning = "Thinking before reasoning";
    public const string HiddenReasoning = "hidden reasoning";
    public const string ThinkingFirstWords = "Thinking first words";
    public const string FirstSentence = "first sentence";
    public const string VoiceAuthorization = "voice authorization";
    public const string VoiceSynthesis = "voice synthesis";
    public const string PlaybackStart = "playback start";
    public const string Speakers = "speakers";
    // Shorter waits for the voice's next audio (the moment between a piece's last audio and its end) aren't heard as a pause.
    private static readonly TimeSpan NoticeablePause = TimeSpan.FromMilliseconds(100);

    /// <summary>The line for a finished reply, or null when nothing of it arrived (no words, no audio).</summary>
    /// <param name="timeline">What happened before the reply started; its last step is the reply's start
    /// (<paramref name="replyStartedAt"/>).</param>
    public static string? Describe(ReplyTimeline? timeline, long replyStartedAt, TimeProvider clock, ConversationSnapshot reply,
        string? models, bool interrupted = false, bool passed = false, bool restarted = false)
    {
        if (reply.FirstTextAfter is null && reply.FirstAudioAfter is null) return null;
        var steps = new List<(string Step, long At)>();
        long origin = timeline?.OriginAt ?? replyStartedAt;
        if (timeline is not null) steps.AddRange(timeline.Steps.Where(step => step.At <= replyStartedAt));
        var timings = reply.Timings ?? new ConversationTimings();
        long At(TimeSpan after) => replyStartedAt + (long)(after.TotalSeconds * clock.TimestampFrequency);
        void Add(string step, TimeSpan? after)
        {
            if (after is { } value) steps.Add((step, At(value)));
        }
        Add(ThinkingAuthorization, timings.TextRequestAfter);
        Add(ThinkingConnection, timings.TextResponseAfter);
        if (timings.FirstReasoningAfter is { } reasoning && (reply.FirstTextAfter is null || reasoning <= reply.FirstTextAfter))
        {
            Add(ThinkingBeforeReasoning, reasoning);
            Add(HiddenReasoning, reply.FirstTextAfter);
        }
        else Add(ThinkingFirstWords, reply.FirstTextAfter);
        Add(FirstSentence, timings.FirstSegmentAfter);
        Add(VoiceAuthorization, timings.SpeechRequestAfter);
        Add(VoiceSynthesis, timings.FirstSpeechAudioAfter);
        Add(PlaybackStart, timings.PlaybackStartedAfter);
        Add(Speakers, reply.FirstAudioAfter);

        var text = new StringBuilder(Prefix);
        var end = reply.FirstAudioAfter is { } audio ? At(audio) : At(reply.FirstTextAfter!.Value);
        var what = reply.FirstAudioAfter is null ? "first words" : "first audio";
        var from = timeline?.Origin ?? ReplyTimeline.Asked;
        text.Append(CultureInfo.InvariantCulture, $"{what} {Milliseconds(end - origin, clock)} ms after {from} (");
        var previous = origin;
        var first = true;
        foreach (var (step, at) in steps.Where(step => step.At <= end).OrderBy(step => step.At))
        {
            if (!first) text.Append(", ");
            first = false;
            text.Append(CultureInfo.InvariantCulture, $"{step} {Milliseconds(Math.Max(0, at - previous), clock)}");
            previous = Math.Max(previous, at);
        }
        text.Append("). ");
        text.Append(CultureInfo.InvariantCulture, $"First words after {Ms(reply.FirstTextAfter)} ms");
        if (reply.FirstAudioAfter is { } spoken)
            text.Append(CultureInfo.InvariantCulture, $" and first audio after {spoken.TotalMilliseconds:0} ms");
        text.Append(CultureInfo.InvariantCulture,
            $" from the reply's start, {reply.CommittedSegments} spoken piece{(reply.CommittedSegments == 1 ? "" : "s")}");
        if (passed) text.Append("; Martlet stayed quiet");
        else if (restarted) text.Append("; replaced because you kept talking");
        else if (reply.FirstAudioAfter is null) text.Append(reply.SpeechFailed ? "; the voice failed" : "; not spoken");
        if (interrupted) text.Append(", stopped when you talked over it");
        text.Append('.');
        if (timings.FirstPieceSpeech is { } speech && timings.FirstPieceSynthesizedAfter is { } made && timings.SpeechRequestAfter is { } asked)
            text.Append(CultureInfo.InvariantCulture,
                $" First piece: {speech.TotalSeconds:0.00} s of speech made in {Math.Max(0, (made - asked).TotalMilliseconds):0} ms.");
        if (timings.VoiceWaits > 0 && timings.VoiceWaited >= NoticeablePause)
            text.Append(CultureInfo.InvariantCulture,
                $" The voice paused {timings.VoiceWaits} time{(timings.VoiceWaits == 1 ? "" : "s")} for " +
                $"{timings.VoiceWaited.TotalMilliseconds:0} ms in all, waiting for its next audio.");
        if (reply.FellBack) text.Append(" Answered by the Thinking fallback.");
        if (!string.IsNullOrWhiteSpace(models)) text.Append(" Models: ").Append(models.Trim()).Append('.');
        return text.ToString();
    }

    private static string Ms(TimeSpan? value) => value is { } span ? span.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) : "-";

    private static string Milliseconds(long ticks, TimeProvider clock) =>
        (ticks * 1000.0 / clock.TimestampFrequency).ToString("0", CultureInfo.InvariantCulture);
}
