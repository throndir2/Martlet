using System.Runtime.Versioning;
using Martlet.Companion.Platform;

namespace Martlet.Platform.Linux;

/// <summary>The Linux platform services. DX02 replaces the defaults here one by one
/// (<c>CompanionPlatform.Defaults() with { Hotkey = new X11Hotkey(...) }</c>); Martlet.Companion calls
/// <see cref="Create"/> when <see cref="OperatingSystem.IsLinux"/> is true.</summary>
[SupportedOSPlatform("linux")]
public static class LinuxPlatform
{
    public static CompanionPlatform Create() => CompanionPlatform.Defaults();
}
