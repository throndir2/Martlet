using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Installation;

namespace Martlet.Desktop;

/// <summary>Drives martlet-host on this PC's Docker Desktop without a console: starts Docker, builds the host image,
/// runs setup, roles, status and updates unattended (--yes) and pairs desktops, streaming output to a
/// <see cref="HostRunWindow"/>. The owner's click in Martlet is the confirmation, as for SSH hosts (<see cref="HostRemote"/>).</summary>
internal static partial class HostLocal
{
    [GeneratedRegex(@"martlet-pair-v1\.[A-Za-z0-9_-]+")]
    private static partial Regex PairingCodePattern();

    internal static string Docker
    {
        get
        {
            var bundled = Path.Combine(Path.GetDirectoryName(MachineInfo.DockerDesktopPath)!, "resources", "bin", "docker.exe");
            return File.Exists(bundled) ? bundled : "docker";
        }
    }

    /// <summary>One "docker info" call is cut off after this long: Docker Desktop holds requests while its engine starts
    /// (or can't), so an unbounded call can wait forever with nothing to show.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(30);

    /// <summary>One check of Docker Desktop's engine: its version when it answered, otherwise the reason (first useful
    /// output line or the timeout), whether Docker Desktop said it is unable to start, and the Windows check its engine
    /// failed when it started (<see cref="DockerDesktopStatus.Precondition"/>), when it said so.</summary>
    internal sealed record EngineProbe(bool Answered, string? Version, string? Error, bool UnableToStart, string? Precondition = null)
    {
        /// <summary>Docker Desktop gave up starting its engine; it doesn't try again until it is restarted.</summary>
        internal bool Failed => UnableToStart || Precondition is not null;
    }

    /// <summary>Starts Docker Desktop when needed and waits (up to ten minutes) until its engine answers, showing each check,
    /// what Docker Desktop reports and its own warnings and errors in the run window. When the engine doesn't answer it first
    /// makes sure Windows can run it (virtualization and WSL 2, <see cref="WindowsVirtualizationSetup"/>): what is off gets
    /// turned on, and when Windows must restart, <paramref name="resume"/> continues after the next sign-in. A Docker Desktop
    /// that was already open while Windows changed is restarted (it doesn't notice new WSL by itself), and so is one that
    /// gave up starting (unable to start, or "Virtualization support not detected") although Windows is ready: it checked
        /// Windows too early, before the restart or while Windows was still starting. When Docker Desktop says WSL or Virtual
        /// Machine Platform is missing and Martlet's own Windows check couldn't read it (for example it timed out), Docker
        /// Desktop's word counts and Windows is set up. Stops when it gives up again after that.</summary>
    internal static async Task EnsureDockerAsync(HostRunWindow run, ContinueSetupKind resume)
    {
        Action<string> status = run.Status;
        var (output, token) = (run.Output, run.Token);
        if (!MachineInfo.DockerDesktopInstalled())
            throw new InvalidOperationException("Install Docker Desktop, open it once, then try again.");
        status("Checking Docker Desktop...");
        output.Report("Checking Docker Desktop...");
        var desktop = new DockerDesktopLog();
        var probe = await ProbeEngineAsync(token);
        var windowsReady = false;
        if (!probe.Answered)
        {
            if (probe.Failed) output.Report("Docker Desktop reported: " + probe.Error);
            // What Docker Desktop's last start said is missing (its previous session when it isn't open): it stands in for
            // Windows facts Martlet can't read, so a slow Windows check can't hide "wsl is not installed".
            var check = probe.Precondition ?? await DockerDesktopStatus.ReadPreconditionAsync(token);
            var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var restarted = false;
            while (true)
            {
                if (check is not null) handled.Add(check);
                // Docker Desktop's WSL 2 engine can't start until Windows' virtualization is on.
                var windows = await WindowsVirtualizationSetup.EnsureReadyAsync(run, resume, check);
                windowsReady = windows.Ready;
                if (windows.Changed)
                {
                    desktop.Restarted();
                    restarted = await RestartDockerDesktopAsync(output, token);
                }
                else if (probe.Failed && MachineInfo.DockerDesktopRunning())
                {
                    output.Report(windows.Ready
                        ? "Martlet finds nothing missing in Windows, so Docker Desktop checked too early. Restarting Docker Desktop..."
                        : "Docker Desktop gave up starting. Restarting Docker Desktop once in case it checked Windows too early...");
                    desktop.Restarted();
                    restarted = await RestartDockerDesktopAsync(output, token);
                }
                if (windows.Changed || restarted) probe = probe with { UnableToStart = false, Precondition = null };
                probe = await WaitForEngineAsync(run, probe, desktop, restarted, windows.Ready,
                    failed => !handled.Contains(failed) && WindowsVirtualization.WindowsCanFix(failed));
                if (!probe.Answered && probe.Precondition is { } next && !handled.Contains(next) && WindowsVirtualization.WindowsCanFix(next))
                {
                    output.Report($"Docker Desktop says \"{next}\". Martlet sets up Windows for it.");
                    check = next;
                    continue;
                }
                break;
            }
        }
        if (probe.Failed)
        {
            if (probe.Error is not null) output.Report("Docker Desktop reported: " + probe.Error);
            await desktop.ReportAsync(output, token);
            throw new InvalidOperationException(probe.Precondition is { } check
                ? windowsReady
                    ? $"Docker Desktop still says \"{check}\", although Martlet finds nothing missing in Windows. Restart Windows, then try " +
                      "again. If Docker Desktop keeps saying so, check that Task Manager > Performance > CPU shows Virtualization: Enabled."
                    : $"Docker Desktop still says \"{check}\". Restart Windows, then try again; Martlet checks Windows again and sets up what is missing."
                : "Docker Desktop couldn't start. Open Docker Desktop Troubleshoot or restart Windows, then try again.");
        }
        HostSetupResume.Clear();
        output.Report("Docker Desktop is running.");
    }

