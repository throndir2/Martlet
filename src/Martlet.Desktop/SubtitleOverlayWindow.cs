using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Martlet.Desktop;

// A click-through, never-activated, topmost caption strip at the bottom of the monitor showing the foreground window.
// Same always-on-top layered window the character overlay uses, so it shows over full-screen games wherever the
// character does; it also re-raises itself to the top of the topmost band on every line.
internal sealed class SubtitleOverlayWindow : Window
{
    private const int ExtendedStyle = -20;
    private const long Transparent = 0x20, ToolWindow = 0x80, Layered = 0x80000, NoActivate = 0x08000000;
    private const uint NoActivateFlag = 0x0010;
    private static readonly IntPtr TopMost = new(-1);
    private readonly TextBlock text = new()
    {
        Foreground = Brushes.White, FontSize = 28, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center
    };
    private readonly Border panel;
    private IntPtr handle;

    internal SubtitleOverlayWindow()
    {
        Title = "Martlet subtitles";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        text.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 1, Color = Colors.Black };
        panel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB4, 0x10, 0x0A, 0x10)), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 8, 18, 10), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom, Child = text
        };
        Content = panel;
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtrW(handle, ExtendedStyle,
                new IntPtr(GetWindowLongPtrW(handle, ExtendedStyle).ToInt64() | Transparent | ToolWindow | Layered | NoActivate));
            // Keep Martlet's own captions out of screenshots, so screen commentary never reads them back.
            SetWindowDisplayAffinity(handle, 0x11);
        };
    }

    internal void ShowLine(string line)
    {
        text.Text = line.Length > 400 ? line[..400] + "…" : line;
        if (handle == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
        var foreground = GetForegroundWindow();
        var monitor = MonitorFromWindow(foreground == IntPtr.Zero ? handle : foreground, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfoW(monitor, ref info)) return;
        var bounds = info.Monitor;
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        var strip = height / 3;
        var dpi = VisualTreeHelper.GetDpi(this);
        text.MaxWidth = width * 0.7 / dpi.DpiScaleX;
        if (!IsVisible) Show();
        SetWindowPos(handle, TopMost, bounds.Left, bounds.Bottom - strip - height / 14, width, strip, NoActivateFlag);
    }

    internal void ClearLine()
    {
        text.Text = "";
        if (IsVisible) Hide();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
