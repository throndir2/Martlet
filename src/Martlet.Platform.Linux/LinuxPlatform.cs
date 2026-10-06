using System.Runtime.Versioning;
using Martlet.Companion.Platform;
using Martlet.Platform.Linux.Capture;
using Martlet.Platform.Linux.Credentials;
using Martlet.Platform.Linux.Desktop;
using Martlet.Platform.Linux.Hotkeys;
using Martlet.Platform.Linux.Overlay;
using Martlet.Platform.Linux.Probe;

namespace Martlet.Platform.Linux;

/// <summary>The Linux platform services (DX02 in docs/DESKTOP_LINUX_MACOS.md). Reads the desktop once (X11 or Wayland,
/// which portals the session bus offers, Secret Service, tray host) and picks each service's route from that; every
/// service reports in plain words what works and what is degraded. Martlet.Companion calls <see cref="Create()"/> when
/// <see cref="OperatingSystem.IsLinux"/> is true.</summary>
[SupportedOSPlatform("linux")]
public static class LinuxPlatform
{
    public static CompanionPlatform Create() => Create(LinuxEnvironment.Read());

    public static CompanionPlatform Create(LinuxEnvironment environment)
    {
        var platform = CompanionPlatform.Defaults() with
        {
            Probe = new LinuxPlatformProbe(LinuxDesktopProbe.Read(nvidiaSmi: false)),
            Hotkey = new LinuxHotkey(environment),
            Overlay = environment.OverlayStatus.Available
                ? new X11Overlay(environment.OverlayStatus, environment.Desktop.Session == LinuxSessionType.Wayland)
                : new UnavailableOverlay(environment.OverlayStatus),
            ScreenCapture = new LinuxScreenCapture(environment),
            Autostart = XdgAutostart.ForCurrentUser()
        };
        // Without a Secret Service the default keeps keys in memory only (and says so) rather than writing them anywhere.
        return environment.Bus.SecretService
            ? platform with { Credentials = new SecretServiceStore(environment.CredentialStatus) }
            : platform;
    }
}
