using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Martlet.Core.Nodes;

namespace Martlet.Desktop;

/// <summary>How the desktop reaches the machine that becomes a Martlet host. Every method runs the same martlet-host engine.
/// <see cref="Agent"/> sends the commands through the host's paired gateway to the Martlet app on that computer, which runs
/// them there; <see cref="OnHost"/> is only the Add a computer wizard's install-by-hand choice (saved hosts use Agent).</summary>
internal enum HostSetupMethod { ThisPcDocker, SshDocker, SshNative, OnHost, Agent }

internal enum HostVerb { Setup, Pair, Add, Status, Remove, Update }

/// <summary>A martlet-host command. Add and Remove name the role (see <see cref="HostRoles"/>); every role uses the same flow.</summary>
internal sealed record HostAction(HostVerb Verb, string? Role = null)
{
    internal static readonly HostAction Setup = new(HostVerb.Setup);
    internal static readonly HostAction Pair = new(HostVerb.Pair);
    internal static readonly HostAction Status = new(HostVerb.Status);
    internal static readonly HostAction Update = new(HostVerb.Update);
    internal static HostAction Add(string role) => new(HostVerb.Add, role);
    internal static HostAction Remove(string role) => new(HostVerb.Remove, role);
}

internal sealed record HostSetupTarget(HostSetupMethod Method, string SshTarget, string Address, string? HostId, string Version);

/// <summary>Builds the exact commands each installation method runs; nothing here executes them.</summary>
internal static partial class HostSetupCommands
{
    internal const string Repository = HostCheckout.Repository;
    internal const string DockerSocket = "/var/run/docker.sock:/var/run/docker.sock";

    [GeneratedRegex(@"\A(?:[A-Za-z0-9._-]{1,64}@)?[A-Za-z0-9][A-Za-z0-9.-]{0,252}(?::[0-9]{1,5})?\z")]
    private static partial Regex SshTargetPattern();

    [GeneratedRegex(@"\A[0-9]{1,6}\.[0-9]{1,6}\.[0-9]{1,6}\z")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9-]{0,31}\z")]
    private static partial Regex RolePattern();

