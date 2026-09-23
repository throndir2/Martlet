using Martlet.Core.Audio;
using Martlet.Core.Contracts;
using System.Runtime.CompilerServices;

namespace Martlet.Avatar.Audio2Face;

// An immutable, bounded copy of synthesized speech, never a capture-device input.
public sealed class GeneratedSpeechClip : ISpeechInput
{
    internal IReadOnlyList<PcmFrame> Frames { get; }
    public CorrelationIds Ids { get; }
    public long Epoch { get; }
    public int SampleRate { get; }
    public long SampleOffset { get; }
    public long SampleCount { get; }

    public GeneratedSpeechClip(IEnumerable<PcmFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var snapshot = new List<PcmFrame>();
        PcmFrame? first = null;
        long sampleCount = 0;
        long nextSequence = 0;
        foreach (var frame in frames)
        {
            ArgumentNullException.ThrowIfNull(frame);
            first ??= frame;
            if (snapshot.Count == 0) nextSequence = frame.Sequence;
            if (snapshot.Count >= 10_000 || frame.Ids != first.Ids || frame.Epoch != first.Epoch ||
                frame.Format != first.Format || frame.Format.Channels != 1 ||
                frame.Sequence != nextSequence || frame.SampleOffset != first.SampleOffset + sampleCount ||
                frame.SampleOffset > 9_007_199_254_740_991L - frame.SamplesPerChannel ||
                sampleCount + frame.SamplesPerChannel > first.Format.SampleRate * 90L)
                throw new ArgumentException("Generated speech must be contiguous, mono, consistently bound, and at most 90 seconds.");
            snapshot.Add(new PcmFrame(frame.Ids, frame.Epoch, frame.Sequence,
                frame.SampleOffset, frame.Format, frame.Data.Span));
            sampleCount += frame.SamplesPerChannel;
            nextSequence++;
        }
        if (first is null) throw new ArgumentException("Generated speech cannot be empty.");
        Frames = snapshot.AsReadOnly();
        Ids = first.Ids;
        Epoch = first.Epoch;
        SampleRate = first.Format.SampleRate;
        SampleOffset = first.SampleOffset;
        SampleCount = sampleCount;
    }

    public override string ToString() => nameof(GeneratedSpeechClip);

    CancellationToken ISpeechInput.Stopped => CancellationToken.None;
    bool ISpeechInput.IsComplete => true;
    void ISpeechInput.Begin() { }
    void ISpeechInput.Check() { }
    void ISpeechInput.Stop() { }
    async IAsyncEnumerable<PcmFrame> ISpeechInput.ReadFrames([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var frame in Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return frame;
        }
    }
}
