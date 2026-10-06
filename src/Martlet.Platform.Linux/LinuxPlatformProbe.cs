using Martlet.Companion.Platform;
using Martlet.Core.Platforms;
using Martlet.Platform.Linux.Probe;

namespace Martlet.Platform.Linux;

/// <summary>The portable probe (OS, memory, NVIDIA through nvidia-smi) plus what only Linux knows: AMD and Intel GPUs from
/// /sys/class/drm (with VRAM for amdgpu), and the NVIDIA card a nouveau driver runs (no CUDA, so not counted as NVIDIA).</summary>
internal sealed class LinuxPlatformProbe(LinuxDesktopFacts facts) : IPlatformProbe
{
    private readonly DefaultPlatformProbe portable = new();

    public PlatformInfo Probe()
    {
        var info = portable.Probe(DevicePlatform.Linux);
        return info with { Gpus = Merge(info.Gpus, facts.Gpus) };
    }

    internal static IReadOnlyList<PlatformGpu> Merge(IReadOnlyList<PlatformGpu>? portable, IReadOnlyList<LinuxGpu> linux)
    {
        var gpus = new List<PlatformGpu>(portable ?? []);
        var nvidiaKnown = gpus.Any(g => g.IsNvidia);
        foreach (var gpu in linux)
        {
            if (gpu.Vendor == GpuVendor.Nvidia && (nvidiaKnown || gpu.Driver == "nvidia")) continue;
            var vendor = gpu.Vendor switch
            {
                GpuVendor.Amd => "amd", GpuVendor.Intel => "intel",
                GpuVendor.Nvidia => "nvidia-" + gpu.Driver, _ => "other"
            };
            var name = gpu.Vendor == GpuVendor.Nvidia ? $"NVIDIA GPU ({gpu.Driver} driver, no CUDA)" : gpu.Name;
            gpus.Add(new PlatformGpu(name, vendor, gpu.VramMegabytes is { } mb ? Math.Round(mb / 1024d, 1) : null));
        }
        return gpus;
    }
}
