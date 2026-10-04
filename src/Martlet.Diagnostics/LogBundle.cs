using System.Globalization;
using System.IO.Compression;
using System.Text;
using Martlet.Core.Logs;

namespace Martlet.Diagnostics;

/// <summary>Where a log bundle was saved: this PC's device ID, every source that is this PC (its desktop app and its own host
/// service), the Martlet version and system, how log sharing went (or null) and when.</summary>
public sealed record LogBundleInfo(string Device, IReadOnlyCollection<string> ThisPc, string Version, string System, string? Sharing,
    DateTimeOffset SavedAt);

/// <summary>What a saved bundle holds: lines, computers, the last 24 hours' errors and warnings, the oldest and newest line,
/// the size on disk and the text of its about.txt.</summary>
public sealed record LogBundleSummary(int Lines, int Computers, int Errors, int Warnings, DateTimeOffset? Oldest, DateTimeOffset? Newest,
    long Bytes, string About);

/// <summary>
/// Diagnostics' "Save logs to share": one ZIP with every log line this PC has from every computer of the owner's Martlet network
/// (its own logs and the lines log sharing collected), easy to attach to a message or an issue. <see cref="AboutEntry"/> says
/// where and when it was saved, which computers and parts it covers and what it can contain; <see cref="LinesEntry"/> holds
/// every line once, oldest first. Logs hold activity, errors and status only, never keys, pairing secrets or conversations.
/// </summary>
public static class LogBundle
{
    public const string AboutEntry = "about.txt";
    public const string LinesEntry = "martlet-logs.txt";

    /// <summary>A file name such as "Martlet logs desktop-diva 2026-10-04 0140.zip".</summary>
    public static string SuggestedFileName(string device, DateTimeOffset at) =>
        $"Martlet logs {device} {at.ToLocalTime().ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)}.zip";

