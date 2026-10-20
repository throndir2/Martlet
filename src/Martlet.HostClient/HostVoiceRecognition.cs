using System.Text.Json;

namespace Martlet.HostClient;

public sealed partial class HostConnection
{
    public const string VoiceRecognitionRouteId = "martlet.gateway.voice-recognition.v1";
    public const string VoiceRecognitionPath = "/martlet/v1/inference/voice-recognition";
    public const string VoiceRecognitionContractId = "martlet.voice-recognition-relay";
    /// <summary>The longest audio the role takes at once: a minute of 16 kHz mono PCM16.</summary>
    public const int VoiceRecognitionMaximumBytes = 60 * 16_000 * 2;

    /// <summary>Asks the host's Voice recognition role (route <c>martlet.gateway.voice-recognition.v1</c>) whether it is ready: one
    /// JSON object with <c>state</c> ("ready" or "loading"), <c>provider</c> ("cuda" or "cpu") and <c>model</c>. Gateway and
    /// transport failures throw <see cref="HostConnectionException"/>.</summary>
    public async Task<JsonElement> VoiceRecognitionStatusAsync(HostRoute route, CancellationToken cancellationToken = default) =>
        OneVoiceRecognitionAnswer(await VoiceRecognitionOperationAsync(route, VoiceRecognitionPayload("status", null), TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false));

    /// <summary>Who spoke in <paramref name="pcm16"/> (16 kHz mono PCM16 little-endian, at most a minute), on the host's Voice
    /// recognition role: one JSON object with the fields of a speaker analysis (<c>speakers</c>, each <c>local</c>,
    /// <c>voiceprint</c>, <c>clean_seconds</c>, <c>start</c> and <c>end</c>; <c>voices</c>, <c>overlap</c>, <c>whole</c>,
    /// <c>speech_seconds</c>) and <c>milliseconds</c>. The audio is analyzed in memory there and not kept.</summary>
    public async Task<JsonElement> VoiceRecognitionAnalyzeAsync(HostRoute route, byte[] pcm16, CancellationToken cancellationToken = default) =>
        OneVoiceRecognitionAnswer(await VoiceRecognitionOperationAsync(route, VoiceRecognitionPayload("analyze", pcm16), TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false));

    /// <summary>One voiceprint of all of <paramref name="pcm16"/> on the host's Voice recognition role: <c>voiceprint</c> (null
    /// when the audio is too short) and <c>milliseconds</c>.</summary>
    public async Task<JsonElement> VoiceRecognitionVoiceprintAsync(HostRoute route, byte[] pcm16, CancellationToken cancellationToken = default) =>
        OneVoiceRecognitionAnswer(await VoiceRecognitionOperationAsync(route, VoiceRecognitionPayload("voiceprint", pcm16), TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false));

    /// <summary>The JSON payload of one operation: <c>operation</c>, and for audio <c>sample_rate</c> 16000 and
    /// <c>pcm16_base64</c>.</summary>
    public static IReadOnlyDictionary<string, object> VoiceRecognitionPayload(string operation, byte[]? pcm16)
    {
        if (operation is not ("status" or "analyze" or "voiceprint")) throw new ArgumentException("Unknown voice recognition operation.", nameof(operation));
        if (operation == "status") return new Dictionary<string, object> { ["operation"] = operation };
        ArgumentNullException.ThrowIfNull(pcm16);
        if (pcm16.Length is 0 or > VoiceRecognitionMaximumBytes || pcm16.Length % 2 != 0)
            throw new ArgumentException("The audio is empty, longer than a minute or not PCM16.", nameof(pcm16));
        return new Dictionary<string, object>
        {
            ["operation"] = operation, ["sample_rate"] = 16_000, ["pcm16_base64"] = Convert.ToBase64String(pcm16)
        };
    }

    private Task<IReadOnlyList<JsonElement>> VoiceRecognitionOperationAsync(HostRoute route, IReadOnlyDictionary<string, object> payload,
        TimeSpan longest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.RouteId != VoiceRecognitionRouteId || route.Path != VoiceRecognitionPath)
            throw new ArgumentException("The route is not the host's Voice recognition role.", nameof(route));
        return JsonOperationAsync(route, payload, longest, "voice recognition", cancellationToken);
    }

    private static JsonElement OneVoiceRecognitionAnswer(IReadOnlyList<JsonElement> answer) => answer.Count == 1
        ? answer[0]
        : throw new HostConnectionException("stream.invalid", "The host's voice recognition stream was invalid.");
}
