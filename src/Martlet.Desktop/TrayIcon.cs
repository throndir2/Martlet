using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Martlet.Desktop;

/// <summary>Martlet's icon in the Windows notification area (Shell_NotifyIcon, version 4). It lives on a hidden top-level
/// window, which also hears Explorer restart (TaskbarCreated) and adds the icon back. A left click, Enter or a click on its
/// notice raises <see cref="Opened"/>; a right click, Shift+F10 or the menu key raises <see cref="MenuRequested"/> with where
/// to show the menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    /// <summary>The hidden window's title. Martlet.Mcp's <c>ui_tray</c> finds the icon's window by it.</summary>
    internal const string WindowTitle = "Martlet notification area";
    /// <summary>A property on the hidden window while the icon is in the notification area (<c>ui_tray</c> reports it).</summary>
    internal const string AddedProperty = "MartletTrayIconAdded";
    /// <summary>The message Explorer sends for clicks on the icon (WM_APP + 0x4D); <c>ui_tray</c> posts the same ones.</summary>
    internal const int CallbackMessage = 0x8000 + 0x4D;
    private const int IconId = 1;

    private readonly HwndSource source;
    private readonly int taskbarCreated;
    private readonly nint icon;
    private string tip = "Martlet";
    private bool disposed;

    internal event Action? Opened;
    internal event Action<Point>? MenuRequested;
    internal bool Added { get; private set; }
    /// <summary>The icon's (hidden) window content, where the icon's menu is placed: that window takes the foreground for it.</summary>
    internal UIElement MenuAnchor { get; }

    internal TrayIcon()
    {
        source = new HwndSource(new HwndSourceParameters(WindowTitle)
        {
            WindowStyle = unchecked((int)WS_POPUP),
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
            PositionX = 0, PositionY = 0, Width = 0, Height = 0
        });
        source.AddHook(Hook);
        // An empty root gives the icon's menu a placement target that always has a window, even before Martlet's window is shown.
        source.RootVisual = MenuAnchor = new System.Windows.Controls.Border();
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        if (taskbarCreated != 0) ChangeWindowMessageFilterEx(source.Handle, taskbarCreated, MSGFLT_ALLOW, 0);
        icon = LoadIcon(GetSystemMetrics(SM_CXSMICON));
        Add();
    }

    /// <summary>The icon's tooltip (at most 127 characters).</summary>
    internal void SetToolTip(string text)
    {
        text = text.Length > 127 ? text[..127] : text;
        if (disposed || text == tip) return;
        tip = text;
        if (!Added) return;
        var data = Data(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>A Windows notification from the icon. Windows holds it back in quiet time and Do not disturb.</summary>
    internal void ShowNotice(string title, string text)
    {
        if (disposed || !Added) return;
        var data = Data(NIF_INFO);
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = text.Length > 255 ? text[..255] : text;
        data.dwInfoFlags = NIIF_USER | NIIF_RESPECT_QUIET_TIME;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>Takes the foreground for the icon's own (hidden) window before its menu opens, as Windows expects for a
    /// notification-area menu: the menu, placed on that window, then has the keyboard and the mouse, so Esc and a click elsewhere
    /// close it.</summary>
    internal bool TakeForeground() => !disposed && SetForegroundWindow(source.Handle);

    private void Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        // After Explorer restarts quickly it can still know the icon; then updating it is enough.
        Added = Shell_NotifyIcon(NIM_ADD, ref data) || Shell_NotifyIcon(NIM_MODIFY, ref data);
        if (Added)
        {
            data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, ref data);
            SetProp(source.Handle, AddedProperty, 1);
        }
        else
        {
            RemoveProp(source.Handle, AddedProperty);
            ErrorLog.Warn("Martlet's icon couldn't be added to the notification area; closing the window exits Martlet.");
        }
    }

    private NOTIFYICONDATAW Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = source.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = icon,
        szTip = tip,
        szInfo = "",
        szInfoTitle = ""
    };

    private nint Hook(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == CallbackMessage)
        {
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case NIN_SELECT or NIN_KEYSELECT or NIN_BALLOONUSERCLICK:
                    handled = true;
                    Opened?.Invoke();
                    break;
                case WM_CONTEXTMENU:
                    handled = true;
                    // Version 4 passes where to open the menu, in screen pixels.
                    var x = (short)(wParam.ToInt64() & 0xFFFF);
                    var y = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                    var point = new Point(x, y);
                    if (source.CompositionTarget is { } target) point = target.TransformFromDevice.Transform(point);
                    MenuRequested?.Invoke(point);
                    break;
            }
        }
        else if (message == taskbarCreated && taskbarCreated != 0 && !disposed)
        {
            Added = false;
            Add();
        }
        return 0;
    }

    /// <summary>The image in Martlet.ico closest to (and not smaller than, when there is one) the notification area's size.</summary>
    private static nint LoadIcon(int size)
    {
        var resource = Application.GetResourceStream(new Uri("/Martlet.Desktop;component/Assets/Martlet.ico", UriKind.Relative))
            ?? throw new InvalidOperationException("Martlet's icon is missing.");
        byte[] file;
        using (var stream = resource.Stream)
        using (var copy = new MemoryStream())
        {
            stream.CopyTo(copy);
            file = copy.ToArray();
        }
        // ICONDIR (6 bytes), then a 16-byte entry per image; a width of 0 means 256.
        var widths = Enumerable.Range(0, BitConverter.ToUInt16(file, 4))
            .Select(index => (Index: index, Width: file[6 + 16 * index] == 0 ? 256 : file[6 + 16 * index])).ToArray();
        var best = widths.Where(entry => entry.Width >= size).OrderBy(entry => entry.Width)
            .Concat(widths.OrderByDescending(entry => entry.Width)).First();
        var entry = 6 + 16 * best.Index;
        var length = BitConverter.ToInt32(file, entry + 8);
        var offset = BitConverter.ToInt32(file, entry + 12);
        var handle = CreateIconFromResourceEx(file.AsSpan(offset, length).ToArray(), length, true, 0x00030000, size, size, 0);
        return handle != 0 ? handle : throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (Added)
        {
            var data = Data(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            Added = false;
        }
        RemoveProp(source.Handle, AddedProperty);
        source.RemoveHook(Hook);
        source.Dispose();
        if (icon != 0) DestroyIcon(icon);
    }

    private const uint WS_POPUP = 0x80000000;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int SM_CXSMICON = 49;
    private const int MSGFLT_ALLOW = 1;
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const int NIIF_USER = 0x4, NIIF_RESPECT_QUIET_TIME = 0x80;
    private const int NOTIFYICON_VERSION_4 = 4;
    private const int WM_CONTEXTMENU = 0x7B, NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401, NIN_BALLOONUSERCLICK = 0x405;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATAW data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(nint window, int message, int action, nint filter);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateIconFromResourceEx(byte[] bits, int size, [MarshalAs(UnmanagedType.Bool)] bool icon, int version,
        int width, int height, int flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProp(nint window, string name, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint RemoveProp(nint window, string name);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
}
