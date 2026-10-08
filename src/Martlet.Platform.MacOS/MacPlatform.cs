using System.Runtime.Versioning;
using Martlet.Companion.Platform;

namespace Martlet.Platform.MacOS;

/// <summary>The macOS platform services (DX03); Martlet.Companion calls <see cref="Create"/> when
/// <see cref="OperatingSystem.IsMacOS"/> is true. Creating them does no native work beyond cheap reads, so the headless
/// <c>--status</c> run is safe; windows, hot keys and the menu bar item touch AppKit only once the app uses them.
/// Also here, outside the shared contracts: <see cref="MacListeningActivity"/> (no App Nap or idle sleep while
/// listening) and <see cref="MacPlatformProbe.Warnings"/>. Finding model apps on this Mac (Ollama, LM Studio, Docker Model
/// Runner and others) is shared with every platform: Martlet.Providers' LocalModelServers.</summary>
[SupportedOSPlatform("macos")]
public static class MacPlatform
{
    public static CompanionPlatform Create() => CompanionPlatform.Defaults() with
    {
        Probe = new MacPlatformProbe(),
        Hotkey = new MacPushToTalkHotkey(),
        Overlay = new MacCharacterOverlay(),
        ScreenCapture = new MacScreenCapture(),
        Credentials = new KeychainCredentialStore(),
        Autostart = new MacAutostart(),
        Tray = new MacTrayStatus()
    };
}
