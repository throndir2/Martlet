using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Martlet.Core.Contracts;

namespace Martlet.Audio;

/// <summary>Removes what the speakers play from the microphone. Each call takes one 10 ms frame
/// (<see cref="EchoReduction.FrameSamples"/> samples of 16 kHz mono): the speaker audio and the microphone at the same moment;
/// the microphone frame is cleaned in place. One instance holds one room's echo model and serves one capture at a time.</summary>
public interface IEchoCanceller : IDisposable
{
    void Process(ReadOnlySpan<float> speaker, Span<float> microphone);
}

/// <summary>What an output endpoint plays (a loopback of it), opened, read, stopped and disposed on the capture's own worker
/// thread. Read never blocks: it returns one packet of interleaved source-format audio, or zero bytes when none is waiting.
/// Silent packets are zeros.</summary>
public interface IEchoReference : IDisposable
{
    CaptureSourceFormat Format { get; }
    void Start();
    CapturePacket Read(Span<byte> destination);
    void Stop();
}

public interface IEchoReferenceFactory
{
    /// <summary>Opens the output the microphone may hear: a fixed endpoint, or Windows' default output when null.</summary>
    IEchoReference Open(string? outputEndpointId, CancellationToken cancellationToken);
}

public enum EchoReductionState { Off, Active, NoSpeakerAudio, Unavailable }

/// <summary>How the latest capture's echo reduction went. Metadata only, never audio: frames cleaned, frames while the
/// speakers played, and how much quieter the microphone got over those frames (dB, the user's own voice included; null without
/// speaker sound).</summary>
public sealed record EchoReductionReport(EchoReductionState State, string? Problem = null, long Frames = 0, long SpeakerFrames = 0,
    double? ReducedDb = null);

public static class EchoReduction
{
    public const int SampleRate = 16000;
    public const int FrameSamples = SampleRate / 100;
    /// <summary>What an echo-reducing microphone delivers: 16 kHz mono float, already cleaned.</summary>
    public static CaptureSourceFormat Format { get; } = new(SampleRate, 1, 32, DeviceSampleEncoding.IeeeFloat);
}

/// <summary>Echo reduction for microphone capture (Companion › Listening › Reduce echo from my speakers). Each capture also
/// reads what the output plays, and the echo canceller subtracts it from the microphone before anything else hears it, so
/// Martlet doesn't hear its own voice, a video or music through the speakers. The speaker audio is used only for that, in
/// memory, and is never kept or sent. The room's echo model is kept for a minute between captures so it needn't relearn the
/// room each time Martlet listens again. If the output or the canceller can't start, the microphone works without it and
/// <see cref="Report"/> says why.</summary>
public sealed class EchoReducer : IDisposable
{
    internal static readonly TimeSpan KeepModel = TimeSpan.FromMinutes(1);
    private readonly object gate = new();
    private readonly ICaptureDeviceFactory microphones;
    private readonly IEchoReferenceFactory speakers;
    private readonly Func<IEchoCanceller> createCanceller;
    private readonly TimeProvider time;
    private IEchoCanceller? kept;
    private string? keptFor;
    private long keptAt, generation;
    private bool disposed;
    private EchoReductionReport report = new(EchoReductionState.Off);

    public EchoReducer(ICaptureDeviceFactory microphones, IEchoReferenceFactory speakers, Func<IEchoCanceller> createCanceller,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(microphones);
        ArgumentNullException.ThrowIfNull(speakers);
        ArgumentNullException.ThrowIfNull(createCanceller);
        this.microphones = microphones;
        this.speakers = speakers;
        this.createCanceller = createCanceller;
        time = timeProvider ?? TimeProvider.System;
    }

    public EchoReductionReport Report { get { lock (gate) return report; } }

