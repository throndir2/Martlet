using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Martlet.Desktop;

internal sealed record GpuInfo(string Name, double? MemoryGb)
{
    internal bool IsNvidia => Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
    internal string Describe() => MemoryGb is { } gb ? $"{Name} ({gb:0.#} GB)" : Name;
}

/// <summary>What this PC has, read locally from Windows (registry and system calls). No network, WMI or process launch.</summary>
internal sealed record MachineInfo(string Name, string Windows, string? Processor, int Threads, double? MemoryGb,
    IReadOnlyList<GpuInfo> Gpus, string? LanAddress, bool DockerInstalled, bool DockerRunning)
{
    internal static MachineInfo Unknown { get; } = new(Environment.MachineName, "Windows", null, Environment.ProcessorCount, null, [], null, false, false);

    internal GpuInfo? BestGpu => Gpus.OrderByDescending(g => g.IsNvidia).ThenByDescending(g => g.MemoryGb ?? 0).FirstOrDefault();

    /// <summary>Plain-language hints about what this PC's graphics card can take on.</summary>
    internal IEnumerable<string> Capabilities()
    {
        var gpu = BestGpu;
        if (gpu is { IsNvidia: true, MemoryGb: >= 4 })
            yield return "Can run Audio2Face lip-sync as a Martlet host";
        if (gpu is { IsNvidia: true, MemoryGb: >= 8 })
            yield return gpu.MemoryGb >= 16 ? "Can run a mid-size local conversation model" : "Can run a small local conversation model";
        if (gpu is null || !gpu.IsNvidia)
            yield return "Best paired with a cloud conversation model; GPU roles need NVIDIA";
    }

    internal static MachineInfo Read()
    {
        var gpus = ReadGpus();
        return new(Environment.MachineName, ReadWindows(), ReadProcessor(), Environment.ProcessorCount, ReadMemory(), gpus,
            SafeAddress(), DockerDesktopInstalled(), DockerDesktopRunning());
    }

    private static string? SafeAddress()
    {
        try { return HostSetupCommands.ThisPcAddress(); }
        catch (System.Net.NetworkInformation.NetworkInformationException) { return null; }
    }

    private static string ReadWindows()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var build = int.TryParse(key?.GetValue("CurrentBuildNumber") as string, out var number) ? number : Environment.OSVersion.Version.Build;
            var name = (key?.GetValue("ProductName") as string ?? "Windows").Replace("Windows 10", build >= 22000 ? "Windows 11" : "Windows 10");
            var display = key?.GetValue("DisplayVersion") as string;
            return display is null ? $"{name} (build {build})" : $"{name} {display} (build {build})";
        }
        catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return RuntimeInformation.OSDescription;
        }
    }

    private static string? ReadProcessor()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim();
        }
        catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        internal uint Length, Load;
        internal ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    private static double? ReadMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? Math.Round(status.TotalPhysical / (1024d * 1024 * 1024)) : null;
    }

    private static readonly string[] VirtualAdapters =
        ["Microsoft Basic", "Remote Display", "Hyper-V", "Virtual", "Parsec", "Meta Virtual", "IddSample", "Citrix", "VMware", "Mirage"];

    private static IReadOnlyList<GpuInfo> ReadGpus()
    {
        var found = new List<GpuInfo>();
        try
        {
            using var display = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (display is null) return found;
            foreach (var child in display.GetSubKeyNames().Where(name => name.Length == 4 && name.All(char.IsAsciiDigit)))
            {
                using var adapter = display.OpenSubKey(child);
                if (adapter?.GetValue("DriverDesc") is not string name || VirtualAdapters.Any(v => name.Contains(v, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var bytes = Bytes(adapter.GetValue("HardwareInformation.qwMemorySize")) ?? Bytes(adapter.GetValue("HardwareInformation.MemorySize"));
                var gb = bytes is > 0 ? Math.Round(bytes.Value / (1024d * 1024 * 1024), 1) : (double?)null;
                if (found.All(g => g.Name != name)) found.Add(new(name.Trim(), gb));
            }
        }
        catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        return found;
    }

    private static long? Bytes(object? value) => value switch
    {
        long number => number,
        int number => (uint)number,
        byte[] { Length: >= 8 } raw => BitConverter.ToInt64(raw, 0),
        byte[] { Length: >= 4 } raw => BitConverter.ToUInt32(raw, 0),
        _ => null
    };

    internal static string DockerDesktopPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe");

    internal static bool DockerDesktopInstalled() => File.Exists(DockerDesktopPath);

    internal static bool DockerDesktopRunning()
    {
        foreach (var name in new[] { "com.docker.backend", "Docker Desktop" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length > 0) return true; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return false;
    }
}
