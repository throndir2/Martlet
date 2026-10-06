using Martlet.Platform.Linux.Probe;

namespace Martlet.Platform.Linux.Tests;

public sealed class LinuxEnvironmentTests
{
    private static LinuxEnvironment Environment(LinuxSessionType session, LinuxDesktop desktop, bool x11 = true,
        uint? shortcuts = null, uint? cast = null, uint? shot = null, bool secrets = true, bool tray = true, bool pipewire = true)
    {
        var name = desktop switch { LinuxDesktop.Gnome => "GNOME", LinuxDesktop.Kde => "KDE Plasma", _ => desktop.ToString() };
        var facts = new LinuxDesktopFacts(session, desktop, name, x11, session == LinuxSessionType.Wayland, "Ubuntu 24.04", [], null, false);
        var bus = new LinuxBusFacts(true, null, true, shortcuts, cast, shot, secrets, tray);
        return new LinuxEnvironment(facts, bus, X11Libraries: true, PipeWireLibrary: pipewire);
    }

    [Fact]
    public void WaylandWithGlobalShortcutsUsesThePortal()
    {
        var environment = Environment(LinuxSessionType.Wayland, LinuxDesktop.Kde, shortcuts: 1);
        Assert.Equal(HotkeyRoute.Portal, environment.HotkeyRoute);
        Assert.True(environment.HotkeyStatus.Available);
        Assert.Contains("GlobalShortcuts portal", environment.HotkeyStatus.Reason);
    }

    [Fact]
    public void WaylandWithoutThePortalFallsBackToXWaylandAndSaysItIsLimited()
    {
        var environment = Environment(LinuxSessionType.Wayland, LinuxDesktop.Gnome);
        Assert.Equal(HotkeyRoute.X11, environment.HotkeyRoute);
        Assert.StartsWith("Limited", environment.HotkeyStatus.Reason);
    }

    [Fact]
    public void X11UsesTheKeyGrabAndNoDisplayMeansNoKey()
    {
        Assert.Equal(HotkeyRoute.X11, Environment(LinuxSessionType.X11, LinuxDesktop.Xfce, shortcuts: 1).HotkeyRoute);
        var none = Environment(LinuxSessionType.Wayland, LinuxDesktop.Gnome, x11: false);
        Assert.Equal(HotkeyRoute.None, none.HotkeyRoute);
        Assert.False(none.HotkeyStatus.Available);
    }

    [Fact]
    public void OverlayReportsXWaylandAndUnusedLayerShell()
    {
        var kde = Environment(LinuxSessionType.Wayland, LinuxDesktop.Kde).OverlayStatus;
        Assert.True(kde.Available);
        Assert.StartsWith("XWayland", kde.Reason);
        Assert.Contains("layer-shell", kde.Reason);
        Assert.DoesNotContain("layer-shell", Environment(LinuxSessionType.Wayland, LinuxDesktop.Gnome).OverlayStatus.Reason);
        Assert.StartsWith("X11", Environment(LinuxSessionType.X11, LinuxDesktop.Gnome).OverlayStatus.Reason);
        Assert.False(Environment(LinuxSessionType.Wayland, LinuxDesktop.Gnome, x11: false).OverlayStatus.Available);
    }

    [Fact]
    public void CaptureRoutesByDisplayServerAndPortals()
    {
        Assert.Equal(CaptureRoute.X11, Environment(LinuxSessionType.X11, LinuxDesktop.Gnome, cast: 5).CaptureRoute);
        Assert.Equal(CaptureRoute.ScreenCastPortal, Environment(LinuxSessionType.Wayland, LinuxDesktop.Gnome, cast: 5, shot: 2).CaptureRoute);
        Assert.Equal(CaptureRoute.ScreenshotPortal, Environment(LinuxSessionType.Wayland, LinuxDesktop.Gnome, cast: 5, shot: 2, pipewire: false).CaptureRoute);
        var none = Environment(LinuxSessionType.Wayland, LinuxDesktop.Gnome);
        Assert.Equal(CaptureRoute.None, none.CaptureRoute);
        Assert.False(none.CaptureStatus.Available);
        Assert.Contains("never remembered", Environment(LinuxSessionType.Wayland, LinuxDesktop.Kde, cast: 5).CaptureStatus.Reason);
    }

    [Fact]
    public void CredentialsAndTrayFollowTheBus()
    {
        Assert.True(Environment(LinuxSessionType.X11, LinuxDesktop.Kde).CredentialStatus.Available);
        Assert.False(Environment(LinuxSessionType.X11, LinuxDesktop.Kde, secrets: false).CredentialStatus.Available);
        Assert.Contains("AppIndicator", Environment(LinuxSessionType.Wayland, LinuxDesktop.Gnome, tray: false).TrayStatus.Reason);
    }

    [Fact]
    public void WithoutASessionBusEverythingBusBasedIsUnavailable()
    {
        var facts = new LinuxDesktopFacts(LinuxSessionType.X11, LinuxDesktop.Xfce, "Xfce", true, false, null, [], null, false);
        var environment = new LinuxEnvironment(facts, LinuxBusFacts.None("no bus"), true, false);
        Assert.False(environment.CredentialStatus.Available);
        Assert.Contains("no bus", environment.CredentialStatus.Reason);
        Assert.Equal(HotkeyRoute.X11, environment.HotkeyRoute);
    }
}