    /// <summary>Writes the bundle to <paramref name="path"/> through a temporary file beside it, replacing an existing file
    /// only when <paramref name="overwrite"/>. Throws on storage failure (or when the file exists and may not be replaced).</summary>
    public static LogBundleSummary Save(string path, IEnumerable<LogRecord> records, LogBundleInfo info, bool overwrite)
    {
        var full = Path.GetFullPath(path);
        if (!overwrite && File.Exists(full)) throw new IOException($"{Path.GetFileName(full)} already exists.");
        var folder = Path.GetDirectoryName(full) ?? throw new IOException("The bundle needs a folder.");
        var temporary = Path.Combine(folder, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            LogBundleSummary summary;
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                summary = Write(stream, records, info);
            File.Move(temporary, full, overwrite);
            return summary with { Bytes = new FileInfo(full).Length };
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Writes the bundle as a ZIP to <paramref name="output"/> (left open). Each line is kept once (per computer,
    /// part and sequence number).</summary>
    public static LogBundleSummary Write(Stream output, IEnumerable<LogRecord> records, LogBundleInfo info)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(info);
        var seen = new HashSet<(string, long)>();
        var lines = records.Where(r => r is not null && seen.Add((r.Stream, r.Seq))).ToList();
        lines.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Stream != b.Stream ? string.CompareOrdinal(a.Stream, b.Stream) : a.Seq.CompareTo(b.Seq));
        var thisPc = info.ThisPc.Append(info.Device).ToHashSet(StringComparer.Ordinal);
        string Computer(string source) => thisPc.Contains(source) ? info.Device : source;
        var day = info.SavedAt - TimeSpan.FromDays(1);
        var recent = lines.Where(r => r.At >= day).ToArray();
        var errors = recent.Count(r => LogLevels.Rank(r.Level) >= 2);
        var warnings = recent.Count(r => LogLevels.Rank(r.Level) == 1);
        var computers = lines.Select(r => Computer(r.Source)).Append(info.Device).Distinct(StringComparer.Ordinal).Count();
        var about = About(lines, info, thisPc, computers, errors, warnings);

        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, AboutEntry, writer => writer.Write(about));
            WriteEntry(zip, LinesEntry, writer =>
            {
                foreach (var record in lines) writer.Write(FormatLine(record, thisPc));
            });
        }
        return new(lines.Count, computers, errors, warnings, lines.Count == 0 ? null : lines[0].At, lines.Count == 0 ? null : lines[^1].At,
            output.CanSeek ? output.Length : 0, about);
    }

    /// <summary>One line as the bundle writes it: "2026-10-04 01:40:12.345 -07:00 WARN  gpu-box/gateway: message", with "(this
    /// PC)" after this PC's sources, "via" the desktop that passed it on, and the message's following lines indented.</summary>
    public static string FormatLine(LogRecord record, IReadOnlySet<string> thisPc)
    {
        ArgumentNullException.ThrowIfNull(record);
        var text = new StringBuilder();
        text.Append(record.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)).Append(' ')
            .Append(record.Level.PadRight(5)).Append(' ').Append(record.Source).Append('/').Append(record.Component);
        if (thisPc.Contains(record.Source)) text.Append(" (this PC)");
        if (record.RelayedBy is { } by) text.Append(" via ").Append(by);
        text.Append(": ");
        var first = true;
        foreach (var line in record.Message.Split('\n'))
        {
            if (!first) text.Append("    ");
            text.Append(line.TrimEnd('\r')).Append('\n');
            first = false;
        }
        return text.ToString();
    }

    private static string About(IReadOnlyList<LogRecord> lines, LogBundleInfo info, IReadOnlySet<string> thisPc, int computers, int errors,
        int warnings)
    {
        static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        text.Append("Martlet diagnostic logs\n\n");
        text.Append($"Saved {Time(info.SavedAt)} on {info.Device} with Martlet {info.Version} ({info.System}).\n");
        var others = thisPc.Where(s => s != info.Device).Order(StringComparer.Ordinal).ToArray();
        if (others.Length > 0) text.Append($"This PC also writes as {string.Join(", ", others)} (its own host service).\n");
        text.Append(lines.Count == 0 ? "No log lines yet.\n"
            : $"{lines.Count.ToString("N0", CultureInfo.InvariantCulture)} lines from {computers} computer{(computers == 1 ? "" : "s")}, " +
              $"{Time(lines[0].At)} to {Time(lines[^1].At)}.\n");
        text.Append($"Last 24 hours before saving: {errors.ToString("N0", CultureInfo.InvariantCulture)} error{(errors == 1 ? "" : "s")}, " +
            $"{warnings.ToString("N0", CultureInfo.InvariantCulture)} warning{(warnings == 1 ? "" : "s")}.\n");
        if (info.Sharing is { Length: > 0 } sharing) text.Append($"Log sharing: {sharing}\n");
        text.Append("\nComputers and parts (lines, newest line):\n");
        foreach (var source in lines.GroupBy(r => r.Source, StringComparer.Ordinal)
                     .OrderBy(g => !thisPc.Contains(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            text.Append($"  {source.Key}{(thisPc.Contains(source.Key) ? " (this PC)" : "")}\n");
            foreach (var part in source.GroupBy(r => r.Component, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
                text.Append($"    {part.Key}: {part.Count().ToString("N0", CultureInfo.InvariantCulture)}, {Time(part.Max(r => r.At))}\n");
        }
        text.Append($"\n{LinesEntry} has every line once, oldest first: time, level, computer/part (\"via\" names the computer that\n");
        text.Append("passed a line on), then the message; a message's following lines (stack traces, output) are indented.\n");
        text.Append("\nThese logs hold activity, errors and status from Martlet on your computers: never keys, pairing secrets or what\n");
        text.Append("you and Martlet said. They can include local paths, computer and host names and provider error text, so look\n");
        text.Append("through them before posting them publicly.\n");
        return text.ToString();
    }

    private static void WriteEntry(ZipArchive zip, string name, Action<StreamWriter> write)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        write(writer);
    }
}
