using Martlet.Companion.Platform;
using Martlet.Platform.Linux.DBus;
using Bus = Martlet.Platform.Linux.DBus.SessionBus;
using PortalApi = Martlet.Platform.Linux.DBus.Portal;
using Martlet.Platform.Linux.Probe;

namespace Martlet.Platform.Linux;

/// <summary>What the session bus offers: portal interfaces (and their versions), the Secret Service and a tray host.
/// Null versions mean the portal does not offer that interface here.</summary>
public sealed record LinuxBusFacts(
    bool SessionBus,
    string? BusError,
    bool Portal,
    uint? GlobalShortcutsVersion,
    uint? ScreenCastVersion,
    uint? ScreenshotVersion,
    bool SecretService,
    bool StatusNotifierHost)
{
    public static LinuxBusFacts None(string error) => new(false, error, false, null, null, null, false, false);

    /// <summary>Asks the session bus, waiting at most a few seconds. Never throws.</summary>
    public static LinuxBusFacts Read(TimeSpan? timeout = null)
    {
        try { return Task.Run(ReadAsync).WaitAsync(timeout ?? TimeSpan.FromSeconds(4)).GetAwaiter().GetResult(); }
        catch (TimeoutException) { return None("the session bus did not answer in time"); }
        catch (Exception error) { return None(error.Message); }
    }

    private static async Task<LinuxBusFacts> ReadAsync()
    {
        var (bus, error) = await Bus.ConnectAsync().ConfigureAwait(false);
        if (bus is null) return None(error ?? "no session bus");
        using (bus)
        {
            var portal = await bus.NameHasOwnerAsync(PortalApi.Service).ConfigureAwait(false) ||
                await Activatable(bus, PortalApi.Service).ConfigureAwait(false);
            uint? shortcuts = null, cast = null, shot = null;
            if (portal)
            {
                shortcuts = await PortalApi.VersionAsync(bus, "org.freedesktop.portal.GlobalShortcuts").ConfigureAwait(false);
                cast = await PortalApi.VersionAsync(bus, "org.freedesktop.portal.ScreenCast").ConfigureAwait(false);
                shot = await PortalApi.VersionAsync(bus, "org.freedesktop.portal.Screenshot").ConfigureAwait(false);
            }
            var secrets = await bus.NameHasOwnerAsync("org.freedesktop.secrets").ConfigureAwait(false) ||
                await Activatable(bus, "org.freedesktop.secrets").ConfigureAwait(false);
            var tray = false;
            if (await bus.NameHasOwnerAsync("org.kde.StatusNotifierWatcher").ConfigureAwait(false))
            {
                var registered = await bus.GetPropertyAsync("org.kde.StatusNotifierWatcher", "/StatusNotifierWatcher",
                    "org.kde.StatusNotifierWatcher", "IsStatusNotifierHostRegistered").ConfigureAwait(false);
                tray = registered is not { } value || value.GetBool();
            }
            return new LinuxBusFacts(true, null, portal, shortcuts, cast, shot, secrets, tray);
        }
    }

    private static async Task<bool> Activatable(Bus bus, string name)
    {
        try { return (await bus.Connection.ListActivatableServicesAsync().ConfigureAwait(false)).Contains(name); }
        catch (Tmds.DBus.Protocol.DBusExceptionBase) { return false; }
    }
}

/// <summary>One snapshot of the Linux desktop (<see cref="LinuxDesktopFacts"/> and <see cref="LinuxBusFacts"/>) and the
/// plain-words status of every companion feature on it. The decisions are pure so they can be tested on any OS.</summary>
public sealed record LinuxEnvironment(LinuxDesktopFacts Desktop, LinuxBusFacts Bus, bool X11Libraries, bool PipeWireLibrary)
{
    public static LinuxEnvironment Read() =>
        new(LinuxDesktopProbe.Read(nvidiaSmi: false), LinuxBusFacts.Read(), Native.NativeLibraries.HasX11, Native.NativeLibraries.HasPipeWire);

    private bool Wayland => Desktop.Session == LinuxSessionType.Wayland;
    private bool X11Usable => Desktop.HasX11Display && X11Libraries;

