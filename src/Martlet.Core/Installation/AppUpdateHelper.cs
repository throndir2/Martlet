using System.Diagnostics;
using System.Text;

namespace Martlet.Core.Installation;

/// <summary>The windowless helper that installs a downloaded Martlet release once Martlet has exited (install-update.cmd in
/// the updates folder of Martlet's data directory). It waits for Martlet's process to end, runs the installer, records the
/// installer's exit code and, when asked, starts Martlet again. When the release was already installed beside the running
/// Martlet (<see cref="AppUpdateStaging"/>), it only switches folders and runs the installer's quick finish step, so Martlet
/// is away for seconds instead of the whole install. An unattended install (automatic, or asked for by another of
/// your computers) runs the installer very silently: no installer window at all, and Martlet restarts minimized or in the
/// notification area. One you confirmed shows the installer's progress window. Every step goes to update.log, which Martlet
/// copies into its own log when it starts again; the installer writes its details to install.log.</summary>
public static class AppUpdateHelper
{
    public const string ScriptFile = "install-update.cmd";
    public const string ResultFile = "last-install.txt";
    public const string LogFile = "update.log";
    public const string InstallerLogFile = "install.log";
    /// <summary>Martlet restarted by an unattended install: minimized, without taking focus.</summary>
    public const string AfterUpdateArgument = "--after-update";
    /// <summary>Rounds the helper waits for Martlet to exit before it gives up and installs nothing (about 3 minutes).</summary>
    private const int WaitRounds = 180;
    /// <summary>The first rounds look again at once instead of after a second, so the install (and Martlet's window after it)
    /// starts moments after Martlet has exited; Martlet usually exits within them.</summary>
    private const int QuickRounds = 15;
    private const int MaximumLogLines = 50;

    /// <summary>The installer's switches: no questions, no message boxes, no Windows restart and no optional prerequisite
    /// tasks. Unattended, /VERYSILENT shows no window at all; otherwise /SILENT shows only the progress window.</summary>
    public static string InstallerArguments(bool unattended, string installerLog) =>
        $"{(unattended ? "/VERYSILENT" : "/SILENT")} /SUPPRESSMSGBOXES /NORESTART /SP- /TASKS=\"\" /LOG=\"{Cmd(installerLog)}\"";

    /// <summary>The installer's switches for the finish step after a staged switch: no window (it installs no files and takes
    /// about half a second), no questions, no restart.</summary>
    public static string FinishArguments(string installerLog) =>
        $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /TASKS=\"\" {AppUpdateStaging.FinishArgument} /LOG=\"{Cmd(installerLog)}\"";

    /// <summary>The arguments Martlet restarts with: its data folder when it was given one, then <paramref name="flags"/>.</summary>
    public static string RelaunchArguments(string? dataDirectoryArgument, params string[] flags) =>
        (dataDirectoryArgument is null ? "" : $" --data-directory \"{Cmd(dataDirectoryArgument)}\"") +
        string.Concat(flags.Select(flag => " " + flag));

    /// <summary>Tries to rename Martlet's program folder (about one second apart) before a staged switch gives up and runs
    /// the whole installer instead.</summary>
    private const int SwitchRounds = 10;

