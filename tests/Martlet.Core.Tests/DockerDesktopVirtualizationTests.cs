using Martlet.Core.Installation;

namespace Martlet.Core.Tests;

public sealed class DockerDesktopVirtualizationTests
{
    private const string Ready =
        "firmware=False|hypervisor=True|vmp=1|wsl=1|wslversion=2.6.1|vm=False|compute=Running|network=Running|wslstatus=Available|wslexit=0|restart=False|issues=";

    [Fact]
    public void FreshPcWithFirmwareVirtualizationOffTurnsOnFeaturesBeforeTheFirmwareRestart()
    {
        var state = WindowsVirtualization.Parse(
            "firmware=False|hypervisor=False|vmp=2|wsl=2|wslversion=none|vm=False|compute=Absent|network=Absent|wslstatus=Failed|wslexit=1|restart=True|issues=");

        Assert.True(state.FirmwareOff);
        Assert.True(state.FeaturesBeforeFirmware);
        Assert.False(state.NeedsChanges);
        Assert.False(state.RestartRequired);
        Assert.Equal(
            ["virtualization is turned off in this PC's firmware (UEFI/BIOS)", "Virtual Machine Platform is off",
             "Windows Subsystem for Linux is off", "WSL isn't installed"],
            state.Problems());
        Assert.Contains("one restart finishes both", state.Recovery, StringComparison.Ordinal);
    }

    [Fact]
    public void FirmwareOffWithFeaturesOnOnlyNeedsTheFirmware()
    {
        var state = WindowsVirtualization.Parse(
            "firmware=False|hypervisor=False|vmp=1|wsl=1|wslversion=2.6.1|vm=False|compute=Stopped|network=Stopped|wslstatus=Unavailable|wslexit=0|restart=False|issues=");

        Assert.True(state.FirmwareOff);
        Assert.False(state.FeaturesBeforeFirmware);
        Assert.Equal(["virtualization is turned off in this PC's firmware (UEFI/BIOS)"], state.Problems());
        Assert.StartsWith("Turn on Intel VT-x or AMD SVM", state.Recovery, StringComparison.Ordinal);
    }

    [Fact]
    public void VirtualMachineWithoutNestedVirtualizationIsNotChangedFromInside()
    {
        var state = WindowsVirtualization.Parse(
            "firmware=False|hypervisor=False|vmp=2|wsl=2|wslversion=none|vm=True|compute=Absent|network=Absent|wslstatus=Failed|wslexit=1|restart=False|issues=");

        Assert.True(state.FirmwareOff);
        Assert.False(state.FeaturesBeforeFirmware);
        Assert.Equal(["hardware virtualization is not exposed to this virtual machine"], state.Problems());
        Assert.Contains("nested virtualization", state.Recovery, StringComparison.Ordinal);
    }

