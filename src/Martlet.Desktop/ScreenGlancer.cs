using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Martlet.Avatar.Hosting;
using Martlet.Providers;

namespace Martlet.Desktop;

internal enum ScreenScope { ActiveWindow, ActiveScreen }

internal enum GlanceSkip { None, MartletInFront, NoWindow, Minimized, Private, Blank, CaptureFailed }

/// <summary>One downscaled look at what the user has in front of them. Pixels live only in memory (BGRA32, top-down)
/// and are zeroed by <see cref="Clear"/>; nothing is written to disk or logs. A screenshot also knows where on the desktop it
/// was taken (<see cref="Area"/>, physical pixels) and how much each cell of a coarse grey grid changed since the screenshot
/// before of the same area (<see cref="Changes"/>, for where the character looks). A screenshot of the screen also knows the
/// program in front (<see cref="App"/>) and whether its window fills its monitor (<see cref="FullScreen"/>).</summary>
internal sealed class ScreenFrame(byte[] pixels, int width, int height, string title, double change, ScreenRect? area = null,
    byte[]? changes = null, string app = "", bool fullScreen = false)
{
    private byte[]? pixels = pixels;
    internal int Width { get; } = width;
    internal int Height { get; } = height;
    internal string Title { get; } = title;
    /// <summary>The program the active window belongs to, by name (<see cref="ActiveApp"/>); empty for a camera, or when
    /// Windows doesn't say.</summary>
    internal string App { get; } = app;
    /// <summary>The active window fills its monitor: a full-screen game (borderless too), video or slide show.</summary>
    internal bool FullScreen { get; } = fullScreen;
    /// <summary>How much the picture changed since the previous capture, 0 (identical) to 1.</summary>
    internal double Change { get; } = change;
    /// <summary>Where on the desktop the screenshot was taken; null for a camera.</summary>
    internal ScreenRect? Area { get; } = area;
    /// <summary>How much each <see cref="CharacterGaze"/> grid cell changed; null without an earlier screenshot of the same area.</summary>
    internal byte[]? Changes { get; } = changes;

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

    /// <summary>A copy of the pixels (BGRA32, top-down) for reading the text on them off the UI thread; null once cleared.</summary>
    internal byte[]? CopyPixels() => Volatile.Read(ref pixels) is { } owned ? (byte[])owned.Clone() : null;

    /// <summary>The pixels themselves, handed over (the frame is cleared); the taker zeroes them. Null once cleared.</summary>
    internal byte[]? TakePixels() => Interlocked.Exchange(ref pixels, null);

    internal void Clear()
    {
        if (Interlocked.Exchange(ref pixels, null) is { } owned) Array.Clear(owned);
    }
}


/// <summary>How a look was taken: Desktop Duplication (what the monitor shows, full-screen games included) or GDI.
/// <paramref name="Note"/> says why duplication was not used, for the watch status. <paramref name="BehindMartlet"/>: a
/// Martlet window was in front, so the picture is of the window behind it (or the whole screen around Martlet).
/// <paramref name="Monitors"/>: how many monitors the picture shows.</summary>
internal sealed record GlanceResult(ScreenFrame? Frame, GlanceSkip Skip, string? Note = null, bool ProtectedContent = false,
    bool BehindMartlet = false, int Monitors = 1);

internal interface IScreenGlancer
{
    GlanceResult Capture(ScreenScope scope);
    /// <summary>The same look at full size, for reading its text (OCR can't read text the downscaled look shrank to a few
    /// pixels). It changes nothing <see cref="Capture"/> remembers between looks.</summary>
    GlanceResult CaptureText(ScreenScope scope) => Capture(scope);
    TimeSpan UserIdle { get; }
    /// <summary>Frees the screen capture resources (the open duplications and their frame copies) when watching stops.</summary>
    void Release();
}

