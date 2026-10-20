using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Martlet.Desktop.Tests;

/// <summary>A test that needs WPF to draw a picture (RenderTargetBitmap). Where Windows draws nothing, for example in a
/// disconnected Remote Desktop session, the test is skipped with that reason instead of failing.</summary>
public sealed class DrawingFactAttribute : FactAttribute
{
    public DrawingFactAttribute()
    {
        if (DesktopSession.CannotDraw is { } why) Skip = why;
    }
}

/// <summary>A test that needs a Direct3D 9 device, as WebView2's composition control (the avatar overlay) does. WPF draws
/// through Direct3D 9 too, so when WPF finds no Direct3D device (render tier 0: no graphics card it can use, or a
/// disconnected session), WebView2 fails with 0x8876086A (D3DERR_NOTAVAILABLE); the test is skipped with that reason.</summary>
public sealed class Direct3DFactAttribute : FactAttribute
{
    public Direct3DFactAttribute()
    {
        if (DesktopSession.NoDirect3D is { } why) Skip = why;
    }
}

internal static class DesktopSession
{
    private const int WtsConnectState = 8, WtsDisconnected = 4;
    private static readonly Lazy<(int Tier, bool Draws)> Probe = new(Measure);

    private static string Disconnect => Disconnected
        ? " This session is disconnected (a closed Remote Desktop window); connect to the PC and run the test again." : "";

    /// <summary>Why WPF can't draw a picture here, or null when it can.</summary>
    internal static string? CannotDraw => Probe.Value.Draws ? null
        : "Needs WPF to draw: a 4 x 4 test picture came out blank on this PC." + Disconnect;

    /// <summary>Why there is no Direct3D 9 device here, or null when there is.</summary>
    internal static string? NoDirect3D => Probe.Value.Tier > 0 ? null
        : "Needs a Direct3D 9 device: WPF found none (render tier 0), so WebView2 would fail with 0x8876086A." + Disconnect;

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

    // WPF's render tier and a small RenderTargetBitmap drawing, on an STA thread as the tests use them.
    private static (int Tier, bool Draws) Measure()
    {
        (int, bool) result = (0, false);
        var thread = new Thread(() =>
        {
            try
            {
                var tier = RenderCapability.Tier >> 16;
                var visual = new DrawingVisual();
                using (var context = visual.RenderOpen())
                {
                    context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 4, 4));
                    context.DrawRectangle(Brushes.Black, null, new Rect(2, 2, 2, 2));
                }
                var bitmap = new RenderTargetBitmap(4, 4, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var pixels = new byte[4 * 4 * 4];
                bitmap.CopyPixels(pixels, 16, 0);
                var white = pixels[0] == 255 && pixels[3] == 255;
                var black = pixels[60] == 0 && pixels[63] == 255;
                result = (tier, white && black);
            }
            catch (Exception error) when (error is COMException or InvalidOperationException or OutOfMemoryException) { }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));
        return result;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(nint server, int session, int infoClass, out nint buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);
}
