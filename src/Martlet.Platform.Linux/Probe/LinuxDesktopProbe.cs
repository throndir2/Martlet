using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Martlet.Platform.Linux.Probe;

public enum LinuxSessionType { Unknown, X11, Wayland, Tty }

public enum LinuxDesktop { Unknown, Gnome, Kde, Xfce, Cinnamon, Mate, Lxqt, Budgie, Pantheon, Unity, Cosmic, Sway, Hyprland, Wlroots, Other }

public enum GpuVendor { Nvidia, Amd, Intel, Other }

public sealed record LinuxGpu(GpuVendor Vendor, string Name, string Driver, long? VramMegabytes);

/// <summary>What kind of Linux desktop Martlet runs on: the display server, the desktop environment, the distribution and the
/// graphics cards (NVIDIA through nvidia-smi or /proc/driver/nvidia, AMD and Intel through /sys/class/drm).</summary>
public sealed record LinuxDesktopFacts(
    LinuxSessionType Session,
    LinuxDesktop Desktop,
    string DesktopName,
    bool HasX11Display,
    bool HasWaylandDisplay,
    string? Distribution,
    IReadOnlyList<LinuxGpu> Gpus,
    string? NvidiaDriver,
    bool Rocm)
{
    public bool HasNvidia => Gpus.Any(g => g.Vendor == GpuVendor.Nvidia && g.Driver == "nvidia") || NvidiaDriver is not null;
    public bool HasAmd => Gpus.Any(g => g.Vendor == GpuVendor.Amd);

    /// <summary>Compositors that offer wlr-layer-shell to native Wayland clients (KDE Plasma, wlroots, Hyprland, COSMIC).
    /// GNOME's Mutter does not.</summary>
    public bool LayerShellCompositor => Session == LinuxSessionType.Wayland &&
        Desktop is LinuxDesktop.Kde or LinuxDesktop.Sway or LinuxDesktop.Hyprland or LinuxDesktop.Wlroots or LinuxDesktop.Cosmic;

    public string Summary()
    {
        var gpus = Gpus.Count == 0 ? "no GPU found" : string.Join(", ", Gpus.Select(g =>
            g.VramMegabytes is { } mb ? $"{g.Name} ({g.Driver}, {mb} MB)" : $"{g.Name} ({g.Driver})"));
        return $"{Distribution ?? "Linux"}, {DesktopName} on {Session}; {gpus}";
    }
}

