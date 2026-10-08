namespace Martlet.Core.Settings;

/// <summary>A self-hosted voice engine that clones a reference recording on a paired Martlet host. Every engine runs as
/// its own host role (<see cref="HostRoleKind"/>) behind its own gateway route (<see cref="RouteId"/>, <see cref="Path"/>),
/// and all of them speak the same reference-voice synthesis contract: the request carries the chosen recording, its
/// transcript and the reply text, and the stream returns contiguous 24 kHz mono 16-bit PCM frames (Martlet.F5's
/// <c>martlet.f5.worker</c> 1.0 events, which F5 defined first). Saved TTS gateway routes
/// (<see cref="SetupRouteType.GatewayF5"/>) may name any engine's route. <see cref="Tags"/> is the engine's own tag
/// catalog in its native syntax (empty when it reads words only): see <see cref="VoiceTags"/>. <see cref="Summary"/> is its
/// strength in a few words; <see cref="Abilities"/> is the rundown of what it can do (voice cloning, laughs &amp; sighs,
/// emotions, the last as far as <see cref="Emotions"/> says: a tone in the catalog is not proof the voice performs it),
/// <see cref="RunsOn"/> where it runs and how much graphics memory it takes, and <see cref="Features"/> its other needs, as
/// Companion › Voice shows them.
/// <see cref="MultipleReferences"/> engines learn a voice from each of several recordings (zero-shot on all of them); the
/// others hear a voice made from several recordings as one, the recordings joined with a short pause. A <see cref="Cloud"/>
/// voice (ElevenLabs, <see cref="SpeechEngines.CloudVoices"/>) is no host engine: its host role, route and path only name it, and
/// it runs on its provider's servers with the owner's own key.</summary>
public sealed record SpeechEngine(
    string Key,
    string Name,
    string HostRoleKind,
    string RouteId,
    string Path,
    string DefaultModel,
    string WeightsLicense,
    string Summary,
    int MinimumReferenceMilliseconds,
    int MaximumReferenceMilliseconds,
    int MinimumGpuMemoryGb,
    IReadOnlyList<VoiceTag>? TagCatalog = null,
    string Languages = "English",
    bool StreamsWhileGenerating = false,
    bool MultipleReferences = false,
    EmotionSupport Emotions = EmotionSupport.None,
    bool Cloud = false)
{
    /// <summary>The tags the engine speaks as sounds or tones; replies keep exactly these for it and lose every other tag.</summary>
    public IReadOnlyList<VoiceTag> Tags => TagCatalog ?? [];

    public bool SupportsTags => Tags.Count > 0;

    /// <summary>The quick rundown: every engine here copies a voice from your recordings; it makes laughs and sighs when its
    /// catalog has sound tags; its emotions are what was measured (<see cref="Emotions"/>), not what its catalog lists.</summary>
    public VoiceAbilities Abilities => new(true, Tags.Any(tag => tag.Kind == VoiceTagKind.Sound), Emotions);

    /// <summary>Whether it needs an NVIDIA graphics card (<see cref="MinimumGpuMemoryGb"/> above 0). One that doesn't
    /// (Chatterbox Nano) uses the card when its computer has one and the processor otherwise.</summary>
    public bool NeedsGpu => MinimumGpuMemoryGb > 0;

    /// <summary>Its footprint (graphics card, memory, download) in Martlet's catalog (docs/RESOURCE_FOOTPRINTS.md), or null
    /// for an engine registered without one: the one on a graphics card when it has both.</summary>
    public Planning.ComponentOption? Footprint => Planning.FootprintCatalog.Default.FindModel(Planning.PlanComponent.Voice, DefaultModel);

    /// <summary>Where it runs and how much graphics memory it takes: "Runs on an NVIDIA GPU: about 3.7 GB of graphics memory,
    /// up to 4.2 GB (6 GB+ card)." (<see cref="Planning.ComponentOption.WhereItRuns"/>), with "; without one, on the CPU." for
    /// an engine that has a processor footprint as well.</summary>
    public string RunsOn
    {
        get
        {
            if (Cloud) return $"Runs on {Name}{(Name.EndsWith('s') ? "'" : "'s")} servers, with your own {Name} account and API key. Nothing runs on your computers.";
            var options = Planning.FootprintCatalog.Default.For(Planning.PlanComponent.Voice)
                .Where(option => option.IsLocal && option.ModelId == DefaultModel).ToArray();
            var gpu = options.FirstOrDefault(option => option.UsesGpu);
            var cpu = options.FirstOrDefault(option => !option.UsesGpu);
            if (gpu is not null && cpu is not null) return gpu.WhereItRuns.TrimEnd('.') + "; without one, on the CPU.";
            return (gpu ?? cpu)?.WhereItRuns ?? $"Runs on an NVIDIA GPU ({MinimumGpuMemoryGb} GB+ card).";
        }
    }

    /// <summary>Whether its model allows only non-commercial use (CC-BY-NC, Coqui Public Model License).</summary>
    public bool NonCommercial => WeightsLicense.Contains("NC", StringComparison.Ordinal) || WeightsLicense.StartsWith("CPML", StringComparison.Ordinal);

    /// <summary>The recording lengths it clones, when narrower than the usual 1-30 seconds ("5 s+ samples", "3-10 s samples").</summary>
    public string? SampleLimits =>
        MinimumReferenceMilliseconds > 1_000 && MaximumReferenceMilliseconds < 30_000
            ? $"{MinimumReferenceMilliseconds / 1000}-{MaximumReferenceMilliseconds / 1000} s samples"
        : MinimumReferenceMilliseconds > 1_000 ? $"{MinimumReferenceMilliseconds / 1000} s+ samples"
        : MaximumReferenceMilliseconds < 30_000 ? $"Samples up to {MaximumReferenceMilliseconds / 1000} s"
        : null;

    /// <summary>Its other needs and how it runs, a few words each: Docker (or, for a <see cref="Cloud"/> voice, the cloud), the
    /// recording lengths it clones, streaming and its languages. What it can do is <see cref="Abilities"/>; its graphics card
    /// and memory are <see cref="RunsOn"/>.</summary>
    public IReadOnlyList<string> Features =>
    [
        Cloud ? "Cloud, paid" : "Docker",
        .. SampleLimits is { } limits ? [limits] : Array.Empty<string>(),
        .. StreamsWhileGenerating ? ["Streams"] : Array.Empty<string>(),
        .. MultipleReferences ? ["Learns from several samples"] : Array.Empty<string>(),
        Languages
    ];
}

