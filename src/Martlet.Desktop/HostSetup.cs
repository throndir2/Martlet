using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Martlet.Desktop;

/// <summary>How the desktop reaches the machine that becomes a Martlet host. Every method runs the same martlet-host engine.</summary>
internal enum HostSetupMethod { ThisPcDocker, SshDocker, SshNative, OnHost }

internal enum HostAction { Setup, Pair, AddAudio2Face, Status, RemoveAudio2Face }

internal sealed record HostSetupTarget(HostSetupMethod Method, string SshTarget, string Address, string? HostId, string Version);

/// <summary>Builds the exact commands each installation method runs; nothing here executes them.</summary>
internal static partial class HostSetupCommands
{
    internal const string Repository = "https://github.com/throndir2/Martlet.git";
    internal const string DockerSocket = "/var/run/docker.sock:/var/run/docker.sock";

    [GeneratedRegex(@"\A(?:[A-Za-z0-9._-]{1,64}@)?[A-Za-z0-9][A-Za-z0-9.-]{0,252}(?::[0-9]{1,5})?\z")]
    private static partial Regex SshTargetPattern();

    [GeneratedRegex(@"\A[0-9]{1,6}\.[0-9]{1,6}\.[0-9]{1,6}\z")]
    private static partial Regex VersionPattern();

