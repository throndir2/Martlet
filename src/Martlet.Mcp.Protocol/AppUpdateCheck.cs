using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Martlet.Core.Installation;

namespace Martlet.Mcp;

/// <summary>app_update_check: runs the desktop's production update helper (<see cref="AppUpdateHelper"/>: the same script
/// and the same hidden start Martlet uses when it installs an update) end to end in a disposable folder. A windowless
/// process that exits after about two seconds stands in for Martlet, and Martlet.NodeLinkCheck stands in for the installer
/// and for the restarted Martlet (FIXTURE: it records how it was started and installs nothing). Three installs run side by
/// side: one another computer asked for, an automatic one from the notification area whose installer fails, and one you
/// confirmed. Touches no real install, data directory or network.</summary>
internal static class AppUpdateCheck
{
    // The variables Martlet.NodeLinkCheck's UpdateHelperFixture reads.
    private const string RecordVariable = "MARTLET_UPDATE_CHECK_RECORD";
    private const string ExitVariable = "MARTLET_UPDATE_CHECK_EXIT";
    private const string Version = "9.9.9";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(90);

    private sealed record Scenario(string Name, bool Unattended, bool InTray, int Exit);

    private sealed record Started(DateTimeOffset At, int Pid, string[] Args, bool ConsoleWindowVisible, uint[] ConsoleProcesses);

    private sealed record Outcome(string Data, int Helper, DateTimeOffset MartletExited, Started? Installer, Started? Restarted,
        (string Code, string Version)? Result, IReadOnlyList<string> Log, IReadOnlyList<string> InstallerLog);

