using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Martlet.Desktop;

/// <summary>What a script run on a Linux computer returned: its exit code and everything it printed.</summary>
internal sealed record ShellRun(int ExitCode, string Output);

/// <summary>Runs a bash script on a Linux computer over SSH, streaming what it prints. The script travels over the SSH
/// connection itself, so the computer needs no copy of it. <paramref name="sudo"/> marks runs that need administrator
/// rights (the script calls sudo itself and must be able to ask for the password).</summary>
internal interface IHostShell
{
    Task<ShellRun> RunAsync(string sshTarget, string script, IReadOnlyList<string> arguments, bool sudo, Action<string> output,
        CancellationToken token);
}

internal static class HostShells
{
    /// <summary>The runner the desktop uses: Martlet's in-app SSH runner (<see cref="HostShell"/>). App startup points it
    /// at the data directory Martlet was started with.</summary>
    internal static IHostShell Current { get; set; } = new SshHostShell(null);
}

/// <summary><see cref="IHostShell"/> over Martlet's one SSH stack, <see cref="HostShell"/>: Martlet's own key (the account
/// password is asked once to install it), the pinned host key, and for sudo runs a masked sudo password prompt whose
/// answer reaches the script's sudo through a private askpass helper. The script goes to <c>bash -s</c> on stdin.
/// Questions are asked over the window the owner is using.</summary>
internal sealed partial class SshHostShell(string? dataDirectory) : IHostShell
{
    [GeneratedRegex(@"\A(?:[A-Za-z0-9._-]{1,64}@)?[A-Za-z0-9][A-Za-z0-9.-]{0,252}(?::[0-9]{1,5})?\z")]
    private static partial Regex TargetPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9-][A-Za-z0-9=,._:-]{0,127}\z")]
    private static partial Regex ArgumentPattern();

    internal static void Validate(string sshTarget, IReadOnlyList<string> arguments)
    {
        if (!TargetPattern().IsMatch(sshTarget))
            throw new InvalidOperationException("Enter the SSH target as user@computer (for example me@192.168.1.20 or me@gpu-pc).");
        foreach (var argument in arguments)
            if (!ArgumentPattern().IsMatch(argument)) throw new InvalidOperationException($"Unexpected script argument '{argument}'.");
    }

    public async Task<ShellRun> RunAsync(string sshTarget, string script, IReadOnlyList<string> arguments, bool sudo,
        Action<string> output, CancellationToken token)
    {
        Validate(sshTarget, arguments);
        var target = HostShellTarget.Parse(sshTarget);
        var text = new StringBuilder();
        var sink = new LineSink(line =>
        {
            lock (text) text.Append(line).Append('\n');
            output(line + "\n");
        });
        var shell = new HostShell(dataDirectory ?? Martlet.Core.Settings.SettingsStore.DefaultDataDirectory(), new ActiveWindowPrompts());
        try
        {
            var result = await shell.RunAsync(target, new()
            {
                Command = "bash -s -- " + string.Join(' ', arguments), Input = script, Sudo = sudo
            }, sink, token);
            lock (text) return new(result.ExitCode, text.ToString());
        }
        catch (Exception error) when (error is HostShellException or Renci.SshNet.Common.SshException or
            System.Net.Sockets.SocketException)
        {
            throw new InvalidOperationException(error.Message, error);
        }
    }

    /// <summary>Asks over whichever Martlet window is active (the prepare window while it runs).</summary>
    private sealed class ActiveWindowPrompts : IHostShellPrompts
    {
        private static IHostShellPrompts Current()
        {
            var app = System.Windows.Application.Current;
            var window = app?.Dispatcher.Invoke(() =>
                app.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow);
            return window is null ? NoHostShellPrompts.Instance : new HostShellDialogs(window);
        }

        public bool TrustHostKey(HostShellTarget target, string hostKey) => Current().TrustHostKey(target, hostKey);
        public string? LoginPassword(HostShellTarget target, bool retry) => Current().LoginPassword(target, retry);
        public HostShellSudo? SudoPassword(HostShellTarget target, bool retry) => Current().SudoPassword(target, retry);
    }
}

/// <summary>The martlet-prepare script (deploy/host/martlet-prepare, embedded) and the arguments for what the owner ticked.</summary>
internal static partial class PrepareScript
{
    internal static readonly string[] Items =
        ["updates", "docker", "nvidia-driver", "nvidia-toolkit", "headless", "virtual-display", "gpu-power", "tools", "wol", "reboot", "shutdown"];
    internal static readonly string[] Tools = ["cuda", "python", "node", "git", "build", "htop", "nvtop", "curl", "tmux"];
    internal static readonly string[] Resolutions = ["1280x720", "1920x1080", "2560x1440", "3440x1440", "3840x2160"];

