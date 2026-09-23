using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Avatars;

public sealed record PlaybackBinding : IContract
{
    public required CorrelationIds Ids { get; init; }
    public required string SourceId { get; init; }
    public required long Epoch { get; init; }
    public required int SampleRate { get; init; }

    public void Validate()
    {
        ContractRules.Require(Ids is not null, "Playback correlation is required.");
        Ids!.Validate();
        ContractRules.Identifier(SourceId);
        AvatarValidation.Counter(Epoch);
        AvatarValidation.SampleRate(SampleRate);
    }

    public static PlaybackBinding FromPcm(PcmFrame frame, string sourceId)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var binding = new PlaybackBinding
        {
            Ids = frame.Ids, Epoch = frame.Epoch, SampleRate = frame.Format.SampleRate, SourceId = sourceId
        };
        binding.Validate();
        return binding;
    }
}

/// <summary>A device-rendered position in the original source PCM timebase, never a submitted sample count.</summary>
public sealed record PlaybackPosition : IContract
{
    public required CorrelationIds Ids { get; init; }
    public required long Epoch { get; init; }
    public required int SampleRate { get; init; }
    public required long SampleOffset { get; init; }

    public void Validate()
    {
        ContractRules.Require(Ids is not null, "Playback correlation is required.");
        Ids!.Validate();
        AvatarValidation.Counter(Epoch);
        AvatarValidation.SampleRate(SampleRate);
        ContractRules.Require(SampleOffset is >= 0 and <= AvatarValidation.MaxSampleOffset,
            "Playback position is out of range.");
    }
}

public enum FrameDisposition
{
    Accepted, PlaybackUnavailable, WrongBinding, Stopped, OutOfOrder, PlaybackRegressed,
    TooLate, Future, TooFarAhead
}

/// <summary>Serialized per-source gate. It does not queue frames or own a playback device.</summary>
public sealed class PlaybackFrameGate
{
    private PlaybackBinding binding;
    private readonly int maximumLatenessMilliseconds;
    private readonly int maximumLeadMilliseconds;
    private long lastSequence = -1;
    private long lastFrameOffset = -1;
    private long lastPlaybackOffset = -1;
    private bool stopped;

    public PlaybackFrameGate(PlaybackBinding binding, int maximumLatenessMilliseconds = 250,
        int maximumLeadMilliseconds = 1000)
    {
        binding.Validate();
        ContractRules.Require(maximumLatenessMilliseconds is >= 0 and <= 1000 &&
            maximumLeadMilliseconds is >= 0 and <= 5000, "Avatar timing bounds are invalid.");
        this.binding = binding;
        this.maximumLatenessMilliseconds = maximumLatenessMilliseconds;
        this.maximumLeadMilliseconds = maximumLeadMilliseconds;
    }

    public FrameDisposition TryAccept(AvatarFrame frame, PlaybackPosition? position)
    {
        frame.Validate();
        if (stopped) return FrameDisposition.Stopped;
        if (frame.Ids != binding.Ids || frame.SourceId != binding.SourceId ||
            frame.Epoch != binding.Epoch || frame.SampleRate != binding.SampleRate)
            return FrameDisposition.WrongBinding;
        if (position is null) return FrameDisposition.PlaybackUnavailable;
        position.Validate();
        if (position.Ids != binding.Ids || position.Epoch != binding.Epoch || position.SampleRate != binding.SampleRate)
            return FrameDisposition.WrongBinding;
        if (position.SampleOffset < lastPlaybackOffset) return FrameDisposition.PlaybackRegressed;
        lastPlaybackOffset = position.SampleOffset;
        if (frame.Sequence <= lastSequence || frame.SampleOffset < lastFrameOffset)
            return FrameDisposition.OutOfOrder;
        var difference = frame.SampleOffset - position.SampleOffset;
        if (difference > (long)binding.SampleRate * maximumLeadMilliseconds / 1000)
            return FrameDisposition.TooFarAhead;
        if (difference > 0) return FrameDisposition.Future;
        if (-difference > (long)binding.SampleRate * maximumLatenessMilliseconds / 1000)
            return FrameDisposition.TooLate;
        lastSequence = frame.Sequence;
        lastFrameOffset = frame.SampleOffset;
        return FrameDisposition.Accepted;
    }

    public void Stop() => stopped = true;

    public void Reset(PlaybackBinding nextBinding)
    {
        nextBinding.Validate();
        ContractRules.Require(nextBinding.Ids != binding.Ids || nextBinding.Epoch > binding.Epoch,
            "Reset needs new correlation or a newer epoch; stopped playback cannot be revived.");
        binding = nextBinding;
        lastSequence = lastFrameOffset = lastPlaybackOffset = -1;
        stopped = false;
    }
}

public static class SampleClock
{
    public static long ConvertOffset(long sampleOffset, int fromSampleRate, int toSampleRate)
    {
        AvatarValidation.SampleRate(fromSampleRate);
        AvatarValidation.SampleRate(toSampleRate);
        ContractRules.Require(sampleOffset is >= 0 and <= AvatarValidation.MaxSampleOffset, "Sample offset is out of range.");
        var converted = (Int128)sampleOffset * toSampleRate / fromSampleRate;
        ContractRules.Require(converted <= AvatarValidation.MaxSampleOffset, "Converted sample offset is out of range.");
        return (long)converted;
    }
}
