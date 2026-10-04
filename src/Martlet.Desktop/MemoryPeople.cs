using Martlet.Core.Speakers;
using Martlet.Memory;

namespace Martlet.Desktop;

/// <summary>Whose memories: every fact can belong to one person Martlet knows by voice (<see cref="MemoryFact.VoiceId"/>, an ID
/// from the voice list), the one who said it or the one it is about. A fact without a voice is about no one in particular.
/// Merged voices keep their facts (a merged ID resolves to the voice it joined); a forgotten voice's facts stay until the owner
/// deletes them, shown as a forgotten voice.</summary>
internal static class MemoryPeople
{
    /// <summary>The label of a fact whose voice was forgotten (or is unknown on this PC).</summary>
    internal const string Forgotten = "a forgotten voice";
    /// <summary>The label of a fact whose voice can't be looked up (the voice list isn't available).</summary>
    internal const string Someone = "someone";

    /// <summary>How a voice is named in prompts and in the talk window: its name, else its tag (V3), like the conversation's
    /// "[Sam] " prefix.</summary>
    internal static string Label(KnownVoice voice) => VoicePromptContext.Sanitize(voice.Named ? voice.DisplayName : voice.Tag);

    /// <summary>Whose fact <paramref name="voiceId"/> is, or null for a fact about no one in particular.</summary>
    internal static string? Label(string? voiceId, VoiceRoster? roster) =>
        voiceId is null ? null : roster is null ? Someone : roster.Resolve(voiceId) is { } voice ? Label(voice) : Forgotten;

    /// <summary>The label of each voice the facts belong to, by voice ID.</summary>
    internal static IReadOnlyDictionary<string, string> Labels(IEnumerable<MemoryFact> facts, VoiceRoster? roster) =>
        facts.Select(fact => fact.VoiceId).OfType<string>().Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id, id => Label(id, roster)!, StringComparer.Ordinal);

    /// <summary>The voice an ID stands for now (following merges), so two IDs of one person compare equal.</summary>
    internal static string? Canonical(string? voiceId, VoiceRoster? roster) =>
        voiceId is null ? null : roster?.Resolve(voiceId)?.Id ?? voiceId;

    /// <summary>Every voice ID that stands for <paramref name="voice"/>: its own and those merged into it. Null without a voice.</summary>
    internal static IReadOnlySet<string>? Ids(KnownVoice? voice, VoiceRoster? roster)
    {
        if (voice is null) return null;
        var ids = new HashSet<string>(StringComparer.Ordinal) { voice.Id };
        if (roster is not null)
            foreach (var other in roster.Voices)
                if (roster.Resolve(other.Id)?.Id == voice.Id) ids.Add(other.Id);
        return ids;
    }
}
