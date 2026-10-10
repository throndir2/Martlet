using System.IO;
using Martlet.Core.Logs;
using Martlet.Core.Settings;
using Martlet.Diagnostics;

namespace Martlet.Mcp;

/// <summary>The logs as the Diagnostics page shows them: this PC's local parts (desktop, avatar-renderer, host-runs, each with
/// its rotated copies) and the other computers' lines log sharing collected (network-logs.json), merged into one timeline,
/// newest first, with the page's filters. Read-only: never writes, rotates or deletes a log, and contacts no host.</summary>
internal static class LogTimeline
{
    internal static readonly string[] Levels = ["all", "warnings", "errors"];
    internal static readonly string[] Components = [.. LogComponents.Local, LogComponents.Gateway];
    internal const int DefaultLines = 200;
    internal const int MaximumLines = 1_000;

    internal static object Read(string? dataDirectory, string? level, string? component, string? contains, int? lines, string? source = null)
    {
        level ??= "all";
        var count = lines ?? DefaultLines;
        if (!Levels.Contains(level, StringComparer.Ordinal)) throw new ArgumentException($"Unknown level '{level}'; use {string.Join(", ", Levels)}.");
        if (component is not null && !Components.Contains(component, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown component '{component}'; use {string.Join(", ", Components)}.");
        if (count is < 1 or > MaximumLines) throw new ArgumentException($"lines must be 1-{MaximumLines}.");
        if (contains is { Length: > LogTail.MaximumFilterLength }) throw new ArgumentException($"contains is limited to {LogTail.MaximumFilterLength} characters.");
        if (source is { Length: > 64 }) throw new ArgumentException("source is limited to 64 characters.");
        var root = Root(dataDirectory);
        var device = LocalLogs.ThisDeviceId(root);
        var directory = LocalLogs.Directory(root);
        var local = Directory.Exists(directory) ? LocalLogs.Read(directory, device) : [];
        var network = Directory.Exists(directory) ? NetworkLogs.Load(directory) : new NetworkLogs();
        var others = network.Snapshot();
        var seen = new HashSet<(string, long)>();
        var all = local.Concat(others).Where(r => seen.Add((r.Stream, r.Seq)))
            .OrderByDescending(r => r.At).ThenByDescending(r => r.Seq).ToArray();
        var minimum = level switch { "errors" => 2, "warnings" => 1, _ => 0 };
        var matching = all
            .Where(r => LogLevels.Rank(r.Level) >= minimum && (component is null || r.Component == component) &&
                (source is null || r.Source == source) &&
                (string.IsNullOrEmpty(contains) || r.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return new
        {
            device,
            logsFolder = Directory.Exists(directory),
            total = all.Length,
            errors = all.Count(r => LogLevels.Rank(r.Level) >= 2),
            warnings = all.Count(r => LogLevels.Rank(r.Level) == 1),
            components = all.GroupBy(r => r.Component).ToDictionary(g => g.Key, g => g.Count()),
            sources = all.GroupBy(r => r.Source).ToDictionary(g => g.Key, g => g.Count()),
            network = new { state = network.LoadState, lines = others.Count },
            matching = matching.Length,
            lines = matching.Take(count).Select(r => new
            {
                at = r.At, level = r.Level, source = r.Source, component = r.Component, seq = r.Seq, relayedBy = r.RelayedBy, message = r.Message
            }).ToArray()
        };
    }

    /// <summary>Diagnostics › Save logs to share from a data directory: every line it has (its own logs and the other computers'
    /// lines log sharing collected) in one new ZIP at <paramref name="outputPath"/> (absolute, ending .zip, in an existing
    /// folder; an existing file is never replaced). Returns what the bundle holds and its about.txt.</summary>
    internal static object Export(string? dataDirectory, string outputPath)
    {
        if (!Path.IsPathFullyQualified(outputPath) || !outputPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("outputPath must be an absolute path ending in .zip.");
        var folder = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (folder is null || !Directory.Exists(folder)) throw new ArgumentException("outputPath's folder must exist.");
        if (File.Exists(outputPath)) throw new ArgumentException("outputPath already exists; choose a new file name.");
        var root = Root(dataDirectory);
        var device = LocalLogs.ThisDeviceId(root);
        var directory = LocalLogs.Directory(root);
        var local = Directory.Exists(directory) ? LocalLogs.Read(directory, device) : [];
        var network = Directory.Exists(directory) ? NetworkLogs.Load(directory) : new NetworkLogs();
        var version = typeof(LogTimeline).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var summary = LogBundle.Save(outputPath, local.Concat(network.Snapshot()),
            new LogBundleInfo(device, [device], version, System.Runtime.InteropServices.RuntimeInformation.OSDescription, null, DateTimeOffset.Now),
            overwrite: false);
        using var zip = System.IO.Compression.ZipFile.OpenRead(outputPath);
        return new
        {
            path = Path.GetFullPath(outputPath),
            bytes = summary.Bytes,
            files = zip.Entries.Select(e => new { name = e.FullName, bytes = e.Length }).ToArray(),
            lines = summary.Lines,
            computers = summary.Computers,
            errors = summary.Errors,
            warnings = summary.Warnings,
            oldest = summary.Oldest,
            newest = summary.Newest,
            network = new { state = network.LoadState, lines = network.Count },
            about = summary.About
        };
    }

    private static string Root(string? dataDirectory)
    {
        var root = dataDirectory ?? SettingsStore.DefaultDataDirectory();
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("dataDirectory must be an absolute path.");
        return root;
    }
}