    [GeneratedRegex(@"\AGPU-[0-9a-fA-F-]{8,64}\z")]
    private static partial Regex GpuPattern();

    [GeneratedRegex(@"\A([0-9]{3,4})x([0-9]{3,4})\z")]
    private static partial Regex ResolutionPattern();

    private static string? text;

    internal static string Text => text ??= Load();

    private static string Load()
    {
        using var stream = typeof(PrepareScript).Assembly.GetManifestResourceStream("Martlet.Desktop.martlet-prepare") ??
            throw new InvalidOperationException("This Martlet build does not include martlet-prepare.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    internal static IReadOnlyList<string> Status => ["status", "--json"];

    internal static IReadOnlyList<string> Arguments(PrepareRequest request)
    {
        var items = request.Items.Distinct().ToArray();
        if (items.Length == 0) throw new InvalidOperationException("Tick at least one thing to do.");
        if (items.Except(Items).FirstOrDefault() is { } unknown) throw new InvalidOperationException($"Unknown item '{unknown}'.");
        var arguments = new List<string>(items);
        if (items.Contains("headless") && request.Boot is { } boot)
        {
            if (boot is not ("text" or "graphical")) throw new InvalidOperationException("Boot mode must be text or graphical.");
            arguments.AddRange(["--boot", boot]);
        }
        if (items.Contains("virtual-display"))
        {
            if (request.RemoveVirtualDisplay) arguments.Add("--virtual-display-off");
            else
            {
                var match = ResolutionPattern().Match(request.Resolution);
                if (!match.Success || int.Parse(match.Groups[1].Value) is < 640 or > 7680 || int.Parse(match.Groups[2].Value) is < 480 or > 4320)
                    throw new InvalidOperationException("Choose a virtual display resolution between 640x480 and 7680x4320.");
                arguments.AddRange(["--resolution", request.Resolution]);
            }
        }
        if (items.Contains("gpu-power"))
        {
            if (request.ResetPower) arguments.Add("--power-reset");
            else
                foreach (var (gpu, watts) in request.PowerLimits)
                {
                    if (!GpuPattern().IsMatch(gpu) || watts is < 1 or > 9999) throw new InvalidOperationException("Invalid GPU power limit.");
                    arguments.AddRange(["--power", $"{gpu}={watts}"]);
                }
        }
        if (items.Contains("tools"))
        {
            var tools = request.Tools.Distinct().ToArray();
            if (tools.Length == 0) throw new InvalidOperationException("Tick at least one developer tool, or untick Developer tools.");
            if (tools.Except(Tools).FirstOrDefault() is { } tool) throw new InvalidOperationException($"Unknown tool '{tool}'.");
            arguments.AddRange(["--tools", string.Join(',', tools)]);
        }
        if (!items.Contains("reboot") && !items.Contains("shutdown")) arguments.Add("--status");
        return arguments;
    }
}

internal sealed record PrepareRequest
{
    public required IReadOnlyList<string> Items { get; init; }
    public string? Boot { get; init; }
    public string Resolution { get; init; } = "1920x1080";
    public bool RemoveVirtualDisplay { get; init; }
    public IReadOnlyDictionary<string, int> PowerLimits { get; init; } = new Dictionary<string, int>();
    public bool ResetPower { get; init; }
    public IReadOnlyList<string> Tools { get; init; } = PrepareScript.Tools;
}

// ---------- what martlet-prepare reports (status --json and the MARTLET-RESULT line) ----------

internal sealed record PrepareOs
{
    public string? Id { get; init; }
    public string? Version { get; init; }
    public string? Name { get; init; }
    public string? Kernel { get; init; }
    public string? Arch { get; init; }
}

internal sealed record PrepareDocker
{
    public bool Installed { get; init; }
    public string? Version { get; init; }
    public string? Compose { get; init; }
    public bool Running { get; init; }
    public bool Enabled { get; init; }
    public bool UserInGroup { get; init; }
}

internal sealed record PrepareNvidia
{
    public bool Present { get; init; }
    public bool Working { get; init; }
    public string? Driver { get; init; }
    public string? DriverPackage { get; init; }
    public string? Recommended { get; init; }
    public string? Cuda { get; init; }
    public bool? SecureBoot { get; init; }
    public string? Toolkit { get; init; }
    public bool ToolkitConfigured { get; init; }
}

internal sealed record PreparePower
{
    public double? Current { get; init; }
    public double? Default { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Draw { get; init; }
    public double? Saved { get; init; }
    public bool Adjustable => Min is > 0 && Max is { } max && max >= Min;
}

internal sealed record PrepareGpu
{
    public int? Index { get; init; }
    public string Uuid { get; init; } = "";
    public string? Name { get; init; }
    public int? MemoryMb { get; init; }
    public bool Persistence { get; init; }
    public PreparePower Power { get; init; } = new();
}

internal sealed record PrepareHeadless
{
    public bool SshServer { get; init; }
    public bool SshEnabled { get; init; }
    public bool SleepMasked { get; init; }
    public string? DefaultTarget { get; init; }
    public bool GraphicalActive { get; init; }
    public string? DisplayManager { get; init; }
}

internal sealed record PrepareVirtualDisplay
{
    public bool Configured { get; init; }
    public string? Kind { get; init; }
    public string? Resolution { get; init; }
}

internal sealed record PrepareTools
{
    public string? Cuda { get; init; }
    public string? Python { get; init; }
    public string? Pip { get; init; }
    public bool Venv { get; init; }
    public string? Node { get; init; }
    public string? Npm { get; init; }
    public string? Git { get; init; }
    public bool Build { get; init; }
    public string? Htop { get; init; }
    public string? Nvtop { get; init; }
    public string? Curl { get; init; }
    public string? Tmux { get; init; }

    /// <summary>The installed version of a tool (or "installed"), or null when it is missing.</summary>
    internal string? Of(string tool) => tool switch
    {
        "cuda" => Cuda,
        "python" => Python is { } python && Pip is not null && Venv ? $"{python}, pip {Pip}" : null,
        "node" => Node is { } node ? Npm is { } npm ? $"{node}, npm {npm}" : node : null,
        "git" => Git,
        "build" => Build ? "installed" : null,
        "htop" => Htop,
        "nvtop" => Nvtop,
        "curl" => Curl,
        "tmux" => Tmux,
        _ => null
    };

    internal static string Label(string tool) => tool switch
    {
        "cuda" => "CUDA toolkit (nvcc)",
        "python" => "Python 3 with pip and venv",
        "node" => "Node.js LTS",
        "build" => "Build tools (gcc, make)",
        _ => tool
    };
}

internal sealed record PrepareWol
{
    public string? Interface { get; init; }
    public string? Mac { get; init; }
    public bool Wireless { get; init; }
    public string? Supports { get; init; }
    public bool? Supported { get; init; }
    public bool Enabled { get; init; }
    public bool Persistent { get; init; }
}

internal sealed record PrepareUpdates
{
    public int? Upgradable { get; init; }
}

internal sealed record PrepareGpuPower
{
    /// <summary>martlet-gpu-power.service reapplies persistence mode and saved limits at boot.</summary>
    public bool Service { get; init; }
}

internal sealed record PrepareStatus
{
    public int MartletPrepare { get; init; }
    public bool Supported { get; init; }
    public string? Reason { get; init; }
    public string? Hostname { get; init; }
    public string? User { get; init; }
    public string? Sudo { get; init; }
    public bool Systemd { get; init; }
    public PrepareOs Os { get; init; } = new();
    public bool RebootRequired { get; init; }
    public IReadOnlyList<string> RebootReasons { get; init; } = [];
    public PrepareUpdates Updates { get; init; } = new();
    public PrepareDocker Docker { get; init; } = new();
    public PrepareNvidia Nvidia { get; init; } = new();
    public IReadOnlyList<PrepareGpu> Gpus { get; init; } = [];
    public PrepareGpuPower GpuPower { get; init; } = new();
    public PrepareHeadless Headless { get; init; } = new();
    public PrepareVirtualDisplay VirtualDisplay { get; init; } = new();
    public PrepareTools Tools { get; init; } = new();
    public PrepareWol Wol { get; init; } = new();

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The last status line in a run's output, or null when there is none.</summary>
    internal static PrepareStatus? Find(string output)
    {
        var line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("{\"martlet_prepare\":", StringComparison.Ordinal));
        if (line is null) return null;
        try { return JsonSerializer.Deserialize<PrepareStatus>(line, Json); }
        catch (JsonException) { return null; }
    }

    internal static T? Parse<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, Json); }
        catch (JsonException) { return null; }
    }
}