    /// <summary>Whether echo reduction tells what the speakers play from the user: once a capture's echo-reducing microphone has
    /// opened, that capture's own <paramref name="capture"/> timeline decides (false once its canceller or the speakers' sound
    /// was lost); otherwise the latest report does (a capture that couldn't start echo reduction reports why at once, and before
    /// any capture nothing says it can't).</summary>
    public bool Works(EchoTimeline? capture = null) =>
        capture?.Reducing ?? Report.State is not (EchoReductionState.NoSpeakerAudio or EchoReductionState.Unavailable);

    /// <summary>Raised on the capture's worker thread after each capture, or when one starts without echo reduction.</summary>
    public event Action<EchoReductionReport>? Reported;

    /// <summary>A microphone factory that reduces echo from this output (null: Windows' default output). With a
    /// <paramref name="timeline"/>, each capture it opens also says frame by frame whether its sound was what the speakers
    /// played.</summary>
    public ICaptureDeviceFactory For(string? outputEndpointId, EchoTimeline? timeline = null) => new Bound(this, outputEndpointId, timeline);

    /// <summary>Drops the kept echo model (pause, lock, closing); the next capture learns the room again.</summary>
    public void Forget()
    {
        IEchoCanceller? drop;
        lock (gate)
        {
            generation++;
            drop = kept;
            kept = null;
            keptFor = null;
        }
        Release(drop);
    }

    public void Dispose()
    {
        lock (gate) disposed = true;
        Forget();
    }

    private sealed class Bound(EchoReducer owner, string? output, EchoTimeline? timeline) : ICaptureDeviceFactory
    {
        public ICaptureDevice Open(CaptureDeviceAccess access, CancellationToken cancellationToken) =>
            owner.Open(access, output, timeline, cancellationToken);
    }

    private ICaptureDevice Open(CaptureDeviceAccess access, string? output, EchoTimeline? timeline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        var microphone = microphones.Open(access, cancellationToken);
        var key = (access.Input.EndpointId ?? "default") + "\n" + (output ?? "default");
        IEchoCanceller? canceller = null;
        IEchoReference? speaker = null;
        long taken = 0;
        try
        {
            try { (canceller, taken) = Take(key); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Publish(new(EchoReductionState.Unavailable, "The echo canceller couldn't start (" + error.GetType().Name + "), so Martlet " +
                    "listened without it."));
                return microphone;
            }
            cancellationToken.ThrowIfCancellationRequested();
            access.CheckAuthorization();
            try
            {
                speaker = speakers.Open(output, cancellationToken);
                speaker.Format.Validate();
            }
            catch (CaptureDeviceException error) when (error.ResourcesReleased)
            {
                if (speaker is not null)
                {
                    var opened = speaker;
                    speaker = null;
                    try { opened.Dispose(); }
                    catch (Exception) { throw new CaptureDeviceException(ErrorCode.AudioCaptureFailed, resourcesReleased: false); }
                }
                Return(canceller, key, taken);
                canceller = null;
                Publish(new(EchoReductionState.NoSpeakerAudio, SpeakerProblem(error.Code)));
                return microphone;
            }
            var device = new EchoCancellingCaptureDevice(this, microphone, speaker, canceller, key, taken, time, timeline);
            canceller = null;
            speaker = null;
            return device;
        }
        catch (Exception failure)
        {
            // Cancellation, expired consent or an output that didn't release: release everything opened here, on this thread.
            var released = true;
            try { speaker?.Dispose(); }
            catch (Exception) { released = false; }
            if (canceller is not null) Return(canceller, key, taken);
            try { microphone.Dispose(); }
            catch (Exception) { released = false; }
            if (!released || failure is CaptureDeviceException { ResourcesReleased: false })
                throw new CaptureDeviceException(ErrorCode.AudioCaptureFailed, resourcesReleased: false);
            throw;
        }
    }

