namespace Martlet.Core.Settings;

/// <summary>How far a voice speaks with emotions such as angry or sad.</summary>
public enum EmotionSupport
{
    /// <summary>It can't: the reply never changes its tone (a cloned voice keeps the tone of its recording).</summary>
    None,

    /// <summary>Only whispering, which the voice model does itself, and only now and then: Martlet adds no whisper of its own.
    /// Chatterbox Turbo reads its other tones ([happy], [angry]...) but measurably doesn't perform them; they only cue the
    /// character.</summary>
    WhisperOnly,

    /// <summary>Calm or expressive for each sentence (the original Chatterbox's exaggeration and CFG weight, chosen by the reply
    /// with [expressive]), but no particular emotion.</summary>
    Intensity,

    /// <summary>Tags that pick the emotion.</summary>
    Tags
}

/// <summary>Whether a voice has an ability: yes, partly (its note says how far) or no.</summary>
public enum AbilityLevel { No, Partly, Yes }

/// <summary>One item of a voice's rundown: the ability ("Voice cloning"), how far the voice has it, a few words when it has it
/// only partly ("whispering only") and one sentence saying what that means for the owner.</summary>
public sealed record VoiceAbility(string Name, AbilityLevel Level, string? Note, string Help)
{
    /// <summary>"Voice cloning: yes", "Emotions: whispering only", "Laughs &amp; sighs: no".</summary>
    public override string ToString() =>
        $"{Name}: {Note ?? (Level == AbilityLevel.Yes ? "yes" : Level == AbilityLevel.No ? "no" : "partly")}";
}

/// <summary>What a way of speaking can do: the quick rundown Companion › Voice shows for every voice engine and
/// the cloud voice. <see cref="Cloning"/>: it copies a voice from your recordings. <see cref="Sounds"/>: it makes non-word sounds
/// such as laughs and sighs where the reply writes a tag for them (paralinguistic tags). <see cref="Emotions"/>: how far it
/// speaks angrily, sadly and so on.</summary>
public sealed record VoiceAbilities(bool Cloning, bool Sounds, EmotionSupport Emotions)
{
    /// <summary>OpenAI's voice (gpt-4o-mini-tts): only OpenAI's built-in voices, words only. The model can follow tone
    /// instructions, but Martlet sends none, so the reply doesn't change its tone.</summary>
    public static VoiceAbilities OpenAiVoice { get; } = new(false, false, EmotionSupport.None);

    /// <summary>The three items, always in this order: voice cloning, laughs &amp; sighs, emotions.</summary>
    public IReadOnlyList<VoiceAbility> Items =>
    [
        Cloning
            ? new("Voice cloning", AbilityLevel.Yes, null, "Copies a voice from your recordings.")
            : new("Voice cloning", AbilityLevel.No, null, "Speaks only with its own built-in voices."),
        Sounds
            ? new("Laughs & sighs", AbilityLevel.Yes, null, "Laughs, sighs and other sounds where the reply asks for them.")
            : new("Laughs & sighs", AbilityLevel.No, null, "Says the words only; Martlet leaves sound tags out."),
        Emotions switch
        {
            EmotionSupport.Tags => new("Emotions", AbilityLevel.Yes, null, "Speaks angrily, sadly, happily and so on where the reply asks."),
            EmotionSupport.WhisperOnly => new("Emotions", AbilityLevel.Partly, "whispering only",
                "The voice model itself whispers where the reply asks, but only now and then. Tones such as angry or sad don't change the voice; they only move the character."),
            EmotionSupport.Intensity => new("Emotions", AbilityLevel.Partly, "calm or expressive",
                "The reply makes each sentence calm or expressive, but can't pick an emotion such as angry or sad."),
            _ => new("Emotions", AbilityLevel.No, null,
                Cloning ? "Keeps the tone of your recording; the reply can't change it." : "The reply can't change its tone.")
        }
    ];

    /// <summary>The rundown in one line, for screen readers and MCP: "Voice cloning: yes. Laughs &amp; sighs: yes. Emotions:
    /// whispering only."</summary>
    public string Describe() => string.Join(" ", Items.Select(item => item + "."));

    /// <summary>The tags of an engine's catalog that do what <paramref name="item"/> says: its sounds for laughs &amp; sighs,
    /// and for emotions the tones that change the voice (all of them with <see cref="EmotionSupport.Tags"/> or
    /// <see cref="EmotionSupport.Intensity"/>, otherwise only [whispering], which the model itself performs now and then). Tones
    /// a voice only reads (Chatterbox Turbo's [angry]...) are not listed.</summary>
    public IReadOnlyList<VoiceTag> TagsFor(VoiceAbility item, IReadOnlyList<VoiceTag> tags) => item.Level == AbilityLevel.No ? [] : item.Name switch
    {
        "Laughs & sighs" => [.. tags.Where(tag => tag.Kind == VoiceTagKind.Sound)],
        "Emotions" when Emotions is EmotionSupport.Tags or EmotionSupport.Intensity => [.. tags.Where(tag => tag.Kind == VoiceTagKind.Emotion)],
        "Emotions" => [.. tags.Where(tag => tag.Kind == VoiceTagKind.Emotion && tag.Cue == "whispering")],
        _ => []
    };
}
