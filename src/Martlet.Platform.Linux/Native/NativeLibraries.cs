using System.Runtime.InteropServices;

namespace Martlet.Platform.Linux.Native;

internal static class NativeLibraries
{
    public static bool HasX11 => OperatingSystem.IsLinux() && Loads("libX11.so.6") && Loads("libXext.so.6");
    public static bool HasPipeWire => OperatingSystem.IsLinux() && Loads(PipeWire.Lib);

    private static bool Loads(string name)
    {
        if (!NativeLibrary.TryLoad(name, out var handle)) return false;
        NativeLibrary.Free(handle);
        return true;
    }
}