    internal static string SpeakerProblem(ErrorCode code) => code switch
    {
        ErrorCode.AudioDeviceUnavailable => "No speakers to reduce echo from were found, so Martlet listened without echo reduction.",
        ErrorCode.AudioFormatUnsupported => "Your speakers' sound format isn't supported, so Martlet listened without echo reduction.",
        ErrorCode.AudioAccessDenied => "Windows didn't let Martlet hear what your speakers play, so it listened without echo reduction.",
        ErrorCode.AudioDeviceLost or ErrorCode.AudioDeviceChanged => "Your speakers changed while Martlet listened, so it stopped reducing echo.",
        _ => "Martlet couldn't hear what your speakers play, so it listened without echo reduction."
    };

    private (IEchoCanceller Canceller, long Generation) Take(string key)
    {
        IEchoCanceller? drop = null;
        long current;
        lock (gate)
        {
            current = generation;
            if (kept is not null && keptFor == key && time.GetElapsedTime(keptAt) <= KeepModel)
            {
                var reused = kept;
                kept = null;
                keptFor = null;
                return (reused, current);
            }
            drop = kept;
            kept = null;
            keptFor = null;
        }
        Release(drop);
        return (createCanceller(), current);
    }

    private void Return(IEchoCanceller canceller, string key, long taken)
    {
        IEchoCanceller? drop = canceller;
        lock (gate)
        {
            if (!disposed && taken == generation)
            {
                drop = kept;
                kept = canceller;
                keptFor = key;
                keptAt = time.GetTimestamp();
            }
        }
        Release(drop);
    }

    internal void Finish(IEchoCanceller? canceller, string key, long taken, EchoReductionReport outcome)
    {
        if (canceller is not null) Return(canceller, key, taken);
        Publish(outcome);
    }

    private void Publish(EchoReductionReport next)
    {
        lock (gate) report = next;
        Reported?.Invoke(next);
    }

    private static void Release(IEchoCanceller? canceller)
    {
        try { canceller?.Dispose(); }
        catch (Exception) { }
    }
}

/// <summary>A microphone that delivers 16 kHz mono float with the speakers' sound removed. Both streams are put on one timeline
/// by their devices' timestamps (the performance counter); each 10 ms microphone frame is cleaned against the speaker audio
/// <see cref="Lead"/> later on that timeline, because an echo always arrives after it was played. A frame whose speaker audio
/// hasn't arrived within <see cref="Patience"/> counts the missing part as silence (nothing was playing). All of it runs on
/// the capture's own worker thread; buffers are cleared when they are done with.</summary>
internal sealed class EchoCancellingCaptureDevice : ICaptureDevice
{
    private const int Rate = EchoReduction.SampleRate;
    private const int Frame = EchoReduction.FrameSamples;
    // 40 ms: keeps the echo inside the canceller's window even when the two devices' timestamps disagree a little.
    internal const int Lead = Rate / 25;
    // 30 ms past due, speaker audio that hasn't come is silence: nothing played, or the output went quiet.
    internal const int Patience = Rate * 3 / 100;
    // Timestamps that move less than 2 ms are jitter; a speaker jump over 20 ms starts a new stretch of sound.
    private const int Jitter = Rate / 500;
    private const int Gap = Rate / 50;
    private const int MaximumPending = Rate / 2;
    private const int RingSize = 1 << 15;
    private const long RingMask = RingSize - 1;
    private const double SpeakerFloor = 1e-8;
    // 300 ms in 10 ms frames: a room's echo outlasts what the speakers played by about this much.
    private const int EchoReach = 30;
    private int sinceSpeaker = EchoReach;
    private readonly EchoReducer owner;
    private readonly ICaptureDevice microphone;
    private readonly string key;
    private readonly long generation;
    private readonly TimeProvider time;
    private readonly EchoTimeline? timeline;
    private readonly CaptureNormalizer microphoneNormalizer;
    private readonly byte[] microphoneScratch, microphonePcm, speakerScratch, speakerPcm, output;
    private readonly float[] pending = new float[MaximumPending + 4096];
    private readonly float[] ring = new float[RingSize];
    private readonly long[] stamps = new long[RingSize];
    private readonly float[] speakerFrame = new float[Frame], microphoneFrame = new float[Frame];
    private IEchoReference? speaker;
    private CaptureNormalizer? speakerNormalizer;
    private IEchoCanceller? canceller;
    private long? microphoneAnchor, speakerAnchor;
    private long consumed, covered = long.MinValue, frames, speakerFrames;
    private int pendingStart, pendingCount, outputStart, outputCount;
    private double before, after;
    private string? problem;
    private EchoReductionState state = EchoReductionState.Active;
    private bool speakerUnreleased, disposed;

