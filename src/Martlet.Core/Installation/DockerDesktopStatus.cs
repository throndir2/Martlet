using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Martlet.Core.Installation;

/// <summary>Docker Desktop's own engine state as <c>docker desktop status --format json</c> reports it (for example
/// <c>running</c>, <c>starting</c> or <c>stopped</c>), and the Windows check its engine failed when it started, from its
/// own log. Read locally from Docker Desktop; nothing is sent anywhere.</summary>
public static partial class DockerDesktopStatus
{
    [GeneratedRegex(@"checking preconditions:\s*(?<reason>.+?)\s*(?:\(<nil>\))?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PreconditionPattern();

    [GeneratedRegex(@"virtuali[sz]ation support (?:was not|wasn't|not) detected|no virtuali[sz]ation available|virtual machine platform (?:is )?not enabled",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VirtualizationPattern();

    [GeneratedRegex(@"\A\[(?<time>\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.(?<fraction>\d{1,9}))?Z\]")]
    private static partial Regex LogTimePattern();

    /// <summary>The Windows check Docker Desktop's engine failed when it started, in Docker's words (for example
    /// <c>Virtual Machine Platform not enabled</c>, <c>No virtualization available</c> or <c>WSL update required</c>), or
    /// null when <paramref name="line"/> is not such a failure. Docker shows these as "Virtualization support not detected"
    /// or "Virtual Machine Platform not enabled", and keeps showing them until it starts again.</summary>
    public static string? Precondition(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var match = PreconditionPattern().Match(line);
        if (match.Success && match.Groups["reason"].Value.Trim().TrimEnd('.') is { Length: > 0 } reason) return reason;
        return VirtualizationPattern().Match(line) is { Success: true } virtualization ? virtualization.Value : null;
    }

    /// <summary>When Docker Desktop wrote <paramref name="line"/> (UTC), from its leading <c>[2026-10-04T19:40:26.0852815Z]</c>.</summary>
    public static DateTimeOffset? LogTime(string? line)
    {
        var match = LogTimePattern().Match(line ?? "");
        if (!match.Success || !DateTime.TryParseExact(match.Groups["time"].Value, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time))
            return null;
        var fraction = match.Groups["fraction"].Value;
        var ticks = fraction.Length == 0 ? 0 : long.Parse(fraction.PadRight(7, '0')[..7], CultureInfo.InvariantCulture);
        return new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc).AddTicks(ticks));
    }

    /// <summary>The last start check Docker Desktop's engine failed in <paramref name="logLines"/> at or after
    /// <paramref name="since"/>, or null when there is none. A line without a time continues the message before it
    /// (Docker writes one reason per line), so it counts when that message does; leading untimed lines count.</summary>
    public static string? LastPrecondition(IEnumerable<string> logLines, DateTimeOffset since)
    {
        string? last = null;
        var counts = true;
        foreach (var line in logLines)
        {
            if (LogTime(line) is { } time) counts = time >= since;
            if (counts) last = Precondition(line) ?? last;
        }
        return last;
    }

    /// <summary>The last start check Docker Desktop's engine failed in its current session (<see cref="Precondition"/> in
    /// <c>docker desktop logs --boot 0 --priority 1</c>): its window then says, for example, "Virtualization support not
    /// detected". Null when it logged none, off Windows, when Docker Desktop isn't installed or can't show its log.</summary>
    public static async Task<string?> ReadPreconditionAsync(CancellationToken token)
    {
        var (exit, output) = await RunAsync(["desktop", "logs", "--boot", "0", "--priority", "1", "--no-color"], token);
        if (exit != 0 || output is null) return null;
        return LastPrecondition(output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), DateTimeOffset.MinValue);
    }
    /// <summary>The engine isn't running. While Docker Desktop itself stays open this way it does not recover by
    /// itself (for example when it started before WSL was installed).</summary>
    public const string Stopped = "stopped";

    public static string DockerPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Docker", "Docker", "resources", "bin", "docker.exe");

    /// <summary>The <c>Status</c> value of <c>docker desktop status --format json</c>, or null when it isn't there.</summary>
    public static string? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("Status", out var value) &&
                value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Asks Docker Desktop for its engine state once, for at most 15 seconds. Null off Windows, when Docker
    /// Desktop isn't installed, doesn't answer or has no <c>docker desktop</c> command.</summary>
    public static async Task<string?> ReadAsync(CancellationToken token)
    {
        var (exit, output) = await RunAsync(["desktop", "status", "--format", "json"], token);
        return exit == 0 ? Parse(output) : null;
    }

    /// <summary>Runs Docker Desktop's docker.exe once, hidden, for at most 15 seconds: its exit code and output, or
    /// (null, null) off Windows, when Docker Desktop isn't installed, docker.exe can't start or doesn't answer.</summary>
    private static async Task<(int? Exit, string? Output)> RunAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(DockerPath)) return (null, null);
        var start = new ProcessStartInfo(DockerPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process? process;
        try { process = Process.Start(start); }
        catch (System.ComponentModel.Win32Exception) { return (null, null); }
        if (process is null) return (null, null);
        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                await errors;
                return (process.ExitCode, output);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                if (token.IsCancellationRequested) throw;
                return (null, null);
            }
        }
    }
}
