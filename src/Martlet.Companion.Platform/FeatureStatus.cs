namespace Martlet.Companion.Platform;

/// <summary>Whether a platform service works on this computer right now, and in plain words why not. Every service
/// exposes one so the app (and its headless status) can say why a feature is missing instead of failing silently.</summary>
public sealed record FeatureStatus(bool Available, string Reason)
{
    public static FeatureStatus Yes(string note = "") => new(true, note);
    public static FeatureStatus No(string reason) => new(false, reason);

    /// <summary>The default for services the Linux (DX02) or macOS (DX03) integration has not provided yet.</summary>
    public static FeatureStatus NotBuilt(string feature) =>
        new(false, $"{feature} isn't built for this system yet (docs/DESKTOP_LINUX_MACOS.md).");
}
