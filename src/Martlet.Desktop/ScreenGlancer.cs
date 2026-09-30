using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Martlet.Providers;

namespace Martlet.Desktop;

internal enum ScreenScope { ActiveWindow, ActiveScreen }

internal enum GlanceSkip { None, MartletInFront, NoWindow, Minimized, Private, Blank, CaptureFailed }

/// <summary>One downscaled look at what the user has in front of them. Pixels live only in memory (BGRA32, top-down)
/// and are zeroed by <see cref="Clear"/>; nothing is written to disk or logs.</summary>
internal sealed class ScreenFrame(byte[] pixels, int width, int height, string title, double change)
{
    private byte[]? pixels = pixels;
    internal int Width { get; } = width;
    internal int Height { get; } = height;
    internal string Title { get; } = title;
    /// <summary>How much the picture changed since the previous capture, 0 (identical) to 1.</summary>
    internal double Change { get; } = change;

    /// <summary>JPEG-encodes the frame for one look. Call on the UI thread (WPF imaging).</summary>
    internal BoundedImage Encode()
    {
        var source = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null,
            pixels ?? throw new ObjectDisposedException(nameof(ScreenFrame)), Width * 4);
        foreach (var quality in new[] { 72, 55, 40 })
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            try
            {
                if (stream.Length <= BoundedImage.HardMaxBytes)
                    return new(stream.GetBuffer().AsSpan(0, (int)stream.Length), ImageMediaType.Jpeg, Width, Height);
            }
            finally { Array.Clear(stream.GetBuffer()); }
        }
        throw new InvalidOperationException("The screen image could not be made small enough.");
    }

    internal void Clear()
    {
        if (Interlocked.Exchange(ref pixels, null) is { } owned) Array.Clear(owned);
    }
}

internal sealed record GlanceResult(ScreenFrame? Frame, GlanceSkip Skip);

internal interface IScreenGlancer
{
    GlanceResult Capture(ScreenScope scope);
    TimeSpan UserIdle { get; }
}

/// <summary>Captures the foreground window (or its whole monitor) with GDI, downscaled to at most 1024 px. Works for
/// windowed and borderless games; exclusive full-screen and DRM-protected content read back black and are skipped.
/// Martlet's own windows, minimized windows and password managers / private browsing windows are never captured.</summary>
internal sealed class ScreenGlancer : IScreenGlancer
{
    internal const int MaximumEdge = 1024;
    private const int SignatureWidth = 16, SignatureHeight = 9;
    private static readonly string[] PrivateTitles =
    [
        "password", "1password", "bitwarden", "keepass", "lastpass", "dashlane", "keeper", "credential manager",
        "inprivate", "incognito", "private browsing", "authenticator", "online banking"
    ];
    private byte[]? previous;

