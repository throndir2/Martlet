using System.IO;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Logs;
using Martlet.Core.Settings;
using Martlet.Diagnostics;

namespace Martlet.Mcp;

/// <summary>This PC's logs as the Diagnostics page shows them: every local part (desktop, avatar-renderer, host-runs, each
/// with its rotated copies) parsed into one timeline, newest first, with the page's filters, and the log host chosen in the
/// shared plan. Read-only: never writes, rotates or deletes a log, and contacts no host.</summary>
internal static class LogTimeline
{
    internal static readonly string[] Levels = ["all", "warnings", "errors"];
    internal const int DefaultLines = 200;
    internal const int MaximumLines = 1_000;

    internal static object Read(string? dataDirectory, string? level, string? component, string? contains, int? lines)
    {
        level ??= "all";
        var count = lines ?? DefaultLines;
        if (!Levels.Contains(level, StringComparer.Ordinal)) throw new ArgumentException($"Unknown level '{level}'; use {string.Join(", ", Levels)}.");
        if (component is not null && !LogComponents.Local.Contains(component, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown component '{component}'; use {string.Join(", ", LogComponents.Local)}.");
        if (count is < 1 or > MaximumLines) throw new ArgumentException($"lines must be 1-{MaximumLines}.");
        if (contains is { Length: > LogTail.MaximumFilterLength }) throw new ArgumentException($"contains is limited to {LogTail.MaximumFilterLength} characters.");
        var root = dataDirectory ?? SettingsStore.DefaultDataDirectory();
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("dataDirectory must be an absolute path.");
        var device = LocalLogs.ThisDeviceId();
        var directory = LocalLogs.Directory(root);
        var all = Directory.Exists(directory) ? LocalLogs.Read(directory, device) : [];
        var minimum = level switch { "errors" => 2, "warnings" => 1, _ => 0 };
        var matching = all.Reverse()
            .Where(r => LogLevels.Rank(r.Level) >= minimum && (component is null || r.Component == component) &&
                (string.IsNullOrEmpty(contains) || r.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        string? logHost = null;
        var planState = "none";
        try
        {
            var path = Path.Combine(root, "cluster.json");
            if (File.Exists(path))
            {
                logHost = ClusterPlan.Parse(File.ReadAllBytes(path)).For(ClusterJobs.Logs)?.HostId;
                planState = "loaded";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException) { planState = "unreadable"; }
        return new
        {
            device,
            logsFolder = Directory.Exists(directory),
            logHost,
            plan = planState,
            total = all.Count,
            errors = all.Count(r => LogLevels.Rank(r.Level) >= 2),
            warnings = all.Count(r => LogLevels.Rank(r.Level) == 1),
            components = all.GroupBy(r => r.Component).ToDictionary(g => g.Key, g => g.Count()),
            matching = matching.Length,
            lines = matching.Take(count).Select(r => new
            {
                at = r.At, level = r.Level, component = r.Component, seq = r.Seq, message = r.Message
            }).ToArray()
        };
    }
}