    /// <summary>Which push-to-talk route to use: the GlobalShortcuts portal on Wayland when offered, otherwise an X11 grab.</summary>
    public HotkeyRoute HotkeyRoute =>
        Wayland && Bus.GlobalShortcutsVersion is not null ? HotkeyRoute.Portal
        : X11Usable ? HotkeyRoute.X11
        : Bus.GlobalShortcutsVersion is not null ? HotkeyRoute.Portal
        : HotkeyRoute.None;

    public FeatureStatus HotkeyStatus => HotkeyRoute switch
    {
        HotkeyRoute.Portal => FeatureStatus.Yes(
            $"GlobalShortcuts portal (version {Bus.GlobalShortcutsVersion}); {Desktop.DesktopName} asks you to confirm the key the first time"),
        HotkeyRoute.X11 when Wayland => FeatureStatus.Yes(
            $"Limited: {Desktop.DesktopName} on Wayland has no GlobalShortcuts portal, so the key is grabbed through XWayland and only works while an X11 app (or Martlet) has focus. GNOME 48+ and KDE Plasma offer the portal."),
        HotkeyRoute.X11 => FeatureStatus.Yes("X11 key grab"),
        _ => FeatureStatus.No(Wayland
            ? $"{Desktop.DesktopName} offers no GlobalShortcuts portal and there is no XWayland display; use the push-to-talk button in the window."
            : "No X11 display or libX11; use the push-to-talk button in the window.")
    };

    public FeatureStatus OverlayStatus
    {
        get
        {
            if (!X11Usable)
                return FeatureStatus.No(Desktop.HasX11Display
                    ? "libX11/libXext are missing, so the character window can't be made click-through."
                    : "There is no X11 or XWayland display; the character shows as a normal window.");
            if (!Wayland) return FeatureStatus.Yes("X11: always on top, transparent, click-through except the character, no focus, all workspaces");
            var layerShell = Desktop.LayerShellCompositor
                ? $" {Desktop.DesktopName} offers layer-shell, but Avalonia 12 has no native Wayland backend, so it isn't used."
                : "";
            return FeatureStatus.Yes(
                $"XWayland: on top of normal windows, transparent and click-through except the character. Full-screen native Wayland games may cover it.{layerShell}");
        }
    }

    public CaptureRoute CaptureRoute =>
        Wayland ? (Bus.ScreenCastVersion is not null && PipeWireLibrary ? CaptureRoute.ScreenCastPortal
                : Bus.ScreenshotVersion is not null ? CaptureRoute.ScreenshotPortal : CaptureRoute.None)
            : X11Usable ? CaptureRoute.X11 : CaptureRoute.None;

    public FeatureStatus CaptureStatus => CaptureRoute switch
    {
        CaptureRoute.X11 => FeatureStatus.Yes("X11 (XGetImage of the screen), only while watching is on"),
        CaptureRoute.ScreenCastPortal => FeatureStatus.Yes(
            "ScreenCast portal + PipeWire: the system asks which screen to share each time watching starts; never remembered"),
        CaptureRoute.ScreenshotPortal => FeatureStatus.Yes(
            "Screenshot portal (PipeWire unavailable): the system may ask before each look"),
        _ => FeatureStatus.No(Wayland
            ? "This Wayland desktop offers no ScreenCast or Screenshot portal (install xdg-desktop-portal and your desktop's backend, and libpipewire-0.3)."
            : "No X11 display to capture.")
    };

    public FeatureStatus CredentialStatus => Bus.SecretService
        ? FeatureStatus.Yes("Secret Service (GNOME Keyring / KWallet)")
        : FeatureStatus.No(Bus.SessionBus
            ? "No Secret Service on the session bus (install gnome-keyring or enable KWallet); keys are kept only until Martlet quits."
            : $"No session bus ({Bus.BusError}); keys are kept only until Martlet quits.");

    public FeatureStatus TrayStatus => Bus.StatusNotifierHost
        ? FeatureStatus.Yes("StatusNotifierItem tray")
        : FeatureStatus.No(Desktop.Desktop == LinuxDesktop.Gnome
            ? "GNOME shows tray icons only with the AppIndicator extension (Ubuntu ships it); use the Martlet window instead."
            : "No StatusNotifierItem host on this desktop; use the Martlet window instead.");
}

public enum HotkeyRoute { None, Portal, X11 }

public enum CaptureRoute { None, X11, ScreenCastPortal, ScreenshotPortal }
