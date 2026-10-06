using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Martlet.Mcp;

/// <summary>listening_engine_check: Listening on another computer, headless. Phrases a Windows voice says (rendered to memory,
/// never played, after 0.3 s and before 1 s of faint noise) are transcribed by a live speech-to-text service on loopback (the
/// stt host role's whisper.cpp or Parakeet, default http://127.0.0.1:8178/) through Martlet.NodeLinkCheck's listening-engine
/// mode: the role's production relay inside a real gateway with the desktop's paired client. Scores each transcript's word
/// errors; ok when every phrase came back with at most 20% word errors overall.</summary>
internal static partial class ListeningEngineCheck
{
    [GeneratedRegex(@"\A[a-z0-9][a-z0-9.-]{0,63}\z")]
    private static partial Regex ModelPattern();

    internal static async Task<object> RunAsync(JsonElement arguments, string? endpointText, string? model, CancellationToken cancellation)
    {
        endpointText ??= "http://127.0.0.1:8178/";
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttp ||
            !System.Net.IPAddress.TryParse(endpoint.Host, out var address) || !System.Net.IPAddress.IsLoopback(address))
            throw new ArgumentException("endpoint must be a numeric loopback address such as http://127.0.0.1:8178/.");
        if (model is not null && !ModelPattern().IsMatch(model))
            throw new ArgumentException("model must be a speech-to-text model name such as small or parakeet-tdt-110m-en.");
        var phrases = ParakeetCheck.Strings(arguments, "phrases", ParakeetCheck.MaximumPhrases, 200) ?? ParakeetCheck.Phrases;
        var clips = await Task.Run(() => phrases.Select(text => UtteranceFilterCheck.Synthesize("speech:" + text)).ToArray(), cancellation);
        var folder = Path.Combine(Path.GetTempPath(), "martlet-listening-check-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)));
        Directory.CreateDirectory(folder);
        try
        {
            for (var i = 0; i < clips.Length; i++)
                await File.WriteAllBytesAsync(Path.Combine(folder, $"{i:D2}.pcm"), clips[i], cancellation);
            var (exitCode, report) = await McpServer.NodeLinkCheckReportAsync(TimeSpan.FromMinutes(4), cancellation,
                ["listening-engine", endpoint.GetLeftPart(UriPartial.Authority) + "/", model ?? "-", folder]);
            return Score(exitCode, report, phrases);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The relay's report with each transcript scored against the phrase that was said.</summary>
    internal static object Score(int exitCode, JsonElement report, IReadOnlyList<string> phrases)
    {
        var heard = new List<object>();
        var times = new List<double>();
        int errors = 0, words = 0;
        if (report.TryGetProperty("clips", out var clips) && clips.ValueKind == JsonValueKind.Array)
            foreach (var clip in clips.EnumerateArray())
            {
                var index = int.Parse(clip.GetProperty("clip").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                var text = clip.GetProperty("text").GetString() ?? "";
                var (wrong, total) = ParakeetCheck.WordErrors(phrases[index], text);
                errors += wrong;
                words += total;
                var ms = clip.GetProperty("transcribeMs").GetDouble();
                times.Add(ms);
                heard.Add(new { said = phrases[index], transcript = text, wordErrors = wrong, words = total, transcribeMs = ms,
                    seconds = clip.GetProperty("seconds").GetDouble() });
            }
        times.Sort();
        var rate = words == 0 ? 1 : Math.Round(errors / (double)words, 3);
        var ok = exitCode == 0 && heard.Count == phrases.Count && rate <= ParakeetCheck.MaximumWordErrorRate;
        string? Text(string name) => report.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return new
        {
            ok, maximumWordErrorRate = ParakeetCheck.MaximumWordErrorRate, wordErrorRate = rate,
            medianTranscribeMs = times.Count == 0 ? (double?)null : times[times.Count / 2],
            scene = "each phrase is said by a Windows voice after 0.3 s and before 1 s of faint noise, and goes through the stt " +
                "role's relay in a real gateway on 127.0.0.1 with the desktop's paired client",
            endpoint = Text("endpoint"), route = Text("route"), model = Text("model"), modelRevision = Text("modelRevision"),
            status = report.TryGetProperty("status", out var status) ? status : default(JsonElement?),
            phrases = heard, failure = Text("failure"), problem = Text("problem")
        };
    }
}
