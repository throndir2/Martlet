using Martlet.Companion.Platform;
using Martlet.Core.Platforms;
using Martlet.Platform.Linux.Probe;

namespace Martlet.Platform.Linux.Tests;

public sealed class LinuxProbeTests
{
    private static Dictionary<string, string> Env(params (string Key, string Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

    [Theory]
    [InlineData("wayland", "wayland-0", ":0", LinuxSessionType.Wayland)]
    [InlineData("x11", "", ":0", LinuxSessionType.X11)]
    [InlineData("", "wayland-0", "", LinuxSessionType.Wayland)]
    [InlineData("", "", ":1", LinuxSessionType.X11)]
    [InlineData("tty", "", "", LinuxSessionType.Tty)]
    [InlineData("", "", "", LinuxSessionType.Unknown)]
    public void SessionTypeComesFromXdgAndDisplays(string type, string wayland, string display, LinuxSessionType expected) =>
        Assert.Equal(expected, LinuxDesktopProbe.ClassifySession(Env(("XDG_SESSION_TYPE", type), ("WAYLAND_DISPLAY", wayland), ("DISPLAY", display))));

    [Theory]
    [InlineData("ubuntu:GNOME", LinuxDesktop.Gnome, "GNOME")]
    [InlineData("KDE", LinuxDesktop.Kde, "KDE Plasma")]
    [InlineData("Budgie:GNOME", LinuxDesktop.Budgie, "Budgie")]
    [InlineData("X-Cinnamon", LinuxDesktop.Cinnamon, "Cinnamon")]
    [InlineData("sway", LinuxDesktop.Sway, "sway")]
    [InlineData("Hyprland", LinuxDesktop.Hyprland, "Hyprland")]
    [InlineData("river", LinuxDesktop.Wlroots, "river")]
    [InlineData("Enlightenment", LinuxDesktop.Other, "Enlightenment")]
    public void DesktopComesFromXdgCurrentDesktop(string value, LinuxDesktop desktop, string name)
    {
        var (found, pretty) = LinuxDesktopProbe.ClassifyDesktop(Env(("XDG_CURRENT_DESKTOP", value)));
        Assert.Equal(desktop, found);
        Assert.Equal(name, pretty);
    }

    [Fact]
    public void DesktopFallsBackToSessionVariables()
    {
        Assert.Equal(LinuxDesktop.Kde, LinuxDesktopProbe.ClassifyDesktop(Env(("KDE_FULL_SESSION", "true"))).Desktop);
        Assert.Equal(LinuxDesktop.Unknown, LinuxDesktopProbe.ClassifyDesktop(Env()).Desktop);
    }

    [Fact]
    public void OsReleasePrefersPrettyName()
    {
        Assert.Equal("Ubuntu 24.04.1 LTS", LinuxDesktopProbe.ParseOsRelease("NAME=\"Ubuntu\"\nPRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\nID=ubuntu\n"));
        Assert.Equal("Arch Linux", LinuxDesktopProbe.ParseOsRelease("NAME='Arch Linux'\n"));
        Assert.Null(LinuxDesktopProbe.ParseOsRelease(null));
    }

    [Theory]
    [InlineData("NVRM version: NVIDIA UNIX x86_64 Kernel Module  550.54.14  Thu Feb 22 01:44:30 UTC 2024\nGCC version:", "550.54.14")]
    [InlineData("NVRM version: NVIDIA UNIX Open Kernel Module for x86_64  560.35.03  Release Build  (dvs-builder@U16)", "560.35.03")]
    [InlineData("garbage", null)]
    public void NvidiaDriverVersionFromProc(string text, string? expected) =>
        Assert.Equal(expected, LinuxDesktopProbe.ParseNvidiaProcVersion(text));

    [Fact]
    public void NvidiaSmiCsvParses()
    {
        var gpus = LinuxDesktopProbe.ParseNvidiaSmi("NVIDIA GeForce RTX 4070, 12282, 550.54.14\nNVIDIA RTX A2000, 6138, 550.54.14\n");
        Assert.Equal(2, gpus.Count);
        Assert.Equal("NVIDIA GeForce RTX 4070", gpus[0].Name);
        Assert.Equal(12282, gpus[0].MemoryMegabytes);
        Assert.Equal("550.54.14", gpus[1].Driver);
        Assert.Empty(LinuxDesktopProbe.ParseNvidiaSmi("NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver."));
    }

    [Fact]
    public void ReadCombinesSmiAndDrmCards()
    {
        var facts = LinuxDesktopProbe.Read(
            Env(("XDG_SESSION_TYPE", "wayland"), ("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0"), ("XDG_CURRENT_DESKTOP", "KDE")),
            path => path == "/etc/os-release" ? "PRETTY_NAME=\"Fedora Linux 41\"" : null,
            () => [new("0x10de", "nvidia", null), new("0x1002", "amdgpu", 8L * 1024 * 1024 * 1024), new("0x8086", "i915", null)],
            (_, _) => "NVIDIA GeForce RTX 3060, 12288, 555.42\n",
            path => path == "/dev/kfd");
        Assert.Equal(LinuxSessionType.Wayland, facts.Session);
        Assert.True(facts.LayerShellCompositor);
        Assert.Equal("Fedora Linux 41", facts.Distribution);
        Assert.Equal("555.42", facts.NvidiaDriver);
        Assert.True(facts.Rocm);
        Assert.Collection(facts.Gpus,
            g => Assert.Equal(("NVIDIA GeForce RTX 3060", 12288L), (g.Name, g.VramMegabytes!.Value)),
            g => Assert.Equal((GpuVendor.Amd, 8192L), (g.Vendor, g.VramMegabytes!.Value)),
            g => Assert.Equal((GpuVendor.Intel, "i915"), (g.Vendor, g.Driver)));
    }

    [Fact]
    public void MergeAddsAmdAndIntelAndMarksNouveauAsNoCuda()
    {
        var merged = LinuxPlatformProbe.Merge([new PlatformGpu("NVIDIA GeForce RTX 3060", "nvidia", 12)],
        [
            new LinuxGpu(GpuVendor.Nvidia, "NVIDIA GPU", "nvidia", null),
            new LinuxGpu(GpuVendor.Amd, "AMD GPU", "amdgpu", 16384)
        ]);
        Assert.Equal(["nvidia", "amd"], merged.Select(g => g.Vendor));
        Assert.Equal(16, merged[1].MemoryGb);

        var nouveau = LinuxPlatformProbe.Merge([], [new LinuxGpu(GpuVendor.Nvidia, "NVIDIA GPU", "nouveau", null)]);
        Assert.False(nouveau.Single().IsNvidia);
        Assert.Contains("no CUDA", nouveau.Single().Name);
    }
}
