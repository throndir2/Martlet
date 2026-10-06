using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Martlet.Desktop;

/// <summary>A Windows power request that keeps this PC from sleeping when it is left idle (PowerRequestSystemRequired), as
/// downloads and media players do: the screen can still turn off, and Sleep, the power button, closing a laptop's lid and
/// shutting down still work. An administrator's <c>powercfg /requests</c> lists it under SYSTEM with its reason. Windows
/// drops it when Martlet exits.</summary>
internal sealed class PowerRequest : IDisposable
{
    private const int PowerRequestSystemRequired = 1;
    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 1;
    private nint handle;

    /// <summary>The reason the request is held with, or null while it isn't.</summary>
    internal string? Reason { get; private set; }

    /// <summary>Holds the request with <paramref name="reason"/> (a new reason replaces the old one with no gap), or releases
    /// it for null. Throws <see cref="Win32Exception"/> when Windows refuses; the previous request then stays as it was.</summary>
    internal void Hold(string? reason)
    {
        if (reason == Reason) return;
        nint next = 0;
        if (reason is not null)
        {
            var context = new ReasonContext
            {
                Version = PowerRequestContextVersion, Flags = PowerRequestContextSimpleString, SimpleReasonString = reason
            };
            next = PowerCreateRequest(ref context);
            if (next == 0 || next == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!PowerSetRequest(next, PowerRequestSystemRequired))
            {
                var error = Marshal.GetLastWin32Error();
                CloseHandle(next);
                throw new Win32Exception(error);
            }
        }
        Release();
        handle = next;
        Reason = reason;
    }

    public void Dispose() => Release();

    private void Release()
    {
        if (handle != 0)
        {
            PowerClearRequest(handle, PowerRequestSystemRequired);
            CloseHandle(handle);
            handle = 0;
        }
        Reason = null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(nint request, int type);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(nint request, int type);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