    [Fact]
    public void FeaturesOffWithVirtualizationAvailableNeedChangesNotFirmware()
    {
        var state = WindowsVirtualization.Parse(
            "firmware=True|hypervisor=False|vmp=2|wsl=2|wslversion=none|vm=False|compute=Absent|network=Absent|wslstatus=Failed|wslexit=1|restart=False|issues=");

        Assert.False(state.FirmwareOff);
        Assert.False(state.FeaturesBeforeFirmware);
        Assert.True(state.NeedsChanges);
        Assert.DoesNotContain(state.Problems(), p => p.Contains("firmware", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadyWindowsHasNoProblems()
    {
        var state = WindowsVirtualization.Parse(Ready);

        Assert.True(state.Ready);
        Assert.False(state.Blocked);
        Assert.False(state.FeaturesBeforeFirmware);
        Assert.Empty(state.Problems());
    }

    [Fact]
    public void DockerSayingWslIsMissingCountsWhenTheWindowsProbeTimedOut()
    {
        const string check = "checking WSL version: wsl is not installed";
        Assert.Equal(check, DockerDesktopStatus.Precondition(
            "[2026-10-04T21:20:00.000000000Z][com.docker.backend.exe.engines][E] attempting to recover from error: starting engine: " +
            "engine linux/wsl failed to start: checking preconditions: checking WSL version: wsl is not installed"));
        var unknown = WindowsVirtualization.Unknown with { ProbeIssues = ["Windows prerequisite probe timed out after one minute"] };
        Assert.False(unknown.NeedsChanges);

        var state = unknown with { DockerPrecondition = check };

        Assert.True(WindowsVirtualization.WindowsCanFix(check));
        Assert.True(state.DockerNeedsWindows);
        Assert.True(state.NeedsChanges);
        Assert.True(state.Blocked);
        Assert.False(state.Ready);
        Assert.Contains($"Docker Desktop says \"{check}\"", state.Problems());
        Assert.StartsWith("Let Martlet set up the Windows features and WSL", state.Recovery, StringComparison.Ordinal);
        Assert.Contains("Docker Desktop says", state.Describe(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("WSL update required")]
    [InlineData("Virtual Machine Platform not enabled")]
    public void DockerSayingWindowsFeaturesAreMissingCountsWhenUnread(string check)
    {
        Assert.True(WindowsVirtualization.WindowsCanFix(check));
        Assert.True((WindowsVirtualization.Unknown with { DockerPrecondition = check }).NeedsChanges);
    }

    [Fact]
    public void DockerSayingWslIsMissingDoesNotOverrideWhatWindowsReported()
    {
        // Docker Desktop's message can be from before WSL was installed: a WSL version Windows reports wins.
        var state = WindowsVirtualization.Parse(Ready) with { DockerPrecondition = "checking WSL version: wsl is not installed" };

        Assert.False(state.DockerNeedsWindows);
        Assert.False(state.NeedsChanges);
        Assert.True(state.Ready);
        Assert.Empty(state.Problems());
    }

    [Theory]
    [InlineData("No virtualization available")]
    [InlineData("querying the Win32OptionalFeature class over WMI: Exception occurred")]
    [InlineData(null)]
    public void OtherDockerChecksDoNotAskForWindowsChanges(string? check)
    {
        Assert.False(WindowsVirtualization.WindowsCanFix(check));
        Assert.False((WindowsVirtualization.Unknown with { DockerPrecondition = check }).NeedsChanges);
    }

    [Theory]
    [InlineData("[2026-10-04T19:40:26.085281500Z][com.docker.backend.exe.engines][E] engine linux/wsl failed to start: checking preconditions: Virtual Machine Platform not enabled",
        "Virtual Machine Platform not enabled")]
    [InlineData("[2024-06-17T18:35:46.126301000Z][com.docker.backend.exe.engines][E] engine was terminated with result: waiting for the VM setup to be ready: checking preconditions: WSL update required",
        "WSL update required")]
    [InlineData("[2025-08-01T08:40:56.738286200Z][com.docker.backend.exe.engines][W] attempting recovery from engine failure: running engine: waiting for the VM setup to be ready: checking preconditions: querying the Win32OptionalFeature class over WMI: Exception occurred. (<nil>)",
        "querying the Win32OptionalFeature class over WMI: Exception occurred")]
    [InlineData("No virtualization available", "No virtualization available")]
    [InlineData("Docker Desktop - Virtual Machine Platform not enabled", "Virtual Machine Platform not enabled")]
    [InlineData("Virtualization support not detected", "Virtualization support not detected")]
    [InlineData("Docker Desktop failed to start because virtualisation support wasn't detected.", "virtualisation support wasn't detected")]
    public void RecognizesDockerDesktopsFailedStartCheck(string line, string expected) =>
        Assert.Equal(expected, DockerDesktopStatus.Precondition(line));

    [Theory]
    [InlineData("[2026-10-04T19:40:26.085281500Z][com.docker.backend.exe.idle] event handler 254fa8c3: request cancelled by client")]
    [InlineData("error during connect: open //./pipe/dockerDesktopLinuxEngine: The system cannot find the file specified.")]
    [InlineData("[2026-10-04T19:40:26.085281500Z][com.docker.backend.exe.engines][E] engine linux/wsl failed to start: checking preconditions:")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherDockerDesktopLinesAreNotAFailedStartCheck(string? line) => Assert.Null(DockerDesktopStatus.Precondition(line));

    [Fact]
    public void ReadsDockerDesktopLogTimesInUtc()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 19, 40, 26, TimeSpan.Zero).AddTicks(852815),
            DockerDesktopStatus.LogTime("[2026-10-04T19:40:26.085281500Z][com.docker.backend.exe.idle] event"));
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 19, 40, 26, TimeSpan.Zero), DockerDesktopStatus.LogTime("[2026-10-04T19:40:26Z][x] y"));
        Assert.Null(DockerDesktopStatus.LogTime("Virtual Machine Platform not enabled"));
    }

    [Fact]
    public void OnlyFailuresSinceDockerDesktopRestartedCount()
    {
        string[] log =
        [
            "[2026-10-04T19:40:00.000000000Z][com.docker.backend.exe.engines][E] engine linux/wsl failed to start: checking preconditions:",
            "Virtual Machine Platform not enabled",
            "No virtualization available",
            "[2026-10-04T19:41:00.000000000Z][com.docker.backend.exe.engines][W] something else"
        ];
        var before = new DateTimeOffset(2026, 10, 4, 19, 39, 0, TimeSpan.Zero);
        var after = new DateTimeOffset(2026, 10, 4, 19, 40, 30, TimeSpan.Zero);

        Assert.Equal("No virtualization available", DockerDesktopStatus.LastPrecondition(log, DateTimeOffset.MinValue));
        Assert.Equal("No virtualization available", DockerDesktopStatus.LastPrecondition(log, before));
        Assert.Null(DockerDesktopStatus.LastPrecondition(log, after));
        Assert.Null(DockerDesktopStatus.LastPrecondition([], DateTimeOffset.MinValue));
    }
}