public static class LinuxDesktopProbe
{
    /// <summary>Reads this computer. Never throws; missing files and tools leave their facts empty.
    /// <paramref name="nvidiaSmi"/> false skips running nvidia-smi (NVIDIA cards then come from /sys only).</summary>
    public static LinuxDesktopFacts Read(bool nvidiaSmi = true) => Read(
        Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => e.Value as string ?? "", StringComparer.Ordinal),
        ReadFile, Cards, nvidiaSmi ? RunTool : static (_, _) => null, Directory.Exists);

    internal static LinuxDesktopFacts Read(IReadOnlyDictionary<string, string> environment, Func<string, string?> readFile,
        Func<IEnumerable<DrmCard>> cards, Func<string, string, string?> runTool, Func<string, bool> directoryExists)
    {
        var (desktop, desktopName) = ClassifyDesktop(environment);
        var nvidiaDriver = ParseNvidiaProcVersion(readFile("/proc/driver/nvidia/version"));
        var smi = ParseNvidiaSmi(runTool("nvidia-smi", "--query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits"));
        var gpus = new List<LinuxGpu>(smi.Select(s => new LinuxGpu(GpuVendor.Nvidia, s.Name, "nvidia", s.MemoryMegabytes)));
        nvidiaDriver ??= smi.FirstOrDefault().Driver;
        foreach (var card in cards())
        {
            var vendor = VendorFromPciId(card.Vendor);
            if (vendor == GpuVendor.Nvidia && smi.Count > 0) continue;
            var name = vendor switch
            {
                GpuVendor.Nvidia => "NVIDIA GPU", GpuVendor.Amd => "AMD GPU", GpuVendor.Intel => "Intel GPU", _ => "GPU"
            };
            gpus.Add(new LinuxGpu(vendor, name, card.Driver ?? "unknown",
                card.VramBytes is > 0 ? card.VramBytes / (1024 * 1024) : null));
        }
        return new LinuxDesktopFacts(ClassifySession(environment), desktop, desktopName,
            Has(environment, "DISPLAY"), Has(environment, "WAYLAND_DISPLAY"),
            ParseOsRelease(readFile("/etc/os-release")), gpus, nvidiaDriver, directoryExists("/dev/kfd") || File.Exists("/dev/kfd"));
    }

    private static bool Has(IReadOnlyDictionary<string, string> environment, string name) =>
        environment.TryGetValue(name, out var value) && value.Length > 0;

    internal static LinuxSessionType ClassifySession(IReadOnlyDictionary<string, string> environment)
    {
        var type = environment.GetValueOrDefault("XDG_SESSION_TYPE")?.Trim().ToLowerInvariant();
        return type switch
        {
            "wayland" => LinuxSessionType.Wayland,
            "x11" => LinuxSessionType.X11,
            "tty" when !Has(environment, "WAYLAND_DISPLAY") && !Has(environment, "DISPLAY") => LinuxSessionType.Tty,
            _ => Has(environment, "WAYLAND_DISPLAY") ? LinuxSessionType.Wayland
                : Has(environment, "DISPLAY") ? LinuxSessionType.X11
                : type == "tty" ? LinuxSessionType.Tty : LinuxSessionType.Unknown
        };
    }

    internal static (LinuxDesktop Desktop, string Name) ClassifyDesktop(IReadOnlyDictionary<string, string> environment)
    {
        var raw = environment.GetValueOrDefault("XDG_CURRENT_DESKTOP");
        if (string.IsNullOrWhiteSpace(raw)) raw = environment.GetValueOrDefault("XDG_SESSION_DESKTOP");
        if (string.IsNullOrWhiteSpace(raw)) raw = environment.GetValueOrDefault("DESKTOP_SESSION");
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (Has(environment, "KDE_FULL_SESSION")) return (LinuxDesktop.Kde, "KDE");
            if (Has(environment, "GNOME_DESKTOP_SESSION_ID")) return (LinuxDesktop.Gnome, "GNOME");
            if (Has(environment, "SWAYSOCK")) return (LinuxDesktop.Sway, "sway");
            if (Has(environment, "HYPRLAND_INSTANCE_SIGNATURE")) return (LinuxDesktop.Hyprland, "Hyprland");
            return (LinuxDesktop.Unknown, "unknown desktop");
        }
        var parts = raw.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // XDG_CURRENT_DESKTOP lists the most specific name first ("ubuntu:GNOME", "Budgie:GNOME"); the first known one wins.
        foreach (var part in parts)
        {
            var desktop = part.ToLowerInvariant() switch
            {
                "gnome" or "gnome-classic" or "gnome-flashback" or "ubuntu" or "zorin" or "pop" => LinuxDesktop.Gnome,
                "kde" or "plasma" or "plasmawayland" or "plasmax11" => LinuxDesktop.Kde,
                "xfce" => LinuxDesktop.Xfce,
                "x-cinnamon" or "cinnamon" => LinuxDesktop.Cinnamon,
                "mate" => LinuxDesktop.Mate,
                "lxqt" => LinuxDesktop.Lxqt,
                "budgie" or "budgie-desktop" => LinuxDesktop.Budgie,
                "pantheon" => LinuxDesktop.Pantheon,
                "unity" => LinuxDesktop.Unity,
                "cosmic" => LinuxDesktop.Cosmic,
                "sway" => LinuxDesktop.Sway,
                "hyprland" => LinuxDesktop.Hyprland,
                "wlroots" or "river" or "wayfire" or "labwc" or "niri" or "hikari" or "dwl" => LinuxDesktop.Wlroots,
                _ => LinuxDesktop.Unknown
            };
            // "ubuntu" alone is GNOME; but "Budgie:GNOME" must stay Budgie, so a later "GNOME" never overrides an earlier match.
            if (desktop != LinuxDesktop.Unknown) return (desktop, Pretty(desktop, part));
        }
        return (LinuxDesktop.Other, parts.Length > 0 ? parts[0] : raw.Trim());
    }

    private static string Pretty(LinuxDesktop desktop, string raw) => desktop switch
    {
        LinuxDesktop.Gnome => "GNOME", LinuxDesktop.Kde => "KDE Plasma", LinuxDesktop.Xfce => "Xfce", LinuxDesktop.Cinnamon => "Cinnamon",
        LinuxDesktop.Mate => "MATE", LinuxDesktop.Lxqt => "LXQt", LinuxDesktop.Budgie => "Budgie", LinuxDesktop.Pantheon => "Pantheon",
        LinuxDesktop.Unity => "Unity", LinuxDesktop.Cosmic => "COSMIC", LinuxDesktop.Sway => "sway", LinuxDesktop.Hyprland => "Hyprland",
        _ => raw
    };

    internal static string? ParseOsRelease(string? text)
    {
        if (text is null) return null;
        string? name = null;
        foreach (var line in text.Split('\n'))
        {
            var index = line.IndexOf('=');
            if (index <= 0) continue;
            var key = line[..index].Trim();
            var value = line[(index + 1)..].Trim().Trim('"', '\'');
            if (key == "PRETTY_NAME" && value.Length > 0) return value;
            if (key == "NAME" && value.Length > 0) name = value;
        }
        return name;
    }

    internal static string? ParseNvidiaProcVersion(string? text)
    {
        if (text is null) return null;
        var match = Regex.Match(text, @"Kernel Module(?: for \S+)?\s+(\d+(?:\.\d+)+)", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    internal readonly record struct NvidiaSmiGpu(string Name, long? MemoryMegabytes, string? Driver);

    internal static IReadOnlyList<NvidiaSmiGpu> ParseNvidiaSmi(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return [];
        var gpus = new List<NvidiaSmiGpu>();
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length < 1 || fields[0].Length == 0 || fields[0].StartsWith("NVIDIA-SMI has failed", StringComparison.Ordinal) ||
                fields[0].StartsWith("No devices", StringComparison.Ordinal)) continue;
            long? memory = fields.Length > 1 && long.TryParse(fields[1].Replace("MiB", "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb)
                ? mb : null;
            gpus.Add(new NvidiaSmiGpu(fields[0], memory, fields.Length > 2 && fields[2].Length > 0 ? fields[2] : null));
        }
        return gpus;
    }

    internal static GpuVendor VendorFromPciId(string? vendor) => vendor?.Trim().ToLowerInvariant() switch
    {
        "0x10de" => GpuVendor.Nvidia, "0x1002" or "0x1022" => GpuVendor.Amd, "0x8086" => GpuVendor.Intel, _ => GpuVendor.Other
    };

    internal sealed record DrmCard(string Vendor, string? Driver, long? VramBytes);

    private static IEnumerable<DrmCard> Cards()
    {
        const string root = "/sys/class/drm";
        if (!Directory.Exists(root)) yield break;
        foreach (var card in Directory.EnumerateDirectories(root, "card*").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(card);
            if (name.Contains('-')) continue;
            var vendor = ReadFile(Path.Combine(card, "device", "vendor"));
            if (vendor is null) continue;
            string? driver = null;
            try { driver = new DirectoryInfo(Path.Combine(card, "device", "driver")).ResolveLinkTarget(true)?.Name; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            long? vram = long.TryParse(ReadFile(Path.Combine(card, "device", "mem_info_vram_total"))?.Trim(), out var bytes) ? bytes : null;
            yield return new DrmCard(vendor.Trim(), driver, vram);
        }
    }

    private static string? ReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static string? RunTool(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(3000)) { try { process.Kill(true); } catch (InvalidOperationException) { } return null; }
            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}
