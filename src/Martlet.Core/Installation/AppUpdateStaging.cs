using System.ComponentModel;
using System.Diagnostics;

namespace Martlet.Core.Installation;

/// <summary>Installs a downloaded Martlet release beside the running Martlet, so the update costs only a restart of a few
/// seconds instead of the whole install. The installer's <see cref="PrepareArgument"/> writes the new files into the program
/// folder's <see cref="StagedSuffix"/> sibling while Martlet keeps running (idle priority, no window, nothing registered).
/// After Martlet exits, the update helper (<see cref="AppUpdateHelper"/>) renames the folders and runs the installer with
/// <see cref="FinishArgument"/>, which installs no files and updates the shortcuts, the uninstaller and Windows' list of
/// installed apps. A development build, not installed by the installer, has no program folder and installs the classic way.</summary>
public static class AppUpdateStaging
{
    public const string StagedSuffix = ".next";
    public const string PreviousSuffix = ".previous";
    public const string PrepareArgument = "/STAGE=prepare";
    public const string FinishArgument = "/STAGE=finish";
    public const string StageLogFile = "stage.log";
    public const string DesktopFolder = "Desktop";
    public const string DesktopExecutable = "Martlet.Desktop.exe";
    private const string UninstallFolder = "uninstall";

    /// <summary>The program folder the installer put Martlet in: the parent of <paramref name="baseDirectory"/> when that is
    /// its Desktop folder and the program folder holds the installer's uninstall folder. Null otherwise (a development build).</summary>
    public static string? InstallRoot(string baseDirectory)
    {
        var desktop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        if (!string.Equals(Path.GetFileName(desktop), DesktopFolder, StringComparison.OrdinalIgnoreCase)) return null;
        var root = Path.GetDirectoryName(desktop);
        return root is not null && Directory.Exists(Path.Combine(root, UninstallFolder)) ? root : null;
    }

    /// <summary>Where the next version is installed while Martlet runs.</summary>
    public static string StagedRoot(string root) => root + StagedSuffix;

    /// <summary>Where the running version goes for the moment the helper switches folders.</summary>
    public static string PreviousRoot(string root) => root + PreviousSuffix;

    /// <summary>Whether <see cref="StagedRoot"/> holds a complete staged install (its Desktop app).</summary>
    public static bool IsStaged(string root) => File.Exists(Path.Combine(StagedRoot(root), DesktopFolder, DesktopExecutable));

    /// <summary>The installer's switches for the prepare step: no window, no questions, no restart, no optional tasks.</summary>
    public static string PrepareArguments(string stagedRoot, string installerLog) =>
        $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /TASKS=\"\" {PrepareArgument} /DIR=\"{stagedRoot}\" /LOG=\"{installerLog}\"";

    /// <summary>Installs <paramref name="installer"/> into <see cref="StagedRoot"/> of <paramref name="root"/> and waits for it,
    /// at idle priority so Martlet's own work goes first. An earlier staged folder is removed first; canceling ends the
    /// installer and removes what it wrote. Returns the installer's exit code, or -1 when it reported success but left no
    /// complete install. The installer writes its details to <see cref="StageLogFile"/> in <paramref name="updatesDirectory"/>.</summary>
    public static async Task<int> PrepareAsync(string installer, string root, string updatesDirectory, CancellationToken token,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var staged = StagedRoot(root);
        RemoveFolder(staged);
        Directory.CreateDirectory(updatesDirectory);
        var start = new ProcessStartInfo(installer, PrepareArguments(staged, Path.Combine(updatesDirectory, StageLogFile)))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = updatesDirectory
        };
        if (environment is not null)
            foreach (var (name, value) in environment) start.Environment[name] = value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The installer did not start.");
        // Set before the installer starts its own setup process, which inherits idle priority.
        try { process.PriorityClass = ProcessPriorityClass.Idle; }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { }
            TryRemoveFolder(staged);
            throw;
        }
        if (process.ExitCode != 0) return process.ExitCode;
        return IsStaged(root) ? 0 : -1;
    }

    /// <summary>Removes what an update can leave beside the program folder: a staged version Martlet never switched to and
    /// the previous version kept for the switch. Folders still in use stay; the next update or start tries again.</summary>
    public static void CleanUp(string root)
    {
        TryRemoveFolder(StagedRoot(root));
        TryRemoveFolder(PreviousRoot(root));
    }

    private static void RemoveFolder(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static void TryRemoveFolder(string path)
    {
        try { RemoveFolder(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
