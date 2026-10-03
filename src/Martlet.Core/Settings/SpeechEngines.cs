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

    public static readonly IReadOnlyList<SpeechEngine> All = [F5, Xtts];

    public static SpeechEngine? ForRoute(string? routeId) => All.FirstOrDefault(engine => engine.RouteId == routeId);

    public static SpeechEngine? ForKey(string? key) => All.FirstOrDefault(engine => engine.Key == key);

    public static SpeechEngine? ForRoleKind(string? kind) => All.FirstOrDefault(engine => engine.HostRoleKind == kind);
}
