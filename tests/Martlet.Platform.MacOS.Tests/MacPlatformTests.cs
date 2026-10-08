using System.Runtime.InteropServices;
using Martlet.Companion.Platform;
using Martlet.Core.Platforms;

namespace Martlet.Platform.MacOS.Tests;

public sealed class MacHotkeyMapTests
{
    [Fact]
    public void ControlOptionLetterMapsToCarbonCodes()
    {
        Assert.True(MacHotkeyMap.TryMap(new("T", HotkeyModifiers.Control | HotkeyModifiers.Alt), out var key, out var mods, out _));
        Assert.Equal(0x11u, key);
        Assert.Equal(MacHotkeyMap.ControlKey | MacHotkeyMap.OptionKey, mods);
    }

    [Fact]
    public void MetaIsCommandAndAvaloniaNamesMap()
    {
        Assert.True(MacHotkeyMap.TryMap(new("Space", HotkeyModifiers.Meta | HotkeyModifiers.Shift), out var key, out var mods, out _));
        Assert.Equal(0x31u, key);
        Assert.Equal(MacHotkeyMap.CmdKey | MacHotkeyMap.ShiftKey, mods);
        Assert.True(MacHotkeyMap.TryMap(new("D5", HotkeyModifiers.Control), out key, out _, out _));
        Assert.Equal(0x17u, key);
        Assert.True(MacHotkeyMap.TryMap(new("OemTilde", HotkeyModifiers.Control), out key, out _, out _));
        Assert.Equal(0x32u, key);
        Assert.True(MacHotkeyMap.TryMap(new("f13", HotkeyModifiers.Meta), out key, out _, out _));
        Assert.Equal(0x69u, key);
    }

    [Fact]
    public void BareFunctionKeyWorksWithFnNote()
    {
        Assert.True(MacHotkeyMap.TryMap(HotkeyGesture.Default, out var key, out var mods, out _));
        Assert.Equal(0x64u, key);
        Assert.Equal(0u, mods);
        Assert.Contains("fn", MacHotkeyMap.Note(HotkeyGesture.Default), StringComparison.Ordinal);
        Assert.Equal("", MacHotkeyMap.Note(MacHotkeyMap.Suggested));
    }

    [Theory]
    [InlineData("T", HotkeyModifiers.None, "Control or Command")]
    [InlineData("T", HotkeyModifiers.Alt, "only of Option or Shift")]
    [InlineData("F8", HotkeyModifiers.Alt | HotkeyModifiers.Shift, "only of Option or Shift")]
    [InlineData("MediaPlayPause", HotkeyModifiers.Control, "can't be a push-to-talk key")]
    public void RejectsWhatMacOsRefuses(string key, HotkeyModifiers modifiers, string reason)
    {
        Assert.False(MacHotkeyMap.TryMap(new(key, modifiers), out _, out _, out var problem));
        Assert.Contains(reason, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void SuggestedGestureIsValid() => Assert.True(MacHotkeyMap.TryMap(MacHotkeyMap.Suggested, out _, out _, out _));
}

public sealed class MacOverlayRulesTests
{
    [Fact]
    public void CollectionBehaviorJoinsAllSpacesAndFullScreen() => Assert.Equal(337ul, MacOverlayRules.CollectionBehavior);

    [Fact]
    public void HitTestFlipsCocoaCoordinates()
    {
        // Window 400x600 points at Cocoa (100, 50); character in its lower part (top-left origin): y 400..600.
        OverlayRect[] character = [new(150, 400, 100, 200)];
        Assert.True(MacOverlayRules.IsOverCharacter(character, 100, 50, 400, 600, mouseX: 300, mouseY: 100)); // 50 above bottom
        Assert.False(MacOverlayRules.IsOverCharacter(character, 100, 50, 400, 600, mouseX: 300, mouseY: 600)); // near the top
        Assert.False(MacOverlayRules.IsOverCharacter(character, 100, 50, 400, 600, mouseX: 50, mouseY: 100)); // left of window
        Assert.False(MacOverlayRules.IsOverCharacter([], 100, 50, 400, 600, 300, 100));
    }

    [Fact]
    public void DragKeepsTheMouseUntilRelease()
    {
        Assert.True(MacOverlayRules.TakesMouse(overCharacter: false, buttonDown: true, takingNow: true));
        Assert.False(MacOverlayRules.TakesMouse(overCharacter: false, buttonDown: true, takingNow: false));
        Assert.False(MacOverlayRules.TakesMouse(overCharacter: false, buttonDown: false, takingNow: true));
    }

    [Fact]
    public void PixelsBecomePointsOnRetina() =>
        Assert.Equal(new OverlayRect(50, 100, 150, 200), MacOverlayRules.ToPoints(new PixelRect(100, 200, 300, 400), 2));

    [Fact]
    public void AttachNeedsAnNSWindow()
    {
        Assert.Null(MacOverlayRules.AttachProblem(42, "NSWindow"));
        Assert.Contains("no native handle", MacOverlayRules.AttachProblem(0, "NSWindow"));
        Assert.Contains("HWND", MacOverlayRules.AttachProblem(42, "HWND"));
    }
}

public sealed class KeychainCredentialStoreTests
{
    [Fact]
    public void SecretNamesBecomeCompanionAccounts() =>
        Assert.Equal("companion/openai-api-key", KeychainCredentialStore.Account("openai-api-key"));

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("ünïcode")]
    public void RejectsUnsafeNames(string name) => Assert.Throws<ArgumentException>(() => KeychainCredentialStore.Account(name));

    [Fact]
    public void ExplainsKeychainErrors()
    {
        Assert.Contains("denied", KeychainCredentialStore.Describe(-25293, "read"));
        Assert.Contains("locked", KeychainCredentialStore.Describe(-25308, "save"));
        Assert.Contains("error -1", KeychainCredentialStore.Describe(-1, "save"));
    }
}

public sealed class MacScreenCaptureTests
{
    [Fact]
    public void CaptureIsSilentJpegOfTheMainDisplay() =>
        Assert.Equal(["-x", "-t", "jpg", "-D", "1", "/tmp/a.jpg"], MacScreenCapture.CaptureArguments("/tmp/a.jpg", 1));

