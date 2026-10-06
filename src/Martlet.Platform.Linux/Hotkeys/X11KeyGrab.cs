using Martlet.Companion.Platform;
using Martlet.Platform.Linux.Native;
using static Martlet.Platform.Linux.Native.Xlib;

namespace Martlet.Platform.Linux.Hotkeys;

/// <summary>A passive XGrabKey on the root window, read on its own thread through its own X connection. Key down raises
/// <c>down</c> once per hold and key up raises <c>up</c>; auto-repeat is swallowed (detectable auto-repeat, and a
/// release immediately followed by a press at the same time is treated as repeat). Under XWayland the X server only sees
/// keys while an X11 window has focus.</summary>
internal sealed class X11KeyGrab : IDisposable
{
    private readonly Thread thread;
    private readonly string keysym;
    private readonly uint mask;
    private readonly Action down, up;
    private readonly TaskCompletionSource<FeatureStatus> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool stopping;

    private X11KeyGrab(string keysym, uint mask, Action down, Action up)
    {
        this.keysym = keysym;
        this.mask = mask;
        this.down = down;
        this.up = up;
        thread = new Thread(Run) { IsBackground = true, Name = "Martlet X11 push-to-talk" };
    }

    public static async Task<(X11KeyGrab? Grab, FeatureStatus Status)> StartAsync(HotkeyGesture gesture, string mode, Action down, Action up)
    {
        var keysym = LinuxKeys.Keysym(gesture.Key);
        if (keysym is null) return (null, FeatureStatus.No($"{gesture.Key} can't be used as a global key on Linux."));
        var grab = new X11KeyGrab(keysym, LinuxKeys.X11Mask(gesture.Modifiers), down, up);
        grab.thread.Start();
        var status = await grab.started.Task.ConfigureAwait(false);
        if (!status.Available) { grab.Dispose(); return (null, status); }
        return (grab, FeatureStatus.Yes($"{mode}: hold {gesture} to talk"));
    }

    private unsafe void Run()
    {
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero) { started.TrySetResult(FeatureStatus.No("Cannot open the X11 display.")); return; }
        TrackErrors(display);
        var root = XDefaultRootWindow(display);
        var keycode = XKeysymToKeycode(display, XStringToKeysym(keysym));
        var masks = LinuxKeys.GrabMasks(mask);
        try
        {
            if (keycode == 0) { started.TrySetResult(FeatureStatus.No($"This keyboard has no {keysym} key.")); return; }
            XkbSetDetectableAutoRepeat(display, true, out _);
            foreach (var m in masks) XGrabKey(display, keycode, m, root, false, GrabModeAsync, GrabModeAsync);
            XSync(display, false);
            if (TakeError(display) is var error and not 0)
            {
                started.TrySetResult(FeatureStatus.No(error == BadAccess
                    ? "That key is already taken by another app; choose another."
                    : $"The X server refused the key (X error {error})."));
                return;
            }
            started.TrySetResult(FeatureStatus.Yes());
            Loop(display, keycode);
        }
        catch (Exception error) { started.TrySetResult(FeatureStatus.No($"The X11 key grab failed: {error.Message}")); }
        finally
        {
            if (keycode != 0) foreach (var m in masks) XUngrabKey(display, keycode, m, root);
            XSync(display, false);
            ForgetErrors(display);
            XCloseDisplay(display);
        }
    }

    private unsafe void Loop(IntPtr display, int keycode)
    {
        var fd = new PollFd { Fd = XConnectionNumber(display), Events = 1 };
        var held = false;
        XEvent e;
        while (!stopping)
        {
            if (XPending(display) == 0) { poll(&fd, 1, 200); continue; }
            XNextEvent(display, &e);
            if (e.Type is not (KeyPress or KeyRelease) || e.Key.Keycode != keycode) continue;
            if (e.Type == KeyPress)
            {
                if (!held) { held = true; Raise(down); }
                continue;
            }
            // Without detectable auto-repeat, repeat arrives as release + press with the same timestamp.
            if (XPending(display) > 0)
            {
                XEvent next;
                XPeekEvent(display, &next);
                if (next.Type == KeyPress && next.Key.Keycode == keycode && next.Key.Time == e.Key.Time)
                {
                    XNextEvent(display, &next);
                    continue;
                }
            }
            if (held) { held = false; Raise(up); }
        }
        if (held) Raise(up);
    }

    private static void Raise(Action action)
    {
        try { action(); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        stopping = true;
        if (Thread.CurrentThread != thread && thread.IsAlive) thread.Join(TimeSpan.FromSeconds(2));
    }
}
