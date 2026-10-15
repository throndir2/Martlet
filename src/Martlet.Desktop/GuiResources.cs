using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

#if MARTLET_MCP
namespace Martlet.Mcp.Shared;
#else
namespace Martlet.Desktop;
#endif

/// <summary>A process's Window Manager (USER) and GDI objects now and at most since it started, and the most Windows lets one
/// process have (10,000 of each unless the registry changes it).</summary>
internal sealed record GuiCount(int User, int UserPeak, int Gdi, int GdiPeak, int UserQuota, int GdiQuota)
{
    /// <summary>The larger of the two shares of their limits (0 to 1 and more).</summary>
    internal double Share => Math.Max(UserQuota > 0 ? (double)User / UserQuota : 0, GdiQuota > 0 ? (double)Gdi / GdiQuota : 0);

    internal string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{User:N0} Window Manager objects (at most {UserPeak:N0}) of {UserQuota:N0} and {Gdi:N0} GDI objects (at most {GdiPeak:N0}) of {GdiQuota:N0}");
}

/// <summary>The windows a process has, also hidden and message-only ones, by window class (GUIDs in class names, such as
/// WPF's <c>HwndWrapper[Martlet.Desktop;;&lt;guid&gt;]</c>, become <c>*</c> so the same kind counts once).</summary>
internal sealed record WindowCensus(int Windows, int Threads, IReadOnlyList<(string Class, int Count)> Classes)
{
    /// <summary>"27 windows on 6 threads (HwndWrapper[Martlet.Desktop;;*] 12, IME 6, ...)", the <paramref name="top"/> largest
    /// classes; the rest of the Window Manager objects are icons, cursors, menus, hooks and the like.</summary>
    internal string Describe(int top = 8)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{Windows:N0} windows on {Threads:N0} threads");
        if (Classes.Count == 0) return text;
        var shown = string.Join(", ", Classes.Take(top).Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Class} {c.Count:N0}")));
        return $"{text} ({shown}{(Classes.Count > top ? ", ..." : "")})";
    }
}

/// <summary>Reads how many Window Manager and GDI objects a process holds (GetGuiResources) and which windows it has. A
/// process that leaks them stops working at its limit: timers, windows and menus then fail with "The current process has
/// used all of its system allowance of handles for Window Manager objects". Reading needs no new objects, so it also
/// works at the limit.</summary>
internal static class GuiResources
{
    internal const int DefaultQuota = 10_000;
    private const uint GdiObjects = 0, UserObjects = 1, GdiObjectsPeak = 2, UserObjectsPeak = 4;
    private const uint QueryLimitedInformation = 0x1000;
    private static readonly Regex GuidPattern = new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.CultureInvariant);
    private static (int User, int Gdi)? quotas;

    /// <summary>The counts of process <paramref name="processId"/>, or null when it can't be read (gone, or no access).</summary>
    internal static GuiCount? Read(int processId)
    {
        var own = processId == Environment.ProcessId;
        var process = own ? GetCurrentProcess() : OpenProcess(QueryLimitedInformation, false, processId);
        if (process == 0) return null;
        try
        {
            var (userQuota, gdiQuota) = Quotas();
            var user = (int)GetGuiResources(process, UserObjects);
            var gdi = (int)GetGuiResources(process, GdiObjects);
            if (user == 0 && gdi == 0 && Marshal.GetLastWin32Error() != 0) return null;
            return new(user, (int)GetGuiResources(process, UserObjectsPeak), gdi, (int)GetGuiResources(process, GdiObjectsPeak),
                userQuota, gdiQuota);
        }
        finally
        {
            if (!own) CloseHandle(process);
        }
    }

    /// <summary>Every window of process <paramref name="processId"/>: top-level ones with all their child windows, and
    /// message-only ones.</summary>
    internal static WindowCensus Census(int processId)
    {
        var classes = new Dictionary<string, int>(StringComparer.Ordinal);
        var threads = new HashSet<uint>();
        var name = new StringBuilder(256);
        var total = 0;
        void Count(nint window)
        {
            total++;
            name.Clear();
            var length = GetClassName(window, name, name.Capacity);
            var kind = length > 0 ? GuidPattern.Replace(name.ToString(), "*") : "?";
            classes[kind] = classes.GetValueOrDefault(kind) + 1;
        }
        bool Mine(nint window, out uint thread)
        {
            thread = GetWindowThreadProcessId(window, out var owner);
            return owner == (uint)processId;
        }
        // A child window can belong to another process (the character renderer's WebView2), so each is checked too.
        EnumWindowsProc child = (window, _) =>
        {
            if (Mine(window, out var thread))
            {
                threads.Add(thread);
                Count(window);
            }
            return true;
        };
        EnumWindows((window, _) =>
        {
            if (!Mine(window, out var thread)) return true;
            threads.Add(thread);
            Count(window);
            EnumChildWindows(window, child, 0);
            return true;
        }, 0);
        // Message-only windows (WPF's dispatcher windows, for one) aren't top-level, so EnumWindows never lists them.
        nint after = 0;
        for (var guard = 0; guard < 100_000; guard++)
        {
            after = FindWindowEx(MessageOnly, after, null, null);
            if (after == 0) break;
            if (!Mine(after, out var thread)) continue;
            threads.Add(thread);
            Count(after);
            EnumChildWindows(after, child, 0);
        }
        GC.KeepAlive(child);
        return new(total, threads.Count,
            [.. classes.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).Select(c => (c.Key, c.Value))]);
    }

    /// <summary>The per-process limits: USERProcessHandleQuota and GDIProcessHandleQuota, 10,000 each by default.</summary>
    private static (int User, int Gdi) Quotas()
    {
        if (quotas is { } known) return known;
        int user = DefaultQuota, gdi = DefaultQuota;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows");
            if (key?.GetValue("USERProcessHandleQuota") is int u && u > 0) user = u;
            if (key?.GetValue("GDIProcessHandleQuota") is int g && g > 0) gdi = g;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { }
        return (quotas = (user, gdi)).Value;
    }

    private static readonly nint MessageOnly = -3;

    private delegate bool EnumWindowsProc(nint window, nint data);

    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetGuiResources(nint process, uint flags);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsProc callback, nint data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string? className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
