using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// Also built into Martlet's MCP server (active_app_check), in its own namespace.
#if MARTLET_MCP
namespace Martlet.Mcp.Shared;
#else
namespace Martlet.Desktop;
#endif

/// <summary>Which program the window in front belongs to and whether it fills its monitor, for what a look at the screen tells
/// the Thinking model (docs/SCREEN_COMMENTARY.md). The name is the one in the program's file: its description (such as
/// Google Chrome), else its product name, else its file name. A window fills its monitor when it covers all of it, taskbar
/// included, and isn't an ordinary maximized window: a full-screen game (exclusive or borderless), video or slide show.
/// Windows reports both; nothing is injected or hooked, and neither contains the window's title.</summary>
internal static class ActiveApp
{
    internal const int MaximumName = 60;
    // Names by program file: each file's version information is read once.
    private static readonly ConcurrentDictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The program <paramref name="window"/> belongs to and whether it fills its monitor.</summary>
    internal static (string Name, bool FullScreen) Of(nint window) => (Name(window), FullScreen(window));

    /// <summary>The program <paramref name="window"/> belongs to, by name; empty when Windows doesn't say.</summary>
    internal static string Name(nint window)
    {
        if (window == 0) return "";
        GetWindowThreadProcessId(window, out var process);
        var path = ProgramFile(process);
        // A Store app's window belongs to Windows' frame host; the app owns the window inside it.
        if (path is not null && Path.GetFileName(path).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) &&
            Inside(window, process) is not 0 and var inner && ProgramFile(inner) is { } app)
            path = app;
        return path is null ? "" : NameOf(path);
    }

    /// <summary>The name of the program in <paramref name="path"/>: its file description, else its product name, else its file
    /// name without the extension.</summary>
    internal static string NameOf(string path)
    {
        if (Names.TryGetValue(path, out var known)) return known;
        var name = "";
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            name = Clean(info.FileDescription) is { Length: > 0 } description ? description : Clean(info.ProductName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        if (name.Length == 0) name = Clean(Path.GetFileNameWithoutExtension(path));
        if (Names.Count < 256) Names[path] = name;
        return name;
    }

    /// <summary>Whether <paramref name="window"/> fills its monitor (<see cref="Fills"/>). A minimized window and the desktop
    /// never do.</summary>
    internal static bool FullScreen(nint window)
    {
        if (window == 0 || IsIconic(window) || ClassName(window) is "Progman" or "WorkerW") return false;
        var old = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            var monitor = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(MonitorFromWindow(window, MonitorDefaultToNearest), ref monitor) || !GetWindowRect(window, out var bounds))
                return false;
            var screen = monitor.rcMonitor;
            return Fills((bounds.Left, bounds.Top, bounds.Right, bounds.Bottom), (screen.Left, screen.Top, screen.Right, screen.Bottom),
                IsZoomed(window), (GetWindowLongPtrW(window, GwlStyle) & WsCaption) == WsCaption);
        }
        finally
        {
            if (old != 0) SetThreadDpiAwarenessContext(old);
        }
    }

    /// <summary>Whether a window at <paramref name="window"/> fills a monitor at <paramref name="monitor"/> (left, top, right
    /// and bottom, in pixels): it covers all of it, and it isn't a <paramref name="maximized"/> window with a
    /// <paramref name="titleBar"/>, which Windows also sizes over the whole monitor when the taskbar hides itself. A borderless
    /// full-screen window has no title bar, and an exclusive full-screen game covers its monitor at the game's resolution.</summary>
    internal static bool Fills((int Left, int Top, int Right, int Bottom) window, (int Left, int Top, int Right, int Bottom) monitor,
        bool maximized, bool titleBar) =>
        monitor.Right > monitor.Left && monitor.Bottom > monitor.Top && !(maximized && titleBar) &&
        window.Left <= monitor.Left && window.Top <= monitor.Top && window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;

    /// <summary>The app as a look's message names it: "Google Chrome", "Google Chrome (full screen)" or "unknown".</summary>
    internal static string Describe(string? name, bool fullScreen) =>
        (string.IsNullOrWhiteSpace(name) ? "unknown" : name) + (fullScreen ? " (full screen)" : "");

    /// <summary>The app inside brackets, as the conversation keeps it: "Google Chrome, full screen", "Google Chrome",
    /// "full screen", or empty when neither is known.</summary>
    internal static string Label(string? name, bool fullScreen) => string.IsNullOrWhiteSpace(name)
        ? fullScreen ? "full screen" : ""
        : fullScreen ? name + ", full screen" : name;

    /// <summary>A name on one line, without quotes or control characters, at most <see cref="MaximumName"/> characters.</summary>
    internal static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var clean = new StringBuilder(Math.Min(text.Length, MaximumName));
        foreach (var c in text)
        {
            if (clean.Length >= MaximumName) break;
            if (char.IsControl(c) || char.IsWhiteSpace(c) || c == '"')
            {
                if (clean.Length > 0 && clean[^1] != ' ') clean.Append(' ');
            }
            else clean.Append(c);
        }
        return clean.ToString().Trim();
    }

    private static string? ProgramFile(uint process)
    {
        if (process == 0) return null;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, process);
        if (handle == 0) return null;
        try
        {
            var path = new StringBuilder(1024);
            var size = (uint)path.Capacity;
            return QueryFullProcessImageNameW(handle, 0, path, ref size) && size > 0 ? path.ToString(0, (int)size) : null;
        }
        finally { CloseHandle(handle); }
    }

    // The first window inside a frame that another process owns.
    private static uint Inside(nint frame, uint host)
    {
        uint found = 0;
        EnumChildWindows(frame, (child, _) =>
        {
            GetWindowThreadProcessId(child, out var process);
            if (process == 0 || process == host) return true;
            found = process;
            return false;
        }, 0);
        return found;
    }

    private static string ClassName(nint window)
    {
        var name = new StringBuilder(64);
        return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    private static readonly nint PerMonitorAwareV2 = -4;
    private const uint MonitorDefaultToNearest = 2, ProcessQueryLimitedInformation = 0x1000;
    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000;

    private delegate bool ChildCallback(nint window, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out RECT rect);
    [DllImport("user32.dll")] private static extern long GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parent, ChildCallback callback, nint data);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, uint process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(nint process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}
