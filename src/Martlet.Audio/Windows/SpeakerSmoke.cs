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
        var ids = new CorrelationIds { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };
        await using var sink = new PcmPlaybackSink(new WasapiDeviceFactory());
        var run = sink.Start(new(ids, 0, SyntheticTone.Format, output, DateTimeOffset.UtcNow.AddSeconds(5)), cancellationToken);
        if (await run.Ready.ConfigureAwait(false) is null)
            return new(true, await run.Completion.ConfigureAwait(false));
        foreach (var frame in SyntheticTone.Frames(ids, 0))
        {
            if (run.Submit(frame) != FrameAcceptance.Accepted)
                return new(true, await run.Completion.ConfigureAwait(false));
        }
        run.CompleteInput(SyntheticTone.SampleCount);
        return new(true, await run.Completion.ConfigureAwait(false));
    }
}