    public TimeSpan UserIdle
    {
        get
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime)) : TimeSpan.Zero;
        }
    }

    public GlanceResult Capture(ScreenScope scope)
    {
        var old = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            var window = GetForegroundWindow();
            if (window == 0 || window == GetShellWindow()) return new(null, GlanceSkip.NoWindow);
            GetWindowThreadProcessId(window, out var process);
            if (process == (uint)Environment.ProcessId) return new(null, GlanceSkip.MartletInFront);
            if (IsIconic(window)) return new(null, GlanceSkip.Minimized);
            if (DwmGetWindowAttribute(window, DwmCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return new(null, GlanceSkip.NoWindow);
            var title = Title(window);
            var lower = title.ToLowerInvariant();
            if (PrivateTitles.Any(lower.Contains)) return new(null, GlanceSkip.Private);
            var monitor = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(MonitorFromWindow(window, MonitorDefaultToNearest), ref monitor))
                return new(null, GlanceSkip.CaptureFailed);
            var area = monitor.rcMonitor;
            if (scope == ScreenScope.ActiveWindow)
            {
                if (DwmGetWindowAttribute(window, DwmExtendedFrameBounds, out RECT bounds, Marshal.SizeOf<RECT>()) != 0 &&
                    !GetWindowRect(window, out bounds)) return new(null, GlanceSkip.CaptureFailed);
                area = RECT.Intersect(bounds, monitor.rcMonitor);
            }
            if (area.Width < 64 || area.Height < 64) return new(null, GlanceSkip.NoWindow);
            var scale = Math.Min(1.0, (double)MaximumEdge / Math.Max(area.Width, area.Height));
            int width = Math.Max(1, (int)Math.Round(area.Width * scale)), height = Math.Max(1, (int)Math.Round(area.Height * scale));
            var pixels = Grab(area, width, height);
            if (pixels is null) return new(null, GlanceSkip.CaptureFailed);
            var signature = Signature(pixels, width, height);
            if (signature.Max() < 10)
            {
                Array.Clear(pixels);
                return new(null, GlanceSkip.Blank);
            }
            var change = previous is null ? 1.0 : signature.Zip(previous, (a, b) => Math.Abs(a - b)).Average() / 255.0;
            previous = signature;
            return new(new(pixels, width, height, title.Length > 80 ? title[..80] : title, change), GlanceSkip.None);
        }
        catch (Exception error) when (error is ExternalException or OutOfMemoryException or ArgumentException)
        {
            return new(null, GlanceSkip.CaptureFailed);
        }
        finally
        {
            if (old != 0) SetThreadDpiAwarenessContext(old);
        }
    }

    private static byte[]? Grab(RECT area, int width, int height)
    {
        var screen = GetDC(0);
        if (screen == 0) return null;
        nint memory = 0, bitmap = 0, previousObject = 0;
        try
        {
            memory = CreateCompatibleDC(screen);
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32
            };
            bitmap = CreateDIBSection(screen, ref header, 0, out var bits, 0, 0);
            if (memory == 0 || bitmap == 0 || bits == 0) return null;
            previousObject = SelectObject(memory, bitmap);
            SetStretchBltMode(memory, Halftone);
            SetBrushOrgEx(memory, 0, 0, 0);
            if (!StretchBlt(memory, 0, 0, width, height, screen, area.Left, area.Top, area.Width, area.Height, SourceCopy))
                return null;
            GdiFlush();
            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            if (previousObject != 0) SelectObject(memory, previousObject);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    // A 16x9 grey thumbnail: enough to notice a scene change, far too small to carry content.
    internal static byte[] Signature(byte[] pixels, int width, int height)
    {
        var result = new byte[SignatureWidth * SignatureHeight];
        for (int cellY = 0; cellY < SignatureHeight; cellY++)
        for (int cellX = 0; cellX < SignatureWidth; cellX++)
        {
            int x0 = cellX * width / SignatureWidth, x1 = Math.Max(x0 + 1, (cellX + 1) * width / SignatureWidth);
            int y0 = cellY * height / SignatureHeight, y1 = Math.Max(y0 + 1, (cellY + 1) * height / SignatureHeight);
            long sum = 0, count = 0;
            for (int y = y0; y < y1 && y < height; y += 2)
            for (int x = x0; x < x1 && x < width; x += 2)
            {
                var i = (y * width + x) * 4;
                sum += (pixels[i] * 29 + pixels[i + 1] * 150 + pixels[i + 2] * 77) >> 8;
                count++;
            }
            result[cellY * SignatureWidth + cellX] = (byte)(count == 0 ? 0 : sum / count);
        }
        return result;
    }

    private static string Title(nint window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0) return "";
        var buffer = new StringBuilder(Math.Min(length, 512) + 1);
        GetWindowText(window, buffer, buffer.Capacity);
        return new string(buffer.ToString().Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
    }

    private static readonly nint PerMonitorAwareV2 = -4;
    private const uint MonitorDefaultToNearest = 2;
    private const int DwmExtendedFrameBounds = 9, DwmCloaked = 14, Halftone = 4;
    private const uint SourceCopy = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public static RECT Intersect(RECT a, RECT b) => new()
        {
            Left = Math.Max(a.Left, b.Left), Top = Math.Max(a.Top, b.Top),
            Right = Math.Min(a.Right, b.Right), Bottom = Math.Min(a.Bottom, b.Bottom)
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out RECT value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BITMAPINFOHEADER header, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetBrushOrgEx(nint dc, int x, int y, nint previous);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StretchBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
}