    internal static string Engine(HostAction action) => action.Verb switch
    {
        HostVerb.Setup => "setup",
        HostVerb.Pair => "pair",
        HostVerb.Add when action.Role is { } role && RolePattern().IsMatch(role) => $"add {role}",
        HostVerb.Status => "status",
        HostVerb.Remove when action.Role is { } role && RolePattern().IsMatch(role) => $"remove {role}",
        HostVerb.Update => "update",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    internal static void Validate(HostSetupTarget target, HostAction action)
    {
        _ = Engine(action);
        if (!VersionPattern().IsMatch(target.Version)) throw new InvalidOperationException("Unexpected Martlet version.");
        if (target.Method is HostSetupMethod.SshDocker or HostSetupMethod.SshNative && !SshTargetPattern().IsMatch(target.SshTarget))
            throw new InvalidOperationException("Enter the SSH target as user@computer.");
        if (action == HostAction.Setup && !IsPrivate(target.Address))
            throw new InvalidOperationException("Enter the host's private network address.");
        if (target.HostId is { } id && !Regex.IsMatch(id, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z"))
            throw new InvalidOperationException("Invalid host ID.");
    }

    internal static string Image(HostSetupTarget target) => $"martlet-host:{target.Version}";

    /// <summary>How a martlet-host command is run: in a console (humans), by ssh.exe in batch mode (keys only, never
    /// prompts; the fallback for background updates) or by Martlet's in-app SSH runner (<see cref="HostShell"/>).</summary>
    private enum ShellMode { Console, Batch, Runner }

    /// <summary>POSIX shell for a Docker host: build the image once from this version's source, then run the engine
    /// (interactively in a terminal; see <see cref="RemoteShell"/> for Martlet's unattended SSH runs). Unattended batch
    /// runs never prompt: no TTY and no sudo password, so they fail instead and the owner finishes from Martlet.</summary>
    internal static string DockerShell(HostSetupTarget target, HostAction action, bool attended = true) =>
        DockerShell(target, Engine(action), action == HostAction.Setup, attended ? ShellMode.Console : ShellMode.Batch, false);

    private static string DockerShell(HostSetupTarget target, string engine, bool setup, ShellMode mode, bool assumeYes, int lockWaitSeconds = 0)
    {
        var image = Image(target);
        var build = $"$D build -t {image} -f deploy/host/Dockerfile {Repository}#";
        var run = mode switch { ShellMode.Console => " -it", ShellMode.Runner => " -i --log-driver none", _ => "" };
        return $"D=docker; docker info >/dev/null 2>&1 || D='{(mode == ShellMode.Batch ? "sudo -n docker" : "sudo docker")}'; " +
            $"($D image inspect {image} >/dev/null 2>&1 || {build}v{target.Version} || {build}main) && " +
            $"$D run --rm{run} -u 0 -v {DockerSocket}{Environment(target, setup)}{LockWait(lockWaitSeconds)} {image} {(assumeYes ? "--yes " : "")}{engine}";
    }

    /// <summary>POSIX shell for a native Ubuntu host: keep a Martlet checkout in ~/Martlet and run its engine
    /// (<see cref="HostCheckout.Command"/>). An update checks out this desktop's release tag (falling back to main) before
    /// rebuilding the gateway from it. <paramref name="supplied"/>: the host has no internet access and Martlet sent it
    /// what it needs (<see cref="HostSupplier"/>).</summary>
    internal static string NativeShell(HostSetupTarget target, HostAction action) =>
        NativeShell(target, Engine(action), action == HostAction.Setup, ShellMode.Console, false);

    private static string NativeShell(HostSetupTarget target, string engine, bool setup, ShellMode mode, bool assumeYes, int lockWaitSeconds = 0,
        bool supplied = false)
    {
        var environment = (setup ? $"MARTLET_HOST_ADDRESS={target.Address} " : "") +
            (lockWaitSeconds > 0 ? $"MARTLET_LOCK_WAIT={lockWaitSeconds} " : "");
        var verb = engine.Split(' ')[0];
        // The runner keeps stdin for martlet-host's answers, so nothing before it may read it.
        return HostCheckout.Command((assumeYes ? "--yes " : "") + engine, environment, target.Version,
            refresh: verb is "setup" or "update", update: verb == "update", supplied, closeStdin: mode == ShellMode.Runner);
    }

    /// <summary>The POSIX shell Martlet's SSH runner (<see cref="HostShell"/>) executes for one martlet-host command
    /// (for example "status", "add audio2face" or "pair --device-id ... --name ..."): unattended (--yes, no terminal),
    /// with answers read from stdin. The owner's click in Martlet is the confirmation. <paramref name="lockWaitSeconds"/>
    /// above 0 lets a run without --yes wait that long for another change on the host (MARTLET_LOCK_WAIT).
    /// <paramref name="supplied"/>: a native host without internet access that Martlet sent its files to.</summary>
    internal static string RemoteShell(HostSetupTarget target, string engine, bool setup, bool assumeYes = true, int lockWaitSeconds = 0,
        bool supplied = false)
    {
        Validate(target, setup ? HostAction.Setup : HostAction.Status);
        return target.Method switch
        {
            HostSetupMethod.SshDocker => DockerShell(target, engine, setup, ShellMode.Runner, assumeYes, lockWaitSeconds),
            HostSetupMethod.SshNative => NativeShell(target, engine, setup, ShellMode.Runner, assumeYes, lockWaitSeconds, supplied),
            _ => throw new InvalidOperationException("Only SSH hosts run through Martlet's SSH runner.")
        };
    }

    private static string Environment(HostSetupTarget target, bool setup)
    {
        if (!setup) return "";
        var text = $" -e MARTLET_HOST_ADDRESS={target.Address}";
        return target.HostId is { } id ? text + $" -e MARTLET_HOST_ID={id}" : text;
    }

    /// <summary>docker run's option that lets the engine wait <paramref name="seconds"/> for another change on the host (empty: the
    /// engine's default, which for a run without a terminal or --yes is to stop at once).</summary>
    private static string LockWait(int seconds) => seconds > 0 ? $" -e MARTLET_LOCK_WAIT={seconds}" : "";

    /// <summary>The Windows command script equivalent of a this-PC action (Docker Desktop), shown as the command preview and
    /// for running by hand. Martlet itself runs these actions without any console (<see cref="HostLocal"/>).</summary>
    internal static string Script(HostSetupTarget target, HostAction action)
    {
        Validate(target, action);
        if (target.Method != HostSetupMethod.ThisPcDocker)
            throw new InvalidOperationException(target.Method is HostSetupMethod.OnHost or HostSetupMethod.Agent
                ? "Martlet on that computer runs its commands." : "Martlet runs SSH host commands for you.");
        var lines = new StringBuilder("@echo off\r\n");
        lines.Append($"title Martlet host - {Engine(action)}\r\n");
        var image = Image(target);
        var build = $"docker build -t {image} -f deploy/host/Dockerfile {Repository}#";
        lines.Append("echo Martlet host on this PC (Docker Desktop): ").Append(Engine(action)).Append("\r\n");
        lines.Append("where docker >NUL 2>&1 || (echo Docker Desktop is not installed. Use Install Docker Desktop in Martlet hosts. & goto :eof)\r\n");
        lines.Append("docker info >NUL 2>&1 && goto :ready\r\n");
        lines.Append("echo Starting Docker Desktop. The first start can take a few minutes; accept Docker's terms if it asks.\r\n");
        lines.Append(DockerDesktopStart);
        lines.Append("for /l %%i in (1,1,100) do (\r\n  docker info >NUL 2>&1 && goto :ready\r\n  ping -n 4 127.0.0.1 >NUL\r\n)\r\n");
        lines.Append("echo Docker Desktop is not running yet. When it shows it is running, press the same button again. & goto :eof\r\n");
        lines.Append(":ready\r\n");
        lines.Append($"docker image inspect {image} >NUL 2>&1 || {build}v{target.Version} || {build}main\r\n");
        // The owner's click in Martlet is the confirmation (--yes), as for SSH hosts; the console only asks for secrets and choices.
        lines.Append($"docker run --rm -it -u 0 -v {DockerSocket}{Environment(target, action == HostAction.Setup)} {image} --yes {Engine(action)}\r\n");
        lines.Append("echo.\r\necho Finished. You can close this window.\r\n");
        return lines.ToString();
    }

    /// <summary>The commands a method runs (without console chatter), or what to type on the host itself.</summary>
    internal static string Preview(HostSetupTarget target, HostAction action) => target.Method switch
    {
        HostSetupMethod.Agent =>
            $"Martlet sends \"{Engine(action)}\" to Martlet on {target.HostId ?? target.Address} through its paired host service; " +
            "Martlet there runs it and its output shows here.",
        HostSetupMethod.OnHost =>
            "Nothing to type on that PC: install Martlet there and choose Use as a Martlet host. Once paired, this PC sends \"" +
            Engine(action) + "\" and other commands to Martlet there, which runs them.",
        HostSetupMethod.SshDocker or HostSetupMethod.SshNative =>
            $"Martlet will run this on {target.SshTarget}:\r\n  " +
            RemoteShell(target, Engine(action), action == HostAction.Setup),
        _ => string.Join("\r\n", Script(target, action).Split("\r\n")
            .Where(line => line.StartsWith("docker image ", StringComparison.Ordinal) ||
                line.StartsWith("docker run ", StringComparison.Ordinal)))
    };

    private const string DockerDesktopStart =
        "if exist \"%ProgramFiles%\\Docker\\Docker\\Docker Desktop.exe\" start \"\" \"%ProgramFiles%\\Docker\\Docker\\Docker Desktop.exe\"\r\n";

    private const string SshUnattended = "-T -o BatchMode=yes -o ConnectTimeout=15";

    /// <summary>The console-less script automatic host updates run: SSH keys only (BatchMode), no TTY and no sudo password.
    /// Anything that would need an answer fails instead, and the owner finishes with Update host in Martlet.</summary>
    internal static string UnattendedScript(HostSetupTarget target, HostAction action, int lockWaitSeconds = 0)
    {
        Validate(target, action);
        var lines = new StringBuilder("@echo off\r\n");
        switch (target.Method)
        {
            case HostSetupMethod.ThisPcDocker:
                var image = Image(target);
                var build = $"docker build -t {image} -f deploy/host/Dockerfile {Repository}#";
                lines.Append("docker info >NUL 2>&1 || (echo Docker Desktop is not running. & exit /b 3)\r\n");
                lines.Append($"docker image inspect {image} >NUL 2>&1 || {build}v{target.Version} || {build}main || exit /b 4\r\n");
                lines.Append($"docker run --rm -u 0 -v {DockerSocket}{Environment(target, action == HostAction.Setup)}{LockWait(lockWaitSeconds)} {image} {Engine(action)}\r\n");
                break;
            case HostSetupMethod.SshDocker:
                lines.Append($"ssh {SshUnattended} {target.SshTarget} \"{DockerShell(target, Engine(action), action == HostAction.Setup, ShellMode.Batch, false, lockWaitSeconds)}\"\r\n");
                break;
            case HostSetupMethod.SshNative:
                lines.Append($"ssh {SshUnattended} {target.SshTarget} \"{NativeShell(target, Engine(action), action == HostAction.Setup, ShellMode.Console, false, lockWaitSeconds)}\"\r\n");
                break;
            default:
                throw new InvalidOperationException("Commands for this host go through Martlet on that computer, not a script.");
        }
        lines.Append("exit /b %errorlevel%\r\n");
        return lines.ToString();
    }

    /// <summary>Runs an action without a window or any question and returns its exit code and the log it wrote. SSH hosts
    /// go through Martlet's SSH runner when <paramref name="dataDirectory"/> is given (Martlet's key, the pinned host key
    /// and a sudo password the owner chose to remember; anything else fails instead of asking); otherwise
    /// <see cref="UnattendedScript"/> runs. Another change running on the host stops it at once (exit 75, MARTLET-BUSY)
    /// unless <paramref name="lockWaitSeconds"/> lets it wait that long for that change first.</summary>
    internal static async Task<(int ExitCode, string Log)> RunUnattendedAsync(HostSetupTarget target, HostAction action,
        CancellationToken token, string? dataDirectory = null, string? pinnedHostKey = null, int lockWaitSeconds = 0)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet");
        Directory.CreateDirectory(directory);
        var name = $"martlet-host-{Engine(action).Replace(' ', '-')}-{target.HostId ?? "this-pc"}";
        var script = Path.Combine(directory, name + ".cmd");
        var log = Path.Combine(directory, name + ".log");
        if (dataDirectory is not null && target.Method is HostSetupMethod.SshDocker or HostSetupMethod.SshNative)
            return (await RunQuietlyOverSshAsync(target, action, dataDirectory, pinnedHostKey, log, lockWaitSeconds, token), log);
        File.WriteAllText(script, UnattendedScript(target, action, lockWaitSeconds), Encoding.ASCII);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /s /c \"\"{script}\" > \"{log}\" 2>&1\"")
            { UseShellExecute = false, CreateNoWindow = true }) ?? throw new InvalidOperationException("Couldn't start the host update.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromMinutes(45));
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            if (token.IsCancellationRequested) throw;
            return (-1, log);
        }
        return (process.ExitCode, log);
    }

