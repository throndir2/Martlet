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
/// confirmed. Two more install the update beside a disposable program folder first and then switch to it: one that
/// switches in seconds, and one whose program folder is still in use, so the whole installer runs instead. Touches no real
/// install, data directory or network.</summary>
internal static class AppUpdateCheck
{
    // The variables Martlet.NodeLinkCheck's UpdateHelperFixture reads.
    private const string RecordVariable = "MARTLET_UPDATE_CHECK_RECORD";
    private const string ExitVariable = "MARTLET_UPDATE_CHECK_EXIT";
    private const string Version = "9.9.9";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(90);

    private sealed record Scenario(string Name, bool Unattended, bool InTray, int Exit);

    private sealed record Started(DateTimeOffset At, int Pid, string[] Args, bool ConsoleWindowVisible, uint[] ConsoleProcesses,
        string? Priority = null, string? Cwd = null);

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
            await Task.WhenAll(
                CheckStagedAsync(root, fixture, "staged-switch", folderInUse: false, Step, cancellation),
                CheckStagedAsync(root, fixture, "staged-folder-in-use", folderInUse: true, Step, cancellation));
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
                    "shows no window, and what its /STAGE=prepare and /STAGE=finish steps install and register, are not observed here."
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

    /// <summary>An update installed beside the running Martlet (<see cref="AppUpdateStaging"/>), in a disposable program folder
    /// holding an old Desktop file and an uninstall folder. First the prepare step, as Martlet runs it while it keeps working:
    /// the stand-in installer gets /STAGE=prepare with the staging folder as /DIR, no window and idle priority, and the program
    /// folder is untouched. Then the helper, as Martlet starts it when it closes for the update: with
    /// <paramref name="folderInUse"/> false it switches folders, moves the uninstall folder over, runs only the finish step
    /// and starts Martlet again within seconds; with a file in the program folder held open, it gives up switching and runs
    /// the whole installer as before, leaving the program folder as it was for that installer.</summary>
    private static async Task CheckStagedAsync(string root, string fixture, string name, bool folderInUse,
        Action<string, bool, string> step, CancellationToken cancellation)
    {
        var data = Path.Combine(root, name);
        var updates = Path.Combine(data, "updates");
        var program = Path.Combine(data, "Programs", "Martlet");
        Directory.CreateDirectory(Path.Combine(program, AppUpdateStaging.DesktopFolder));
        Directory.CreateDirectory(Path.Combine(program, "uninstall"));
        var old = Path.Combine(program, AppUpdateStaging.DesktopFolder, "old-version.txt");
        File.WriteAllText(old, "FIXTURE: the running version.");
        File.WriteAllText(Path.Combine(program, "uninstall", "unins000.dat"), "FIXTURE: the uninstall log.");
        var record = Path.Combine(data, "started.jsonl");
        var environment = new Dictionary<string, string> { [RecordVariable] = record, [ExitVariable] = "0" };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(Limit);

        var code = await AppUpdateStaging.PrepareAsync(fixture, program, updates, limit.Token, environment);
        var prepare = Read(record).FirstOrDefault(s => s.Args.Contains(AppUpdateStaging.PrepareArgument));
        var staged = AppUpdateStaging.StagedRoot(program);
        step($"{name}: prepares-beside-martlet", code == 0 && prepare is not null && AppUpdateStaging.IsStaged(program) &&
                prepare.Args.Contains("/VERYSILENT") && prepare.Args.Contains("/DIR=" + staged) && prepare.Priority == "Idle" &&
                !prepare.ConsoleWindowVisible && File.Exists(old),
            prepare is null ? $"the installer's prepare step never started (exit {code})"
                : $"exit {code}; ran with {string.Join(' ', prepare.Args.Select(a => a.StartsWith("/LOG=", StringComparison.Ordinal) ? "/LOG=...\\stage.log" : a.StartsWith("/DIR=", StringComparison.Ordinal) ? "/DIR=<program folder>.next" : a))}; " +
                  $"priority {prepare.Priority}; console window visible: {prepare.ConsoleWindowVisible}; staged Desktop app: " +
                  $"{AppUpdateStaging.IsStaged(program)}; running version untouched: {File.Exists(old)}");

        using var martlet = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c ping -n 3 127.0.0.1 >NUL")
        {
            UseShellExecute = false, CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start the stand-in for Martlet.");
        var script = AppUpdateHelper.Script(updates, fixture, Version, martlet.Id, unattended: true, fixture,
            AppUpdateHelper.RelaunchArguments(data, AppUpdateHelper.AfterUpdateArgument), program, staged: true);
        // Something else still using a file in the program folder (a Doctor window, a tool server) stops the rename.
        using var held = folderInUse ? new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        using var helper = AppUpdateHelper.Start(updates, script, environment)
            ?? throw new InvalidOperationException("Could not start the update helper.");
        try
        {
            await martlet.WaitForExitAsync(limit.Token);
            await helper.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            helper.Kill(entireProcessTree: true);
            throw new TimeoutException($"The update helper ({name}) did not finish within {Limit.TotalSeconds:0} seconds.");
        }
        held?.Dispose();
        var exited = new DateTimeOffset(martlet.ExitTime).ToUniversalTime();
        var started = new List<Started>();
        for (var wait = 0; wait < 50; wait++)
        {
            started = Read(record);
            if (started.Any(s => s.Args.Contains("--data-directory"))) break;
            await Task.Delay(200, limit.Token);
        }
        var installer = started.FirstOrDefault(s => s.Args.Contains("/SUPPRESSMSGBOXES") && !s.Args.Contains(AppUpdateStaging.PrepareArgument));
        var restarted = started.FirstOrDefault(s => s.Args.Contains("--data-directory"));
        var result = AppUpdateHelper.TakeResult(updates);
        var log = AppUpdateHelper.TakeLog(updates);
        var newDesktop = File.Exists(Path.Combine(program, AppUpdateStaging.DesktopFolder, AppUpdateStaging.DesktopExecutable));
        var oldDesktop = File.Exists(old);
        var uninstall = File.Exists(Path.Combine(program, "uninstall", "unins000.dat"));
        var leftovers = Directory.Exists(staged) || Directory.Exists(AppUpdateStaging.PreviousRoot(program));
        var away = restarted is null ? (double?)null : (restarted.At - exited).TotalSeconds;
        var logged = log.Count == 0 ? "update.log was empty" : $"update.log ({log.Count} lines): " + string.Join(" | ", log);
        if (!folderInUse)
        {
            step($"{name}: switches-folders", newDesktop && !oldDesktop && uninstall && !leftovers,
                $"program folder holds the staged version: {newDesktop}; old version gone: {!oldDesktop}; uninstall folder moved " +
                $"over: {uninstall}; staging and previous folders removed: {!leftovers}");
            step($"{name}: runs-only-finish-step", installer is not null && installer.At >= exited &&
                    installer.Args.Contains(AppUpdateStaging.FinishArgument) && installer.Args.Contains("/VERYSILENT"),
                installer is null ? "the installer's finish step never ran"
                    : $"ran {(installer.At - exited).TotalSeconds:0.0} s after Martlet exited with {AppUpdateStaging.FinishArgument} " +
                      $"/VERYSILENT: {installer.Args.Contains(AppUpdateStaging.FinishArgument) && installer.Args.Contains("/VERYSILENT")}");
            // A helper working inside the program folder (Martlet's own folder, which it would otherwise inherit) stops the rename.
            var fixtureFolder = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(fixture))!);
            bool Same(string? a, string b) => a is not null &&
                string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
            step($"{name}: works-outside-program-folder", Same(installer?.Cwd, updates) && Same(restarted?.Cwd, fixtureFolder),
                $"the helper (and the installer it runs) worked in the updates folder: {Same(installer?.Cwd, updates)}; Martlet " +
                $"started again in its own folder: {Same(restarted?.Cwd, fixtureFolder)}");
            step($"{name}: back-in-seconds", away is < 5 && result == ("0", Version) &&
                    log.Any(line => line.Contains("Switched to Martlet", StringComparison.Ordinal)),
                (away is { } seconds ? $"Martlet started again {seconds:0.0} s after it exited (the whole install took 11-23 s on " +
                    "this PC before)" : "Martlet was not started again") +
                $"; last-install.txt: {(result is { } r ? $"exit {r.Code}, version {r.Version}" : "none")}; {logged}");
        }
        else
            step($"{name}: falls-back-to-whole-install", oldDesktop && uninstall && !leftovers && installer is not null &&
                    !installer.Args.Contains(AppUpdateStaging.FinishArgument) && installer.Args.Contains("/VERYSILENT") &&
                    restarted is not null && result == ("0", Version) &&
                    log.Any(line => line.Contains("still in use", StringComparison.Ordinal)),
                $"program folder kept for the whole installer: {oldDesktop && uninstall}; installer ran " +
                $"{(installer is null ? "never" : installer.Args.Contains(AppUpdateStaging.FinishArgument) ? "only its finish step" : "whole")}; " +
                $"staging folder removed: {!Directory.Exists(staged)}; Martlet started again " +
                $"{(away is { } s ? $"{s:0.0} s after it exited" : "never")}; {logged}");
    }

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
