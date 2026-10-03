using Martlet.Core.Contracts;

namespace Martlet.Audio;

/// <summary>What this PC plays, as a loopback (see <see cref="IEchoReference"/>: opened, read, stopped and disposed on the
/// capture's own worker thread; Read never blocks and returns nothing while nothing plays). <see cref="WithoutMartlet"/>: Martlet's
/// own sound (its voice) is left out, so listening to the PC never hears Martlet itself.</summary>
public interface IPcAudioSource : IEchoReference
{
    bool WithoutMartlet { get; }
}

public interface IPcAudioSourceFactory
{
    IPcAudioSource Open(CancellationToken cancellationToken);
}

/// <summary>Companion › Listening › Hear what this PC plays: what the PC plays (a video, a stream, a call, a game) as a capture
/// device for always listening, beside the microphone. A loopback delivers nothing while nothing plays, so the gaps are filled
/// with silence on the clock: the stream stays continuous and voice activity hears a video pause or go quiet as a pause, which
/// ends the utterance. The audio only goes where the microphone's would (speech-to-text), never to Voice ID, voice recognition
/// or memory, and is never kept.</summary>
public sealed class PcAudioCaptureFactory(IPcAudioSourceFactory sources, TimeProvider? clock = null) : ICaptureDeviceFactory
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private int withoutMartlet = -1;

    /// <summary>Whether the last opened source left Martlet's own sound out; null until one opened.</summary>
    public bool? WithoutMartlet => Volatile.Read(ref withoutMartlet) switch { 0 => false, 1 => true, _ => null };

    public ICaptureDevice Open(CaptureDeviceAccess access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        access.CheckAuthorization();
        cancellationToken.ThrowIfCancellationRequested();
        var source = sources.Open(cancellationToken);
        Volatile.Write(ref withoutMartlet, source.WithoutMartlet ? 1 : 0);
        return new PcAudioDevice(source, time);
    }
}

/// <summary>One continuous stream of what the PC plays: real packets as they come, and silence for any stretch the loopback
/// left empty once it is <see cref="Slack"/> overdue (a real packet never waits that long while something plays).</summary>
internal sealed class PcAudioDevice(IPcAudioSource source, TimeProvider clock) : ICaptureDevice
{
    internal static TimeSpan Slack => TimeSpan.FromMilliseconds(120);
    private long started, frames;
    private bool running;

    public CaptureSourceFormat Format => source.Format;

    public void Start(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        source.Start();
        started = clock.GetTimestamp();
        running = true;
    }

    public CapturePacket Read(Span<byte> destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!running) return new(0);
        var format = source.Format;
        var packet = source.Read(destination);
        if (packet.ByteCount < 0 || packet.ByteCount > destination.Length || packet.ByteCount % format.BlockAlignment != 0)
            throw new CaptureDeviceException(ErrorCode.PayloadTooLarge);
        // A gap the loopback reports is already filled with silence by time, so the stream never looks truncated.
        if (packet.ByteCount > 0)
        {
            frames += packet.ByteCount / format.BlockAlignment;
            return new(packet.ByteCount);
        }
        var elapsed = clock.GetElapsedTime(started) - Slack;
        if (elapsed <= TimeSpan.Zero) return new(0);
        var due = (long)(elapsed.Ticks * (double)format.SampleRate / TimeSpan.TicksPerSecond);
        var missing = due - frames;
        if (missing <= 0) return new(0);
        var count = (int)Math.Min(missing, Math.Min(format.SampleRate / 10, destination.Length / format.BlockAlignment));
        var bytes = count * format.BlockAlignment;
        destination[..bytes].Clear();
        frames += count;
        return new(bytes);
    }

    public void Stop()
    {
        running = false;
        source.Stop();
    }

    public void Dispose() => source.Dispose();
}
