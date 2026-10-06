using System.Runtime.InteropServices;
using Martlet.Companion.Platform;
using Martlet.Core.Platforms;

namespace Martlet.Companion.Tests;

public sealed class PlatformProbeTests
{
    private static DefaultPlatformProbe Probe(Dictionary<string, string> env, Dictionary<string, string>? files = null, string? smi = null) =>
        new(name => env.GetValueOrDefault(name), path => files?.GetValueOrDefault(path), (_, _) => smi);

    [Fact]
    public void LinuxWaylandSessionWithXWaylandAndNvidiaIsDetected()
    {
        var info = Probe(new() { ["XDG_SESSION_TYPE"] = "wayland", ["DISPLAY"] = ":0", ["XDG_CURRENT_DESKTOP"] = "ubuntu:GNOME" },
            new() { ["/etc/os-release"] = "NAME=\"Ubuntu\"\nPRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\n" },
            "NVIDIA GeForce RTX 4070, 12282\n").Probe(DevicePlatform.Linux);

        Assert.Equal(DisplayServer.Wayland, info.DisplayServer);
        Assert.True(info.XWayland);
        Assert.Equal("ubuntu:GNOME", info.Desktop);
        Assert.Equal("Ubuntu 24.04.1 LTS", info.OsDescription);
        Assert.True(info.HasNvidia);
        Assert.Equal(12, info.Gpus![0].MemoryGb!.Value, 0);
    }

    [Fact]
    public void LinuxX11WithoutNvidiaSmiFallsBackToDriverProcEntries()
    {
        var info = Probe(new() { ["DISPLAY"] = ":0" },
            new()
            {
                ["/proc/driver/nvidia/gpus/.list"] = "0000:01:00.0",
                ["/proc/driver/nvidia/gpus/0000:01:00.0/information"] = "Model: \t\t NVIDIA GeForce GTX 1060 6GB\nIRQ: 1\n"
            }).Probe(DevicePlatform.Linux);

        Assert.Equal(DisplayServer.X11, info.DisplayServer);
        Assert.Null(info.XWayland);
        Assert.Equal("NVIDIA GeForce GTX 1060 6GB", Assert.Single(info.Gpus!).Name);
    }

    [Fact]
    public void LinuxWithoutDisplayIsHeadlessAndReportsNoGpu()
    {
        var info = Probe([]).Probe(DevicePlatform.Linux);
        Assert.Equal(DisplayServer.Headless, info.DisplayServer);
        Assert.False(info.HasNvidia);
        Assert.NotNull(info.Gpus);
    }

    [Fact]
    public void IntelMacIsCpuOnlyAndNotAppleSilicon()
    {
        var info = new PlatformInfo { Platform = DevicePlatform.MacOs, AppleSilicon = false, Architecture = Architecture.X64, Gpus = [] };
        Assert.True(info.CpuOnlyLocalModels);
        Assert.False(info.ToDevice().Arm64);
        var silicon = info with { AppleSilicon = true, Architecture = Architecture.Arm64 };
        Assert.False(silicon.CpuOnlyLocalModels);
        Assert.True(silicon.ToDevice().Arm64);
    }

    [Fact]
    public void DefaultsRunWithEveryServiceReportingWhy()
    {
        var platform = CompanionPlatform.Defaults();
        Assert.False(platform.Hotkey.Status.Available);
        Assert.False(platform.ScreenCapture.Status.Available);
        Assert.False(platform.Credentials.IsPersistent);
        var json = CompanionStatus.Json(platform, platform.Probe.Probe());
        Assert.Contains("\"hotkey\"", json, StringComparison.Ordinal);
        Assert.Contains("isn't built for this system yet", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionCredentialStoreKeepsSecretsInMemoryOnly()
    {
        var store = new SessionCredentialStore();
        await store.SetAsync("openai-api-key", "sk-test", CancellationToken.None);
        Assert.Equal("sk-test", await store.GetAsync("openai-api-key", CancellationToken.None));
        await store.DeleteAsync("openai-api-key", CancellationToken.None);
        Assert.Null(await store.GetAsync("openai-api-key", CancellationToken.None));
    }

    [Fact]
    public void HotkeyGestureReadsLikeAShortcut() =>
        Assert.Equal("Control+Shift+Space", new HotkeyGesture("Space", HotkeyModifiers.Control | HotkeyModifiers.Shift).ToString());
}
