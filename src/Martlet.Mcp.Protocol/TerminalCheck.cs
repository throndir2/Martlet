using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Martlet.Mcp.Client;

namespace Martlet.Mcp;

/// <summary>terminal_status reads Companion › Tools › Terminal from a data directory's terminal.json; terminal_check runs the
/// desktop's production <see cref="TerminalRunner"/> with fixed, harmless commands (never anything a model or the owner chose)
/// in a fresh temporary folder that is removed afterwards: UTF-8 output and the start folder, quotes and special characters,
/// errors with an exit code, closed
/// input, the time limit stopping the whole process tree, long output keeping its start and end, and what is refused before
/// anything runs. Local only; reads no credentials.</summary>
internal static class TerminalCheck
{
    private const string Marker = "martlet-terminal-check héllo ✓ 日本";
    private static readonly TimeSpan ShortLimit = TimeSpan.FromSeconds(2);

    internal static object Status(string directory)
    {
        var (settings, state) = TerminalSettings.Read(directory);
        return new
        {
            state,
            enabled = settings.Enabled,
            shell = settings.Shell.ToString(),
            shellName = TerminalRunner.Name(settings.Shell),
            shellInstalled = TerminalRunner.Find(settings.Shell) is not null,
            installedShells = Enum.GetValues<TerminalShell>().Where(s => TerminalRunner.Find(s) is not null).Select(s => s.ToString()).ToArray(),
            askFirst = settings.AskFirst,
            timeLimitSeconds = settings.TimeLimitSeconds,
            startFolder = settings.CustomFolder ? "chosen" : "home",
            startFolderExists = Directory.Exists(settings.StartFolder),
            tool = Tool(settings)
        };
    }

