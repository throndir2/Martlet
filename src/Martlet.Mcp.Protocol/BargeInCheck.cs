using System.IO;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>barge_in_check: Companion › Listening › When you talk over Martlet. Simulates words said over a reply and reads the
/// production verdict (<see cref="BargeInJudging"/>, <see cref="RulesBargeInJudge"/>): a clear cue stops at once and is never
/// judged; other words pause the reply and the judge says interrupt or not for Martlet. It also rehearses the judge's deadline
/// (a fixture model judge, NOT AI, slower than <see cref="BargeInJudging.Deadline"/> gives way to the local rules, and one in
/// time is used) and what a pause does on a simulated clock (<see cref="BargeInHold"/>: the user going quiet after a not-for-me
/// verdict plays the reply on; talking on past <see cref="BargeInJudging.KeepTalkingLimit"/> stops it; an interrupt verdict
/// stops it). Reads the saved choice from talk-preferences.json. Nothing is recorded or played; nothing leaves this PC.</summary>
internal static class BargeInCheck
{
    private const int MaximumSamples = 64;

    private sealed record Sample(string Name, string Heard, string? Sentence, string? Reply, int VoicedMs, BargeInVerdict? Expect,
        bool? ExpectCue = null);

    internal static async Task<object> RunAsync(JsonElement arguments, string dataDirectory, CancellationToken cancellation)
    {
        var (behavior, source, bargeIn, wordCheck) = Saved(dataDirectory);
        var sensitivity = wordCheck;
        var samples = Samples(arguments) ?? Defaults;
        var results = new List<object>();
        var samplesOk = true;
        foreach (var sample in samples)
        {
            var input = new BargeInJudgeInput(sample.Heard, sample.Sentence, sample.Reply,
                new UtteranceContext { Voiced = TimeSpan.FromMilliseconds(sample.VoicedMs) }, sensitivity);
            var quick = BargeInPolicy.Decide(sample.Heard, input.Context, sensitivity);
            var ruling = await BargeInJudging.RuleAsync(RulesBargeInJudge.Instance, input, TimeProvider.System, cancellationToken: cancellation);
            // What the reply does: nothing for words the quick check lets go, stops at once for a cue or with Stop at once,
            // otherwise pauses and the verdict decides.
            var action = !quick.Interrupt ? "keeps playing" : quick.Cue || behavior == BargeInBehavior.StopAtOnce ? "stops at once"
                : ruling.Interrupt ? "pauses, then stops" : "pauses, then plays on";
            var ok = (sample.Expect is null || sample.Expect == ruling.Verdict) && (sample.ExpectCue is null || sample.ExpectCue == quick.Cue);
            samplesOk &= ok;
            results.Add(new
            {
                name = sample.Name, heard = sample.Heard, ok, verdict = ruling.Verdict.ToString(), reason = ruling.Reason,
                source = ruling.Source.ToString(), judge = ruling.Judge, judgeMs = Math.Round(ruling.JudgeTime.TotalMilliseconds, 2),
                quickCheckInterrupts = quick.Interrupt, cue = quick.Cue, action, expect = sample.Expect?.ToString()
            });
        }

        var deadline = TimeSpan.FromMilliseconds(Int(arguments, "deadlineMs") ?? (int)BargeInJudging.Deadline.TotalMilliseconds);
        var slow = TimeSpan.FromMilliseconds(Int(arguments, "judgeDelayMs") ?? 1_000);
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromSeconds(5)) throw new ArgumentException("deadlineMs must be 1-5000.");
        if (slow < TimeSpan.Zero || slow > TimeSpan.FromSeconds(10)) throw new ArgumentException("judgeDelayMs must be 0-10000.");
        var deadlines = await DeadlineAsync(deadline, slow, sensitivity, cancellation);
        var holds = Holds();
        var deadlinesOk = deadlines.All(d => d.Ok);
        var holdsOk = holds.All(h => h.Ok);
        return new
        {
            ok = samplesOk && deadlinesOk && holdsOk,
            behavior = behavior.ToString(),
            behaviorSource = source,
            bargeIn,
            wordCheck = sensitivity.ToString(),
            timings = new
            {
                deadlineMs = (int)BargeInJudging.Deadline.TotalMilliseconds,
                keepTalkingLimitMs = (int)BargeInJudging.KeepTalkingLimit.TotalMilliseconds,
                quietToResumeMs = (int)BargeInJudging.QuietToResume.TotalMilliseconds,
                maximumPauseMs = (int)BargeInJudging.MaximumPause.TotalMilliseconds,
                voiceBeforeCheckMs = (int)BargeInPolicy.VoiceBeforeCheck(sensitivity).TotalMilliseconds,
                resumeFadeMs = (int)Martlet.Audio.PlaybackRun.ResumeFade.TotalMilliseconds
            },
            samplesOk,
            samples = results,
            deadlinesOk,
            deadlines = deadlines.Select(d => d.Value),
            holdsOk,
            holds = holds.Select(h => h.Value)
        };
    }

    private sealed record Result(bool Ok, object Value);

    // A fixture model judge (NOT AI) that always says interrupt after a delay: slower than the deadline the rules decide (here
    // "not for Martlet" for agreement), in time its verdict is used.
    private sealed class FixtureJudge(TimeSpan delay) : IBargeInJudge
    {
        public string Name => "fixture model";
        public async Task<BargeInJudgment> JudgeAsync(BargeInJudgeInput input, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new(BargeInVerdict.Interrupt, "the fixture model judge says it is for Martlet");
        }
    }

    private static async Task<Result[]> DeadlineAsync(TimeSpan deadline, TimeSpan slow, ListeningSensitivity sensitivity,
        CancellationToken cancellation)
    {
        var input = new BargeInJudgeInput("yeah that's so true", "The best part is the view from the top.", null,
            new UtteranceContext { Voiced = TimeSpan.FromMilliseconds(900) }, sensitivity);
        var results = new List<Result>();
        foreach (var (name, delay) in new[] { ("slow model judge", slow), ("model judge in time", TimeSpan.Zero) })
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var late = delay >= deadline;
            // The judge in time gets a generous deadline, so a busy PC never makes it look late.
            var limit = late ? deadline : deadline + TimeSpan.FromSeconds(5);
            var ruling = await BargeInJudging.RuleAsync(new FixtureJudge(delay), input, TimeProvider.System, limit, cancellation);
            var tookMs = watch.Elapsed.TotalMilliseconds;
            var ok = late
                ? ruling.Source == BargeInSource.Timeout && ruling.Verdict == BargeInVerdict.NotForMe && tookMs < deadline.TotalMilliseconds + 1_000
                : ruling.Source == BargeInSource.Judge && ruling.Verdict == BargeInVerdict.Interrupt;
            results.Add(new(ok, new
            {
                name, ok, judgeDelayMs = delay.TotalMilliseconds, deadlineMs = limit.TotalMilliseconds, verdict = ruling.Verdict.ToString(),
                source = ruling.Source.ToString(), judge = ruling.Judge, reason = ruling.Reason, tookMs = Math.Round(tookMs)
            }));
        }
        return [.. results];
    }

    // What a pause does, frame by frame on a simulated clock (20 ms frames).
    private static Result[] Holds()
    {
        var notForMe = new BargeInRuling(BargeInVerdict.NotForMe, "a quick backchannel", BargeInSource.Judge, "rules", TimeSpan.Zero);
        var forMe = new BargeInRuling(BargeInVerdict.Interrupt, "3 words", BargeInSource.Judge, "rules", TimeSpan.Zero);
        return
        [
            Hold("not for Martlet, then quiet", notForMe, voiceFrames: 10, BargeInOutcome.Resume),
            Hold("not for Martlet, but you keep talking", notForMe, voiceFrames: 100, BargeInOutcome.Stop),
            Hold("for Martlet", forMe, voiceFrames: 5, BargeInOutcome.Stop),
            Hold("no verdict", null, voiceFrames: 0, BargeInOutcome.Resume)
        ];
    }

    private static Result Hold(string name, BargeInRuling? ruling, int voiceFrames, BargeInOutcome expect)
    {
        var clock = new ManualClock();
        var hold = new BargeInHold(clock);
        if (ruling is not null) hold.Rule(ruling);
        var frames = 0;
        var maximum = (int)(BargeInJudging.MaximumPause.TotalMilliseconds / BargeInHold.FrameMilliseconds) + 5;
        while (hold.Evaluate() == BargeInOutcome.Pending && frames < maximum)
        {
            clock.Advance(TimeSpan.FromMilliseconds(BargeInHold.FrameMilliseconds));
            hold.Frame(frames < voiceFrames);
            frames++;
        }
        var ok = hold.Outcome == expect;
        return new(ok, new
        {
            name, ok, outcome = hold.Outcome.ToString(), expect = expect.ToString(), why = hold.Why, source = hold.Source?.ToString(),
            pausedMs = hold.Paused.TotalMilliseconds, voiceMs = hold.Voice.TotalMilliseconds
        });
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        internal void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }

    private static readonly IReadOnlyList<Sample> Defaults =
    [
        new("stop-word", "Wait, stop.", "The best part is the view from the top.", null, 400, BargeInVerdict.Interrupt, ExpectCue: true),
        new("name", "Martlet, what time is it?", "The best part is the view from the top.", null, 900, BargeInVerdict.Interrupt, ExpectCue: true),
        new("backchannel", "Yeah.", "The best part is the view from the top.", null, 300, BargeInVerdict.NotForMe, ExpectCue: false),
        new("agreement", "Yeah that's so true.", "The best part is the view from the top.", null, 900, BargeInVerdict.NotForMe, ExpectCue: false),
        new("laughing-along", "Haha no way.", "And then the cat sat on the keyboard.", null, 700, BargeInVerdict.NotForMe, ExpectCue: false),
        new("own-words-heard-back", "the view from the top", "The best part is the view from the top.", null, 900, BargeInVerdict.NotForMe,
            ExpectCue: false),
        new("question", "What about the weather tomorrow?", "The best part is the view from the top.", null, 1100, BargeInVerdict.Interrupt,
            ExpectCue: false),
        new("new-request", "Can you play some music instead?", "The best part is the view from the top.", null, 1200, BargeInVerdict.Interrupt,
            ExpectCue: false)
    ];

    private static IReadOnlyList<Sample>? Samples(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("samples", out var list)) return null;
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is 0 or > MaximumSamples)
            throw new ArgumentException($"samples must be an array of 1-{MaximumSamples} items.");
        return list.EnumerateArray().Select((item, i) =>
        {
            var heard = Text(item, "heard") ?? throw new ArgumentException("Each sample needs heard.");
            BargeInVerdict? expect = Text(item, "expectVerdict") is { } verdict
                ? verdict switch
                {
                    "interrupt" => BargeInVerdict.Interrupt,
                    "notForMe" => BargeInVerdict.NotForMe,
                    _ => throw new ArgumentException("expectVerdict must be interrupt or notForMe.")
                }
                : null;
            return new Sample(Text(item, "name") ?? $"sample-{i + 1}", heard, Text(item, "sentence"), Text(item, "recentReply"),
                Int(item, "voicedMs") ?? 900, expect);
        }).ToArray();
    }

    private static string? Text(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var text = value.GetString();
        if (text is null || text.Length > 1024) throw new ArgumentException($"{name} must be a string of up to 1024 characters.");
        return text;
    }

    private static int? Int(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32() : null;

    // The saved choices: When you talk over Martlet, Let me interrupt Martlet by talking and Word check.
    private static (BargeInBehavior Behavior, string Source, bool BargeIn, ListeningSensitivity WordCheck) Saved(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "talk-preferences.json");
        if (!File.Exists(path)) return (BargeInBehavior.PauseAndDecide, "default", false, ListeningSensitivity.Normal);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var behavior = root.TryGetProperty("BargeInStyle", out var style) && style.ValueKind == JsonValueKind.Number &&
                Enum.IsDefined((BargeInBehavior)style.GetInt32()) ? (BargeInBehavior)style.GetInt32() : BargeInBehavior.PauseAndDecide;
            var bargeIn = root.TryGetProperty("BargeIn", out var on) && on.ValueKind == JsonValueKind.True &&
                root.TryGetProperty("Version", out var version) && version.ValueKind == JsonValueKind.Number && version.GetInt32() >= 3;
            var wordCheck = root.TryGetProperty("WordCheck", out var check) && check.ValueKind == JsonValueKind.Number &&
                Enum.IsDefined((ListeningSensitivity)check.GetInt32()) ? (ListeningSensitivity)check.GetInt32() : ListeningSensitivity.Normal;
            return (behavior, "talk-preferences.json", bargeIn, wordCheck);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return (BargeInBehavior.PauseAndDecide, "unreadable talk-preferences.json (the default)", false, ListeningSensitivity.Normal);
        }
    }
}
