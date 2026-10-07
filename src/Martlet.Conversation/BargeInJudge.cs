using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>What talking over a reply with real words does (Companion › Listening › When you talk over Martlet).
/// PauseAndDecide (the default): a clear cue (a stop word, Martlet's name) stops it at once; other words pause it at once and a
/// judge decides whether to stop or play on from where it paused (<see cref="BargeInJudging"/>). StopAtOnce: real words stop
/// it at once, as before.</summary>
public enum BargeInBehavior { PauseAndDecide, StopAtOnce }

/// <summary>What the judge made of words said over Martlet: Interrupt (they are for Martlet: stop and answer them) or
/// NotForMe (a backchannel, side talk to someone else, sound from a TV or this PC, laughter, agreement: play on).</summary>
public enum BargeInVerdict { Interrupt, NotForMe }

/// <summary>How a ruling was reached: a clear cue (never judged), the judge in time, the judge too slow or failed (the local
/// rules decided), the user kept talking past <see cref="BargeInJudging.KeepTalkingLimit"/>, or the pause reached
/// <see cref="BargeInJudging.MaximumPause"/>.</summary>
public enum BargeInSource { Cue, Judge, Timeout, KeptTalking, Limit }

/// <summary>What the judge reads: what the user said so far, the sentence Martlet is saying, the reply's recent text, what the
/// filter knows of the voice (<see cref="UtteranceContext"/>), the Word check and, when known, how sure speech-to-text was
/// (0-1).</summary>
public sealed record BargeInJudgeInput(string Heard, string? Sentence, string? RecentReply, UtteranceContext Context,
    ListeningSensitivity Sensitivity, double? Confidence = null);

/// <summary>A judge's verdict and why, in a few words for the log (never what was said).</summary>
public sealed record BargeInJudgment(BargeInVerdict Verdict, string Reason);

/// <summary>Decides whether words said over Martlet are for it. <see cref="RulesBargeInJudge"/> is the fast local judge; a
/// model judge (the Thinking pool) answers within <see cref="BargeInJudging.Deadline"/> or the rules decide.</summary>
public interface IBargeInJudge
{
    /// <summary>A short name for the log: "rules", or the model judge's.</summary>
    string Name { get; }
    Task<BargeInJudgment> JudgeAsync(BargeInJudgeInput input, CancellationToken cancellationToken);
}

/// <summary>A judge's ruling with how it was reached (<see cref="BargeInSource"/>), which judge answered and how long that
/// took.</summary>
public sealed record BargeInRuling(BargeInVerdict Verdict, string Reason, BargeInSource Source, string Judge, TimeSpan JudgeTime)
{
    public bool Interrupt => Verdict == BargeInVerdict.Interrupt;
}

/// <summary>The fast local judge: <see cref="BargeInPolicy"/> first (only real words; a stop word or Martlet's name stops at
/// once; a backchannel never), then words that are Martlet's own sentence heard back (a TV, the speakers, a call) and words that
/// only agree or laugh along are not for Martlet. Everything else is. Instant and private: nothing leaves this PC.</summary>
public sealed class RulesBargeInJudge : IBargeInJudge
{
    public static RulesBargeInJudge Instance { get; } = new();
    public string Name => "rules";

    public Task<BargeInJudgment> JudgeAsync(BargeInJudgeInput input, CancellationToken cancellationToken) =>
        Task.FromResult(Judge(input));

    private static readonly HashSet<string> Agreement = new(StringComparer.Ordinal)
    {
        "that's", "thats", "so", "agree", "know", "of", "course", "for", "sure", "same", "me", "too", "lol", "hehe", "heh", "aha",
        "huh", "no", "way", "my", "god", "gosh", "love", "that", "is", "it's", "its", "funny", "hilarious", "awesome", "perfect",
        "amazing", "much", "very", "wow", "yes", "yeah", "right", "true", "oh", "ha", "haha", "hahaha", "nice", "cool", "good",
        "great", "exactly", "totally", "absolutely", "definitely", "indeed", "okay", "ok", "sure", "mhm", "mm-hmm", "uh-huh"
    };

    public static BargeInJudgment Judge(BargeInJudgeInput input)
    {
        var policy = BargeInPolicy.Decide(input.Heard, input.Context, input.Sensitivity);
        if (!policy.Interrupt) return new(BargeInVerdict.NotForMe, policy.Reason);
        if (policy.Cue) return new(BargeInVerdict.Interrupt, policy.Reason);
        var said = UtteranceFilter.Words(UtteranceFilter.WithoutSounds(input.Heard, out _));
        if (Echo(said, input.Sentence + " " + input.RecentReply)) return new(BargeInVerdict.NotForMe, "Martlet's own words heard back");
        if (said.All(word => Agreement.Contains(word) || BargeInPolicy.IsBackchannel(word)))
            return new(BargeInVerdict.NotForMe, "agreeing or laughing along");
        return new(BargeInVerdict.Interrupt, policy.Reason);
    }

