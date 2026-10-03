using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Martlet.Core.Contracts;
using Martlet.Core.Network;

namespace Martlet.Core.Installation;

/// <summary>How far this PC's own host service on Docker Desktop is.</summary>
public enum LocalHostServiceStage
{
    /// <summary>No docker command on this PC.</summary>
    DockerMissing,
    /// <summary>Docker's engine didn't answer (Docker Desktop stopped or still starting), so the host service is unknown.</summary>
    DockerNotRunning,
    /// <summary>Docker answers, but the host service was never set up here.</summary>
    NotSetUp,
    /// <summary>The gateway container exists but isn't running.</summary>
    Stopped,
    Running
}

/// <summary>A desktop in the Martlet network this PC's host service joined: a computer that paired with it.</summary>
public sealed record LocalHostDesktop(string Id, string Name);

/// <summary>What <see cref="LocalHostService.ProbeAsync"/> found. <see cref="Roles"/>, <see cref="HostId"/> and the network
/// are read only while the gateway runs; <see cref="Answering"/> is a TCP connect to the published address.</summary>
public sealed record LocalHostServiceState
{
    public required LocalHostServiceStage Stage { get; init; }
    /// <summary>The Martlet version of the gateway image (martlet-host:x.y.z), once set up.</summary>
    public string? Version { get; init; }
    public string? HostId { get; init; }
    /// <summary>The ip:port the network holder publishes the gateway on.</summary>
    public string? Address { get; init; }
    /// <summary>Whether <see cref="Address"/> is still one of this PC's addresses (false after the network gave it another one).</summary>
    public bool? AddressOnThisPc { get; init; }
    public bool? Answering { get; init; }
    /// <summary>Installed roles (deploy/host/roles names), from the gateway's role records.</summary>
    public IReadOnlyList<string>? Roles { get; init; }
    /// <summary>The gateway's Martlet network: unbound, bound, removed or unreadable.</summary>
    public string? Network { get; init; }
    /// <summary>The active desktops in that network.</summary>
    public IReadOnlyList<LocalHostDesktop> Desktops { get; init; } = [];
    /// <summary>Why Docker or the gateway couldn't be read (Docker's first error line, or a timeout).</summary>
    public string? Problem { get; init; }

    /// <summary>Set up, running and answering at an address this PC still has.</summary>
    public bool Ready => Stage == LocalHostServiceStage.Running && Answering == true && AddressOnThisPc != false;
}

