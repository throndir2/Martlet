namespace Martlet.Audio;

public interface ICaptureDeviceFactory
{
    // All native work belongs to the caller's single dedicated worker, including failed-open cleanup.
    ICaptureDevice Open(CaptureDeviceAccess access, CancellationToken cancellationToken);
}

// Timestamp: when the device recorded the packet's first frame, on the performance counter in 100 ns units (WASAPI's QPC
// position), or null when the device reports none. Echo reduction lines microphone and speaker audio up by it.
public readonly record struct CapturePacket(int ByteCount, bool Discontinuity = false, long? Timestamp = null);

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
