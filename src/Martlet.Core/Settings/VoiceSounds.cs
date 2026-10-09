namespace Martlet.Core.Settings;

/// <summary>Voice sounds a touch zone plays: a non-word sound (a gasp, a giggle, a sigh) that Martlet's own voice makes alone,
/// with no words. A zone's reaction list holds one as <c>sound:&lt;cue&gt;</c> (<see cref="Entry"/>), where the cue is a
/// <see cref="VoiceTagKind.Sound"/> tag's engine-independent <see cref="VoiceTag.Cue"/> (<c>sound:laugh</c>, <c>sound:gasp</c>),
/// so the entry keeps working when the owner changes to another voice that makes the same sound. Only the sounds the active
/// voice makes (<see cref="Supported"/>) play; the others stay in the list and wait for a voice that makes them.</summary>
public static class VoiceSounds
{
    /// <summary>What starts a voice-sound entry in a zone's reaction list.</summary>
    public const string Prefix = "sound:";

    // Sounds that need words after them (Dia's (mumbles) mumbles the words that follow): alone they make nothing.
    private static readonly HashSet<string> NeedWords = new(StringComparer.Ordinal) { "mumble" };

    /// <summary>The reaction-list entry for <paramref name="cue"/>: <c>sound:laugh</c>.</summary>
    public static string Entry(string cue) => Prefix + cue;

    /// <summary>The cue of a voice-sound entry (<c>laugh</c> for <c>sound:laugh</c>), or null for any other entry.</summary>
    public static string? CueOf(string? entry) =>
        entry is not null && entry.Length > Prefix.Length && entry.StartsWith(Prefix, StringComparison.Ordinal) &&
        entry[Prefix.Length..].Trim() is { Length: > 0 } cue ? cue : null;

    /// <summary>Whether <paramref name="entry"/> is a voice-sound entry.</summary>
    public static bool IsSound(string? entry) => CueOf(entry) is not null;

    /// <summary>The sounds <paramref name="engine"/> makes alone: its <see cref="VoiceTagKind.Sound"/> tags, each cue once, in its
    /// catalog's order. Empty for a voice without sounds (OpenAI, Windows, F5, XTTS, GPT-SoVITS, the original Chatterbox) or no
    /// voice.</summary>
    public static IReadOnlyList<VoiceTag> Supported(SpeechEngine? engine) =>
        engine is null ? [] : [.. engine.Tags.Where(tag => tag.Kind == VoiceTagKind.Sound && !NeedWords.Contains(tag.Cue)).DistinctBy(tag => tag.Cue)];

    /// <summary>The tag <paramref name="engine"/> makes <paramref name="cue"/> with, or null when it can't.</summary>
    public static VoiceTag? Tag(SpeechEngine? engine, string cue) =>
        Supported(engine).FirstOrDefault(tag => string.Equals(tag.Cue, cue, StringComparison.Ordinal));

    /// <summary>Every sound some voice makes, each cue once (Chatterbox's first, then the others'), for naming an entry the
    /// active voice can't make.</summary>
    public static IReadOnlyList<string> AllCues =>
        [.. SpeechEngines.All.Concat(SpeechEngines.CloudVoices).SelectMany(Supported).Select(tag => tag.Cue).Distinct(StringComparer.Ordinal)];

    /// <summary>The sound's name as the owner reads it: "Laugh", "Clear throat".</summary>
    public static string Label(string cue) => cue.Length == 0 ? cue : char.ToUpperInvariant(cue[0]) + cue[1..];

    /// <summary>The text the voice is sent to make the sound alone: the engine's own tag and nothing else (Chatterbox's
    /// <c>[laugh]</c>, Dia's <c>(laughs)</c>, ElevenLabs' <c>[laughs]</c>).</summary>
    public static string Text(VoiceTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return tag.Canonical;
    }

    /// <summary>The voice engine a Voice route speaks with: a paired host's engine, ElevenLabs, or null (OpenAI, none).</summary>
    public static SpeechEngine? EngineOf(SetupRoute? tts) => tts switch
    {
        { RouteType: SetupRouteType.ElevenLabs } => SpeechEngines.ElevenLabs,
        { RouteType: SetupRouteType.GatewayF5 } => SpeechEngines.ForRoute(tts.GatewaySnapshot?.RouteId) ?? SpeechEngines.ForModel(tts.ModelId),
        _ => null
    };

    /// <summary>Why the voice can't make any sound, for the zone list: "OpenAI's voice says words only, so voice sounds don't
    /// play." Null when it makes at least one.</summary>
    public static string? NoSoundsReason(SpeechEngine? engine, bool voice) =>
        !voice ? "Martlet has no voice set up, so voice sounds don't play."
        : engine is null ? "This voice says words only, so voice sounds don't play. Chatterbox Turbo, Chatterbox Nano, Dia and ElevenLabs make them."
        : Supported(engine).Count == 0 ? $"{engine.Name} says words only, so voice sounds don't play. Chatterbox Turbo, Chatterbox Nano, Dia and ElevenLabs make them."
        : null;
}
