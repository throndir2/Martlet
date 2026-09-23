using System.Threading.Channels;
using Martlet.Audio;
using Martlet.Core.Audio;

namespace Martlet.Conversation;

public enum SpeechObservationFailure { None, Disabled, Replaced, QueueFull, InvalidInput, Stopped }

// A concrete, nonblocking metadata/PCM tee; never invokes consumer code on the producer.
public sealed class GeneratedSpeechObserver : IDisposable
{
    private readonly object gate = new();
    private readonly Channel<GeneratedSpeechObservation> segments = Channel.CreateBounded<GeneratedSpeechObservation>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    private GeneratedSpeechObservation? current;
    private bool enabled, disposed;
    public bool IsEnabled { get { lock (gate) return enabled && !disposed; } }
    public ChannelReader<GeneratedSpeechObservation> Segments => segments.Reader;
    public SpeechObservationFailure Failure { get; private set; }

    public void Enable()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Failure = SpeechObservationFailure.None;
            enabled = true;
        }
    }

    public void Disable()
    {
        lock (gate)
        {
            enabled = false;
            current?.Stop(SpeechObservationFailure.Disabled);
            while (segments.Reader.TryRead(out var pending)) pending.Stop(SpeechObservationFailure.Disabled);
        }
    }

    internal GeneratedSpeechObservation? Begin(PlaybackRun playback, PcmFormat format)
    {
        lock (gate)
        {
            if (!enabled || disposed) return null;
            current?.Stop(SpeechObservationFailure.Replaced);
            var observation = new GeneratedSpeechObservation(playback, format);
            current = observation;
            if (!segments.Writer.TryWrite(observation))
            {
                Failure = SpeechObservationFailure.QueueFull;
                observation.Stop(Failure);
            }
            return observation;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            Disable();
            disposed = true;
            segments.Writer.TryComplete();
        }
    }
}

public sealed class GeneratedSpeechObservation
{
    private readonly object gate = new();
    private readonly Channel<PcmFrame> frames = Channel.CreateBounded<PcmFrame>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long samples, sequence;
    private bool sealedInput;
    private SpeechObservationFailure failure;

    internal GeneratedSpeechObservation(PlaybackRun playback, PcmFormat format)
    {
        Playback = playback;
        Format = format;
    }

    public PlaybackRun Playback { get; }
    public PcmFormat Format { get; }
    public ChannelReader<PcmFrame> Frames => frames.Reader;
    public Task Stopped => stopped.Task;
    public SpeechObservationFailure Failure { get { lock (gate) return failure; } }
    public long SampleCount { get { lock (gate) return samples; } }
    public bool InputCompleted { get { lock (gate) return sealedInput && failure == SpeechObservationFailure.None; } }

    internal void Submit(PcmFrame frame)
    {
        lock (gate)
        {
            if (failure != SpeechObservationFailure.None) return;
            var binding = Playback.Snapshot;
            if (sealedInput || frame.Ids != binding.Ids || frame.Epoch != binding.Epoch ||
                frame.Format != Format || frame.SampleOffset != samples || frame.Sequence != sequence)
            {
                Stop(SpeechObservationFailure.InvalidInput);
                return;
            }
            if (!frames.Writer.TryWrite(frame))
            {
                Stop(SpeechObservationFailure.QueueFull);
                return;
            }
            samples += frame.SamplesPerChannel;
            sequence++;
        }
    }

    internal void CompleteInput(long finalSamples)
    {
        lock (gate)
        {
            if (failure != SpeechObservationFailure.None) return;
            if (finalSamples != samples) { Stop(SpeechObservationFailure.InvalidInput); return; }
            sealedInput = true;
            frames.Writer.TryComplete();
        }
    }

    public void Stop(SpeechObservationFailure reason = SpeechObservationFailure.Stopped)
    {
        if (reason == SpeechObservationFailure.None) throw new ArgumentOutOfRangeException(nameof(reason));
        lock (gate)
        {
            if (failure != SpeechObservationFailure.None) return;
            failure = reason;
            while (frames.Reader.TryRead(out _)) { }
            frames.Writer.TryComplete();
            stopped.TrySetResult();
        }
    }
}