    public CaptureSourceFormat Format => EchoReduction.Format;

    internal EchoCancellingCaptureDevice(EchoReducer owner, ICaptureDevice microphone, IEchoReference speaker, IEchoCanceller canceller,
        string key, long generation, TimeProvider time, EchoTimeline? timeline = null)
    {
        this.owner = owner;
        this.microphone = microphone;
        this.speaker = speaker;
        this.canceller = canceller;
        this.key = key;
        this.generation = generation;
        this.time = time;
        this.timeline = timeline;
        microphoneNormalizer = new(microphone.Format);
        speakerNormalizer = new(speaker.Format);
        microphoneScratch = new byte[microphone.Format.MaximumPacketBytes];
        speakerScratch = new byte[speaker.Format.MaximumPacketBytes];
        microphonePcm = new byte[(Rate / 10 + 64) * 2];
        speakerPcm = new byte[(Rate / 10 + 64) * 2];
        output = new byte[pending.Length * 4];
        timeline?.Reduce(true);
    }

    public void Start(CancellationToken cancellationToken)
    {
        if (speaker is not null)
        {
            try { speaker.Start(); }
            catch (Exception error) when (error is not OperationCanceledException) { LoseSpeaker(error); }
        }
        microphone.Start(cancellationToken);
    }

    public CapturePacket Read(Span<byte> destination, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (outputCount == 0)
        {
            PumpSpeaker();
            var packet = microphone.Read(microphoneScratch, cancellationToken);
            if (packet.Discontinuity) return new(0, Discontinuity: true);
            if (packet.ByteCount < 0 || packet.ByteCount > microphoneScratch.Length)
                throw new CaptureDeviceException(ErrorCode.PayloadTooLarge);
            try
            {
                if (packet.ByteCount > 0) AcceptMicrophone(microphoneScratch.AsSpan(0, packet.ByteCount), packet.Timestamp);
            }
            finally { CryptographicOperations.ZeroMemory(microphoneScratch); }
            CleanReadyFrames();
        }
        if (outputCount == 0) return new(0);
        var bytes = Math.Min(outputCount, destination.Length / 4 * 4);
        output.AsSpan(outputStart, bytes).CopyTo(destination);
        CryptographicOperations.ZeroMemory(output.AsSpan(outputStart, bytes));
        outputStart += bytes;
        outputCount -= bytes;
        if (outputCount == 0) outputStart = 0;
        return new(bytes);
    }

    public void Stop()
    {
        try { speaker?.Stop(); }
        catch (Exception) { }
        microphone.Stop();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ExceptionDispatchInfo? failure = null;
        try { microphone.Dispose(); }
        catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        try { speaker?.Dispose(); }
        catch (Exception) { speakerUnreleased = true; }
        speaker = null;
        microphoneNormalizer.Dispose();
        speakerNormalizer?.Dispose();
        foreach (var buffer in new[] { microphoneScratch, microphonePcm, speakerScratch, speakerPcm, output })
            CryptographicOperations.ZeroMemory(buffer);
        Array.Clear(pending);
        Array.Clear(ring);
        Array.Clear(speakerFrame);
        Array.Clear(microphoneFrame);
        double? reduced = speakerFrames > 0 && before > 0 ? Math.Round(10 * Math.Log10(before / Math.Max(after, before * 1e-12)), 1) : null;
        owner.Finish(canceller, key, generation, new(state, problem, frames, speakerFrames, reduced));
        canceller = null;
        if (failure is not null)
        {
            if (failure.SourceException is CaptureDeviceException) failure.Throw();
            throw new CaptureDeviceException(ErrorCode.AudioCaptureFailed, resourcesReleased: false);
        }
        if (speakerUnreleased) throw new CaptureDeviceException(ErrorCode.AudioCaptureFailed, resourcesReleased: false);
    }

