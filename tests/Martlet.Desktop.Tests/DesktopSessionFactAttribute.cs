using System.Runtime.InteropServices;

namespace Martlet.Desktop.Tests;

/// <summary>A test that draws on the Windows desktop: WPF pictures, or WebView2 through Direct3D. In a disconnected session
/// (for example a closed Remote Desktop window), Windows draws nothing and has no Direct3D device, so the test is skipped
/// with that reason instead of failing.</summary>
public sealed class DesktopSessionFactAttribute : FactAttribute
{
    public DesktopSessionFactAttribute()
    {
        if (DesktopSession.Disconnected) Skip = "Needs a connected Windows desktop session; this session is disconnected, so " +
            "Windows draws nothing and has no Direct3D device. Connect to the PC (or sign in at it) and run the test again.";
    }
}

internal static class DesktopSession
{
    private const int WtsConnectState = 8, WtsDisconnected = 4;

    /// <summary>Whether this process's Windows session has no display: its Remote Desktop connection was closed.</summary>
    internal static bool Disconnected
    {
        get
        {
            if (!WTSQuerySessionInformationW(0, -1, WtsConnectState, out var buffer, out var bytes)) return false;
            try { return bytes >= sizeof(int) && Marshal.ReadInt32(buffer) == WtsDisconnected; }
            finally { WTSFreeMemory(buffer); }
        }
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(nint server, int session, int infoClass, out nint buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);
}
