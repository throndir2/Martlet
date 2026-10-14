using System.IO;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Speakers;
using Martlet.Desktop;
using Martlet.Sherpa;

namespace Martlet.Desktop.Tests;

/// <summary>Companion › People › Where it runs: the Voice recognition list on this PC (pools.json), recognition on a computer of
/// the list with this PC's processor as the fallback, this PC's engine loaded again after failures, the host's answers checked,
/// and what MCP reads.</summary>
public sealed class VoiceRecognitionPoolTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("martlet-voice-recognition-").FullName;

    public VoiceRecognitionPoolTests()
    {
        HostRegistry.Save(directory, [Paired("gpu-pc", 11), Paired("m3-host", 13)]);
        // No clips are written in the background, so the folder can be deleted at once.
        File.WriteAllText(Path.Combine(directory, VoiceClips.ChoiceFile), "off");
    }

    public void Dispose()
    {
        Directory.Delete(directory, true);
    }

    private static PairedHost Paired(string id, int octet) => new()
    {
        Pairing = new AvatarRemoteHost
        {
            Origin = $"https://192.168.1.{octet}:9443/", HostId = id, SpkiFingerprint = "sha256:" + new string('a', 64),
            DeviceId = "desktop-test", CredentialId = new string('B', 22)
        }
    };

    private void Save(params PoolMember[] members)
    {
        Assert.True(PoolSettings.SaveFor(directory, PoolAreas.VoiceRecognition, new PoolList { Area = PoolAreas.VoiceRecognition.Id, Members = members }));
    }

    private static float[] Print(float lean)
    {
        var vector = Enumerable.Range(0, 256).Select(i => (float)(1 + lean * Math.Sin(i * 0.7))).ToArray();
        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        return [.. vector.Select(v => (float)(v / norm))];
    }

    private static SpeakerAnalysis One(float lean) => new([new HeardSpeaker(0, Print(lean), 2.4, 0.2, 2.8)], 1, false, null, 2.8);

    private sealed class Engine(Func<bool> fails, List<string> calls) : IVoiceEngine<SpeakerAnalysis>
    {
        public SpeakerAnalysis Analyze(float[] samples)
        {
            lock (calls) calls.Add("this-pc");
            return fails() ? throw new SherpaException("The voice model could not be loaded.") : One(0.9f);
        }
        public void Load()
        {
            if (fails()) throw new SherpaException("The voice model could not be loaded.");
        }
        public void Dispose() { }
    }

    private LocalVoices Voices(VoiceRecognitionRouter<SpeakerAnalysis>.Remote remote, List<string> calls, Func<bool>? fails = null) =>
        new(directory, "desk-1", engine: () => new Engine(fails ?? (() => false), calls), remote: (target, samples, token) =>
        {
            lock (calls) calls.Add(target.Key);
            return remote(target, samples, token);
        }, queue: new WorkQueue());

    [Fact]
    public void With_no_list_this_pcs_processor_recognizes_and_the_list_is_tried_in_order()
    {
        Assert.Null(VoiceRecognitionHosts.Load(directory));
        Assert.Equal(["this-pc"], VoiceRecognitionHosts.Targets(directory, "desk-1").Select(t => t.Key));

        Save(PoolMember.Computer("gpu-pc"), PoolMember.Computer("nas-host"), PoolMember.ThisPc(), PoolMember.Computer("m3-host"));
        Assert.Equal(["host:gpu-pc", "this-pc", "host:m3-host"], VoiceRecognitionHosts.Targets(directory, "desk-1").Select(t => t.Key));
    }

    [Fact]
    public void A_computer_in_the_list_recognizes_and_its_voiceprints_join_the_same_voice_list()
    {
        Save(PoolMember.Computer("gpu-pc"), PoolMember.ThisPc());
        var calls = new List<string>();
        using var voices = Voices((_, _, _) => Task.FromResult(One(0.2f)), calls);

        var heard = voices.Recognize(new float[3 * 16_000]);
        Assert.True(heard.Speaker!.Added);
        Assert.Equal(["host:gpu-pc"], calls);
        Assert.Equal("host:gpu-pc", voices.LastRecognition!.Key);
        Assert.Equal("gpu-pc's Voice recognition role", voices.LastRecognition.Where);
        // The same voice heard again on this PC is the same voice: one list, one model.
        var again = voices.Recognize(new float[3 * 16_000]);
        Assert.Equal(VoiceMatchKind.Known, again.Speaker!.Kind);
        Assert.Single(voices.Roster.Live);
    }

    [Fact]
    public void A_computer_that_fails_or_answers_badly_hands_the_utterance_to_this_pc()
    {
        Save(PoolMember.Computer("gpu-pc"), PoolMember.ThisPc() with { Off = true });
        var calls = new List<string>();
        using var voices = Voices((_, _, _) => Task.FromException<SpeakerAnalysis>(
            new Audio2FaceHostException("response.invalid", "gpu-pc: The Voice recognition role's answer was invalid (voiceprint).")), calls);
        var changed = 0;
        voices.RecognitionChanged += () => Interlocked.Increment(ref changed);

        var heard = voices.Recognize(new float[3 * 16_000]);
        Assert.NotNull(heard.Speaker!.Voice);
        Assert.Equal(["host:gpu-pc", "this-pc"], calls);
        Assert.Equal("this-pc", voices.LastRecognition!.Key);
        Assert.Contains(voices.LastRecognition.Passed, p => p.Contains("invalid", StringComparison.Ordinal));
        var host = Assert.Single(voices.RecognitionHosts);
        Assert.NotNull(host.RestUntil);
        Assert.True(changed > 0);
        Assert.Contains("this PC's processor", MainWindow.VoiceRecognitionNow(true, voices.LastRecognition, voices.RecognitionTargets),
            StringComparison.Ordinal);
    }

    [Fact]
    public void This_pcs_engine_failing_is_shown_and_never_silent_and_a_check_brings_it_back()
    {
        var calls = new List<string>();
        var broken = true;
        using var voices = Voices((_, _, _) => throw new InvalidOperationException(), calls, () => broken);

        Assert.Throws<VoiceEngineException>(() => voices.Recognize(new float[3 * 16_000]));
        Assert.Equal(VoiceEngineState.Failed, voices.Engine.State);
        Assert.StartsWith("Voice recognition on this PC stopped working: The voice model could not be loaded.", voices.RecognitionProblem,
            StringComparison.Ordinal);

        broken = false;
        Assert.True(voices.CheckEngine());
        Assert.Null(voices.RecognitionProblem);
        Assert.NotNull(voices.Recognize(new float[3 * 16_000]).Speaker);
    }

    [Fact]
    public void Turning_recognition_off_shows_no_problem()
    {
        using var voices = Voices((_, _, _) => throw new InvalidOperationException(), [], () => true);
        Assert.Throws<VoiceEngineException>(() => voices.Recognize(new float[3 * 16_000]));
        Assert.NotNull(voices.RecognitionProblem);
        voices.SetEnabled(false);
        Assert.Null(voices.RecognitionProblem);
        Assert.Equal("Voice recognition is off, so it runs nowhere.", MainWindow.VoiceRecognitionNow(false, null, voices.RecognitionTargets));
    }

    // ---------- the host's answers ----------

    private static string Answer(float[] print, double end = 2.9, int voices = 1) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["speakers"] = new[] { new Dictionary<string, object> { ["local"] = 0, ["voiceprint"] = print, ["clean_seconds"] = 2.4, ["start"] = 0.5, ["end"] = end } },
        ["voices"] = voices, ["overlap"] = true, ["whole"] = null, ["speech_seconds"] = 2.9, ["milliseconds"] = 12
    });

    private static SpeakerAnalysis Read(string json, double seconds = 3) => SpeakerAnswers.Analysis(JsonDocument.Parse(json).RootElement, seconds);

    [Fact]
    public void A_hosts_answer_is_read_as_this_pcs_analysis_and_a_bad_one_is_refused()
    {
        var print = Print(0.3f);
        var analysis = Read(Answer(print));
        var speaker = Assert.Single(analysis.Speakers);
        Assert.Equal(print, speaker.Voiceprint);
        Assert.Equal(2.4, speaker.CleanSeconds);
        Assert.True(analysis.Overlap);
        Assert.Null(analysis.Whole);

        Assert.Throws<SherpaException>(() => Read(Answer(print[..255])));
        Assert.Throws<SherpaException>(() => Read(Answer([.. print.Select(v => v * 3)])));
        var notNumber = System.Text.Json.Nodes.JsonNode.Parse(Answer(print))!;
        notNumber["speakers"]![0]!["voiceprint"]![7] = "x";
        Assert.Throws<SherpaException>(() => Read(notNumber.ToJsonString()));
        Assert.Throws<SherpaException>(() => Read(Answer(print, end: 9)));
        Assert.Throws<SherpaException>(() => Read(Answer(print, voices: 0)));
        Assert.Throws<SherpaException>(() => Read("{\"error\":{\"summary\":\"out of memory\"}}"));
        Assert.Throws<SherpaException>(() => Read("[1,2]"));

        Assert.Equal(("ready", "cuda", "wespeaker-en-voxceleb-resnet34-lm"), SpeakerAnswers.Status(JsonDocument.Parse(
            "{\"state\":\"ready\",\"provider\":\"cuda\",\"model\":\"wespeaker-en-voxceleb-resnet34-lm\"}").RootElement));
        Assert.Null(SpeakerAnswers.Voiceprint(JsonDocument.Parse("{\"voiceprint\":null,\"milliseconds\":3}").RootElement));
    }

    [Fact]
    public void The_request_carries_the_samples_as_pcm16_exactly()
    {
        var samples = Enumerable.Range(0, 16_000).Select(i => (float)(short)(Math.Sin(i * 0.01) * 20_000) / 32768f).ToArray();
        var pcm = SpeakerAnswers.Pcm16(samples);
        Assert.Equal(samples, Pcm.ToFloats(pcm));
        var payload = Audio2FaceHostConnection.VoiceRecognitionPayload("analyze", pcm);
        Assert.Equal("analyze", payload["operation"]);
        Assert.Equal(16_000, payload["sample_rate"]);
        Assert.Equal(pcm, Convert.FromBase64String((string)payload["pcm16_base64"]));
        Assert.Equal(["operation"], Audio2FaceHostConnection.VoiceRecognitionPayload("status", null).Keys);
        Assert.Throws<ArgumentException>(() => Audio2FaceHostConnection.VoiceRecognitionPayload("analyze", new byte[Audio2FaceHostConnection.VoiceRecognitionMaximumBytes + 2]));
    }

    [Fact]
    public void Mcp_reads_the_list_the_places_and_the_desktops_state()
    {
        Save(PoolMember.Computer("gpu-pc"), PoolMember.Computer("nas-host"));
        var last = new VoiceRecognitionLast("this-pc", "this PC's processor", 180, 3, true, ["gpu-pc didn't answer within 600 ms"], DateTimeOffset.UtcNow);
        VoiceRecognitionHosts.WriteState(directory, true, VoiceEngineStatus.Initial with { State = VoiceEngineState.Ready, MillisecondsPerSecond = 61.24 },
            true, last, [new("gpu-pc", null, null, "didn't answer within 600 ms", DateTimeOffset.UtcNow.AddSeconds(30))],
            VoiceRecognitionHosts.Targets(directory, "desk-1"), null);

        var status = JsonSerializer.SerializeToElement(Martlet.Mcp.VoiceRecognitionCheck.Status(directory, "desk-1"));
        Assert.Equal("saved", status.GetProperty("list").GetString());
        Assert.True(status.GetProperty("shared").GetBoolean());
        Assert.False(status.GetProperty("members")[1].GetProperty("paired").GetBoolean());
        Assert.Equal(["host:gpu-pc", "this-pc"], status.GetProperty("tries").EnumerateArray().Select(e => e.GetProperty("key").GetString()));
        Assert.True(status.GetProperty("tries")[1].GetProperty("fallback").GetBoolean());
        var desktop = status.GetProperty("desktop");
        Assert.Equal("ready", desktop.GetProperty("engine").GetProperty("state").GetString());
        Assert.Equal(61.2, desktop.GetProperty("engine").GetProperty("ms_per_second").GetDouble());
        Assert.Equal("this-pc", desktop.GetProperty("last").GetProperty("key").GetString());
        Assert.Equal("gpu-pc", desktop.GetProperty("hosts")[0].GetProperty("host_id").GetString());
    }
}