    private static async Task<int> RunQuietlyOverSshAsync(HostSetupTarget target, HostAction action, string dataDirectory,
        string? pinnedHostKey, string log, int lockWaitSeconds, CancellationToken token)
    {
        using var writer = new StreamWriter(log, append: false, Encoding.UTF8) { AutoFlush = true };
        var sink = new LineSink(line => { lock (writer) writer.WriteLine(line); });
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromMinutes(45));
        try
        {
            var shell = new HostShell(dataDirectory, NoHostShellPrompts.Instance);
            var supplied = await new HostRemote(shell).SupplyIfOfflineAsync(target, action.Verb, dataDirectory, pinnedHostKey, sink, limit.Token);
            var result = await shell.RunAsync(HostShellTarget.Parse(target.SshTarget),
                new()
                {
                    Command = RemoteShell(target, Engine(action), action == HostAction.Setup, assumeYes: false, lockWaitSeconds, supplied),
                    Input = "end\n", Sudo = true, PinnedHostKey = pinnedHostKey
                }, sink, limit.Token);
            return result.ExitCode;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            sink.Report("Stopped: SSH needed a password or took too long.");
            return -1;
        }
        catch (Exception error) when (error is HostShellException or Renci.SshNet.Common.SshException or IOException or
            System.Net.Sockets.SocketException or InvalidOperationException)
        {
            sink.Report("Stopped: " + error.Message);
            return -1;
        }
    }

    /// <summary>The Martlet version of this PC's own host service (Docker Desktop), from its gateway container's image tag;
    /// null when it is not set up or Docker is not running.</summary>
    internal static async Task<string?> ThisPcGatewayVersionAsync(CancellationToken token) =>
        (await ThisPcGatewayAsync(token))?.Version;

    /// <summary>This PC's own host service (Docker Desktop) as its gateway container shows it: the Martlet version of its image
    /// (null when the tag names none) and whether it runs. Null when it is not set up or Docker is not running.</summary>
    internal static async Task<Martlet.Core.Nodes.OwnHostReading?> ThisPcGatewayAsync(CancellationToken token)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker",
                "container inspect -f \"{{.State.Running}} {{.Config.Image}}\" martlet-host-gateway")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            });
            if (process is null) return null;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(15));
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            var output = await process.StandardOutput.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token);
            await error;
            return process.ExitCode == 0 ? ParseGateway(output) : null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or
            OperationCanceledException or IOException)
        {
            return null;
        }
    }

    /// <summary>Reads "&lt;running&gt; &lt;image&gt;" as docker container inspect prints it for the gateway.</summary>
    internal static Martlet.Core.Nodes.OwnHostReading? ParseGateway(string output)
    {
        var words = output.Trim().Split(' ', 2, StringSplitOptions.TrimEntries);
        if (words is not [var running, var image] || running is not ("true" or "false")) return null;
        var version = image.StartsWith("martlet-host:", StringComparison.Ordinal) &&
            System.Version.TryParse(image["martlet-host:".Length..], out var parsed) ? parsed.ToString(3) : null;
        return new(version, running == "true");
    }

    internal static bool IsPrivate(string? text) =>
        IPAddress.TryParse(text, out var address) && address.AddressFamily == AddressFamily.InterNetwork &&
        address.ToString() == text && IsPrivate(address);

    internal static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31);
    }

    /// <summary>This PC's LAN address: a private IPv4 on an up adapter with a default gateway (skips WSL/Hyper-V switches).</summary>
    internal static string? ThisPcAddress()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => (Adapter: n, Properties: n.GetIPProperties()))
            .SelectMany(n => n.Properties.UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a.Address))
                .Select(a => (Address: a.Address.ToString(),
                    Rank: (n.Properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork) ? 0 : 2) +
                          (n.Adapter.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
                           n.Adapter.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase) ? 1 : 0))))
            .OrderBy(c => c.Rank)
            .ToList();
        return candidates.FirstOrDefault().Address;
    }

    /// <summary>The LAN address for an SSH target: the literal IP, or its first private IPv4 from DNS.</summary>
    internal static async Task<string?> ResolveAsync(string sshTarget, CancellationToken token)
    {
        var host = sshTarget[(sshTarget.IndexOf('@') + 1)..].Split(':')[0];
        if (IPAddress.TryParse(host, out var literal)) return IsPrivate(literal) ? literal.ToString() : null;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, token);
            return addresses.FirstOrDefault(IsPrivate)?.ToString();
        }
        catch (SocketException) { return null; }
    }

    internal static string SuggestedHostId(string name)
    {
        var clean = new string(name.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').ToArray())
            .TrimStart('-', '_', '.');
        return (clean.Length == 0 ? "martlet" : clean.Length > 58 ? clean[..58] : clean) + "-host";
    }

    internal static string SuggestedDeviceId() => Martlet.Diagnostics.LocalLogs.ThisDeviceId();
}
