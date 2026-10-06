using Martlet.Companion.Platform;

namespace Martlet.Platform.Linux.Hotkeys;

/// <summary>The Linux push-to-talk key: the GlobalShortcuts portal on Wayland when the desktop offers it, otherwise an X11
/// key grab (on Wayland through XWayland, which only sees keys while an X11 window has focus).</summary>
internal sealed class LinuxHotkey(LinuxEnvironment environment) : IPushToTalkHotkey
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IDisposable? active;

    public FeatureStatus Status { get; private set; } = environment.HotkeyStatus;

    public event EventHandler? Pressed;
    public event EventHandler? Released;

    public async Task<FeatureStatus> RegisterAsync(HotkeyGesture gesture, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Stop();
            if (environment.HotkeyRoute == HotkeyRoute.None) return Status = environment.HotkeyStatus;
            void Down() => Pressed?.Invoke(this, EventArgs.Empty);
            void Up() => Released?.Invoke(this, EventArgs.Empty);
            if (environment.HotkeyRoute == HotkeyRoute.Portal)
            {
                var (shortcut, status) = await PortalShortcut.StartAsync(gesture, Down, Up, cancellationToken).ConfigureAwait(false);
                active = shortcut;
                // A desktop whose portal fails can still have XWayland; fall back rather than leave the user without a key.
                if (shortcut is null && environment.Desktop.HasX11Display && environment.X11Libraries)
                {
                    var (grab, fallback) = await X11KeyGrab.StartAsync(gesture, "XWayland key grab (only while an X11 window has focus)", Down, Up)
                        .ConfigureAwait(false);
                    active = grab;
                    return Status = grab is null ? status : FeatureStatus.Yes($"{fallback.Reason}; the portal said: {status.Reason}");
                }
                return Status = status;
            }
            var mode = environment.Desktop.Session == Probe.LinuxSessionType.Wayland
                ? "XWayland key grab (only while an X11 window has focus)" : "X11 key grab";
            var (x11, x11Status) = await X11KeyGrab.StartAsync(gesture, mode, Down, Up).ConfigureAwait(false);
            active = x11;
            return Status = x11Status;
        }
        finally { gate.Release(); }
    }

    public void Unregister()
    {
        gate.Wait();
        try
        {
            Stop();
            Status = environment.HotkeyStatus;
        }
        finally { gate.Release(); }
    }

    private void Stop()
    {
        active?.Dispose();
        active = null;
    }

    public void Dispose() => Unregister();
}
