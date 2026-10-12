using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Martlet.Conversation;

namespace Martlet.Desktop;

/// <summary>The apps the user has open on this PC, for Martlet's greeting after it starts: each program with a window on the
/// taskbar (a visible, unowned, uncloaked top-level window with a title, not a tool window), by its friendly name (the
/// program's file description, such as "Google Chrome", else its file name), the one in front first, then in the order the
/// windows lie on top of each other. Martlet's own windows are left out. Only program names come out of it, never window
/// titles; nothing is kept.</summary>
internal static class OpenApps
{
    internal static IReadOnlyList<GreetingApp> Read(int most = Greetings.MaximumApps)
    {
        var own = Environment.ProcessId;
        var front = GetForegroundWindow();
        var names = new Dictionary<uint, string?>();
        var apps = new List<GreetingApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            EnumWindows((window, _) =>
            {
                if (!IsWindowVisible(window) || GetWindow(window, GwOwner) != 0 || Cloaked(window) || GetWindowTextLength(window) <= 0 ||
                    (GetWindowLongPtrW(window, GwlExStyle) & WsExToolWindow) != 0)
                    return true;
                GetWindowThreadProcessId(window, out var process);
                if (process == 0 || process == (uint)own) return true;
                if (!names.TryGetValue(process, out var name)) names[process] = name = Name(process, window);
                if (name is null) return true;
                if (seen.Add(name)) apps.Add(new(name, window == front));
                else if (window == front) apps[apps.FindIndex(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))] = new(name, true);
                return true;
            }, 0);
        }
        catch (ExternalException) { }
        return [.. apps.OrderByDescending(a => a.Front).Take(most)];
    }

    // The program's friendly name, or null for Martlet's own, the desktop and taskbar, and hosts that aren't apps themselves.
    private static string? Name(uint process, nint window)
    {
        var path = Image(process);
        if (path is null) return null;
        var file = Path.GetFileNameWithoutExtension(path);
        if (file.StartsWith("Martlet", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) || file.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) || file.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("SearchHost", StringComparison.OrdinalIgnoreCase) || file.Equals("LockApp", StringComparison.OrdinalIgnoreCase))
            return null;
        if (file.Equals("explorer", StringComparison.OrdinalIgnoreCase))
            return Class(window) == "CabinetWClass" ? "File Explorer" : null;
        string? description = null;
        try { description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        var name = string.IsNullOrWhiteSpace(description) ? file : description;
        name = new string([.. name.Where(c => !char.IsControl(c))]).Trim();
        return name.Length == 0 ? null : name.Length > 60 ? name[..60] : name;
    }

    private static string? Image(uint process)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, process);
        if (handle == 0) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    private static string Class(nint window)
    {
        var name = new StringBuilder(64);
        return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    private static bool Cloaked(nint window) => DwmGetWindowAttribute(window, DwmCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private const uint ProcessQueryLimitedInformation = 0x1000, GwOwner = 4;
    private const int DwmCloaked = 14, GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;

    private delegate bool EnumWindowsCallback(nint window, nint data);

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsCallback callback, nint data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern long GetWindowLongPtrW(nint window, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, uint process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}