    // Martlet's own words played back (a TV, the speakers through a call, an echo the canceller missed): three words in a row
    // from what it says, or at least three different words that all are in it.
    private static bool Echo(IReadOnlyList<string> said, string reply)
    {
        if (said.Count < 3 || string.IsNullOrWhiteSpace(reply)) return false;
        var spoken = UtteranceFilter.Words(reply);
        var joined = " " + string.Join(" ", spoken) + " ";
        for (var i = 0; i + 2 < said.Count; i++)
            if (joined.Contains($" {said[i]} {said[i + 1]} {said[i + 2]} ", StringComparison.Ordinal)) return true;
        var known = spoken.ToHashSet(StringComparer.Ordinal);
        var distinct = said.Distinct(StringComparer.Ordinal).ToArray();
        return distinct.Length >= 3 && distinct.All(known.Contains);
    }
}

/// <summary>A model judge: asks a Thinking model (the Thinking pool's barge-in judge job) whether the words said over Martlet
/// were for it. <c>ask</c> sends <see cref="Instructions"/> and <see cref="Prompt"/> and returns the model's answer (null when
/// none). An answer that names no verdict throws, so <see cref="BargeInJudging.RuleAsync"/> lets the local rules decide, as it
/// does when the model is slower than <see cref="BargeInJudging.Deadline"/>.</summary>
public sealed class ModelBargeInJudge(Func<string, string, CancellationToken, Task<string?>> ask, string name = "model") : IBargeInJudge
{
    /// <summary>Few words out: the verdict is the first word.</summary>
    public const int MaxOutputTokens = 16;

    public const string Instructions =
        "You decide whether words a person said while a voice assistant was talking were meant for the assistant. " +
        "Answer INTERRUPT when they are for the assistant: a question, a request, a correction, a new topic, or wanting it to stop. " +
        "Answer NOTFORME when they are not: a quick backchannel such as \"yeah\" or \"mm-hmm\", agreeing, laughing along, " +
        "talking to someone else in the room, a TV, video or other audio, or the assistant's own words heard back. " +
        "Answer with the one word INTERRUPT or NOTFORME first, then optionally a colon and at most five words why.";

    public string Name => name;

    /// <summary>The Thinking pool's judge: each ruling is a <see cref="ThinkingJobKind.BargeInJudge"/> job (the pool's highest
    /// priority, a fast kind) on <paramref name="run"/> (<see cref="ThinkingJobBoard.RunAsync"/>), with
    /// <see cref="MaxOutputTokens"/>, no reasoning steps, and dropped when no member frees up within
    /// <see cref="BargeInJudging.Deadline"/>. No member (or none free in time) lets the local rules decide at once; a failed or
    /// timed-out job lets them decide too.</summary>
    public static ModelBargeInJudge ForPool(Func<ThinkingJob, CancellationToken, Task<ThinkingJobResult>> run, string name = "Thinking pool")
    {
        ArgumentNullException.ThrowIfNull(run);
        return new(async (instructions, text, token) =>
        {
            var result = await run(new ThinkingJob
            {
                Kind = ThinkingJobKind.BargeInJudge, Instructions = instructions, Text = text, Timeout = BargeInJudging.Deadline,
                DropWhenStale = true, MaxOutputTokens = MaxOutputTokens, Reasoning = false
            }, token).ConfigureAwait(false);
            return result.Outcome switch
            {
                ThinkingJobOutcome.Succeeded => result.Text,
                ThinkingJobOutcome.NoMember or ThinkingJobOutcome.Stale => null,
                _ => throw new InvalidOperationException($"The Thinking pool's barge-in judge ended {result.Outcome}.")
            };
        }, name);
    }