/// <summary>What a tag does: a non-word sound or a tone of voice the engine performs, or (never sent to an engine) a
/// character tag such as <c>{blush}</c> that makes the desktop character act, or a control tag such as
/// <c>[chattiness:quiet]</c> that tells Martlet something about the reply (<see cref="ChattinessTags"/>).</summary>
public enum VoiceTagKind { Sound, Emotion, Character, Control }

/// <summary>One tag an engine understands, written exactly as the engine expects it (Chatterbox <c>[laugh]</c>, Dia
/// <c>(laughs)</c>), with one line telling the Thinking model when to use it. <see cref="Cue"/> is what it means across
/// engines (<c>laugh</c> for both of those), so a character emote or motion linked to a cue follows every engine's
/// spelling of it. <see cref="AliasOf"/> is set on another spelling of a tag that Martlet accepts too (<see cref="VoiceTags.Spellings"/>,
/// such as <c>[nod]</c> or <c>*nods*</c> for <c>{nod}</c>): the tag it stands for.</summary>
public sealed record VoiceTag(string Text, VoiceTagKind Kind, string Usage, string? CueName = null, string? AliasOf = null)
{
    /// <summary>The engine-independent name of the sound or tone: <see cref="CueName"/>, or the tag without its brackets.</summary>
    public string Cue => CueName ?? Text.Trim('[', ']', '(', ')', ' ').ToLowerInvariant();

    /// <summary>The tag as the engine, character or request spells it: <see cref="AliasOf"/>, or <see cref="Text"/>.</summary>
    public string Canonical => AliasOf ?? Text;
}

