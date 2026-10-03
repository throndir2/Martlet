using System.Threading.Channels;

namespace Martlet.Conversation;

/// <summary>One tag in a reply that can make the desktop character act: a character tag such as <c>{blush}</c> (removed from
/// the words) or the speaking engine's own voice tag such as <c>[laugh]</c> (which the voice also performs), written as the
/// catalog spells it, with how long after its sentence starts playing it falls.</summary>
public sealed record CharacterCue(string Tag, TimeSpan Delay);

/// <summary>The cues of one sentence as it starts playing (or, for a reply that isn't spoken, as its text arrives);
/// <see cref="Finished"/> completes when its playback ends.</summary>
public sealed record CharacterCueLine(IReadOnlyList<CharacterCue> Cues, Task Finished);

// A nonblocking tee of the character cues in replies, for the desktop character. Never invokes consumer code on the producer.
public sealed class CharacterCueFeed
{
    private readonly Channel<CharacterCueLine> lines = Channel.CreateBounded<CharacterCueLine>(
        new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false });
    public ChannelReader<CharacterCueLine> Lines => lines.Reader;
    public void Post(IReadOnlyList<CharacterCue> cues, Task finished)
    {
        if (cues.Count > 0) lines.Writer.TryWrite(new(cues, finished));
    }
}