    /// <summary>What the model reads: the assistant's name, the sentence it is saying, the end of its reply so far, the words
    /// heard over it and how sure speech-to-text was.</summary>
    public static string Prompt(BargeInJudgeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var text = new System.Text.StringBuilder();
        var names = input.Context.Names.Prepend("Martlet").Distinct(StringComparer.OrdinalIgnoreCase);
        text.Append("The assistant's name: ").AppendJoin(" or ", names).Append('\n');
        if (!string.IsNullOrWhiteSpace(input.Sentence)) text.Append("The assistant is saying: \"").Append(input.Sentence.Trim()).Append("\"\n");
        if (!string.IsNullOrWhiteSpace(input.RecentReply))
            text.Append("The end of its reply so far: \"").Append(input.RecentReply.Trim()).Append("\"\n");
        text.Append("Heard over it: \"").Append(input.Heard.Trim()).Append('"');
        if (input.Confidence is { } confidence)
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"\nSpeech-to-text confidence: {confidence:0.00}");
        return text.ToString();
    }

    /// <summary>The verdict a model's answer names first (INTERRUPT, NOTFORME, "not for me"...), with its short reason; null
    /// when it names none.</summary>
    public static BargeInJudgment? Parse(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return null;
        var text = answer.Trim().TrimStart('*', '"', '\'', '`', '[', '(', ' ');
        var squeezed = new string(text.TakeWhile(c => c is not (':' or '\n' or '.' or ',' or '-' or '—')).Where(char.IsLetter).ToArray());
        BargeInVerdict? verdict = squeezed.ToUpperInvariant() switch
        {
            "INTERRUPT" or "INTERRUPTION" or "STOP" => BargeInVerdict.Interrupt,
            "NOTFORME" or "NOTFORYOU" or "NOTFORTHEASSISTANT" or "IGNORE" or "CONTINUE" => BargeInVerdict.NotForMe,
            _ => null
        };
        if (verdict is null) return null;
        var colon = text.IndexOf(':');
        var why = colon < 0 ? "" : new string(text[(colon + 1)..].Trim().TakeWhile(c => c != '\n').Take(60).ToArray()).Trim();
        return new(verdict.Value, why.Length > 0 ? $"the model judge: {why}"
            : verdict == BargeInVerdict.Interrupt ? "the model judge: for Martlet" : "the model judge: not for Martlet");
    }

    public async Task<BargeInJudgment> JudgeAsync(BargeInJudgeInput input, CancellationToken cancellationToken)
    {
        var answer = await ask(Instructions, Prompt(input), cancellationToken).ConfigureAwait(false);
        // No answer at all: no model could take the job now (the pool has no free capable member).
        if (answer is null) throw new BargeInJudgeUnavailableException();
        return Parse(answer) ?? throw new InvalidOperationException("The model judge's answer named no verdict.");
    }
}

/// <summary>No model could judge now (the Thinking pool has no capable member): the local rules decide at once, without waiting
/// for <see cref="BargeInJudging.Deadline"/>.</summary>
public sealed class BargeInJudgeUnavailableException() : Exception("No model judge is available.");

/// <summary>The timings of pause and decide, and the ruling with its deadline: a cue never waits for a judge; a judge that
/// doesn't answer within <see cref="Deadline"/> (or fails) gives way to the local rules.</summary>
public static class BargeInJudging
{
    /// <summary>How long a judge has before the local rules decide.</summary>
    public static TimeSpan Deadline => TimeSpan.FromMilliseconds(400);
    /// <summary>Paused, the user talking on for this long in all stops the reply whatever the judge said.</summary>
    public static TimeSpan KeepTalkingLimit => TimeSpan.FromMilliseconds(1500);
    /// <summary>Paused with a not-for-me verdict, this much quiet plays the reply on.</summary>
    public static TimeSpan QuietToResume => TimeSpan.FromMilliseconds(240);
    /// <summary>A pause never lasts longer than this: the verdict then decides, and with none the reply plays on.</summary>
    public static TimeSpan MaximumPause => TimeSpan.FromSeconds(4);

    /// <summary>Rules on <paramref name="input"/>: a cue at once, otherwise <paramref name="judge"/> within
    /// <paramref name="deadline"/> (default <see cref="Deadline"/>) or the local rules.</summary>
    public static async Task<BargeInRuling> RuleAsync(IBargeInJudge judge, BargeInJudgeInput input, TimeProvider clock,
        TimeSpan? deadline = null, CancellationToken cancellationToken = default)
    {
        var started = clock.GetTimestamp();
        var policy = BargeInPolicy.Decide(input.Heard, input.Context, input.Sensitivity);
        if (policy.Cue) return new(BargeInVerdict.Interrupt, policy.Reason, BargeInSource.Cue, "none", TimeSpan.Zero);
        var rules = RulesBargeInJudge.Judge(input);
        if (judge is RulesBargeInJudge) return new(rules.Verdict, rules.Reason, BargeInSource.Judge, judge.Name, clock.GetElapsedTime(started));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var answer = Task.Run(() => judge.JudgeAsync(input, stop.Token), stop.Token);
        var limit = Task.Delay(deadline ?? Deadline, clock, stop.Token);
        await Task.WhenAny(answer, limit).ConfigureAwait(false);
        var took = clock.GetElapsedTime(started);
        if (answer.IsCompletedSuccessfully && answer.Result is { } judged)
            return new(judged.Verdict, judged.Reason, BargeInSource.Judge, judge.Name, took);
        await stop.CancelAsync().ConfigureAwait(false);
        _ = answer.ContinueWith(static t => t.Exception, TaskScheduler.Default);
        var why = answer.Exception?.InnerException is BargeInJudgeUnavailableException ? $"; no {judge.Name} judge was available"
            : answer.IsFaulted ? $"; the {judge.Name} judge failed" : $"; the {judge.Name} judge was too slow";
        return new(rules.Verdict, rules.Reason + why, BargeInSource.Timeout, "rules", took);
    }
}

