using System.Buffers.Binary;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Audio.Windows;

public sealed record SpeakerSmokeResult(bool PlaybackRequested, PlaybackSnapshot? Playback);

public static class SpeakerSmoke
{
    public static async Task<SpeakerSmokeResult> RunAsync(OutputSelection? output = null,
        bool allowPhysicalPlayback = false, CancellationToken cancellationToken = default)
    {
        if (!allowPhysicalPlayback)
            return new(false, null);
        ArgumentNullException.ThrowIfNull(output);
        output.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var format = new PcmFormat { SampleRate = 24000, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian };
        var ids = new CorrelationIds { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };
        await using var sink = new PcmPlaybackSink(new WasapiDeviceFactory());
        var run = sink.Start(new(ids, 0, format, output, DateTimeOffset.UtcNow.AddSeconds(5)), cancellationToken);
        if (await run.Ready.ConfigureAwait(false) is null)
            return new(true, await run.Completion.ConfigureAwait(false));
        for (var frame = 0; frame < 2; frame++)
        {
            var bytes = new byte[4800];
            for (var sample = 0; sample < 2400; sample++)
            {
                var position = frame * 2400 + sample;
                var fade = Math.Min(1.0, Math.Min(position, 4799 - position) / 240.0);
                var value = (short)(655 * fade * Math.Sin(2 * Math.PI * 440 * position / 24000));
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(sample * 2), value);
            }
            if (run.Submit(new(ids, 0, frame, frame * 2400, format, bytes)) != FrameAcceptance.Accepted)
                return new(true, await run.Completion.ConfigureAwait(false));
        }
        run.CompleteInput(4800);
        return new(true, await run.Completion.ConfigureAwait(false));
    }
}
