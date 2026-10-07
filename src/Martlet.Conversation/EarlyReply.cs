using System.Globalization;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>Companion › Listening › Start replies early. On (the default), always listening starts the reply's Thinking request
/// at the end-of-turn check point, as soon as the quick transcript of what was said so far has real words, without waiting for
/// the verdict; with <see cref="Voice"/> the first spoken piece is synthesized too. Nothing is shown, said or done until the
/// turn really ends with the same words: then the reply goes on at once (promoted), otherwise it is let go. A Thinking model in
/// a cloud (a paid provider) charges the input of a request that is let go, so <see cref="Cloud"/> is a separate choice, off
/// by default; a paid cloud voice needs it too.</summary>
public sealed record EarlyReplyOptions
{
    /// <summary>Start replies early (on by default) with a Thinking model on this PC, a paired Martlet host or another of the
    /// user's computers at home.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>Also for cloud models (off by default): a request that is let go may still cost its input tokens there.</summary>
    public bool Cloud { get; init; }
    /// <summary>Prepare the voice early too (on by default): the first spoken piece is made while the reply waits.</summary>
    public bool Voice { get; init; } = true;
    /// <summary>The most replies one turn may start early: each pause after the user went on talking may start one more.</summary>
    public int MaximumStarts { get; init; } = 3;

    /// <summary>A reply started early that the turn never took (the words were let go, the window closed) stops after this.</summary>
    public static TimeSpan Expiry { get; } = TimeSpan.FromSeconds(12);

    public static EarlyReplyOptions Off { get; } = new() { Enabled = false };

    /// <summary>Whether a reply may start early with this Thinking model: <paramref name="ownComputer"/> is a model on this PC,
    /// a paired Martlet host or another computer on the home network; any other needs <see cref="Cloud"/>.</summary>
    public bool ForThinking(bool ownComputer) => Enabled && (ownComputer || Cloud);

    /// <summary>Whether its first spoken piece may be made early as well: a voice on this PC or a paired host is free, a paid
    /// cloud voice (<paramref name="paidVoice"/>) needs <see cref="Cloud"/>.</summary>
    public bool ForVoice(bool ownComputer, bool paidVoice) => ForThinking(ownComputer) && Voice && (!paidVoice || Cloud);
}

/// <summary>What starting replies early decides in one turn of always listening (one utterance), frame by frame with the
/// end-of-turn check: <see cref="TryStart"/> when the quick transcript of the pause under way arrives with real words
/// (<see cref="Worth"/>), <see cref="VoiceResumed"/> when the user's own voice comes back (never the speakers' sound), and
/// <see cref="Ended"/> when the turn ends. A reply started early is promoted only when the turn ends in the same pause it
/// started in, with the same audio; at most <see cref="EarlyReplyOptions.MaximumStarts"/> start in one turn.</summary>
public sealed class EarlyReplyGate(EarlyReplyOptions options)
{
    public EarlyReplyOptions Options { get; } = options;
    /// <summary>How many replies this turn started early.</summary>
    public int Starts { get; private set; }
    /// <summary>How many of them were let go because the user went on talking (or the words changed).</summary>
    public int Cancelled { get; private set; }
    /// <summary>The pause the reply started early now belongs to, or null when none runs.</summary>
    public int? Running { get; private set; }
    /// <summary>No more replies may start early in this turn.</summary>
    public bool Spent => Starts >= Options.MaximumStarts;

    /// <summary>The quick transcript of pause <paramref name="pause"/> arrived: start a reply early when it is worth it
    /// (<paramref name="worth"/>), the turn is still in that pause (<paramref name="currentPause"/>, <paramref name="silent"/>),
    /// none runs and this turn has starts left. Returns true when the caller should start one now.</summary>
    public bool TryStart(int pause, int currentPause, bool silent, bool worth)
    {
        if (!Options.Enabled || !worth || !silent || pause != currentPause || Running is not null || Spent) return false;
        Starts++;
        Running = pause;
        return true;
    }

    /// <summary>The start the caller asked for couldn't happen after all (the conversation was busy or refused it): it doesn't
    /// count, and nothing runs.</summary>
    public void NotStarted()
    {
        if (Running is null) return;
        Running = null;
        Starts--;
    }

    /// <summary>The user's own voice came back during the pause: a reply started early is let go. Returns true when one ran.</summary>
    public bool VoiceResumed() => Cancel();

    /// <summary>A reply started early is let go (the words changed, it was refused, it expired). Returns true when one ran.</summary>
    public bool Cancel()
    {
        if (Running is null) return false;
        Running = null;
        Cancelled++;
        return true;
    }

    /// <summary>The turn ended in pause <paramref name="pause"/>: true when the reply started early belongs to it, so it may
    /// be promoted once the words are confirmed; one from an earlier pause is let go.</summary>
    public bool Ended(int pause)
    {
        if (Running is null) return false;
        if (Running == pause) return true;
        Cancel();
        return false;
    }

