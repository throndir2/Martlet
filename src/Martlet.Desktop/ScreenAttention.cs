using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace Martlet.Desktop;

internal enum AttentionKind { Notification, Flash }

/// <summary>Something on screen that wants the user's attention: a pop-up notification, or a taskbar button that started
/// flashing (<paramref name="App"/> is that window's title). Never logged or shown in status text.</summary>
internal sealed record AttentionSignal(AttentionKind Kind, string App)
{
    /// <summary>What happened, for the glance message (the window's title in quotes, never control characters or quotes).</summary>
    internal string Describe()
    {
        if (Kind == AttentionKind.Notification) return "a notification just popped up";
        var app = new string(App.Where(c => !char.IsControl(c) && c != '"').Take(60).ToArray()).Trim();
        return app.Length > 0
            ? $"the taskbar button of \"{app}\" just started flashing for attention"
            : "a taskbar button just started flashing for attention";
    }

    /// <summary>A plain description for the vision status line (no titles).</summary>
    internal string Plain => Kind == AttentionKind.Notification ? "a notification" : "a flashing taskbar button";
}

internal interface IScreenAttention
{
    /// <summary>Starts noticing; call on the UI thread. Starting again does nothing.</summary>
    void Start();
    void Stop();
    /// <summary>The newest thing that wants attention since the last call (checks for pop-up notifications at most once a
    /// second), or null. Call on the UI thread.</summary>
    AttentionSignal? Check();
}

/// <summary>Notices what wants the user's attention while Martlet watches the whole screen, so it can look right away
/// instead of when its pacer next would. A taskbar button that starts flashing (a chat app's new message, an invite, a
/// finished download) comes from Windows' documented shell hook (<c>RegisterShellHookWindow</c>, <c>HSHELL_FLASH</c>). A
/// pop-up notification is noticed when the shell's notification window appears (best effort: a visible, uncloaked
/// <c>Windows.UI.Core.CoreWindow</c> of ShellExperienceHost or ShellHost that isn't in front, as toasts never take focus,
/// and is smaller than half its monitor). Only window titles and classes are read: nothing is hooked into another app and
/// no notification text is read; the picture shows what popped up. Martlet's own windows, windows already in front and
/// password managers / private windows are ignored, and the same window flashing again within two minutes counts once.</summary>
internal sealed class ScreenAttention : IScreenAttention
{
    private const int FlashMessage = 0x8006; // HSHELL_FLASH = HSHELL_REDRAW | HSHELL_HIGHBIT
    private static readonly TimeSpan Repeat = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(1);
    private static readonly string[] NotificationHosts = ["ShellExperienceHost", "ShellHost"];
    private readonly TimeProvider clock;
    private readonly Dictionary<nint, long> flashed = [];
    private readonly Dictionary<uint, string> processNames = [];
    private HwndSource? hook;
    private int shellMessage;
    private AttentionSignal? pending;
    private bool notificationShown;
    private long lastPoll;

    internal ScreenAttention(TimeProvider? clock = null) => this.clock = clock ?? TimeProvider.System;

    public void Start()
    {
        if (hook is not null) return;
        // A hidden tool window that only receives the shell's notifications; it is never shown.
        var source = new HwndSource(new HwndSourceParameters("Martlet attention")
        {
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80, Width = 0, Height = 0
        });
        shellMessage = RegisterWindowMessage("SHELLHOOK");
        if (shellMessage == 0 || !RegisterShellHookWindow(source.Handle))
        {
            source.Dispose();
            ErrorLog.Info("Vision: Windows didn't let Martlet notice taskbar buttons that flash; it still notices pop-up notifications.");
            hook = null;
            notificationShown = NotificationShowing();
            return;
        }
        source.AddHook(Hook);
        hook = source;
        // A notification already on screen when watching starts isn't new.
        notificationShown = NotificationShowing();
    }

    public void Stop()
    {
        if (hook is { } source)
        {
            DeregisterShellHookWindow(source.Handle);
            source.RemoveHook(Hook);
            source.Dispose();
        }
        hook = null;
        pending = null;
        flashed.Clear();
        notificationShown = false;
    }

    public AttentionSignal? Check()
    {
        var now = clock.GetTimestamp();
        if (clock.GetElapsedTime(lastPoll, now) >= PollEvery)
        {
            lastPoll = now;
            var showing = NotificationShowing();
            if (showing && !notificationShown) pending = new(AttentionKind.Notification, "");
            notificationShown = showing;
        }
        var signal = pending;
        pending = null;
        return signal;
    }

    private nint Hook(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == shellMessage && shellMessage != 0 && (int)(wParam.ToInt64() & 0xFFFF) == FlashMessage && lParam != 0)
            Flashed(lParam);
        return 0;
    }

    private void Flashed(nint window)
    {
        GetWindowThreadProcessId(window, out var process);
        if (process == (uint)Environment.ProcessId || window == GetForegroundWindow()) return;
        var title = ScreenGlancer.WindowTitle(window);
        if (ScreenGlancer.IsPrivateTitle(title)) return;
        var now = clock.GetTimestamp();
        foreach (var stale in flashed.Where(f => clock.GetElapsedTime(f.Value, now) >= Repeat).Select(f => f.Key).ToArray())
            flashed.Remove(stale);
        if (!flashed.TryAdd(window, now)) return;
        pending = new(AttentionKind.Flash, title);
    }

    // A pop-up notification on screen: the shell's notification window, visible, not in front and smaller than half its monitor.
    private bool NotificationShowing()
    {
        var foreground = GetForegroundWindow();
        var found = false;
        EnumWindows((window, _) =>
        {
            var name = new StringBuilder(32);
            if (GetClassName(window, name, name.Capacity) <= 0 || name.ToString() != "Windows.UI.Core.CoreWindow" ||
                window == foreground || !IsWindowVisible(window) ||
                DwmGetWindowAttribute(window, DwmCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0 ||
                !GetWindowRect(window, out var rect) || rect.Width < 32 || rect.Height < 32) return true;
            GetWindowThreadProcessId(window, out var process);
            if (!NotificationHosts.Contains(ProcessName(process), StringComparer.OrdinalIgnoreCase)) return true;
            var monitor = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref monitor) ||
                rect.Width * 2 > monitor.rcMonitor.Width || rect.Height * 2 > monitor.rcMonitor.Height) return true;
            found = true;
            return false;
        }, 0);
        return found;
    }

    private string ProcessName(uint id)
    {
        if (processNames.TryGetValue(id, out var name)) return name;
        try
        {
            using var process = Process.GetProcessById((int)id);
            name = process.ProcessName;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            name = "";
        }
        if (processNames.Count > 256) processNames.Clear();
        return processNames[id] = name;
    }

    private const int DwmCloaked = 14;

    private delegate bool EnumWindowsCallback(nint window, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public uint cbSize; public NativeRect rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterShellHookWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeregisterShellHookWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}
