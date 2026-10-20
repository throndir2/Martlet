using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.HostClient;
using Martlet.Core.Cluster;
using Martlet.Core.Speakers;
using Martlet.Sherpa;

namespace Martlet.Mcp;

/// <summary>
/// Companion › People › Where it runs (docs/VOICES.md#where-voice-recognition-runs): the Voice recognition list
/// (<see cref="PoolAreas.VoiceRecognition"/>, pools.json, shared) and how recognition keeps working. <see cref="Status"/> gives a
/// data directory's list, the places this PC tries (<see cref="VoiceRecognitionPool.Targets"/>) and the desktop's own state file
/// (voice-recognition-state.json: this PC's engine, the last recognition, the computers tried). <see cref="RunAsync"/> rehearses
/// the production routing (<see cref="VoiceRecognitionRouter{T}"/> through <see cref="WorkQueue"/>), the engine keeper
/// (<see cref="VoiceEngineKeeper{T}"/>), the answer checks (<see cref="SpeakerAnswers"/>) and the request
/// (<see cref="HostConnection.VoiceRecognitionPayload"/>) with SIMULATED computers and a simulated engine, NOT real
/// hosts. With a Martlet folder it also runs the real engine on this PC through the keeper and reports its speed.
/// </summary>
internal static class VoiceRecognitionCheck
{
    private const int Rate = SpeakerEngine.SampleRate;

    /// <summary>The list, the places this PC tries and the desktop's state, from a data directory's files.</summary>
    internal static object Status(string dataDirectory, string? device = null)
    {
        device ??= Martlet.Diagnostics.LocalLogs.ThisDeviceId(dataDirectory);
        var paired = Paired(dataDirectory);
        var saved = PoolSettings.LoadFor(dataDirectory, PoolAreas.VoiceRecognition);
        var area = PoolAreas.VoiceRecognition;
        var tries = VoiceRecognitionPool.Targets(saved, device, paired.Contains);
        JsonNode? state = null;
        try
        {
            var path = Path.Combine(dataDirectory, "voice-recognition-state.json");
            if (File.Exists(path) && new FileInfo(path).Length <= 65_536) state = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { state = null; }
        return new
        {
            area = area.Id, page = area.Page, file = PoolSettings.File(area.Shared), shared = area.Shared, required = area.Required,
            whenEmpty = area.WhenEmpty, hostRole = area.HostRole, lane = VoiceRecognitionRouter<object>.Lane, device,
            list = saved is null ? "none saved: this PC's processor" : "saved",
            members = (saved?.Members ?? []).Select((m, i) => new
            {
                index = i, key = m.Key, name = m.Name, kind = m.Kind.ToString(), on = !m.Off, recognizes = VoiceRecognitionPool.Describe(m),
                onlyFor = m.OnlyFor, paired = m.HostId is null || paired.Contains(m.HostId)
            }),
            tries = tries.Select(t => new { key = t.Key, hostId = t.HostId, fallback = t.Fallback }),
            summary = VoiceRecognitionPool.Summary(tries),
            // Written by the running desktop (this PC only): null when it never ran here, or before this version.
            desktop = state
        };
    }

    private static HashSet<string> Paired(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "hosts.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 65_536) return [];
            return [.. (JsonNode.Parse(File.ReadAllText(path))?["hosts"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(h => h["pairing"]?["hostId"]?.GetValue<string>()).OfType<string>()];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return []; }
    }

    // ---------- the rehearsal ----------

    private sealed class Busy : Exception;
    private sealed class Gone() : Exception("it isn't reachable");

    private static WorkRefusal Classify(Exception error) => error switch
    {
        Busy => WorkRefusal.Busy,
        _ => WorkRefusal.Unavailable
    };

    /// <summary>A simulated engine on this PC: answers at once, or fails a number of times (a native failure) before it works.</summary>
    private sealed class Engine(Func<int> fail, List<string> calls) : IVoiceEngine<SpeakerAnalysis>
    {
        public SpeakerAnalysis Analyze(float[] samples)
        {
            lock (calls) calls.Add("this-pc");
            if (fail() > 0) throw new SherpaException("The voice model could not be loaded.");
            return new([], 1, false, Print(0.5f), samples.Length / (double)Rate);
        }

        public void Load()
        {
            if (fail() > 0) throw new SherpaException("The voice model could not be loaded.");
        }

        public void Dispose() { }
    }

    private static float[] Print(float lean)
    {
        var vector = Enumerable.Range(0, SpeakerAnswers.Dimension).Select(i => (float)(1 + lean * Math.Sin(i))).ToArray();
        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        return [.. vector.Select(v => (float)(v / norm))];
    }

    /// <summary>A host's answer to analyze, as the contract says (one speaker, 2.4 clean seconds).</summary>
    private static string Answer(float[] print, int voices = 1) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["speakers"] = new[] { new Dictionary<string, object> { ["local"] = 0, ["voiceprint"] = print, ["clean_seconds"] = 2.4, ["start"] = 0.5, ["end"] = 2.9 } },
        ["voices"] = voices, ["overlap"] = false, ["whole"] = null, ["speech_seconds"] = 2.9, ["milliseconds"] = 12
    });