    internal static async Task<object> RunAsync(string directory, string? shellName, CancellationToken cancellationToken)
    {
        var (saved, state) = TerminalSettings.Read(directory);
        var shell = saved.Shell;
        if (shellName is not null && !Enum.TryParse(shellName, ignoreCase: false, out shell))
            throw new ArgumentException($"Unknown shell '{shellName}'. Use {string.Join(", ", Enum.GetNames<TerminalShell>())}.");
        var name = TerminalRunner.Name(shell);
        if (TerminalRunner.Find(shell) is null)
            return new { ok = false, shell = shell.ToString(), shellName = name, installed = false, problem = $"{name} isn't installed on this PC." };

        var folder = Directory.CreateTempSubdirectory("Martlet.TerminalCheck.").FullName;
        var settings = saved with { Shell = shell, Folder = folder };
        var steps = new List<object>();
        var passed = true;
        void Step(string step, bool ok, object detail)
        {
            steps.Add(new { name = step, ok, detail });
            passed &= ok;
        }
        var cmd = shell == TerminalShell.CommandPrompt;
        try
        {
            var output = await Run(cmd ? $"echo {Marker}& cd" : $"Write-Output '{Marker}'; (Get-Location).Path").ConfigureAwait(false);
            Step("output", output.Succeeded && output.Output.Contains(Marker, StringComparison.Ordinal) &&
                output.Output.Contains(Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase),
                new { output.ExitCode, utf8 = output.Output.Contains(Marker, StringComparison.Ordinal),
                    startedInFolder = output.Output.Contains(Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase),
                    seconds = Seconds(output), report = TerminalRunner.Report(output, settings).Replace(folder, "{folder}", StringComparison.OrdinalIgnoreCase) });

            var special = await Run(cmd ? "echo \"a&b|c\" & echo it's done" : "Write-Output \"a&b|c\"; Write-Output 'it''s done'").ConfigureAwait(false);
            Step("special-characters", special.Succeeded && special.Output.Contains("a&b|c", StringComparison.Ordinal) &&
                special.Output.Contains("it's done", StringComparison.Ordinal),
                new { special.ExitCode, output = special.Output });

            var errors = await Run(cmd ? "echo martlet-error 1>&2 & exit /b 3" : "[Console]::Error.WriteLine('martlet-error'); exit 3").ConfigureAwait(false);
            Step("errors", errors.ExitCode == 3 && !errors.Succeeded && errors.Output.Contains("martlet-error", StringComparison.Ordinal),
                new { errors.ExitCode, stderrKept = errors.Output.Contains("martlet-error", StringComparison.Ordinal), seconds = Seconds(errors) });

            var input = await Run(cmd ? "set /p answer=Type something: & echo martlet-done" : "$typed = [Console]::In.ReadToEnd(); \"martlet-done $($typed.Length)\"",
                TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            Step("input-closed", !input.TimedOut && input.Output.Contains("martlet-done", StringComparison.Ordinal),
                new { input.ExitCode, input.TimedOut, seconds = Seconds(input) });

            var started = DateTime.Now;
            var slow = await Run(cmd ? "ping -n 30 127.0.0.1 >nul" : "& ping.exe -n 30 127.0.0.1 | Out-Null", ShortLimit).ConfigureAwait(false);
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            var leftRunning = PingsSince(started);
            Step("time-limit", slow.TimedOut && slow.ExitCode is null && slow.Elapsed < ShortLimit + TimeSpan.FromSeconds(4) && leftRunning == 0,
                new { slow.TimedOut, limitSeconds = ShortLimit.TotalSeconds, seconds = Seconds(slow), childProcessesLeft = leftRunning,
                    report = TerminalRunner.Report(slow, settings with { TimeLimitSeconds = (int)ShortLimit.TotalSeconds }) });

            var flood = await Run(cmd ? "for /l %i in (1,1,20000) do @echo line %i" : "1..20000 | ForEach-Object { \"line $_\" }",
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            Step("long-output", flood.Succeeded && flood.Cut && flood.Output.StartsWith("line 1\n", StringComparison.Ordinal) &&
                flood.Output.TrimEnd().EndsWith("line 20000", StringComparison.Ordinal) && flood.Output.Contains("characters left out", StringComparison.Ordinal) &&
                flood.Output.Length <= 11_000,
                new { flood.ExitCode, flood.Cut, characters = flood.Output.Length, seconds = Seconds(flood) });

            var empty = TerminalRunner.Check(shell, "  ");
            var multiLine = TerminalRunner.Check(TerminalShell.CommandPrompt, "echo one\necho two");
            var tooLong = TerminalRunner.Check(shell, new string('x', TerminalRunner.MaxCommandCharacters + 1));
            Step("refused", empty is not null && multiLine is not null && tooLong is not null,
                new { empty, commandPromptMultiLine = multiLine, tooLong });

            // A program the command starts that keeps running (and holds the output open) doesn't hold up the reply: the run ends
            // with the shell, and the program (a 3-second loopback ping) carries on by itself.
            var detachedAt = DateTime.Now;
            var detached = await Run(cmd ? "start /b ping -n 4 127.0.0.1 >nul & echo martlet-started"
                : "Start-Process -NoNewWindow ping.exe -ArgumentList '-n','4','127.0.0.1'; 'martlet-started'", TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            var stillRunning = PingsSince(detachedAt);
            for (var wait = 0; wait < 30 && PingsSince(detachedAt) > 0; wait++) await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            Step("program-outlives-shell", detached.Succeeded && detached.Output.Contains("martlet-started", StringComparison.Ordinal) &&
                detached.Elapsed < TimeSpan.FromSeconds(3.5) && stillRunning == 1,
                new { detached.ExitCode, detached.TimedOut, seconds = Seconds(detached), programKeptRunning = stillRunning == 1 });
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return new
        {
            ok = passed, shell = shell.ToString(), shellName = name, installed = true, savedState = state, savedEnabled = saved.Enabled,
            steps, tool = Tool(settings with { Folder = saved.Folder })
        };

        Task<TerminalRun> Run(string command, TimeSpan? limit = null) => TerminalRunner.RunAsync(settings, command, cancellationToken, limit);
    }

    // The function as the Thinking model gets it, with the start folder's path left out.
    private static object Tool(TerminalSettings settings) => new
    {
        name = TerminalTool.Name,
        description = TerminalTool.Description(settings, "{folder}"),
        parameters = JsonNode.Parse(TerminalTool.ParametersJson)
    };

    private static double Seconds(TerminalRun run) => Math.Round(run.Elapsed.TotalSeconds, 2);

    private static int PingsSince(DateTime started)
    {
        var count = 0;
        foreach (var process in Process.GetProcessesByName("PING"))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited && process.StartTime >= started.AddSeconds(-1)) count++;
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return count;
    }
}
