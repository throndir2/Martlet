using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Martlet.Mcp.Client;

/// <summary>Builds the process for a stdio server. On Windows, npm-style commands such as npx are .cmd scripts that
/// CreateProcess can't run directly, so they go through cmd.exe with cross-spawn's quoting rules.</summary>
internal static partial class McpProcessStart
{
    internal static ProcessStartInfo Create(McpServerDefinition server)
    {
        var utf8 = new UTF8Encoding(false);
        var info = new ProcessStartInfo
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = utf8, StandardOutputEncoding = utf8, StandardErrorEncoding = utf8
        };
        if (server.WorkingDirectory is { Length: > 0 } directory) info.WorkingDirectory = directory;
        foreach (var (name, value) in server.Env) info.Environment[name] = value;
        var command = server.Command ?? throw new McpException("The server has no command.");
        if (!OperatingSystem.IsWindows())
        {
            info.FileName = command;
            foreach (var argument in server.Args) info.ArgumentList.Add(argument);
            return info;
        }
        var path = info.Environment.TryGetValue("PATH", out var configured) ? configured : Environment.GetEnvironmentVariable("PATH");
        var resolved = Resolve(command, path, info.WorkingDirectory) ??
            throw new McpException($"Couldn't find \"{command}\" on this PC. Install it (for npx, install Node.js) " +
                "or use the program's full path, then save again.");
        if (Path.GetExtension(resolved).ToLowerInvariant() is ".cmd" or ".bat")
        {
            var doubleEscape = NpmShimPattern().IsMatch(resolved);
            var line = string.Join(' ', new[] { EscapeCommand(Path.IsPathRooted(command) ? resolved : command) }
                .Concat(server.Args.Select(argument => EscapeArgument(argument, doubleEscape))));
            info.FileName = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } shell ? shell : "cmd.exe";
            info.Arguments = $"/d /s /c \"{line}\"";
            return info;
        }
        info.FileName = resolved;
        foreach (var argument in server.Args) info.ArgumentList.Add(argument);
        return info;
    }

    internal static string? Resolve(string command, string? path, string? workingDirectory)
    {
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> Candidates(string stem) =>
            Path.HasExtension(stem) ? [stem, .. extensions.Select(e => stem + e)] : extensions.Select(e => stem + e);
        if (command.Contains('\\') || command.Contains('/') || Path.IsPathRooted(command))
        {
            var full = Path.IsPathRooted(command) ? command : Path.GetFullPath(command, workingDirectory ?? Environment.CurrentDirectory);
            return Candidates(full).FirstOrDefault(File.Exists);
        }
        foreach (var folder in (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string expanded;
            try { expanded = Environment.ExpandEnvironmentVariables(folder.Trim('"')); }
            catch (ArgumentException) { continue; }
            foreach (var candidate in Candidates(command))
            {
                string full;
                try { full = Path.Combine(expanded, candidate); }
                catch (ArgumentException) { break; }
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    [GeneratedRegex(@"([()\][%!^""`<>&|;, *?])", RegexOptions.CultureInvariant)]
    private static partial Regex MetaCharacters();

    [GeneratedRegex(@"node_modules[\\/]\.bin[\\/][^\\/]+\.cmd$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NpmShimPattern();

    [GeneratedRegex(@"(\\*)""", RegexOptions.CultureInvariant)]
    private static partial Regex QuotePattern();

    [GeneratedRegex(@"(\\*)$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingBackslashes();

    internal static string EscapeCommand(string command) => MetaCharacters().Replace(command, "^$1");

    internal static string EscapeArgument(string argument, bool doubleEscape)
    {
        argument = QuotePattern().Replace(argument, "$1$1\\\"");
        argument = TrailingBackslashes().Replace(argument, "$1$1");
        argument = MetaCharacters().Replace($"\"{argument}\"", "^$1");
        return doubleEscape ? MetaCharacters().Replace(argument, "^$1") : argument;
    }
}

/// <summary>One Windows job for every MCP server Martlet starts: the servers (and anything they start) end with Martlet,
/// even when it is killed.</summary>
internal static class McpChildProcesses
{
    private static readonly Lazy<SafeFileHandle?> Job = new(Create);

    internal static void Adopt(Process process)
    {
        if (!OperatingSystem.IsWindows() || Job.Value is not { IsInvalid: false } job) return;
        try { AssignProcessToJobObject(job, process.Handle); }
        catch (InvalidOperationException) { }
    }

    private static SafeFileHandle? Create()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) return null;
        var limits = new JobLimits { Basic = new() { Flags = 0x2000 } };
        if (SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JobLimits>())) return job;
        job.Dispose();
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        internal long ProcessTime, JobTime;
        internal uint Flags;
        internal UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcesses;
        internal UIntPtr Affinity;
        internal uint Priority, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        internal BasicLimits Basic;
        internal IoCounters Io;
        internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobLimits limits, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
