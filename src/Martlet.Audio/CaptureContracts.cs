using System.Buffers.Binary;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Audio;

public enum InputPolicy { FixedEndpoint, FollowDefaultOnNextPress }

// Selection is private UI/control data, never included in diagnostic events.
public sealed record InputSelection(InputPolicy Policy, string? EndpointId = null)
{
    public void Validate()
    {
        ContractRules.Defined(Policy);
        ContractRules.Require(Policy == InputPolicy.FixedEndpoint
            ? EndpointId is { Length: > 0 and <= 1024 } && !string.IsNullOrWhiteSpace(EndpointId) && !EndpointId.Any(char.IsControl)
            : EndpointId is null, "Select a fixed input or deliberately follow the default on the next press.");
    }
}

public sealed record CaptureRequest(CorrelationIds Ids, long Epoch, InputSelection Input,
    TimeSpan MaximumDuration, DateTimeOffset ExpiresAt)
{
    internal void Validate(Guid sessionId, DateTimeOffset now, CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(Ids);
        ArgumentNullException.ThrowIfNull(Input);
        Ids.Validate();
        Input.Validate();
        ContractRules.Require(Ids.SessionId == sessionId && Epoch is >= 0 and <= int.MaxValue,
            "Capture must belong to the current session and a valid epoch.");
        ContractRules.Require(MaximumDuration >= TimeSpan.FromMilliseconds(20) && MaximumDuration <= options.MaximumDuration,
            "Capture duration must be between 20 ms and the configured maximum.");
        ContractRules.Require(ExpiresAt > now && ExpiresAt <= now.AddSeconds(30),
            "Capture authorization needs a future expiry at most 30 seconds away.", ErrorCode.DeadlineExceeded);
    }
}

// An assertion by the owning UI, not a Windows permission grant or a persisted preference.
public sealed record CaptureAuthorization(CaptureRequest Request, bool MicrophoneCaptureRequested = false);

public sealed record CaptureOptions
{
    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromSeconds(30);
    public int MaximumPcmBytes { get; init; } = 2 * 1024 * 1024;
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(2);

    internal void Validate()
    {
        ContractRules.Require(MaximumDuration >= TimeSpan.FromMilliseconds(20) && MaximumDuration <= TimeSpan.FromSeconds(30),
            "Maximum capture duration must be 20 ms through 30 seconds.");
        ContractRules.Require(MaximumPcmBytes is >= 2 and <= 2 * 1024 * 1024 && MaximumPcmBytes % 2 == 0,
            "Capture capacity must be aligned PCM16, at most 2 MiB.");
        ContractRules.Require(ShutdownTimeout > TimeSpan.Zero && ShutdownTimeout <= TimeSpan.FromSeconds(2),
            "Capture shutdown timeout must be positive and at most two seconds.");
    }
}

public enum CaptureState { Starting, Capturing, Stopping, Completed, NoFrames, Canceled, Failed }
public enum CaptureEndReason { Released, DurationLimit, ByteLimit, Stopped, Muted, Paused, Locked, Disposed, CallerCanceled, DeviceFailure, AuthorizationExpired }
public enum CaptureEventKind { Starting, Bound, Meter, StopRequested, Terminal }
public enum CaptureArmingDecision { Allowed, OwnOutputSuppressed, PlaybackStopRequired }

public static class CaptureArming
{
    // The coordinator must stop only its own output, await its release/tail, then signal inactive.
    public static CaptureArmingDecision Evaluate(bool manualPushToTalk, bool ownOutputActive) =>
        !ownOutputActive ? CaptureArmingDecision.Allowed
        : manualPushToTalk ? CaptureArmingDecision.PlaybackStopRequired : CaptureArmingDecision.OwnOutputSuppressed;
}

// Native source metadata. Core PcmFormat remains the only downstream PCM contract.
public sealed record CaptureSourceFormat(int SampleRate, int Channels, int BitsPerSample, DeviceSampleEncoding Encoding)
{
    public int BlockAlignment => Channels * (BitsPerSample / 8);
    public int MaximumPacketBytes => SampleRate / 10 * BlockAlignment;

    public void Validate()
    {
        if (SampleRate is not (16000 or 24000 or 32000 or 44100 or 48000 or 96000)
            || Channels is < 1 or > 8
            || !((Encoding == DeviceSampleEncoding.IntegerPcm && BitsPerSample == 16)
                || (Encoding == DeviceSampleEncoding.IeeeFloat && BitsPerSample == 32)))
            throw new CaptureDeviceException(ErrorCode.AudioFormatUnsupported);
    }
}

// FailureDetail says why the device failed, for the local log only: never a device name or ID, or anything heard.
public sealed record CaptureSnapshot(CorrelationIds Ids, long Epoch, CaptureState State,
    CaptureEndReason? EndReason, long SourceSamples, long CanonicalSamples, int RetainedPcmBytes,
    long DroppedEvents, MartletError? Error, string? FailureDetail = null);

public sealed record CaptureEvent(long Sequence, TimeSpan Elapsed, CaptureEventKind Kind,
    CaptureSnapshot Snapshot, CaptureSourceFormat? SourceFormat = null, double? Peak = null, double? Rms = null);

