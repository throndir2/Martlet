using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Martlet.Mcp;

internal sealed partial class DesktopAutomation
{
    /// <summary>Martlet's notification-area icon: "status" reads whether the icon is shown and the main window visible; "open"
    /// and "menu" send the icon what Explorer sends for a left click (show Martlet) and a right click (its menu, at the mouse
    /// pointer or at x, y), granting it the foreground as Explorer does when this process may, then ui_snapshot lists the menu's
    /// Tray* items; every action reports menuOpen and, while it is open, menuBounds. "close" presses the main window's close
    /// button, which hides Martlet in the notification area by default and exits it when Keep running when closed is off, so it
    /// needs --allow-ui-effects.</summary>
    internal object Tray(string action, int? x = null, int? y = null)
    {
        if (!TrayActions.Contains(action)) throw new ArgumentException($"Unknown ui_tray action '{action}'.");
        if (action == "close" && !allowEffects)
            throw new InvalidOperationException("Closing Martlet's window can exit it, so it requires --allow-ui-effects.");
        if ((x is null) != (y is null)) throw new ArgumentException("Give both x and y, or neither.");
        if (x is not null && action is not ("open" or "menu")) throw new ArgumentException("x and y only apply to \"open\" and \"menu\".");
        if (x is < short.MinValue or > short.MaxValue || y is < short.MinValue or > short.MaxValue)
            throw new ArgumentOutOfRangeException(x is < short.MinValue or > short.MaxValue ? nameof(x) : nameof(y), "Screen pixels fit in 16 bits.");
        if (action == "status" && processId is int exited && !Running(exited))
            return new { processId = exited, running = false, trayIcon = false, mainWindowVisible = false, inTray = false, menuOpen = false,
                menuBounds = (int[]?)null };
        var windows = ConnectedWindows();
        var pid = processId!.Value;
        var icon = TrayWindow(pid);
        switch (action)
        {
            case "open" or "menu":
                if (icon == 0) throw new InvalidOperationException("Martlet has no notification-area icon.");
                // Explorer passes where the click was in physical screen pixels, whatever the app's DPI awareness.
                var pointer = x is int clickX && y is int clickY ? new NativePoint { X = clickX, Y = clickY } : PhysicalPointer();
                var at = (nint)((pointer.Y & 0xFFFF) << 16 | (pointer.X & 0xFFFF));
                // Explorer lets the clicked icon's app take the foreground; this only works while this process may take it itself.
                AllowSetForegroundWindow(pid);
                if (!PostMessage(icon, TrayCallbackMessage, at, TrayIconId << 16 | (action == "open" ? NinSelect : WmContextMenu)))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                break;
            case "close":
                var main = windows.FirstOrDefault(window => window.Current.AutomationId == "MartletMainWindow")
                    ?? throw new InvalidOperationException("Martlet's window is already hidden.");
                ((WindowPattern)main.GetCurrentPattern(WindowPattern.Pattern)).Close();
                break;
        }
        if (action != "status") Thread.Sleep(500);
        if (!Running(pid))
            return new { processId = pid, running = false, trayIcon = false, mainWindowVisible = false, inTray = false, menuOpen = false,
                menuBounds = (int[]?)null };
        var now = Windows(pid);
        var visible = MainWindowVisible(now);
        icon = TrayWindow(pid);
        var menu = TrayMenuWindow(now);
        return new { processId = pid, running = true, trayIcon = icon != 0 && GetProp(icon, TrayAddedProperty) != 0, mainWindowVisible = visible,
            inTray = icon != 0 && !visible, menuOpen = menu is not null,
            menuBounds = menu is null ? null : Placement((nint)menu.Current.NativeWindowHandle).Bounds };
    }

    /// <summary>The window holding the icon's open menu: a Martlet popup window (not the main window) with the TrayMenu.</summary>
    private static AutomationElement? TrayMenuWindow(AutomationElement[] windows) =>
        windows.Where(window => window.Current.AutomationId != "MartletMainWindow").FirstOrDefault(window =>
            window.Current.AutomationId == "TrayMenu" ||
            window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "TrayMenu")) is not null);

    /// <summary>The mouse pointer in physical screen pixels, as Explorer reports it to the icon.</summary>
    private static NativePoint PhysicalPointer()
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            GetCursorPos(out var pointer);
            return pointer;
        }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }

    /// <summary>The attached process's (hidden) notification-area window, or 0.</summary>
    private static nint TrayWindow(int pid)
    {
        nint found = 0;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var owner);
            if (owner != pid || GetWindowTextLength(handle) != TrayWindowTitle.Length) return true;
            var title = new System.Text.StringBuilder(TrayWindowTitle.Length + 1);
            GetWindowText(handle, title, title.Capacity);
            if (title.ToString() != TrayWindowTitle) return true;
            found = handle;
            return false;
        }, 0);
        return found;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, System.Text.StringBuilder text, int count);
}
