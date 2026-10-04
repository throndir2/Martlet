using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Martlet.Mcp.Client;

// Martlet's own terminal, a built-in tool beside the MCP servers' tools: the desktop offers run_terminal_command to replies
// (McpToolService), and Martlet.Mcp's terminal_status and terminal_check read and rehearse the same code.

/// <summary>The program that runs Martlet's terminal commands.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TerminalShell>))]
public enum TerminalShell { WindowsPowerShell, PowerShell, CommandPrompt }

/// <summary>Companion › Tools › Terminal on this PC, saved in terminal.json in the data folder and never shared with other
/// computers: whether replies may run commands here (off by default), in which shell and folder, whether every command asks
/// first (on by default) and how long one may run. A missing or unreadable file means the defaults, so the terminal is off.</summary>
public sealed record TerminalSettings
{
    public const string FileName = "terminal.json";
    public const int DefaultTimeLimit = 30;
    private const int MaxFileBytes = 64 * 1024;
    private const int MaxFolderLength = 1024;

    /// <summary>The time limits offered, in seconds. A confirmation (60 s at most) and the command both fit in one reply.</summary>
    public static IReadOnlyList<int> TimeLimits { get; } = [15, 30, 60];

    public bool Enabled { get; init; }
    public TerminalShell Shell { get; init; } = TerminalShell.WindowsPowerShell;
    /// <summary>Where each command starts; empty is the user's home folder.</summary>
    public string Folder { get; init; } = "";
    public bool AskFirst { get; init; } = true;
    public int TimeLimitSeconds { get; init; } = DefaultTimeLimit;

    [JsonIgnore] public bool CustomFolder => Folder.Length > 0;
    [JsonIgnore] public string StartFolder => CustomFolder ? Folder : HomeFolder;
    public static string HomeFolder => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static TerminalSettings Load(string? directory) => Read(directory).Settings;

