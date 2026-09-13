using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Audio;

public enum OutputPolicy { FixedEndpoint, DefaultAtStart }

public sealed record OutputSelection(OutputPolicy Policy, string? EndpointId = null)
{
    public void Validate()
    {
        ContractRules.Defined(Policy);
        ContractRules.Require(Policy == OutputPolicy.FixedEndpoint
            ? EndpointId is { Length: > 0 and <= 1024 } && !string.IsNullOrWhiteSpace(EndpointId) && !EndpointId.Any(char.IsControl)
            : EndpointId is null, "Select a fixed output endpoint or explicitly select the default at start.");
    }
}

public sealed record PlaybackRequest(
    CorrelationIds Ids, long Epoch, PcmFormat Format, OutputSelection Output, DateTimeOffset Deadline)
{
    internal void Validate(DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(Ids);
        ArgumentNullException.ThrowIfNull(Format);
        ArgumentNullException.ThrowIfNull(Output);
        Ids.Validate();
        Format.Validate();
        Output.Validate();
        ContractRules.Require(Epoch is >= 0 and <= int.MaxValue, "Playback epoch is out of range.");
        ContractRules.Require(Deadline > now && Deadline <= now.AddSeconds(90),
            "Playback needs a future deadline no more than 90 seconds away.", ErrorCode.DeadlineExceeded);
    }
}

public sealed record PlaybackOptions
{
    public TimeSpan Capacity { get; init; } = TimeSpan.FromSeconds(5);
    public int MaximumQueuedFrames { get; init; } = 256;
    public TimeSpan Prebuffer { get; init; } = TimeSpan.FromMilliseconds(150);
    public TimeSpan FirstAudioTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan UnderrunTimeout { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(2);

    internal void Validate()
    {
        ContractRules.Require(Capacity >= TimeSpan.FromMilliseconds(100) && Capacity <= TimeSpan.FromSeconds(5),
            "Playback capacity must be 100 ms through 5 seconds.");
        ContractRules.Require(MaximumQueuedFrames is >= 1 and <= 256, "Playback frame capacity must be 1 through 256.");
        ContractRules.Require(Prebuffer >= TimeSpan.Zero && Prebuffer <= Capacity, "Prebuffer must fit the playback capacity.");
        ContractRules.Require(FirstAudioTimeout > TimeSpan.Zero && FirstAudioTimeout <= TimeSpan.FromSeconds(20),
            "First audio timeout must be positive and at most 20 seconds.");
        ContractRules.Require(UnderrunTimeout > TimeSpan.Zero && UnderrunTimeout <= TimeSpan.FromSeconds(1),
            "Underrun recovery must be positive and at most 1 second.");
        ContractRules.Require(ShutdownTimeout > TimeSpan.Zero && ShutdownTimeout <= TimeSpan.FromSeconds(2),
            "Shutdown timeout must be positive and at most 2 seconds.");
    }
}

public enum FrameAcceptance { Accepted, DuplicateDiscarded, StaleDiscarded }
public enum PlaybackState { Starting, Buffering, Playing, Underrun, Stopping, Completed, Canceled, Replaced, Failed }
public enum PlaybackEventKind { Starting, Bound, Progress, Underrun, Resumed, InputCompleted, StopRequested, Terminal, DeviceReleased, LateDeviceFailure }
public enum DeviceSampleEncoding { IntegerPcm, IeeeFloat }

// Native mix-format metadata, not a second PCM input contract. Endpoint IDs/names are not diagnostic events.
public sealed record PlaybackDeviceInfo(int MixSampleRate, int MixChannels, int MixBitsPerSample,
    DeviceSampleEncoding MixEncoding, int BufferCapacitySamples, bool UsesSystemConversion);

public sealed record PlaybackSnapshot(
    CorrelationIds Ids, long Epoch, PlaybackState State,
    long AcceptedSamples, long ReadSamples, long SubmittedSamples, long DeviceConsumedSamples,
    long QueuedSamples, int QueuedFrames, bool DeviceDrainObserved,
    bool DeviceReleased, long DroppedEvents, MartletError? Error)
{
    public bool MayHavePlayed => ReadSamples > 0;
    public long? AudibleSamples => null;
}

public sealed record PlaybackEvent(long Sequence, DateTimeOffset Timestamp, TimeSpan Elapsed,
    PlaybackEventKind Kind, PlaybackSnapshot Snapshot, PlaybackDeviceInfo? Device = null);

public static class PlaybackErrors
{
    public static MartletError Create(ErrorCode code) => code switch
    {
        ErrorCode.AudioDeviceUnavailable => Error(code, "The selected output is unavailable. Select an available output and start explicitly.", "audio.select_output"),
        ErrorCode.AudioDeviceLost => Error(code, "The output was lost or changed. Playback stopped; select an output before retrying.", "audio.select_output"),
        ErrorCode.AudioFormatUnsupported => Error(code, "The output cannot safely accept this audio format. Check the output format before retrying.", "audio.check_format"),
        ErrorCode.DeadlineExceeded => Error(code, "Playback exceeded its deadline. Check the stream or device before an explicit retry.", "audio.check_deadline"),
        ErrorCode.PayloadTooLarge => Error(code, "Playback exceeded its bounded capacity. Pace the producer before retrying.", "audio.pace_producer"),
        ErrorCode.StreamTruncated => Error(code, "The audio stream ended incorrectly or underrun recovery expired. Playback may be partial.", "audio.check_stream"),
        ErrorCode.InvalidContract => Error(code, "The audio sequence, correlation or format is invalid. Correct the stream before retrying.", "audio.check_stream"),
        ErrorCode.AudioPlaybackFailed => Error(code, "Playback or device cleanup failed. Do not retry until the previous device has been released.", "audio.check_device"),
        _ => throw new ArgumentOutOfRangeException(nameof(code))
    };

    private static MartletError Error(ErrorCode code, string summary, string action) => new()
    {
        Code = code, Stage = Stage.Playback, Retryable = false, Summary = summary, ActionId = action
    };
}