public static class SpeechEngines
{
    /// <summary>The processing destination every reference-voice engine's relay advertises. Engines share it (it is named
    /// for F5, the first engine), so one voice list and one voice-rights confirmation serve every engine on a host.</summary>
    public const string VoiceDestination = "f5-host";

    /// <summary>Chatterbox Turbo's tags, verified against the pinned tokenizer's added_tokens.json
    /// (huggingface.co/ResembleAI/chatterbox-turbo at 749d1c1a46eb10492095d68fbcf55691ccf137cd), which defines 19. Resemble's
    /// documentation (the model card, the README and the official Turbo apps' EVENT_TAGS) names the nine non-word sounds; the
    /// other ten are the tokenizer's style tokens, the tones of voice. Turbo has no exaggeration or CFG setting (its loader
    /// turns emotion_adv off and generate() ignores both), and measured, its tones don't change the voice: only [whispering]
    /// does, because Martlet's service whispers those sentences itself (docs/CHATTERBOX_VOICE.md). The tones stay as cues for
    /// the character. [advertisement] and [narration] (reading genres, not conversation) are left out.</summary>
    public static readonly IReadOnlyList<VoiceTag> ChatterboxTurboTags =
    [
        new("[laugh]", VoiceTagKind.Sound, "a laugh, after something genuinely funny"),
        new("[chuckle]", VoiceTagKind.Sound, "a small amused chuckle"),
        new("[sigh]", VoiceTagKind.Sound, "a sigh, for relief, tiredness or mild exasperation"),
        new("[gasp]", VoiceTagKind.Sound, "a gasp of surprise"),
        new("[cough]", VoiceTagKind.Sound, "a cough"),
        new("[clear throat]", VoiceTagKind.Sound, "clearing your throat before saying something"),
        new("[groan]", VoiceTagKind.Sound, "a groan, for something annoying or painful"),
        new("[sniff]", VoiceTagKind.Sound, "a sniff"),
        new("[shush]", VoiceTagKind.Sound, "a shushing sound"),
        new("[happy]", VoiceTagKind.Emotion, "happy and cheerful, for good news or delight"),
        new("[sarcastic]", VoiceTagKind.Emotion, "sarcastic, for dry teasing or an obvious joke"),
        new("[surprised]", VoiceTagKind.Emotion, "surprised, for something unexpected"),
        new("[angry]", VoiceTagKind.Emotion, "angry, for real annoyance or outrage"),
        new("[fear]", VoiceTagKind.Emotion, "fearful, for something scary or worrying"),
        new("[crying]", VoiceTagKind.Emotion, "tearful, as if crying, for something genuinely sad"),
        new("[whispering]", VoiceTagKind.Emotion, "whispered, for a secret or something hushed"),
        new("[dramatic]", VoiceTagKind.Emotion, "dramatic, for playful theatrics")
    ];

    public static readonly SpeechEngine Chatterbox = new("chatterbox", "Chatterbox Turbo", "chatterbox",
        "martlet.gateway.chatterbox-synthesis.v1", "/martlet/v1/inference/chatterbox-synthesis", "chatterbox-turbo", "MIT",
        "Natural and quick.", 5_001, 30_000, 6, ChatterboxTurboTags, Emotions: EmotionSupport.WhisperOnly);

    /// <summary>The original Chatterbox's tags. The model has no sound or tone tokens (a [laugh] would be read out), so replies
    /// keep only these two, which its service turns into how a sentence is said: [expressive] speaks it with the Expressive
    /// exaggeration and CFG weight instead of the General ones (<see cref="ChatterboxStyle"/>), and [whispering] whispers it.</summary>
    public static readonly IReadOnlyList<VoiceTag> ChatterboxOriginalTags =
    [
        new("[expressive]", VoiceTagKind.Emotion,
            "expressive and animated, for excitement, delight, drama or strong feeling; leave it out for calm, even speech"),
        new("[whispering]", VoiceTagKind.Emotion, "whispered, for a secret or something hushed")
    ];