/// <summary>Captures the foreground window, downscaled to at most 1024 px, or the whole screen: every monitor side by side as
/// Windows arranges them (taskbars, the notification area and pop-up notifications included), each monitor at most 1024 px
/// and the picture at most 2048 px. It reads monitors through DXGI Desktop Duplication, which also sees full-screen DirectX
/// games, and falls back to GDI when duplication is unavailable (remote sessions, a refused hybrid-GPU laptop, a rotated
/// monitor). No hooking or injection. When a Martlet window is in front, it looks at the window behind it. Martlet's own
/// windows, password managers and private browsing windows are painted over wherever they show; a minimized window or a
/// password manager / private window in front is never captured; protected video and windows that exclude themselves from
/// capture read back black and are skipped. Each screenshot also names the program in front and says whether its window
/// fills its monitor (<see cref="ActiveApp"/>). <see cref="CaptureText"/> takes the same look at full size (at most
/// <see cref="TextMaximumEdge"/>) for reading the text on it.</summary>
internal sealed class ScreenGlancer : IScreenGlancer
{
    internal const int MaximumEdge = 1024;
    /// <summary>A text picture is full size up to this long edge (Windows OCR reads pictures up to 10000 pixels).</summary>
    internal const int TextMaximumEdge = 8192;
    private const int SignatureWidth = 16, SignatureHeight = 9;
    private static readonly string[] PrivateTitles =
    [
        "password", "1password", "bitwarden", "keepass", "lastpass", "dashlane", "keeper", "credential manager",
        "inprivate", "incognito", "private browsing", "authenticator", "online banking"
    ];
    // One duplication per monitor, open while watching.
    private readonly Dictionary<nint, DesktopDuplication> duplications = [];
    private byte[]? previous;
    // The last screenshot's gaze grid and where it was taken: the next one of the same area says what changed where.
    private byte[]? previousGrid;
    private NativeRect previousArea;

    public void Release()
    {
        DesktopDuplication[] open;
        lock (duplications)
        {
            open = [.. duplications.Values];
            duplications.Clear();
        }
        foreach (var duplication in open) duplication.Release();
        previous = null;
        previousGrid = null;
    }

