using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Martlet.Sherpa;

namespace Martlet.Mcp;

/// <summary>voice_recognition_engine_check: Voice recognition on another computer, headless. Two of Windows' voices (rendered to
/// memory, never played) speak alone, in turns, over each other and too briefly; a live voice recognition service on loopback
/// (the voice-recognition host role's worker, default http://127.0.0.1:50091/) recognizes them through Martlet.NodeLinkCheck's
/// voice-recognition-engine mode: the role's production relay inside a real gateway with the desktop's paired client and answer
/// checks. With a Martlet folder it also runs this PC's own engine on the same audio and compares the answers.</summary>
internal static class VoiceRecognitionEngineCheck
{
    private const int Rate = 16_000;

    internal static async Task<object> RunAsync(string? endpointText, string? martletDirectory, CancellationToken cancellation)
    {
        endpointText ??= "http://127.0.0.1:50091/";
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttp ||
            !System.Net.IPAddress.TryParse(endpoint.Host, out var address) || !System.Net.IPAddress.IsLoopback(address))
            throw new ArgumentException("endpoint must be a numeric loopback address such as http://127.0.0.1:50091/.");
        var clips = await Task.Run(Clips, cancellation);
        var folder = Path.Combine(Path.GetTempPath(), "martlet-voice-recognition-check-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)));
        Directory.CreateDirectory(folder);
        try
        {
            for (var i = 0; i < clips.Count; i++)
                await File.WriteAllBytesAsync(Path.Combine(folder, $"{i:D2}-{clips[i].Name}.pcm"), clips[i].Pcm, cancellation);
            var (exitCode, report) = await McpServer.NodeLinkCheckReportAsync(TimeSpan.FromMinutes(6), cancellation,
                ["voice-recognition-engine", endpoint.GetLeftPart(UriPartial.Authority) + "/", folder]);
            var comparison = martletDirectory is null ? null : await Task.Run(() => Compare(martletDirectory, clips, report), cancellation);
            string? Text(string name) => report.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return new
            {
                ok = exitCode == 0 && comparison?.Matches != false,
                scene = "two Windows voices (one female, one male) speak alone, in turns, over each other for 2 s and for 1.4 s only; " +
                    "each goes through the voice-recognition role's relay in a real gateway on 127.0.0.1 with the desktop's paired " +
                    "client, which checks every answer as Companion > People's list does",
                endpoint = Text("endpoint"), route = Text("route"), model = Text("model"), modelRevision = Text("modelRevision"),
                state = Text("state"), provider = Text("provider"),
                readyMs = report.TryGetProperty("readyMs", out var ready) && ready.ValueKind == JsonValueKind.Number ? ready.GetDouble() : (double?)null,
                service = report.TryGetProperty("service", out var service) ? service : default(JsonElement?),
                clips = Summaries(report).ToArray(),
                comparedWithThisPc = comparison is null ? null : new
                {
                    matches = comparison.Matches, lowestCosine = comparison.LowestCosine, largestTimeDifference = comparison.LargestTimeDifference,
                    differences = comparison.Differences
                },
                failure = Text("failure"), problem = Text("problem")
            };
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record Comparison(bool Matches, double LowestCosine, double LargestTimeDifference, IReadOnlyList<string> Differences);

    /// <summary>Each clip's answer without its voiceprints: voices, recognized speakers and their times, and the times taken.</summary>
    private static IEnumerable<object> Summaries(JsonElement report)
    {
        if (!report.TryGetProperty("clips", out var clips) || clips.ValueKind != JsonValueKind.Array) yield break;
        foreach (var clip in clips.EnumerateArray())
            yield return new
            {
                clip = clip.GetProperty("clip").GetString(), seconds = clip.GetProperty("seconds").GetDouble(),
                voices = clip.GetProperty("voices").GetInt32(), overlap = clip.GetProperty("overlap").GetBoolean(),
                speechSeconds = clip.GetProperty("speechSeconds").GetDouble(),
                speakers = clip.GetProperty("speakers").EnumerateArray().Select(s => new
                {
                    local = s.GetProperty("local").GetInt32(), cleanSeconds = s.GetProperty("cleanSeconds").GetDouble(),
                    start = s.GetProperty("start").GetDouble(), end = s.GetProperty("end").GetDouble()
                }).ToArray(),
                wholeVoiceprint = clip.GetProperty("whole").ValueKind != JsonValueKind.Null,
                voiceprint = clip.GetProperty("voiceprint").ValueKind != JsonValueKind.Null,
                analyzeMs = clip.GetProperty("analyzeMs").GetDouble(), voiceprintMs = clip.GetProperty("voiceprintMs").GetDouble(),
                hostMs = clip.TryGetProperty("hostMs", out var host) && host.ValueKind == JsonValueKind.Number ? host.GetInt32() : (int?)null
            };
    }

    /// <summary>This PC's engine (in <paramref name="martletDirectory"/>) on the same audio: the same voices, speakers, times within
    /// 0.05 s and voiceprints with a cosine of at least 0.995 (the same on a processor; a graphics card differs a little).</summary>
    private static Comparison Compare(string martletDirectory, IReadOnlyList<(string Name, byte[] Pcm)> clips, JsonElement report)
    {
        if (!SpeakerEngine.Included(martletDirectory))
            return new(false, 0, 0, [$"{martletDirectory} has no voice recognition files"]);
        if (!report.TryGetProperty("clips", out var answers) || answers.ValueKind != JsonValueKind.Array || answers.GetArrayLength() != clips.Count)
            return new(false, 0, 0, ["the role didn't answer every clip"]);
        using var engine = new SpeakerEngine(martletDirectory);
        var differences = new List<string>();
        double lowest = 1, largest = 0;
        var index = 0;
        foreach (var answer in answers.EnumerateArray())
        {
            var (name, pcm) = clips[index++];
            var samples = new float[pcm.Length / 2];
            for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;
            var mine = engine.Analyze(samples);
            var print = engine.Voiceprint(samples);
            void Time(double a, double b) => largest = Math.Max(largest, Math.Abs(a - b));
            if (mine.Voices != answer.GetProperty("voices").GetInt32() || mine.Overlap != answer.GetProperty("overlap").GetBoolean())
                differences.Add($"{name}: voices or overlap differ");
            Time(mine.SpeechSeconds, answer.GetProperty("speechSeconds").GetDouble());
            var speakers = answer.GetProperty("speakers").EnumerateArray().ToArray();
            if (speakers.Length != mine.Speakers.Count) differences.Add($"{name}: {speakers.Length} recognized there, {mine.Speakers.Count} here");
            else
                for (var i = 0; i < speakers.Length; i++)
                {
                    if (speakers[i].GetProperty("local").GetInt32() != mine.Speakers[i].Local) differences.Add($"{name}: speaker order differs");
                    Time(mine.Speakers[i].CleanSeconds, speakers[i].GetProperty("cleanSeconds").GetDouble());
                    Time(mine.Speakers[i].Start, speakers[i].GetProperty("start").GetDouble());
                    Time(mine.Speakers[i].End, speakers[i].GetProperty("end").GetDouble());
                    lowest = Math.Min(lowest, Cosine(mine.Speakers[i].Voiceprint, speakers[i].GetProperty("voiceprint")));
                }
            foreach (var (here, there, what) in new[] { (mine.Whole, answer.GetProperty("whole"), "whole"), (print, answer.GetProperty("voiceprint"), "voiceprint") })
            {
                if ((here is null) != (there.ValueKind == JsonValueKind.Null)) differences.Add($"{name}: {what} is missing on one side");
                else if (here is not null) lowest = Math.Min(lowest, Cosine(here, there));
            }
        }
        if (lowest < 0.995) differences.Add($"a voiceprint pair has cosine {lowest:0.0000}");
        if (largest > 0.05) differences.Add($"a time differs by {largest:0.00} s");
        return new(differences.Count == 0, Math.Round(lowest, 7), Math.Round(largest, 3), differences);
    }

    private static double Cosine(float[] mine, JsonElement theirs)
    {
        if (theirs.GetArrayLength() != mine.Length) return 0;
        double sum = 0;
        var i = 0;
        foreach (var value in theirs.EnumerateArray()) sum += mine[i++] * value.GetDouble();
        return sum;
    }

    // 16 kHz mono PCM16 by two of Windows' voices.
    private static IReadOnlyList<(string Name, byte[] Pcm)> Clips()
    {
        using var probe = new System.Speech.Synthesis.SpeechSynthesizer();
        var voices = probe.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToArray();
        var female = voices.FirstOrDefault(v => v.Gender == System.Speech.Synthesis.VoiceGender.Female)
            ?? throw new InvalidOperationException("No female Windows voice is installed.");
        var male = voices.FirstOrDefault(v => v.Gender == System.Speech.Synthesis.VoiceGender.Male)
            ?? throw new InvalidOperationException("No male Windows voice is installed.");
        var a = Speak(female.Name, "I went to the market this morning and bought some fresh bread, a few apples and a bunch of flowers for the kitchen table.");
        var b = Speak(male.Name, "That sounds lovely. I spent the whole morning fixing the old bicycle in the garage, and now it rides like new again.");
        var over = new short[Math.Max(a.Length, a.Length - 2 * Rate + b.Length)];
        Array.Copy(a, over, a.Length);
        for (var i = 0; i < b.Length; i++)
            over[a.Length - 2 * Rate + i] = (short)Math.Clamp(over[a.Length - 2 * Rate + i] + b[i], short.MinValue, short.MaxValue);
        return
        [
            ("alone", Bytes(a)),
            ("turns", Bytes([.. a, .. new short[Rate / 2], .. b])),
            ("over", Bytes(over)),
            ("short", Bytes(a[..(int)(1.4 * Rate)]))
        ];
    }

    private static short[] Speak(string voice, string text)
    {
        using var stream = new MemoryStream();
        using (var synthesizer = new System.Speech.Synthesis.SpeechSynthesizer())
        {
            synthesizer.SelectVoice(voice);
            synthesizer.SetOutputToAudioStream(stream, new System.Speech.AudioFormat.SpeechAudioFormatInfo(Rate,
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
