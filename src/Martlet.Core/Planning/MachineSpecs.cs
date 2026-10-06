using Martlet.Core.Installation;

namespace Martlet.Core.Planning;

public enum GpuVendor { Nvidia, Amd, Intel, Apple, Other }

/// <summary>One graphics card. <see cref="UsedGb"/> is memory already taken by things the plan does not place (games,
/// other programs), when known. <see cref="UnifiedMemory"/>: the card shares main memory (Apple Silicon, some AMD/Intel
/// APUs), so its <see cref="VramGb"/> is the share of RAM it may use and anything placed on it also takes RAM.</summary>
public sealed record MachineGpu(string Name, GpuVendor Vendor, double VramGb)
{
    public double UsedGb { get; init; }
    public bool UnifiedMemory { get; init; }
    public bool IsNvidia => Vendor == GpuVendor.Nvidia;

    public static GpuVendor VendorOf(string? vendorOrName)
    {
        var text = vendorOrName ?? "";
        if (text.Contains("nvidia", StringComparison.OrdinalIgnoreCase) || text.Contains("geforce", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("rtx", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Nvidia;
        if (text.Contains("amd", StringComparison.OrdinalIgnoreCase) || text.Contains("radeon", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Amd;
        if (text.Contains("intel", StringComparison.OrdinalIgnoreCase) || text.Contains("arc", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Intel;
        if (text.Contains("apple", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Apple;
        return GpuVendor.Other;
    }
}

/// <summary>What the planner knows about one computer in the user's Martlet network. <see cref="IsPrimary"/> marks the PC
/// the user talks to (microphone, speakers, character); in-app options only run there.</summary>
public sealed record MachineSpecs(string Id, string Name)
{
    public IReadOnlyList<MachineGpu> Gpus { get; init; } = [];
    public double RamGb { get; init; }
    public int CpuThreads { get; init; }
    /// <summary>windows, linux or macos.</summary>
    public string Platform { get; init; } = "windows";
    /// <summary>x64 or arm64.</summary>
    public string Architecture { get; init; } = "x64";
    public double? DiskFreeGb { get; init; }
    public bool IsPrimary { get; init; }
    /// <summary>The user games on this computer: its graphics card is left to the games.</summary>
    public bool KeepGpuForGames { get; init; }
    /// <summary>A laptop running on battery, or a computer that sleeps: the planner prefers always-on machines for host work.</summary>
    public bool OnBattery { get; init; }

    public double BestGpuGb => Gpus.Count == 0 ? 0 : Gpus.Max(g => g.VramGb);
    public bool HasNvidia => Gpus.Any(g => g.IsNvidia);

    /// <summary>The PC the user talks to, from plain numbers (the wizard's and Devices view's own detection).</summary>
    public static MachineSpecs ThisPc(IReadOnlyList<MachineGpu> gpus, double ramGb, int cpuThreads, string platform = "windows",
        string architecture = "x64", double? diskFreeGb = null) =>
        new("this-pc", "This PC")
        {
            Gpus = gpus, RamGb = ramGb, CpuThreads = cpuThreads, Platform = platform, Architecture = architecture,
            DiskFreeGb = diskFreeGb, IsPrimary = true
        };

    /// <summary>A paired host from its hardware report. Integrated AMD/Intel adapters (under 2.5 GB) count as no card; an
    /// Apple Silicon (macos arm64) host's GPU shares main memory.</summary>
    public static MachineSpecs FromHostHardware(HostHardware report, string? name = null, bool isPrimary = false)
    {
        ArgumentNullException.ThrowIfNull(report);
        var platform = (report.Platform ?? (report.OperatingSystem.Contains("windows", StringComparison.OrdinalIgnoreCase) ? "windows" : "linux"))
            .ToLowerInvariant();
        var arch = (report.Architecture ?? "x64").ToLowerInvariant();
        var gpus = report.Gpus
            .Select(g => new MachineGpu(g.Name, MachineGpu.VendorOf(g.Vendor + " " + g.Name), g.MemoryGb ?? 0))
            .Where(g => g.IsNvidia ? true : g.VramGb >= 2.5)
            .ToList();
        var ram = report.MemoryGb ?? 0;
        if (platform == "macos" && arch == "arm64")
            gpus = [new MachineGpu("Apple GPU", GpuVendor.Apple, Math.Round(ram * 0.7, 1)) { UnifiedMemory = true }];
        return new(report.HostId, name ?? report.HostId)
        {
            Gpus = gpus, RamGb = ram, CpuThreads = report.ProcessorThreads ?? 0, Platform = platform, Architecture = arch,
            IsPrimary = isPrimary
        };
    }
}
