using System.Runtime.InteropServices;
using Martlet.Core.Contracts;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;
using Martlet.Diagnostics;

namespace Martlet.Core.Tests;

public sealed class MachineArchitectureTests
{
    [Theory]
    [InlineData((ushort)0x8664, Architecture.X64, Architecture.X64, false)]
    // The x64 build on Windows on Arm: .NET may report X64 for the machine, IsWow64Process2 names ARM64.
    [InlineData((ushort)0xAA64, Architecture.X64, Architecture.Arm64, true)]
    [InlineData((ushort)0xAA64, Architecture.Arm64, Architecture.Arm64, false)]
    public void The_native_machine_wins_over_what_dotnet_reports(ushort native, Architecture process, Architecture machine, bool emulated)
    {
        var detected = MachineArchitecture.FromNative(native, process, Architecture.X64);
        Assert.Equal(machine, detected.Machine);
        Assert.Equal(process, detected.Process);
        Assert.Equal(emulated, detected.Emulated);
        Assert.Equal(machine == Architecture.Arm64, detected.WindowsOnArm);
    }

    [Fact]
    public void An_unreadable_native_machine_falls_back_to_the_reported_one()
    {
        Assert.Equal(Architecture.Arm64, MachineArchitecture.FromNative(null, Architecture.Arm64, Architecture.Arm64).Machine);
        Assert.Equal(Architecture.X64, MachineArchitecture.FromNative(0, Architecture.X64, Architecture.X64).Machine);
    }

    [Fact]
    public void Windows_on_arm_is_described_and_noted_and_x64_has_no_note()
    {
        var emulated = MachineArchitecture.FromNative(0xAA64, Architecture.X64, Architecture.X64);
        Assert.Equal("arm64", emulated.MachineName);
        Assert.Contains("Windows on Arm", emulated.Describe(), StringComparison.Ordinal);
        Assert.Contains("x64 emulation", emulated.Describe(), StringComparison.Ordinal);
        Assert.Contains("x64 emulation", emulated.WindowsOnArmNote, StringComparison.Ordinal);
        Assert.Contains("NVIDIA", emulated.WindowsOnArmNote, StringComparison.Ordinal);

        var native = MachineArchitecture.FromNative(0xAA64, Architecture.Arm64, Architecture.Arm64);
        Assert.Contains("runs natively", native.Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain("emulation", native.WindowsOnArmNote, StringComparison.Ordinal);

        var x64 = MachineArchitecture.FromNative(0x8664, Architecture.X64, Architecture.X64);
        Assert.Equal("x64 processor; Martlet runs natively.", x64.Describe());
        Assert.Null(x64.WindowsOnArmNote);
    }

    [Fact]
    public void This_pc_on_windows_on_arm_is_arm64_and_cannot_take_nvidia_engines()
    {
        var device = PlatformDevice.ThisPc(MachineArchitecture.FromNative(0xAA64, Architecture.X64, Architecture.X64),
            new(10, 0, 26100), 16, []);
        Assert.True(device.Arm64);
        Assert.True(device.WindowsOnArm);
        Assert.Contains(PlatformFeatures.X64Emulation, device.Features!);
        var audio2Face = PlatformCatalog.Check("audio2face", PlatformSide.Host, device);
        Assert.Equal(PlatformVerdict.No, audio2Face.Verdict);
        Assert.Contains("Windows on Arm", audio2Face.Reason, StringComparison.Ordinal);
        // Even before its graphics cards are read, an ARM64 Windows PC is a definite no for NVIDIA engines.
        Assert.Equal(PlatformVerdict.No, PlatformCatalog.Check("f5", PlatformSide.Host, device with { Gpus = null }).Verdict);
        Assert.True(PlatformCatalog.Check("openai-llm", PlatformSide.Companion, device).Allowed);
        Assert.True(PlatformCatalog.Check("character-overlay", PlatformSide.Companion, device).Allowed);

        var x64 = PlatformDevice.ThisPc(MachineArchitecture.FromNative(0x8664, Architecture.X64, Architecture.X64));
        Assert.False(x64.Arm64);
        Assert.Empty(x64.Features!);
    }

    [Theory]
    [InlineData(Architecture.X64, Architecture.X64, "platform.x64")]
    [InlineData(Architecture.Arm64, Architecture.X64, "platform.arm64_emulated")]
    [InlineData(Architecture.Arm64, Architecture.Arm64, "platform.arm64_native")]
    [InlineData(Architecture.X86, Architecture.X86, "platform.unsupported")]
    public void The_doctor_names_the_architecture(Architecture machine, Architecture process, string finding)
    {
        Assert.Equal(finding, ProbeRegistry.ArchitectureFinding(new(machine, process)));
        var catalog = DiagnosticCatalog.Finding(finding);
        Assert.NotNull(DiagnosticCatalog.Remedy(catalog.ActionId));
    }

    [Fact]
    public async Task The_platform_probe_runs_live_and_reports_this_pc()
    {
        var path = Path.Combine(Path.GetTempPath(), "Martlet.Tests", Guid.NewGuid().ToString("N"));
        var service = new FoundationStatusService(new SettingsStore(path));
        var report = await service.Executor.RunAsync(["platform.architecture"]);
        var probe = Assert.Single(report.Probes);
        Assert.Equal(ProbeRegistry.ArchitectureFinding(MachineArchitecture.Current), probe.DiagnosticCode);
        Assert.Equal(EvidenceProvenance.Live, probe.Provenance);
        Assert.True(probe.Required);
        Assert.Equal(MachineArchitecture.Current.WindowsOnArm || MachineArchitecture.Current.Machine == Architecture.X64 ? 0 : 2, report.ExitCode);
        Assert.False(Directory.Exists(path));
    }
}
