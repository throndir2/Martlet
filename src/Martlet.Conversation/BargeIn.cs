using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>What Martlet is saying aloud right now, which decides what talking over it does. A reply stops for real words; a
/// song keeps going unless it is asked to stop ("okay okay Martlet, stop singing").</summary>
public enum PlaybackMode { Reply, Song }

/// <summary>Whether what the user said over Martlet stops it, and why, in a few words for the log (never what was said).</summary>
public sealed record BargeInDecision(bool Interrupt, string Reason, UtteranceDecision Words);

/// <summary>The one place that decides whether talking over Martlet stops it. Only real words do (<see cref="UtteranceFilter"/>):
/// a hum, a long "mmmm", laughter or a cough never does, however long it lasts. A stop word ("stop", "wait", "hold on", "shh")
/// or Martlet's name stops a reply at once; a quick backchannel ("yeah", "right", "mm-hmm") never does and waits for after the
/// reply; anything else needs <see cref="WordsToInterrupt"/> words. While Martlet sings (<see cref="PlaybackMode.Song"/>) only
/// a stop request does ("stop singing", or a stop word with Martlet's name). The words come from a quick transcript of what was
/// said so far (<see cref="BargeInGate"/>) or, where speech-to-text isn't on this PC, from the utterance's own transcript.</summary>
public static class BargeInPolicy
{
    /// <summary>How much voice talking over Martlet takes before its words are checked.</summary>
    public static TimeSpan VoiceBeforeCheck(ListeningSensitivity sensitivity) => sensitivity switch
    {
        ListeningSensitivity.Sensitive => TimeSpan.FromMilliseconds(200),
        ListeningSensitivity.Relaxed => TimeSpan.FromMilliseconds(450),
        _ => TimeSpan.FromMilliseconds(300)
    };

    /// <summary>How many words stop a reply when none of them is a stop word or Martlet's name.</summary>
    public static int WordsToInterrupt(ListeningSensitivity sensitivity) => sensitivity switch
    {
        ListeningSensitivity.Sensitive => 1,
        ListeningSensitivity.Relaxed => 3,
        _ => 2
    };

    private static readonly string[] StopCues =
    [
        "stop", "wait", "hold on", "hang on", "hold up", "shut up", "be quiet", "quiet", "enough", "pause", "shh", "shush",
        "one sec", "one second", "just a sec", "just a second", "excuse me", "never mind", "nevermind", "no no", "cancel",
        "not now", "okay okay", "ok ok", "hey", "sorry", "actually", "listen", "stop talking", "that's enough"
    ];

    private static readonly HashSet<string> Backchannels = new(StringComparer.Ordinal)
    {
        "yeah", "yes", "yep", "yup", "right", "okay", "ok", "sure", "uh-huh", "mm-hmm", "mhm", "i", "see", "got", "it", "true",
        "nice", "cool", "wow", "really", "oh", "ha", "haha", "exactly", "totally", "indeed", "alright", "all", "fine", "good",
        "great", "interesting", "makes", "sense", "agreed", "absolutely", "definitely", "mm", "hmm", "uh", "um", "ah", "thank",
        "thanks", "you"
    };

    private static readonly string[] SongWords = ["sing", "singing", "song", "music"];

    /// <summary>Decide on what was said over Martlet: <paramref name="text"/> is a transcript of it (so far), and
    /// <paramref name="context"/> its voice, the engine's evidence and the names that address Martlet.</summary>
    public static BargeInDecision Decide(string? text, UtteranceContext context, ListeningSensitivity sensitivity,
        PlaybackMode mode = PlaybackMode.Reply)
    {
        var words = UtteranceFilter.Check(text, context, sensitivity);
        if (!words.Keep) return new(false, words.Reason, words);
        var said = UtteranceFilter.Words(UtteranceFilter.WithoutSounds(text!, out _));
        var joined = " " + string.Join(" ", said) + " ";
        var cue = StopCues.FirstOrDefault(c => joined.Contains(" " + c + " ", StringComparison.Ordinal));
        var addressed = UtteranceFilter.Addressed(said, context.Names);
        if (mode == PlaybackMode.Song)
            return cue is not null && (addressed || SongWords.Any(w => joined.Contains(" " + w + " ", StringComparison.Ordinal)))
                ? new(true, "asked to stop singing", words)
                : new(false, "Martlet keeps singing unless asked to stop", words);
        if (cue is not null) return new(true, "a stop word", words);
        if (addressed) return new(true, "Martlet's name", words);
        if (said.All(Backchannels.Contains)) return new(false, "a quick backchannel", words);
        // One word said over and over counts once: a laugh's "ha ha ha" often comes out as a word repeated ("One, one, one.").
        var count = Math.Min(words.Words, said.Distinct(StringComparer.Ordinal).Count());
        // A few words the engine was far from sure of are often its guess at laughter or a noise (Parakeet's English models
        // write "Come on." or "Cosmos was" for a laugh): they don't stop a reply on their count alone. A later check of the same
        // voice, or a stop word or Martlet's name, still does.
        if (count <= 3 && Unsure(context.Evidence, sensitivity)) return new(false, "speech-to-text wasn't sure of the words", words);
        return count >= WordsToInterrupt(sensitivity)
            ? new(true, $"{count} words", words)
            : new(false, count < words.Words ? "repeated words" : "too few words", words);
    }