public sealed record CaptureDeviceRelease(bool Released, MartletError? Error);

public readonly record struct PcmAmplitude(double Peak, double Rms, long SamplesAtOrAboveThreshold);

public static class CaptureErrors
{
    public static MartletError Create(ErrorCode code) => new()
    {
        Code = code, Stage = Stage.Capture, Retryable = false,
        Summary = code switch
        {
            ErrorCode.NotConfigured => "Explicit microphone authorization is missing or does not match this turn.",
            ErrorCode.AudioAccessDenied => "Windows denied microphone access. Review microphone privacy settings before retrying.",
            ErrorCode.AudioDeviceBusy => "The selected microphone is busy. Release the competing client before retrying.",
            ErrorCode.AudioDeviceUnavailable => "The selected microphone is unavailable. Select an available input.",
            ErrorCode.AudioDeviceLost => "The microphone was lost. Select an input and press again; capture will not restart itself.",
            ErrorCode.AudioDeviceChanged => "The input or its format changed. Review the selection and press again.",
            ErrorCode.AudioFormatUnsupported => "The input format is unsupported. Select a supported PCM16 or float32 input.",
            ErrorCode.StreamTruncated => "Input samples were lost or incomplete. The utterance was discarded.",
            ErrorCode.PayloadTooLarge => "The input packet exceeded its bounded capacity. The utterance was discarded.",
            ErrorCode.DeadlineExceeded => "Microphone authorization expired. Discard the utterance and request a new capture.",
            ErrorCode.AudioCaptureFailed => "Capture or resource cleanup failed. Await device release before retrying.",
            _ => throw new ArgumentOutOfRangeException(nameof(code))
        },
        ActionId = code switch
        {
            ErrorCode.NotConfigured => "audio.authorize_input",
            ErrorCode.AudioAccessDenied => "audio.check_privacy",
            ErrorCode.AudioDeviceBusy => "audio.release_input",
            ErrorCode.AudioFormatUnsupported => "audio.check_format",
            ErrorCode.AudioDeviceUnavailable or ErrorCode.AudioDeviceLost or ErrorCode.AudioDeviceChanged => "audio.select_input",
            _ => "audio.check_capture"
        }
    };

    internal static MartletError Normalize(Exception exception) =>
        exception is CaptureDeviceException failure ? Create(failure.Code) : Create(ErrorCode.AudioCaptureFailed);
}

public sealed class CaptureDeviceException : Exception
{
    public ErrorCode Code { get; }
    public bool ResourcesReleased { get; }
    /// <summary>What went wrong, for the local log only: never a device name or ID, or anything heard.</summary>
    public string? Detail { get; }

    public CaptureDeviceException(ErrorCode code, bool resourcesReleased = true, string? detail = null)
        : base(CaptureErrors.Create(code).Summary)
    {
        Code = code;
        ResourcesReleased = resourcesReleased;
        Detail = detail;
    }
}

public sealed class CapturedUtterance : IDisposable
{
    private readonly object gate = new();
    private byte[]? pcm;
    public CorrelationIds Ids { get; }
    public long Epoch { get; }
    public CaptureSourceFormat SourceFormat { get; }
    public long SourceSamples { get; }
    public int SampleCount { get; }
    public int ByteCount => SampleCount * 2;
    public static PcmFormat Format { get; } = new()
    {
        SampleRate = 16000, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian
    };

    internal CapturedUtterance(CaptureRequest request, CaptureSourceFormat source, long sourceSamples, byte[] ownedPcm, int byteCount)
    {
        Ids = request.Ids;
        Epoch = request.Epoch;
        SourceFormat = source;
        SourceSamples = sourceSamples;
        pcm = ownedPcm;
        SampleCount = byteCount / 2;
    }

    public void CopyPcmTo(Span<byte> destination)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(pcm is null, this);
            pcm.AsSpan(0, ByteCount).CopyTo(destination);
        }
    }

    // Only metadata leaves the owned canonical PCM lease; no additional audio buffer is allocated.
    public PcmAmplitude MeasureAmplitude(double threshold)
    {
        if (!double.IsFinite(threshold) || threshold is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(pcm is null, this);
            double peak = 0, squares = 0;
            long aboveThreshold = 0;
            for (var i = 0; i < SampleCount; i++)
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2, 2)) / 32768.0;
                var level = Math.Abs(sample);
                peak = Math.Max(peak, level);
                squares += sample * sample;
                if (level >= threshold) aboveThreshold++;
            }
            return new(peak, SampleCount == 0 ? 0 : Math.Sqrt(squares / SampleCount), aboveThreshold);
        }
    }

    // 20 ms Core frames, including a shorter final frame. Caller owns each copy.
    public PcmFrame GetFrame(int frameIndex)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(pcm is null, this);
            ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
            var offset = checked(frameIndex * 320);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(offset, SampleCount);
            return new(Ids, Epoch, frameIndex, offset, Format, pcm.AsSpan(offset * 2, Math.Min(320, SampleCount - offset) * 2));
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (pcm is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(pcm);
            pcm = null;
        }
    }
}
