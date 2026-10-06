using Martlet.Companion.Platform;
using Martlet.Platform.Linux.Native;
using static Martlet.Platform.Linux.Native.Xlib;

namespace Martlet.Platform.Linux.Overlay;

/// <summary>Makes the character window an overlay on X11 and XWayland (Avalonia 12 draws through X11 on every Linux
/// desktop). Avalonia already makes it topmost and transparent; this adds what it can't: an XShape input region so clicks
/// pass through everywhere except the character, no input focus (WM_HINTS), no taskbar or pager entry, and sticky on every
/// workspace. Uses its own X connection; window ids are server-wide. Call on the UI thread.</summary>
internal sealed unsafe class X11Overlay(FeatureStatus status, bool xwayland) : ICharacterOverlay, IDisposable
{
    private IntPtr display;

    public FeatureStatus Status { get; } = status;

    private bool Open()
    {
        if (display != IntPtr.Zero) return true;
        if (!OperatingSystem.IsLinux() || !NativeLibraries.HasX11) return false;
        display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero) return false;
        TrackErrors(display);
        return true;
    }

    public FeatureStatus Attach(NativeWindow window, OverlayBehavior behavior)
    {
        if (window.Descriptor != "XID" || window.Handle == 0)
            return FeatureStatus.No($"The character window is a {window.Descriptor} window, not an X11 one; overlay behaviors were not applied.");
        if (!Open()) return FeatureStatus.No("Cannot open the X11 display (no DISPLAY, or libX11 missing).");
        var xid = (nuint)window.Handle;
        if (XGetWindowAttributes(display, xid, out var attributes) == 0)
            return FeatureStatus.No("The character window is not an X11 window this display knows.");
        var mapped = attributes.MapState != IsUnmapped;
        var notes = new List<string>();
        var applied = new List<string>();

        var states = OverlayGeometry.States(behavior);
        if (states.Count > 0)
        {
            SetStates(xid, mapped, states, add: true);
            if (behavior.HasFlag(OverlayBehavior.AlwaysOnTop)) applied.Add("always on top");
        }
        if (behavior.HasFlag(OverlayBehavior.AllSpacesAndFullScreen))
        {
            ulong all = 0xFFFFFFFF;
            if (mapped) SendRootMessage(xid, "_NET_WM_DESKTOP", all, 1);
            else XChangeProperty(display, xid, XInternAtom(display, "_NET_WM_DESKTOP", false), XA_CARDINAL, 32, PropModeReplace, &all, 1);
            applied.Add("all workspaces");
        }
        if (behavior.HasFlag(OverlayBehavior.NonActivating))
        {
            var hints = new XWMHints { Flags = (nint)InputHint, Input = 0 };
            XSetWMHints(display, xid, &hints);
            ulong zero = 0;
            XChangeProperty(display, xid, XInternAtom(display, "_NET_WM_USER_TIME", false), XA_CARDINAL, 32, PropModeReplace, &zero, 1);
            applied.Add("no focus or taskbar entry");
        }
        if (behavior.HasFlag(OverlayBehavior.Transparent))
        {
            var screen = XDefaultScreen(display);
            var compositor = XGetSelectionOwner(display, XInternAtom(display, $"_NET_WM_CM_S{screen}", false)) != 0;
            if (attributes.Depth != 32) notes.Add($"the window has no alpha channel (depth {attributes.Depth}), so its background is not see-through");
            else if (!compositor) notes.Add("no compositor is running, so the transparent background shows black");
            else applied.Add("transparent");
        }
        if (behavior.HasFlag(OverlayBehavior.ClickThroughExceptCharacter))
        {
            if (XShapeQueryExtension(display, out _, out _))
            {
                ApplyInputRegion(xid, []);
                applied.Add("click-through except the character");
            }
            else notes.Add("the X server has no SHAPE extension, so the window can't be click-through");
        }
        XSync(display, false);
        var error = TakeError(display);
        if (error != 0) notes.Add($"the X server refused part of it (X error {error})");
        if (applied.Count == 0) return FeatureStatus.No(notes.Count > 0 ? string.Join("; ", notes) : "No overlay behavior was requested.");
        var mode = xwayland ? "XWayland (full-screen native Wayland games may cover it)" : "X11";
        return FeatureStatus.Yes($"{mode}: {string.Join(", ", applied)}" + (notes.Count > 0 ? "; but " + string.Join("; ", notes) : ""));
    }

    public void SetInteractiveRegions(NativeWindow window, IReadOnlyList<PixelRect> regions)
    {
        if (window.Descriptor != "XID" || window.Handle == 0 || !Open()) return;
        ApplyInputRegion((nuint)window.Handle, regions);
        XFlush(display);
    }

    public void Detach(NativeWindow window)
    {
        if (window.Descriptor != "XID" || window.Handle == 0 || display == IntPtr.Zero) return;
        var xid = (nuint)window.Handle;
        if (XShapeQueryExtension(display, out _, out _)) XShapeCombineMask(display, xid, ShapeInput, 0, 0, 0, ShapeSet);
        if (XGetWindowAttributes(display, xid, out var attributes) != 0)
            SetStates(xid, attributes.MapState != IsUnmapped,
                ["_NET_WM_STATE_STICKY", "_NET_WM_STATE_SKIP_PAGER", "_NET_WM_STATE_SKIP_TASKBAR"], add: false);
        XSync(display, false);
        TakeError(display);
    }

    private void ApplyInputRegion(nuint xid, IReadOnlyList<PixelRect> regions)
    {
        var rectangles = OverlayGeometry.ToXRectangles(regions);
        fixed (XRectangle* pointer = rectangles)
            XShapeCombineRectangles(display, xid, ShapeInput, 0, 0, pointer, rectangles.Length, ShapeSet, Unsorted);
    }

    private void SetStates(nuint xid, bool mapped, IReadOnlyList<string> names, bool add)
    {
        if (mapped)
        {
            // EWMH: a mapped window changes state by asking the window manager, at most two atoms per message.
            for (var i = 0; i < names.Count; i += 2)
            {
                var first = XInternAtom(display, names[i], false);
                var second = i + 1 < names.Count ? XInternAtom(display, names[i + 1], false) : 0;
                SendRootMessage(xid, "_NET_WM_STATE", add ? 1u : 0u, first, second, 1);
            }
            return;
        }
        if (!add) return;
        // Before mapping, the window manager reads the property. Format-32 property data is an array of C longs.
        var atoms = names.Select(n => (ulong)XInternAtom(display, n, false)).ToArray();
        fixed (ulong* pointer = atoms)
            XChangeProperty(display, xid, XInternAtom(display, "_NET_WM_STATE", false), XA_ATOM, 32, PropModeReplace, pointer, atoms.Length);
    }

    private void SendRootMessage(nuint xid, string type, params ulong[] data)
    {
        var e = new XEvent();
        e.Client.Type = ClientMessage;
        e.Client.Window = xid;
        e.Client.MessageType = XInternAtom(display, type, false);
        e.Client.Format = 32;
        for (var i = 0; i < data.Length && i < 5; i++) e.Client.Data[i] = (long)data[i];
        XSendEvent(display, XDefaultRootWindow(display), false, (nint)(SubstructureNotifyMask | SubstructureRedirectMask), &e);
    }

    public void Dispose()
    {
        if (display == IntPtr.Zero) return;
        ForgetErrors(display);
        XCloseDisplay(display);
        display = IntPtr.Zero;
    }
}

