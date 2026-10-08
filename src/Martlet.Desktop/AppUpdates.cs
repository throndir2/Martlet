using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Martlet.Core.Installation;

namespace Martlet.Desktop;

/// <summary>How often Martlet looks for releases and what it does with them, in updates.json beside the other local
/// preferences. Whether it looks at all stays in update-checks.txt (<see cref="UpdateCheckPreferences"/>).</summary>
internal sealed record UpdatePreferences
{
    internal const string FileName = "updates.json";
    internal static readonly int[] Intervals = [15, 30, 60, 180, 360, 720, 1440];

    /// <summary>Minutes between automatic checks while Martlet runs.</summary>
    public int IntervalMinutes { get; init; } = 60;
    /// <summary>Download and install new releases without asking, as soon as one is downloaded (after a reply or work that
    /// exiting would cut short), or when Martlet exits.</summary>
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
        Martlet.Avatar.Audio2Face.Remote.HostRelease.IsOlder(reported, target);
}

/// <summary>Downloads a release installer into the local updates directory and runs it after Martlet exits. The installer is
/// unsigned; its bytes are checked against GitHub's SHA-256 asset digest, which detects corruption, not the publisher.</summary>
internal static class AppUpdateInstaller
{
    internal static string UpdatesDirectory(string dataDirectory) => Path.Combine(dataDirectory, "updates");

    /// <summary>Returns the verified installer, reusing an earlier complete download of the same asset; <paramref name="received"/>
    /// gets the bytes a new download has so far.</summary>
    internal static async Task<string> DownloadAsync(GitHubReleaseClient client, GitHubUpdate update, string dataDirectory,
        CancellationToken token, IProgress<long>? received = null)
    {
        var directory = UpdatesDirectory(dataDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, update.AssetName);
        if (File.Exists(path))
        {
            if (await MatchesAsync(path, update, token)) return path;
            File.Delete(path);
        }
        await client.DownloadAsync(update, path, token, received);
        return path;
    }

    private static async Task<bool> MatchesAsync(string path, GitHubUpdate update, CancellationToken token)
    {
        if (new FileInfo(path).Length != update.Bytes) return false;
        await using var stream = File.OpenRead(path);
        var digest = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(digest).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Starts the windowless helper (<see cref="AppUpdateHelper"/>) that waits for this Martlet process to exit, runs
    /// the installer, records the result and, when asked, starts Martlet again (in the notification area when
    /// <paramref name="inTray"/>). <paramref name="unattended"/> (an automatic install, or one another computer asked for):
    /// no installer window at all (/VERYSILENT) and Martlet restarts minimized; otherwise the installer shows its progress
    /// window (/SILENT). Neither asks anything or runs optional prerequisite tasks.</summary>
    internal static void Launch(string installer, Version version, string dataDirectory, bool relaunch, bool unattended,
        string? dataDirectoryArgument, bool inTray = false)
    {
        var directory = UpdatesDirectory(dataDirectory);
        var flags = new List<string>();
        if (unattended) flags.Add(AppUpdateHelper.AfterUpdateArgument);
        if (inTray) flags.Add(WindowsStartup.TrayArgument);
        var script = AppUpdateHelper.Script(directory, installer, version.ToString(3), Environment.ProcessId, unattended,
            relaunch ? Environment.ProcessPath! : null, AppUpdateHelper.RelaunchArguments(dataDirectoryArgument, [.. flags]));
        AppUpdateHelper.Start(directory, script)?.Dispose();
    }

    /// <summary>Reports the outcome of the last installer run once, then forgets it. Null when there is nothing to report;
    /// <c>Failed</c> names the version that did not install, so it is not installed automatically again this session.</summary>
    internal static (string Message, string? Failed)? TakeLastResult(string dataDirectory, string running)
    {
        try
        {
            if (AppUpdateHelper.TakeResult(UpdatesDirectory(dataDirectory)) is not var (code, version)) return null;
            if (code == "0" && !AppVersions.IsOlder(running, version)) return ($"Martlet updated to {running}.", null);
            if (code == "wait")
                return ($"The update to {version} couldn't start. Press Install to try again.", version);
            return ($"The update to {version} didn't finish. Press Install to try again.", version);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Copies what the last helper run did into Martlet's log (once), and for a failed install the installer log's
    /// last lines, so every computer's Diagnostics page shows how an update went, even an unattended one.</summary>
    internal static void LogLastRun(string dataDirectory, (string Message, string? Failed)? result)
    {
        var directory = UpdatesDirectory(dataDirectory);
        try
        {
            foreach (var line in AppUpdateHelper.TakeLog(directory)) ErrorLog.Info("Update helper: " + line);
            if (result is not { } outcome) return;
            if (outcome.Failed is null)
            {
                ErrorLog.Info(outcome.Message);
                return;
            }
            var tail = AppUpdateHelper.InstallerLogTail(directory, 15);
            ErrorLog.Warn(outcome.Message + (tail.Count == 0
                ? " The installer wrote no log."
                : $" Last lines of the installer's log ({AppUpdateHelper.InstallerLogFile} in the updates folder):\n" + string.Join("\n", tail)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn("Could not read the last update's log", error);
        }
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
}

/// <summary>FIXTURE for checking automatic installs through MCP: with <see cref="Variable"/> set to a version newer than this
/// build (for example 9.9.9) before Martlet starts, update checks find that version without contacting GitHub and its
/// "download" is a small text file, not a program. Installing it runs the real update helper, whose stand-in installer fails
/// at once (Windows can't run it), so nothing is installed; the helper records that and starts Martlet again as after any
/// update.</summary>
internal static class SimulatedAppUpdate
{
    internal const string Variable = "MARTLET_SIMULATE_APP_UPDATE";

    internal static GitHubUpdate? Update { get; } =
        System.Version.TryParse(Environment.GetEnvironmentVariable(Variable), out var version) &&
        version.Build >= 0 && version.Revision < 0 && version > typeof(App).Assembly.GetName().Version
            ? new GitHubUpdate(version, $"v{version.ToString(3)}", $"Martlet-{version.ToString(3)}-win-x64.exe", 0,
                Content.Length, Convert.ToHexString(SHA256.HashData(Content)),
                new Uri($"https://github.com/throndir2/Martlet/releases/tag/v{version.ToString(3)}"))
            : null;

    private static byte[] Content => "FIXTURE: a simulated Martlet update (MARTLET_SIMULATE_APP_UPDATE). Not a program."u8.ToArray();

    /// <summary>Writes the stand-in installer into the updates folder in place of a download.</summary>
    internal static string Write(string dataDirectory, GitHubUpdate update)
    {
        var directory = AppUpdateInstaller.UpdatesDirectory(dataDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, update.AssetName);
        File.WriteAllBytes(path, Content);
        return path;
    }
}
