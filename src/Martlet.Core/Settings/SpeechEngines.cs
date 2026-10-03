namespace Martlet.Core.Settings;

/// <summary>A self-hosted voice engine that clones a reference recording on a paired Martlet host. Every engine runs as
/// its own host role (<see cref="HostRoleKind"/>) behind its own gateway route (<see cref="RouteId"/>, <see cref="Path"/>),
/// and all of them speak the same reference-voice synthesis contract: the request carries the chosen recording, its
/// transcript and the reply text, and the stream returns contiguous 24 kHz mono 16-bit PCM frames (Martlet.F5's
/// <c>martlet.f5.worker</c> 1.0 events, which F5 defined first). Saved TTS gateway routes
/// (<see cref="SetupRouteType.GatewayF5"/>) may name any engine's route.</summary>
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
    int MinimumGpuMemoryGb);

public static class SpeechEngines
{
    /// <summary>The processing destination every reference-voice engine's relay advertises. Engines share it (it is named
    /// for F5, the first engine), so one voice list and one voice-rights confirmation serve every engine on a host.</summary>
    public const string VoiceDestination = "f5-host";

    public static readonly SpeechEngine F5 = new("f5", "F5-TTS", "f5",
        "martlet.gateway.f5-synthesis.v1", "/martlet/v1/inference/f5-synthesis", "f5tts-v1-base", "CC-BY-NC-4.0",
        "Close likeness; speaks each sentence once it is fully generated.", 1_000, 30_000, 6);

    public static readonly SpeechEngine Xtts = new("xtts", "XTTS-v2", "xtts",
        "martlet.gateway.xtts-synthesis.v1", "/martlet/v1/inference/xtts-synthesis", "xtts-v2", "CPML-1.0",
        "Starts speaking before a sentence is finished (streams as it generates).", 1_000, 30_000, 4);

    /// <summary>GPT-SoVITS v2Pro (release 20250606v2pro): good for anime-style voices; clones a 3-10 second recording whose
    /// transcript is English or Japanese (<see cref="ReferenceLanguage"/>), and speaks each sentence as soon as it is
    /// generated.</summary>
    public static readonly SpeechEngine GptSovits = new("gpt-sovits", "GPT-SoVITS", "gpt-sovits",
        "martlet.gateway.gpt-sovits-synthesis.v1", "/martlet/v1/inference/gpt-sovits-synthesis", "gpt-sovits-v2pro", "MIT",
        "Good for anime-style voices; needs a 3-10 second recording and speaks each sentence as soon as it is generated.",
        3_000, 10_000, 4);

    /// <summary>Nari Labs' Dia: clones the voice from the recording and its transcript, and performs the nonverbal cues it
    /// recognizes in reply text, such as (laughs), (sighs), (coughs) and (gasps). English only. Shorter references (5-10 s)
    /// leave Dia room to speak; longer ones are refused.</summary>
    public static readonly SpeechEngine Dia = new("dia", "Dia", "dia",
        "martlet.gateway.dia-synthesis.v1", "/martlet/v1/inference/dia-synthesis", "dia-1.6b-0626", "Apache-2.0",
        "Can laugh, sigh, cough and gasp on cue; English only; speaks each sentence once it is generated.", 1_000, 20_000, 8);

    public static readonly IReadOnlyList<SpeechEngine> All = [F5, Xtts, GptSovits, Dia];

    /// <summary>Reference recording languages engines that need one (GPT-SoVITS) accept.</summary>
    public static readonly IReadOnlyList<string> ReferenceLanguages = ["en", "ja"];

    /// <summary>The language of a reference recording, read from its exact transcript: "ja" when it has kana or kanji,
    /// otherwise "en". GPT-SoVITS needs it with every request.</summary>
    public static string ReferenceLanguage(string transcript) =>
        transcript.Any(c => c is >= '\u3041' and <= '\u30ff' or >= '\u3400' and <= '\u4dbf' or >= '\u4e00' and <= '\u9fff' or
            >= '\uff66' and <= '\uff9d') ? "ja" : "en";

    /// <summary>Why <paramref name="engine"/> cannot clone a recording of <paramref name="durationMilliseconds"/>, or null.</summary>
    public static string? ReferenceProblem(SpeechEngine engine, int durationMilliseconds) =>
        durationMilliseconds < engine.MinimumReferenceMilliseconds || durationMilliseconds > engine.MaximumReferenceMilliseconds
            ? $"{engine.Name} needs a {engine.MinimumReferenceMilliseconds / 1000}-{engine.MaximumReferenceMilliseconds / 1000} " +
              $"second recording; this one is {durationMilliseconds / 1000d:0.#} seconds."
            : null;

    public static SpeechEngine? ForRoute(string? routeId) => All.FirstOrDefault(engine => engine.RouteId == routeId);

    public static SpeechEngine? ForKey(string? key) => All.FirstOrDefault(engine => engine.Key == key);

    public static SpeechEngine? ForRoleKind(string? kind) => All.FirstOrDefault(engine => engine.HostRoleKind == kind);
}