    public TimeSpan UserIdle
    {
        get
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime)) : TimeSpan.Zero;
        }
    }

    /// <summary>A window title Martlet never looks at: password managers, private browsing, authenticators, banking.</summary>
    internal static bool IsPrivateTitle(string title)
    {
        var lower = title.ToLowerInvariant();
        return PrivateTitles.Any(lower.Contains);
    }

    public GlanceResult Capture(ScreenScope scope) => Capture(scope, text: false);

    public GlanceResult CaptureText(ScreenScope scope) => Capture(scope, text: true);

    private GlanceResult Capture(ScreenScope scope, bool text)
    {
        var old = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            var own = (uint)Environment.ProcessId;
            var window = GetForegroundWindow();
            if (window == GetShellWindow()) window = 0;
            var behind = false;
            if (window != 0)
            {
                GetWindowThreadProcessId(window, out var process);
                // A Martlet window in front (say, the talk window you clicked to read it): look at the window you were using behind it.
                if (process == own)
                {
                    behind = true;
                    window = WindowBehind(window, own);
                }
            }
            return scope == ScreenScope.ActiveScreen ? CaptureScreen(window, behind, own, text) : CaptureWindow(window, behind, own, text);
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

    private GlanceResult CaptureWindow(nint window, bool behind, uint own, bool text)
    {
        if (window == 0) return new(null, behind ? GlanceSkip.MartletInFront : GlanceSkip.NoWindow);
        if (IsIconic(window)) return new(null, GlanceSkip.Minimized);
        if (Cloaked(window)) return new(null, GlanceSkip.NoWindow);
        var title = WindowTitle(window);
        if (IsPrivateTitle(title)) return new(null, GlanceSkip.Private, BehindMartlet: behind);
        var monitorHandle = MonitorFromWindow(window, MonitorDefaultToNearest);
        var monitor = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitorHandle, ref monitor) || !Bounds(window, out var bounds))
            return new(null, GlanceSkip.CaptureFailed);
        var area = NativeRect.Intersect(bounds, monitor.rcMonitor);
        if (area.Width < 64 || area.Height < 64) return new(null, GlanceSkip.NoWindow);
        var scale = Math.Min(1.0, (double)(text ? TextMaximumEdge : MaximumEdge) / Math.Max(area.Width, area.Height));
        int width = Math.Max(1, (int)Math.Round(area.Width * scale)), height = Math.Max(1, (int)Math.Round(area.Height * scale));
        if (!text) Keep([monitorHandle]);
        string? note = null;
        var protectedContent = false;
        var pixels = GrabMonitor(monitorHandle, area, width, height, ref note, ref protectedContent);
        if (pixels is null) return new(null, GlanceSkip.CaptureFailed, note);
        return Finish(pixels, width, height, area, title, own, window, note, protectedContent, behind, 1, ActiveApp.Of(window), text);
    }

    // Every monitor in one picture, laid out as Windows arranges them; areas no monitor covers stay black.
    private GlanceResult CaptureScreen(nint window, bool behind, uint own, bool text)
    {
        var title = window == 0 ? "" : WindowTitle(window);
        // A private window in front (or behind Martlet) means you are busy with something private: no look at all.
        if (window != 0 && IsPrivateTitle(title)) return new(null, GlanceSkip.Private, BehindMartlet: behind);
        var monitors = Monitors();
        if (monitors.Count == 0) return new(null, GlanceSkip.CaptureFailed);
        var area = monitors[0].Area;
        foreach (var (_, rect) in monitors)
            area = new() { Left = Math.Min(area.Left, rect.Left), Top = Math.Min(area.Top, rect.Top),
                Right = Math.Max(area.Right, rect.Right), Bottom = Math.Max(area.Bottom, rect.Bottom) };
        var largest = monitors.Max(m => Math.Max(m.Area.Width, m.Area.Height));
        var scale = text ? Math.Min(1.0, (double)TextMaximumEdge / Math.Max(area.Width, area.Height))
            : Math.Min(1.0, Math.Min((double)MaximumEdge / largest, (double)BoundedImage.HardMaxEdge / Math.Max(area.Width, area.Height)));
        int width = Math.Max(1, (int)Math.Round(area.Width * scale)), height = Math.Max(1, (int)Math.Round(area.Height * scale));
        if (!text) Keep(monitors.Select(m => m.Handle));
        var pixels = new byte[width * height * 4];
        string? note = null;
        bool protectedContent = false, captured = false;
        foreach (var (handle, rect) in monitors)
        {
            int left = Math.Clamp((int)Math.Round((rect.Left - area.Left) * scale), 0, width),
                top = Math.Clamp((int)Math.Round((rect.Top - area.Top) * scale), 0, height),
                right = Math.Clamp((int)Math.Round((rect.Right - area.Left) * scale), 0, width),
                bottom = Math.Clamp((int)Math.Round((rect.Bottom - area.Top) * scale), 0, height);
            if (right - left < 1 || bottom - top < 1) continue;
            var part = GrabMonitor(handle, rect, right - left, bottom - top, ref note, ref protectedContent);
            if (part is null) continue;
            captured = true;
            for (var y = 0; y < bottom - top; y++)
                Buffer.BlockCopy(part, y * (right - left) * 4, pixels, ((top + y) * width + left) * 4, (right - left) * 4);
            Array.Clear(part);
        }
        if (!captured)
        {
            Array.Clear(pixels);
            return new(null, GlanceSkip.CaptureFailed, note, Monitors: monitors.Count);
        }
        return Finish(pixels, width, height, area, title, own, window, note, protectedContent, behind, monitors.Count,
            window == 0 ? ("", false) : ActiveApp.Of(window), text);
    }

    // The black check, painting over Martlet's own and private windows, and the change score. The program in front and whether
    // it fills its monitor go with the picture. A text picture keeps the looks' own change score and gaze grid as they were.
    private GlanceResult Finish(byte[] pixels, int width, int height, NativeRect area, string title, uint own, nint target,
        string? note, bool protectedContent, bool behind, int monitors, (string Name, bool FullScreen) app, bool text)
    {
        var signature = Signature(pixels, width, height);
        if (signature.Max() < 10)
        {
            Array.Clear(pixels);
            return new(null, GlanceSkip.Blank, note, protectedContent, behind, monitors);
        }
        // Martlet's own windows never leave this PC: they are painted over, and a picture that is nearly all Martlet is skipped.
        if (BlankHidden(pixels, width, height, area, own, target) >= 0.9)
        {
            Array.Clear(pixels);
            return new(null, GlanceSkip.MartletInFront, note, BehindMartlet: behind, Monitors: monitors);
        }
        if (text)
            return new(new(pixels, width, height, title.Length > 80 ? title[..80] : title, 0,
                new ScreenRect(area.Left, area.Top, area.Width, area.Height), app: app.Name, fullScreen: app.FullScreen),
                GlanceSkip.None, note, protectedContent, behind, monitors);
        signature = Signature(pixels, width, height);
        var change = previous is null ? 1.0 : signature.Zip(previous, (a, b) => Math.Abs(a - b)).Average() / 255.0;
        previous = signature;
        var grid = CharacterGaze.Grid(pixels, width, height);
        var changes = previousArea.Equals(area) ? CharacterGaze.Changes(previousGrid, grid) : null;
        previousGrid = grid;
        previousArea = area;
        return new(new(pixels, width, height, title.Length > 80 ? title[..80] : title, change,
            new ScreenRect(area.Left, area.Top, area.Width, area.Height), changes, app.Name, app.FullScreen),
            GlanceSkip.None, note, protectedContent, behind, monitors);
    }

    // Duplication first: it sees full-screen games and does not stall the game the way a GDI screen read can. A still desktop
    // can briefly read black from a fresh duplication; GDI decides then.
    private byte[]? GrabMonitor(nint monitor, NativeRect area, int width, int height, ref string? note, ref bool protectedContent)
    {
        DesktopDuplication duplication;
        lock (duplications)
        {
            if (!duplications.TryGetValue(monitor, out duplication!)) duplications[monitor] = duplication = new();
        }
        var pixels = duplication.Grab(monitor, area, width, height);
        if (pixels is not null && duplication.ProtectedContent) protectedContent = true;
        if (pixels is null) note ??= duplication.Problem;
        if (pixels is not null && Signature(pixels, width, height).Max() >= 10) return pixels;
        var grabbed = Grab(area, width, height);
        if (grabbed is null) return pixels;
        if (pixels is not null) Array.Clear(pixels);
        return grabbed;
    }

    // Closes the duplications of monitors this look doesn't use (a window moved to another monitor, a monitor unplugged).
    private void Keep(IEnumerable<nint> monitors)
    {
        var used = monitors.ToHashSet();
        List<DesktopDuplication> unused = [];
        lock (duplications)
        {
            foreach (var monitor in duplications.Keys.Where(m => !used.Contains(m)).ToArray())
            {
                unused.Add(duplications[monitor]);
                duplications.Remove(monitor);
            }
        }
        foreach (var duplication in unused) duplication.Release();
    }

    private static List<(nint Handle, NativeRect Area)> Monitors()
    {
        var found = new List<(nint, NativeRect)>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint _, ref NativeRect rect, nint _) =>
        {
            if (rect.Width > 0 && rect.Height > 0 && found.Count < 16) found.Add((monitor, rect));
            return true;
        }, 0);
        return found;
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

    /// <summary>Paints neutral grey wherever Martlet's own windows and password manager / private browsing windows show in
    /// <paramref name="area"/>, working down the z-order so a window only hides what isn't already covered by windows above
    /// it, and returns the share of the picture Martlet's own windows covered. A hidden window's edge pixels are painted and a
    /// covering window's edge pixels aren't counted as covering, so nothing of a hidden window bleeds through a downscaled
    /// edge. Click-through overlays are left alone (the subtitles keep themselves out of capture, and painting a transparent
    /// overlay's box would hide what is under it), and see-through (layered) windows don't count as covering what is below
    /// them, except the active window (<paramref name="target"/>).</summary>
    private static double BlankHidden(byte[] pixels, int width, int height, NativeRect area, uint own, nint target)
    {
        double scaleX = (double)width / area.Width, scaleY = (double)height / area.Height;
        // Outward for what is hidden, inward for what covers.
        (int Left, int Top, int Right, int Bottom) Map(NativeRect rect, bool outward) => (
            Math.Clamp((int)(outward ? Math.Floor((rect.Left - area.Left) * scaleX) : Math.Ceiling((rect.Left - area.Left) * scaleX)), 0, width),
            Math.Clamp((int)(outward ? Math.Floor((rect.Top - area.Top) * scaleY) : Math.Ceiling((rect.Top - area.Top) * scaleY)), 0, height),
            Math.Clamp((int)(outward ? Math.Ceiling((rect.Right - area.Left) * scaleX) : Math.Floor((rect.Right - area.Left) * scaleX)), 0, width),
            Math.Clamp((int)(outward ? Math.Ceiling((rect.Bottom - area.Top) * scaleY) : Math.Floor((rect.Bottom - area.Top) * scaleY)), 0, height));
        // Windows that cover what is below them or are hidden, top of the z-order first.
        var windows = new List<(NativeRect Bounds, bool Hide, bool Own)>();
        var last = -1;
        var window = GetTopWindow(0);
        for (int i = 0; window != 0 && i < 4096; i++, window = GetWindow(window, GwHwndNext))
        {
            if (!IsWindowVisible(window) || IsIconic(window) || Cloaked(window)) continue;
            var style = GetWindowLongPtrW(window, GwlExStyle);
            if ((style & ExTransparent) != 0 || !Bounds(window, out var bounds) || bounds.Width <= 0 || bounds.Height <= 0) continue;
            GetWindowThreadProcessId(window, out var process);
            var mine = process == own;
            var hide = mine || IsPrivateTitle(WindowTitle(window));
            if (!hide && window != target && (style & ExLayered) != 0) continue;
            windows.Add((bounds, hide, mine));
            if (hide) last = windows.Count - 1;
        }
        if (last < 0) return 0;
        var covered = new bool[width * height];
        int taken = 0, count = 0;
        for (var w = 0; w <= last && taken < covered.Length; w++)
        {
            var (bounds, hide, mine) = windows[w];
            var (left, top, right, bottom) = Map(NativeRect.Intersect(bounds, area), hide);
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                var index = y * width + x;
                if (covered[index]) continue;
                covered[index] = true;
                taken++;
                if (!hide) continue;
                if (mine) count++;
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
            // CAPTUREBLT includes layered windows such as pop-up notifications.
            if (!StretchBlt(memory, 0, 0, width, height, screen, area.Left, area.Top, area.Width, area.Height, SourceCopy | CaptureBlt))
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

    /// <summary>A window's title without control characters. Another process's title is read without sending it a message,
    /// so a hung window can't stall a look.</summary>
    internal static string WindowTitle(nint window)
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
    private const long ExTransparent = 0x20, ExToolWindow = 0x80, ExLayered = 0x80000, ExNoActivate = 0x08000000;
    private const uint SourceCopy = 0x00CC0020, CaptureBlt = 0x40000000;

    private delegate bool MonitorCallback(nint monitor, nint dc, ref NativeRect rect, nint data);

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
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
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