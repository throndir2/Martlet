using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Martlet.Core.Settings;
using Martlet.Sherpa;

namespace Martlet.Mcp;

/// <summary>Companion › Listening › Parakeet in Martlet, headless: which Parakeet models a data directory's speech folder has,
/// which one Listening uses and which Martlet recommends (voices_status), and parakeet_check, which loads each downloaded model
/// through the production <see cref="ParakeetEngine"/> and transcribes phrases a Windows voice says. Nothing is downloaded,
/// recorded or played, and nothing leaves this PC.</summary>
internal static class ParakeetCheck
{
    /// <summary>The highest word error rate on the clean synthesized phrases that still passes.</summary>
    internal const double MaximumWordErrorRate = 0.2;
    private const int MaximumPhrases = 8;

    // English phrases with no numbers or names (the English-only models write numbers as digits and don't know Martlet's name).
    private static readonly string[] Phrases =
    [
        "Hey, what's the weather like tomorrow morning?",
        "Can you remind me to call my sister after lunch?",
        "Stop, wait, hold on a second.",
        "I think the second option sounds better, so let's go with that one."
    ];

    /// <summary>Listening's route in a data directory's settings.json (null when there is none or it can't be read).</summary>
    internal static SetupRoute? Listening(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "settings.json");
            return File.Exists(path) ? SettingsJson.Read(File.ReadAllBytes(path)).Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Stt) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or Martlet.Core.Contracts.ContractException)
        {
            return null;
        }
    }

    /// <summary>The Parakeet model a check runs: <paramref name="requested"/>, else Listening's model in the data directory when it
    /// is downloaded, else v3 (the first one Martlet offered), else the first one downloaded; null when none is.</summary>
    internal static ParakeetModel? ModelFor(string? requested, string dataDirectory, string speechDirectory)
    {
        if (requested is not null) return Known(requested);
        var installed = SherpaComponents.InstalledParakeetModels(speechDirectory);
        if (Listening(dataDirectory) is { RouteType: SetupRouteType.LocalParakeet } route && installed.FirstOrDefault(m => m.Id == route.ModelId) is { } chosen)
            return chosen;
        return installed.Contains(ParakeetModels.V3) ? ParakeetModels.V3 : installed.FirstOrDefault();
    }

    private static ParakeetModel Known(string id) => ParakeetModels.Find(id) ??
        throw new ArgumentException($"'{id}' isn't a Parakeet model Martlet knows: {string.Join(", ", ParakeetModels.All.Select(m => m.Id))}.");

    /// <summary>voices_status's Parakeet part: the models, which are downloaded in <paramref name="speechDirectory"/>, the one
    /// Listening uses (with the route type) and the one recommended for Windows' display language.</summary>
    internal static object Status(string dataDirectory, string speechDirectory)
    {
        var route = Listening(dataDirectory);
        var chosen = route?.RouteType == SetupRouteType.LocalParakeet ? route.ModelId : null;
        var recommended = LocalSpeechSetup.RecommendedParakeetModel(CultureInfo.CurrentUICulture);
        return new
        {
            listening = route is null ? null : new
            {
                route = (route.RouteType ?? SetupRouteType.OpenAi).ToString(),
                parakeetModel = chosen,
                known = chosen is null ? (bool?)null : ParakeetModels.Find(chosen) is not null,
                downloaded = chosen is null ? (bool?)null : SherpaComponents.IsParakeetInstalled(speechDirectory, chosen)
            },
            displayLanguage = CultureInfo.CurrentUICulture.Name,
            recommended,
            models = ParakeetModels.All.Select(model => new
            {
                id = model.Id, name = model.Name, languages = model.Languages, englishOnly = model.EnglishOnly,
                downloadMb = Math.Round(model.DownloadBytes / 1_000_000.0), revision = model.Revision,
                downloaded = SherpaComponents.IsParakeetInstalled(speechDirectory, model),
                notice = File.Exists(Path.Combine(speechDirectory, "models", model.NoticeFile)),
                recommended = model.Id == recommended, inUse = model.Id == chosen
            })
        };
    }

    /// <summary>parakeet_check: each downloaded model (or the ones named) loaded through the production engine with the runtime in
    /// <paramref name="martletDirectory"/>, transcribing phrases a Windows voice says (rendered to memory, never played) after 0.3 s
    /// and before 1 s of faint noise.</summary>
    internal static async Task<object> RunAsync(JsonElement arguments, string dataDirectory, string martletDirectory, string speechDirectory,
        CancellationToken cancellation)
    {
        var named = Strings(arguments, "models", ParakeetModels.All.Count, 64);
        var phrases = Strings(arguments, "phrases", MaximumPhrases, 200) ?? Phrases;
        var models = named?.Select(Known).Distinct().ToArray() ?? [.. SherpaComponents.InstalledParakeetModels(speechDirectory)];
        var status = Status(dataDirectory, speechDirectory);
        var runtime = SherpaComponents.RuntimeDirectory(martletDirectory);
        if (runtime is null)
            return new { ran = false, ok = false, reason = "the sherpa-onnx runtime isn't in martletDirectory (build Martlet.Desktop)", status };
        if (models.Length == 0)
            return new { ran = false, ok = false, reason = "no Parakeet model is downloaded in speechDirectory (Companion > Listening > Parakeet in Martlet)", status };

        var clips = await Task.Run(() => phrases.Select(text => (Text: text, Pcm: UtteranceFilterCheck.Synthesize("speech:" + text))).ToArray(), cancellation);
        var results = new List<object>();
        var ok = true;
        foreach (var model in models)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!SherpaComponents.IsParakeetInstalled(speechDirectory, model))
            {
                ok = false;
                results.Add(new { model = model.Id, name = model.Name, ran = false, ok = false, reason = "not downloaded in speechDirectory" });
                continue;
            }
            results.Add(await RunModelAsync(model, speechDirectory, runtime, clips, cancellation, passed => ok &= passed));
        }
        return new
        {
            ran = true, ok, maximumWordErrorRate = MaximumWordErrorRate, threads = Math.Clamp(Environment.ProcessorCount / 4, 2, 4),
            scene = "each phrase is said by a Windows voice after 0.3 s and before 1 s of faint noise", status, models = results
        };
    }

    private static async Task<object> RunModelAsync(ParakeetModel model, string speechDirectory, string runtime, (string Text, byte[] Pcm)[] clips,
        CancellationToken cancellation, Action<bool> passed)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = PrivateBytes();
        var load = Stopwatch.StartNew();
        using var engine = new ParakeetEngine(speechDirectory, model.Id, runtimeDirectory: runtime);
        try { await Task.Run(engine.Warm, cancellation); }
        catch (SherpaException error)
        {
            passed(false);
            return new { model = model.Id, name = model.Name, ran = false, ok = false, reason = error.Message };
        }
        var loadMs = load.ElapsedMilliseconds;
        var heard = new List<object>();
        var times = new List<double>();
        int errors = 0, words = 0;
        foreach (var (text, pcm) in clips)
        {
            var samples = UtteranceFilterCheck.ToFloats(pcm, 0, pcm.Length);
            var watch = Stopwatch.StartNew();
            var transcript = await Task.Run(() => engine.Transcribe(samples), cancellation);
            var ms = Math.Round(watch.Elapsed.TotalMilliseconds, 1);
            var (wrong, total) = WordErrors(text, transcript.Text);
            errors += wrong;
            words += total;
            times.Add(ms);
            heard.Add(new
            {
                said = text, transcript = transcript.Text, wordErrors = wrong, words = total, transcribeMs = ms,
                seconds = Math.Round(pcm.Length / 2.0 / ParakeetEngine.SampleRate, 2), meanProbability = transcript.Confidence,
                language = transcript.Language
            });
        }
        var memoryMb = Math.Round((PrivateBytes() - before) / 1_000_000.0);
        var rate = words == 0 ? 0 : Math.Round(errors / (double)words, 3);
        var ok = rate <= MaximumWordErrorRate;
        passed(ok);
        times.Sort();
        return new
        {
            model = model.Id, name = model.Name, languages = model.Languages, ran = true, ok, loadMs, memoryMb, wordErrorRate = rate,
            medianTranscribeMs = times[times.Count / 2], phrases = heard
        };
    }

    private static long PrivateBytes()
    {
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    /// <summary>Word errors (substitutions, insertions and deletions) of <paramref name="heard"/> against <paramref name="said"/>,
    /// ignoring case and punctuation, and the number of words said.</summary>
    internal static (int Errors, int Words) WordErrors(string said, string heard)
    {
        var reference = Words(said);
        var hypothesis = Words(heard);
        var row = Enumerable.Range(0, hypothesis.Length + 1).ToArray();
        for (var i = 1; i <= reference.Length; i++)
        {
            var next = new int[hypothesis.Length + 1];
            next[0] = i;
            for (var j = 1; j <= hypothesis.Length; j++)
                next[j] = Math.Min(Math.Min(next[j - 1] + 1, row[j] + 1), row[j - 1] + (reference[i - 1] == hypothesis[j - 1] ? 0 : 1));
            row = next;
        }
        return (row[hypothesis.Length], reference.Length);
    }

    private static string[] Words(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant()) builder.Append(char.IsLetterOrDigit(c) || c == '\'' ? c : ' ');
        return builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string[]? Strings(JsonElement arguments, string property, int maximum, int maximumLength)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is 0 || value.GetArrayLength() > maximum)
            throw new ArgumentException($"'{property}' must be an array of 1 to {maximum} strings.");
        return [.. value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text &&
            text.Length <= maximumLength ? text : throw new ArgumentException($"Each of '{property}' must be a string of 1 to {maximumLength} characters."))];
    }
}
