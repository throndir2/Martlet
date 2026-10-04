using System.Threading.Channels;

namespace Martlet.Conversation;

/// <summary>One sentence Martlet has started to say aloud, or (after the voice failed) shows instead of saying;
/// <see cref="Finished"/> completes when its playback, or its reading time, ends. For a song, <paramref name="Sung"/> is the
/// whole line being sung while <paramref name="Text"/> grows word by word (karaoke).</summary>
public sealed record SpokenLine(string Text, Task Finished, string? Sung = null);

// A nonblocking tee of the sentences sent to text-to-speech, for captions (and, after the voice failed, of the sentences it
// couldn't say). Never invokes consumer code on the producer.
public sealed class SpokenTextFeed
{
    private readonly Channel<SpokenLine> lines = Channel.CreateBounded<SpokenLine>(
        new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false });
    public ChannelReader<SpokenLine> Lines => lines.Reader;
    internal void Post(string text, Task finished, string? sung = null) => lines.Writer.TryWrite(new(text, finished, sung));
}
