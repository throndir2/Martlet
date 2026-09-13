using Martlet.Core.Audio;

namespace Martlet.Audio;

// All methods, including Open/Dispose, run on one non-UI worker. No callback may reopen an endpoint.
public interface IPlaybackDeviceFactory
{
    // A failed Open must release any partially acquired resources before throwing.
    IPlaybackDevice Open(OutputSelection selection, PcmFormat format, CancellationToken cancellationToken);
}

public interface IPlaybackDevice : IDisposable
{
    PlaybackDeviceInfo Info { get; }
    int GetPadding(CancellationToken cancellationToken);
    // Returns committed samples per channel, not bytes. A partial write leaves the remainder with the pump.
    int Write(ReadOnlySpan<byte> pcm, CancellationToken cancellationToken);
    void Start(CancellationToken cancellationToken);
    void StopAndReset();
}