    [Fact]
    public void ResizeClampsQualityAndEdge() =>
        Assert.Equal(["-Z", "64", "-s", "format", "jpeg", "-s", "formatOptions", "100", "a", "--out", "b"],
            MacScreenCapture.ResizeArguments("a", "b", 10, 150));

    [Fact]
    public void ReadsJpegSizeFromStartOfFrame()
    {
        byte[] jpeg =
        [
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0,
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x02, 0xD0, 0x05, 0x00, 0x03, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1,
            0xFF, 0xD9
        ];
        Assert.Equal((1280, 720), MacScreenCapture.JpegSize(jpeg));
        Assert.Null(MacScreenCapture.JpegSize([0x89, 0x50, 0x4E, 0x47]));
    }
}

public sealed class MacAutostartTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("martlet-agents-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Fact]
    public void DevelopmentBuildUsesALaunchAgent()
    {
        var autostart = new MacAutostart("/Users/me/src/Martlet & Co/Martlet.Companion", folder);
        Assert.False(autostart.InAppBundle);
        Assert.False(autostart.IsEnabled);
        Assert.True(autostart.SetEnabled(true).Available);
        Assert.True(autostart.IsEnabled);
        var plist = File.ReadAllText(autostart.LaunchAgentPath);
        Assert.Contains("<string>/Users/me/src/Martlet &amp; Co/Martlet.Companion</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>RunAtLoad</key>", plist, StringComparison.Ordinal);
        Assert.Contains(MacAutostart.Label, plist, StringComparison.Ordinal);
        autostart.SetEnabled(false);
        Assert.False(autostart.IsEnabled);
        Assert.False(File.Exists(autostart.LaunchAgentPath));
    }

    [Fact]
    public void FindsTheAppBundle()
    {
        Assert.Equal("/Applications/Martlet.app", MacAutostart.AppBundle("/Applications/Martlet.app/Contents/MacOS/Martlet"));
        Assert.Null(MacAutostart.AppBundle("/usr/local/bin/martlet"));
    }

    [Theory]
    [InlineData(0, MacAutostartState.Off)]
    [InlineData(1, MacAutostartState.On)]
    [InlineData(2, MacAutostartState.NeedsApproval)]
    [InlineData(3, MacAutostartState.Unavailable)]
    public void MapsLoginItemStatus(long status, MacAutostartState expected) => Assert.Equal(expected, MacAutostart.MapStatus(status));
}

public sealed class MacPlatformProbeTests
{
    private static readonly PlatformInfo Portable = new() { Platform = DevicePlatform.MacOs, OsVersion = new(24, 1), MemoryGb = 8 };

