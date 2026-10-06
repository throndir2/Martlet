using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Martlet.Core.Platforms;

namespace Martlet.Gateway.Host.Linux;

/// <summary>Where the Mac host keeps its files: everything under ~/Library/Application Support/Martlet/Host (0700), the
/// gateway's configuration in gateway/ (host.json, machine.json and the rest beside it), its identity in private/state,
/// whisper models in models/ and logs in logs/; the launchd agents in ~/Library/LaunchAgents.</summary>
internal sealed record MacHostLayout(string Home)
{
    internal const string GatewayLabel = "io.github.throndir2.martlet.host";
    internal const string WhisperLabel = "io.github.throndir2.martlet.whisper";
    internal string Base => $"{Home}/Library/Application Support/Martlet/Host";
    internal string ConfigDirectory => $"{Base}/gateway";
    internal string ConfigPath => $"{ConfigDirectory}/host.json";
    internal string MachinePath => $"{ConfigDirectory}/machine.json";
    internal string Private => $"{Base}/private";
    internal string State => $"{Private}/state";
    internal string Models => $"{Base}/models";
    internal string Logs => $"{Base}/logs";
    internal string Agents => $"{Home}/Library/LaunchAgents";
    internal string AgentPath(string label) => $"{Agents}/{label}.plist";
}

/// <summary>What the Mac reported about itself (sysctl and Metal), before it becomes machine.json.</summary>
internal sealed record MacFacts(string OsVersion, string Kernel, string Processor, bool AppleSilicon, int Threads,
    ulong MemoryBytes, string? GpuName, ulong? GpuWorkingSetBytes, bool OnBattery);

/// <summary>The Mac host: the same protocol-2 gateway as Linux hosts, run by launchd while you are logged in, relaying
/// to native Ollama (Metal on Apple silicon) and whisper.cpp's server (Metal or CPU) on this Mac's loopback. GPU workers
/// built for NVIDIA (F5's PyTorch worker, Audio2Face, XTTS and the rest) are refused with the platform catalog's reason.</summary>
internal static class MacHost
{
    internal const int OllamaPort = 11434, WhisperPort = 8178, DefaultPort = 9443;
    /// <summary>Role kinds a Mac host serves; everything else is refused with the platform catalog's reason.</summary>
    internal static readonly IReadOnlySet<string> RoleKinds = new HashSet<string>(StringComparer.Ordinal) { "ollama", "deep-thinking", "stt" };

