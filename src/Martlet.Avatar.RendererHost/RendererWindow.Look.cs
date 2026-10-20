using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Martlet.Avatar.Hosting;
using Martlet.Avatar.RendererHost.Logging;
using Martlet.Presentation;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Martlet.Avatar.RendererHost;

/// <summary>Where the character looks: the mouse, the user's window, a point Martlet asked for, and the Windows calls behind it.</summary>
internal sealed partial class RendererWindow
{
    // The character's head and eyes follow its usual gaze (the mouse, the mouse when it's near, straight ahead or where the user
    // works in the window they are using), or for a while a point on the desktop Martlet asked it to look at, or the mouse after
    // a touch ("gaze"); messages to the browser are fire-and-forget and never replied to.
    private void StartLookTracking()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            if (closed || failure.Failed || browser.CoreWebView2 is null || !Look()) timer.Stop();
        };
        Closed += (_, _) => timer.Stop();
        timer.Start();
    }

    // Where Martlet asked the character to look (physical screen pixels, like its screenshots) and until when; until when a touch
    // keeps its eyes on the mouse; its usual gaze and what the Eyes menu shows checked; the direction the browser was last given
    // (+x right, +y up, -1 to 1), what it was toward and the point there; and the window the user was last using and where they
    // work in it (for GazeMode.Window).
    private Point? gazePoint;
    private long gazeUntil, attendUntil;
    private GazeMode gazeMode = GazeMode.Mouse;
    private string gazeChoice = RendererGaze.Personality;
    private bool gazeFree = true;
    private double lookX = double.NaN, lookY = double.NaN;
    private string lookTarget = "mouse";
    private ScreenPoint? lookAt;
    private IntPtr userHandle;
    private ScreenRect? userWindow;
    private readonly WindowWatch windowWatch = new();

    /// <summary>Turns the head and eyes toward what the gaze says now. False once the browser can't take messages any more.</summary>
    private bool Look(bool always = false)
    {
        if (LookDirection() is not { } look) return true;
        if (!always && Math.Abs(look.X - lookX) < 0.01 && Math.Abs(look.Y - lookY) < 0.01) return true;
        lookX = look.X;
        lookY = look.Y;
        if (browser.CoreWebView2 is null || failure.Failed) return true;
        try
        {
            browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "look", data = new { x = look.X, y = look.Y } },
                RendererProtocol.Json));
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { return false; }
    }

    private (double X, double Y)? LookDirection()
    {
        Point face, frameTopLeft, frameBottomRight;
        // The face sits at 30% height when unzoomed; follow it through the camera zoom, within the character's frame.
        var faceX = (viewX + 1) / 2;
        var faceY = (1 - (0.4 * viewZoom + viewY)) / 2;
        var frameLeft = FrameOffset(viewport.ActualWidth);
        var frameWidth = viewport.ActualWidth * FrameFraction;
        try
        {
            face = viewport.PointToScreen(new Point(frameLeft + frameWidth * faceX, viewport.ActualHeight * faceY));
            frameTopLeft = viewport.PointToScreen(new Point(frameLeft, 0));
            frameBottomRight = viewport.PointToScreen(new Point(frameLeft + frameWidth, viewport.ActualHeight));
        }
        catch (InvalidOperationException) { return null; }
        var now = Environment.TickCount64;
        if (gazePoint is not null && now >= gazeUntil) gazePoint = null;
        ScreenPoint? mouse = GetCursorPos(out var cursor) ? new ScreenPoint(cursor.X, cursor.Y) : null;
        var frame = new ScreenRect((int)Math.Round(frameTopLeft.X), (int)Math.Round(frameTopLeft.Y),
            (int)Math.Round(frameBottomRight.X - frameTopLeft.X), (int)Math.Round(frameBottomRight.Y - frameTopLeft.Y));
        // The window the user is using is kept track of in every gaze, so watching it starts at once (also from the Eyes menu,
        // which is in front then), with where they last pointed or typed in it.
        var (handle, used, front) = UserWindow();
        windowWatch.Update(handle, mouse, front && mouse is { } pointer && Under(pointer) == handle, front ? TextCursor(handle) : null);
        var (target, at) = CharacterGaze.Aim(gazeMode, gazePoint is { } point ? new ScreenPoint(point.X, point.Y) : null, now < attendUntil,
            mouse, frame, used, windowWatch.Working);
        lookTarget = target;
        lookAt = at;
        if (target == "ahead") return (0, 0);
        if (at is not { } aim) return null;
        // Martlet's points are physical pixels; the mouse and windows are read in this process's own coordinates, like the face.
        if (target == "point") face = Physical(face);
        return (Math.Clamp((aim.X - face.X) / 700, -1, 1), Math.Clamp((face.Y - aim.Y) / 700, -1, 1));
    }

    /// <summary>The window the user is using and whether it is in front now: the one in front, unless it is one of the character's
    /// own (its menus; then the one before stays, not in front), minimized, or the desktop or taskbar (then none). Its handle is
    /// zero and its place null when none is known.</summary>
    private (IntPtr Handle, ScreenRect? Area, bool Front) UserWindow()
    {
        var front = GetForegroundWindow();
        if (front == IntPtr.Zero || GetWindowThreadProcessId(front, out var owner) == 0 || owner == (uint)Environment.ProcessId)
            return (userHandle, userWindow, false);
        var name = new StringBuilder(64);
        var shell = GetClassName(front, name, name.Capacity) > 0 &&
            name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
        var rect = default(NativeRect);
        var usable = !shell && !IsIconic(front) && GetWindowRect(front, out rect) && rect.Right > rect.Left && rect.Bottom > rect.Top;
        userHandle = usable ? front : IntPtr.Zero;
        userWindow = usable ? new ScreenRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top) : null;
        return (userHandle, userWindow, usable);
    }

    /// <summary>The top-level window under a screen point: not the character where it is drawn (its overlay is), but the window
    /// behind where the overlay is clear.</summary>
    private static IntPtr Under(ScreenPoint point)
    {
        var under = WindowFromPoint(new CursorPoint { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) });
        return under == IntPtr.Zero ? IntPtr.Zero : GetAncestor(under, RootAncestor);
    }

    /// <summary>Where the text cursor (caret) of <paramref name="window"/> is on the screen, or null when it shows none: classic
    /// Windows programs such as Notepad show Windows' own, while browsers and many other apps draw theirs.</summary>
    private static ScreenPoint? TextCursor(IntPtr window)
    {
        var thread = GetWindowThreadProcessId(window, out _);
        var info = new GuiThreadInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<GuiThreadInfo>() };
        if (thread == 0 || !GetGUIThreadInfo(thread, ref info) || info.Caret == IntPtr.Zero || info.CaretRect.Bottom <= info.CaretRect.Top ||
            !IsWindowVisible(info.Caret) || GetAncestor(info.Caret, RootAncestor) != window) return null;
        var middle = new CursorPoint { X = (info.CaretRect.Left + info.CaretRect.Right) / 2, Y = (info.CaretRect.Top + info.CaretRect.Bottom) / 2 };
        return ClientToScreen(info.Caret, ref middle) ? new ScreenPoint(middle.X, middle.Y) : null;
    }

    /// <summary>A screen point as this window's process sees it, in physical pixels (unchanged where Windows doesn't scale it).</summary>
    private Point Physical(Point point)
    {
        var native = new CursorPoint { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) };
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        return handle != IntPtr.Zero && LogicalToPhysicalPointForPerMonitorDPI(handle, ref native) ? new(native.X, native.Y) : point;
    }

    private int lookReadings;

    /// <summary>Where the character looks now (as of its last look, at most 50 ms ago), for Martlet's MCP character_look through
    /// UI Automation: what the eyes are on ("mouse", "window", "point" or "ahead"), the direction the browser was last given
    /// (-1 to 1, +x right, +y up), the point on the desktop, the usual gaze, and the window the user is using (where it is, never
    /// its title) and what the eyes watch in it (the pointer, the text cursor or its middle). It changes nothing.</summary>
    private void ReadLook()
    {
        var used = userWindow;
        viewport.LastLook = JsonSerializer.Serialize(new
        {
            n = ++lookReadings,
            target = lookTarget,
            x = double.IsFinite(lookX) ? Math.Round(lookX, 3) : 0,
            y = double.IsFinite(lookY) ? Math.Round(lookY, 3) : 0,
            at = lookAt is { } point ? new { x = Math.Round(point.X), y = Math.Round(point.Y) } : null,
            usual = CharacterGaze.Word(gazeMode),
            window = used is { } area ? new { left = area.Left, top = area.Top, width = area.Width, height = area.Height } : null,
            watching = lookTarget == "window" ? windowWatch.Watching(used) : null
        });
    }

    /// <summary>Sets the usual gaze and the Eyes menu's choices (when given), then looks at the asked-for point for a while, or at
    /// the mouse for a while, or (with neither) the usual way at once; says what the character looks at.</summary>
    internal RendererLook Gaze(RendererGaze gaze)
    {
        if ((gaze.Mode is { } mode && !Enum.IsDefined(mode)) || (gaze.Choice is { } choice && !RendererGaze.Choices.Contains(choice)) ||
            !double.IsFinite(gaze.Seconds) || (gaze.X is null) != (gaze.Y is null) || (gaze.Mouse && gaze.X is not null))
            throw new InvalidDataException("Gaze is invalid.");
        var seconds = (long)(Math.Clamp(gaze.Seconds, RendererGaze.MinimumSeconds, RendererGaze.MaximumSeconds) * 1000);
        if (gaze.X is { } x && gaze.Y is { } y)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) >= 1_000_000 || Math.Abs(y) >= 1_000_000)
                throw new InvalidDataException("Gaze is invalid.");
            gazePoint = new(x, y);
            gazeUntil = Environment.TickCount64 + seconds;
        }
        else
        {
            gazePoint = null;
            attendUntil = gaze.Mouse ? Environment.TickCount64 + seconds : 0;
        }
        if (gaze.Mode is { } usual) gazeMode = usual;
        if (gaze.Choice is { } chosen) gazeChoice = chosen;
        if (gaze.Free is { } free) gazeFree = free;
        Look(always: true);
        return new(lookTarget, double.IsFinite(lookX) ? Math.Round(lookX, 3) : 0, double.IsFinite(lookY) ? Math.Round(lookY, 3) : 0, gazeMode);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

    private const uint RootAncestor = 2;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(CursorPoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref CursorPoint point);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public NativeRect CaretRect;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CursorPoint { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool LogicalToPhysicalPointForPerMonitorDPI(IntPtr window, ref CursorPoint point);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(CursorPoint point, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoEx(IntPtr monitor, ref MonitorInfoEx info);

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, IntPtr bounds, IntPtr data);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    /// <summary>Top of the work area of the screen the overlay is on, in this window's coordinates.</summary>
    private double? WorkAreaTop() => WorkArea()?.Top;

    /// <summary>Work area of the screen the overlay is on, in this window's coordinates.</summary>
    private Rect? WorkArea()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || PresentationSource.FromVisual(this)?.CompositionTarget is not { } target) return null;
        var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        var monitor = MonitorFromWindow(handle, 2);
        if (monitor == IntPtr.Zero || !GetMonitorInfoW(monitor, ref info)) return null;
        return new Rect(target.TransformFromDevice.Transform(new Point(info.Work.Left, info.Work.Top)),
            target.TransformFromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom)));
    }
}
