using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Martlet.Core.Settings;
using Martlet.Diagnostics;

namespace Martlet.Mcp;

/// <summary>The desktop log's reply latency lines (one per reply: how long from when the user stopped talking, or sent their
/// message, to the first audio, step by step) parsed into numbers, with the median and 90th percentile of the total and of each
/// step over the newest replies, so a slow stage and the effect of a change stand out. Older lines that only gave the time to
/// the first words and audio from the reply's start are read too (<c>legacy</c>). Read-only: never writes or rotates a log and
/// starts no audio, network or provider request.</summary>
internal static partial class LatencyReport
{
    internal const int DefaultReplies = 20;
    internal const int MaximumReplies = 500;
    private const string Prefix = "Reply latency: ";

    [GeneratedRegex(@"^Reply latency: (?<what>first audio|first words) (?<total>\d+) ms after (?<from>[^(]+?) \((?<steps>[^)]*)\)\. (?<rest>.*)$")]
    private static partial Regex Line();
    [GeneratedRegex(@"^(?<name>.*\S) (?<ms>\d+)$")]
    private static partial Regex Step();
    [GeneratedRegex(@"First words after (?<words>\d+|-) ms(?: and first audio after (?<audio>\d+) ms)? from the reply's start, (?<pieces>\d+) spoken pieces?")]
    private static partial Regex FromStart();
    [GeneratedRegex(@"^Reply latency: first words after (?<words>\d+) ms, first audio after (?<audio>\d+) ms, (?<pieces>\d+) spoken pieces?")]
    private static partial Regex Legacy();
    [GeneratedRegex(@"First piece: (?<speech>[\d.]+) s of speech made in (?<made>\d+) ms")]
    private static partial Regex FirstPiece();
    [GeneratedRegex(@"The voice paused (?<pauses>\d+) times? for (?<ms>\d+) ms in all")]
    private static partial Regex VoicePauses();
    [GeneratedRegex(@", paused (?<ms>\d+) ms(?:, then stopped| when you talked over it, then resumed)")]
    private static partial Regex PausedForYou();
    [GeneratedRegex(@"Models: (?<models>.*)\.$")]
    private static partial Regex Models();
    // What the live floor did to background work for the turn ("held 2 pool jobs, stopped 1 (think longer)").
    [GeneratedRegex(@"Live floor: (?<floor>.*?)\.(?: Models: |$)")]
    private static partial Regex Floor();

    internal sealed record Reply(DateTimeOffset At, string Measured, double? TotalMs, string? From, IReadOnlyDictionary<string, double> Steps,
        double? FirstWordsMs, double? FirstAudioMs, int? SpokenPieces, double? FirstPieceSpeechSeconds, double? FirstPieceMadeMs,
        string? Models, bool Interrupted, bool Legacy, bool Restarted = false, int VoicePauses = 0, double VoicePausedMs = 0,
        double? PausedForYouMs = null, bool Resumed = false, string? Floor = null);

    internal static object Read(string? dataDirectory, int? replies)
    {
        var count = replies ?? DefaultReplies;
        if (count is < 1 or > MaximumReplies) throw new ArgumentException($"replies must be 1-{MaximumReplies}.");
        var root = dataDirectory ?? SettingsStore.DefaultDataDirectory();
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("dataDirectory must be an absolute path.");
        var directory = LocalLogs.Directory(root);
        var records = Directory.Exists(directory) ? LocalLogs.Read(directory, LocalLogs.ThisDeviceId()) : [];
        var parsed = records
            .Where(r => r.Component == "desktop" && r.Message.StartsWith(Prefix, StringComparison.Ordinal))
            .OrderBy(r => r.At)
            .Select(r => Parse(r.At, r.Message))
            .OfType<Reply>()
            .TakeLast(count)
            .ToArray();
        return Summarize(parsed, Directory.Exists(directory));
    }

