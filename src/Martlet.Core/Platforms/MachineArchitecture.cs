using System.Runtime.InteropServices;

namespace Martlet.Core.Platforms;

/// <summary>The processor of the computer Martlet runs on and the architecture of Martlet's own process. On Windows on
/// Arm the x64 build runs under Windows' x64 emulation, where .NET can report the emulated architecture for the machine,
/// so on Windows the machine is read with <c>IsWow64Process2</c>, which always names the real processor.</summary>
public sealed record MachineArchitecture(Architecture Machine, Architecture Process)
{
    private const ushort Amd64 = 0x8664, Arm64Machine = 0xAA64, I386 = 0x014C, ArmNt = 0x01C4;

    /// <summary>A Windows PC with an ARM64 processor (Snapdragon X and similar).</summary>
    public bool WindowsOnArm => Machine == Architecture.Arm64;

    /// <summary>Martlet's process runs under emulation (the x64 build on an ARM64 PC).</summary>
    public bool Emulated => Machine != Process;

    /// <summary>x64, arm64, x86 or arm, as host reports and <see cref="PlatformDevice"/> spell it.</summary>
    public string MachineName => Name(Machine);
    public string ProcessName => Name(Process);

    /// <summary>One plain line for Settings, the Devices map and status tools.</summary>
    public string Describe() => (Machine, Emulated) switch
    {
        (Architecture.Arm64, true) => $"ARM64 processor (Windows on Arm); Martlet's {ProcessName} build runs under Windows' x64 emulation.",
        (Architecture.Arm64, false) => "ARM64 processor (Windows on Arm); Martlet runs natively.",
        (_, false) => $"{MachineName} processor; Martlet runs natively.",
        _ => $"{MachineName} processor; Martlet's {ProcessName} build runs under emulation."
    };

    /// <summary>What Windows on Arm means for Martlet, or null on any other PC.</summary>
    public string? WindowsOnArmNote => !WindowsOnArm ? null
        : (Emulated ? "Martlet runs on this Windows on Arm PC under Windows' x64 emulation: everything works, but it uses more " +
              "processor time and battery than on an x64 PC. "
            : "Martlet runs natively on this Windows on Arm PC. ") +
          "NVIDIA graphics cards don't work on Windows on Arm, so GPU jobs such as Audio2Face lip-sync and voice cloning need " +
          "another of your computers or a cloud provider.";

    private static readonly Lazy<MachineArchitecture> current = new(Detect);

    /// <summary>This computer, detected once.</summary>
    public static MachineArchitecture Current => current.Value;

    public static MachineArchitecture Detect() => FromNative(OperatingSystem.IsWindows() ? NativeMachine() : null,
        RuntimeInformation.ProcessArchitecture, RuntimeInformation.OSArchitecture);

    /// <summary>From <c>IsWow64Process2</c>'s native machine (an IMAGE_FILE_MACHINE value; null when it couldn't be read)
    /// and the process architecture. An unreadable or unknown machine falls back to what .NET reports.</summary>
    public static MachineArchitecture FromNative(ushort? nativeMachine, Architecture process, Architecture reportedMachine) =>
        new(nativeMachine switch
        {
            Amd64 => Architecture.X64,
            Arm64Machine => Architecture.Arm64,
            I386 => Architecture.X86,
            ArmNt => Architecture.Arm,
            _ => reportedMachine
        }, process);

    public static string Name(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        Architecture.Arm => "arm",
        _ => architecture.ToString().ToLowerInvariant()
    };

    private static ushort? NativeMachine()
    {
        // -1 is the current-process pseudo handle; it needs no closing.
        try { return IsWow64Process2(new IntPtr(-1), out _, out var native) ? native : null; }
        catch (EntryPointNotFoundException) { return null; }
        catch (DllNotFoundException) { return null; }
    }

    // Windows 10 1709+. For an x64 process on ARM64 the process machine is UNKNOWN (x64 emulation is not WOW64); the
    // native machine is ARM64 either way.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
}
