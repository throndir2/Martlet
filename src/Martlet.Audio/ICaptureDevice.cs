namespace Martlet.Audio;

public interface ICaptureDeviceFactory
{
    // All native work belongs to the caller's single dedicated worker, including failed-open cleanup.
    ICaptureDevice Open(CaptureDeviceAccess access, CancellationToken cancellationToken);
}

public readonly record struct CapturePacket(int ByteCount, bool Discontinuity = false);

public interface ICaptureDevice : IDisposable
{
    CaptureSourceFormat Format { get; }
    void Start(CancellationToken cancellationToken);
    CapturePacket Read(Span<byte> destination, CancellationToken cancellationToken);
    void Stop();
}

public sealed class CaptureDeviceAccess
{
    private readonly Action check;
    public InputSelection Input { get; }

    internal CaptureDeviceAccess(InputSelection input, Action check)
    {
        Input = input;
        this.check = check;
    }

    // Required immediately before native enumeration/activation/start, including after a blocking call.
    public void CheckAuthorization() => check();
}