    /// <summary>The helper script for one install. <paramref name="processId"/> is the Martlet process it waits for;
    /// <paramref name="relaunch"/> (with <paramref name="relaunchArguments"/>) is started afterwards, or nothing when null.
    /// <paramref name="installRoot"/>: Martlet's program folder, whose update staging folders the helper removes at the end.
    /// With <paramref name="staged"/> the new version is already installed beside it (<see cref="AppUpdateStaging"/>): the
    /// helper renames the folders and runs the installer's quick finish step instead of the whole install, so Martlet is
    /// away for seconds. When the folder can't be renamed (something still uses a file in it), the whole installer runs as
    /// before.</summary>
    public static string Script(string directory, string installer, string version, int processId, bool unattended,
        string? relaunch, string relaunchArguments = "", string? installRoot = null, bool staged = false)
    {
        if (staged && installRoot is null) throw new ArgumentException("A staged switch needs Martlet's program folder.", nameof(installRoot));
        var log = Path.Combine(directory, LogFile);
        var result = Path.Combine(directory, ResultFile);
        var installerLog = Path.Combine(directory, InstallerLogFile);
        // Log lines carry only fixed text, numbers and the version: paths could hold characters cmd.exe would act on.
        string Note(string text) => $">>\"{Cmd(log)}\" echo %date% %time% {text}\r\n";
        var text = new StringBuilder("@echo off\r\nset n=0\r\n")
            .Append($">\"{Cmd(log)}\" echo %date% %time% Waiting for Martlet, process {processId}, to exit before " +
                $"{(staged ? "switching to" : "installing")} Martlet {version}.\r\n")
            .Append(":wait\r\n")
            .Append($"tasklist /FI \"PID eq {processId}\" /NH 2>NUL | find \" {processId} \" >NUL || goto install\r\n")
            .Append($"set /a n+=1\r\nif %n% gtr {WaitRounds} goto gaveup\r\n")
            .Append($"if %n% gtr {QuickRounds} ping -n 2 127.0.0.1 >NUL\r\ngoto wait\r\n")
            .Append(":gaveup\r\n")
            .Append(Note("Martlet did not exit within 3 minutes, so nothing was installed."))
            .Append($">\"{Cmd(result)}\" echo wait {version}\r\nexit /b 1\r\n")
            .Append(":install\r\n");
        if (staged)
        {
            var root = Cmd(installRoot!);
            var next = Cmd(AppUpdateStaging.StagedRoot(installRoot!));
            var previous = Cmd(AppUpdateStaging.PreviousRoot(installRoot!));
            // The new version is already in <program folder>.next: two folder renames switch to it. The first try comes at
            // once; the character's renderer and tool servers end with Martlet (kill-on-close job objects).
            text.Append(Note($"Martlet exited. Switching to Martlet {version}, which was installed beside it while Martlet ran."))
                .Append($"if not exist \"{next}\\{AppUpdateStaging.DesktopFolder}\\{AppUpdateStaging.DesktopExecutable}\" goto nostage\r\n")
                .Append("set s=0\r\n:switch\r\n")
                .Append($"if exist \"{previous}\" rd /s /q \"{previous}\"\r\n")
                .Append($"if exist \"{previous}\" goto inuse\r\n")
                .Append($"move \"{root}\" \"{previous}\" >NUL 2>&1 && goto switched\r\n")
                .Append($"set /a s+=1\r\nif %s% geq {SwitchRounds} goto inuse\r\n")
                .Append("ping -n 2 127.0.0.1 >NUL\r\ngoto switch\r\n")
                .Append(":switched\r\n")
                .Append($"move \"{next}\" \"{root}\" >NUL 2>&1 || goto putback\r\n")
                .Append($"if exist \"{previous}\\uninstall\" move \"{previous}\\uninstall\" \"{root}\\uninstall\" >NUL 2>&1\r\n")
                .Append(Note($"Switched to Martlet {version}. Updating its shortcuts and Windows' list of installed apps."))
                .Append($"\"{Cmd(installer)}\" {FinishArguments(installerLog)}\r\n")
                .Append("set code=%errorlevel%\r\n")
                .Append("if not \"%code%\"==\"0\" " + Note("The installer's finish step ended with exit code %code%. Martlet " +
                    $"{version} runs, but Windows' list of installed apps may still show the earlier version. Details are in install.log."))
                .Append($">\"{Cmd(result)}\" echo 0 {version}\r\n")
                .Append("goto done\r\n")
                .Append(":putback\r\n")
                .Append($"move \"{previous}\" \"{root}\" >NUL 2>&1\r\n")
                .Append(":inuse\r\n")
                .Append(Note("Martlet's program folder is still in use, so it could not be switched. Running the whole installer instead."))
                .Append("goto full\r\n")
                .Append(":nostage\r\n")
                .Append(Note("The version installed beside Martlet is missing. Running the whole installer instead."))
                .Append("ping -n 2 127.0.0.1 >NUL\r\n")
                .Append(":full\r\n");
        }
        else
            // One second for what Martlet started to let go of its files: the character's renderer and tool servers run in
            // kill-on-close job objects, so they end with Martlet, and the installer's own start adds more before it replaces
            // anything. Every second here is a second longer without Martlet's window.
            text.Append("ping -n 2 127.0.0.1 >NUL\r\n");
        text.Append(Note(unattended
                ? $"{(staged ? "Installing" : "Martlet exited. Installing")} Martlet {version} silently, with no installer window."
                : $"{(staged ? "Installing" : "Martlet exited. Installing")} Martlet {version} with the installer's progress window."))
            .Append($"\"{Cmd(installer)}\" {InstallerArguments(unattended, installerLog)}\r\n")
            .Append("set code=%errorlevel%\r\n")
            .Append(Note("The installer finished with exit code %code%. Its details are in install.log."))
            .Append($">\"{Cmd(result)}\" echo %code% {version}\r\n")
            .Append(":done\r\n");
        // /B: no console of its own. Martlet is a windowed app, so this only keeps the helper itself out of sight. /D: Martlet
        // works in its own folder, as when started from its shortcut, not in the helper's.
        if (relaunch is not null)
            text.Append(Note("Starting Martlet again."))
                .Append($"start \"\" /D \"{Cmd(Path.GetDirectoryName(relaunch) ?? directory)}\" /B \"{Cmd(relaunch)}\"{relaunchArguments}\r\n");
        else text.Append(Note("Done. Martlet starts the new version the next time it is opened."));
        // The previous version (and a staged one the whole installer replaced) go once Martlet runs again.
        if (installRoot is not null)
            text.Append($"if exist \"{Cmd(AppUpdateStaging.PreviousRoot(installRoot))}\" rd /s /q \"{Cmd(AppUpdateStaging.PreviousRoot(installRoot))}\"\r\n")
                .Append($"if exist \"{Cmd(AppUpdateStaging.StagedRoot(installRoot))}\" rd /s /q \"{Cmd(AppUpdateStaging.StagedRoot(installRoot))}\"\r\n");
        var content = text.ToString();
        // cmd.exe reads scripts in the OEM code page; switch to UTF-8 only when a path needs it.
        return content.Any(c => c > 127)
            ? content.Replace("@echo off\r\n", "@echo off\r\nchcp 65001 >NUL\r\n", StringComparison.Ordinal)
            : content;
    }

