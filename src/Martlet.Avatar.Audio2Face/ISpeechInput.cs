using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Audio2Face;

internal interface ISpeechInput
{
    CorrelationIds Ids { get; }
    long Epoch { get; }
    int SampleRate { get; }
    long SampleOffset { get; }
    long SampleCount { get; }
    bool IsComplete { get; }
    CancellationToken Stopped { get; }
    void Begin();
    void Check();
    void Stop();
    IAsyncEnumerable<PcmFrame> ReadFrames(CancellationToken cancellationToken);
}
