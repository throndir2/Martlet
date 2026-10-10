using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Martlet.Core.Settings;
using Martlet.Diagnostics;

namespace Martlet.Mcp;

/// <summary>The desktop log's Thinking trace (docs/VOICE_LATENCY.md, Thinking trace) put back together: the newest conversation
/// turns (a reply, a glance, a background think, a Thinking pool job...), each with its lines in order (started, prepared, each
/// request, tool rounds, Backup Thinking, still waiting, ended) and what they say in numbers, and the newest requests for the
/// Thinking pool's slots with theirs (waits in line, started on a member, let go, waits, ended). Turn numbers and request IDs
/// start again with each run of Martlet, so a "started" or "waits in line" line begins a new one. Read-only: never writes or
/// rotates a log and starts no audio, network or provider request.</summary>
internal static partial class ThinkingTraceReport
{
    internal const int DefaultTurns = 10;
    internal const int MaximumTurns = 200;

    // A purpose may hold parentheses itself ("image sense job on diva (llava)"): the name ends at the ")" before the line's rest.
    [GeneratedRegex(@"^(?<name>Thinking turn (?<number>\d+) \((?<purpose>.*?)\))(?<rest>(?: started: | ended at |: ).*)$")]
    private static partial Regex Turn();
    [GeneratedRegex(@"^Thinking request (?<id>tr-\d+) \((?<kind>[^,]+), (?<holder>[^)]+)\): (?<rest>.*)$")]
    private static partial Regex Request();
    [GeneratedRegex(@"^ ended at (?<ms>\d+) ms: (?<state>\w+)")]
    private static partial Regex Ended();
    [GeneratedRegex(@"first words at (?<ms>\d+) ms")]
    private static partial Regex FirstWords();
    [GeneratedRegex(@"first audio at (?<ms>\d+) ms")]
    private static partial Regex FirstAudio();
    [GeneratedRegex(@"^ started: (?<route>.*?), model (?<model>[^;]+);")]
    private static partial Regex Started();
    [GeneratedRegex(@"^: still waiting at (?<at>\d+) ms(?: for (?<what>.*?) \((?<ms>\d+) ms so far|: no new words from request \d+ for (?<idle>\d+) ms)")]
    private static partial Regex Waiting();
    [GeneratedRegex(@"^(?<state>Succeeded|Failed|TimedOut|Stale|NoMember|Preempted|Canceled) after (?<total>\d+) ms: waited (?<waited>\d+) ms, ran (?<ran>\d+) ms")]
    private static partial Regex RequestEnded();

    private sealed class TurnTrace(string name, string purpose, DateTimeOffset at)
    {
        internal string Name { get; } = name;
        internal string Purpose { get; } = purpose;
        internal DateTimeOffset At { get; } = at;
        internal bool HasStart { get; set; }
        internal List<(DateTimeOffset At, string Message)> Lines { get; } = [];
        internal string? Route { get; set; }
        internal string? Model { get; set; }
        internal string? State { get; set; }
        internal double? TotalMs { get; set; }
        internal double? FirstWordsMs { get; set; }
        internal double? FirstAudioMs { get; set; }
        internal int Requests { get; set; }
        internal List<object> Waits { get; } = [];
    }

    private sealed class PoolTrace(string id, string kind, string holder, DateTimeOffset at)
    {
        internal string Id { get; } = id;
        internal string Kind { get; } = kind;
        internal string Holder { get; } = holder;
        internal DateTimeOffset At { get; } = at;
        internal List<(DateTimeOffset At, string Message)> Lines { get; } = [];
        internal string? State { get; set; }
        internal double? TotalMs { get; set; }
        internal double? WaitedMs { get; set; }
        internal double? RanMs { get; set; }
    }

    internal static object Read(string? dataDirectory, int? turns, string? contains = null)
    {
        var count = turns ?? DefaultTurns;
        if (count is < 1 or > MaximumTurns) throw new ArgumentException($"turns must be 1-{MaximumTurns}.");
        var root = dataDirectory ?? SettingsStore.DefaultDataDirectory();
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("dataDirectory must be an absolute path.");
        var directory = LocalLogs.Directory(root);
        var records = Directory.Exists(directory) ? LocalLogs.Read(directory, LocalLogs.ThisDeviceId()) : [];
        return Summarize(records.Where(r => r.Component == "desktop").Select(r => (r.At, r.Message)), count, contains, Directory.Exists(directory));
    }