    // The timeline is 16 kHz samples of the performance counter; WASAPI stamps packets in 100 ns units of it.
    private long Now() => (long)((Int128)time.GetTimestamp() * Rate / time.TimestampFrequency);

    // Where a packet's first frame sits on the timeline: its own timestamp, or (without a believable one) just before it arrived.
    private long Place(long? timestamp, int sourceFrames, int sampleRate)
    {
        var now = Now();
        var estimate = now - (long)sourceFrames * Rate / sampleRate;
        if (timestamp is not { } stamp) return estimate;
        var start = stamp / (TimeSpan.TicksPerSecond / Rate);
        return Math.Abs(start - estimate) > Rate * 2 ? estimate : start;
    }

    private void AcceptMicrophone(ReadOnlySpan<byte> source, long? timestamp)
    {
        var format = microphone.Format;
        var anchor = Place(timestamp, source.Length / format.BlockAlignment, format.SampleRate)
            - microphoneNormalizer.SourceSamples * Rate / format.SampleRate;
        if (microphoneAnchor is not { } current || Math.Abs(anchor - current) > Jitter) microphoneAnchor = anchor;
        var written = microphoneNormalizer.Convert(source, microphonePcm) / 2;
        if (pendingStart + pendingCount + written > pending.Length)
        {
            Array.Copy(pending, pendingStart, pending, 0, pendingCount);
            Array.Clear(pending, pendingCount, pending.Length - pendingCount);
            pendingStart = 0;
        }
        for (var i = 0; i < written; i++)
            pending[pendingStart + pendingCount + i] = BitConverter.ToInt16(microphonePcm, i * 2) / 32768f;
        pendingCount += written;
        CryptographicOperations.ZeroMemory(microphonePcm);
    }