    /// <summary>The original 500M Chatterbox (huggingface.co/ResembleAI/chatterbox, English): no laughs or sighs, but each
    /// sentence is calm (General) or, with [expressive], expressive, with the exaggeration and CFG weight chosen in Companion ›
    /// Voice (<see cref="ChatterboxStyle"/>). It speaks each sentence whole.</summary>
    public static readonly SpeechEngine ChatterboxOriginal = new("chatterbox-original", "Chatterbox Original", "chatterbox-original",
        "martlet.gateway.chatterbox-original-synthesis.v1", "/martlet/v1/inference/chatterbox-original-synthesis", "chatterbox-original",
        "MIT", "Calm or expressive, as each sentence needs.", 5_001, 30_000, 6, ChatterboxOriginalTags, Emotions: EmotionSupport.Intensity);

    /// <summary>Chatterbox Nano (huggingface.co/ResembleAI/chatterbox-nano): Turbo's architecture with a 110M backbone and the
    /// same tags. Its role runs on the NVIDIA GPU when the computer has one and on the processor otherwise
    /// (<see cref="SpeechEngine.NeedsGpu"/> is false).</summary>
    public static readonly SpeechEngine ChatterboxNano = new("chatterbox-nano", "Chatterbox Nano", "chatterbox-nano",
        "martlet.gateway.chatterbox-nano-synthesis.v1", "/martlet/v1/inference/chatterbox-nano-synthesis", "chatterbox-nano", "MIT",
        "Small; runs without a graphics card.", 5_001, 30_000, 0, ChatterboxTurboTags, Emotions: EmotionSupport.WhisperOnly);

    public static readonly SpeechEngine F5 = new("f5", "F5-TTS", "f5",
        "martlet.gateway.f5-synthesis.v1", "/martlet/v1/inference/f5-synthesis", "f5tts-v1-base", "CC-BY-NC-4.0",
        "Closest likeness to the recording.", 1_000, 30_000, 6, Languages: "English, Chinese");

    /// <summary>XTTS-v2 speaks the language chosen when its host role is installed (XTTS_LANGUAGE, English by default), and
    /// averages the speaker from each of a voice's recordings (<c>get_conditioning_latents</c> on all of them).</summary>
    public static readonly SpeechEngine Xtts = new("xtts", "XTTS-v2", "xtts",
        "martlet.gateway.xtts-synthesis.v1", "/martlet/v1/inference/xtts-synthesis", "xtts-v2", "CPML-1.0",
        "Starts speaking soonest.", 1_000, 30_000, 4, Languages: "17 languages", StreamsWhileGenerating: true, MultipleReferences: true);

    /// <summary>GPT-SoVITS v2Pro (release 20250606v2pro): good for anime-style voices; clones a 3-10 second recording whose
    /// transcript is English or Japanese (<see cref="ReferenceLanguage"/>), and speaks each sentence as soon as it is
    /// generated. With a voice of several recordings, the first 3-10 second one is its prompt and the others add their tone
    /// (aux_ref_audio_paths).</summary>
    public static readonly SpeechEngine GptSovits = new("gpt-sovits", "GPT-SoVITS", "gpt-sovits",
        "martlet.gateway.gpt-sovits-synthesis.v1", "/martlet/v1/inference/gpt-sovits-synthesis", "gpt-sovits-v2pro", "MIT",
        "Best for anime-style voices.", 3_000, 10_000, 4, Languages: "English, Japanese", MultipleReferences: true);