    /// <summary>The saved settings and the file's state: none, loaded or unreadable (which reads as the defaults).</summary>
    public static (TerminalSettings Settings, string State) Read(string? directory)
    {
        if (directory is null) return (new(), "none");
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return (new(), "none");
            if (new FileInfo(path).Length > MaxFileBytes) return (new(), "unreadable");
            var loaded = JsonSerializer.Deserialize<TerminalSettings>(File.ReadAllText(path));
            return loaded is null ? (new(), "unreadable") : (loaded.Normalized(), "loaded");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return (new(), "unreadable");
        }
    }

    public TerminalSettings Normalized() => this with
    {
        Shell = Enum.IsDefined(Shell) ? Shell : TerminalShell.WindowsPowerShell,
        Folder = Folder is { Length: > 0 and <= MaxFolderLength } folder && Path.IsPathFullyQualified(folder) ? folder : "",
        TimeLimitSeconds = TimeLimits.Contains(TimeLimitSeconds) ? TimeLimitSeconds : DefaultTimeLimit
    };

    /// <summary>Saves terminal.json atomically; false when the data folder can't be written.</summary>
    public bool Save(string? directory)
    {
        if (directory is null) return false;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = Path.Combine(directory, $"terminal.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(Normalized(), new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>What one terminal command did. <see cref="Problem"/> says why it didn't run at all.</summary>
public sealed record TerminalRun(int? ExitCode, string Output, bool TimedOut, bool Cut, TimeSpan Elapsed, string? Problem = null)
{
    public bool Succeeded => Problem is null && !TimedOut && ExitCode == 0;
    public override string ToString() => $"{nameof(TerminalRun)} (exit {ExitCode}, timed out {TimedOut})";
}

/// <summary>Runs one command the way a terminal would, but hidden: a new shell in the start folder, as the user, with its
/// input closed (so nothing waits for typing), UTF-8 output, and the whole process tree stopped at the time limit or when the
/// reply stops. Programs the command starts that outlive the shell (an app it opened) keep running. Output and errors are
/// kept in the order they arrive; long output keeps its start and its end.</summary>
public static partial class TerminalRunner
{
    public const int MaxCommandCharacters = 4000;
    internal const int HeadCharacters = 3000, TailCharacters = 7000;
    private static readonly TimeSpan DrainWait = TimeSpan.FromSeconds(1);

    public static string Name(TerminalShell shell) => shell switch
    {
        TerminalShell.WindowsPowerShell => "Windows PowerShell",
        TerminalShell.PowerShell => "PowerShell 7",
        _ => "Command Prompt"
    };

    /// <summary>The shell's program on this PC, or null when it isn't installed.</summary>
    public static string? Find(TerminalShell shell)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var path = shell switch
        {
            TerminalShell.WindowsPowerShell => Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"),
            TerminalShell.CommandPrompt => Path.Combine(system, "cmd.exe"),
            _ => McpProcessStart.Resolve("pwsh.exe", Environment.GetEnvironmentVariable("PATH"), null) is { } found ? found
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe")
        };
        return File.Exists(path) ? path : null;
    }

    /// <summary>Why the shell can't run this command as given, or null.</summary>
    public static string? Check(TerminalShell shell, string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "The command is empty.";
        if (command.Length > MaxCommandCharacters) return $"The command is longer than {MaxCommandCharacters} characters.";
        if (command.Contains('\0')) return "The command contains a null character.";
        if (shell == TerminalShell.CommandPrompt && command.IndexOfAny(['\r', '\n']) >= 0)
            return "Command Prompt runs one line. Join commands with & instead.";
        return null;
    }

    /// <summary>Runs <paramref name="command"/> with <paramref name="settings"/>' shell, folder and time limit
    /// (<paramref name="timeLimit"/> overrides it). Throws <see cref="OperationCanceledException"/> after stopping the command
    /// when <paramref name="cancellationToken"/> is canceled; every other failure is in the result.</summary>
    public static async Task<TerminalRun> RunAsync(TerminalSettings settings, string command, CancellationToken cancellationToken,
        TimeSpan? timeLimit = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var name = Name(settings.Shell);
        if (Check(settings.Shell, command) is { } invalid) return Failed(invalid);
        var folder = settings.StartFolder;
        if (!Directory.Exists(folder))
            return Failed("The terminal's start folder doesn't exist. Choose another in Companion › Tools › Terminal.");
        if (Find(settings.Shell) is not { } program)
            return Failed($"{name} isn't installed on this PC. Choose another shell in Companion › Tools › Terminal.");
        cancellationToken.ThrowIfCancellationRequested();

        var output = new TerminalOutput();
        var watch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = StartInfo(settings.Shell, program, command, folder) };
        try
        {
            if (!process.Start()) return Failed($"Couldn't start {name}.");
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            return Failed($"Couldn't start {name}: {error.Message}");
        }
        try { process.StandardInput.Close(); }
        catch (IOException) { }
        Task[] pumps = [Pump(process.StandardOutput, output), Pump(process.StandardError, output)];

        var timedOut = false;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            limit.CancelAfter(timeLimit ?? TimeSpan.FromSeconds(settings.TimeLimitSeconds));
            try { await process.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                Stop(process);
                if (cancellationToken.IsCancellationRequested)
                {
                    output.Close();
                    throw;
                }
                timedOut = true;
            }
        }
        // A program the command started can hold the output open after the shell ends; what arrived by then is the output.
        await Task.WhenAny(Task.WhenAll(pumps), Task.Delay(DrainWait, CancellationToken.None)).ConfigureAwait(false);
        int? exitCode = null;
        if (!timedOut)
        {
            try { exitCode = process.ExitCode; }
            catch (InvalidOperationException) { }
        }
        var (text, cut) = output.Close();
        return new(exitCode, text, timedOut, cut, watch.Elapsed);

        TerminalRun Failed(string problem) => new(null, "", false, false, TimeSpan.Zero, problem);
    }

    /// <summary>What the model is told about a run: the exit code (or why it stopped or didn't run) and the output.</summary>
    public static string Report(TerminalRun run, TerminalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Problem is { } problem) return problem;
        var heading = run.TimedOut
            ? $"Stopped after {settings.TimeLimitSeconds} seconds (the user's time limit); it may not have finished. Output so far:"
            : $"Exit code {run.ExitCode}.";
        return heading + "\n" + (run.Output.Length == 0 ? "(no output)" : run.Output);
    }

    private static ProcessStartInfo StartInfo(TerminalShell shell, string program, string command, string folder)
    {
        var utf8 = new UTF8Encoding(false);
        var info = new ProcessStartInfo(program)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = utf8, StandardErrorEncoding = utf8
        };
        // Plain text without colors, and git never waits for a typed password.
        info.Environment["NO_COLOR"] = "1";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (shell == TerminalShell.CommandPrompt)
        {
            // cmd fixes its code page when it starts, so an outer cmd switches the console to UTF-8 and starts the one that runs
            // the command. The command arrives through delayed expansion (!...!), after the outer cmd has parsed its own line,
            // so it never sees the command's quotes, & or |; the inner one parses it like a typed line (/s keeps it as given).
            info.Arguments = "/d /v:on /s /c \"chcp 65001 >nul & cmd /d /s /c !MARTLET_TERMINAL_COMMAND!\"";
            info.Environment["MARTLET_TERMINAL_COMMAND"] = $"\"{command}\"";
            return info;
        }
        // An encoded command needs no quoting. Progress bars are left out, and output is UTF-8 like the pipe reads it.
        var script = "$ProgressPreference = 'SilentlyContinue'\n[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)\n" + command;
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            info.ArgumentList.Add(argument);
        return info;
    }

    private static void Stop(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException) { }
    }

    // Appends whole lines where it can, so output and errors interleave line by line.
    private static Task Pump(StreamReader reader, TerminalOutput output) => Task.Run(async () =>
    {
        var buffer = new char[4096];
        var pending = new StringBuilder();
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                pending.Append(buffer, 0, read);
                var text = pending.ToString();
                var end = text.LastIndexOf('\n');
                if (end >= 0)
                {
                    output.Append(text[..(end + 1)]);
                    pending.Clear().Append(text, end + 1, text.Length - end - 1);
                }
                else if (pending.Length >= buffer.Length)
                {
                    output.Append(text);
                    pending.Clear();
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
        if (pending.Length > 0) output.Append(pending.ToString());
    });

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(\x07|\x1B\\)", RegexOptions.CultureInvariant)]
    private static partial Regex Escapes();

    /// <summary>Output without terminal color codes, with one kind of line break.</summary>
    internal static string Clean(string text) => Escapes().Replace(text, "").Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>The first <see cref="HeadCharacters"/> and last <see cref="TailCharacters"/> of what the command printed.</summary>
    private sealed class TerminalOutput
    {
        private readonly object gate = new();
        private readonly StringBuilder head = new(), tail = new();
        private long total;
        private bool closed;

        internal void Append(string text)
        {
            lock (gate)
            {
                if (closed || text.Length == 0) return;
                total += text.Length;
                var room = HeadCharacters - head.Length;
                if (room > 0)
                {
                    var take = Math.Min(room, text.Length);
                    if (take < text.Length && char.IsHighSurrogate(text[take - 1])) take--;
                    head.Append(text, 0, take);
                    text = text[take..];
                }
                if (text.Length == 0) return;
                tail.Append(text);
                if (tail.Length > TailCharacters * 2) Trim(tail, TailCharacters);
            }
        }

        internal (string Text, bool Cut) Close()
        {
            lock (gate)
            {
                closed = true;
                // The tail is only trimmed past twice its size, so short output is all still here.
                if (total <= HeadCharacters + TailCharacters) return (Clean(head.ToString() + tail), false);
                Trim(tail, TailCharacters);
                var left = total - head.Length - tail.Length;
                return (Clean(head.ToString()).TrimEnd('\n') + $"\n[... {left:N0} characters left out ...]\n" + Clean(tail.ToString()), true);
            }
        }

        private static void Trim(StringBuilder text, int keep)
        {
            if (text.Length <= keep) return;
            var start = text.Length - keep;
            if (char.IsLowSurrogate(text[start])) start++;
            text.Remove(0, start);
        }
    }
}