    internal static object Summarize(IReadOnlyList<Reply> parsed, bool logsFolder = true)
    {
        var current = parsed.Where(r => !r.Legacy && r.TotalMs is not null).ToArray();
        var stepNames = current.SelectMany(r => r.Steps.Keys).Distinct(StringComparer.Ordinal).ToArray();
        var steps = stepNames.ToDictionary(name => name, name => Stats(current.Where(r => r.Steps.ContainsKey(name)).Select(r => r.Steps[name])));
        var spoken = current.Where(r => r.Measured == "first audio").ToArray();
        return new
        {
            logsFolder,
            replies = parsed.Count,
            measured = current.Length,
            legacy = parsed.Count(r => r.Legacy),
            firstAudio = Stats(spoken.Select(r => r.TotalMs!.Value)),
            firstWordsFromReplyStart = Stats(parsed.Where(r => r.FirstWordsMs is not null).Select(r => r.FirstWordsMs!.Value)),
            firstAudioFromReplyStart = Stats(parsed.Where(r => r.FirstAudioMs is not null).Select(r => r.FirstAudioMs!.Value)),
            slowestSteps = steps.OrderByDescending(s => s.Value.Median).Take(5).Select(s => new { step = s.Key, medianMs = s.Value.Median }).ToArray(),
            steps,
            // Replies whose speakers ran dry mid-reply waiting for a voice made slower than real time, and those waits.
            voicePauses = new
            {
                replies = parsed.Count(r => r.VoicePauses > 0),
                pauses = parsed.Sum(r => r.VoicePauses),
                pausedMs = Stats(parsed.Where(r => r.VoicePauses > 0).Select(r => r.VoicePausedMs))
            },
            // Replies paused because you talked over them (Pause and decide), how many played on, and how long they paused.
            pausedForYou = new
            {
                replies = parsed.Count(r => r.PausedForYouMs is not null),
                resumed = parsed.Count(r => r.Resumed),
                stopped = parsed.Count(r => r.PausedForYouMs is not null && r.Interrupted),
                pausedMs = Stats(parsed.Where(r => r.PausedForYouMs is not null).Select(r => r.PausedForYouMs!.Value))
            },
            newest = parsed.Reverse().Select(r => new
            {
                at = r.At, measured = r.Measured, totalMs = r.TotalMs, from = r.From, steps = r.Steps, firstWordsMs = r.FirstWordsMs,
                firstAudioMs = r.FirstAudioMs, spokenPieces = r.SpokenPieces, firstPieceSpeechSeconds = r.FirstPieceSpeechSeconds,
                firstPieceMadeMs = r.FirstPieceMadeMs, voicePauses = r.VoicePauses, voicePausedMs = r.VoicePausedMs, models = r.Models,
                interrupted = r.Interrupted, restarted = r.Restarted, pausedForYouMs = r.PausedForYouMs, resumed = r.Resumed,
                liveFloor = r.Floor, legacy = r.Legacy
            }).ToArray()
        };
    }

    internal sealed record StepStats(int Count, double Median, double P90, double Min, double Max);

    private static StepStats Stats(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return new(0, 0, 0, 0, 0);
        return new(sorted.Length, Percentile(sorted, 0.5), Percentile(sorted, 0.9), sorted[0], sorted[^1]);
    }

    private static double Percentile(double[] sorted, double p)
    {
        var position = (sorted.Length - 1) * p;
        var low = (int)Math.Floor(position);
        var high = (int)Math.Ceiling(position);
        return Math.Round(sorted[low] + (sorted[high] - sorted[low]) * (position - low), 1);
    }

    internal static Reply? Parse(DateTimeOffset at, string message)
    {
        var interrupted = message.Contains("stopped when you talked over it", StringComparison.Ordinal);
        if (Legacy().Match(message) is { Success: true } old)
            return new(at, "first audio", null, null, new Dictionary<string, double>(), Number(old.Groups["words"].Value),
                Number(old.Groups["audio"].Value), (int?)Number(old.Groups["pieces"].Value), null, null, null, interrupted, true);
        if (Line().Match(message) is not { Success: true } line) return null;
        var steps = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var part in line.Groups["steps"].Value.Split(", ", StringSplitOptions.RemoveEmptyEntries))
            if (Step().Match(part.Trim()) is { Success: true } step)
                steps[step.Groups["name"].Value] = steps.GetValueOrDefault(step.Groups["name"].Value) + Number(step.Groups["ms"].Value)!.Value;
        var rest = line.Groups["rest"].Value;
        var start = FromStart().Match(rest);
        var piece = FirstPiece().Match(rest);
        var pauses = VoicePauses().Match(rest);
        var models = Models().Match(rest);
        var held = PausedForYou().Match(rest);
        var floor = Floor().Match(rest);
        return new(at, line.Groups["what"].Value, Number(line.Groups["total"].Value), line.Groups["from"].Value.Trim(), steps,
            start.Success ? Number(start.Groups["words"].Value) : null,
            start.Success && start.Groups["audio"].Success ? Number(start.Groups["audio"].Value) : null,
            start.Success ? (int?)Number(start.Groups["pieces"].Value) : null,
            piece.Success ? Number(piece.Groups["speech"].Value) : null, piece.Success ? Number(piece.Groups["made"].Value) : null,
            models.Success ? models.Groups["models"].Value : null, interrupted, false,
            rest.Contains("replaced because you kept talking", StringComparison.Ordinal),
            pauses.Success ? (int)Number(pauses.Groups["pauses"].Value)!.Value : 0,
            pauses.Success ? Number(pauses.Groups["ms"].Value)!.Value : 0,
            held.Success ? Number(held.Groups["ms"].Value) : null, held.Success && held.Value.EndsWith("resumed", StringComparison.Ordinal),
            floor.Success ? floor.Groups["floor"].Value : null);
    }

    private static double? Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}
