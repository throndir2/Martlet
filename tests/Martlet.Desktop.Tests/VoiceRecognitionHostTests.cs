using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Sherpa;
using Xunit.Abstractions;

namespace Martlet.Desktop.Tests;

/// <summary>The voice-recognition host role (workers/voice-recognition) against this PC's speaker recognition
/// (<see cref="SpeakerEngine"/>): the same pinned voice models, the same clean-speech and rounding steps, and, opt-in, the same
/// answers for the same audio.</summary>
public sealed class VoiceRecognitionHostTests(ITestOutputHelper output)
{
    private static string Repository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "workers", "voice-recognition")))
                return directory.FullName;
        throw new DirectoryNotFoundException("workers/voice-recognition wasn't found above the test output.");
    }

    private static string HostScript => Path.Combine(Repository(), "workers", "voice-recognition", "host", "martlet_voice_recognition_host.py");

    [Fact]
    public void The_hosts_clean_speech_and_rounding_cases_are_this_pcs()
    {
        // workers/voice-recognition/tests/test_host.py checks the host's port against the same file.
        using var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository(), "workers", "voice-recognition", "tests", "clean_cases.json")));
        foreach (var item in cases.RootElement.GetProperty("clean").EnumerateArray())
        {
            var turns = item.GetProperty("turns").EnumerateArray()
                .Select(t => (t[0].GetDouble(), t[1].GetDouble(), t[2].GetInt32())).ToArray();
            var clean = SpeakerEngine.Clean(turns, item.GetProperty("speaker").GetInt32());
            var expected = item.GetProperty("expected").EnumerateArray().Select(e => (e[0].GetDouble(), e[1].GetDouble())).ToArray();
            Assert.True(expected.Length == clean.Count, item.GetProperty("name").GetString());
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i].Item1, clean[i].Start, 9);
                Assert.Equal(expected[i].Item2, clean[i].End, 9);
            }
        }
        foreach (var pair in cases.RootElement.GetProperty("round").EnumerateArray())
            Assert.Equal(pair[1].GetDouble(), Math.Round(pair[0].GetDouble(), 2));
    }

    [Fact]
    public void The_voice_recognition_role_uses_this_pcs_pinned_voice_models()
    {
        var repository = Repository();
        var service = File.ReadAllText(HostScript);
        using var toolchain = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "packaging", "windows", "toolchain.json")));
        var models = toolchain.RootElement.GetProperty("voiceModels").EnumerateArray()
            .Where(m => m.GetProperty("path").GetString()!.StartsWith(@"Desktop\voice-recognition\", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, models.Length);
        foreach (var model in models)
        {
            // ModelFile("pyannote-segmentation-3-0/model.onnx", 5_992_913, "<sha256>", "<url>"): the file, at the same place.
            var path = model.GetProperty("path").GetString()![@"Desktop\voice-recognition\".Length..].Replace('\\', '/');
            var size = model.GetProperty("bytes").GetInt64().ToString("#,0", CultureInfo.InvariantCulture).Replace(',', '_');
            Assert.Contains($"\"{path}\", {size}, \"{model.GetProperty("sha256").GetString()}\",", service);
            Assert.Contains($"\"{model.GetProperty("url").GetString()}\")", service);
        }
        Assert.Contains($"\"{SherpaComponents.SpeakerModel}\"", service);
        Assert.Contains($"\"{SherpaComponents.SegmentationFolder}/model.onnx\"", service);
        // The relay names the voice model by the same SHA-256, and the service uses SpeakerEngine's thresholds.
        var speaker = models.Single(m => m.GetProperty("path").GetString()!.EndsWith(SherpaComponents.SpeakerModel, StringComparison.Ordinal));
        Assert.Contains(speaker.GetProperty("sha256").GetString()!,
            File.ReadAllText(Path.Combine(repository, "src", "Martlet.Gateway.VoiceRecognition", "VoiceRecognitionRelayWorker.cs")));
        foreach (var constant in new[] { $"MINIMUM_CLEAN_SECONDS = {SpeakerEngine.MinimumCleanSeconds:0.0#}",
            $"MINIMUM_WHOLE_SECONDS = {SpeakerEngine.MinimumWholeSeconds:0.0#}", $"MAXIMUM_SECONDS = {SpeakerEngine.MaximumSeconds}",
            "EDGE_TRIM = 0.12", "MINIMUM_FRAGMENT = 1.5", "MAXIMUM_FRAGMENT = 8.0", "CONSISTENCY = 0.45", "CLUSTER_THRESHOLD = 0.5" })
            Assert.Contains(constant, service);
    }

    [Fact]
    public void The_role_can_be_installed_and_is_named_where_hosts_roles_are()
    {
        var role = HostRoles.Get(HostRoles.VoiceRecognition);
        Assert.Equal(("Voice recognition", "martlet.gateway.voice-recognition.v1"), (role.Name, role.RouteId));
        Assert.Same(role, HostRoles.ForRoute("martlet.gateway.voice-recognition.v1"));
        var conf = File.ReadAllLines(Path.Combine(Repository(), "deploy", "host", "roles", HostRoles.VoiceRecognition, "role.conf"));
        Assert.Contains("gateway_kind=voice-recognition", conf);
        Assert.Contains("port=50091", conf);
        Assert.Contains("gpu=optional|compose.gpu.yaml", conf);
        Assert.Contains("model_from=VOICE_RECOGNITION_MODEL", conf);
        Assert.Equal("wespeaker-en-voxceleb-resnet34-lm",
            conf.Single(l => l.StartsWith("choice=VOICE_RECOGNITION_MODEL|", StringComparison.Ordinal)).Split('|')[3]);

        // A host that serves the route is in the cluster plan's node roles with its model.
        var route = new HostRoute(role.RouteId, "/martlet/v1/inference/voice-recognition", "martlet.voice-recognition-relay", "1.0",
            "voice-recognition-host", "voice-recognition-relay", "1.0.0", "wespeaker-en-voxceleb-resnet34-lm", "speaker-recongition-models",
            new string('0', 64), "sha256:" + new string('0', 64), 1, 1, 1, 1, 2, 1, TimeSpan.FromSeconds(60), "none");
        var node = Assert.Single(ClusterSync.Roles([route]));
        Assert.Equal((HostRoles.VoiceRecognition, "wespeaker-en-voxceleb-resnet34-lm"), (node.Kind, node.Model));
        Assert.Contains((HostRoles.VoiceRecognition, "Voice recognition"), Martlet.Core.Installation.SharedGpu.OtherGpuRoles);
        Assert.Equal("voice-recognition", Martlet.Core.Platforms.PlatformCatalog.EngineForHostRole(HostRoles.VoiceRecognition));
        var pi = Martlet.Core.Platforms.PlatformCatalog.Check("voice-recognition", Martlet.Core.Platforms.PlatformSide.Host,
            new Martlet.Core.Platforms.PlatformDevice { Platform = Martlet.Core.Platforms.DevicePlatform.Linux, Name = "pi", Arm64 = true });
        Assert.Equal(Martlet.Core.Platforms.PlatformVerdict.Yes, pi.Verdict);
    }

    /// <summary>Opt-in: the host's port gives this PC's answers for the same speech. MARTLET_VOICE_RECOGNITION_PYTHON names a
    /// Python with sherpa-onnx 1.13.8 (it runs the port here on this PC's own model files); MARTLET_VOICE_RECOGNITION_IMAGE names
    /// the built role image (Docker runs the port in it, on the image's baked models; MARTLET_VOICE_RECOGNITION_GPU=1 runs it on
    /// the NVIDIA graphics card). The speech is Windows' own voices (System.Speech), two of them.</summary>
    [Fact]
    public void The_hosts_port_answers_like_this_pc()
    {
        var python = Environment.GetEnvironmentVariable("MARTLET_VOICE_RECOGNITION_PYTHON");
        var image = Environment.GetEnvironmentVariable("MARTLET_VOICE_RECOGNITION_IMAGE");
        if (python is null && image is null) return;
        Assert.True(SpeakerEngine.Included(), "The voice models and runtime are not beside the tests.");
        var gpu = Environment.GetEnvironmentVariable("MARTLET_VOICE_RECOGNITION_GPU") == "1";
        var scenarios = Scenarios();
        var folder = Directory.CreateTempSubdirectory("martlet-voices-parity-");
        try
        {
            foreach (var (name, pcm) in scenarios) File.WriteAllBytes(Path.Combine(folder.FullName, name + ".pcm"), pcm);
            using var engine = new SpeakerEngine();
            var desktop = scenarios.ToDictionary(s => s.Name, s =>
            {
                var samples = Pcm.ToFloats(s.Pcm);
                var watch = Stopwatch.StartNew();
                var analysis = engine.Analyze(samples);
                output.WriteLine($"This PC, {s.Name}: analyzed in {watch.ElapsedMilliseconds} ms");
                return (Analysis: analysis, Voiceprint: engine.Voiceprint(samples));
            });
            if (python is not null)
                Compare("Python on this PC", desktop, Run(python, [HostScript, "analyze-files", "--models", SherpaComponents.VoiceModelsDirectory(),
                    .. scenarios.Select(s => Path.Combine(folder.FullName, s.Name + ".pcm"))]), cuda: false);
            if (image is not null)
                Compare(gpu ? "the image on the graphics card" : "the image on the processor", desktop, Run("docker",
                    ["run", "--rm", "-v", folder.FullName + ":/data:ro", .. gpu ? new[] { "--gpus", "all" } : [], image, "analyze-files",
                        "--provider", gpu ? "cuda" : "cpu", .. scenarios.Select(s => "/data/" + s.Name + ".pcm")]), cuda: gpu);
        }
        finally { folder.Delete(true); }
    }

    private void Compare(string where, Dictionary<string, (SpeakerAnalysis Analysis, float[]? Voiceprint)> desktop,
        IReadOnlyList<JsonElement> host, bool cuda)
    {
        // On the processor the same models and ONNX Runtime give the same answers (the Windows wheel to the last bit, the Linux
        // images within 0.000002 a number); on CUDA the voiceprints differ a little more.
        var least = cuda ? 0.995 : 0.9999;
        var times = cuda ? 0.05 : 0.0;
        Assert.Equal(desktop.Count, host.Count);
        var lowest = 1.0;
        var largest = 0.0;
        var index = 0;
        foreach (var (name, (analysis, voiceprint)) in desktop)
        {
            var answer = host[index++];
            Assert.True(analysis.Voices == answer.GetProperty("voices").GetInt32(), $"{where}, {name}: voices");
            Assert.True(analysis.Overlap == answer.GetProperty("overlap").GetBoolean(), $"{where}, {name}: overlap");
            Assert.InRange(answer.GetProperty("speech_seconds").GetDouble(), analysis.SpeechSeconds - times, analysis.SpeechSeconds + times);
            var speakers = answer.GetProperty("speakers").EnumerateArray().ToArray();
            Assert.True(analysis.Speakers.Count == speakers.Length, $"{where}, {name}: speakers");
            for (var i = 0; i < speakers.Length; i++)
            {
                var (mine, theirs) = (analysis.Speakers[i], speakers[i]);
                Assert.Equal(mine.Local, theirs.GetProperty("local").GetInt32());
                Assert.InRange(theirs.GetProperty("clean_seconds").GetDouble(), mine.CleanSeconds - times, mine.CleanSeconds + times);
                Assert.InRange(theirs.GetProperty("start").GetDouble(), mine.Start - times, mine.Start + times);
                Assert.InRange(theirs.GetProperty("end").GetDouble(), mine.End - times, mine.End + times);
                lowest = Math.Min(lowest, Cosine(mine.Voiceprint, theirs.GetProperty("voiceprint"), ref largest));
            }
            foreach (var (mine, theirs) in new[] { (analysis.Whole, answer.GetProperty("whole")), (voiceprint, answer.GetProperty("voiceprint")) })
            {
                Assert.True((mine is null) == (theirs.ValueKind == JsonValueKind.Null), $"{where}, {name}: a voiceprint is missing");
                if (mine is not null) lowest = Math.Min(lowest, Cosine(mine, theirs, ref largest));
            }
            output.WriteLine($"{where}, {name}: {analysis.Voices} voices, {analysis.Speakers.Count} recognized, overlap {analysis.Overlap}, " +
                $"{analysis.SpeechSeconds} s of speech; host took {answer.GetProperty("milliseconds").GetInt32()} ms");
        }
        output.WriteLine($"{where}: the least similar voiceprint pair has cosine {lowest:0.0000000}; the largest difference of one " +
            $"voiceprint number is {largest:0.#########}.");
        Assert.InRange(lowest, least, 1.0001);
    }

    private static double Cosine(float[] mine, JsonElement theirs, ref double largest)
    {
        Assert.Equal(mine.Length, theirs.GetArrayLength());
        double sum = 0;
        var i = 0;
        foreach (var value in theirs.EnumerateArray())
        {
            largest = Math.Max(largest, Math.Abs(mine[i] - value.GetSingle()));
            sum += mine[i++] * value.GetDouble();
        }
        return sum;
    }

    private static IReadOnlyList<JsonElement> Run(string program, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var lines = new List<JsonElement>();
        while (process.StandardOutput.ReadLine() is { } line)
            if (line.StartsWith('{')) lines.Add(JsonDocument.Parse(line).RootElement.Clone());
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(10)), "The host's port didn't finish.");
        Assert.True(process.ExitCode == 0, $"{program} failed: {error.Result}");
        return lines;
    }

    // 16 kHz mono PCM16 speech by two of Windows' voices: alone, taking turns, talking over each other, coming back, and too short.
    private static List<(string Name, byte[] Pcm)> Scenarios()
    {
        var voices = Voices();
        var a1 = Speak(voices.First, "I went to the market this morning and bought some fresh bread, a few apples and a bunch of flowers for the kitchen table.");
        var a2 = Speak(voices.First, "Then I walked home along the river, where the ducks were waiting for the children to bring them something to eat.");
        var b1 = Speak(voices.Second, "That sounds lovely. I spent the whole morning fixing the old bicycle in the garage, and now it rides like new again.");
        static short[] Silence(double seconds) => new short[(int)(seconds * 16000)];
        static short[] Mix(short[] first, short[] second, int at)
        {
            var mixed = new short[Math.Max(first.Length, at + second.Length)];
            Array.Copy(first, mixed, first.Length);
            for (var i = 0; i < second.Length; i++) mixed[at + i] = (short)Math.Clamp(mixed[at + i] + second[i], short.MinValue, short.MaxValue);
            return mixed;
        }
        return
        [
            ("alone", Bytes(a1)),
            ("turns", Bytes([.. a1, .. Silence(0.5), .. b1])),
            ("over", Bytes(Mix(a1, b1, a1.Length - 2 * 16000))),
            ("back", Bytes([.. a2, .. Silence(0.3), .. b1, .. Silence(0.3), .. a1])),
            ("short", Bytes(a1[..(int)(1.4 * 16000)])),
            ("tiny", Bytes(a1[..(int)(0.6 * 16000)]))
        ];
    }

    private static (string First, string Second) Voices()
    {
        using var synthesizer = new System.Speech.Synthesis.SpeechSynthesizer();
        var installed = synthesizer.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToArray();
        var female = installed.FirstOrDefault(v => v.Gender == System.Speech.Synthesis.VoiceGender.Female);
        var male = installed.FirstOrDefault(v => v.Gender == System.Speech.Synthesis.VoiceGender.Male);
        Assert.True(female is not null && male is not null, "Two of Windows' voices (one female, one male) are needed.");
        return (female!.Name, male!.Name);
    }

    private static short[] Speak(string voice, string text)
    {
        using var stream = new MemoryStream();
        using (var synthesizer = new System.Speech.Synthesis.SpeechSynthesizer())
        {
            synthesizer.SelectVoice(voice);
            synthesizer.SetOutputToAudioStream(stream, new System.Speech.AudioFormat.SpeechAudioFormatInfo(16000,
                System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
            synthesizer.Speak(text);
        }
        var bytes = stream.ToArray();
        var samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
        return samples;
    }

    private static byte[] Bytes(short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
