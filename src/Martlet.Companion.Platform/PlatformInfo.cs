using System.Runtime.InteropServices;
using Martlet.Core.Platforms;

namespace Martlet.Companion.Platform;

/// <summary>The windowing system of the desktop session. <see cref="Wayland"/> means a Wayland session (the app itself
/// may still draw through XWayland, see <see cref="PlatformInfo.XWayland"/>).</summary>
public enum DisplayServer { Unknown, Headless, X11, Wayland, Quartz, Windows }

/// <summary>What the companion knows about the computer it runs on. Null facts were not detected and make catalog
/// checks answer "unknown", never "yes".</summary>
public sealed record PlatformInfo
{
    public required DevicePlatform Platform { get; init; }
    /// <summary>macOS: the product version (15.1). Linux: the kernel version. Windows: the build.</summary>
    public Version? OsVersion { get; init; }
    /// <summary>A readable system name, for example "Ubuntu 24.04.1 LTS" or "macOS 15.1".</summary>
    public string OsDescription { get; init; } = "";
    /// <summary>The machine's architecture (not the process's: an x64 build under Rosetta still reports Arm64 when the
    /// probe can tell).</summary>
    public Architecture Architecture { get; init; } = RuntimeInformation.OSArchitecture;
    public Architecture ProcessArchitecture { get; init; } = RuntimeInformation.ProcessArchitecture;
    /// <summary>Macs only: true on Apple silicon (M1 or later), false on Intel, null when unknown or not a Mac.</summary>
    public bool? AppleSilicon { get; init; }
    /// <summary>The GPUs found; null when the probe could not look.</summary>
    public IReadOnlyList<PlatformGpu>? Gpus { get; init; }
    public double? MemoryGb { get; init; }
    public DisplayServer DisplayServer { get; init; }
    /// <summary>Linux Wayland sessions: whether XWayland (DISPLAY) is available for X11 windows.</summary>
    public bool? XWayland { get; init; }
    /// <summary>Linux: XDG_CURRENT_DESKTOP, for example "ubuntu:GNOME" or "KDE".</summary>
    public string? Desktop { get; init; }

    public bool HasNvidia => Gpus?.Any(g => g.IsNvidia) == true;

    /// <summary>Intel Macs run local models on the CPU only.</summary>
    public bool CpuOnlyLocalModels => Platform == DevicePlatform.MacOs && AppleSilicon == false;

    /// <summary>This computer as the platform catalog sees it, for <see cref="PlatformCatalog.Check"/>.</summary>
    public PlatformDevice ToDevice(string name = "This computer") => new()
    {
        Platform = Platform,
        Name = name,
        OsVersion = OsVersion,
        Arm64 = Platform == DevicePlatform.MacOs && AppleSilicon is { } silicon ? silicon : Architecture == Architecture.Arm64,
        MemoryGb = MemoryGb,
        Gpus = Gpus,
        Features = new HashSet<string>(StringComparer.Ordinal)
    };
}

/// <summary>Detects the platform. The default (<see cref="DefaultPlatformProbe"/>) is portable; the per-OS
/// integrations may replace it with a more precise one (for example sysctl on macOS).</summary>
public interface IPlatformProbe
{
    PlatformInfo Probe();
}
