using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Martlet.Gateway.Persistence.PersistenceFailure;

namespace Martlet.Gateway.Persistence;

[SupportedOSPlatform("windows")]
internal static class WindowsBootIdentity
{
    internal static Guid Read()
    {
        // Windows QPC is comparable across processes, but only within the same OS boot.
        if (NtQuerySystemInformation(90, out var information, Marshal.SizeOf<BootEnvironment>(),
            out var returned) != 0 || returned != Marshal.SizeOf<BootEnvironment>() ||
            information.Identifier == Guid.Empty)
            throw Error(GatewayPersistenceFailure.UnsupportedPlatform);
        return information.Identifier;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BootEnvironment
    {
        internal Guid Identifier;
        internal uint FirmwareType;
        internal ulong BootFlags;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass,
        out BootEnvironment information, int length, out int returnedLength);
}
