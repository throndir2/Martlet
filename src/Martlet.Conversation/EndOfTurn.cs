using System.Globalization;

namespace Martlet.Conversation;

/// <summary>Whether the user finished their turn: <see cref="Complete"/> ends it at once; <see cref="Incomplete"/> keeps listening
/// a while longer.</summary>
public enum TurnVerdict { Complete, Incomplete }

/// <summary>What an end-of-turn judge gets: what was said so far (16 kHz mono PCM16, from just before the voice began to now,
/// with the short silence after it) and, when speech-to-text runs on this PC, its quick transcript of the same speech (the task
/// gives null when there is none or it failed). An audio judge reads the sound; a text judge (a Thinking pool model, later) can
/// read the words.</summary>
public sealed record EndOfTurnRequest(ReadOnlyMemory<byte> Pcm, TimeSpan Silence, Task<string?>? Words = null);

/// <summary>A judge's answer and, when it has one, how sure it is that the turn is complete (0-1).</summary>
public sealed record EndOfTurnJudgement(TurnVerdict Verdict, double? Probability = null);

/// <summary>The seam for end-of-turn judges. Always listening asks one after a short pause (<see cref="EndOfTurnOptions.JudgeAfter"/>)
/// and must have an answer before the plain pause rule (Companion › Listening › Reply after) would end the turn; a judge that is
/// missing (<see cref="Available"/> false), throws or is slower than that leaves the plain rule to decide. Smart Turn on this PC
/// is the primary judge; a Thinking-pool model can be added behind it with <see cref="EndOfTurnJudges.WithFallback"/>.</summary>
public interface IEndOfTurnJudge
{
    /// <summary>For the log and status, for example "Smart Turn v3.2".</summary>
    string Name { get; }

    /// <summary>Whether the judge can answer now (its model is there and loads).</summary>
    bool Available { get; }

    Task<EndOfTurnJudgement> JudgeAsync(EndOfTurnRequest request, CancellationToken cancellationToken);
}

public static class EndOfTurnJudges
{
    /// <summary>A judge that asks <paramref name="primary"/> and, when it isn't available or fails, <paramref name="fallback"/>.</summary>
    public static IEndOfTurnJudge WithFallback(IEndOfTurnJudge primary, IEndOfTurnJudge fallback) => new Chain(primary, fallback);

    private sealed class Chain(IEndOfTurnJudge primary, IEndOfTurnJudge fallback) : IEndOfTurnJudge
    {
        public string Name => primary.Available ? primary.Name : fallback.Name;
        public bool Available => primary.Available || fallback.Available;

