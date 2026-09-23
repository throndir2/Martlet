using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Audio2Face;

public enum SpeechIngressResult { Accepted, Completed, Closed, InvalidFrame, LimitExceeded, QueueFull }

// This is a permission-bound generated-audio stream, not a capture device or an audio sink.
public sealed class GeneratedSpeechStream : IDisposable, ISpeechInput
{
    private readonly object gate = new();
    private readonly Channel<PcmFrame> frames;
    private readonly CancellationTokenSource stopped = new();
    private readonly CancellationToken stopToken;
    private readonly PcmFormat format;
    private long nextSequence;
    private long sampleCount;
    private int frameCount;
    private bool completed, disposed;
    private int begun;
    private Audio2FaceFailure? failure;

    public CorrelationIds Ids { get; }
    public long Epoch { get; }
    public int SampleRate => format.SampleRate;
    public long SampleOffset { get; }
    public long MaxSamples { get; }
    public int QueueCapacity { get; }
    public long SampleCount { get { lock (gate) return sampleCount; } }
    public Audio2FaceFailure? Failure { get { lock (gate) return failure; } }

    public GeneratedSpeechStream(CorrelationIds ids, long epoch, PcmFormat format,
        long firstSequence, long sampleOffset, long maxSamples, int queueCapacity = 16)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(format);
        ids.Validate();
        format.Validate();
        if (epoch is < 0 or > int.MaxValue || firstSequence is < 0 or > int.MaxValue ||
            format.Channels != 1 || maxSamples < 1 || maxSamples > format.SampleRate * 90L ||
            sampleOffset < 0 || sampleOffset > 9_007_199_254_740_991L - maxSamples ||
            queueCapacity is < 1 or > 128)
            throw new ArgumentException("Generated speech stream requires bounded mono PCM, identity and original sample-clock limits.");
        Ids = ids;
        Epoch = epoch;
        this.format = format;
        nextSequence = firstSequence;
        SampleOffset = sampleOffset;
        MaxSamples = maxSamples;
        QueueCapacity = queueCapacity;
        stopToken = stopped.Token;
        frames = Channel.CreateBounded<PcmFrame>(new BoundedChannelOptions(queueCapacity)
        {
            SingleWriter = false, SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false
        });
    }

    // Never waits for channel capacity. A failed enqueue stops this animation segment only.
    public SpeechIngressResult TrySubmit(PcmFrame frame)
    {
        lock (gate)
        {
            if (completed || disposed || failure is not null) return SpeechIngressResult.Closed;
            if (frame is null || frame.Ids != Ids || frame.Epoch != Epoch || frame.Format != format ||
                frame.Sequence != nextSequence || frame.SampleOffset != SampleOffset + sampleCount)
                return Fail(SpeechIngressResult.InvalidFrame, Audio2FaceFailure.InvalidInput);
            if (frameCount >= 10_000 || frame.SamplesPerChannel > MaxSamples - sampleCount)
                return Fail(SpeechIngressResult.LimitExceeded, Audio2FaceFailure.LimitExceeded);
            var copy = new PcmFrame(frame.Ids, frame.Epoch, frame.Sequence, frame.SampleOffset, frame.Format, frame.Data.Span);
            if (!frames.Writer.TryWrite(copy))
                return Fail(SpeechIngressResult.QueueFull, Audio2FaceFailure.Backpressure);
            sampleCount += frame.SamplesPerChannel;
            nextSequence++;
            frameCount++;
            return SpeechIngressResult.Accepted;
        }
    }

    public SpeechIngressResult CompleteInput(long finalSampleCount)
    {
        lock (gate)
        {
            if (completed || disposed || failure is not null) return SpeechIngressResult.Closed;
            if (sampleCount == 0 || finalSampleCount != sampleCount)
                return Fail(SpeechIngressResult.InvalidFrame, Audio2FaceFailure.InvalidInput);
            completed = true;
            frames.Writer.TryComplete();
            return SpeechIngressResult.Completed;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            frames.Writer.TryComplete();
            while (frames.Reader.TryRead(out _)) { }
            stopped.Cancel();
            stopped.Dispose();
        }
    }

    CancellationToken ISpeechInput.Stopped => stopToken;
    bool ISpeechInput.IsComplete { get { lock (gate) return completed; } }
    void ISpeechInput.Stop() => Dispose();
    void ISpeechInput.Begin()
    {
        Check();
        if (Interlocked.CompareExchange(ref begun, 1, 0) != 0)
            throw new Audio2FaceException(Audio2FaceFailure.AuthorizationConsumed);
    }
    void ISpeechInput.Check() => Check();

    async IAsyncEnumerable<PcmFrame> ISpeechInput.ReadFrames([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var frame in frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            Check();
            yield return frame;
        }
        Check();
        lock (gate)
        {
            if (!completed) throw new Audio2FaceException(Audio2FaceFailure.InvalidInput);
        }
    }

    private void Check()
    {
        lock (gate)
        {
            if (failure is { } reason) throw new Audio2FaceException(reason);
            stopToken.ThrowIfCancellationRequested();
        }
    }

    private SpeechIngressResult Fail(SpeechIngressResult result, Audio2FaceFailure reason)
    {
        failure = reason;
        frames.Writer.TryComplete(new Audio2FaceException(reason));
        while (frames.Reader.TryRead(out _)) { }
        stopped.Cancel();
        return result;
    }

    public override string ToString() => nameof(GeneratedSpeechStream);
}