internal sealed record PrepareItemResult
{
    public string Item { get; init; } = "";
    public string State { get; init; } = "";
    public string? Message { get; init; }
}

/// <summary>The final "MARTLET-RESULT {json}" line of an apply run.</summary>
internal sealed record PrepareOutcome
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public bool RebootRequired { get; init; }
    public IReadOnlyList<string> RebootReasons { get; init; } = [];
    public string? Restarting { get; init; }
    public string? WolMac { get; init; }
    public IReadOnlyList<PrepareItemResult> Items { get; init; } = [];

    internal const string Marker = "MARTLET-RESULT ";

    internal static PrepareOutcome? Find(string output)
    {
        var line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith(Marker, StringComparison.Ordinal));
        return line is null ? null : PrepareStatus.Parse<PrepareOutcome>(line[Marker.Length..]);
    }
}

/// <summary>Wakes computers with a Wake-on-LAN magic packet and watches for their SSH server to come and go.</summary>
internal static partial class HostPower
{
    [GeneratedRegex(@"\A([0-9A-Fa-f]{2})([:-]?)([0-9A-Fa-f]{2})\2([0-9A-Fa-f]{2})\2([0-9A-Fa-f]{2})\2([0-9A-Fa-f]{2})\2([0-9A-Fa-f]{2})\z")]
    private static partial Regex MacPattern();

