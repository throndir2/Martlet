using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Martlet.Avatar.Audio2Face.Remote;

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
    /// output line or the timeout) and whether Docker Desktop said it is unable to start.</summary>
    internal sealed record EngineProbe(bool Answered, string? Version, string? Error, bool UnableToStart);

    /// <summary>Starts Docker Desktop when needed and waits (up to ten minutes) until its engine answers, showing each check,
    /// what Docker Desktop reports and its own warnings and errors in the run window. Stops at once when Docker Desktop
    /// says it is unable to start.</summary>
    internal static async Task EnsureDockerAsync(Action<string> status, IProgress<string> output, CancellationToken token)
    {
        if (!MachineInfo.DockerDesktopInstalled())
            throw new InvalidOperationException("Docker Desktop isn't installed on this PC yet. Install it, start it once, then try again.");
        status("Checking Docker Desktop...");
        output.Report($"Checking Docker Desktop's engine (docker info, up to {ProbeTimeout.TotalSeconds:0} seconds)...");
        var desktop = new DockerDesktopLog();
        var probe = await ProbeEngineAsync(token);
        if (!probe.Answered && !probe.UnableToStart) probe = await WaitForEngineAsync(probe, desktop, status, output, token);
        if (probe.UnableToStart)
        {
            output.Report("Docker Desktop's engine answered: " + probe.Error);
            await desktop.ReportAsync(output, token);
            throw new InvalidOperationException("Docker Desktop reports that it is unable to start on this PC. Its messages above show why " +
                "(often virtualization or WSL 2 isn't turned on yet). Fix that in Docker Desktop, make sure it says it is running, then try again.");
        }
        output.Report($"Docker Desktop is running (engine {probe.Version}).");
    }

    /// <summary>Starts Docker Desktop unless it is already running, then checks its engine every few seconds until it answers
    /// or says it is unable to start; every 30 seconds (or when the reason changes) shows the reason and Docker Desktop's
    /// own new messages. Throws after ten minutes.</summary>
    private static async Task<EngineProbe> WaitForEngineAsync(EngineProbe probe, DockerDesktopLog desktop, Action<string> status,
        IProgress<string> output, CancellationToken token)
    {
        output.Report("Docker Desktop's engine isn't answering yet: " + probe.Error);
        if (!MachineInfo.DockerDesktopRunning())
        {
            output.Report("Starting Docker Desktop...");
            Process.Start(new ProcessStartInfo(MachineInfo.DockerDesktopPath) { UseShellExecute = true })?.Dispose();
        }
        await desktop.ReportAsync(output, token);
        status("Waiting for Docker Desktop to start. The first start can take a few minutes; accept Docker's terms if it asks, " +
            "and check Docker Desktop's window for errors.");
        var started = DateTime.UtcNow;
        var reported = started;
        var reason = probe.Error;
        while (true)
        {
            var waited = DateTime.UtcNow - started;
            if (waited >= StartTimeout)
            {
                await desktop.ReportAsync(output, token);
                throw new InvalidOperationException("Docker Desktop did not start within ten minutes. Its messages above show what it reported; " +
                    "open it, make sure it says it is running, then try again.");
            }
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            probe = await ProbeEngineAsync(token);
            if (probe.Answered || probe.UnableToStart) return probe;
            if (probe.Error == reason && DateTime.UtcNow - reported < ProgressInterval) continue;
            output.Report($"Still waiting for Docker Desktop ({DateTime.UtcNow - started:m\\:ss}): {probe.Error}");
            await desktop.ReportAsync(output, token);
            reported = DateTime.UtcNow;
            reason = probe.Error;
        }
    }

    [GeneratedRegex(@"\A\d+(\.\d+)+")]
    private static partial Regex VersionPattern();

    /// <summary>Asks Docker Desktop's engine for its version once, for at most 15 seconds.</summary>
    internal static async Task<EngineProbe> ProbeEngineAsync(CancellationToken token)
    {
        var (exit, lines) = await CaptureAsync(["info", "--format", "{{.ServerVersion}}"], token);
        if (exit is null)
            return new(false, null, $"docker info got no answer within {ProbeTimeout.TotalSeconds:0} seconds " +
                "(Docker Desktop is still starting, waiting for you in its window, or stuck).", false);
        if (exit == 0 && lines.FirstOrDefault(line => VersionPattern().IsMatch(line)) is { } version) return new(true, version, null, false);
        var error = lines.FirstOrDefault(line => line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("daemon", StringComparison.OrdinalIgnoreCase) || line.Contains("docker", StringComparison.OrdinalIgnoreCase))
            ?? lines.FirstOrDefault() ?? $"docker info exited with {exit} and printed nothing.";
        return new(false, null, Shorten(error),
            lines.Any(line => line.Contains("unable to start", StringComparison.OrdinalIgnoreCase)));
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
    /// once. Read locally from Docker Desktop; nothing is sent anywhere.</summary>
    private sealed class DockerDesktopLog
    {
        private const int MaximumLines = 12;
        private readonly HashSet<string> shown = new(StringComparer.Ordinal);
        private string? state;
        private bool unavailable;

        internal async Task ReportAsync(IProgress<string> output, CancellationToken token)
        {
            if (unavailable) return;
            var (exit, lines) = await CaptureAsync(["desktop", "status", "--format", "json"], token);
            if (lines.Any(line => line.Contains("not a docker command", StringComparison.OrdinalIgnoreCase)))
            {
                unavailable = true;
                output.Report(@"This Docker Desktop can't show its status or logs here; they are in %LOCALAPPDATA%\Docker\log\host.");
                return;
            }
            var now = exit == 0 ? Status(lines) : exit is null ? "no answer" : Shorten(lines.FirstOrDefault() ?? $"exit {exit}");
            if (now is not null && now != state) output.Report("Docker Desktop reports: " + now);
            state = now;
            (exit, lines) = await CaptureAsync(["desktop", "logs", "--boot", "0", "--priority", "1", "--no-color"], token);
            if (exit != 0) return;
            var fresh = lines.Where(line => !line.Contains(".analytics", StringComparison.Ordinal))
                .Select(line => Shorten(TimestampPattern().Replace(line, "").Trim()))
                .Where(line => line.Length > 0 && shown.Add(line)).ToList();
            foreach (var line in fresh.TakeLast(MaximumLines)) output.Report("Docker Desktop: " + line);
        }

        private static string? Status(IReadOnlyList<string> lines)
        {
            try
            {
                using var json = JsonDocument.Parse(string.Join('\n', lines));
                return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("Status", out var value) &&
                    value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            }
            catch (JsonException) { return null; }
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
            "winget (App Installer from the Microsoft Store) isn't available on this PC, so Martlet can't install Docker Desktop. " +
            "Install Docker Desktop from docker.com, then try again.");
        status("Installing Docker Desktop. Windows asks for administrator approval; this can take several minutes...");
        string[] args = ["install", "--exact", "--id", "Docker.DockerDesktop", "--source", "winget",
            "--accept-package-agreements", "--accept-source-agreements"];
        output.Report("$ winget " + string.Join(' ', args));
        var exit = await LocalProcess.RunAsync(winget, args, output, token);
        if (!MachineInfo.DockerDesktopInstalled())
            throw new InvalidOperationException($"Docker Desktop was not installed (winget exit {exit}). The output shows why.");
        output.Report("Docker Desktop is installed. If it asks you to restart or sign out, do that, then press the same button again.");
    }

    /// <summary>Builds martlet-host:&lt;version&gt; from this version's source (falling back to main) unless it exists.</summary>
    internal static async Task EnsureImageAsync(HostSetupTarget target, Action<string> status, IProgress<string> output, CancellationToken token)
    {
        var image = HostSetupCommands.Image(target);
        if (await RunAsync(["image", "inspect", image], null, token) == 0) return;
        status($"Building Martlet's host image {image}. The first time this takes a few minutes...");
        foreach (var reference in new[] { "v" + target.Version, "main" })
        {
            output.Report($"$ docker build -t {image} -f deploy/host/Dockerfile {HostSetupCommands.Repository}#{reference}");
            if (await RunAsync(["build", "-t", image, "-f", "deploy/host/Dockerfile", $"{HostSetupCommands.Repository}#{reference}"],
                    output, token) == 0)
                return;
        }
        throw new InvalidOperationException("The host image could not be built. The output shows why.");
    }

    /// <summary>Runs one martlet-host command unattended in a container on this PC; returns its exit code.
    /// <paramref name="answers"/> are martlet-host answers (secret.&lt;name&gt;=..., choice.&lt;VAR&gt;=...) sent over stdin.</summary>
    internal static Task<int> EngineAsync(HostSetupTarget target, IReadOnlyList<string> engine, IProgress<string> output,
        CancellationToken token, Task<string?>? moreInput = null, IReadOnlyDictionary<string, string>? answers = null)
    {
        HostSetupCommands.Validate(target, HostAction.Status);
        var setup = engine.Count > 0 && engine[0] == "setup";
        var args = new List<string> { "run", "--rm", "-i", "--log-driver", "none", "-u", "0", "-v", HostSetupCommands.DockerSocket };
        if (setup)
        {
            args.AddRange(["-e", "MARTLET_HOST_ADDRESS=" + target.Address]);
            if (target.HostId is { } id) args.AddRange(["-e", "MARTLET_HOST_ID=" + id]);
        }
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
        if (pairing is null) throw new InvalidOperationException($"This PC's host did not show a pairing code (exit {exit}). See the output.");
        var result = await pairing;
        if (exit != 0) output.Report($"Paired, but the host reported exit {exit} while restarting its gateway. Check the host.");
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
            throw new InvalidOperationException($"Could not read the {role} role from this PC's host service (exit {exit}). See the output.");
        return HostRemote.ParseRole(lines);
    }

    /// <summary>Lets another desktop (for example the main PC) pair with this PC's host: runs "pair --device-id ... --name ..."
    /// and shows its one-use code, which <paramref name="code"/> also receives (for the clipboard). The engine waits up to
    /// five minutes for that desktop to redeem it; canceling withdraws the code.</summary>
    internal static async Task<int> PairOtherAsync(HostSetupTarget target, string deviceId, string name, Action<string> code,
        IProgress<string> output, CancellationToken token)
    {
        if (!DeviceIdPattern().IsMatch(deviceId)) throw new InvalidOperationException("Enter the other PC's device ID (for example desktop-main-pc).");
        var label = new string(name.Where(c => c is >= ' ' and <= '~').Take(64).ToArray()).Trim();
        if (label.Length == 0 || label[0] == '-') label = "Martlet desktop";
        var withdraw = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => withdraw.TrySetResult("cancel\n"));
        var sink = new LineSink(line =>
        {
            if (PairingCodePattern().Match(line) is { Success: true } match) code(match.Value);
            output.Report(line);
        });
        try { return await EngineAsync(target, ["pair", "--device-id", deviceId, "--name", label], sink, token, withdraw.Task); }
        finally { withdraw.TrySetResult(null); }
    }

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")]
    private static partial Regex DeviceIdPattern();

    private static Task<int> RunAsync(IEnumerable<string> args, IProgress<string>? output, CancellationToken token,
        string? input = null, Task<string?>? moreInput = null) =>
        LocalProcess.RunAsync(Docker, args, output, token, input, moreInput);
}