    /// <summary>Writes <paramref name="script"/> into <paramref name="directory"/> and starts it in a hidden console; it
    /// outlives the process that started it. <paramref name="environment"/> adds variables (MCP's app_update_check uses it
    /// to direct its stand-ins).</summary>
    public static Process? Start(string directory, string script, IReadOnlyDictionary<string, string>? environment = null)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ScriptFile);
        File.WriteAllText(path, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        // Not Martlet's own current folder (usually its Desktop folder): a process working in the program folder stops
        // Windows from renaming it for a staged switch.
        var start = new ProcessStartInfo("cmd.exe", $"/d /c \"{path}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = directory
        };
        if (environment is not null)
            foreach (var (name, value) in environment) start.Environment[name] = value;
        return Process.Start(start);
    }

    /// <summary>The last install's outcome, once (the file goes): the installer's exit code ("wait" when Martlet did not exit
    /// in time) and the version it installed. Null when there is none.</summary>
    public static (string Code, string Version)? TakeResult(string directory)
    {
        var path = Path.Combine(directory, ResultFile);
        if (!File.Exists(path)) return null;
        var parts = File.ReadAllText(path).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        File.Delete(path);
        return parts.Length == 2 ? (parts[0], parts[1]) : null;
    }

    /// <summary>The steps the helper wrote, once (the file goes); empty when there are none.</summary>
    public static IReadOnlyList<string> TakeLog(string directory)
    {
        var path = Path.Combine(directory, LogFile);
        if (!File.Exists(path)) return [];
        var lines = File.ReadLines(path).Select(line => line.Trim()).Where(line => line.Length > 0).Take(MaximumLogLines).ToArray();
        File.Delete(path);
        return lines;
    }

    /// <summary>The installer log's last <paramref name="count"/> lines, for a failed install; empty when there is none.
    /// <paramref name="file"/>: another log in the folder (the install beside Martlet writes <see cref="AppUpdateStaging.StageLogFile"/>).</summary>
    public static IReadOnlyList<string> InstallerLogTail(string directory, int count, string file = InstallerLogFile)
    {
        var path = Path.Combine(directory, file);
        if (!File.Exists(path)) return [];
        var tail = new Queue<string>(count);
        foreach (var line in File.ReadLines(path))
        {
            if (line.Trim().Length == 0) continue;
            if (tail.Count == count) tail.Dequeue();
            tail.Enqueue(line.TrimEnd());
        }
        return tail.ToArray();
    }

    private static string Cmd(string value) => value.Replace("%", "%%", StringComparison.Ordinal);
}
