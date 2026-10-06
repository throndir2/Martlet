namespace Martlet.Companion.Platform;

/// <summary>One look at the screen: a JPEG and its size.</summary>
public sealed record ScreenShot(byte[] Jpeg, int Width, int Height, DateTimeOffset TakenAt);

/// <summary>How to take one look. Images are scaled so the long edge is at most <see cref="MaxLongEdge"/>.</summary>
public sealed record ScreenCaptureRequest(int MaxLongEdge = 1280, int JpegQuality = 80);

/// <summary>"Watch my screen": one JPEG per look, never a stream. Consent-gated twice: the app only calls it after
/// the user turned watching on in Martlet, and <see cref="RequestConsentAsync"/> asks the system (macOS Screen
/// Recording permission via ScreenCaptureKit; Wayland ScreenCast portal + PipeWire). Nothing here may start at
/// launch or by itself. Linux X11: XGetImage/XShm of the root window.</summary>
public interface IScreenCapture
{
    FeatureStatus Status { get; }

    /// <summary>Asks for the system permission if needed. Only call from an explicit user action.</summary>
    Task<FeatureStatus> RequestConsentAsync(CancellationToken cancellationToken);

    /// <summary>Takes one look. Returns null when consent is missing or was withdrawn; never prompts by itself.</summary>
    Task<ScreenShot?> CaptureAsync(ScreenCaptureRequest request, CancellationToken cancellationToken);

    /// <summary>Ends any portal/ScreenCaptureKit session the consent opened (watching turned off).</summary>
    void Release();
}