    private sealed record Heard(string? By, double Ms, bool Raced, string[] Passed, string? Failed, string[] Calls);

    internal static async Task<object> RunAsync(string? martletDirectory, CancellationToken cancellation)
    {
        List<object> steps = [];
        var ok = true;
        void Step(string name, bool passed, object detail)
        {
            ok &= passed;
            steps.Add(new { name, passed, detail });
        }

        // The list's order: desk-1 is this PC; gpu-pc and m3-host are paired, nas-host isn't.
        bool Paired(string id) => id is "gpu-pc" or "m3-host" or "kept-pc";
        PoolList List(params PoolMember[] members) => new() { Area = PoolAreas.VoiceRecognition.Id, Members = members };
        string[] Keys(IReadOnlyList<VoiceRecognitionTarget> t) => [.. t.Select(x => x.Key + (x.Fallback ? " (fallback)" : ""))];
        var ordered = Keys(VoiceRecognitionPool.Targets(List(PoolMember.Computer("gpu-pc"), PoolMember.Gpu("gpu-pc", 2), PoolMember.ThisPc(),
            PoolMember.Computer("nas-host"), PoolMember.Computer("kept-pc") with { OnlyFor = ["desk-2"] }, PoolMember.Computer("m3-host")), "desk-1", Paired));
        Step("The list's order: gpu-pc once (a card is its computer), this PC, then m3-host; a computer not paired here or kept for " +
            "another companion PC is left out", ordered.SequenceEqual(["host:gpu-pc", "this-pc", "host:m3-host"]), new { tries = ordered });
        var hostOnly = Keys(VoiceRecognitionPool.Targets(List(PoolMember.Computer("gpu-pc"), PoolMember.ThisPc() with { Off = true }), "desk-1", Paired));
        var none = Keys(VoiceRecognitionPool.Targets(null, "desk-1", Paired));
        var empty = Keys(VoiceRecognitionPool.Targets(List(), "desk-1", Paired));
        var allOff = Keys(VoiceRecognitionPool.Targets(List(PoolMember.Computer("gpu-pc") with { Off = true }), "desk-1", Paired));
        Step("This PC's processor always comes last as the fallback: with no list, an empty list, nothing on or only computers on",
            hostOnly.SequenceEqual(["host:gpu-pc", "this-pc (fallback)"]) && none.SequenceEqual(["this-pc"]) &&
            empty.SequenceEqual(["this-pc (fallback)"]) && allOff.SequenceEqual(["this-pc (fallback)"]), new { hostOnly, none, empty, allOff });

        // Shared like the other shared lists: two simulated computers.
        var desk = new PoolSettings().With(List(PoolMember.Computer("gpu-pc"), PoolMember.ThisPc()));
        var other = PoolSettings.Parse(desk.Share());
        Step("The list is shared (pools.json, the pools shared setting) and required, with this PC's processor as its fallback; a " +
            "second computer reads the same list", PoolAreas.VoiceRecognition is { Shared: true, Required: true } &&
            PoolAreas.VoiceRecognition.WhenEmpty == "This PC's processor" && other is not null &&
            other.Pool(PoolAreas.VoiceRecognition.Id).Members.Select(m => m.Key).SequenceEqual(["host:gpu-pc", "this-pc"]),
            new { file = PoolSettings.File(PoolAreas.VoiceRecognition.Shared), second = other?.Pool(PoolAreas.VoiceRecognition.Id).Members.Select(m => m.Key) });

        // The request and the answer checks.
        var samples = new float[3 * Rate];
        for (var i = 0; i < samples.Length; i++) samples[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 180 * i / Rate));
        var pcm = SpeakerAnswers.Pcm16(samples);
        var payload = HostConnection.VoiceRecognitionPayload("analyze", pcm);
        Step("The request: operation analyze, sample_rate 16000 and the audio as PCM16 in base64 (3 s: 96,000 bytes)",
            (string)payload["operation"] == "analyze" && (int)payload["sample_rate"] == Rate &&
            Convert.FromBase64String((string)payload["pcm16_base64"]).Length == pcm.Length && pcm.Length == 96_000,
            new { bytes = pcm.Length, base64 = ((string)payload["pcm16_base64"]).Length });
        string? Refused(string json)
        {
            try
            {
                SpeakerAnswers.Analysis(JsonDocument.Parse(json).RootElement, 3);
                return null;
            }
            catch (SherpaException error) { return error.Message; }
        }
        var good = SpeakerAnswers.Analysis(JsonDocument.Parse(Answer(Print(0.2f))).RootElement, 3);
        var bad = new Dictionary<string, string?>
        {
            ["255 numbers"] = Refused(Answer(Print(0.2f)[..255])),
            ["not length 1"] = Refused(Answer([.. Print(0.2f).Select(v => v * 2)])),
            ["after the audio"] = Refused(Answer(Print(0.2f)).Replace("\"end\":2.9", "\"end\":9.5")),
            ["fewer voices than speakers"] = Refused(Answer(Print(0.2f), voices: 0)),
            ["an error answer"] = Refused("{\"error\":{\"summary\":\"out of memory\"}}")
        };
        Step("A good answer is read as this PC's analysis; a bad one (255 numbers, not length 1, a time after the audio, fewer voices " +
            "than speakers, an error) is refused", good.Speakers.Count == 1 && good.Speakers[0].Voiceprint.Length == 256 &&
            bad.Values.All(v => v is not null), new { good = new { speakers = good.Speakers.Count, good.Voices, good.SpeechSeconds }, refused = bad });

