namespace Martlet.Core.Settings;

/// <summary>A self-hosted voice engine that clones a reference recording on a paired Martlet host. Every engine runs as
/// its own host role (<see cref="HostRoleKind"/>) behind its own gateway route (<see cref="RouteId"/>, <see cref="Path"/>),
/// and all of them speak the same reference-voice synthesis contract: the request carries the chosen recording, its
/// transcript and the reply text, and the stream returns contiguous 24 kHz mono 16-bit PCM frames (Martlet.F5's
/// <c>martlet.f5.worker</c> 1.0 events, which F5 defined first). Saved TTS gateway routes
/// (<see cref="SetupRouteType.GatewayF5"/>) may name any engine's route. <see cref="Tags"/> is the engine's own tag
/// catalog in its native syntax (empty when it reads words only): see <see cref="VoiceTags"/>. <see cref="Summary"/> is its
/// strength in a few words; <see cref="Features"/> lists what it needs and can do, as Companion › Voice shows them.
/// <see cref="MultipleReferences"/> engines learn a voice from each of several recordings (zero-shot on all of them); the
/// others hear a voice made from several recordings as one, the recordings joined with a short pause.</summary>
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
    bool MultipleReferences = false)
{
    /// <summary>The tags the engine speaks as sounds or tones; replies keep exactly these for it and lose every other tag.</summary>
    public IReadOnlyList<VoiceTag> Tags => TagCatalog ?? [];

    public bool SupportsTags => Tags.Count > 0;

    /// <summary>Whether its model allows only non-commercial use (CC-BY-NC, Coqui Public Model License).</summary>
    public bool NonCommercial => WeightsLicense.Contains("NC", StringComparison.Ordinal) || WeightsLicense.StartsWith("CPML", StringComparison.Ordinal);

    /// <summary>The recording lengths it clones, when narrower than the usual 1-30 seconds ("5 s+ samples", "3-10 s samples").</summary>
    public string? SampleLimits =>
        MinimumReferenceMilliseconds > 1_000 && MaximumReferenceMilliseconds < 30_000
            ? $"{MinimumReferenceMilliseconds / 1000}-{MaximumReferenceMilliseconds / 1000} s samples"
        : MinimumReferenceMilliseconds > 1_000 ? $"{MinimumReferenceMilliseconds / 1000} s+ samples"
        : MaximumReferenceMilliseconds < 30_000 ? $"Samples up to {MaximumReferenceMilliseconds / 1000} s"
        : null;

    /// <summary>What it needs and can do, a few words each: the graphics card and Docker it runs on, voice cloning (and
    /// its recording lengths), sounds and tones, streaming and its languages.</summary>
    public IReadOnlyList<string> Features =>
    [
        $"NVIDIA GPU, {MinimumGpuMemoryGb} GB+",
        "Docker",
        "Voice cloning",
        .. SampleLimits is { } limits ? [limits] : Array.Empty<string>(),
        .. Tags.Any(tag => tag.Kind == VoiceTagKind.Sound) ? ["Laughs & sighs"] : Array.Empty<string>(),
        .. Tags.Any(tag => tag.Kind == VoiceTagKind.Emotion) ? ["Emotions"] : Array.Empty<string>(),
        .. StreamsWhileGenerating ? ["Streams"] : Array.Empty<string>(),
        .. MultipleReferences ? ["Learns from several samples"] : Array.Empty<string>(),
        Languages
    ];
}

/// <summary>What a tag does: a non-word sound or a tone of voice the engine performs, or (never sent to an engine) a
/// character tag such as <c>{blush}</c> that makes the desktop character act.</summary>
public enum VoiceTagKind { Sound, Emotion, Character }

/// <summary>One tag an engine understands, written exactly as the engine expects it (Chatterbox <c>[laugh]</c>, Dia
/// <c>(laughs)</c>), with one line telling the Thinking model when to use it. <see cref="Cue"/> is what it means across
/// engines (<c>laugh</c> for both of those), so a character emote or motion linked to a cue follows every engine's
/// spelling of it.</summary>
public sealed record VoiceTag(string Text, VoiceTagKind Kind, string Usage, string? CueName = null)
{
    /// <summary>The engine-independent name of the sound or tone: <see cref="CueName"/>, or the tag without its brackets.</summary>
    public string Cue => CueName ?? Text.Trim('[', ']', '(', ')', ' ').ToLowerInvariant();
}

public static class SpeechEngines
{
    /// <summary>The processing destination every reference-voice engine's relay advertises. Engines share it (it is named
    /// for F5, the first engine), so one voice list and one voice-rights confirmation serve every engine on a host.</summary>
    public const string VoiceDestination = "f5-host";

    /// <summary>Chatterbox Turbo's tags, verified against the pinned tokenizer's added_tokens.json
    /// (huggingface.co/ResembleAI/chatterbox-turbo at 749d1c1a46eb10492095d68fbcf55691ccf137cd). Turbo's generate() ignores
    /// the older exaggeration slider, so its emotion controls are these native style tokens. [advertisement] and
    /// [narration] (reading genres, not conversation) are left out.</summary>
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
        new("[happy]", VoiceTagKind.Emotion, "say the words after it happily"),
        new("[sarcastic]", VoiceTagKind.Emotion, "say the words after it sarcastically"),
        new("[surprised]", VoiceTagKind.Emotion, "say the words after it with surprise"),
        new("[angry]", VoiceTagKind.Emotion, "say the words after it angrily"),
        new("[fear]", VoiceTagKind.Emotion, "say the words after it fearfully"),
        new("[crying]", VoiceTagKind.Emotion, "say the words after it as if crying"),
        new("[whispering]", VoiceTagKind.Emotion, "whisper the words after it"),
        new("[dramatic]", VoiceTagKind.Emotion, "say the words after it dramatically")
    ];

    public static readonly SpeechEngine Chatterbox = new("chatterbox", "Chatterbox Turbo", "chatterbox",
        "martlet.gateway.chatterbox-synthesis.v1", "/martlet/v1/inference/chatterbox-synthesis", "chatterbox-turbo", "MIT",
        "Expressive and quick.", 5_001, 30_000, 6, ChatterboxTurboTags);

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
    private static SpeechEngine[] all = [Chatterbox, F5, Xtts, GptSovits, Dia];

    /// <summary>Every engine; the first is the default cloning engine (Chatterbox Turbo).</summary>
    public static IReadOnlyList<SpeechEngine> All => Volatile.Read(ref all);

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
}
