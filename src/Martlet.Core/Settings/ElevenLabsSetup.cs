using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>ElevenLabs as the Voice: the owner's own ElevenLabs API key, a voice cloned once from one of the owner's saved
/// speaking voices (Instant Voice Cloning, <c>POST /v1/voices/add</c>) and replies spoken with that voice and ElevenLabs'
/// audio tags over the Text to Dialogue WebSocket (<c>/v1/text-to-dialogue/stream-input</c>). Built from ElevenLabs'
/// documentation (elevenlabs.io/docs, read 2026-10-07); it was never run against the live service. The route is
/// <see cref="SetupRouteType.ElevenLabs"/>: its <see cref="SetupRoute.VoiceId"/> is the cloned voice's ElevenLabs
/// <c>voice_id</c> and its <see cref="SetupRoute.ClonedVoice"/> says which saved recording was uploaded.</summary>
public static class ElevenLabsSetup
{
    public const string Alias = "elevenlabs-tts";
    public const string Origin = "https://api.elevenlabs.io";
    public const string Pricing = "https://elevenlabs.io/pricing/api";
    public const string Documentation = "https://elevenlabs.io/docs/eleven-api/guides/how-to/websockets/realtime-tdd";

    /// <summary>Eleven v4 Turbo: real time (median model latency about 100 ms, excluding the network), audio tags, one voice
    /// per connection; ElevenLabs offers it through the Text to Dialogue WebSocket only.</summary>
    public const string V4Turbo = "eleven_v4_turbo";

    /// <summary>Eleven v3 Conversational: real time (about 280 ms), audio tags, one voice per connection. The WebSocket's API
    /// reference names it as the default model, so it is the choice when ElevenLabs refuses <see cref="V4Turbo"/> there.</summary>
    public const string V3Conversational = "eleven_v3_conversational";

    /// <summary>The models Martlet speaks with, the recommended one first.</summary>
    public static IReadOnlyList<string> ModelIds { get; } = Array.AsReadOnly(new[] { V4Turbo, V3Conversational });

    public static string DefaultModelId => V4Turbo;

    public static bool SupportsModel(string? model) => ModelIds.Contains(model, StringComparer.Ordinal);

    /// <summary>The model in words for the owner.</summary>
    public static string ModelName(string? model) => model switch
    {
        V4Turbo => "Eleven v4 Turbo",
        V3Conversational => "Eleven v3 Conversational",
        _ => model ?? "an unknown model"
    };

    public const string Disclosure =
        "ElevenLabs requests cost money from your ElevenLabs account. Saving uploads the chosen recording once to clone it; " +
        "after that, only reply text is sent, and only while Martlet speaks.";

    /// <summary>Whether <paramref name="value"/> is an ElevenLabs <c>voice_id</c> Martlet accepts: 1-64 ASCII letters and digits
    /// (ElevenLabs' IDs are 20 such characters).</summary>
    public static bool IsVoiceId(string? value) => value is { Length: >= 1 and <= 64 } && value.All(char.IsAsciiLetterOrDigit);

    /// <summary>Speaking with ElevenLabs: <paramref name="voiceId"/> (the voice cloned from <paramref name="cloned"/>) and
    /// <paramref name="modelId"/>. The key the route used before stays when it was an ElevenLabs route; otherwise the key is
    /// added afterwards (<see cref="SetupService"/>). The same selection again changes nothing.</summary>
    public static AppSettings Select(AppSettings settings, string modelId, string voiceId, ClonedVoiceSettings cloned)
    {
        settings.Validate();
        ArgumentNullException.ThrowIfNull(cloned);
        ContractRules.Require(settings.Setup is not null, "Open setup before choosing ElevenLabs.");
        ContractRules.Require(SupportsModel(modelId), $"Choose {ModelName(V4Turbo)} or {ModelName(V3Conversational)}.",
            ErrorCode.ProviderCapability);
        var old = settings.Setup!.Routes.SingleOrDefault(route => route.Role == SetupRole.Tts);
        // The same selection, still on, changes nothing; one a restore turned off is chosen again, so it comes back on.
        if (old is { RouteType: SetupRouteType.ElevenLabs, Enabled: true } && old.ModelId == modelId && old.VoiceId == voiceId &&
            old.ClonedVoice == cloned)
            return settings;
        return SetupSettings.ReplaceRoute(settings, new()
        {
            RouteSchemaVersion = 1, RouteType = SetupRouteType.ElevenLabs, Enabled = true, Role = SetupRole.Tts,
            ProviderAlias = Alias, Origin = Origin, ModelId = modelId, VoiceId = voiceId,
            CredentialId = old?.RouteType == SetupRouteType.ElevenLabs ? old.CredentialId : null,
            ConfigurationRevision = Guid.NewGuid(), ClonedVoice = cloned
        });
    }
}

/// <summary>Which of the owner's saved speaking voices an ElevenLabs route's voice was cloned from: the voice's ID in the
/// voice library, the SHA-256 of the recording that was uploaded, its name when it was cloned (shown on Companion › Voice;
/// never sent to MCP) and whether ElevenLabs asked for the voice to be verified before it speaks. The same voice and
/// recording again is used without another upload.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClonedVoiceSettings : IContract
{
    public required int SchemaVersion { get; init; }
    public required string LibraryVoiceId { get; init; }
    public required string AudioSha256 { get; init; }
    public required string Name { get; init; }
    public required bool RequiresVerification { get; init; }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == 1, "Unsupported cloned voice version.", ErrorCode.UnsupportedVersion);
        SelfHostSetup.Sha256(LibraryVoiceId);
        SelfHostSetup.Sha256(AudioSha256);
        ContractRules.Require(Name is { Length: >= 1 and <= 128 } && !string.IsNullOrWhiteSpace(Name) && !Name.Any(char.IsControl),
            "A cloned voice needs the name of the voice it was made from.");
    }
}