        // The router with simulated computers and engine.
        var calls = new List<string>();
        var failing = 0;
        using var keeper = new VoiceEngineKeeper<SpeakerAnalysis>(() => new Engine(() => failing > 0 ? failing-- : 0, calls));
        var hosts = new Dictionary<string, Func<CancellationToken, Task<string>>>(StringComparer.Ordinal);
        var router = new VoiceRecognitionRouter<SpeakerAnalysis>(keeper, async (target, _, token) =>
        {
            lock (calls) calls.Add(target.Key);
            var json = await hosts[target.HostId!](token).ConfigureAwait(false);
            try { return SpeakerAnswers.Analysis(JsonDocument.Parse(json).RootElement, samples.Length / (double)Rate); }
            catch (SherpaException error) { throw new InvalidDataException(error.Message); }
        }, new WorkQueue(), Classify) { DefaultHeadStart = TimeSpan.FromMilliseconds(300), Longest = TimeSpan.FromSeconds(2) };
        async Task<Heard> Run(params VoiceRecognitionTarget[] targets)
        {
            lock (calls) calls.Clear();
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var run = await router.RunAsync(targets, samples, cancellation).ConfigureAwait(false);
                return new(run.By.Key, Math.Round(started.Elapsed.TotalMilliseconds), run.Raced, [.. run.Passed], null, [.. calls]);
            }
            catch (VoiceEngineException error) { return new(null, Math.Round(started.Elapsed.TotalMilliseconds), false, [], error.Message, [.. calls]); }
        }
        VoiceRecognitionTarget Host(string id) => new("host:" + id, id, PoolMember.Computer(id));
        var self = new VoiceRecognitionTarget(PoolMember.ThisPcKey, null, PoolMember.ThisPc());
        Task<string> Answers(int ms, CancellationToken token) => Task.Delay(ms, token).ContinueWith(_ => Answer(Print(0.2f)), token,
            TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

        hosts["gpu-pc"] = token => Answers(20, token);
        var fast = await Run(Host("gpu-pc"), self);
        Step("A computer that answers in time recognizes; this PC does nothing", fast.By == "host:gpu-pc" && !fast.Raced &&
            fast.Calls.SequenceEqual(["host:gpu-pc"]), fast);

        hosts["gpu-pc"] = _ => Task.FromException<string>(new Busy());
        var busy = await Run(Host("gpu-pc"), self);
        Step("A busy computer passes it to this PC at once, without waiting", busy.By == "this-pc" && busy.Ms < 250 && !busy.Raced, busy);

        hosts["gpu-pc"] = _ => Task.FromResult(Answer(Print(0.2f)[..255]));
        var invalid = await Run(Host("gpu-pc"), self);
        Step("A bad answer is a failed computer: this PC recognizes, and that computer rests", invalid.By == "this-pc" &&
            router.Resting("gpu-pc"), invalid);

        hosts["m3-host"] = token => Answers(1_500, token);
        var slow = await Run(Host("m3-host"), self);
        Step("A slow computer: after this PC's head start (about as long as this PC takes, at least 200 ms) this PC recognizes too " +
            "and wins; the computer rests", slow.By == "this-pc" && slow.Raced && slow.Ms < 1_200 && router.Resting("m3-host"), slow);
        var rested = await Run(Host("m3-host"), self);
        Step("A resting computer is left out: this PC recognizes at once", rested.By == "this-pc" && !rested.Raced &&
            rested.Calls.SequenceEqual(["this-pc"]) && rested.Passed.Any(p => p.Contains("rests", StringComparison.Ordinal)), rested);

        hosts["kept-pc"] = _ => Task.FromException<string>(new Gone());
        hosts["far-pc"] = token => Answers(10, token);
        var gone = await Run(Host("kept-pc"), Host("far-pc"), self);
        Step("A computer that doesn't answer is passed over for the next computer", gone.By == "host:far-pc", gone);

        failing = 1;
        var reload = await Run(self);
        Step("This PC's engine fails once: Martlet loads it again at once and it works", reload.By == "this-pc" &&
            keeper.Status is { State: VoiceEngineState.Ready, Reloads: 1 }, new { reload, keeper.Status.Reloads });

        failing = 100;
        hosts["after-pc"] = token => Answers(10, token);
        var down = await Run(self, Host("after-pc"));
        var status = keeper.Status;
        Step("This PC's engine keeps failing: it rests (tries again in 5 s), and a computer after this PC in the list recognizes",
            down.By == "host:after-pc" && status.State == VoiceEngineState.Failed && status.RetryAt is { } retry &&
            Math.Abs((retry - DateTimeOffset.UtcNow).TotalSeconds - 5) < 1, new { down, status.Problem, status.Failures, status.RetryAt });
        var resting = await Run(self);
        Step("While it rests, nothing else can recognize: the failure is reported (never silent) and the engine isn't called",
            resting.By is null && resting.Failed is not null && !resting.Calls.Contains("this-pc"), resting);
        failing = 0;
        var back = keeper.Check();
        var after = await Run(self);
        Step("The check loads it again and it works: recognition is back on this PC", back && keeper.Status.State == VoiceEngineState.Ready &&
            after.By == "this-pc", new { back, after });

        object? real = null;
        if (martletDirectory is not null)
        {
            if (!SherpaComponents.VoiceRecognitionIncluded(martletDirectory)) real = new { included = false };
            else
            {
                using var engine = new VoiceEngineKeeper<SpeakerAnalysis>(() => new RealEngine(martletDirectory));
                var load = System.Diagnostics.Stopwatch.StartNew();
                var loaded = engine.Check();
                var loadMs = load.ElapsedMilliseconds;
                var times = new List<long>();
                for (var i = 0; i < 3; i++)
                {
                    var one = System.Diagnostics.Stopwatch.StartNew();
                    engine.Analyze(samples);
                    times.Add(one.ElapsedMilliseconds);
                }
                var realRouter = new VoiceRecognitionRouter<SpeakerAnalysis>(engine, (_, _, _) => throw new InvalidOperationException(), new WorkQueue());
                // The same 3 s through the router with this PC alone (the default list): what the router adds to recognition.
                var routed = new List<long>();
                for (var i = 0; i < 3; i++)
                {
                    var one = System.Diagnostics.Stopwatch.StartNew();
                    await realRouter.RunAsync([new(PoolMember.ThisPcKey, null, PoolMember.ThisPc())], samples, cancellation).ConfigureAwait(false);
                    routed.Add(one.ElapsedMilliseconds);
                }
                real = new
                {
                    included = true, loaded, loadMs, seconds = 3, analyzeMs = times, routedMs = routed,
                    msPerSecond = engine.Status.MillisecondsPerSecond is { } r ? Math.Round(r, 1) : (double?)null,
                    headStartMs = realRouter.HeadStart(samples.Length).TotalMilliseconds
                };
                Step("The real engine on this PC loads and recognizes through the keeper", loaded && engine.Status.State == VoiceEngineState.Ready, real);
            }
        }

        return new { ok, fixture = "simulated computers and a simulated engine (NOT real hosts); the real engine only with martletDirectory", steps, real };
    }

    private sealed class RealEngine(string directory) : IVoiceEngine<SpeakerAnalysis>
    {
        private readonly SpeakerEngine engine = new(directory);
        public SpeakerAnalysis Analyze(float[] samples) => engine.Analyze(samples);
        public void Load() => _ = engine.Voiceprint(new float[Rate]);
        public void Dispose() => engine.Dispose();
    }
}