    /// <summary>Restarts Docker Desktop when it is open (after Windows was changed for it, or while its engine stays
    /// stopped), waiting up to three minutes; returns whether it asked. Older Docker Desktops without "docker desktop" are
    /// left as they are.</summary>
    private static async Task<bool> RestartDockerDesktopAsync(IProgress<string> output, CancellationToken token)
    {
        if (!MachineInfo.DockerDesktopRunning()) return false;
        output.Report("Restarting Docker Desktop...");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromMinutes(3));
        try { await RunAsync(["desktop", "restart"], output, limit.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { output.Report("Docker Desktop is still restarting."); }
        return true;
    }

    /// <summary>Starts Docker Desktop unless it is already running, then checks its engine every few seconds until it answers;
    /// every 30 seconds (or when the reason changes) shows the reason and Docker Desktop's own new messages. Windows is
    /// ready by now, so when Docker Desktop gives up starting (unable to start, or its start check failed: "Virtualization
    /// support not detected"), it checked Windows too early and is restarted once; and when it is open but reports its engine
    /// stopped at two checks in a row, it is restarted once too (unless <paramref name="restarted"/>). A failed check that
    /// <paramref name="windowsFix"/> accepts (WSL or Virtual Machine Platform missing) is returned at once, for Martlet to
    /// set Windows up instead of restarting Docker Desktop. Returns the failed check when it gives up again after a restart.
    /// Throws after ten minutes.</summary>
    private static async Task<EngineProbe> WaitForEngineAsync(HostRunWindow run, EngineProbe probe, DockerDesktopLog desktop, bool restarted,
        bool windowsReady, Func<string, bool> windowsFix)
    {
        Action<string> status = run.Status;
        var (output, token) = (run.Output, run.Token);
        const string Waiting = "Waiting for Docker Desktop to start. Accept Docker's terms if it asks.";
        output.Report("Docker Desktop isn't ready yet: " + probe.Error);
        if (!MachineInfo.DockerDesktopRunning())
        {
            output.Report("Starting Docker Desktop...");
            // Until the new session logs, "docker desktop logs --boot 0" still shows the last one, whose failure is old.
            desktop.Restarted();
            Process.Start(new ProcessStartInfo(MachineInfo.DockerDesktopPath) { UseShellExecute = true })?.Dispose();
        }
        await desktop.ReportAsync(output, token);
        status(Waiting);
        var started = DateTime.UtcNow;
        var reported = started;
        var reason = probe.Error;
        var stopped = desktop.State == DockerDesktopStatus.Stopped;
        while (true)
        {
            var waited = DateTime.UtcNow - started;
            if (waited >= StartTimeout)
            {
                await desktop.ReportAsync(output, token);
                throw new InvalidOperationException("Docker Desktop didn't start within ten minutes. Restart Windows, open Docker Desktop and " +
                    "wait until it says it's running, then try again.");
            }
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            probe = await ProbeEngineAsync(token);
            if (probe.Answered) return probe;
            var report = probe.Failed || probe.Error != reason || DateTime.UtcNow - reported >= ProgressInterval;
            if (report)
            {
                if (!probe.Failed) output.Report($"Still waiting for Docker Desktop ({DateTime.UtcNow - started:m\\:ss}): {probe.Error}");
                await desktop.ReportAsync(output, token);
                reported = DateTime.UtcNow;
                reason = probe.Error;
            }
            if (probe.Failed || desktop.Precondition is not null)
            {
                probe = probe with { Precondition = probe.Precondition ?? desktop.Precondition };
                if (restarted || probe.Precondition is { } fixable && windowsFix(fixable)) return probe;
                output.Report((probe.Precondition is { } check ? $"Docker Desktop says \"{check}\"" : $"Docker Desktop says it is unable to start ({probe.Error})") +
                    (windowsReady
                        ? ", but Martlet finds nothing missing in Windows: Docker Desktop checked too early (before Windows restarted, or while it was still starting)."
                        : ". Restarting Docker Desktop once in case it checked Windows too early."));
                status("Restarting Docker Desktop...");
                // Taken before the restart: "docker desktop restart" waits while the new session runs its start check.
                desktop.Restarted();
                if (!await RestartDockerDesktopAsync(output, token)) return probe;
                restarted = true;
                status(Waiting);
                stopped = false;
                continue;
            }
            if (!report) continue;
            var stillStopped = desktop.State == DockerDesktopStatus.Stopped;
            if (stillStopped && stopped && !restarted)
            {
                output.Report("Docker Desktop is open but its engine stays stopped.");
                status("Restarting Docker Desktop...");
                desktop.Restarted();
                restarted = await RestartDockerDesktopAsync(output, token);
                status(Waiting);
                stillStopped = false;
            }
            stopped = stillStopped;
        }
    }

    [GeneratedRegex(@"\A\d+(\.\d+)+")]
    private static partial Regex VersionPattern();

    /// <summary>Asks Docker Desktop's engine for its version once, for at most 15 seconds.</summary>
    internal static async Task<EngineProbe> ProbeEngineAsync(CancellationToken token)
    {
        var (exit, lines) = await CaptureAsync(["info", "--format", "{{.ServerVersion}}"], token);
        if (exit is null)
            return new(false, null, $"Docker Desktop didn't answer within {ProbeTimeout.TotalSeconds:0} seconds.", false);
        if (exit == 0 && lines.FirstOrDefault(line => VersionPattern().IsMatch(line)) is { } version) return new(true, version, null, false);
        var error = lines.FirstOrDefault(line => line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("daemon", StringComparison.OrdinalIgnoreCase) || line.Contains("docker", StringComparison.OrdinalIgnoreCase))
            ?? lines.FirstOrDefault() ?? $"docker info exited with {exit} and printed nothing.";
        return new(false, null, Shorten(error),
            lines.Any(line => line.Contains("unable to start", StringComparison.OrdinalIgnoreCase)),
            DockerDesktopStatus.LastPrecondition(lines, DateTimeOffset.MinValue));
    }

    /// <summary>Runs docker for at most 15 seconds and collects its output lines; the exit code is null when it was cut off.</summary>
    private static async Task<(int? Exit, IReadOnlyList<string> Lines)> CaptureAsync(IEnumerable<string> args, CancellationToken token)
    {
        var lines = new List<string>();
        var sink = new LineSink(line => { lock (lines) lines.Add(line); });
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(ProbeTimeout);
        int? exit;
        try { exit = await RunAsync(args, sink, limit.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { exit = null; }
        lock (lines) return (exit, lines.ToArray());
    }

    private static string Shorten(string text) => text.Length <= 400 ? text : text[..400] + "...";

    [GeneratedRegex(@"\[\d{4}-\d\d-\d\dT[^\]]*\]")]
    private static partial Regex TimestampPattern();

    /// <summary>Docker Desktop's own view of why its engine isn't answering, for the run window: its status
    /// ("docker desktop status") and the warnings and errors of its current run ("docker desktop logs"), each message
    /// once, and the Windows check its engine failed when it started (<see cref="Precondition"/>). Read locally from Docker
    /// Desktop; nothing is sent anywhere.</summary>
    private sealed class DockerDesktopLog
    {
        private const int MaximumLines = 12;
        private readonly HashSet<string> shown = new(StringComparer.Ordinal);
        private bool unavailable;
        private DateTimeOffset since = DateTimeOffset.MinValue;

        /// <summary>What "docker desktop status" said at the last report (for example running, starting or stopped), or why
        /// it didn't answer.</summary>
        internal string? State { get; private set; }

        /// <summary>The Windows check Docker Desktop's engine failed since it last (re)started, as of the last report (for
        /// example "Virtual Machine Platform not enabled": its window says "Virtualization support not detected"), or null.</summary>
        internal string? Precondition { get; private set; }

        /// <summary>Martlet is about to start or restart Docker Desktop: what it logged before no longer counts as its current state.</summary>
        internal void Restarted()
        {
            since = DateTimeOffset.UtcNow;
            Precondition = null;
        }

        internal async Task ReportAsync(IProgress<string> output, CancellationToken token)
        {
            if (unavailable) return;
            var (exit, lines) = await CaptureAsync(["desktop", "status", "--format", "json"], token);
            if (lines.Any(line => line.Contains("not a docker command", StringComparison.OrdinalIgnoreCase)))
            {
                unavailable = true;
                State = null;
                output.Report("Docker Desktop can't show status here. Open Docker Desktop for details.");
                return;
            }
            var now = exit == 0 ? DockerDesktopStatus.Parse(string.Join('\n', lines)) : exit is null ? "no answer" : Shorten(lines.FirstOrDefault() ?? $"exit {exit}");
            if (now is not null && now != State) output.Report("Docker Desktop reports: " + now);
            State = now;
            (exit, lines) = await CaptureAsync(["desktop", "logs", "--boot", "0", "--priority", "1", "--no-color"], token);
            if (exit != 0) return;
            Precondition = DockerDesktopStatus.LastPrecondition(lines, since);
            var fresh = lines.Where(line => !line.Contains(".analytics", StringComparison.Ordinal))
                .Select(line => Shorten(TimestampPattern().Replace(line, "").Trim()))
                .Where(line => line.Length > 0 && shown.Add(line)).ToList();
            foreach (var line in fresh.TakeLast(MaximumLines)) output.Report("Docker Desktop: " + line);
        }
    }

    /// <summary>winget (App Installer), or null when this PC does not have it.</summary>
    internal static string? Winget()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(alias)) return alias;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), "winget.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>Installs Docker Desktop (WSL 2 based) with winget, without a console: Docker's own installer and Windows'
    /// administrator prompt appear; the output streams into the run window. The owner already accepted the terms in Martlet.</summary>
    internal static async Task InstallDockerDesktopAsync(Action<string> status, IProgress<string> output, CancellationToken token)
    {
        var winget = Winget() ?? throw new InvalidOperationException(
            "App Installer isn't available on this PC, so Martlet can't install Docker Desktop. " +
            "Install Docker Desktop from docker.com, then try again.");
        status("Installing Docker Desktop. Windows asks for administrator approval; this can take several minutes...");
        string[] args = ["install", "--exact", "--id", "Docker.DockerDesktop", "--source", "winget",
            "--accept-package-agreements", "--accept-source-agreements"];
        output.Report("$ winget " + string.Join(' ', args));
        var exit = await LocalProcess.RunAsync(winget, args, output, token);
        if (!MachineInfo.DockerDesktopInstalled())
            throw new InvalidOperationException("Docker Desktop wasn't installed. The output shows why.");
        output.Report("Docker Desktop is installed.");
    }

    /// <summary>Builds martlet-host:&lt;version&gt; from this version's source (falling back to main) unless it exists.</summary>
    internal static async Task EnsureImageAsync(HostSetupTarget target, Action<string> status, IProgress<string> output, CancellationToken token)
    {
        var image = HostSetupCommands.Image(target);
        if (await RunAsync(["image", "inspect", image], null, token) == 0) return;
        status("Preparing Martlet host. The first time can take a few minutes...");
        foreach (var reference in new[] { "v" + target.Version, "main" })
        {
            output.Report($"$ docker build -t {image} -f deploy/host/Dockerfile {HostSetupCommands.Repository}#{reference}");
            if (await RunAsync(["build", "-t", image, "-f", "deploy/host/Dockerfile", $"{HostSetupCommands.Repository}#{reference}"],
                    output, token) == 0)
                return;
        }
        throw new InvalidOperationException("Couldn't prepare the Martlet host. The output shows why.");
    }

    /// <summary>Runs one martlet-host command unattended in a container on this PC; returns its exit code.
    /// <paramref name="answers"/> are martlet-host answers (secret.&lt;name&gt;=..., choice.&lt;VAR&gt;=...) sent over stdin.
    /// The engine makes one change to this host at a time: a change waits for one already running (its output says what it
    /// waits for) unless <paramref name="waitForOtherChanges"/> is false, when it stops at once with
    /// <see cref="HostEngineBusy.ExitCode"/> and changes nothing.</summary>
    internal static Task<int> EngineAsync(HostSetupTarget target, IReadOnlyList<string> engine, IProgress<string> output,
        CancellationToken token, Task<string?>? moreInput = null, IReadOnlyDictionary<string, string>? answers = null,
        bool waitForOtherChanges = true)
    {
        HostSetupCommands.Validate(target, HostAction.Status);
        var setup = engine.Count > 0 && engine[0] == "setup";
        var args = new List<string> { "run", "--rm", "-i", "--log-driver", "none", "-u", "0", "-v", HostSetupCommands.DockerSocket };
        if (setup)
        {
            args.AddRange(["-e", "MARTLET_HOST_ADDRESS=" + target.Address]);
            if (target.HostId is { } id) args.AddRange(["-e", "MARTLET_HOST_ID=" + id]);
        }
        if (!waitForOtherChanges) args.AddRange(["-e", "MARTLET_LOCK_WAIT=0"]);
        args.Add(HostSetupCommands.Image(target));
        args.Add("--yes");
        args.AddRange(engine);
        output.Report("$ martlet-host " + string.Join(' ', engine) + "  (on this PC, Docker Desktop)");
        var input = string.Concat((answers ?? new Dictionary<string, string>()).Select(pair =>
        {
            if (pair.Value.Contains('\n') || pair.Value.Contains('\r')) throw new InvalidOperationException("Answers must be a single line.");
            return $"{pair.Key}={pair.Value}\n";
        })) + "end\n";
        return RunAsync(args, output, token, input, moreInput);
    }

    /// <summary>Pairs this desktop with this PC's host: runs "pair --device-id ... --name ...", reads the one-use code from
    /// its output (never shown), redeems it and lets the host restart its gateway.</summary>
    internal static async Task<(Audio2FaceHostPairing Pairing, string Secret)> PairAsync(HostSetupTarget target, string deviceId,
        string name, IProgress<string> output, CancellationToken token)
    {
        var label = new string(name.Where(c => c is >= ' ' and <= '~').Take(64).ToArray()).Trim();
        if (label.Length == 0 || label[0] == '-') label = "Martlet desktop";
        var withdraw = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<(Audio2FaceHostPairing, string)>? pairing = null;
        var sink = new LineSink(line =>
        {
            var match = PairingCodePattern().Match(line);
            if (!match.Success) { output.Report(line); return; }
            output.Report("pairing-code: (read by Martlet; not shown)");
            if (pairing is not null) return;
            output.Report($"Pairing this PC as {deviceId}...");
            pairing = RedeemAsync(match.Value);
        });

        async Task<(Audio2FaceHostPairing, string)> RedeemAsync(string code)
        {
            try { return await HostPairingCode.Parse(code).PairAsync(deviceId, token); }
            catch
            {
                withdraw.TrySetResult("cancel\n");
                throw;
            }
        }

        int exit;
        try { exit = await EngineAsync(target, ["pair", "--device-id", deviceId, "--name", label], sink, token, withdraw.Task); }
        finally { withdraw.TrySetResult(null); }
        if (pairing is null) throw new InvalidOperationException("This PC's host couldn't create a pairing code. See the output.");
        var result = await pairing;
        if (exit != 0) output.Report($"Paired, but the host reported exit {exit} while restarting. Check the host.");
        return result;
    }

    /// <summary>Reads a role's terms, secrets and choices from this PC's host engine (martlet-host describe).</summary>
    internal static async Task<HostRoleInputs> DescribeAsync(HostSetupTarget target, string role, IProgress<string> output,
        CancellationToken token)
    {
        var lines = new List<string>();
        var sink = new LineSink(line =>
        {
            if (line.StartsWith("role.", StringComparison.Ordinal)) lines.Add(line);
            else output.Report(line);
        });
        var exit = await EngineAsync(target, ["describe", role], sink, token);
        if (exit != 0 || lines.Count == 0)
            throw new InvalidOperationException($"Couldn't read {role} from this PC's host service. See the output.");
        return HostRemote.ParseRole(lines);
    }

    /// <summary>Lets another desktop (for example the main PC) pair with this PC's host: runs "pair", which shows this PC's
    /// address and a short one-use code; <paramref name="shown"/> receives both. The code never reaches
    /// <paramref name="output"/> (or the run log). The engine waits until that desktop types it (the code has no deadline);
    /// canceling withdraws the code.</summary>
    internal static async Task<int> PairOtherAsync(HostSetupTarget target, Action<string, string> shown,
        IProgress<string> output, CancellationToken token, string codeNote = "(shown above)")
    {
        var withdraw = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => withdraw.TrySetResult("cancel\n"));
        var sink = ShownCodeSink(HostSetupCommands.ThisPcAddress() ?? target.Address, shown, output, codeNote);
        try { return await EngineAsync(target, ["pair"], sink, token, withdraw.Task); }
        finally { withdraw.TrySetResult(null); }
    }

