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

/// <summary>How a look was taken: Desktop Duplication (what the monitor shows, full-screen games included) or GDI.
/// <paramref name="Note"/> says why duplication was not used, for the watch status. <paramref name="BehindMartlet"/>: a
/// Martlet window was in front, so the picture is of the window behind it.</summary>
internal sealed record GlanceResult(ScreenFrame? Frame, GlanceSkip Skip, string? Note = null, bool ProtectedContent = false,
    bool BehindMartlet = false);

internal interface IScreenGlancer
{
    GlanceResult Capture(ScreenScope scope);
    TimeSpan UserIdle { get; }
    /// <summary>Frees the screen capture resources (the open duplication and its frame copy) when watching stops.</summary>
    void Release();
}

/// <summary>Captures the foreground window (or its whole monitor), downscaled to at most 1024 px. It reads the monitor
/// through DXGI Desktop Duplication, which also sees full-screen DirectX games, and falls back to GDI when duplication
/// is unavailable (remote sessions, a refused hybrid-GPU laptop, a rotated monitor). No hooking or injection.
/// When a Martlet window is in front, it looks at the window behind it. Martlet's own windows are painted over;
/// minimized windows and password managers / private browsing windows are never captured;
/// protected video and windows that exclude themselves from capture read back black and are skipped.</summary>
internal sealed class ScreenGlancer : IScreenGlancer
{
    internal const int MaximumEdge = 1024;
    private const int SignatureWidth = 16, SignatureHeight = 9;
    private static readonly string[] PrivateTitles =
    [
        "password", "1password", "bitwarden", "keepass", "lastpass", "dashlane", "keeper", "credential manager",
        "inprivate", "incognito", "private browsing", "authenticator", "online banking"
    ];
    private readonly DesktopDuplication duplication = new();
    private byte[]? previous;

