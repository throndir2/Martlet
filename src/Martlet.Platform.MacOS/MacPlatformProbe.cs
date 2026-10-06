using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Martlet.Companion.Platform;
using Martlet.Core.Platforms;

namespace Martlet.Platform.MacOS;

/// <summary>Raw facts from sysctl. Null means the key was missing or unreadable.</summary>
public sealed record MacSysctlFacts
{
    /// <summary>kern.osproductversion, for example "15.1".</summary>
    public string? ProductVersion { get; init; }
    /// <summary>hw.optional.arm64: 1 on Apple silicon, also for an x86_64 process under Rosetta.</summary>
    public bool? Arm64Hardware { get; init; }
    /// <summary>sysctl.proc_translated: 1 when this process is x86_64 code translated by Rosetta.</summary>
    public bool? Translated { get; init; }
    /// <summary>hw.memsize in bytes (unified memory on Apple silicon).</summary>
    public ulong? MemoryBytes { get; init; }
    /// <summary>machdep.cpu.brand_string, for example "Apple M2 Pro" or "Intel(R) Core(TM) i7-9750H CPU @ 2.60GHz".</summary>
    public string? CpuBrand { get; init; }
    /// <summary>hw.model, for example "Mac14,10" or "MacBookPro16,1".</summary>
    public string? Model { get; init; }
}

/// <summary>The companion's view of a Mac: Apple silicon vs Intel (from the hardware, so an x64 build under Rosetta still
/// counts as Apple silicon), unified memory and the macOS product version. The platform catalog then keeps MLX and Apple
/// Intelligence away from Intel Macs, marks local models CPU-only there, and never offers local Audio2Face or the F5
/// worker on any Mac.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacPlatformProbe(Func<MacSysctlFacts>? read = null) : IPlatformProbe
{
    public static readonly Version MinimumMacOs = new(14, 0);

    public PlatformInfo Probe() => FromFacts((read ?? ReadSysctl)(), new DefaultPlatformProbe().Probe(DevicePlatform.MacOs));

    internal static PlatformInfo FromFacts(MacSysctlFacts facts, PlatformInfo portable)
    {
        var version = PlatformCatalog.ParseVersion(facts.ProductVersion) ?? portable.OsVersion;
        var silicon = facts.Arm64Hardware ?? (facts.Translated == true ? true : portable.AppleSilicon);
        var memory = facts.MemoryBytes is { } bytes and > 0 ? Math.Round(bytes / 1024d / 1024 / 1024, 1) : portable.MemoryGb;
        var chip = facts.CpuBrand?.Trim();
        var gpu = silicon == true ? new PlatformGpu($"{(chip is { Length: > 0 } && chip.StartsWith("Apple", StringComparison.Ordinal) ? chip : "Apple silicon")} GPU (unified memory)", "apple", null) : null;
        var description = new StringBuilder($"macOS {version?.ToString() ?? "(unknown version)"}");
        var details = new List<string>();
        if (chip is { Length: > 0 }) details.Add(chip);
        else if (silicon is { } s) details.Add(s ? "Apple silicon" : "Intel");
        if (facts.Model is { Length: > 0 } model) details.Add(model);
        if (facts.Translated == true) details.Add("Intel build running under Rosetta");
        if (details.Count > 0) description.Append(" (").Append(string.Join(", ", details)).Append(')');
        return portable with
        {
            Platform = DevicePlatform.MacOs,
            OsVersion = version,
            OsDescription = description.ToString(),
            Architecture = silicon switch { true => Architecture.Arm64, false => Architecture.X64, _ => portable.Architecture },
            ProcessArchitecture = facts.Translated == true ? Architecture.X64 : portable.ProcessArchitecture,
            AppleSilicon = silicon,
            MemoryGb = memory,
            // Intel Macs' GPUs (Intel, AMD, old NVIDIA) have no CUDA or Metal path Martlet's local engines use.
            Gpus = silicon switch { true => [gpu!], false => [], _ => portable.Gpus },
            DisplayServer = DisplayServer.Quartz
        };
    }

    /// <summary>Plain-language warnings for the settings page about what this Mac can't do well or at all.</summary>
    public static IReadOnlyList<string> Warnings(PlatformInfo info)
    {
        var warnings = new List<string>();
        if (info.OsVersion is { } version && version < MinimumMacOs)
            warnings.Add($"Martlet needs macOS {MinimumMacOs.Major} Sonoma or later; this Mac runs {version}.");
        if (info.AppleSilicon == true && info.ProcessArchitecture == Architecture.X64)
            warnings.Add("This is the Intel build of Martlet running under Rosetta on an Apple silicon Mac. Download the Apple silicon " +
                "build: it starts faster and uses less power.");
        if (info.AppleSilicon == false)
            warnings.Add("This Mac has an Intel processor: models on this Mac (Ollama, LM Studio) run on the CPU only, so pick small " +
                "1-4B models or use a cloud service or a paired host for thinking. MLX models and Apple Intelligence need Apple silicon.");
        warnings.Add("Audio2Face lip-sync and F5 voice cloning need an NVIDIA GPU, which no Mac has; use them through a paired " +
            "NVIDIA host.");
        if (info.AppleSilicon == true && info.MemoryGb is < 12)
            warnings.Add($"This Mac has {info.MemoryGb:0.#} GB of unified memory; models on this Mac should stay at 2-4B.");
        return warnings;
    }

    [SupportedOSPlatform("macos")]
    private static MacSysctlFacts ReadSysctl() => new()
    {
        ProductVersion = Sysctl.String("kern.osproductversion"),
        Arm64Hardware = Sysctl.Int("hw.optional.arm64") is { } arm ? arm == 1 : false,
        Translated = Sysctl.Int("sysctl.proc_translated") is { } translated ? translated == 1 : false,
        MemoryBytes = Sysctl.UInt64("hw.memsize"),
        CpuBrand = Sysctl.String("machdep.cpu.brand_string"),
        Model = Sysctl.String("hw.model")
    };
}

[SupportedOSPlatform("macos")]
internal static unsafe class Sysctl
{
    [DllImport("/usr/lib/libSystem.B.dylib", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int sysctlbyname(string name, void* value, nuint* length, void* newValue, nuint newLength);

    public static long? Int(string name)
    {
        long value = 0;
        nuint length = sizeof(long);
        if (sysctlbyname(name, &value, &length, null, 0) != 0) return null;
        return length == sizeof(int) ? *(int*)&value : value;
    }

    public static ulong? UInt64(string name)
    {
        ulong value = 0;
        nuint length = sizeof(ulong);
        return sysctlbyname(name, &value, &length, null, 0) == 0 ? value : null;
    }

    public static string? String(string name)
    {
        nuint length = 0;
        if (sysctlbyname(name, null, &length, null, 0) != 0 || length == 0 || length > 4096) return null;
        var buffer = new byte[length];
        fixed (byte* p = buffer)
            if (sysctlbyname(name, p, &length, null, 0) != 0) return null;
        return Encoding.UTF8.GetString(buffer, 0, (int)length).TrimEnd('\0').Trim();
    }
}