    /// <summary>Dia's nonverbal cues, verbatim from the README at the pinned commit (github.com/nari-labs/dia at
    /// 876125e461a03b157ec905b0fe8b57a0f8b9e7a0), limited to the ones that suit a conversation; (singing), (sings), (beep),
    /// (claps), (applause), (burps) and (screams) are left out. Dia warns that overusing cues or using unlisted ones causes
    /// artifacts, so the worker passes reply text through unchanged.</summary>
    public static readonly IReadOnlyList<VoiceTag> DiaTags =
    [
        new("(laughs)", VoiceTagKind.Sound, "a laugh, after something genuinely funny", "laugh"),
        new("(chuckle)", VoiceTagKind.Sound, "a small amused chuckle"),
        new("(sighs)", VoiceTagKind.Sound, "a sigh, for relief, tiredness or mild exasperation", "sigh"),
        new("(gasps)", VoiceTagKind.Sound, "a gasp of surprise", "gasp"),
        new("(coughs)", VoiceTagKind.Sound, "a cough", "cough"),
        new("(clears throat)", VoiceTagKind.Sound, "clearing your throat before saying something", "clear throat"),
        new("(groans)", VoiceTagKind.Sound, "a groan, for something annoying or painful", "groan"),
        new("(sniffs)", VoiceTagKind.Sound, "a sniff", "sniff"),
        new("(inhales)", VoiceTagKind.Sound, "a breath in, before something big", "inhale"),
        new("(exhales)", VoiceTagKind.Sound, "a breath out, letting go of tension", "exhale"),
        new("(mumbles)", VoiceTagKind.Sound, "mumble the words after it", "mumble"),
        new("(humming)", VoiceTagKind.Sound, "a short hum", "hum"),
        new("(sneezes)", VoiceTagKind.Sound, "a sneeze", "sneeze"),
        new("(whistles)", VoiceTagKind.Sound, "a short whistle, impressed or surprised", "whistle")
    ];

    /// <summary>Nari Labs' Dia: clones the voice from the recording and its transcript and performs the nonverbal cues in
    /// <see cref="DiaTags"/>. English only. Shorter references (5-10 s) leave Dia room to speak; longer ones are refused.</summary>
    public static readonly SpeechEngine Dia = new("dia", "Dia", "dia",
        "martlet.gateway.dia-synthesis.v1", "/martlet/v1/inference/dia-synthesis", "dia-1.6b-0626", "Apache-2.0",
        "Lifelike, conversational delivery.", 1_000, 20_000, 8, DiaTags);

    private static readonly object Gate = new();
    private static SpeechEngine[] all = [Chatterbox, ChatterboxOriginal, ChatterboxNano, F5, Xtts, GptSovits, Dia];

    /// <summary>ElevenLabs' audio tags as its documentation spells them (elevenlabs.io/docs: "How do audio tags work with
    /// Eleven v3 and v4?" and the Eleven v4 prompting guide, read 2026-10-07), limited to the ones that suit a conversation:
    /// human reactions, the delivery directions [whispers] and [shouts], and emotions. Sound effects ([gunshot], [applause]...),
    /// accents and [sings] are left out. The same tags work with Eleven v3 and v4 (Turbo included). ElevenLabs documents that
    /// they change the delivery; Martlet has not measured them (there is no ElevenLabs account to test with). Each tag's cue
    /// is an existing cross-engine cue, so the character's voice emotes follow it: [whispers] is the whispering cue, [shouts]
    /// dramatic, [excited] happy, [sad] sigh, [annoyed] sarcastic and [curious] surprised. Tags without a fitting cue
    /// ([snorts], [thoughtful], [mischievously]...) are left out.</summary>
    public static readonly IReadOnlyList<VoiceTag> ElevenLabsTags =
    [
        new("[laughs]", VoiceTagKind.Sound, "a laugh, after something genuinely funny", "laugh"),
        new("[chuckles]", VoiceTagKind.Sound, "a small amused chuckle", "chuckle"),
        new("[sighs]", VoiceTagKind.Sound, "a sigh, for relief, tiredness or mild exasperation", "sigh"),
        new("[clears throat]", VoiceTagKind.Sound, "clearing your throat before saying something", "clear throat"),
        new("[inhales deeply]", VoiceTagKind.Sound, "a deep breath in, before something big", "inhale"),
        new("[exhales]", VoiceTagKind.Sound, "a breath out, letting go of tension", "exhale"),
        new("[whispers]", VoiceTagKind.Emotion, "whispered, for a secret or something hushed", "whispering"),
        new("[shouts]", VoiceTagKind.Emotion, "shouted, for calling out or big excitement; use it rarely", "dramatic"),
        new("[happy]", VoiceTagKind.Emotion, "happy and cheerful, for good news or delight"),
        new("[excited]", VoiceTagKind.Emotion, "excited, for something you can't wait for", "happy"),
        new("[sad]", VoiceTagKind.Emotion, "sad, for disappointment or a quiet loss", "sigh"),
        new("[crying]", VoiceTagKind.Emotion, "tearful, as if crying, for something genuinely sad"),
        new("[angry]", VoiceTagKind.Emotion, "angry, for real annoyance or outrage"),
        new("[sarcastic]", VoiceTagKind.Emotion, "sarcastic, for dry teasing or an obvious joke"),
        new("[annoyed]", VoiceTagKind.Emotion, "annoyed, for mild irritation", "sarcastic"),
        new("[surprised]", VoiceTagKind.Emotion, "surprised, for something unexpected"),
        new("[curious]", VoiceTagKind.Emotion, "curious, for a question you really want answered", "surprised")
    ];