internal static class OverlayGeometry
{
    /// <summary>The _NET_WM_STATE atoms a behavior asks for.</summary>
    public static IReadOnlyList<string> States(OverlayBehavior behavior)
    {
        var states = new List<string>();
        if (behavior.HasFlag(OverlayBehavior.AlwaysOnTop)) states.Add("_NET_WM_STATE_ABOVE");
        if (behavior.HasFlag(OverlayBehavior.AllSpacesAndFullScreen)) states.Add("_NET_WM_STATE_STICKY");
        if (behavior.HasFlag(OverlayBehavior.NonActivating)) states.AddRange(["_NET_WM_STATE_SKIP_TASKBAR", "_NET_WM_STATE_SKIP_PAGER"]);
        return states;
    }

    /// <summary>Interactive regions as X rectangles, clipped to X's 16-bit coordinates; empty and negative sizes are dropped.</summary>
    public static XRectangle[] ToXRectangles(IReadOnlyList<PixelRect> regions) =>
        [.. regions.Where(r => r.Width > 0 && r.Height > 0).Select(r => new XRectangle
        {
            X = (short)Math.Clamp(r.X, short.MinValue, short.MaxValue),
            Y = (short)Math.Clamp(r.Y, short.MinValue, short.MaxValue),
            Width = (ushort)Math.Min(r.Width, ushort.MaxValue),
            Height = (ushort)Math.Min(r.Height, ushort.MaxValue)
        })];
}

/// <summary>Used when there is no X11 display: reports why, changes nothing.</summary>
internal sealed class UnavailableOverlay(FeatureStatus status) : ICharacterOverlay
{
    public FeatureStatus Status { get; } = status;
    public FeatureStatus Attach(NativeWindow window, OverlayBehavior behavior) => Status;
    public void SetInteractiveRegions(NativeWindow window, IReadOnlyList<PixelRect> regions) { }
    public void Detach(NativeWindow window) { }
}
