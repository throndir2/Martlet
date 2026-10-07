using System.Diagnostics;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Sherpa;

namespace Martlet.Mcp;

/// <summary>turn_judge_check: Companion › Listening › Judge when I finish talking, headless. Loads the Smart Turn model and ONNX
/// Runtime bundled in a Martlet folder through the production <see cref="SmartTurnEngine"/>, judges finished and unfinished
/// phrases a Windows voice says (rendered to memory, never played), each ending 260 ms into the pause as always listening asks it,
/// and rehearses the production <see cref="EndOfTurnGate"/> frame by frame: when a complete, an incomplete, a slow and a failed
/// judgement end the turn. Nothing is recorded, played, downloaded or sent.</summary>
internal static class TurnJudgeCheck
{
    /// <summary>The slowest median judge time (features and model) that still passes: the judge must answer well inside the plain
    /// 800 ms pause.</summary>
    internal const double MaximumMedianMs = 100;

    internal static readonly (string Text, TurnVerdict Expected)[] Phrases =
    [
        ("Can you remind me to call my sister after lunch?", TurnVerdict.Complete),
        ("What's the weather like tomorrow morning?", TurnVerdict.Complete),
        ("I think the second option sounds better.", TurnVerdict.Complete),
        ("So I was thinking that maybe we could", TurnVerdict.Incomplete),
        ("Can you remind me to call my sister and", TurnVerdict.Incomplete),
        ("The thing I wanted to ask you about is", TurnVerdict.Incomplete)
    ];

    internal static async Task<object> RunAsync(JsonElement arguments, string martletDirectory, CancellationToken cancellation)
    {
        var gate = Gate();
        var model = SmartTurnEngine.ModelPath(martletDirectory);
        var runtime = SherpaComponents.RuntimeDirectory(martletDirectory);
        if (model is null || runtime is null)
            return new
            {
                ran = false, ok = false, gate,
                reason = model is null ? $"the Smart Turn model isn't in martletDirectory ({SmartTurnEngine.Folder}\\{SmartTurnEngine.ModelFile}; build Martlet.Desktop)"
                    : "the ONNX Runtime isn't in martletDirectory (build Martlet.Desktop)"
            };
        var clips = await Task.Run(() => Phrases.Select(phrase => (phrase.Text, phrase.Expected, Pcm: Trim(UtteranceFilterCheck.Synthesize("speech:" + phrase.Text)))).ToArray(),
            cancellation);
        using var engine = new SmartTurnEngine(model, runtime);
        TimeSpan load;
        try { load = await Task.Run(engine.Warm, cancellation); }
        catch (Exception error) when (error is SherpaException or DllNotFoundException or BadImageFormatException or InvalidOperationException or EntryPointNotFoundException)
        {
            return new { ran = false, ok = false, reason = error.Message, gate };
        }
        var results = new List<object>();
        var times = new List<double>();
        var agreed = 0;
        foreach (var (text, expected, pcm) in clips)
        {
            cancellation.ThrowIfCancellationRequested();
            var samples = UtteranceFilterCheck.ToFloats(pcm, 0, pcm.Length);
            var watch = Stopwatch.StartNew();
            var probability = await Task.Run(() => engine.Probability(samples), cancellation);
            var ms = Math.Round(watch.Elapsed.TotalMilliseconds, 1);
            times.Add(ms);
            var verdict = probability > 0.5 ? TurnVerdict.Complete : TurnVerdict.Incomplete;
            if (verdict == expected) agreed++;
            results.Add(new
            {
                said = text, expected = expected.ToString(), verdict = verdict.ToString(), probability = Math.Round(probability, 3),
                judgeMs = ms, seconds = Math.Round(pcm.Length / 2.0 / SmartTurnEngine.SampleRate, 2)
            });
        }
        times.Sort();
        var median = times[times.Count / 2];
        var gateOk = gate.All(g => g.ok);
        return new
        {
            ran = true, ok = median <= MaximumMedianMs && gateOk, judge = SmartTurnEngine.Name, loadMs = Math.Round(load.TotalMilliseconds),
            medianJudgeMs = median, maximumMedianMs = MaximumMedianMs, agreed, phrases = results.Count,
            scene = "each phrase is said by a Windows voice after 0.3 s of faint noise and ends 260 ms into the pause that follows (a " +
                "synthetic voice; agreement is informative, not part of ok)",
            results, gate
        };
    }

    // The fixture's 1 s of noise after the voice, cut to the 260 ms always listening waits before asking the judge.
    private static byte[] Trim(byte[] pcm) => pcm[..Math.Max(0, pcm.Length - (int)(0.74 * SmartTurnEngine.SampleRate) * 2)];

    internal sealed record GateResult(string judgement, int? endedAtMs, string expected, bool ok);

    // The production gate with the plain 800 ms pause, frame by frame through one pause: where each kind of answer ends the turn.
    // The detector itself ends an unfinished pause at the gate's longer pause (DetectorEndSilence).
    internal static GateResult[] Gate()
    {
        var plain = TimeSpan.FromMilliseconds(800);
        GateResult Run(string name, Func<int, EndOfTurnJudgement?>? answer, int answerAfterFrames, int expectedMs, string expected)
        {
            var gate = new EndOfTurnGate(plain);
            var cap = (int)(gate.DetectorEndSilence.TotalMilliseconds / 20);
            var askedAt = -1;
            for (var frame = 1; frame <= cap; frame++)
            {
                if (askedAt >= 0 && frame - askedAt == answerAfterFrames && answer is not null) gate.Judged(gate.Pause, answer(frame));
                var step = gate.Step(frame, judgeable: true);
                if (step == EndOfTurnStep.Judge) askedAt = frame;
                if (step is EndOfTurnStep.Complete or EndOfTurnStep.Fallback)
                    return new(name, frame * 20, expected, frame * 20 == expectedMs);
            }
            return new(name, cap * 20, expected, cap * 20 == expectedMs);
        }
        return
        [
            Run("complete", _ => new(TurnVerdict.Complete, 0.9), 2, 300, "ends 40 ms after the judge is asked at 260 ms"),
            Run("incomplete", _ => new(TurnVerdict.Incomplete, 0.1), 2, 1600, "waits for the longer pause (1600 ms)"),
            Run("slow", null, 0, 800, "the plain 800 ms pause ends it"),
            Run("failed", _ => null, 2, 800, "the plain 800 ms pause ends it")
        ];
    }
}