    /// <summary>A MAC address as aa:bb:cc:dd:ee:ff, or null when it is not one (or all zeros/broadcast).</summary>
    internal static string? NormalizeMac(string? mac)
    {
        var match = MacPattern().Match(mac?.Trim() ?? "");
        if (!match.Success) return null;
        var text = string.Join(':', new[] { 1, 3, 4, 5, 6, 7 }.Select(i => match.Groups[i].Value.ToLowerInvariant()));
        return text is "00:00:00:00:00:00" or "ff:ff:ff:ff:ff:ff" ? null : text;
    }

    internal static byte[] MagicPacket(string mac)
    {
        var bytes = (NormalizeMac(mac) ?? throw new InvalidOperationException("Invalid MAC address."))
            .Split(':').Select(b => Convert.ToByte(b, 16)).ToArray();
        var packet = new byte[102];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var i = 1; i <= 16; i++) Array.Copy(bytes, 0, packet, i * 6, 6);
        return packet;
    }

    /// <summary>Sends the magic packet to the limited broadcast and to every local private subnet's broadcast, ports 9 and 7.</summary>
    internal static async Task<int> WakeAsync(string mac, CancellationToken token)
    {
        var packet = MagicPacket(mac);
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || !HostSetupCommands.IsPrivate(unicast.Address)) continue;
                var address = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                targets.Add(new IPAddress(address.Select((b, i) => (byte)(b | ~mask[i])).ToArray()));
            }
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        var sent = 0;
        foreach (var target in targets)
            foreach (var port in new[] { 9, 7 })
            {
                try { await udp.SendAsync(packet, new IPEndPoint(target, port), token); sent++; }
                catch (SocketException) { }
            }
        return sent;
    }

    /// <summary>Whether the SSH server of <paramref name="sshTarget"/> (user@computer[:port]) accepts a TCP connection.</summary>
    internal static async Task<bool> SshAnswersAsync(string sshTarget, CancellationToken token)
    {
        var target = HostShellTarget.Parse(sshTarget);
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await client.ConnectAsync(target.Host, target.Port, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (SocketException) { return false; }
    }

    /// <summary>Waits until SSH stops answering (the computer went down), up to <paramref name="limit"/>.</summary>
    internal static async Task<bool> WaitDownAsync(string sshTarget, TimeSpan limit, CancellationToken token)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until)
        {
            if (!await SshAnswersAsync(sshTarget, token)) return true;
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
        return false;
    }

    /// <summary>Waits until SSH answers again, up to <paramref name="limit"/>.</summary>
    internal static async Task<bool> WaitUpAsync(string sshTarget, TimeSpan limit, CancellationToken token)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until)
        {
            if (await SshAnswersAsync(sshTarget, token)) return true;
            await Task.Delay(TimeSpan.FromSeconds(3), token);
        }
        return false;
    }
}

internal static class HostWake
{
    /// <summary>Saves the Wake-on-LAN MAC a paired host reported with it in hosts.json.</summary>
    internal static async Task<PairedHost?> SaveMacAsync(this HostPairings pairings, string hostId, string? mac, CancellationToken token)
    {
        if (HostPower.NormalizeMac(mac) is not { } normalized) return null;
        var (hosts, _) = await pairings.LoadAsync(token);
        var host = hosts.FirstOrDefault(h => h.HostId == hostId);
        if (host is null || host.WakeMac == normalized) return host;
        var updated = host with { WakeMac = normalized };
        HostRegistry.Save(pairings.DataDirectory, HostRegistry.Upsert(hosts, updated));
        return updated;
    }
}