    public void Release()
    {
        duplication.Release();
        previous = null;
    }

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
            var own = (uint)Environment.ProcessId;
            GetWindowThreadProcessId(window, out var process);
            // A Martlet window in front (say, the talk window you clicked to read it): look at the window you were using behind it.
            var behind = process == own;
            if (behind && (window = WindowBehind(window, own)) == 0) return new(null, GlanceSkip.MartletInFront);
            if (IsIconic(window)) return new(null, GlanceSkip.Minimized);
            if (Cloaked(window)) return new(null, GlanceSkip.NoWindow);
            var title = Title(window);
            var lower = title.ToLowerInvariant();
            if (PrivateTitles.Any(lower.Contains)) return new(null, GlanceSkip.Private, BehindMartlet: behind);
            var monitorHandle = MonitorFromWindow(window, MonitorDefaultToNearest);
            var monitor = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitorHandle, ref monitor))
                return new(null, GlanceSkip.CaptureFailed);
            var area = monitor.rcMonitor;
            if (scope == ScreenScope.ActiveWindow)
            {
                if (!Bounds(window, out var bounds)) return new(null, GlanceSkip.CaptureFailed);
                area = NativeRect.Intersect(bounds, monitor.rcMonitor);
            }
            if (area.Width < 64 || area.Height < 64) return new(null, GlanceSkip.NoWindow);
            var scale = Math.Min(1.0, (double)MaximumEdge / Math.Max(area.Width, area.Height));
            int width = Math.Max(1, (int)Math.Round(area.Width * scale)), height = Math.Max(1, (int)Math.Round(area.Height * scale));
            // Duplication first: it sees full-screen games and does not stall the game the way a GDI screen read can.
            var pixels = duplication.Grab(monitorHandle, area, width, height);
            var protectedContent = pixels is not null && duplication.ProtectedContent;
            var note = pixels is null ? duplication.Problem : null;
            var signature = pixels is null ? null : Signature(pixels, width, height);
            if (pixels is null || signature is null || signature.Max() < 10)
            {
                // A still desktop can briefly read black from a fresh duplication; GDI decides then.
                if (pixels is not null) Array.Clear(pixels);
                pixels = Grab(area, width, height);
                if (pixels is null) return new(null, GlanceSkip.CaptureFailed, note);
                signature = Signature(pixels, width, height);
            }
            if (signature.Max() < 10)
            {
                Array.Clear(pixels);
                return new(null, GlanceSkip.Blank, note, protectedContent, behind);
            }
            // Martlet's own windows never leave this PC: they are painted over, and a picture that is nearly all Martlet is skipped.
            if (BlankOwnWindows(pixels, width, height, area, window, own) >= 0.9)
            {
                Array.Clear(pixels);
                return new(null, GlanceSkip.MartletInFront, note, BehindMartlet: behind);
            }
            signature = Signature(pixels, width, height);
            var change = previous is null ? 1.0 : signature.Zip(previous, (a, b) => Math.Abs(a - b)).Average() / 255.0;
            previous = signature;
            return new(new(pixels, width, height, title.Length > 80 ? title[..80] : title, change), GlanceSkip.None, note, protectedContent, behind);
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

    // The first ordinary window below Martlet's in the z-order: the one you were using before you clicked Martlet.
    private static nint WindowBehind(nint start, uint own)
    {
        var shell = GetShellWindow();
        var window = start;
        for (int i = 0; i < 1024 && (window = GetWindow(window, GwHwndNext)) != 0; i++)
        {
            if (window == shell || !IsWindowVisible(window) || IsIconic(window) || Cloaked(window)) continue;
            GetWindowThreadProcessId(window, out var process);
            if (process == own || (GetWindowLongPtrW(window, GwlExStyle) & (ExToolWindow | ExNoActivate | ExTransparent)) != 0) continue;
            if (!Bounds(window, out var bounds) || bounds.Width < 64 || bounds.Height < 64) continue;
            if (ClassName(window) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") continue;
            return window;
        }
        return 0;
    }

    /// <summary>Paints Martlet's own visible windows in <paramref name="area"/> neutral grey and returns the share of the
    /// picture they covered. Those above <paramref name="target"/> in the z-order cover it; those below it can only show
    /// beside it (the rest of the monitor). Click-through overlays are left alone: the subtitles keep themselves out of
    /// capture, and painting a transparent overlay's box would hide what is under it.</summary>
    private static double BlankOwnWindows(byte[] pixels, int width, int height, NativeRect area, nint target, uint own)
    {
        double scaleX = (double)width / area.Width, scaleY = (double)height / area.Height;
        (int Left, int Top, int Right, int Bottom) Map(NativeRect rect) => (
            Math.Clamp((int)Math.Floor((rect.Left - area.Left) * scaleX), 0, width),
            Math.Clamp((int)Math.Floor((rect.Top - area.Top) * scaleY), 0, height),
            Math.Clamp((int)Math.Ceiling((rect.Right - area.Left) * scaleX), 0, width),
            Math.Clamp((int)Math.Ceiling((rect.Bottom - area.Top) * scaleY), 0, height));
        var front = Bounds(target, out var targetBounds) ? Map(NativeRect.Intersect(targetBounds, area)) : (0, 0, 0, 0);
        bool[]? covered = null;
        var count = 0;
        var above = true;
        var window = GetTopWindow(0);
        for (int i = 0; window != 0 && i < 4096; i++, window = GetWindow(window, GwHwndNext))
        {
            if (window == target) { above = false; continue; }
            GetWindowThreadProcessId(window, out var process);
            if (process != own || !IsWindowVisible(window) || IsIconic(window) || Cloaked(window) ||
                (GetWindowLongPtrW(window, GwlExStyle) & ExTransparent) != 0 || !Bounds(window, out var bounds)) continue;
            var (left, top, right, bottom) = Map(NativeRect.Intersect(bounds, area));
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                if (!above && x >= front.Item1 && x < front.Item3 && y >= front.Item2 && y < front.Item4) continue;
                var index = y * width + x;
                covered ??= new bool[width * height];
                if (covered[index]) continue;
                covered[index] = true;
                count++;
                pixels[index * 4] = pixels[index * 4 + 1] = pixels[index * 4 + 2] = 0x30;
            }
        }
        return count / (double)(width * height);
    }

    private static bool Cloaked(nint window) => DwmGetWindowAttribute(window, DwmCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static bool Bounds(nint window, out NativeRect bounds) =>
        DwmGetWindowAttribute(window, DwmExtendedFrameBounds, out bounds, Marshal.SizeOf<NativeRect>()) == 0 || GetWindowRect(window, out bounds);

    private static string ClassName(nint window)
    {
        var buffer = new StringBuilder(64);
        return GetClassName(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    private static byte[]? Grab(NativeRect area, int width, int height)
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
    private const uint MonitorDefaultToNearest = 2, GwHwndNext = 2;
    private const int DwmExtendedFrameBounds = 9, DwmCloaked = 14, Halftone = 4, GwlExStyle = -20;
    private const long ExTransparent = 0x20, ExToolWindow = 0x80, ExNoActivate = 0x08000000;
    private const uint SourceCopy = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public uint cbSize; public NativeRect rcMonitor, rcWork; public uint dwFlags; }

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
    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern long GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out NativeRect value, int size);
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
