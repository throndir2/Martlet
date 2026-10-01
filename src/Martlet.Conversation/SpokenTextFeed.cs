using System.Threading.Channels;

namespace Martlet.Conversation;

/// <summary>One sentence Martlet has started to say aloud; <see cref="Finished"/> completes when its playback ends.</summary>
public sealed record SpokenLine(string Text, Task Finished);

// A nonblocking tee of the sentences sent to text-to-speech, for captions. Never invokes consumer code on the producer.
public sealed class SpokenTextFeed
{
    private readonly Channel<SpokenLine> lines = Channel.CreateBounded<SpokenLine>(
        new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false });
    public ChannelReader<SpokenLine> Lines => lines.Reader;
    internal void Post(string text, Task finished) => lines.Writer.TryWrite(new(text, finished));
}
