using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Martlet.Desktop;

/// <summary>How often Martlet looks for releases and what it does with them, in updates.json beside the other local
/// preferences. Whether it looks at all stays in update-checks.txt (<see cref="UpdateCheckPreferences"/>).</summary>
internal sealed record UpdatePreferences
{
    internal const string FileName = "updates.json";
    internal static readonly int[] Intervals = [15, 30, 60, 180, 360, 720, 1440];

    /// <summary>Minutes between automatic checks while Martlet runs.</summary>
    public int IntervalMinutes { get; init; } = 60;
    /// <summary>Download and install new releases without asking; installs only while Martlet is idle, or when it exits.</summary>
    public bool AutoInstall { get; init; }
    /// <summary>Bring paired hosts that run an older Martlet up to this PC's version in the background.</summary>
    public bool AutoUpdateHosts { get; init; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static UpdatePreferences Load(string directory)
    {
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(directory, FileName));
            if (bytes.Length > 4096) throw new InvalidDataException($"{FileName} is too large.");
            var loaded = JsonSerializer.Deserialize<UpdatePreferences>(bytes, Json) ?? throw new InvalidDataException($"{FileName} is empty.");
            return Intervals.Contains(loaded.IntervalMinutes) ? loaded : loaded with { IntervalMinutes = 60 };
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return new(); }
        catch (JsonException error) { throw new InvalidDataException($"{FileName} could not be read.", error); }
    }

    internal static void Save(string directory, UpdatePreferences preferences)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        var temporary = Path.Combine(directory, $"updates.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(preferences, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static string Describe(int minutes) => minutes switch
    {
        < 60 => $"{minutes} minutes",
        60 => "hour",
        < 1440 => $"{minutes / 60} hours",
        _ => "day"
    };
}

internal static class AppVersions
{
    internal static string Current { get; } = typeof(App).Assembly.GetName().Version is { } v ? v.ToString(3) : "0.0.0";

    /// <summary>Whether a host reporting <paramref name="reported"/> runs an older Martlet than <paramref name="target"/>;
    /// hosts from 0.2.0 and earlier report nothing and count as older.</summary>
    internal static bool IsOlder(string? reported, string target) =>
        !Version.TryParse(target, out var wanted) || reported is null || !Version.TryParse(reported, out var have) || have < wanted;
}

/// <summary>Downloads a release installer into the local updates directory and runs it after Martlet exits. The installer is
/// unsigned; its bytes are checked against GitHub's SHA-256 asset digest, which detects corruption, not the publisher.</summary>
internal static class AppUpdateInstaller
{
    private const string ResultFile = "last-install.txt";

    internal static string UpdatesDirectory(string dataDirectory) => Path.Combine(dataDirectory, "updates");

    /// <summary>Returns the verified installer, reusing an earlier complete download of the same asset.</summary>
    internal static async Task<string> DownloadAsync(GitHubReleaseClient client, GitHubUpdate update, string dataDirectory,
        CancellationToken token)
    {
        var directory = UpdatesDirectory(dataDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, update.AssetName);
        if (File.Exists(path))
        {
            if (await MatchesAsync(path, update, token)) return path;
            File.Delete(path);
        }
        await client.DownloadAsync(update, path, token);
        return path;
    }

    private static async Task<bool> MatchesAsync(string path, GitHubUpdate update, CancellationToken token)
    {
        if (new FileInfo(path).Length != update.Bytes) return false;
        await using var stream = File.OpenRead(path);
        var digest = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(digest).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Starts a windowless helper that waits for this Martlet process to exit, runs the installer with its progress
    /// window (/SILENT, no optional prerequisite tasks), records the result and, when asked, starts Martlet again (in the
    /// notification area when <paramref name="inTray"/>).</summary>
    internal static void Launch(string installer, Version version, string dataDirectory, bool relaunch, bool quietRelaunch,
        string? dataDirectoryArgument, bool inTray = false)
    {
        var directory = UpdatesDirectory(dataDirectory);
        Directory.CreateDirectory(directory);
        var log = Path.Combine(directory, "install.log");
        var result = Path.Combine(directory, ResultFile);
        var script = Path.Combine(directory, "install-update.cmd");
        var pid = Environment.ProcessId;
        var arguments = (dataDirectoryArgument is null ? "" : $" --data-directory \"{Cmd(dataDirectoryArgument)}\"") +
            (quietRelaunch ? " --after-update" : "") + (inTray ? " " + WindowsStartup.TrayArgument : "");
        var text = new StringBuilder("@echo off\r\nset n=0\r\n:wait\r\n")
            .Append($"tasklist /FI \"PID eq {pid}\" /NH 2>NUL | find \" {pid} \" >NUL || goto install\r\n")
            .Append($"set /a n+=1\r\nif %n% gtr 180 (>\"{Cmd(result)}\" echo wait {version.ToString(3)}& exit /b 1)\r\n")
            .Append("ping -n 2 127.0.0.1 >NUL\r\ngoto wait\r\n:install\r\n")
            .Append("ping -n 3 127.0.0.1 >NUL\r\n")
            .Append($"\"{Cmd(installer)}\" /SILENT /SUPPRESSMSGBOXES /NORESTART /SP- /TASKS=\"\" /LOG=\"{Cmd(log)}\"\r\n")
            .Append($">\"{Cmd(result)}\" echo %errorlevel% {version.ToString(3)}\r\n");
        if (relaunch) text.Append($"start \"\" \"{Cmd(Environment.ProcessPath!)}\"{arguments}\r\n");
        var content = text.ToString();
        // cmd.exe reads scripts in the OEM code page; switch to UTF-8 only when a path needs it.
        if (content.Any(c => c > 127)) content = content.Replace("@echo off\r\n", "@echo off\r\nchcp 65001 >NUL\r\n", StringComparison.Ordinal);
        File.WriteAllText(script, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c \"{script}\"") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
    }

    /// <summary>Reports the outcome of the last installer run once, then forgets it. Null when there is nothing to report;
    /// <c>Failed</c> names the version that did not install, so it is not installed automatically again this session.</summary>
    internal static (string Message, string? Failed)? TakeLastResult(string dataDirectory, string running)
    {
        var directory = UpdatesDirectory(dataDirectory);
        var path = Path.Combine(directory, ResultFile);
        try
        {
            if (!File.Exists(path)) return null;
            var parts = File.ReadAllText(path).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            File.Delete(path);
            if (parts.Length != 2) return null;
            if (parts[0] == "0" && !AppVersions.IsOlder(running, parts[1])) return ($"Martlet updated to {running}.", null);
            if (parts[0] == "wait")
                return ($"The update to {parts[1]} couldn't start. Press Install to try again.", parts[1]);
            return ($"The update to {parts[1]} didn't finish. Press Install to try again.", parts[1]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Deletes downloaded installers for this version or older once they are no longer needed.</summary>
    internal static void CleanUp(string dataDirectory, string running)
    {
        try
        {
            var directory = UpdatesDirectory(dataDirectory);
            if (!Directory.Exists(directory)) return;
            foreach (var file in Directory.EnumerateFiles(directory, "Martlet-*-win-x64.exe"))
            {
                var name = Path.GetFileName(file)["Martlet-".Length..^"-win-x64.exe".Length];
                if (!AppVersions.IsOlder(running, name)) File.Delete(file);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static string Cmd(string value) => value.Replace("%", "%%", StringComparison.Ordinal);
}
