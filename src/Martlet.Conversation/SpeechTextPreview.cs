using Martlet.Core.Settings;

namespace Martlet.Conversation;

/// <summary>What a reply becomes for a voice engine, without speaking it: the pieces the real segmenter hands that engine
/// (its own tags kept, other tags dropped, markup suppressed) and the text the chat and captions show. Used by Martlet MCP's
/// voice_tags tool to observe tag handling headlessly.</summary>
public static class SpeechTextPreview
{
    public sealed record Result(IReadOnlyList<string> Spoken, int SuppressedPieces, string Shown);

    public static Result For(string reply, SpeechEngine? engine)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var segmenter = new SpeechSegmenter(1536, 16_384, tags: engine?.Tags);
        var pieces = segmenter.Push(reply).Concat(segmenter.Finish()).ToArray();
        return new(pieces.Where(p => p.Text is not null).Select(p => p.Text!).ToArray(), pieces.Count(p => p.Text is null),
            VoiceTags.Strip(reply));
    }
}