    /// <summary>The same pinned whisper.cpp ggml models as the Linux stt role (deploy/host/roles/stt/compose.yaml).</summary>
    internal static readonly IReadOnlyDictionary<string, string> WhisperModels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["base"] = "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe",
        ["small"] = "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b",
        ["medium"] = "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208",
        ["large-v3-turbo"] = "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69"
    };
    internal const string WhisperRevision = "5359861c739e955e79d9a303bcbc70fb988958b1";

    internal const string Help = """
        Martlet Mac host (macOS 14+, Apple silicon or Intel). Runs this gateway as a launchd agent while you are logged in,
        relaying to native Ollama (Metal on Apple silicon, CPU on Intel) and whisper.cpp's server (Metal or CPU).
          macos-setup [--address <private IPv4> | --loopback] [--port <n>] [--host-id <id>]
                      [--ollama-model <name> | --no-ollama] [--stt-model base|small|medium|large-v3-turbo | --no-stt]
                      set up or update the host: detects Ollama on 127.0.0.1:11434 and whisper-server (brew install
                      whisper-cpp), offers only what is installed, writes host.json and machine.json, creates or opens
                      the identity, approves it and (re)starts the launchd agents
          macos-pair  [owner-pair options]  stop the agent, show a one-use XXXX-XXXX code to type in Martlet, start it again
          macos-status     the agents, detected engines, roles and the gateway's health (JSON)
          macos-machine    collect this Mac's hardware report again (chip, unified memory, GPU working set)
          macos-uninstall  stop and remove the launchd agents; the identity, pairings and models are kept
        Files: ~/Library/Application Support/Martlet/Host. F5 (PyTorch), Audio2Face, XTTS, Chatterbox, GPT-SoVITS, Dia,
        singing and pictures need an NVIDIA GPU and are never offered on a Mac.
        """;

    internal static bool Handles(string[] args) => args.Length > 0 && args[0].StartsWith("macos-", StringComparison.Ordinal);

    /// <summary>Why a role kind can't run on a Mac host (the platform catalog's reason), or null when it can.</summary>
    internal static string? Refusal(string kind)
    {
        if (RoleKinds.Contains(kind)) return null;
        var engine = PlatformCatalog.EngineForHostRole(kind);
        if (engine is null) return $"{kind} isn't a Martlet host role.";
        var check = PlatformCatalog.Check(engine, PlatformSide.Host, new PlatformDevice { Platform = DevicePlatform.MacOs, Name = "this Mac" });
        return check.Allowed ? $"{kind} isn't served by the Mac host yet." : check.Reason;
    }

    /// <summary>The largest GPU budget (MB) for models: Metal's recommended working set on Apple silicon; Intel Macs run
    /// Ollama on the CPU, so they get the smallest suggestion.</summary>
    internal static long ModelBudgetMb(MacFacts facts) =>
        facts.AppleSilicon ? (long)((facts.GpuWorkingSetBytes ?? EstimatedWorkingSet(facts.MemoryBytes)) / (1024 * 1024)) : 0;

    /// <summary>Without Metal's own answer: about two thirds of unified memory up to 36 GB, three quarters above.</summary>
    internal static ulong EstimatedWorkingSet(ulong memory) => memory <= 36UL << 30 ? memory / 3 * 2 : memory / 4 * 3;

    /// <summary>The Ollama model suggested for a budget, the same steps as the Linux ollama role (choice_by_vram).</summary>
    internal static string SuggestOllamaModel(long budgetMb) => budgetMb switch
    {
        >= 22000 => "gemma4:26b",
        >= 11000 => "gemma4:12b",
        >= 7000 => "gemma4:e4b",
        _ => "gemma4:e2b"
    };

    internal static string SuggestWhisperModel(MacFacts facts) =>
        facts.AppleSilicon ? (ModelBudgetMb(facts) >= 10_000 ? "large-v3-turbo" : "small")
            : facts.MemoryBytes >= 8UL << 30 ? "small" : "base";

    /// <summary>The installed Ollama model to use without downloading: the suggestion when installed, otherwise the largest
    /// that fits the budget (or the smallest when none fits).</summary>
    internal static string? PickInstalledOllamaModel(IReadOnlyList<(string Name, long Bytes)> installed, long budgetMb, string suggested)
    {
        if (installed.Count == 0) return null;
        if (installed.Any(m => m.Name == suggested)) return suggested;
        var budget = Math.Max(budgetMb, 4096) * 1024L * 1024;
        var fitting = installed.Where(m => m.Bytes <= budget).OrderByDescending(m => m.Bytes).FirstOrDefault();
        return (fitting.Name ?? installed.OrderBy(m => m.Bytes).First().Name);
    }

    internal static string MachineJson(MacFacts facts, DateTimeOffset now)
    {
        var gb = Math.Round(facts.MemoryBytes / 1073741824.0, 1);
        var working = facts.GpuWorkingSetBytes is { } bytes ? Math.Round(bytes / 1073741824.0, 1) :
            facts.AppleSilicon ? Math.Round(EstimatedWorkingSet(facts.MemoryBytes) / 1073741824.0, 1) : (double?)null;
        var gpus = new List<object>();
        if (facts.AppleSilicon)
            gpus.Add(new { name = Clean(facts.GpuName ?? facts.Processor + " GPU"), vendor = "apple",
                memory_mb = working is { } w ? (int?)(int)(w * 1024) : null, driver = "Metal" });
        else if (facts.GpuName is { } name)
            gpus.Add(new { name = Clean(name), vendor = Vendor(name), memory_mb = (int?)null, driver = "Metal" });
        var report = new Dictionary<string, object?>
        {
            ["collected_at"] = now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["method"] = "native",
            ["platform"] = "macos",
            ["os_version"] = Clean(facts.OsVersion),
            ["architecture"] = facts.AppleSilicon ? "arm64" : "x64",
            ["operating_system"] = Clean($"macOS {facts.OsVersion}"),
            ["kernel"] = Clean($"Darwin {facts.Kernel}"),
            ["processor"] = Clean(facts.Processor),
            ["processor_threads"] = facts.Threads > 0 ? facts.Threads : null,
            ["memory_gb"] = gb > 0 ? gb : null,
            ["nvidia_containers"] = "no",
            ["gpus"] = gpus,
            ["features"] = facts.OnBattery ? new[] { PlatformFeatures.OnBattery } : Array.Empty<string>(),
            ["chip"] = Clean(facts.Processor),
            ["unified_memory"] = facts.AppleSilicon,
            ["gpu_working_set_gb"] = working
        };
        var text = JsonSerializer.Serialize(report.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value));
        if (GatewayMachineReport.Parse(Encoding.UTF8.GetBytes(text)) is null)
            throw new InvalidOperationException("The Mac machine report did not validate.");
        return text;
    }

    private static string Vendor(string gpu) =>
        gpu.Contains("AMD", StringComparison.OrdinalIgnoreCase) || gpu.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "amd"
        : gpu.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "intel"
        : gpu.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "nvidia" : "other";

    private static string Clean(string value)
    {
        var text = new string(value.Select(c => c is >= ' ' and <= '~' ? c : ' ').ToArray()).Trim();
        while (text.Contains("  ", StringComparison.Ordinal)) text = text.Replace("  ", " ", StringComparison.Ordinal);
        return text.Length == 0 ? "unknown" : text.Length > 120 ? text[..120] : text;
    }

    /// <summary>host.json for the Mac host: the same strict schema as Linux hosts, plus only the roles this Mac serves.</summary>
    internal static string ConfigJson(string hostId, MacHostLayout layout, string bindingMode, string origin, uint uid, uint gid,
        IEnumerable<HostRole> roles)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture,
            $"{{\"schemaVersion\":1,\"hostId\":{JsonSerializer.Serialize(hostId)},\"stateDirectory\":{JsonSerializer.Serialize(layout.State)},");
        builder.Append(CultureInfo.InvariantCulture,
            $"\"storageBackend\":\"{HostConfiguration.Backend}\",\"binding\":{{\"mode\":\"{bindingMode}\",\"origin\":{JsonSerializer.Serialize(origin)}}},");
        builder.Append(CultureInfo.InvariantCulture, $"\"serviceUid\":{uid},\"serviceGid\":{gid},\"roles\":[");
        builder.AppendJoin(',', roles.Select(r => $"{{\"kind\":\"{r.Kind}\",\"endpoint\":\"{r.Endpoint.OriginalString}\",\"model\":{JsonSerializer.Serialize(r.Model)}}}"));
        builder.Append("]}\n");
        return builder.ToString();
    }

    /// <summary>A launchd user agent: started at login and kept running (restarted when it exits with an error), in the
    /// foreground scheduling class so replies are not slowed by background throttling.</summary>
    internal static string AgentPlist(string label, IReadOnlyList<string> arguments, string logPath, bool restartAlways = false)
    {
        static string E(string value) => SecurityElement.Escape(value);
        var builder = new StringBuilder();
        builder.Append("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>

            """);
        builder.Append(CultureInfo.InvariantCulture, $"  <key>Label</key><string>{E(label)}</string>\n");
        builder.Append("  <key>ProgramArguments</key>\n  <array>\n");
        foreach (var argument in arguments) builder.Append(CultureInfo.InvariantCulture, $"    <string>{E(argument)}</string>\n");
        builder.Append("  </array>\n");
        builder.Append("  <key>RunAtLoad</key><true/>\n");
        builder.Append(restartAlways ? "  <key>KeepAlive</key><true/>\n"
            : "  <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>\n");
        builder.Append("  <key>ThrottleInterval</key><integer>10</integer>\n");
        builder.Append("  <key>ProcessType</key><string>Interactive</string>\n");
        builder.Append(CultureInfo.InvariantCulture, $"  <key>StandardOutPath</key><string>{E(logPath)}</string>\n");
        builder.Append(CultureInfo.InvariantCulture, $"  <key>StandardErrorPath</key><string>{E(logPath)}</string>\n");
        builder.Append("</dict>\n</plist>\n");
        return builder.ToString();
    }

    /// <summary>The command line that runs this gateway again: the self-contained executable, or dotnet and this assembly.</summary>
    internal static IReadOnlyList<string> SelfCommand()
    {
        var process = Environment.ProcessPath ?? throw new HostInputException();
        var assembly = typeof(MacHost).Assembly.Location;
        return Path.GetFileNameWithoutExtension(process) == "dotnet" && assembly.Length > 0 ? [process, assembly] : [process];
    }

    /// <summary>A private IPv4 address of this Mac's active network (Wi-Fi or Ethernet), preferring en0.</summary>
    internal static IPAddress? LanAddress()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => (n.Name, a.Address)))
            .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork && PrivateV4(x.Address))
            .OrderBy(x => x.Name == "en0" ? 0 : x.Name.StartsWith("en", StringComparison.Ordinal) ? 1 : 2)
            .ToArray();
        return candidates.Length == 0 ? null : candidates[0].Address;
    }

    internal static bool PrivateV4(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168);
    }

    internal static string DefaultHostId(string machineName)
    {
        var text = new string(machineName.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' ? c : '-')
            .ToArray()).Trim('-', '.', '_');
        if (text.Length > 64) text = text[..64];
        return HostConfiguration.Identifier(text) ? text : "mac-host";
    }

    internal static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellation)
    {
        if (args is [_, "help" or "--help" or "-h"]) { output.WriteLine(Help); return 0; }
        if (!OperatingSystem.IsMacOS())
        {
            output.WriteLine("macos.unsupported: these commands set up a Mac host and run only on macOS.");
            return 4;
        }
        return await MacHostCommands.RunAsync(args, output, cancellation);
    }
}

[SupportedOSPlatform("macos")]
internal static class MacHostCommands
{
    private static readonly UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellation)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home) || !home.StartsWith('/') || MacNative.UserId() == 0)
        {
            output.WriteLine("macos.account: run as your own (non-root) account.");
            return 3;
        }
        var layout = new MacHostLayout(home);
        try
        {
            return args[0] switch
            {
                "macos-setup" => await SetupAsync(layout, args[1..], output, cancellation),
                "macos-pair" => await PairAsync(layout, args[1..], output, cancellation),
                "macos-status" when args.Length == 1 => await StatusAsync(layout, output, cancellation),
                "macos-machine" when args.Length == 1 => Machine(layout, output),
                "macos-uninstall" when args.Length == 1 => Uninstall(layout, output),
                _ => throw new HostInputException()
            };
        }
        catch (HostInputException)
        {
            output.WriteLine("invalid input. " + MacHost.Help);
            return 2;
        }
    }

    private static async Task<int> SetupAsync(MacHostLayout layout, string[] args, TextWriter output, CancellationToken cancellation)
    {
        string? address = null, hostId = null, ollamaModel = null, sttModel = null;
        var loopback = false; var noOllama = false; var noStt = false; int? port = null;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new HostInputException();
            switch (args[i])
            {
                case "--address" when address is null && !loopback: address = Next(); break;
                case "--loopback" when address is null: loopback = true; break;
                case "--port" when port is null:
                    port = int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 1024 and <= 65535
                        ? p : throw new HostInputException(); break;
                case "--host-id" when hostId is null: hostId = Next(); if (!HostConfiguration.Identifier(hostId)) throw new HostInputException(); break;
                case "--ollama-model" when ollamaModel is null && !noOllama:
                    ollamaModel = Next(); if (!HostConfiguration.ModelToken(ollamaModel)) throw new HostInputException(); break;
                case "--no-ollama" when ollamaModel is null: noOllama = true; break;
                case "--stt-model" when sttModel is null && !noStt:
                    sttModel = Next(); if (!MacHost.WhisperModels.ContainsKey(sttModel)) throw new HostInputException(); break;
                case "--no-stt" when sttModel is null: noStt = true; break;
                default: throw new HostInputException();
            }
        }
        if (address is not null && (!IPAddress.TryParse(address, out var parsed) || !MacHost.PrivateV4(parsed)))
            throw new HostInputException();

        output.WriteLine("Martlet Mac host setup");
        foreach (var directory in new[] { layout.Base, layout.ConfigDirectory, layout.Private, layout.Models, layout.Logs })
            EnsurePrivateDirectory(directory);
        Directory.CreateDirectory(layout.Agents);

        // Re-running setup keeps this host's ID and address unless told otherwise.
        string? previousOrigin = null, previousId = null;
        if (File.Exists(layout.ConfigPath))
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(layout.ConfigPath));
                previousId = document.RootElement.GetProperty("hostId").GetString();
                previousOrigin = document.RootElement.GetProperty("binding").GetProperty("origin").GetString();
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { }
        var stateExists = Directory.Exists(layout.State);
        hostId ??= previousId ?? MacHost.DefaultHostId(Environment.MachineName);
        if (stateExists && previousId is not null && hostId != previousId)
        {
            output.WriteLine($"macos.host_id: this Mac already hosts as {previousId}; keep that ID (or remove {layout.State} to start over).");
            return 2;
        }
        string origin, mode;
        if (loopback) { origin = $"https://127.0.0.1:{port ?? DefaultPort(previousOrigin)}"; mode = "loopback"; }
        else
        {
            var previousUri = previousOrigin is null ? null : new Uri(previousOrigin);
            var selected = address ?? (previousUri is { Host: not "127.0.0.1" } ? previousUri.Host : MacHost.LanAddress()?.ToString());
            if (selected is null)
            {
                output.WriteLine("macos.address: no private network address found; connect to your home network or pass --address <IPv4>.");
                return 2;
            }
            origin = $"https://{selected}:{port ?? DefaultPort(previousOrigin)}";
            mode = "privateIp";
        }
        if (stateExists && previousOrigin is not null && previousOrigin != origin)
        {
            output.WriteLine($"macos.address_changed: this host's certificate is for {previousOrigin}. Changing it needs a rebind, " +
                "which the Mac host can't do yet; run setup again without --address/--port, or remove the state to pair again.");
            return 2;
        }

        var facts = MacNative.Collect();
        var budget = MacHost.ModelBudgetMb(facts);
        output.WriteLine($"This Mac: {facts.Processor}, {facts.MemoryBytes / 1073741824.0:0.#} GB " +
            (facts.AppleSilicon ? $"unified memory (GPU working set about {budget / 1024.0:0.#} GB)" : "memory (Intel: models run on the CPU)") + ".");

        var roles = new List<HostRole>();
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        if (!noOllama)
        {
            var ollama = await OllamaAsync(http, cancellation);
            if (ollama is null)
                output.WriteLine("Thinking not offered: Ollama isn't running on 127.0.0.1:11434. Install it from https://ollama.com/download " +
                    "(it uses the Mac's GPU on Apple silicon), open it once, then run setup again.");
            else
            {
                var suggested = MacHost.SuggestOllamaModel(budget);
                var model = ollamaModel ?? MacHost.PickInstalledOllamaModel(ollama.Value.Models, budget, suggested);
                if (model is null)
                    output.WriteLine($"Thinking not offered yet: Ollama {ollama.Value.Version} has no models. Suggested for this Mac: {suggested}. " +
                        $"Run setup again with --ollama-model {suggested} to download it through Ollama (its license applies), or pull another.");
                else
                {
                    if (!ollama.Value.Models.Any(m => m.Name == model))
                    {
                        output.WriteLine($"Downloading {model} through Ollama (once; its license applies)...");
                        if (!await PullAsync(http, model, cancellation))
                        {
                            output.WriteLine($"macos.ollama_pull: Ollama couldn't download {model}. Check the name at https://ollama.com/library.");
                            return 4;
                        }
                    }
                    roles.Add(new("ollama", new Uri($"http://127.0.0.1:{MacHost.OllamaPort}/"), model));
                    output.WriteLine($"Thinking: Ollama {ollama.Value.Version} with {model}" +
                        (facts.AppleSilicon ? " on the GPU (Metal)." : " on the CPU (Intel Macs have no GPU acceleration in Ollama)."));
                }
            }
        }
        var whisper = MacNative.WhisperServer();
        if (!noStt)
        {
            if (whisper is null)
                output.WriteLine("Listening not offered: whisper.cpp's server isn't installed. Install it with Homebrew (brew install whisper-cpp; " +
                    "Metal on Apple silicon), then run setup again.");
            else
            {
                var model = sttModel ?? MacHost.SuggestWhisperModel(facts);
                var file = await WhisperModelAsync(http, layout, model, output, cancellation);
                var threads = Math.Clamp(facts.Threads, 1, 8).ToString(CultureInfo.InvariantCulture);
                WriteAgent(layout, MacHostLayout.WhisperLabel, [whisper, "--host", "127.0.0.1", "--port", MacHost.WhisperPort.ToString(CultureInfo.InvariantCulture),
                    "-m", file, "-l", "auto", "-t", threads, "--suppress-nst"], $"{layout.Logs}/whisper.log", restartAlways: true);
                Launchctl(output, true, "bootout", $"gui/{MacNative.UserId()}/{MacHostLayout.WhisperLabel}");
                if (Launchctl(output, false, "bootstrap", $"gui/{MacNative.UserId()}", layout.AgentPath(MacHostLayout.WhisperLabel)) != 0)
                    return 4;
                roles.Add(new("stt", new Uri($"http://127.0.0.1:{MacHost.WhisperPort}/"), model));
                output.WriteLine($"Listening: whisper.cpp ({whisper}) with the {model} model" +
                    (facts.AppleSilicon ? " on the GPU (Metal)." : " on the CPU."));
            }
        }
        else if (File.Exists(layout.AgentPath(MacHostLayout.WhisperLabel)))
        {
            Launchctl(output, true, "bootout", $"gui/{MacNative.UserId()}/{MacHostLayout.WhisperLabel}");
            File.Delete(layout.AgentPath(MacHostLayout.WhisperLabel));
        }
        output.WriteLine("Not offered on a Mac: F5 (PyTorch) and Audio2Face need an NVIDIA GPU, as do XTTS, Chatterbox, GPT-SoVITS, Dia, singing and pictures.");

        WritePrivateFile(layout.ConfigPath, MacHost.ConfigJson(hostId, layout, mode, origin, MacNative.UserId(), MacNative.GroupId(), roles));
        WritePrivateFile(layout.MachinePath, MacHost.MachineJson(facts, DateTimeOffset.UtcNow));

        // The gateway holds its state alone: stop the agent before opening it here.
        Launchctl(output, true, "bootout", $"gui/{MacNative.UserId()}/{MacHostLayout.GatewayLabel}");
        var owner = stateExists ? "owner-approve" : "owner-init";
        var code = await HostApplication.RunAsync([owner, "--config", layout.ConfigPath], output, cancellation);
        if (code != 0) return code;
        WriteAgent(layout, MacHostLayout.GatewayLabel, [.. MacHost.SelfCommand(), "serve", "--config", layout.ConfigPath], $"{layout.Logs}/gateway.log");
        if (Launchctl(output, false, "bootstrap", $"gui/{MacNative.UserId()}", layout.AgentPath(MacHostLayout.GatewayLabel)) != 0)
            return 4;
        output.WriteLine($"Hosting as {hostId} at {origin} while you are logged in (launchd agent {MacHostLayout.GatewayLabel}).");
        var healthy = await WaitHealthyAsync(layout, cancellation);
        output.WriteLine(healthy ? "The gateway answers." : $"The gateway hasn't answered yet; see {layout.Logs}/gateway.log.");
        output.WriteLine(stateExists ? "Paired desktops keep their pairing." :
            "Next: pair a desktop. Run this host's macos-pair command and type the code in Martlet (Devices > Add a computer > Pair).");
        return healthy ? 0 : 6;
    }

    private static int DefaultPort(string? previousOrigin) =>
        previousOrigin is not null && Uri.TryCreate(previousOrigin, UriKind.Absolute, out var uri) && uri.Port > 0 ? uri.Port : MacHost.DefaultPort;

    private static async Task<int> PairAsync(MacHostLayout layout, string[] extra, TextWriter output, CancellationToken cancellation)
    {
        if (!File.Exists(layout.ConfigPath)) { output.WriteLine("macos.not_set_up: run macos-setup first."); return 3; }
        Launchctl(output, true, "bootout", $"gui/{MacNative.UserId()}/{MacHostLayout.GatewayLabel}");
        try { return await HostApplication.RunAsync(["owner-pair", "--config", layout.ConfigPath, .. extra], output, cancellation); }
        finally
        {
            if (File.Exists(layout.AgentPath(MacHostLayout.GatewayLabel)))
                Launchctl(output, false, "bootstrap", $"gui/{MacNative.UserId()}", layout.AgentPath(MacHostLayout.GatewayLabel));
        }
    }

    private static async Task<int> StatusAsync(MacHostLayout layout, TextWriter output, CancellationToken cancellation)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        var ollama = await OllamaAsync(http, cancellation);
        var configured = File.Exists(layout.ConfigPath);
        var health = configured && await WaitHealthyAsync(layout, cancellation, attempts: 1);
        var roles = new List<object>();
        if (configured)
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(layout.ConfigPath));
                foreach (var role in document.RootElement.GetProperty("roles").EnumerateArray())
                    roles.Add(new { kind = role.GetProperty("kind").GetString(), model = role.GetProperty("model").GetString() });
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { }
        output.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = 1, platform = "macos", configured, config = layout.ConfigPath,
            gatewayAgent = Launchctl(TextWriter.Null, true, "print", $"gui/{MacNative.UserId()}/{MacHostLayout.GatewayLabel}") == 0 ? "loaded" : "not-loaded",
            whisperAgent = Launchctl(TextWriter.Null, true, "print", $"gui/{MacNative.UserId()}/{MacHostLayout.WhisperLabel}") == 0 ? "loaded" : "not-loaded",
            gateway = health ? "ready" : "not-answering",
            ollama = ollama is { } o ? new { version = o.Version, models = o.Models.Select(m => m.Name).ToArray() } : null,
            whisperServer = MacNative.WhisperServer(),
            roles
        }));
        return 0;
    }

    private static int Machine(MacHostLayout layout, TextWriter output)
    {
        if (!Directory.Exists(layout.ConfigDirectory)) { output.WriteLine("macos.not_set_up: run macos-setup first."); return 3; }
        var json = MacHost.MachineJson(MacNative.Collect(), DateTimeOffset.UtcNow);
        WritePrivateFile(layout.MachinePath, json);
        output.WriteLine(json);
        output.WriteLine("The gateway serves it after its next start (macos-setup or logging in again restarts it).");
        return 0;
    }

    private static int Uninstall(MacHostLayout layout, TextWriter output)
    {
        foreach (var label in new[] { MacHostLayout.GatewayLabel, MacHostLayout.WhisperLabel })
        {
            Launchctl(output, true, "bootout", $"gui/{MacNative.UserId()}/{label}");
            if (File.Exists(layout.AgentPath(label))) File.Delete(layout.AgentPath(label));
        }
        output.WriteLine($"Stopped hosting. The identity, pairings and models are kept in {layout.Base}; " +
            "delete that folder to remove them too (paired desktops then need to pair again).");
        return 0;
    }

    private static async Task<(string Version, IReadOnlyList<(string Name, long Bytes)> Models)?> OllamaAsync(HttpClient http, CancellationToken cancellation)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var version = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{MacHost.OllamaPort}/api/version", timeout.Token));
            using var tags = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{MacHost.OllamaPort}/api/tags", timeout.Token));
            var models = tags.RootElement.GetProperty("models").EnumerateArray()
                .Select(m => (m.GetProperty("name").GetString() ?? "", m.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0))
                .Where(m => HostConfiguration.ModelToken(m.Item1)).ToArray();
            return (version.RootElement.GetProperty("version").GetString() ?? "unknown", models);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
            or OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<bool> PullAsync(HttpClient http, string model, CancellationToken cancellation)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { model, stream = false }), Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromHours(3));
        using var response = await http.PostAsync($"http://127.0.0.1:{MacHost.OllamaPort}/api/pull", content, timeout.Token);
        return response.IsSuccessStatusCode;
    }

    private static async Task<string> WhisperModelAsync(HttpClient http, MacHostLayout layout, string model, TextWriter output,
        CancellationToken cancellation)
    {
        var expected = MacHost.WhisperModels[model];
        var file = $"{layout.Models}/ggml-{model}.bin";
        if (File.Exists(file) && File.Exists(file + ".sha256") && File.ReadAllText(file + ".sha256").Trim() == expected) return file;
        output.WriteLine($"Downloading the whisper {model} model (once, from Hugging Face at a pinned revision)...");
        var part = file + ".part";
        using (var response = await http.GetAsync(
            $"https://huggingface.co/ggerganov/whisper.cpp/resolve/{MacHost.WhisperRevision}/ggml-{model}.bin", HttpCompletionOption.ResponseHeadersRead, cancellation))
        {
            if (!response.IsSuccessStatusCode) throw new IOException($"The whisper {model} model download failed ({(int)response.StatusCode}).");
            await using var source = await response.Content.ReadAsStreamAsync(cancellation);
            await using var target = new FileStream(part, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = PrivateFile });
            await source.CopyToAsync(target, cancellation);
        }
        string actual;
        await using (var check = File.OpenRead(part)) actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, cancellation));
        if (actual != expected)
        {
            File.Delete(part);
            throw new IOException("The downloaded whisper model does not match its pinned SHA-256.");
        }
        File.Move(part, file, overwrite: true);
        WritePrivateFile(file + ".sha256", expected + "\n");
        return file;
    }

    private static async Task<bool> WaitHealthyAsync(MacHostLayout layout, CancellationToken cancellation, int attempts = 15)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (i > 0) await Task.Delay(TimeSpan.FromSeconds(2), cancellation);
            if (await HostApplication.RunAsync(["health", "--config", layout.ConfigPath], TextWriter.Null, cancellation) == 0) return true;
        }
        return false;
    }

    private static void EnsurePrivateDirectory(string path)
    {
        if (!Directory.Exists(path)) Directory.CreateDirectory(path, Private);
        File.SetUnixFileMode(path, Private);
    }

    private static void WritePrivateFile(string path, string text)
    {
        var temporary = path + ".tmp";
        File.Delete(temporary);
        using (var stream = new FileStream(temporary, new FileStreamOptions
               { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = PrivateFile }))
        {
            stream.Write(Encoding.UTF8.GetBytes(text));
            stream.Flush(true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static void WriteAgent(MacHostLayout layout, string label, IReadOnlyList<string> arguments, string log, bool restartAlways = false)
    {
        var path = layout.AgentPath(label);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, MacHost.AgentPlist(label, arguments, log, restartAlways));
        File.Move(temporary, path, overwrite: true);
    }

    private static int Launchctl(TextWriter output, bool quiet, params string[] arguments)
    {
        var start = new ProcessStartInfo("/bin/launchctl") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("launchctl did not start.");
        var text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 && !quiet)
            output.WriteLine($"macos.launchctl: launchctl {arguments[0]} failed ({process.ExitCode}): {text.Trim()}");
        return process.ExitCode;
    }
}

/// <summary>What macOS says about this Mac (sysctl, Metal) and the keep-awake assertion the gateway holds while it serves.</summary>
[SupportedOSPlatform("macos")]
internal static class MacNative
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string Metal = "/System/Library/Frameworks/Metal.framework/Metal";
    private const string IoKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    internal static uint UserId() => GetUser();
    internal static uint GroupId() => GetGroup();

    internal static MacFacts Collect()
    {
        var arm = Integer("hw.optional.arm64") == 1;
        var (gpu, working) = MetalDevice();
        return new(Text("kern.osproductversion") ?? "unknown", Text("kern.osrelease") ?? "unknown",
            Text("machdep.cpu.brand_string") ?? (arm ? "Apple silicon" : "Intel"), arm, (int)(Integer("hw.logicalcpu") ?? 0),
            (ulong)(Integer("hw.memsize") ?? 0), gpu, working, OnBattery());
    }

    /// <summary>whisper.cpp's server from Homebrew (Apple silicon or Intel prefix) or on PATH.</summary>
    internal static string? WhisperServer()
    {
        var candidates = new List<string> { "/opt/homebrew/bin/whisper-server", "/usr/local/bin/whisper-server" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
            if (directory.StartsWith('/')) candidates.Add($"{directory}/whisper-server");
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool OnBattery()
    {
        try
        {
            var start = new ProcessStartInfo("/usr/bin/pmset", "-g batt") { RedirectStandardOutput = true };
            using var process = Process.Start(start);
            if (process is null) return false;
            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return text.Contains("'Battery Power'", StringComparison.Ordinal);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException) { return false; }
    }

    private static string? Text(string name)
    {
        nuint length = 0;
        if (SystemControl(name, null, ref length, IntPtr.Zero, 0) != 0 || length is 0 or > 4096) return null;
        var buffer = new byte[(int)length];
        if (SystemControl(name, buffer, ref length, IntPtr.Zero, 0) != 0) return null;
        var text = Encoding.UTF8.GetString(buffer, 0, (int)length).TrimEnd('\0').Trim();
        return text.Length == 0 ? null : text;
    }

    private static long? Integer(string name)
    {
        var buffer = new byte[8];
        nuint length = 8;
        if (SystemControl(name, buffer, ref length, IntPtr.Zero, 0) != 0) return null;
        return length switch { 4 => BitConverter.ToInt32(buffer, 0), 8 => BitConverter.ToInt64(buffer, 0), _ => null };
    }

    /// <summary>The default Metal device's name and recommendedMaxWorkingSetSize (how much memory the GPU may use well).</summary>
    private static (string? Name, ulong? WorkingSet) MetalDevice()
    {
        try
        {
            var device = CreateSystemDefaultDevice();
            if (device == IntPtr.Zero) return (null, null);
            try
            {
                var working = SendUInt64(device, Selector("recommendedMaxWorkingSetSize"));
                var name = SendPointer(device, Selector("name"));
                var utf8 = name == IntPtr.Zero ? IntPtr.Zero : SendPointer(name, Selector("UTF8String"));
                return (utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8), working > 0 ? working : null);
            }
            finally { _ = SendPointer(device, Selector("release")); }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return (null, null); }
    }

    /// <summary>Keeps the Mac from idle sleep while the gateway serves (the display may still sleep); released at exit.</summary>
    internal static IDisposable? KeepAwake()
    {
        try
        {
            var type = CFStringCreateWithCString(IntPtr.Zero, "PreventUserIdleSystemSleep", 0x08000100);
            var name = CFStringCreateWithCString(IntPtr.Zero, "Martlet is hosting for your other computers", 0x08000100);
            try { return IOPMAssertionCreateWithName(type, 255, name, out var id) == 0 ? new Assertion(id) : null; }
            finally { CFRelease(type); CFRelease(name); }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    private sealed class Assertion(uint id) : IDisposable
    {
        private int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) _ = IOPMAssertionRelease(id); }
    }

    private static IntPtr Selector(string name) => RegisterSelector(name);

    [DllImport(LibSystem, EntryPoint = "getuid")] private static extern uint GetUser();
    [DllImport(LibSystem, EntryPoint = "getgid")] private static extern uint GetGroup();
    [DllImport(LibSystem, EntryPoint = "sysctlbyname")]
    private static extern int SystemControl(string name, byte[]? value, ref nuint length, IntPtr newValue, nuint newLength);
    [DllImport(Metal, EntryPoint = "MTLCreateSystemDefaultDevice")] private static extern IntPtr CreateSystemDefaultDevice();
    [DllImport(ObjC, EntryPoint = "sel_registerName")] private static extern IntPtr RegisterSelector(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern ulong SendUInt64(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPointer(IntPtr receiver, IntPtr selector);
    [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr value);
    [DllImport(IoKit)] private static extern int IOPMAssertionCreateWithName(IntPtr type, uint level, IntPtr name, out uint id);
    [DllImport(IoKit)] private static extern int IOPMAssertionRelease(uint id);
}
