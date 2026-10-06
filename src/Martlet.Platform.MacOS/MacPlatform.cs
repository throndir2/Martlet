using System.Runtime.Versioning;
using Martlet.Companion.Platform;

namespace Martlet.Platform.MacOS;

/// <summary>The macOS platform services. DX03 replaces the defaults here one by one
/// (<c>CompanionPlatform.Defaults() with { Credentials = new KeychainStore() }</c>); Martlet.Companion calls
/// <see cref="Create"/> when <see cref="OperatingSystem.IsMacOS"/> is true.</summary>
[SupportedOSPlatform("macos")]
public static class MacPlatform
{
    public static CompanionPlatform Create() => CompanionPlatform.Defaults();
}
