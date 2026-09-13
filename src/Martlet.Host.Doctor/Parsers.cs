using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Martlet.Host.Doctor;

internal sealed record Parsed<T>(FindingCode Code, T? Value) where T : class;

internal static class Parsers
{
    private static readonly Regex NumericVersion = Pattern(@"^[0-9]{1,4}(\.[0-9]{1,4}){1,3}$");
    private static readonly Regex OsVersion = Pattern(@"^[0-9]{1,4}(\.[0-9]{1,4}){0,3}$");
    private static readonly Regex Kernel = Pattern(@"^([0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,4})(-[0-9]{1,5})?([-.][A-Za-z0-9]+)*$");
    private static readonly Regex PackageVersion = Pattern(@"^(?:[0-9]{1,3}:)?([0-9]{1,4}\.[0-9]{1,4}(?:\.[0-9]{1,4})?)(?:[+~.-][A-Za-z0-9.+~:-]{1,96})?$");
    private static readonly Regex GpuModel = Pattern(@"^(?:NVIDIA )?(?:(?:GeForce )?(?:RTX|GTX) [0-9]{3,4}(?: Ti| SUPER| Laptop GPU)?|(?:Tesla |Quadro )?[A-Z][0-9]{2,4}(?:-SXM[0-9])?(?:-[0-9]{1,3}GB)?(?: PCIe| SXM| SXM[0-9]| PCIE| NVL)?|(?:Quadro )?RTX (?:A[0-9]{3,4}|[0-9]{3,4})(?:,? Ada Generation)?|TITAN (?:V|RTX|X))$");
    private static Regex Pattern(string text) => new(text, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static bool SafeVersion(string? version) => version is not null && NumericVersion.IsMatch(version);
    internal static bool SafeOsVersion(string? version) => version is not null && OsVersion.IsMatch(version);
    internal static bool SafeGpu(GpuData gpu) => gpu is not null && gpu.Model.Length <= 96 &&
        GpuModel.IsMatch(gpu.Model) && SafeVersion(gpu.DriverVersion) && gpu.MemoryMiB is > 0 and <= 1048576;

    public static Parsed<PlatformData> Platform(PlatformData runtime, string osRelease, string kernel)
    {
        var selected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in osRelease.Split('\n'))
        {
            var equal = line.IndexOf('=');
            if (equal <= 0) continue;
            var key = line[..equal].Trim();
            if (key is not ("ID" or "VERSION_ID")) continue;
            var value = line[(equal + 1)..].Trim();
            if (value.Length is > 128 or 0 || selected.ContainsKey(key)) return Bad<PlatformData>();
            if (value[0] is '"' or '\'')
            {
                if (value.Length < 2 || value[^1] != value[0]) return Bad<PlatformData>();
                value = value[1..^1];
            }
            selected.Add(key, value);
        }
        if (!selected.TryGetValue("ID", out var id) || !Pattern(@"^[a-z][a-z0-9-]{0,31}$").IsMatch(id))
            return Bad<PlatformData>();
        selected.TryGetValue("VERSION_ID", out var version);
        if (version is not null && !SafeOsVersion(version)) return Bad<PlatformData>();
        kernel = kernel.Trim();
        var match = Kernel.Match(kernel);
        if (!match.Success) return Bad<PlatformData>();
        var flavor = kernel.Contains("microsoft", StringComparison.OrdinalIgnoreCase) ? KernelFlavor.Wsl
            : kernel.EndsWith("-generic", StringComparison.Ordinal) ? KernelFlavor.Generic
            : kernel.EndsWith("-azure", StringComparison.Ordinal) ? KernelFlavor.Azure
            : kernel.EndsWith("-lowlatency", StringComparison.Ordinal) ? KernelFlavor.LowLatency
            : kernel.EndsWith("-virtual", StringComparison.Ordinal) ? KernelFlavor.Virtual : KernelFlavor.Other;
        var data = runtime with
        {
            Distribution = id switch { "ubuntu" => Distro.Ubuntu, "debian" => Distro.Debian, _ => Distro.Other },
            Version = version,
            KernelRelease = match.Groups[1].Value + match.Groups[2].Value,
            KernelFlavor = flavor
        };
        return new(id == "ubuntu" && version == "24.04" && flavor != KernelFlavor.Wsl
            ? FindingCode.HOST_OBSERVED : FindingCode.HOST_UNQUALIFIED_PLATFORM, data);
    }