    // Both the mean and the least sure token's probability are low (engines that report both: Parakeet today).
    private static bool Unsure(TranscriptionEvidence? evidence, ListeningSensitivity sensitivity) =>
        evidence is { MeanProbability: { } mean, MinimumProbability: { } least } &&
        mean < UtteranceFilter.For(sensitivity).MinimumProbability + 0.2 && least < UtteranceFilter.For(sensitivity).MinimumProbability * 0.8;
}

/// <summary>When to check the words of someone talking over Martlet (<see cref="BargeInPolicy"/>): once their voice has gone
/// on for <see cref="BargeInPolicy.VoiceBeforeCheck"/>, again as soon as <see cref="FirstRecheck"/> more of it has been heard
/// (the first check often catches only part of a word), then after every <see cref="Recheck"/> more, and as soon as they pause
/// briefly (<see cref="Pause"/>, after at least half that voice), so a short "stop" is checked the moment it ends. A pause
/// longer than <see cref="Gap"/> starts over. Fed one 20 ms voice-activity frame at a time, with whether it was loud and
/// whether what this PC plays explains it (those never count); it decides only when, never what.</summary>
public sealed class BargeInGate(ListeningSensitivity sensitivity)
{
    public const int FrameMilliseconds = 20;
    public static TimeSpan FirstRecheck => TimeSpan.FromMilliseconds(100);
    public static TimeSpan Recheck => TimeSpan.FromMilliseconds(400);
    public static TimeSpan Pause => TimeSpan.FromMilliseconds(160);
    public static TimeSpan Gap => TimeSpan.FromMilliseconds(500);
    /// <summary>At most this many checks in one stretch of voice.</summary>
    public const int MaximumChecks = 6;
    private readonly int gateFrames = Frames(BargeInPolicy.VoiceBeforeCheck(sensitivity));
    private static readonly int FirstRecheckFrames = Frames(FirstRecheck), RecheckFrames = Frames(Recheck),
        PauseFrames = Frames(Pause), GapFrames = Frames(Gap);
    private int voiced, quiet, checkedAt, heard;

    private static int Frames(TimeSpan time) => (int)(time.TotalMilliseconds / FrameMilliseconds);

    /// <summary>Frames processed so far.</summary>
    public int Processed { get; private set; }
    /// <summary>The frame where the current stretch of voice began, or -1.</summary>
    public int StretchStartFrame { get; private set; } = -1;
    /// <summary>Of the current stretch, how much was a voice.</summary>
    public TimeSpan Voice => TimeSpan.FromMilliseconds(voiced * FrameMilliseconds);
    /// <summary>How long the current stretch has gone on, leaving out frames what this PC plays explains
    /// (<see cref="UtteranceContext.Speech"/>).</summary>
    public TimeSpan Speech => TimeSpan.FromMilliseconds(heard * FrameMilliseconds);
    /// <summary>Checks asked for in the current stretch.</summary>
    public int Checks { get; private set; }

    /// <summary>One 20 ms frame. Returns true when the words said from <see cref="StretchStartFrame"/> to
    /// <see cref="Processed"/> should be checked now (never while <paramref name="busy"/>, a check still running).</summary>
    public bool Process(bool loud, bool speakers, bool busy)
    {
        if (loud && !speakers)
        {
            if (voiced == 0) StretchStartFrame = Processed;
            voiced++;
            quiet = 0;
        }
        else if (++quiet > GapFrames && voiced > 0)
        {
            voiced = checkedAt = Checks = heard = 0;
            StretchStartFrame = -1;
        }
        if (StretchStartFrame >= 0 && !speakers) heard++;
        Processed++;
        if (busy || StretchStartFrame < 0 || Checks >= MaximumChecks) return false;
        // Grown: enough voice for the first check, or more of it since the last. Paused: a short word just ended.
        var grown = voiced >= gateFrames && (checkedAt == 0 || voiced - checkedAt >= (Checks == 1 ? FirstRecheckFrames : RecheckFrames));
        var paused = quiet >= PauseFrames && voiced > checkedAt && voiced >= gateFrames / 2;
        if (!grown && !paused) return false;
        checkedAt = voiced;
        Checks++;
        return true;
    }
}

/// <summary>Martlet staying quiet: a reply that is only the no-reply marker (<see cref="Marker"/>, as "[pass]") says nothing,
/// shows nothing but a muted note and is kept in context as a pass. Always listening, what the PC plays and screen glances ask
/// the Thinking model to answer it when what it heard wasn't meant for it. It reads only the reply's text, so it works the same
/// for a transcript and for a Thinking model that hears the audio itself.</summary>
public static class StayQuiet
{
    /// <summary>The word the model answers (in brackets) to stay quiet; never spoken.</summary>
    public const string Marker = "pass";

    /// <summary>The finished reply is the no-reply marker (or empty).</summary>
    public static bool IsQuiet(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 || trimmed.StartsWith("[" + Marker, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed.Trim('[', ']', '(', ')', '<', '>', '*', '"', '\'', '.', '!', ' '), Marker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A reply still streaming that may turn out to be the marker; it isn't shown until it clearly isn't.</summary>
    public static bool MaybeQuiet(string text)
    {
        var trimmed = text.Trim();
        return IsQuiet(trimmed) || ("[" + Marker + "]").StartsWith(trimmed, StringComparison.OrdinalIgnoreCase) ||
            Marker.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase);
    }
}
