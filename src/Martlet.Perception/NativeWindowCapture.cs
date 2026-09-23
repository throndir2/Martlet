using System.Security.Cryptography;

namespace Martlet.Perception;

internal interface INativeWindowCaptureFactory
{
    IReadOnlyList<NativeWindowSource> Enumerate(
        NativeEnumerationAccess access,
        CancellationToken cancellationToken);

    // This accepts only the exact source object returned by Enumerate. There is no
    // desktop, monitor, region, process-injection or fallback parameter.
    INativeWindowCaptureSession Open(
        NativeWindowSource source,
        NativeCaptureAccess access,
        CancellationToken cancellationToken);
}

internal interface INativeWindowCaptureSession : IDisposable
{
    NativeWindowFrame? ReadFrame(CancellationToken cancellationToken);
    void RequestStop();
}

internal sealed record NativeWindowSource(
    string ApplicationName,
    string WindowTitle)
{
    internal void Validate()
    {
        PerceptionGuard.SafeLabel(ApplicationName, 256);
        PerceptionGuard.SafeLabel(WindowTitle, 512);
    }

    public override string ToString() => "Native selected-window source (labels omitted)";
}

internal sealed class NativeWindowFrame : IDisposable
{
    private readonly object gate = new();
    private byte[]? pixels;

    internal int Width { get; }
    internal int Height { get; }
    internal WindowFrameFormat Format { get; }
    internal DateTimeOffset CapturedAtUtc { get; }
    internal int ByteCount
    {
        get
        {
            lock (gate)
                return pixels?.Length ?? 0;
        }
    }

    internal NativeWindowFrame(
        int width,
        int height,
        byte[] ownedPixels,
        DateTimeOffset capturedAtUtc,
        WindowFrameFormat format = WindowFrameFormat.Bgra32Premultiplied)
    {
        ArgumentNullException.ThrowIfNull(ownedPixels);
        Width = width;
        Height = height;
        Format = format;
        CapturedAtUtc = capturedAtUtc;
        pixels = ownedPixels;
    }

    internal void CopyPixelsTo(Span<byte> destination)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(pixels is null, this);
            if (destination.Length != pixels.Length)
                throw new PerceptionException(PerceptionFailureCode.MalformedFrame);
            pixels.CopyTo(destination);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (pixels is not null)
                CryptographicOperations.ZeroMemory(pixels);
            pixels = null;
        }
    }

    public override string ToString() =>
        $"Native window frame {{ Width = {Width}, Height = {Height}, ByteCount = {ByteCount}, Pixels = [redacted] }}";
}

internal sealed class NativeEnumerationAccess(Action check)
{
    internal void Check() => check();
}

internal sealed class NativeCaptureAccess(Action check, PerceptionOptions options)
{
    internal int MaximumLongestEdge => options.MaximumLongestEdge;
    internal int MaximumFrameBytes => options.MaximumFrameBytes;
    internal TimeSpan MinimumFrameInterval => options.FrameInterval;
    internal void Check() => check();
}

internal sealed class NativeWindowCaptureException : Exception
{
    internal PerceptionFailureCode Code { get; }

    internal NativeWindowCaptureException(PerceptionFailureCode code)
        : base("The native selected-window boundary reported a typed failure.")
    {
        PerceptionGuard.Require(code is PerceptionFailureCode.EnumerationFailed or
            PerceptionFailureCode.SourceUnavailable or PerceptionFailureCode.SourceClosed or
            PerceptionFailureCode.SourceChanged or PerceptionFailureCode.AccessDenied or
            PerceptionFailureCode.NativeCaptureFailed or PerceptionFailureCode.CancellationFailed or
            PerceptionFailureCode.CleanupFailed);
        Code = code;
    }
}