    public static Parsed<CpuData> Cpu(string text)
    {
        var processors = new HashSet<int>();
        var flagsCount = 0;
        var sse = true; var avx = true; var avx2 = true; var avx512 = true;
        foreach (var line in text.Split('\n'))
        {
            var pair = line.Split(':', 2);
            if (pair.Length != 2) continue;
            if (pair[0].Trim() == "processor")
            {
                if (!int.TryParse(pair[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
                    id < 0 || !processors.Add(id)) return Bad<CpuData>();
                if (processors.Count > 4096) return Limit<CpuData>();
            }
            if (pair[0].Trim() != "flags") continue;
            var flags = pair[1].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (flags.Length > 512 || ++flagsCount > 4096) return Limit<CpuData>();
            sse &= flags.Contains("sse4_2"); avx &= flags.Contains("avx");
            avx2 &= flags.Contains("avx2"); avx512 &= flags.Contains("avx512f");
        }
        if (processors.Count == 0 || flagsCount != processors.Count) return Bad<CpuData>();
        return Good(new CpuData(processors.Count, sse, avx, avx2, avx512));
    }

    public static Parsed<ContextData> Context(KernelFlavor kernel, string cgroup, bool marker)
    {
        var lines = cgroup.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 256) return Limit<ContextData>();
        if (lines.Length == 0 || lines.Any(line => !Pattern(@"^[0-9]{1,5}:[a-z0-9_,=]*:/[^\r\n]*$").IsMatch(line)))
            return Bad<ContextData>();
        var context = kernel == KernelFlavor.Wsl ? ExecutionContext.Wsl :
            marker || cgroup.Contains("docker", StringComparison.Ordinal) || cgroup.Contains("kubepods", StringComparison.Ordinal) ||
            cgroup.Contains("libpod", StringComparison.Ordinal) || cgroup.Contains("lxc", StringComparison.Ordinal)
            ? ExecutionContext.ContainerIndicators : ExecutionContext.NoContainerIndicators;
        return new(context == ExecutionContext.NoContainerIndicators ? FindingCode.HOST_OBSERVED : FindingCode.HOST_UNQUALIFIED_PLATFORM, new(context));
    }

    public static Parsed<MemoryData> Memory(string text)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var pair = line.Split(':', 2);
            if (pair.Length != 2 || pair[0] is not ("MemTotal" or "MemAvailable" or "SwapTotal" or "SwapFree")) continue;
            var fields = pair[1].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length != 2 || fields[1] != "kB" ||
                !long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var kib) ||
                kib < 0 || kib > long.MaxValue / 1024 || !values.TryAdd(pair[0], kib * 1024))
                return Bad<MemoryData>();
        }
        if (values.Count != 4 || values["MemTotal"] <= 0 || values["MemAvailable"] > values["MemTotal"] ||
            values["SwapFree"] > values["SwapTotal"]) return Bad<MemoryData>();
        return Good(new MemoryData(values["MemTotal"], values["MemAvailable"], values["SwapTotal"], values["SwapFree"]));
    }

    public static Parsed<GpuList> Nvidia(CommandResult command)
    {
        if (command.Status != ReadStatus.Success) return new(Code(command.Status), null);
        if (command.ExitCode != 0)
            return new(command.ExitCode switch
            {
                4 => FindingCode.HOST_PERMISSION_DENIED,
                9 or 12 => FindingCode.HOST_DRIVER_UNAVAILABLE,
                6 => FindingCode.HOST_GPU_NOT_VISIBLE,
                3 or 13 => FindingCode.HOST_UNSUPPORTED_RESPONSE,
                _ => FindingCode.HOST_COMMAND_FAILED
            }, null);
        var rows = command.Output.Trim().Split('\n');
        if (rows.Length > 16) return Limit<GpuList>();
        var gpus = ImmutableArray.CreateBuilder<GpuData>();
        foreach (var line in rows)
        {
            var fields = Csv(line.TrimEnd('\r'));
            if (fields is null || fields.Count != 3) return Bad<GpuList>();
            if (fields.Any(f => f is "[N/A]" or "[Not Supported]" or "N/A")) return new(FindingCode.HOST_UNSUPPORTED_RESPONSE, null);
            if (!long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var memory)) return Bad<GpuList>();
            var gpu = new GpuData(fields[0], fields[1], memory);
            if (!SafeGpu(gpu)) return Bad<GpuList>();
            gpus.Add(gpu);
        }
        return Good(new GpuList(gpus.ToImmutable()));
    }

    private static List<string>? Csv(string line)
    {
        var result = new List<string>();
        var field = new StringBuilder();
        var quoted = false; var closed = false; var began = false;
        for (var i = 0; i <= line.Length; i++)
        {
            var c = i == line.Length ? ',' : line[i];
            if (quoted)
            {
                if (i == line.Length) return null;
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                    else { quoted = false; closed = true; }
                }
                else field.Append(c);
            }
            else if (c == ',')
            {
                result.Add(field.ToString().Trim()); field.Clear(); began = false; closed = false;
                if (result.Count > 3) return null;
            }
            else if (c == '"' && !began) { quoted = true; began = true; }
            else if (c == '"' || closed && !char.IsWhiteSpace(c)) return null;
            else { field.Append(c); began |= !char.IsWhiteSpace(c); }
        }
        return result;
    }

    public static Parsed<PackageList> Packages(CommandResult command)
    {
        if (command.Status != ReadStatus.Success) return new(Code(command.Status), null);
        if (command.ExitCode is not (0 or 1)) return new(FindingCode.HOST_COMMAND_FAILED, null);
        var entries = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = command.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 6) return Limit<PackageList>();
        foreach (var line in lines)
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length != 3) return Bad<PackageList>();
            var name = parts[0].EndsWith(":amd64", StringComparison.Ordinal) ? parts[0][..^6] : parts[0];
            if (name is not ("docker-ce" or "docker.io" or "docker-compose-plugin" or "docker-compose-v2" or
                "nvidia-container-toolkit" or "nvidia-container-toolkit-base") || !seen.Add(name))
                return Bad<PackageList>();
            if (parts[2] is "not-installed" or "config-files") continue;
            if (parts[2] != "installed") return new(FindingCode.HOST_INCOMPLETE, null);
            var match = PackageVersion.Match(parts[1]);
            if (!match.Success) return Bad<PackageList>();
            entries.Add(name, match.Groups[1].Value);
        }
        return Good(new PackageList(entries.ToImmutable()));
    }

    public static Parsed<DockerAccessData> DockerAccess(string status, string groups, BinaryPresence socket)
    {
        var selected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in status.Split('\n'))
        {
            var pair = line.Split(':', 2);
            if (pair.Length == 2 && pair[0] is "Uid" or "Gid" or "Groups" && !selected.TryAdd(pair[0], pair[1]))
                return Bad<DockerAccessData>();
        }
        if (selected.Count != 3) return Bad<DockerAccessData>();
        var parsed = new Dictionary<string, uint[]>(StringComparer.Ordinal);
        foreach (var item in selected)
        {
            var parts = item.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 256 || item.Key != "Groups" && parts.Length != 4) return Bad<DockerAccessData>();
            var values = new uint[parts.Length];
            for (var i = 0; i < parts.Length; i++)
                if (!uint.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i])) return Bad<DockerAccessData>();
            parsed.Add(item.Key, values);
        }
        uint? dockerGid = null;
        foreach (var line in groups.Split('\n'))
        {
            if (!line.StartsWith("docker:", StringComparison.Ordinal)) continue;
            var parts = line.Split(':');
            if (parts.Length != 4 || dockerGid is not null ||
                !uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var gid)) return Bad<DockerAccessData>();
            dockerGid = gid;
        }
        var data = new DockerAccessData(parsed["Uid"][1] == 0,
            dockerGid is { } d && (parsed["Groups"].Contains(d) || parsed["Gid"][1] == d), socket);
        return new(data.EffectiveRoot || data.DockerGroupMember ? FindingCode.HOST_DOCKER_PRIVILEGE : FindingCode.HOST_INCOMPLETE, data);
    }

    public static Parsed<NetworkData> Network(string dev, string fib, string inet6)
    {
        var devLines = dev.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (devLines.Length < 2 || !devLines[0].Contains("Inter-", StringComparison.Ordinal) ||
            !devLines[1].Contains("face", StringComparison.Ordinal)) return Bad<NetworkData>();
        var interfaces = devLines.Skip(2).Count(line => line.Contains(':'));
        if (interfaces > 64) return Limit<NetworkData>();
        if (interfaces == 0 || devLines.Length != interfaces + 2) return Bad<NetworkData>();
        if (!fib.Contains("Main:", StringComparison.Ordinal) && !fib.Contains("Local:", StringComparison.Ordinal)) return Bad<NetworkData>();
        var addresses = new HashSet<IPAddress>();
        IPAddress? preceding = null;
        foreach (var line in fib.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("|-- ", StringComparison.Ordinal))
                preceding = IPAddress.TryParse(trimmed[4..], out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? address : null;
            else if (trimmed.EndsWith("host LOCAL", StringComparison.Ordinal))
            {
                if (preceding is null) return Bad<NetworkData>();
                addresses.Add(preceding);
            }
            if (addresses.Count > 256) return Limit<NetworkData>();
        }
        foreach (var line in inet6.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 6 || parts[0].Length != 32 || parts[0].Any(c => !Uri.IsHexDigit(c))) return Bad<NetworkData>();
            addresses.Add(new IPAddress(Convert.FromHexString(parts[0])));
            if (addresses.Count > 256) return Limit<NetworkData>();
        }
        if (addresses.Count == 0) return new(FindingCode.HOST_INCOMPLETE, null);
        var counts = new int[8];
        foreach (var address in addresses)
        {
            var bytes = address.GetAddressBytes();
            if (bytes.Length == 4)
                counts[IPAddress.IsLoopback(address) ? 0 : bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                    bytes[0] == 192 && bytes[1] == 168 ? 1 : bytes[0] == 169 && bytes[1] == 254 ? 2 : 3]++;
            else counts[IPAddress.IsLoopback(address) ? 4 : address.IsIPv6LinkLocal ? 5 : (bytes[0] & 0xfe) == 0xfc ? 6 : 7]++;
        }
        return Good(new NetworkData(interfaces, counts[0], counts[1], counts[2], counts[3], counts[4], counts[5], counts[6], counts[7]));
    }

    public static Parsed<PortData> Port(int port, params string[] tables)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var occupied = false;
        for (var table = 0; table < tables.Length; table++)
        {
            var rows = tables[table].Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (rows.Length == 0 || !rows[0].Contains("local_address", StringComparison.Ordinal)) return Bad<PortData>();
            if (rows.Length > 4097) return Limit<PortData>();
            foreach (var row in rows.Skip(1))
            {
                var fields = row.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 4 || fields[3].Length != 2 || fields[3].Any(c => !Uri.IsHexDigit(c))) return Bad<PortData>();
                var endpoint = fields[1].Split(':');
                if (endpoint.Length != 2 || endpoint[0].Length != (table % 2 == 0 ? 8 : 32) ||
                    endpoint[0].Any(c => !Uri.IsHexDigit(c)) || endpoint[1].Length != 4 ||
                    !ushort.TryParse(endpoint[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var localPort))
                    return Bad<PortData>();
                occupied |= localPort == port;
            }
        }
        return new(occupied ? FindingCode.HOST_PORT_IN_USE : FindingCode.HOST_OBSERVED, new(port, occupied));
    }

    public static FindingCode Code(ReadStatus status) => status switch
    {
        ReadStatus.Success => FindingCode.HOST_OBSERVED,
        ReadStatus.Missing => FindingCode.HOST_MISSING,
        ReadStatus.PermissionDenied => FindingCode.HOST_PERMISSION_DENIED,
        ReadStatus.Unsupported => FindingCode.HOST_UNSUPPORTED_RESPONSE,
        ReadStatus.Timeout => FindingCode.HOST_TIMEOUT,
        ReadStatus.Canceled => FindingCode.HOST_CANCELED,
        ReadStatus.Malformed => FindingCode.HOST_MALFORMED,
        ReadStatus.OutputLimit => FindingCode.HOST_OUTPUT_LIMIT,
        ReadStatus.IoError => FindingCode.HOST_IO_ERROR,
        ReadStatus.Failed => FindingCode.HOST_COMMAND_FAILED,
        ReadStatus.NotRun => FindingCode.HOST_NOT_RUN,
        _ => throw new InvalidDataException("Unknown input status.")
    };
    private static Parsed<T> Good<T>(T value) where T : class => new(FindingCode.HOST_OBSERVED, value);
    private static Parsed<T> Bad<T>() where T : class => new(FindingCode.HOST_MALFORMED, null);
    private static Parsed<T> Limit<T>() where T : class => new(FindingCode.HOST_OUTPUT_LIMIT, null);
}

internal sealed record GpuList(ImmutableArray<GpuData> Gpus);
internal sealed record PackageList(ImmutableDictionary<string, string> Versions);