    internal static async Task<object> RunAsync(string fixture, CancellationToken cancellation)
    {
        var root = Path.Combine(Path.GetTempPath(), "Martlet.AppUpdateCheck." + Guid.NewGuid().ToString("N"));
        var steps = new List<object>();
        var passed = true;
        void Step(string name, bool ok, string detail)
        {
            steps.Add(new { name, ok, detail });
            passed &= ok;
        }
        Scenario[] scenarios =
        [
            new("asked-by-another-computer", Unattended: true, InTray: false, Exit: 0),
            new("automatic-from-tray-fails", Unattended: true, InTray: true, Exit: 5),
            new("confirmed", Unattended: false, InTray: false, Exit: 0)
        ];
        try
        {
            var outcomes = await Task.WhenAll(scenarios.Select(scenario => RunScenarioAsync(root, fixture, scenario, cancellation)));
            foreach (var (scenario, outcome) in scenarios.Zip(outcomes)) Check(scenario, outcome, Step);
            CheckResume(Path.Combine(root, "resume", "updates"), Step);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return new
        {
            exitCode = passed ? 0 : 1,
            report = new
            {
                passed, total = steps.Count, steps,
                notCovered = "The real Inno Setup installer is not run (it would install Martlet on this PC), so that /VERYSILENT " +
                    "shows no window is Inno Setup's documented behavior, not observed here."
            }
        };
    }

    private static async Task<Outcome> RunScenarioAsync(string root, string fixture, Scenario scenario, CancellationToken cancellation)
    {
        var data = Path.Combine(root, scenario.Name);
        var updates = Path.Combine(data, "updates");
        Directory.CreateDirectory(updates);
        var record = Path.Combine(data, "started.jsonl");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(Limit);
        using var martlet = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c ping -n 3 127.0.0.1 >NUL")
        {
            UseShellExecute = false, CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start the stand-in for Martlet.");
        var flags = new List<string>();
        if (scenario.Unattended) flags.Add(AppUpdateHelper.AfterUpdateArgument);
        if (scenario.InTray) flags.Add("--tray");
        var script = AppUpdateHelper.Script(updates, fixture, Version, martlet.Id, scenario.Unattended, fixture,
            AppUpdateHelper.RelaunchArguments(data, [.. flags]));
        using var helper = AppUpdateHelper.Start(updates, script, new Dictionary<string, string>
        {
            [RecordVariable] = record,
            [ExitVariable] = scenario.Exit.ToString(CultureInfo.InvariantCulture)
        }) ?? throw new InvalidOperationException("Could not start the update helper.");
        try
        {
            await martlet.WaitForExitAsync(limit.Token);
            await helper.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            helper.Kill(entireProcessTree: true);
            throw new TimeoutException($"The update helper ({scenario.Name}) did not finish within {Limit.TotalSeconds:0} seconds.");
        }
        var exited = new DateTimeOffset(martlet.ExitTime).ToUniversalTime();
        // The restarted stand-in starts as the helper's last step; give it a moment to record itself.
        var started = new List<Started>();
        for (var wait = 0; wait < 50; wait++)
        {
            started = Read(record);
            if (started.Count >= 2) break;
            await Task.Delay(200, limit.Token);
        }
        return new Outcome(data, helper.Id, exited,
            started.FirstOrDefault(s => s.Args.Contains("/SUPPRESSMSGBOXES")),
            started.FirstOrDefault(s => s.Args.Contains("--data-directory")),
            AppUpdateHelper.TakeResult(updates), AppUpdateHelper.TakeLog(updates), AppUpdateHelper.InstallerLogTail(updates, 15));
    }

    private static List<Started> Read(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return File.ReadAllLines(path).Where(line => line.Length > 0)
                .Select(line => JsonSerializer.Deserialize<Started>(line, Json)!).ToList();
        }
        catch (Exception error) when (error is IOException or JsonException) { return []; }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The note Martlet leaves for itself as it closes to install (<see cref="AppUpdateResume"/>), so the restarted
    /// Martlet shows the character, listens and watches again: read once, only what was on, nothing when nothing was, a note
    /// from a Martlet that didn't know about watching still read, and ignored once it is older than an install takes.</summary>
    private static void CheckResume(string updates, Action<string, bool, string> step)
    {
        var now = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(updates);
        var path = Path.Combine(updates, AppUpdateResume.FileName);

        AppUpdateResume.Save(updates, character: true, listening: true, now, watching: true);
        var first = AppUpdateResume.Take(updates, now + TimeSpan.FromMinutes(2));
        var second = AppUpdateResume.Take(updates, now + TimeSpan.FromMinutes(2));
        step("resume: picks-up-character-and-listening-once", first == (true, true, true) && second is null && !File.Exists(path),
            $"two minutes after closing: {Describe(first)}; read again: {Describe(second)}; note left: {File.Exists(path)}");

        AppUpdateResume.Save(updates, character: true, listening: false, now);
        var character = AppUpdateResume.Take(updates, now + TimeSpan.FromMinutes(1));
        step("resume: only-what-was-on", character == (true, false, false), $"character showing, not listening or watching: {Describe(character)}");

        AppUpdateResume.Save(updates, character: false, listening: false, now, watching: true);
        var watching = AppUpdateResume.Take(updates, now + TimeSpan.FromMinutes(1));
        step("resume: watching-alone", watching == (false, false, true), $"only watching: {Describe(watching)}");

        // The Martlet being replaced may predate watching: its note has no watching field.
        File.WriteAllText(path, $"1 1 {now.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)}");
        var older = AppUpdateResume.Take(updates, now + TimeSpan.FromMinutes(1));
        step("resume: reads-note-without-watching", older == (true, true, false) && !File.Exists(path),
            $"a note written before watching was saved: {Describe(older)}");

        AppUpdateResume.Save(updates, character: false, listening: true, now);
        AppUpdateResume.Save(updates, character: false, listening: false, now);
        var nothing = AppUpdateResume.Take(updates, now);
        step("resume: nothing-on-leaves-no-note", nothing is null && !File.Exists(path),
            $"an earlier note is replaced by none: {Describe(nothing)}, note left: {File.Exists(path)}");

        AppUpdateResume.Save(updates, character: true, listening: true, now);
        var stale = AppUpdateResume.Take(updates, now + AppUpdateResume.MaximumAge + TimeSpan.FromMinutes(1));
        step("resume: stale-note-ignored", stale is null && !File.Exists(path),
            $"read {(AppUpdateResume.MaximumAge + TimeSpan.FromMinutes(1)).TotalMinutes:0} minutes later (Martlet started by you, not " +
            $"the update): {Describe(stale)}, note left: {File.Exists(path)}");

        static string Describe((bool Character, bool Listening, bool Watching)? resume) => resume is { } r
            ? $"character {(r.Character ? "on" : "off")}, listening {(r.Listening ? "on" : "off")}, watching {(r.Watching ? "on" : "off")}"
            : "nothing to pick up";
    }

    private static void Check(Scenario scenario, Outcome outcome, Action<string, bool, string> step)
    {
        var name = scenario.Name;
        var installer = outcome.Installer;
        var switches = installer is null ? Array.Empty<string>()
            : installer.Args.Select(arg => arg.StartsWith("/LOG=", StringComparison.Ordinal) ? "/LOG=...\\install.log" : arg).ToArray();
        step($"{name}: waits-for-martlet", installer is not null && installer.At >= outcome.MartletExited,
            installer is null ? "the installer never started"
                : $"the installer started {(installer.At - outcome.MartletExited).TotalSeconds:0.0} s after Martlet's process exited");
        var mode = scenario.Unattended
            ? installer?.Args.Contains("/VERYSILENT") == true && !installer.Args.Contains("/SILENT")
            : installer?.Args.Contains("/SILENT") == true && !installer.Args.Contains("/VERYSILENT");
        var common = installer is not null && new[] { "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/TASKS=" }.All(installer.Args.Contains) &&
            installer.Args.Any(arg => arg.StartsWith("/LOG=", StringComparison.Ordinal) && arg.EndsWith(AppUpdateHelper.InstallerLogFile, StringComparison.Ordinal));
        step($"{name}: installer-switches", mode && common,
            $"{(scenario.Unattended ? "unattended: no installer window (/VERYSILENT)" : "confirmed: progress window only (/SILENT)")}; ran with {string.Join(' ', switches)}");
        step($"{name}: helper-hidden", installer is not null && !installer.ConsoleWindowVisible && installer.ConsoleProcesses.Contains((uint)outcome.Helper),
            installer is null ? "the installer never started"
                : $"the installer ran in the helper's console (shared: {installer.ConsoleProcesses.Contains((uint)outcome.Helper)}), " +
                  $"which shows no window (visible: {installer.ConsoleWindowVisible})");
        var expected = new List<string> { "--data-directory", outcome.Data };
        if (scenario.Unattended) expected.Add(AppUpdateHelper.AfterUpdateArgument);
        if (scenario.InTray) expected.Add("--tray");
        var restarted = outcome.Restarted;
        step($"{name}: restarts", restarted is not null && restarted.Args.SequenceEqual(expected) && !restarted.ConsoleWindowVisible &&
                installer is not null && restarted.At >= installer.At,
            restarted is null ? "Martlet was not started again"
                : $"Martlet started again after the installer with --data-directory <its folder>{string.Concat(restarted.Args.Skip(2).Select(arg => " " + arg))} " +
                  $"(expected{string.Concat(expected.Skip(2).Select(arg => " " + arg))}), console window visible: {restarted.ConsoleWindowVisible}");
        step($"{name}: records-result", outcome.Result == (scenario.Exit.ToString(CultureInfo.InvariantCulture), Version),
            outcome.Result is { } result ? $"last-install.txt: exit {result.Code}, version {result.Version}" : "no last-install.txt");
        var log = outcome.Log;
        bool Logged(string text) => log.Any(line => line.Contains(text, StringComparison.Ordinal));
        var logged = Logged($"Waiting for Martlet, process ") &&
            Logged(scenario.Unattended ? "silently, with no installer window" : "with the installer's progress window") &&
            Logged($"exit code {scenario.Exit}.") && Logged("Starting Martlet again.");
        step($"{name}: logs-steps", logged, log.Count == 0 ? "update.log was empty" : $"update.log ({log.Count} lines): " + string.Join(" | ", log));
        if (scenario.Exit != 0)
            step($"{name}: installer-log-for-failure", outcome.InstallerLog.Any(line => line.Contains("FIXTURE", StringComparison.Ordinal)),
                $"install.log tail Martlet copies into its log after a failed install: {string.Join(" | ", outcome.InstallerLog)}");
    }
}