/// <summary>The terminal as one function the Thinking model may call.</summary>
public static class TerminalTool
{
    public const string Name = "run_terminal_command";
    private const int MaxDescriptionCharacters = 1024;

    public const string ParametersJson =
        "{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\",\"description\":\"The command to run, exactly as " +
        "typed at the prompt (at most 4000 characters).\"}},\"required\":[\"command\"],\"additionalProperties\":false}";

    /// <summary>The function's description for these settings. <paramref name="folder"/> replaces the start folder's path
    /// (status reports use a placeholder); a path too long for the description is left out.</summary>
    public static string Description(TerminalSettings settings, string? folder = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var start = folder ?? settings.StartFolder;
        var text = Text(start);
        return text.Length <= MaxDescriptionCharacters ? text : Text(settings.CustomFolder ? "the folder the user chose" : "the user's home folder");

        string Text(string where) =>
            $"Runs one command in {TerminalRunner.Name(settings.Shell)} on the user's Windows PC and returns its exit code and output. " +
            $"It runs as the user (never as administrator), hidden, in a new shell that starts in {where}; nothing carries over " +
            "between calls, so change folders inside the command. It can't take input, so avoid prompts and interactive programs, " +
            $"and it's stopped after {settings.TimeLimitSeconds} seconds. " +
            (settings.AskFirst ? "The user sees each command and approves or declines it before it runs. "
                : "It runs without asking the user first. ") +
            "Use it when the user asks for something a command can do or check on this PC. Prefer commands that only read; never " +
            "delete, overwrite, install or send anything unless the user clearly asked for exactly that. Tell the user the result " +
            "in a sentence or two; don't read long output aloud.";
    }
}
