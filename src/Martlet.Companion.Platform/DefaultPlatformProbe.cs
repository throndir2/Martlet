using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Martlet.Core.Platforms;

namespace Martlet.Companion.Platform;

/// <summary>Portable detection that needs no native code: OS and version, architecture, memory, NVIDIA GPUs (nvidia-smi
/// or /proc/driver/nvidia), and on Linux the session type (Wayland or X11) and desktop. On macOS it treats an Arm64
/// machine as Apple silicon; an x64 build under Rosetta needs the macOS integration's sysctl probe to tell.</summary>
public sealed class DefaultPlatformProbe : IPlatformProbe
{
    private readonly Func<string, string?> environment;
    private readonly Func<string, string?> readFile;
    private readonly Func<string, string, string?> run;

    public DefaultPlatformProbe() : this(Environment.GetEnvironmentVariable, ReadFileOrNull, RunOrNull) { }

    /// <summary>For tests: inject the environment, file reads and process runs.</summary>
    public DefaultPlatformProbe(Func<string, string?> environment, Func<string, string?> readFile, Func<string, string, string?> run)
    {
        this.environment = environment;
        this.readFile = readFile;
        this.run = run;
    }

    public PlatformInfo Probe() => Probe(Current());

    public PlatformInfo Probe(DevicePlatform platform)
    {
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var info = new PlatformInfo
        {
            Platform = platform,
            OsVersion = Environment.OSVersion.Version,
            OsDescription = RuntimeInformation.OSDescription,
            MemoryGb = memory > 0 ? Math.Round(memory / 1024d / 1024 / 1024, 1) : null
        };
        return platform switch
        {
            DevicePlatform.Linux => info with
            {
                OsDescription = OsRelease() ?? info.OsDescription,
                Gpus = NvidiaGpus(),
                DisplayServer = LinuxSession(),
                XWayland = LinuxSession() == DisplayServer.Wayland ? !string.IsNullOrEmpty(environment("DISPLAY")) : null,
                Desktop = environment("XDG_CURRENT_DESKTOP")
            },
            DevicePlatform.MacOs => info with
            {
                OsDescription = $"macOS {info.OsVersion}",
                AppleSilicon = info.Architecture == Architecture.Arm64,
                Gpus = info.Architecture == Architecture.Arm64 ? [new PlatformGpu("Apple GPU", "apple", null)] : [],
                DisplayServer = DisplayServer.Quartz
            },
            DevicePlatform.Windows => info with { Gpus = NvidiaGpus(), DisplayServer = DisplayServer.Windows },
            _ => info
        };
    }

    public static DevicePlatform Current() =>
        OperatingSystem.IsMacOS() ? DevicePlatform.MacOs : OperatingSystem.IsLinux() ? DevicePlatform.Linux : DevicePlatform.Windows;

    internal DisplayServer LinuxSession()
    {
        var type = environment("XDG_SESSION_TYPE");
        if (string.Equals(type, "wayland", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(environment("WAYLAND_DISPLAY")))
            return DisplayServer.Wayland;
        if (string.Equals(type, "x11", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(environment("DISPLAY")))
            return DisplayServer.X11;
        return DisplayServer.Headless;
    }

    private string? OsRelease()
    {
        var text = readFile("/etc/os-release");
        var line = text?.Split('\n').FirstOrDefault(l => l.StartsWith("PRETTY_NAME=", StringComparison.Ordinal));
        return line?["PRETTY_NAME=".Length..].Trim().Trim('"');
    }

    /// <summary>NVIDIA GPUs with memory from nvidia-smi; without it, names from the driver's /proc entries.</summary>
    internal IReadOnlyList<PlatformGpu> NvidiaGpus()
    {
        var smi = run("nvidia-smi", "--query-gpu=name,memory.total --format=csv,noheader,nounits");
        if (!string.IsNullOrWhiteSpace(smi))
            return [.. smi.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split(',', StringSplitOptions.TrimEntries))
                .Where(parts => parts.Length >= 1 && parts[0].Length > 0)
                .Select(parts => new PlatformGpu(parts[0], "nvidia",
                    parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var mib)
                        ? Math.Round(mib / 1024, 1) : null))];
        var gpus = new List<PlatformGpu>();
        var list = readFile("/proc/driver/nvidia/gpus/.list");
        foreach (var dir in list?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [])
        {
            var model = readFile($"/proc/driver/nvidia/gpus/{dir.Trim()}/information")?.Split('\n')
                .FirstOrDefault(l => l.StartsWith("Model:", StringComparison.Ordinal));
            if (model is not null) gpus.Add(new PlatformGpu(model["Model:".Length..].Trim(), "nvidia", null));
        }
        return gpus;
    }

    private static string? ReadFileOrNull(string path)
    {
        if (path.EndsWith("/.list", StringComparison.Ordinal))
        {
            var dir = Path.GetDirectoryName(path)!;
            return Directory.Exists(dir) ? string.Join('\n', Directory.GetDirectories(dir).Select(Path.GetFileName)) : null;
        }
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string? RunOrNull(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(3000)) { process.Kill(true); return null; }
            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}