    private void PumpSpeaker()
    {
        if (speaker is null) return;
        try
        {
            // Bounded: a stalled reader catches up over a few calls rather than all at once.
            for (var packets = 0; packets < 32; packets++)
            {
                var packet = speaker.Read(speakerScratch);
                if (packet.ByteCount == 0) break;
                if (packet.ByteCount < 0 || packet.ByteCount > speakerScratch.Length || packet.ByteCount % speaker.Format.BlockAlignment != 0)
                    throw new CaptureDeviceException(ErrorCode.PayloadTooLarge);
                try { AcceptSpeaker(speakerScratch.AsSpan(0, packet.ByteCount), packet.Timestamp, packet.Discontinuity); }
                finally { CryptographicOperations.ZeroMemory(speakerScratch); }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException) { LoseSpeaker(error); }
    }

    private void AcceptSpeaker(ReadOnlySpan<byte> source, long? timestamp, bool discontinuity)
    {
        var format = speaker!.Format;
        var anchor = Place(timestamp, source.Length / format.BlockAlignment, format.SampleRate)
            - speakerNormalizer!.SourceSamples * Rate / format.SampleRate;
        if (speakerAnchor is { } current && (discontinuity || Math.Abs(anchor - current) > Gap))
        {
            // Sound started again after a pause, or the timeline jumped: finish the last stretch where it was, start again here.
            var tail = speakerNormalizer.OutputSamples;
            Remember(tail, speakerNormalizer.Complete(speakerPcm) / 2, current);
            speakerNormalizer = new(format);
            anchor = Place(timestamp, source.Length / format.BlockAlignment, format.SampleRate);
            speakerAnchor = anchor;
        }
        else if (speakerAnchor is not { } kept || Math.Abs(anchor - kept) > Jitter) speakerAnchor = anchor;
        var first = speakerNormalizer.OutputSamples;
        Remember(first, speakerNormalizer.Convert(source, speakerPcm) / 2, speakerAnchor!.Value);
    }

    private void Remember(long first, int count, long anchor)
    {
        for (var i = 0; i < count; i++)
        {
            var index = anchor + first + i;
            var slot = index & RingMask;
            ring[slot] = BitConverter.ToInt16(speakerPcm, i * 2) / 32768f;
            stamps[slot] = index + 1;
        }
        if (count > 0) covered = Math.Max(covered, anchor + first + count);
        CryptographicOperations.ZeroMemory(speakerPcm);
    }

    private void CleanReadyFrames()
    {
        if (microphoneAnchor is not { } anchor) return;
        var now = Now();
        while (pendingCount >= Frame)
        {
            var at = anchor + consumed + Lead;
            if (speaker is not null && covered < at + Frame && now < at + Frame + Patience && pendingCount < MaximumPending) break;
            double speakerEnergy = 0, input = 0, cleaned = 0;
            for (var i = 0; i < Frame; i++)
            {
                var index = at + i;
                var slot = index & RingMask;
                var value = stamps[slot] == index + 1 ? ring[slot] : 0f;
                speakerFrame[i] = value;
                speakerEnergy += value * value;
                var heard = microphoneFrame[i] = pending[pendingStart + i];
                input += heard * heard;
            }
            if (canceller is not null)
            {
                try { canceller.Process(speakerFrame, microphoneFrame); }
                catch (Exception error) when (error is not OperationCanceledException) { LoseCanceller(error); }
            }
            for (var i = 0; i < Frame; i++)
            {
                var value = float.IsFinite(microphoneFrame[i]) ? Math.Clamp(microphoneFrame[i], -1f, 1f) : 0f;
                cleaned += value * value;
                BitConverter.TryWriteBytes(output.AsSpan(outputStart + outputCount + i * 4, 4), value);
            }
            outputCount += Frame * 4;
            frames++;
            if (speakerEnergy / Frame > SpeakerFloor)
            {
                speakerFrames++;
                before += input;
                after += cleaned;
                sinceSpeaker = 0;
            }
            else if (sinceSpeaker < EchoReach) sinceSpeaker++;
            // Only a working canceller tells the speakers' sound from the user's; without one nothing is attributed to them.
            timeline?.Add(canceller is not null && sinceSpeaker < EchoReach, input, cleaned);
            Array.Clear(pending, pendingStart, Frame);
            pendingStart += Frame;
            pendingCount -= Frame;
            consumed += Frame;
            if (pendingCount == 0) pendingStart = 0;
        }
        Array.Clear(speakerFrame);
        Array.Clear(microphoneFrame);
    }

    private void LoseSpeaker(Exception error)
    {
        var lost = speaker;
        speaker = null;
        state = EchoReductionState.NoSpeakerAudio;
        timeline?.Reduce(false);
        problem ??= EchoReducer.SpeakerProblem(error is CaptureDeviceException failure ? failure.Code : ErrorCode.AudioCaptureFailed);
        try { lost?.Stop(); }
        catch (Exception) { }
        try { lost?.Dispose(); }
        catch (Exception) { speakerUnreleased = true; }
    }

    private void LoseCanceller(Exception error)
    {
        var failed = canceller;
        canceller = null;
        state = EchoReductionState.Unavailable;
        timeline?.Reduce(false);
        problem = "The echo canceller stopped (" + error.GetType().Name + "), so Martlet listened without it.";
        try { failed?.Dispose(); }
        catch (Exception) { }
    }
}
