using System.Text;
using System.Text.RegularExpressions;
using Martlet.Core.Logs;

namespace Martlet.Diagnostics;

/// <summary>Reads Martlet's own local logs under <c>&lt;data directory&gt;\logs</c> as structured lines: desktop.log and
/// avatar-renderer.log (time, level, thread, message and any stack-trace lines after it) and host-runs.log (time, run
/// and output line), each with its rotated older copies. Read-only: never writes, rotates or deletes a log.</summary>
public static partial class LocalLogs
{
    /// <summary>How much of each part's newest files is read (the current file, then its rotated copies).</summary>
    public const int DefaultBytesPerComponent = 1_048_576;
    private static readonly string[] Rotations = ["", ".1", ".2", ".3"];

    [GeneratedRegex(@"^(?<at>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) (?:(?<level>INFO|WARN|ERROR|FATAL) \[\d+\] )?(?<message>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Header();

    public static string Directory(string dataDirectory) => Path.Combine(dataDirectory, "logs");

    /// <summary>The ID this PC uses with Martlet hosts and in the shared plan ("desktop-" and its computer name), which
    /// also names it as the source of its log lines.</summary>
    public static string ThisDeviceId()
    {
        var name = new string(Environment.MachineName.ToLowerInvariant()
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
        var id = "desktop-" + (name.Length == 0 ? "pc" : name);
        return id.Length > 64 ? id[..64] : id;
    }

    /// <summary>Every local part's lines, oldest first.</summary>
    public static IReadOnlyList<LogRecord> Read(string logDirectory, string source, int bytesPerComponent = DefaultBytesPerComponent) =>
        LogComponents.Local.SelectMany(component => ReadComponent(logDirectory, source, component, bytesPerComponent))
            .OrderBy(record => record.At).ThenBy(record => record.Seq).ToArray();

    /// <summary>One part's lines, oldest first, from at most <paramref name="budget"/> bytes of its newest files. Sequence
    /// numbers only grow, so reading the same file again gives the same lines the same numbers.</summary>
    public static IReadOnlyList<LogRecord> ReadComponent(string logDirectory, string source, string component, int budget = DefaultBytesPerComponent)
    {
        var chunks = new List<(string Text, bool Partial)>();
        var remaining = (long)budget;
        foreach (var rotation in Rotations)
        {
            if (remaining <= 0) break;
            var path = Path.Combine(logDirectory, component + rotation + ".log");
            if (!File.Exists(path)) continue;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var start = Math.Max(0, stream.Length - remaining);
                stream.Seek(start, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                chunks.Add((reader.ReadToEnd(), start > 0));
                remaining -= stream.Length - start;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        chunks.Reverse();
        var records = new List<LogRecord>();
        var last = 0L;
        foreach (var (text, partial) in chunks)
            foreach (var record in Parse(text, source, component, partial))
            {
                // A clock set back must not reuse numbers already given to earlier lines.
                var seq = record.Seq > last ? record.Seq : last + 1;
                records.Add(seq == record.Seq ? record : record with { Seq = seq });
                last = seq;
            }
        return records;
    }

    /// <summary>Parses log text. Lines that don't start with a timestamp continue the line before (a stack trace);
    /// <paramref name="partial"/> text starts mid-file, so everything before its first timestamped line is dropped.</summary>
    public static IReadOnlyList<LogRecord> Parse(string text, string source, string component, bool partial = false)
    {
        var records = new List<LogRecord>();
        var lines = text.Split('\n');
        DateTimeOffset at = default;
        string? level = null;
        StringBuilder? message = null;
        var lastMilliseconds = long.MinValue;
        var ordinal = 0;
        void Flush()
        {
            if (message is null) return;
            var body = LogRules.Clean(message.ToString());
            if (body.Length == 0) return;
            var milliseconds = at.ToUnixTimeMilliseconds();
            ordinal = milliseconds == lastMilliseconds ? ordinal + 1 : 0;
            lastMilliseconds = milliseconds;
            records.Add(new LogRecord
            {
                Source = source, Component = component, Seq = LogRules.Seq(at, ordinal), At = at, Level = level!, Message = body
            });
        }
        for (var index = partial ? 1 : 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            var header = Header().Match(line);
            if (header.Success && DateTimeOffset.TryParseExact(header.Groups["at"].Value, "yyyy-MM-dd HH:mm:ss.fff zzz",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var time))
            {
                Flush();
                at = time;
                level = header.Groups["level"].Success ? header.Groups["level"].Value : LogLevels.Info;
                message = new StringBuilder(header.Groups["message"].Value);
            }
            else if (message is not null && message.Length < LogRecord.MaximumMessageCharacters)
                message.Append('\n').Append(line);
        }
        Flush();
        return records;
    }
}
