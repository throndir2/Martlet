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
    /// <summary>The runner the desktop uses. The in-app SSH runner (Martlet-owned key, pinned host key, sudo prompt)
    /// replaces the console fallback here once it lands.</summary>
    internal static IHostShell Current { get; set; } = new ConsoleSshShell();
}

/// <summary>Runs scripts with Windows' own OpenSSH client. Without a password it runs quietly in the background (key
/// sign-in, and for sudo runs passwordless sudo); otherwise it opens an SSH window that is only used to type the SSH and
/// sudo passwords, while everything the script prints still streams into Martlet.</summary>
internal sealed partial class ConsoleSshShell : IHostShell
{
    [GeneratedRegex(@"\A(?:[A-Za-z0-9._-]{1,64}@)?[A-Za-z0-9][A-Za-z0-9.-]{0,252}\z")]
    private static partial Regex TargetPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9-][A-Za-z0-9=,._:-]{0,127}\z")]
    private static partial Regex ArgumentPattern();

    /// <summary>Shown when a run needs the SSH window.</summary>
    internal const string WindowHint =
        "Martlet opened an SSH window for this. If it asks for a password (SSH, then sudo), type it in that window; " +
        "the output appears here.\n";

    internal static string SshPath()
    {
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
        return File.Exists(system) ? system : "ssh.exe";
    }

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
        var quiet = !sudo || await CanSudoQuietlyAsync(sshTarget, token);
        if (quiet)
        {
            var run = await RunQuietAsync(sshTarget, script, arguments, output, token);
            if (run is not null) return run;
        }
        output(WindowHint);
        return await RunInWindowAsync(sshTarget, script, arguments, output, token);
    }

    private static ProcessStartInfo Ssh(bool window, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(SshPath())
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = !window, CreateNoWindow = !window,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static string[] Quiet(string sshTarget) =>
        ["-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-o", "ServerAliveInterval=15", "-T", sshTarget];

    /// <summary>Key sign-in and passwordless sudo both work, so a sudo run needs no window.</summary>
    private static async Task<bool> CanSudoQuietlyAsync(string sshTarget, CancellationToken token)
    {
        try
        {
            using var process = Process.Start(Ssh(false, [.. Quiet(sshTarget), "sudo -n true"])) ??
                throw new InvalidOperationException("ssh.exe did not start.");
            process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { Kill(process); throw; }
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (System.ComponentModel.Win32Exception) { throw Missing(); }
    }

    /// <summary>Streams the script over stdin with key sign-in only. Returns null when SSH needs a password or a new host
    /// key confirmed, so the caller falls back to the SSH window.</summary>
    private static async Task<ShellRun?> RunQuietAsync(string sshTarget, string script, IReadOnlyList<string> arguments,
        Action<string> output, CancellationToken token)
    {
        Process process;
        try
        {
            process = Process.Start(Ssh(false, [.. Quiet(sshTarget), "bash -s -- " + string.Join(' ', arguments)])) ??
                throw new InvalidOperationException("ssh.exe did not start.");
        }
        catch (System.ComponentModel.Win32Exception) { throw Missing(); }
        using (process)
        {
            var text = new StringBuilder();
            var errors = new StringBuilder();
            void Stdout(string chunk) { lock (text) text.Append(chunk); output(chunk); }
            void Stderr(string chunk) { lock (errors) errors.Append(chunk); }
            var reading = Task.WhenAll(Pump(process.StandardOutput, Stdout), Pump(process.StandardError, Stderr));
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(script), token);
                process.StandardInput.Close();
            }
            catch (IOException) { }
            try
            {
                await process.WaitForExitAsync(token);
                await reading;
            }
            catch (OperationCanceledException) { Kill(process); throw; }
            var stderr = errors.ToString();
            if (process.ExitCode == 255 && NeedsWindow(stderr)) return null;
            if (stderr.Length > 0) output(stderr);
            return new(process.ExitCode, text + stderr);
        }
    }

    private static bool NeedsWindow(string stderr) =>
        stderr.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
        stderr.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase) ||
        stderr.Contains("No more authentication methods", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens ssh.exe in its own console with a terminal (so SSH and sudo can ask for passwords there) and sends the
    /// script inside the command, compressed, because stdin is that console.</summary>
    private static async Task<ShellRun> RunInWindowAsync(string sshTarget, string script, IReadOnlyList<string> arguments,
        Action<string> output, CancellationToken token)
    {
        var remote = $"f=$(mktemp) && echo {Packed(script)} | base64 -d | gzip -dc > \"$f\" && bash \"$f\" {string.Join(' ', arguments)}; " +
            "r=$?; rm -f \"$f\"; exit $r";
        Process process;
        try
        {
            process = Process.Start(Ssh(true, ["-t", "-o", "ConnectTimeout=15", "-o", "ServerAliveInterval=15", sshTarget, remote])) ??
                throw new InvalidOperationException("ssh.exe did not start.");
        }
        catch (System.ComponentModel.Win32Exception) { throw Missing(); }
        using (process)
        {
            var text = new StringBuilder();
            void Both(string chunk)
            {
                chunk = chunk.Replace("\r\n", "\n", StringComparison.Ordinal);
                lock (text) text.Append(chunk);
                output(chunk);
            }
            var reading = Task.WhenAll(Pump(process.StandardOutput, Both), Pump(process.StandardError, Both));
            try
            {
                await process.WaitForExitAsync(token);
                await reading;
            }
            catch (OperationCanceledException) { Kill(process); throw; }
            return new(process.ExitCode, text.ToString());
        }
    }

    internal static string Packed(string script)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(script));
        return Convert.ToBase64String(buffer.ToArray());
    }

    private static async Task Pump(StreamReader reader, Action<string> chunk)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0) chunk(new string(buffer, 0, read));
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static InvalidOperationException Missing() =>
        new("Windows' OpenSSH client (ssh.exe) is missing. Add it in Settings > System > Optional features > OpenSSH Client.");
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

    internal static string HostOf(string sshTarget) => sshTarget[(sshTarget.IndexOf('@') + 1)..];

    internal static async Task<bool> SshAnswersAsync(string host, CancellationToken token)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await client.ConnectAsync(host, 22, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (SocketException) { return false; }
    }

    /// <summary>Waits until SSH stops answering (the computer went down), up to <paramref name="limit"/>.</summary>
    internal static async Task<bool> WaitDownAsync(string host, TimeSpan limit, CancellationToken token)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until)
        {
            if (!await SshAnswersAsync(host, token)) return true;
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
        return false;
    }

    /// <summary>Waits until SSH answers again, up to <paramref name="limit"/>.</summary>
    internal static async Task<bool> WaitUpAsync(string host, TimeSpan limit, CancellationToken token)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until)
        {
            if (await SshAnswersAsync(host, token)) return true;
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
