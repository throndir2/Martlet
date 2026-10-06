using Martlet.Companion.Platform;

namespace Martlet.Platform.Linux.Capture;

/// <summary>"Watch my screen" on Linux. X11 sessions read the screen directly once the user turned watching on. Wayland
/// sessions ask the system through the ScreenCast portal (or, without PipeWire, the Screenshot portal). Nothing starts by
/// itself: <see cref="CaptureAsync"/> returns null until <see cref="RequestConsentAsync"/> succeeded, and
/// <see cref="Release"/> ends the portal session.</summary>
internal sealed class LinuxScreenCapture(LinuxEnvironment environment) : IScreenCapture
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private ScreenCastSession? cast;
    private volatile bool consented;

    public FeatureStatus Status { get; private set; } = environment.CaptureStatus;

    public async Task<FeatureStatus> RequestConsentAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            switch (environment.CaptureRoute)
            {
                case CaptureRoute.X11:
                case CaptureRoute.ScreenshotPortal:
                    consented = true;
                    return Status = environment.CaptureStatus;
                case CaptureRoute.ScreenCastPortal:
                    if (cast is { IsOpen: true }) return Status;
                    cast?.Dispose();
                    var (session, status) = await ScreenCastSession.StartAsync(cancellationToken).ConfigureAwait(false);
                    cast = session;
                    consented = session is not null;
                    return Status = status;
                default:
                    return Status = environment.CaptureStatus;
            }
        }
        finally { gate.Release(); }
    }

    public async Task<ScreenShot?> CaptureAsync(ScreenCaptureRequest request, CancellationToken cancellationToken)
    {
        if (!consented) return null;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            switch (environment.CaptureRoute)
            {
                case CaptureRoute.X11:
                    return await Task.Run(() => X11Capture.Capture(request), cancellationToken).ConfigureAwait(false);
                case CaptureRoute.ScreenshotPortal:
                    return await ScreenshotPortal.CaptureAsync(request, cancellationToken).ConfigureAwait(false);
                case CaptureRoute.ScreenCastPortal:
                    if (cast is not { IsOpen: true })
                    {
                        consented = false;
                        Status = FeatureStatus.No("Screen sharing was stopped from the system; turn watching on again to pick a screen.");
                        return null;
                    }
                    return await cast.CaptureAsync(request, cancellationToken).ConfigureAwait(false);
                default:
                    return null;
            }
        }
        finally { gate.Release(); }
    }

    public void Release()
    {
        gate.Wait();
        try
        {
            consented = false;
            cast?.Dispose();
            cast = null;
            Status = environment.CaptureStatus;
        }
        finally { gate.Release(); }
    }
}