    /// <summary>Whether a quick transcript is worth starting a reply on: words the word check counts (<see cref="UtteranceFilter"/>)
    /// and not only a quick backchannel ("yeah", "okay", "mm-hmm"), which often leads into more.</summary>
    public static bool Worth(string? text, UtteranceContext context, ListeningSensitivity sensitivity)
    {
        if (string.IsNullOrWhiteSpace(text) || !UtteranceFilter.Check(text, context, sensitivity).Keep) return false;
        var said = UtteranceFilter.Words(UtteranceFilter.WithoutSounds(text, out _));
        return said.Count > 0 && !said.All(BargeInPolicy.IsBackchannel);
    }
}

/// <summary>One reply started early, for the desktop log, Companion › Listening's status and MCP (no words, no audio): what
/// became of it (<see cref="Promoted"/>, <see cref="Cancelled"/>: the user went on talking, <see cref="Changed"/>: the words
/// or what goes with them changed, <see cref="Refused"/>: Martlet doesn't answer it, <see cref="Expired"/>: nothing took it),
/// how far into the user's pause it started, how long it waited, which start of the turn it was, whether its first piece was
/// already synthesized when it was promoted, and the reason in a few words.</summary>
public sealed record EarlyReplyRecord(DateTimeOffset At, string Outcome, TimeSpan StartedAfter, TimeSpan Waited, int Start,
    string? Reason = null, bool? FirstPieceReady = null)
{
    public const string Promoted = "promoted", Cancelled = "cancelled", Changed = "changed", Refused = "refused", Expired = "expired";

    /// <summary>The desktop log's line, for example <c>Early reply: promoted after 1340 ms; it started 262 ms into your pause
    /// (start 1), and its first piece was ready.</c></summary>
    public string Describe()
    {
        var into = $"it started {Ms(StartedAfter)} ms into your pause (start {Start})";
        var ready = FirstPieceReady switch { true => ", and its first piece was ready", false => ", before its first piece was ready", _ => "" };
        return "Early reply: " + Outcome switch
        {
            Promoted => $"promoted after {Ms(Waited)} ms; {into}{ready}.",
            Cancelled => $"let go after {Ms(Waited)} ms ({Reason ?? "you went on talking"}); {into}.",
            Changed => $"let go after {Ms(Waited)} ms ({Reason ?? "what you said changed"}); {into}. A new reply starts now.",
            Refused => $"let go after {Ms(Waited)} ms ({Reason ?? "Martlet doesn't answer it"}); {into}.",
            _ => $"let go after {Ms(Waited)} ms ({Reason ?? "nothing took it"}); {into}."
        };
    }

    private static string Ms(TimeSpan value) => Math.Max(0, value.TotalMilliseconds).ToString("0", CultureInfo.InvariantCulture);
}

/// <summary>The replies a turn started early, for the reply latency line: whether the reply itself is one of them, promoted
/// (it started at the reply's start), how many started in the turn and how many were let go.</summary>
public sealed record EarlyStarts(bool Promoted, int Starts, int Cancelled);

/// <summary>A reply's request in the few things that can differ between a reply started early and the one the conversation
/// asks for once the turn ends: the words (or, straight to Thinking, the recording alone), whether it is spoken, the recording
/// that goes with it, how chatty it is told to be, whether Martlet is in a Discord call, and who is talking (the voices block,
/// its preamble and the history prefix, as one text). Everything else is built the same way from the same conversation.
/// <see cref="Extra"/> names what goes with the ask that a reply started early never carries (a picture, what this PC played,
/// typed text), or null.</summary>
public sealed record EarlyAsk(string Text, bool Voice, bool Straight, BoundedWaveAudio? Recording,
    Martlet.Core.Settings.ChattinessChoice? Chattiness, bool DiscordCall, string? Voices, string? Extra = null)
{
    /// <summary>The same ask with who is talking as recognition said it.</summary>
    public EarlyAsk WithVoices(string? voices) => this with { Voices = voices };

    /// <summary>Why <paramref name="asked"/> isn't the request this reply started early with, in a few words; null when it is the
    /// same request, so the reply goes on as it is (no second request).</summary>
    public string? Differs(EarlyAsk asked)
    {
        ArgumentNullException.ThrowIfNull(asked);
        if (asked.Extra is { } extra) return extra;
        if (asked.Voice != Voice) return "the voice was turned on or off";
        if (asked.Straight != Straight) return "how your voice goes to Thinking changed";
        if (!Straight && !string.Equals(asked.Text, Text, StringComparison.Ordinal)) return "what you said changed";
        if (asked.Recording is null != Recording is null || asked.Recording is not null && !asked.Recording.SameAudio(Recording))
            return "your recording changed";
        if (asked.Chattiness != Chattiness) return "how chatty Martlet is changed";
        if (asked.DiscordCall != DiscordCall) return "the Discord call started or ended";
        if (!string.Equals(asked.Voices, Voices, StringComparison.Ordinal)) return "who spoke changed";
        return null;
    }
}