/// <summary>What happens to a reply paused while the user talks over it.</summary>
public enum BargeInOutcome { Pending, Resume, Stop }

/// <summary>One pause of a reply for words said over it: fed the user's voice frame by frame (20 ms each) from the pause on,
/// and the judge's rulings, it decides once whether the reply stops (an interrupt verdict, or the user kept talking past
/// <see cref="BargeInJudging.KeepTalkingLimit"/>) or plays on (a not-for-me verdict and the user quiet for
/// <see cref="BargeInJudging.QuietToResume"/>, or their utterance ended). A pause never outlasts
/// <see cref="BargeInJudging.MaximumPause"/>. Thread-safe; it decides only, the caller pauses and resumes.</summary>
public sealed class BargeInHold(TimeProvider clock)
{
    public const int FrameMilliseconds = 20;
    private readonly object gate = new();
    private int voiced, quiet;
    private bool ended;
    private BargeInRuling? ruling;
    private BargeInOutcome outcome;
    private string? why;
    private BargeInSource? source;
    private TimeSpan? held;

    public long PausedAt { get; } = clock.GetTimestamp();
    public BargeInRuling? Ruling { get { lock (gate) return ruling; } }
    public BargeInOutcome Outcome { get { lock (gate) return outcome; } }
    /// <summary>Why it stopped or played on, in a few words for the log.</summary>
    public string? Why { get { lock (gate) return why; } }
    /// <summary>What decided the outcome.</summary>
    public BargeInSource? Source { get { lock (gate) return source; } }
    /// <summary>How long the reply has been (or was) paused.</summary>
    public TimeSpan Paused { get { lock (gate) return held ?? clock.GetElapsedTime(PausedAt); } }
    /// <summary>How much the user talked from the pause on.</summary>
    public TimeSpan Voice { get { lock (gate) return TimeSpan.FromMilliseconds(voiced * FrameMilliseconds); } }

    /// <summary>A judge's ruling; a later one (on more of the words) replaces an earlier one until the outcome is decided.</summary>
    public BargeInOutcome Rule(BargeInRuling value)
    {
        lock (gate)
        {
            if (outcome == BargeInOutcome.Pending) ruling = value;
            return Decide();
        }
    }

    /// <summary>One 20 ms frame of the microphone since the pause: whether it was the user's voice.</summary>
    public BargeInOutcome Frame(bool voice)
    {
        lock (gate)
        {
            if (outcome != BargeInOutcome.Pending) return outcome;
            if (voice)
            {
                voiced++;
                quiet = 0;
            }
            else quiet++;
            return Decide();
        }
    }

    /// <summary>The user's utterance ended: they are quiet.</summary>
    public BargeInOutcome Ended()
    {
        lock (gate)
        {
            ended = true;
            return Decide();
        }
    }

    /// <summary>The outcome now, checking the time limit.</summary>
    public BargeInOutcome Evaluate()
    {
        lock (gate) return Decide();
    }

    private BargeInOutcome Decide()
    {
        if (outcome != BargeInOutcome.Pending) return outcome;
        if (ruling is { Interrupt: true } stop) return Finish(BargeInOutcome.Stop, stop.Reason, stop.Source);
        if (voiced * FrameMilliseconds >= BargeInJudging.KeepTalkingLimit.TotalMilliseconds)
            return Finish(BargeInOutcome.Stop, "you kept talking", BargeInSource.KeptTalking);
        if (ruling is { } go && (ended || quiet * FrameMilliseconds >= BargeInJudging.QuietToResume.TotalMilliseconds))
            return Finish(BargeInOutcome.Resume, go.Reason, go.Source);
        if (clock.GetElapsedTime(PausedAt) >= BargeInJudging.MaximumPause)
            return Finish(BargeInOutcome.Resume, ruling?.Reason ?? "no verdict in time", BargeInSource.Limit);
        return outcome;
    }

    private BargeInOutcome Finish(BargeInOutcome value, string reason, BargeInSource by)
    {
        outcome = value;
        why = reason;
        source = by;
        held = clock.GetElapsedTime(PausedAt);
        return value;
    }
}