        public async Task<EndOfTurnJudgement> JudgeAsync(EndOfTurnRequest request, CancellationToken cancellationToken)
        {
            if (primary.Available)
            {
                try { return await primary.JudgeAsync(request, cancellationToken).ConfigureAwait(false); }
                catch (Exception) when (!cancellationToken.IsCancellationRequested && fallback.Available) { }
            }
            return await fallback.JudgeAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Companion › Listening › Judge when I finish talking (on by default). <see cref="JudgeAfter"/>: the pause before the
/// judge is asked. <see cref="IncompleteCap"/>: the longest pause always listening waits through when the judge says the user
/// isn't finished (at least twice the plain pause).</summary>
public sealed record EndOfTurnOptions
{
    public bool Enabled { get; init; } = true;
    public TimeSpan JudgeAfter { get; init; } = TimeSpan.FromMilliseconds(260);
    public TimeSpan IncompleteCap { get; init; } = TimeSpan.FromMilliseconds(1600);
    public double Threshold { get; init; } = 0.5;

    /// <summary>The longest pause for speech the judge found unfinished, with the plain pause <paramref name="endSilence"/>.</summary>
    public TimeSpan Cap(TimeSpan endSilence) =>
        TimeSpan.FromTicks(Math.Clamp(Math.Max(IncompleteCap.Ticks, endSilence.Ticks * 2), endSilence.Ticks, TimeSpan.FromSeconds(5).Ticks));
}

/// <summary>What always listening does at this frame of a pause.</summary>
public enum EndOfTurnStep
{
    /// <summary>Keep listening.</summary>
    Wait,
    /// <summary>Ask the judge now (once per pause).</summary>
    Judge,
    /// <summary>The judge said the turn is complete: end it now.</summary>
    Complete,
    /// <summary>The plain pause rule ends the turn (<see cref="EndOfTurnGate.Fallback"/> says why the judge didn't decide).</summary>
    Fallback
}

/// <summary>Why the plain pause rule ended a turn while the judge was on.</summary>
public enum EndOfTurnFallback { None, NotJudged, Slow, Failed }

/// <summary>The end-of-turn decision for one utterance, frame by frame (20 ms each) through each pause in it. After
/// <see cref="EndOfTurnOptions.JudgeAfter"/> of silence the judge is asked once; Complete ends the turn at once; Incomplete lets the
/// pause run up to <see cref="EndOfTurnOptions.Cap"/> (the voice activity detector, built with <see cref="DetectorEndSilence"/>,
/// ends it there); no answer yet (slow), a failure or no judge leaves the plain pause (<c>endSilence</c>) to end it, exactly as
/// without the judge. Talking again starts a new pause with a new question.</summary>
public sealed class EndOfTurnGate
{
    private readonly int judgeFrames, endFrames;
    private EndOfTurnJudgement? verdict;
    private bool silent, asked, failed;

    public EndOfTurnGate(TimeSpan endSilence, EndOfTurnOptions? options = null)
    {
        Options = options ?? new();
        EndSilence = endSilence;
        DetectorEndSilence = Options.Cap(endSilence);
        judgeFrames = Math.Max(1, (int)Math.Ceiling(Options.JudgeAfter.TotalMilliseconds / 20));
        endFrames = Math.Max(1, (int)Math.Round(endSilence.TotalMilliseconds / 20));
    }

    public EndOfTurnOptions Options { get; }
    public TimeSpan EndSilence { get; }
    /// <summary>The pause the voice activity detector itself ends speech at: the longer pause for unfinished speech.</summary>
    public TimeSpan DetectorEndSilence { get; }
    /// <summary>Counts the pauses so far; a judgement for an earlier one is ignored.</summary>
    public int Pause { get; private set; }
    public EndOfTurnJudgement? Verdict => verdict;
    public bool Asked => asked;
    public EndOfTurnFallback Fallback { get; private set; }
    /// <summary>Set for one frame when the user went on talking after the judge found the pause unfinished.</summary>
    public EndOfTurnJudgement? WentOn { get; private set; }

    /// <summary>One frame of the speech under way: how many silent frames end it so far (0 while the voice goes on) and whether
    /// the judge may be asked (the voice was accepted as someone talking and a judge is there).</summary>
    public EndOfTurnStep Step(int silenceFrames, bool judgeable)
    {
        WentOn = null;
        if (silenceFrames <= 0)
        {
            if (silent && verdict?.Verdict == TurnVerdict.Incomplete) WentOn = verdict;
            silent = false;
            return EndOfTurnStep.Wait;
        }
        if (!silent)
        {
            silent = true;
            Pause++;
            verdict = null;
            asked = failed = false;
            Fallback = EndOfTurnFallback.None;
        }
        if (verdict?.Verdict == TurnVerdict.Complete) return EndOfTurnStep.Complete;
        if (judgeable && !asked && silenceFrames >= judgeFrames && judgeFrames < endFrames)
        {
            asked = true;
            return EndOfTurnStep.Judge;
        }
        if (silenceFrames >= endFrames && verdict?.Verdict != TurnVerdict.Incomplete)
        {
            Fallback = !asked ? EndOfTurnFallback.NotJudged : failed ? EndOfTurnFallback.Failed : EndOfTurnFallback.Slow;
            return EndOfTurnStep.Fallback;
        }
        return EndOfTurnStep.Wait;
    }

    /// <summary>The judge's answer for pause <paramref name="pause"/> (null when it failed). Returns false when that pause is
    /// over (the user went on talking), so the answer no longer counts.</summary>
    public bool Judged(int pause, EndOfTurnJudgement? judgement)
    {
        if (pause != Pause || !silent) return false;
        if (judgement is null) failed = true;
        else verdict = judgement;
        return true;
    }
}

/// <summary>One end-of-turn decision for the desktop log and Companion › Listening's status (no words, no audio). Outcome is
/// complete, incomplete (the longer pause ran out), went-on (judged unfinished and the user went on), slow, failed or
/// not-judged (the plain pause decided).</summary>
public sealed record EndOfTurnDecision(DateTimeOffset At, string Outcome, TimeSpan Silence, TimeSpan EndSilence,
    TimeSpan? JudgeTime = null, double? Probability = null, string? Judge = null, string? Problem = null)
{
    public const string Complete = "complete", Incomplete = "incomplete", WentOn = "went-on", Slow = "slow", Failed = "failed",
        NotJudged = "not-judged";

    /// <summary>The desktop log's line, for example <c>End of turn: complete (Smart Turn v3.2, 0.93) after 280 ms of silence;
    /// judge 31 ms.</c></summary>
    public string Describe()
    {
        var by = Judge is null ? "" : Probability is { } p ? $" ({Judge}, {p.ToString("0.00", CultureInfo.InvariantCulture)})" : $" ({Judge})";
        var silence = Ms(Silence);
        var judged = JudgeTime is { } took ? $"; judge {Ms(took)} ms" : "";
        return "End of turn: " + Outcome switch
        {
            Complete => $"complete{by} after {silence} ms of silence{judged}.",
            Incomplete => $"incomplete{by}; the longer pause for unfinished speech ended it after {silence} ms of silence{judged}.",
            WentOn => $"incomplete{by}, and you went on talking after {silence} ms of silence{judged}.",
            Slow => $"the judge{by} had no answer within {Ms(EndSilence)} ms of silence; the plain pause ended the turn.",
            Failed => $"the judge{by} failed ({Problem ?? "error"}); the plain {Ms(EndSilence)} ms pause ended the turn.",
            _ => $"not judged ({Problem ?? "no judge"}); the plain {Ms(EndSilence)} ms pause ended the turn."
        };
    }

    private static string Ms(TimeSpan value) => value.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);
}