    internal static object Summarize(IEnumerable<(DateTimeOffset At, string Message)> lines, int count, string? contains = null, bool logsFolder = true)
    {
        var turns = new List<TurnTrace>();
        var open = new Dictionary<string, TurnTrace>(StringComparer.Ordinal);
        var requests = new List<PoolTrace>();
        var openRequests = new Dictionary<string, PoolTrace>(StringComparer.Ordinal);
        foreach (var (at, message) in lines.OrderBy(line => line.At))
        {
            if (Turn().Match(message) is { Success: true } turn)
            {
                var name = turn.Groups["name"].Value;
                var rest = turn.Groups["rest"].Value;
                var start = rest.StartsWith(" started: ", StringComparison.Ordinal);
                // The start line is made off the reply's path, so a caller's line about the turn ("prepared") may come first.
                if (!open.TryGetValue(name, out var traced) || start && traced.HasStart)
                {
                    traced = new(name, turn.Groups["purpose"].Value, at);
                    open[name] = traced;
                    turns.Add(traced);
                }
                if (start) traced.HasStart = true;
                traced.Lines.Add((at, message));
                if (Started().Match(rest) is { Success: true } started)
                {
                    traced.Route = started.Groups["route"].Value;
                    traced.Model = started.Groups["model"].Value.Trim();
                }
                else if (rest.StartsWith(": request ", StringComparison.Ordinal)) traced.Requests++;
                else if (Waiting().Match(rest) is { Success: true } waiting)
                    traced.Waits.Add(new
                    {
                        atMs = Number(waiting.Groups["at"].Value),
                        what = waiting.Groups["what"].Success ? waiting.Groups["what"].Value : "the next words",
                        waitedMs = Number(waiting.Groups[waiting.Groups["ms"].Success ? "ms" : "idle"].Value)
                    });
                else if (Ended().Match(rest) is { Success: true } ended)
                {
                    traced.State = ended.Groups["state"].Value;
                    traced.TotalMs = Number(ended.Groups["ms"].Value);
                    if (FirstWords().Match(rest) is { Success: true } words) traced.FirstWordsMs = Number(words.Groups["ms"].Value);
                    if (FirstAudio().Match(rest) is { Success: true } audio) traced.FirstAudioMs = Number(audio.Groups["ms"].Value);
                    open.Remove(name);
                }
            }
            else if (Request().Match(message) is { Success: true } request)
            {
                var id = request.Groups["id"].Value;
                var rest = request.Groups["rest"].Value;
                if (rest.StartsWith("waits in line: ", StringComparison.Ordinal) || !openRequests.TryGetValue(id, out var pooled))
                {
                    pooled = new(id, request.Groups["kind"].Value, request.Groups["holder"].Value, at);
                    openRequests[id] = pooled;
                    requests.Add(pooled);
                }
                pooled.Lines.Add((at, message));
                if (RequestEnded().Match(rest) is { Success: true } ended)
                {
                    pooled.State = ended.Groups["state"].Value;
                    pooled.TotalMs = Number(ended.Groups["total"].Value);
                    pooled.WaitedMs = Number(ended.Groups["waited"].Value);
                    pooled.RanMs = Number(ended.Groups["ran"].Value);
                    openRequests.Remove(id);
                }
            }
        }
        bool Wanted(IEnumerable<(DateTimeOffset At, string Message)> traced) =>
            string.IsNullOrEmpty(contains) || traced.Any(line => line.Message.Contains(contains, StringComparison.OrdinalIgnoreCase));
        var newestTurns = turns.Where(t => Wanted(t.Lines)).TakeLast(count).Reverse().ToArray();
        var newestRequests = requests.Where(r => Wanted(r.Lines)).TakeLast(count).Reverse().ToArray();
        return new
        {
            logsFolder,
            turns = turns.Count,
            unfinishedTurns = turns.Count(t => t.State is null),
            stillWaitingLines = turns.Sum(t => t.Waits.Count),
            poolRequests = requests.Count,
            newest = newestTurns.Select(t => new
            {
                name = t.Name, purpose = t.Purpose, at = t.At, route = t.Route, model = t.Model, state = t.State, totalMs = t.TotalMs,
                firstWordsMs = t.FirstWordsMs, firstAudioMs = t.FirstAudioMs, requests = t.Requests, waits = t.Waits,
                lines = t.Lines.Select(line => new { at = line.At, message = line.Message })
            }).ToArray(),
            newestPoolRequests = newestRequests.Select(r => new
            {
                id = r.Id, kind = r.Kind, holder = r.Holder, at = r.At, state = r.State, totalMs = r.TotalMs, waitedMs = r.WaitedMs,
                ranMs = r.RanMs, lines = r.Lines.Select(line => new { at = line.At, message = line.Message })
            }).ToArray()
        };
    }

    private static double? Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}
