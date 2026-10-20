using System.Reflection;
using System.Runtime.InteropServices;
using Martlet.Avatar.RendererHost.Logging;

namespace Martlet.Avatar.RendererHost;

/// <summary>Whether this PC can give the character's composition WebView2 the Direct3D 9 device it draws through. WebView2's
/// GraphicsItemD3DImage makes a Direct3D 11 device and then this one in its constructor; when the second fails (0x8876086A in a
/// session without Direct3D 9, issue #1083) the half-built image is left to its finalizer, which throws an
/// ObjectDisposedException on the finalizer thread and ends the renderer process. So the renderer makes the same device first,
/// with WebView2's own helper, and stops cleanly with the reason when it can't.</summary>
internal static class RendererGraphics
{
    /// <summary>FIXTURE for MCP verification, never a real failure: set to 1, the check says Direct3D 9 isn't available, so
    /// the clean stop can be shown on a PC where Direct3D 9 works. Martlet passes it on to the renderer.</summary>
    internal const string SimulateVariable = "MARTLET_SIMULATE_NO_DIRECT3D9";

    private static readonly MethodInfo? CreateD3D9Device = typeof(Microsoft.Web.WebView2.Wpf.WebView2CompositionControl).Assembly
        .GetType("Microsoft.Web.WebView2.Wpf.Direct3DHelper")
        ?.GetMethod("CreateD3D9Device", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, [typeof(IntPtr)]);

    /// <summary>Whether this WebView2 version has the helper the check calls (a WebView2 update could rename it).</summary>
    internal static bool CanCheck => CreateD3D9Device is not null;

    /// <summary>Why the Direct3D 9 device can't be made for <paramref name="window"/>, or null when it can (or when this WebView2
    /// version has no such helper, so there is nothing to check).</summary>
    internal static string? Direct3D9Problem(IntPtr window) =>
        Direct3D9Problem(window, Environment.GetEnvironmentVariable(SimulateVariable) == "1");

    internal static string? Direct3D9Problem(IntPtr window, bool simulated)
    {
        if (simulated)
        {
            ErrorLog.Warn($"FIXTURE: Direct3D 9 is treated as unavailable ({SimulateVariable}).");
            return $"Direct3D 9 isn't available in this Windows session (simulated by {SimulateVariable})";
        }
        if (CreateD3D9Device is null)
        {
            ErrorLog.Warn("WebView2 has no Direct3D 9 helper to check; the character draws without the check.");
            return null;
        }
        try
        {
            var device = CreateD3D9Device.Invoke(null, [window]);
            if (device is not null && Marshal.IsComObject(device)) Marshal.ReleaseComObject(device);
            return null;
        }
        catch (TargetInvocationException error) when (error.InnerException is COMException or InvalidCastException or DllNotFoundException
            or EntryPointNotFoundException)
        {
            var inner = error.InnerException!;
            return inner is COMException com
                ? $"Direct3D 9 isn't available in this Windows session (0x{com.HResult:X8})"
                : $"Direct3D 9 isn't available ({inner.GetType().Name})";
        }
    }
}
