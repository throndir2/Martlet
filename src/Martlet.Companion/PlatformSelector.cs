using Martlet.Companion.Platform;
using Martlet.Platform.Linux;
using Martlet.Platform.MacOS;

namespace Martlet.Companion;

/// <summary>Picks this system's platform services without reflection: Linux and macOS integrations are referenced
/// directly and chosen by the running OS. Any other system (a Windows dev run) gets the portable defaults.</summary>
public static class PlatformSelector
{
    public static CompanionPlatform Create()
    {
        if (OperatingSystem.IsLinux()) return LinuxPlatform.Create();
        if (OperatingSystem.IsMacOS()) return MacPlatform.Create();
        return CompanionPlatform.Defaults();
    }
}