    /// <summary>ElevenLabs (elevenlabs.io): a cloud voice, not a host engine (so not in <see cref="All"/>), that clones a voice
    /// from one of your recordings (Instant Voice Cloning) and performs <see cref="ElevenLabsTags"/> with it in real time over the
    /// Text to Dialogue WebSocket (<see cref="ElevenLabsSetup"/>). Its emotions are what ElevenLabs documents, not measured.</summary>
    public static readonly SpeechEngine ElevenLabs = new("elevenlabs", "ElevenLabs", "elevenlabs", "elevenlabs.text-to-dialogue.v1",
        "/v1/text-to-dialogue/stream-input", ElevenLabsSetup.V4Turbo, "ElevenLabs terms (paid)",
        "Your cloned voice with tones, in the cloud.", 1_000, 30_000, 0, ElevenLabsTags, "90+ languages",
        StreamsWhileGenerating: true, Emotions: EmotionSupport.Tags, Cloud: true);

    /// <summary>The cloud voices that have a tag catalog (ElevenLabs). Their tags join every engine's in what replies may write
    /// and what chat and captions never show (<see cref="VoiceTags.Known"/>).</summary>
    public static IReadOnlyList<SpeechEngine> CloudVoices { get; } = [ElevenLabs];

    /// <summary>Every engine; the first is the default cloning engine (Chatterbox Turbo).</summary>
    public static IReadOnlyList<SpeechEngine> All => Volatile.Read(ref all);

    /// <summary>Whether <paramref name="engine"/> is one of Resemble AI's Chatterbox models, whose replies all carry the Perth
    /// watermark.</summary>
    public static bool IsChatterbox(SpeechEngine? engine) => engine is not null && engine.Key.StartsWith("chatterbox", StringComparison.Ordinal);

    /// <summary>The engine Martlet suggests and starts with when the owner has not chosen one.</summary>
    public static SpeechEngine Default => Chatterbox;

    /// <summary>Adds an engine (or replaces one with the same key), e.g. from a static constructor or startup:
    /// <c>SpeechEngines.Register(new("dia", "Dia", "dia", routeId, path, model, licence, summary, min, max, gb, tags))</c>.
    /// Keys, host roles and routes must be unique; tags are written in the engine's own syntax.</summary>
    public static void Register(SpeechEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (engine.Tags.Any(tag => string.IsNullOrWhiteSpace(tag.Text) || tag.Text != tag.Text.Trim()))
            throw new ArgumentException("Voice tags must be non-empty, trimmed text.", nameof(engine));
        lock (Gate)
        {
            var others = all.Where(e => e.Key != engine.Key).ToArray();
            if (others.Any(e => e.HostRoleKind == engine.HostRoleKind || e.RouteId == engine.RouteId || e.Path == engine.Path))
                throw new ArgumentException("Another voice engine already uses that host role or route.", nameof(engine));
            var index = Array.FindIndex(all, e => e.Key == engine.Key);
            Volatile.Write(ref all, index < 0 ? [.. all, engine] : [.. all[..index], engine, .. all[(index + 1)..]]);
        }
    }

