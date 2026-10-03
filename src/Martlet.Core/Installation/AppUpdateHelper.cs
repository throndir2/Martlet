using System.Diagnostics;
using System.Text;

namespace Martlet.Core.Installation;

/// <summary>The windowless helper that installs a downloaded Martlet release once Martlet has exited (install-update.cmd in
/// the updates folder of Martlet's data directory). It waits for Martlet's process to end, runs the installer, records the
/// installer's exit code and, when asked, starts Martlet again. An unattended install (automatic, or asked for by another of
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
    /// <summary>One-second rounds the helper waits for Martlet to exit before it gives up and installs nothing.</summary>
    private const int WaitRounds = 180;
    private const int MaximumLogLines = 50;

    /// <summary>The installer's switches: no questions, no message boxes, no Windows restart and no optional prerequisite
    /// tasks. Unattended, /VERYSILENT shows no window at all; otherwise /SILENT shows only the progress window.</summary>
    public static string InstallerArguments(bool unattended, string installerLog) =>
        $"{(unattended ? "/VERYSILENT" : "/SILENT")} /SUPPRESSMSGBOXES /NORESTART /SP- /TASKS=\"\" /LOG=\"{Cmd(installerLog)}\"";

    /// <summary>The arguments Martlet restarts with: its data folder when it was given one, then <paramref name="flags"/>.</summary>
    public static string RelaunchArguments(string? dataDirectoryArgument, params string[] flags) =>
        (dataDirectoryArgument is null ? "" : $" --data-directory \"{Cmd(dataDirectoryArgument)}\"") +
        string.Concat(flags.Select(flag => " " + flag));

    /// <summary>The helper script for one install. <paramref name="processId"/> is the Martlet process it waits for;
    /// <paramref name="relaunch"/> (with <paramref name="relaunchArguments"/>) is started afterwards, or nothing when null.</summary>
    public static string Script(string directory, string installer, string version, int processId, bool unattended,
        string? relaunch, string relaunchArguments = "")
    {
        var log = Path.Combine(directory, LogFile);
        var result = Path.Combine(directory, ResultFile);
        // Log lines carry only fixed text, numbers and the version: paths could hold characters cmd.exe would act on.
        string Note(string text) => $">>\"{Cmd(log)}\" echo %date% %time% {text}\r\n";
        var text = new StringBuilder("@echo off\r\nset n=0\r\n")
            .Append($">\"{Cmd(log)}\" echo %date% %time% Waiting for Martlet, process {processId}, to exit before installing Martlet {version}.\r\n")
            .Append(":wait\r\n")
            .Append($"tasklist /FI \"PID eq {processId}\" /NH 2>NUL | find \" {processId} \" >NUL || goto install\r\n")
            .Append($"set /a n+=1\r\nif %n% gtr {WaitRounds} goto gaveup\r\n")
            .Append("ping -n 2 127.0.0.1 >NUL\r\ngoto wait\r\n")
            .Append(":gaveup\r\n")
            .Append(Note("Martlet did not exit within 3 minutes, so nothing was installed."))
            .Append($">\"{Cmd(result)}\" echo wait {version}\r\nexit /b 1\r\n")
            .Append(":install\r\n")
            .Append("ping -n 3 127.0.0.1 >NUL\r\n")
            .Append(Note(unattended
                ? $"Martlet exited. Installing Martlet {version} silently, with no installer window."
                : $"Martlet exited. Installing Martlet {version} with the installer's progress window."))
            .Append($"\"{Cmd(installer)}\" {InstallerArguments(unattended, Path.Combine(directory, InstallerLogFile))}\r\n")
            .Append("set code=%errorlevel%\r\n")
            .Append(Note("The installer finished with exit code %code%. Its details are in install.log."))
            .Append($">\"{Cmd(result)}\" echo %code% {version}\r\n");
        // /B: no console of its own. Martlet is a windowed app, so this only keeps the helper itself out of sight.
        if (relaunch is not null)
            text.Append(Note("Starting Martlet again."))
                .Append($"start \"\" /B \"{Cmd(relaunch)}\"{relaunchArguments}\r\n");
        else text.Append(Note("Done. Martlet starts the new version the next time it is opened."));
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
        var start = new ProcessStartInfo("cmd.exe", $"/d /c \"{path}\"") { UseShellExecute = false, CreateNoWindow = true };
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

    /// <summary>The installer log's last <paramref name="count"/> lines, for a failed install; empty when there is none.</summary>
    public static IReadOnlyList<string> InstallerLogTail(string directory, int count)
    {
        var path = Path.Combine(directory, InstallerLogFile);
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
