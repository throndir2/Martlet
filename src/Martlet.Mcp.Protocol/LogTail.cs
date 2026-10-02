using System.IO;
using System.Text;
using Martlet.Core.Settings;

namespace Martlet.Mcp;

/// <summary>Reads the end of one of Martlet's local logs under <c>&lt;data directory&gt;\logs</c>. Read-only: it never
/// writes, rotates or deletes a log, and starts no audio, network or provider request.</summary>
internal static class LogTail
{
    internal static readonly string[] Logs = ["desktop", "avatar-renderer", "host-runs"];
    internal const int DefaultLines = 100;
    internal const int MaximumLines = 400;
    internal const int MaximumFilterLength = 200;
    private const int MaximumBytes = 256 * 1024;

    internal static object Read(string? dataDirectory, string? log, int? lines, string? contains)
    {
        log ??= "desktop";
        var count = lines ?? DefaultLines;
        if (!Logs.Contains(log, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown log '{log}'; use {string.Join(", ", Logs)}.");
        if (count is < 1 or > MaximumLines) throw new ArgumentException($"lines must be 1-{MaximumLines}.");
        if (contains is { Length: > MaximumFilterLength }) throw new ArgumentException($"contains is limited to {MaximumFilterLength} characters.");
        var root = dataDirectory ?? SettingsStore.DefaultDataDirectory();
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("dataDirectory must be an absolute path.");
        var path = Path.Combine(root, "logs", log + ".log");
        if (!File.Exists(path)) return new { log, exists = false, truncated = false, lines = Array.Empty<string>() };
        string text;
        long start;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            start = Math.Max(0, stream.Length - MaximumBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Could not read the {log} log: {error.Message}");
        }
        IEnumerable<string> all = text.Split('\n').Select(line => line.TrimEnd('\r'));
        // Reading from the middle of the file starts inside a line; drop that fragment.
        if (start > 0) all = all.Skip(1);
        all = all.Where(line => line.Length > 0);
        if (!string.IsNullOrEmpty(contains)) all = all.Where(line => line.Contains(contains, StringComparison.OrdinalIgnoreCase));
        return new { log, exists = true, truncated = start > 0, lines = all.TakeLast(count).ToArray() };
    }
}