    /// <summary>Reference recording languages engines that need one (GPT-SoVITS) accept.</summary>
    public static readonly IReadOnlyList<string> ReferenceLanguages = ["en", "ja"];

    /// <summary>The language of a reference recording, read from its exact transcript: "ja" when it has kana or kanji,
    /// otherwise "en". GPT-SoVITS needs it with every request.</summary>
    public static string ReferenceLanguage(string transcript) =>
        transcript.Any(c => c is >= '\u3041' and <= '\u30ff' or >= '\u3400' and <= '\u4dbf' or >= '\u4e00' and <= '\u9fff' or
            >= '\uff66' and <= '\uff9d') ? "ja" : "en";

    /// <summary>Why <paramref name="engine"/> cannot clone a recording of <paramref name="durationMilliseconds"/>, or null.</summary>
    public static string? ReferenceProblem(SpeechEngine engine, int durationMilliseconds) =>
        !Fits(engine, durationMilliseconds)
            ? $"{engine.Name} needs a {engine.MinimumReferenceMilliseconds / 1000}-{engine.MaximumReferenceMilliseconds / 1000} " +
              $"second recording; this one is {durationMilliseconds / 1000d:0.#} seconds."
            : null;

    /// <summary>Why <paramref name="engine"/> cannot clone a voice whose recording lasts <paramref name="durationMilliseconds"/>
    /// and, for a voice made from several recordings, whose recordings last <paramref name="clipMilliseconds"/>; or null.
    /// The joined recording may fit, or (for an engine that learns from several, <see cref="UsesClips"/>) one of them.</summary>
    public static string? ReferenceProblem(SpeechEngine engine, int durationMilliseconds, IReadOnlyList<int>? clipMilliseconds)
    {
        if (Fits(engine, durationMilliseconds) || UsesClips(engine, clipMilliseconds)) return null;
        var problem = ReferenceProblem(engine, durationMilliseconds)!;
        return engine.MultipleReferences && clipMilliseconds is { Count: > 1 }
            ? problem[..^1] + $", and none of its {clipMilliseconds.Count} recordings is."
            : problem;
    }

    /// <summary>Whether <paramref name="engine"/> gets each of a voice's several recordings rather than them joined: it learns
    /// from several and at least one of them (its prompt) fits its length bounds.</summary>
    public static bool UsesClips(SpeechEngine engine, IReadOnlyList<int>? clipMilliseconds) =>
        engine.MultipleReferences && clipMilliseconds is { Count: > 1 } && clipMilliseconds.Any(ms => Fits(engine, ms));

    private static bool Fits(SpeechEngine engine, int milliseconds) =>
        milliseconds >= engine.MinimumReferenceMilliseconds && milliseconds <= engine.MaximumReferenceMilliseconds;

    public static SpeechEngine? ForRoute(string? routeId) => All.FirstOrDefault(engine => engine.RouteId == routeId);

    public static SpeechEngine? ForKey(string? key) => All.FirstOrDefault(engine => engine.Key == key);

    public static SpeechEngine? ForRoleKind(string? kind) => All.FirstOrDefault(engine => engine.HostRoleKind == kind);

    /// <summary>The engine whose host model this is (a host voice route's model ID), or null for any other voice.</summary>
    public static SpeechEngine? ForModel(string? modelId) =>
        modelId is null ? null : All.FirstOrDefault(engine => engine.DefaultModel == modelId);

    /// <summary>The tags the voice with this host model speaks; empty for OpenAI, Windows and engines without a catalog.</summary>
    public static IReadOnlyList<VoiceTag> TagsForModel(string? modelId) => ForModel(modelId)?.Tags ?? [];

    /// <summary>The tags a reply's voice keeps: ElevenLabs' when <paramref name="elevenLabs"/> speaks, otherwise those of the
    /// host engine with <paramref name="hostModelId"/> (none for OpenAI and Windows).</summary>
    public static IReadOnlyList<VoiceTag> TagsFor(bool elevenLabs, string? hostModelId) =>
        elevenLabs ? ElevenLabs.Tags : TagsForModel(hostModelId);
}