    internal static string Engine(HostAction action) => action switch
    {
        HostAction.Setup => "setup",
        HostAction.Pair => "pair",
        HostAction.AddAudio2Face => "add audio2face",
        HostAction.Status => "status",
        HostAction.RemoveAudio2Face => "remove audio2face",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    internal static void Validate(HostSetupTarget target, HostAction action)
    {
        if (!VersionPattern().IsMatch(target.Version)) throw new InvalidOperationException("Unexpected Martlet version.");
        if (target.Method is HostSetupMethod.SshDocker or HostSetupMethod.SshNative && !SshTargetPattern().IsMatch(target.SshTarget))
            throw new InvalidOperationException("Enter the SSH target as user@computer (for example me@192.168.1.20 or me@gpu-pc).");
        if (action == HostAction.Setup && !IsPrivate(target.Address))
            throw new InvalidOperationException("Enter the host's private LAN IPv4 address (10.x, 172.16-31.x or 192.168.x).");
        if (target.HostId is { } id && !Regex.IsMatch(id, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z"))
            throw new InvalidOperationException("Invalid host ID.");
    }

    internal static string Image(HostSetupTarget target) => $"martlet-host:{target.Version}";

    /// <summary>POSIX shell for a Docker host: build the image once from this version's source, then run the engine
    /// (interactively in a terminal; see <see cref="RemoteShell"/> for Martlet's unattended SSH runs).</summary>
    internal static string DockerShell(HostSetupTarget target, HostAction action) =>
        DockerShell(target, Engine(action), action == HostAction.Setup, unattended: false);

    private static string DockerShell(HostSetupTarget target, string engine, bool setup, bool unattended)
    {
        var image = Image(target);
        var build = $"$D build -t {image} -f deploy/host/Dockerfile {Repository}#";
        var run = unattended ? "-i --log-driver none" : "-it";
        return "D=docker; docker info >/dev/null 2>&1 || D='sudo docker'; " +
            $"($D image inspect {image} >/dev/null 2>&1 || {build}v{target.Version} || {build}main) && " +
            $"$D run --rm {run} -u 0 -v {DockerSocket}{Environment(target, setup)} {image} {(unattended ? "--yes " : "")}{engine}";
    }

    /// <summary>POSIX shell for a native Ubuntu host: keep a Martlet checkout in ~/Martlet and run its engine.</summary>
    internal static string NativeShell(HostSetupTarget target, HostAction action) =>
        NativeShell(target, Engine(action), action == HostAction.Setup, unattended: false);

    private static string NativeShell(HostSetupTarget target, string engine, bool setup, bool unattended)
    {
        var prefix = setup ? $"MARTLET_HOST_ADDRESS={target.Address} " : "";
        var quiet = unattended ? " </dev/null" : "";
        return $"(command -v git >/dev/null 2>&1 || (sudo apt-get update -q && sudo apt-get install -y git)){quiet}; " +
            $"(test -d ~/Martlet/.git || git clone --depth 1 {Repository} ~/Martlet){quiet}; git -C ~/Martlet pull --ff-only -q{quiet} || true; " +
            $"{prefix}~/Martlet/deploy/host/martlet-host {(unattended ? "--yes " : "")}{engine}";
    }

    /// <summary>The POSIX shell Martlet's SSH runner (<see cref="HostShell"/>) executes for one martlet-host command
    /// (for example "status", "add audio2face" or "pair --device-id ... --name ..."): unattended (--yes, no terminal),
    /// with answers read from stdin. The owner's click in Martlet is the confirmation.</summary>
    internal static string RemoteShell(HostSetupTarget target, string engine, bool setup)
    {
        Validate(target, setup ? HostAction.Setup : HostAction.Status);
        return target.Method switch
        {
            HostSetupMethod.SshDocker => DockerShell(target, engine, setup, unattended: true),
            HostSetupMethod.SshNative => NativeShell(target, engine, setup, unattended: true),
            _ => throw new InvalidOperationException("Only SSH hosts run through Martlet's SSH runner.")
        };
    }

    private static string Environment(HostSetupTarget target, bool setup)
    {
        if (!setup) return "";
        var text = $" -e MARTLET_HOST_ADDRESS={target.Address}";
        return target.HostId is { } id ? text + $" -e MARTLET_HOST_ID={id}" : text;
    }

    /// <summary>The Windows command script a launcher opens in a console window (this PC with Docker Desktop only; SSH
    /// hosts run in-app through <see cref="HostShell"/>).</summary>
    internal static string Script(HostSetupTarget target, HostAction action)
    {
        Validate(target, action);
        if (target.Method != HostSetupMethod.ThisPcDocker)
            throw new InvalidOperationException(target.Method == HostSetupMethod.OnHost
                ? "Run the shown commands on the host itself." : "SSH hosts run inside Martlet, not in a console window.");
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
        lines.Append($"docker run --rm -it -u 0 -v {DockerSocket}{Environment(target, action == HostAction.Setup)} {image} {Engine(action)}\r\n");
        lines.Append("echo.\r\necho Finished. You can close this window.\r\n");
        return lines.ToString();
    }

    /// <summary>The commands a method runs (without console chatter), or what to type on the host itself.</summary>
    internal static string Preview(HostSetupTarget target, HostAction action) => target.Method switch
    {
        HostSetupMethod.OnHost =>
            "On a Docker host (Linux, or Windows/macOS with Docker Desktop):\r\n  " +
            DockerShell(target, action) + "\r\n\r\nOn Ubuntu without Docker for the gateway (native):\r\n  " +
            NativeShell(target, action),
        HostSetupMethod.SshDocker or HostSetupMethod.SshNative =>
            $"Martlet runs this on {target.SshTarget} over SSH (no console; you confirm here):\r\n  " +
            RemoteShell(target, Engine(action), action == HostAction.Setup),
        _ => string.Join("\r\n", Script(target, action).Split("\r\n")
            .Where(line => line.StartsWith("docker image ", StringComparison.Ordinal) ||
                line.StartsWith("docker run ", StringComparison.Ordinal)))
    };

    private const string DockerDesktopStart =
        "if exist \"%ProgramFiles%\\Docker\\Docker\\Docker Desktop.exe\" start \"\" \"%ProgramFiles%\\Docker\\Docker\\Docker Desktop.exe\"\r\n";

    /// <summary>Installs WSL-based Docker Desktop with winget; the user already agreed to the listed terms in Martlet.</summary>
    internal const string DockerDesktopInstall =
        "winget install -e --id Docker.DockerDesktop --accept-package-agreements --accept-source-agreements";

    internal static void Launch(HostSetupTarget target, HostAction action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"martlet-host-{Engine(action).Replace(' ', '-')}.cmd");
        File.WriteAllText(path, Script(target, action), Encoding.ASCII);
        Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"{path}\"") { UseShellExecute = true })?.Dispose();
    }

    internal static void InstallDockerDesktop()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "install-docker-desktop.cmd");
        File.WriteAllText(path,
            "@echo off\r\ntitle Install Docker Desktop\r\n" +
            "echo Installing Docker Desktop with winget. Windows asks for administrator approval; a restart or sign-out may follow.\r\n" +
            DockerDesktopInstall + "\r\n" +
            "if errorlevel 1 (echo. & echo Docker Desktop was not installed. See the messages above. & goto :eof)\r\n" +
            "echo.\r\necho Starting Docker Desktop. Accept Docker's terms if it asks; if it asks you to restart or sign out, do that first.\r\n" +
            DockerDesktopStart +
            "echo Then return to Martlet hosts and press Set up host.\r\n", Encoding.ASCII);
        Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"{path}\"") { UseShellExecute = true })?.Dispose();
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

    internal static string SuggestedDeviceId()
    {
        var name = new string(System.Environment.MachineName.ToLowerInvariant()
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
        var id = "desktop-" + (name.Length == 0 ? "pc" : name);
        return id.Length > 64 ? id[..64] : id;
    }
}
