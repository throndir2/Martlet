using Martlet.Core.Settings;

namespace Martlet.Conversation;

/// <summary>What a reply becomes for a voice engine, without speaking it: the pieces the real segmenter hands that engine
/// (its own tags kept, other tags, the character's tags and control tags dropped, markup suppressed, broken where a spoken
/// reply with these speech breaks breaks), the cues the character acts on in each piece, the text the chat and captions show
/// and the control tags found, in order. Used by Martlet MCP's voice_tags tool to observe tag handling and a persona's speech
/// breaks headlessly.</summary>
public static class SpeechTextPreview
{
    public sealed record Cue(int Piece, string Tag, int Offset);
    public sealed record Result(IReadOnlyList<string> Spoken, int SuppressedPieces, string Shown, IReadOnlyList<Cue> Cues,
        IReadOnlyList<string> Controls);

    public static Result For(string reply, SpeechEngine? engine, IReadOnlyList<string>? characterTags = null,
        SpeechBreaks? breaks = null, IReadOnlyList<string>? controlTags = null)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var segmenter = new SpeechSegmenter(1536, 16_384, eagerFirstClause: true, tags: engine?.Tags, characterTags: characterTags,
            breaks: breaks ?? SpeechBreaks.Default, controlTags: controlTags);
        var pieces = segmenter.Push(reply).Concat(segmenter.Finish()).ToArray();
        var spoken = new List<string>();
        var cues = new List<Cue>();
        foreach (var piece in pieces)
        {
            // A piece with no words is -1: its cues act after the spoken piece before it.
            if (piece.Text is not null) spoken.Add(piece.Text);
            foreach (var cue in piece.Cues ?? []) cues.Add(new(piece.Text is null ? -1 : spoken.Count - 1, cue.Tag, cue.Offset));
        }
        var controls = new List<string>();
        var stripper = new VoiceTagStripper(characterTags, controlTags: controlTags, droppedControl: controls.Add);
        var shown = stripper.Push(reply) + stripper.Finish();
        return new(spoken, pieces.Count(p => p.Text is null && p.Cues is null or { Count: 0 }), shown, cues, controls);
    }
}
