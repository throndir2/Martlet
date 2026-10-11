using System.Runtime.InteropServices;
using System.Text.Json;

namespace Martlet.NodeLinkCheck;

/// <summary>For MCP's app_update_check: stands in for the Martlet installer and for Martlet itself, both of which the
/// desktop's update helper starts. It appends how it was started to the file <see cref="RecordVariable"/> names (its
/// arguments, whether it has a console window that shows, and which processes share its console), then exits with
/// <see cref="ExitVariable"/>'s code (default 0). It installs and starts nothing; for the installer's /STAGE=prepare it puts a
/// text file where the staged Desktop app goes, so the update helper has a folder to switch to.</summary>
internal static class UpdateHelperFixture
{
    internal const string RecordVariable = "MARTLET_UPDATE_CHECK_RECORD";
    internal const string ExitVariable = "MARTLET_UPDATE_CHECK_EXIT";

    internal static int Record(string path, string[] args)
    {
        var window = GetConsoleWindow();
        var processes = new uint[64];
        var count = GetConsoleProcessList(processes, (uint)processes.Length);
        var record = new
        {
            at = DateTimeOffset.UtcNow,
            pid = Environment.ProcessId,
            args,
            consoleWindowVisible = window != IntPtr.Zero && IsWindowVisible(window),
            consoleProcesses = processes.Take((int)Math.Min(count, (uint)processes.Length)).ToArray(),
            priority = System.Diagnostics.Process.GetCurrentProcess().PriorityClass.ToString(),
            cwd = Environment.CurrentDirectory
        };
        File.AppendAllText(path, JsonSerializer.Serialize(record) + "\n");
        var code = int.TryParse(Environment.GetEnvironmentVariable(ExitVariable), out var exit) ? exit : 0;
        // Like the real installer, write the log /LOG names (here one FIXTURE line).
        if (args.FirstOrDefault(arg => arg.StartsWith("/LOG=", StringComparison.OrdinalIgnoreCase)) is { } log)
            File.WriteAllText(log["/LOG=".Length..], $"FIXTURE installer stand-in: {args.Length} switches, exit code {code}.\r\n");
        // Like the installer's /STAGE=prepare, put a (stand-in) Desktop app into the /DIR folder.
        if (code == 0 && args.Contains("/STAGE=prepare", StringComparer.OrdinalIgnoreCase) &&
            args.FirstOrDefault(arg => arg.StartsWith("/DIR=", StringComparison.OrdinalIgnoreCase)) is { } dir)
        {
            var desktop = Directory.CreateDirectory(Path.Combine(dir["/DIR=".Length..], "Desktop")).FullName;
            File.WriteAllText(Path.Combine(desktop, "Martlet.Desktop.exe"), "FIXTURE: a staged Martlet stand-in. Not a program.");
        }
        return code;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}