    /// <summary>Reads what "martlet-host pair" shows for another desktop (its "Address:" and "Code:" lines) and passes both to
    /// <paramref name="shown"/>; every other line goes to <paramref name="output"/>, the code only as <paramref name="codeNote"/>.</summary>
    internal static LineSink ShownCodeSink(string fallbackAddress, Action<string, string> shown, IProgress<string> output, string codeNote)
    {
        string? address = null;
        return new LineSink(line =>
        {
            if (ShownAddressPattern().Match(line) is { Success: true } where) address = where.Groups[1].Value;
            if (ShownCodePattern().Match(line) is { Success: true } code)
            {
                output.Report("    Code:     " + codeNote);
                shown(address ?? fallbackAddress, code.Groups[1].Value);
                return;
            }
            if (!line.Contains("martlet-pair-v1.", StringComparison.Ordinal)) output.Report(line);
        });
    }

    [GeneratedRegex(@"^\s*Address:\s+(\S+)\s*$")]
    private static partial Regex ShownAddressPattern();

    [GeneratedRegex(@"^\s*Code:\s+([2-9A-HJ-NP-Z]{4}-[2-9A-HJ-NP-Z]{4})\s*$")]
    private static partial Regex ShownCodePattern();

    private static Task<int> RunAsync(IEnumerable<string> args, IProgress<string>? output, CancellationToken token,
        string? input = null, Task<string?>? moreInput = null) =>
        LocalProcess.RunAsync(Docker, args, output, token, input, moreInput);
}