    [Fact]
    public void AppleSiliconReportsUnifiedMemoryAndAppleGpu()
    {
        var info = MacPlatformProbe.FromFacts(new()
        {
            ProductVersion = "15.1", Arm64Hardware = true, Translated = false, MemoryBytes = 32UL << 30, CpuBrand = "Apple M2 Pro",
            Model = "Mac14,10"
        }, Portable with { ProcessArchitecture = Architecture.Arm64 });
        Assert.Equal(new Version(15, 1), info.OsVersion);
        Assert.True(info.AppleSilicon);
        Assert.Equal(Architecture.Arm64, info.Architecture);
        Assert.Equal(32, info.MemoryGb);
        Assert.Equal("apple", Assert.Single(info.Gpus!).Vendor);
        Assert.False(info.CpuOnlyLocalModels);
        Assert.Equal("macOS 15.1 (Apple M2 Pro, Mac14,10)", info.OsDescription);
        Assert.True(info.ToDevice().Arm64);
        Assert.DoesNotContain(MacPlatformProbe.Warnings(info), w => w.Contains("Rosetta", StringComparison.Ordinal));
    }

    [Fact]
    public void RosettaStillCountsAsAppleSilicon()
    {
        var info = MacPlatformProbe.FromFacts(new() { ProductVersion = "14.6.1", Arm64Hardware = true, Translated = true },
            Portable with { Architecture = Architecture.X64, ProcessArchitecture = Architecture.X64 });
        Assert.True(info.AppleSilicon);
        Assert.Equal(Architecture.Arm64, info.Architecture);
        Assert.Equal(Architecture.X64, info.ProcessArchitecture);
        Assert.True(info.ToDevice().Arm64);
        Assert.Contains(MacPlatformProbe.Warnings(info), w => w.Contains("Rosetta", StringComparison.Ordinal));
        Assert.Contains("Rosetta", info.OsDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void IntelMacIsCpuOnlyWithoutAppleSiliconEngines()
    {
        var info = MacPlatformProbe.FromFacts(new()
        {
            ProductVersion = "15.7", Arm64Hardware = false, Translated = false, MemoryBytes = 16UL << 30,
            CpuBrand = "Intel(R) Core(TM) i7-9750H CPU @ 2.60GHz"
        }, Portable with { Architecture = Architecture.X64 });
        Assert.False(info.AppleSilicon);
        Assert.True(info.CpuOnlyLocalModels);
        Assert.Empty(info.Gpus!);
        Assert.False(info.HasNvidia);
        var device = info.ToDevice();
        Assert.False(device.Arm64);
        Assert.Empty(device.Features!);
        var warnings = MacPlatformProbe.Warnings(info);
        Assert.Contains(warnings, w => w.Contains("CPU only", StringComparison.Ordinal) && w.Contains("MLX", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("Audio2Face", StringComparison.Ordinal));
    }

    [Fact]
    public void LocalAudio2FaceAndF5AreNeverOfferedOnAMac()
    {
        var device = MacPlatformProbe.FromFacts(new() { ProductVersion = "15.1", Arm64Hardware = true }, Portable).ToDevice();
        foreach (var side in new[] { PlatformSide.Host, PlatformSide.Companion })
        {
            Assert.Equal(PlatformVerdict.No, PlatformCatalog.Check("audio2face", side, device).Verdict);
            Assert.Equal(PlatformVerdict.No, PlatformCatalog.Check("f5", side, device).Verdict);
        }
    }

    [Fact]
    public void WarnsBelowSonoma() =>
        Assert.Contains(MacPlatformProbe.Warnings(MacPlatformProbe.FromFacts(new() { ProductVersion = "13.6", Arm64Hardware = true }, Portable)),
            w => w.Contains("Sonoma", StringComparison.Ordinal));
}

public sealed class MacTrayTextTests
{
    [Fact]
    public void TitlesShowTheState()
    {
        Assert.Equal("Martlet", MacTrayText.Title(TrayState.Idle));
        Assert.Contains("Listening", MacTrayText.Title(TrayState.Listening), StringComparison.Ordinal);
        Assert.Contains(MacTrayText.Menu, m => m.Command == TrayCommand.Quit && m.Title is not null);
    }
}