/// <summary>Reads this PC's own Martlet host service on Docker Desktop, the one the host dashboard sets up
/// (deploy/host/martlet-host, Docker method): the gateway container (martlet-host-gateway), the network holder that publishes
/// its port (martlet-host-net), the role records and host ID in its configuration volume and the Martlet network roster the
/// gateway accepted (network.json). All of it is nonsecret; the agent token, keys, secrets and pairings are never read.
/// Contacts nothing but this PC's Docker engine and the gateway's own published port.</summary>
public static partial class LocalHostService
{
    public const string Gateway = "martlet-host-gateway";
    public const string Holder = "martlet-host-net";
    private const string RolesDirectory = "/var/lib/martlet/config/roles";
    private const string HostSettings = "/var/lib/martlet/config/host.env";
    private const string NetworkFile = "/var/lib/martlet/config/gateway/network.json";
    private static readonly TimeSpan DockerTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9-]{0,31}\z")]
    private static partial Regex RolePattern();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")]
    private static partial Regex HostIdPattern();

    private static string Docker => OperatingSystem.IsWindows() && File.Exists(DockerDesktopStatus.DockerPath) ? DockerDesktopStatus.DockerPath : "docker";

    public static async Task<LocalHostServiceState> ProbeAsync(CancellationToken token)
    {
        (int Exit, string Output, string Error) gateway;
        try { gateway = await DockerAsync(["container", "inspect", "-f", "{{.State.Running}} {{.Config.Image}}", Gateway], token); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new() { Stage = LocalHostServiceStage.DockerMissing, Problem = "Docker isn't installed on this PC." };
        }
        if (gateway.Exit != 0)
        {
            var problem = FirstLine(gateway.Error);
            return problem is not null && (problem.Contains("No such container", StringComparison.OrdinalIgnoreCase) ||
                problem.Contains("No such object", StringComparison.OrdinalIgnoreCase))
                ? new() { Stage = LocalHostServiceStage.NotSetUp }
                : new() { Stage = LocalHostServiceStage.DockerNotRunning, Problem = problem ?? "Docker didn't answer." };
        }
        var words = gateway.Output.Trim().Split(' ', 2);
        var running = words[0] == "true";
        var version = words.Length == 2 && words[1].StartsWith("martlet-host:", StringComparison.Ordinal) &&
            System.Version.TryParse(words[1]["martlet-host:".Length..], out var parsed) ? parsed.ToString(3) : null;

        var holder = await DockerAsync(["container", "inspect", "-f",
            "{{range $p, $b := .HostConfig.PortBindings}}{{range $b}}{{.HostIp}}:{{.HostPort}} {{end}}{{end}}", Holder], token);
        var address = holder.Exit == 0 ? holder.Output.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() : null;
        IPEndPoint? endpoint = address is not null && IPEndPoint.TryParse(address, out var given) ? given : null;
        var state = new LocalHostServiceState
        {
            Stage = running ? LocalHostServiceStage.Running : LocalHostServiceStage.Stopped, Version = version,
            Address = endpoint?.ToString(), AddressOnThisPc = endpoint is null ? null : OnThisPc(endpoint.Address)
        };
        if (!running) return state;

        var answering = endpoint is not null ? await AnswersAsync(endpoint, token) : (bool?)null;
        var files = await DockerAsync(["exec", Gateway, "sh", "-c",
            $"ls -1 {RolesDirectory} 2>/dev/null; echo ::host::; grep -E '^host_id=' {HostSettings} 2>/dev/null; " +
            $"echo ::network::; cat {NetworkFile} 2>/dev/null"], token);
        if (files.Exit != 0 && !files.Output.Contains("::network::", StringComparison.Ordinal))
            return state with { Answering = answering, Problem = FirstLine(files.Error) ?? "The host service's files couldn't be read." };
        var (roles, hostId, network, desktops) = ParseFiles(files.Output);
        return state with { Answering = answering, Roles = roles, HostId = hostId, Network = network, Desktops = desktops };
    }

    /// <summary>The role records, host ID and network roster from the gateway's configuration, as ProbeAsync's one docker exec
    /// prints them (role record names, then ::host:: and host_id=..., then ::network:: and network.json).</summary>
    public static (IReadOnlyList<string> Roles, string? HostId, string Network, IReadOnlyList<LocalHostDesktop> Desktops) ParseFiles(string output)
    {
        var text = output.Replace("\r\n", "\n", StringComparison.Ordinal);
        var hostAt = text.IndexOf("::host::\n", StringComparison.Ordinal);
        var networkAt = text.IndexOf("::network::\n", StringComparison.Ordinal);
        if (hostAt < 0 || networkAt < hostAt) return ([], null, "unreadable", []);
        var roles = text[..hostAt].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.EndsWith(".role", StringComparison.Ordinal)).Select(line => line[..^".role".Length])
            .Where(role => RolePattern().IsMatch(role)).Order(StringComparer.Ordinal).ToArray();
        var hostId = text[(hostAt + "::host::\n".Length)..networkAt].Split('\n', StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("host_id=", StringComparison.Ordinal)).Select(line => line["host_id=".Length..])
            .FirstOrDefault(id => HostIdPattern().IsMatch(id));
        var json = text[(networkAt + "::network::\n".Length)..].Trim();
        if (json.Length == 0) return (roles, hostId, "unbound", []);
        try
        {
            var roster = NetworkRoster.Parse(Encoding.UTF8.GetBytes(json));
            var bound = hostId is null ? roster.ActiveHosts.Any() : roster.Host(hostId) is { Removed: false };
            return (roles, hostId, bound ? "bound" : "removed",
                roster.ActiveDesktops.Select(d => new LocalHostDesktop(d.Id, d.Name)).ToArray());
        }
        catch (ContractException) { return (roles, hostId, "unreadable", []); }
    }

    private static async Task<bool> AnswersAsync(IPEndPoint endpoint, CancellationToken token)
    {
        using var client = new TcpClient(endpoint.AddressFamily);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(ConnectTimeout);
        try
        {
            await client.ConnectAsync(endpoint, limit.Token);
            return true;
        }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    private static bool? OnThisPc(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any)) return true;
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Any(a => a.Address.Equals(address));
        }
        catch (NetworkInformationException) { return null; }
    }

    private static string? FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return line is null ? null : line.Length > 200 ? line[..200] : line;
    }

    private static async Task<(int Exit, string Output, string Error)> DockerAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        var start = new ProcessStartInfo(Docker)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker didn't start.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(DockerTimeout);
        try
        {
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            var output = await process.StandardOutput.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token);
            return (process.ExitCode, output, await error);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            if (token.IsCancellationRequested) throw;
            return (-1, "", $"Docker didn't answer within {DockerTimeout.TotalSeconds:0} seconds.");
        }
    }
}
