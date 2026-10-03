using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Martlet.Desktop;

/// <summary>Keeps Martlet's windows on screen at any display scale. A window opens centered over the window it belongs to (or
/// Martlet's main window), no larger than the work area of that window's monitor and wholly inside it, title bar first; its
/// minimum size shrinks to fit a small screen, also after it is moved to another monitor. A dialog that sizes to its content
/// scrolls instead of growing past the screen, stays on screen as it grows, and is resizable without minimize and maximize,
/// like Windows' own resizable dialogs.</summary>
internal static class ScreenFit
{
    /// <summary>A display scale in percent (for example 200) to fit windows as if Windows used it, for checking through MCP on a
    /// low-scale screen: the usable area shrinks from the work area's top-left corner as that scale would shrink it.</summary>
    internal const string SimulatedScaleVariable = "MARTLET_SIMULATE_DISPLAY_SCALE";

    private static readonly double SimulatedScale =
        double.TryParse(Environment.GetEnvironmentVariable(SimulatedScaleVariable), NumberStyles.Float, CultureInfo.InvariantCulture,
            out var percent) && percent is >= 100 and <= 500 ? percent / 100 : 0;

    /// <summary>Fits <paramref name="window"/> once its native window exists and before it shows (OnSourceInitialized).</summary>
    internal static void Attach(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != 0) new Fitter(window, handle).Open();
    }

    private sealed class Fitter(Window window, nint handle)
    {
        private readonly double minWidth = window.MinWidth, minHeight = window.MinHeight;
        private readonly double maxWidth = window.MaxWidth, maxHeight = window.MaxHeight;
        // Dialogs keep their screen-sized maximum; a main window's would stop maximize covering the screen.
        private readonly bool dialog = window.SizeToContent != SizeToContent.Manual;
        private nint monitor;
        private bool moving, keepPending;

        internal void Open()
        {
            if (dialog)
            {
                ScrollWhenTall(window);
                if (window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip) DialogFrame(handle);
            }
            Place();
            window.LocationChanged += (_, _) => FollowMonitor();
            window.StateChanged += (_, _) =>
            {
                if (window.WindowState != WindowState.Normal) return;
                Limit(MonitorFromWindow(handle, MonitorDefaultToNearest), shrink: true);
                KeepInside();
            };
            // A dialog growing with its content (until it is resized by hand) stays on screen. WPF resizes its native window
            // only after layout, so this waits for that.
            window.SizeChanged += (_, _) => { if (window.SizeToContent != SizeToContent.Manual) KeepInsideSoon(); };
        }

        private void Place()
        {
            var anchor = Anchor(window);
            var target = MonitorFromWindow(anchor != 0 ? anchor : handle, MonitorDefaultToNearest);
            if (!Limit(target, shrink: true)) return;
            window.UpdateLayout();
            if (IsIconic(handle) || IsZoomed(handle) || !GetWindowRect(handle, out var rect) || !WorkArea(target, out var work, out _))
                return;
            // A resize WPF hasn't applied yet still ends within the work area.
            var width = Math.Min(rect.Width, work.Width);
            var height = Math.Min(rect.Height, work.Height);
            var (x, y) = anchor != 0 && window.WindowStartupLocation != WindowStartupLocation.Manual && GetWindowRect(anchor, out var over)
                ? (over.Left + over.Width / 2, over.Top + over.Height / 2)
                : (rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
            MoveInside(work, x - width / 2, y - height / 2, width, height, rect);
            // Arriving on a monitor with another scale can rescale the window, and content can still change its size as it shows.
            KeepInside();
            KeepInsideSoon();
        }

        /// <summary>Caps the minimum (and a dialog's maximum) size at the monitor's work area; <paramref name="shrink"/> also
        /// shrinks a window that is larger.</summary>
        private bool Limit(nint target, bool shrink)
        {
            if (!WorkArea(target, out var work, out var pixelsPerDip)) return false;
            monitor = target;
            var width = work.Width / pixelsPerDip;
            var height = work.Height / pixelsPerDip;
            window.MinWidth = Math.Min(minWidth, width);
            window.MinHeight = Math.Min(minHeight, height);
            if (dialog)
            {
                window.MaxWidth = Math.Min(maxWidth, width);
                window.MaxHeight = Math.Min(maxHeight, height);
            }
            if (shrink && !IsIconic(handle) && !IsZoomed(handle) && GetWindowRect(handle, out var rect))
            {
                if (rect.Width > work.Width && window.SizeToContent is SizeToContent.Manual or SizeToContent.Height) window.Width = width;
                if (rect.Height > work.Height && window.SizeToContent is SizeToContent.Manual or SizeToContent.Width) window.Height = height;
            }
            return true;
        }

        private void FollowMonitor()
        {
            if (moving) return;
            var current = MonitorFromWindow(handle, MonitorDefaultToNearest);
            if (current != monitor) Limit(current, shrink: false);
        }

        private void KeepInside()
        {
            if (moving || IsIconic(handle) || IsZoomed(handle) || !GetWindowRect(handle, out var rect) ||
                !WorkArea(MonitorFromWindow(handle, MonitorDefaultToNearest), out var work, out _))
                return;
            MoveInside(work, rect.Left, rect.Top, rect.Width, rect.Height, rect);
        }

        private void KeepInsideSoon()
        {
            if (keepPending) return;
            keepPending = true;
            window.Dispatcher.InvokeAsync(() =>
            {
                keepPending = false;
                if (PresentationSource.FromVisual(window) is not null) KeepInside();
            }, DispatcherPriority.Loaded);
        }

        private void MoveInside(NativeRect work, int x, int y, int width, int height, NativeRect current)
        {
            // Left and top win when the window is still larger, so its title bar stays reachable.
            x = Math.Max(work.Left, Math.Min(x, work.Right - width));
            y = Math.Max(work.Top, Math.Min(y, work.Bottom - height));
            if (x == current.Left && y == current.Top) return;
            moving = true;
            try { SetWindowPos(handle, 0, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder); }
            finally { moving = false; }
        }
    }

    /// <summary>The visible window to center over: the owner, else Martlet's main window for a window of its own.</summary>
    private static nint Anchor(Window window)
    {
        var owner = new WindowInteropHelper(window).Owner;
        if (owner != 0) return Showing(owner) ? owner : 0;
        return Application.Current?.MainWindow is { } main && !ReferenceEquals(main, window) &&
            new WindowInteropHelper(main).Handle is var handle && Showing(handle) ? handle : 0;
    }

    private static bool Showing(nint handle) => handle != 0 && IsWindowVisible(handle) && !IsIconic(handle);

    /// <summary>The monitor's work area in screen pixels, shrunk for <see cref="SimulatedScaleVariable"/>, and the pixels per
    /// device-independent unit that Martlet's windows use on it.</summary>
    private static bool WorkArea(nint target, out NativeRect work, out double pixelsPerDip)
    {
        work = default;
        pixelsPerDip = 1;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (target == 0 || !GetMonitorInfo(target, ref info) || info.Work.Width <= 0 || info.Work.Height <= 0) return false;
        work = info.Work;
        pixelsPerDip = GetDpiForMonitor(target, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : SystemDpi();
        if (SimulatedScale > pixelsPerDip)
        {
            work.Right = work.Left + (int)(work.Width * pixelsPerDip / SimulatedScale);
            work.Bottom = work.Top + (int)(work.Height * pixelsPerDip / SimulatedScale);
        }
        return true;
    }

    private static double SystemDpi() =>
        Application.Current?.MainWindow is { } main ? VisualTreeHelper.GetDpi(main).PixelsPerDip : 1;

    /// <summary>Puts a dialog's content in a scroller, so all of it stays reachable when the screen is shorter than it.</summary>
    private static void ScrollWhenTall(Window window)
    {
        if (window.Content is not UIElement content || content is ScrollViewer) return;
        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            IsTabStop = false
        };
        window.Content = scroller;
        scroller.Content = content;
    }

    /// <summary>A resizable frame with only Close, like Windows' resizable dialogs: minimizing a modal prompt would leave its
    /// owner blocked behind it.</summary>
    private static void DialogFrame(nint handle)
    {
        const int Style = -16;
        const nint MinimizeBox = 0x20000, MaximizeBox = 0x10000;
        SetWindowLongPtrW(handle, Style, GetWindowLongPtrW(handle, Style) & ~(MinimizeBox | MaximizeBox));
        SetWindowPos(handle, 0, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpFrameChanged);
    }

    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoZOrder = 0x4, SwpNoActivate = 0x10, SwpFrameChanged = 0x20,
        SwpNoOwnerZOrder = 0x200;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
