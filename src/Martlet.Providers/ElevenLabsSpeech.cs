using System.Net;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Providers;

/// <summary>ElevenLabs' endpoints and formats as Martlet uses them, from ElevenLabs' documentation (read 2026-10-07; see
/// docs/ELEVENLABS_VOICE.md). Never run against the live service: there is no ElevenLabs account to test with.</summary>
public static class ElevenLabsSpeechCatalog
{
    public const string ProviderId = ElevenLabsSetup.Alias;
    public static Uri Origin { get; } = new(ElevenLabsSetup.Origin);
    public static DateOnly DocumentationDate { get; } = new(2026, 10, 7);

    /// <summary>The Text to Dialogue WebSocket: <c>model_id</c> and <c>output_format</c> in the query, the key in the
    /// <c>xi-api-key</c> header.</summary>
    public const string DialoguePath = "/v1/text-to-dialogue/stream-input";

    /// <summary>Instant Voice Cloning: a multipart form with <c>name</c> and <c>files</c>.</summary>
    public const string ClonePath = "/v1/voices/add";

    /// <summary>Raw PCM, signed 16-bit little-endian (S16LE), mono, 24 kHz: exactly what Martlet's playback takes, so nothing
    /// is decoded or resampled.</summary>
    public const string OutputFormat = "pcm_24000";

    public static bool SupportsModel(string? model) => ElevenLabsSetup.SupportsModel(model);

    /// <summary>The origins a client may talk to: ElevenLabs' own HTTPS origin, or a numeric loopback HTTP origin for a local
    /// fixture that follows the documented protocol (tests and MCP checks, never a saved route).</summary>
    public static bool IsAllowedOrigin(Uri? origin) => origin is { IsAbsoluteUri: true, AbsolutePath: "/", Query: "", Fragment: "", UserInfo: "" } &&
        (origin.Scheme == Uri.UriSchemeHttps && origin.Port == 443 && string.Equals(origin.IdnHost, "api.elevenlabs.io", StringComparison.Ordinal) ||
         origin.Scheme == Uri.UriSchemeHttp && IPAddress.TryParse(origin.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    internal static Uri DialogueUri(Uri origin, string model) =>
        new($"{(origin.Scheme == Uri.UriSchemeHttps ? "wss" : "ws")}://{origin.Authority}{DialoguePath}" +
            $"?model_id={Uri.EscapeDataString(model)}&output_format={OutputFormat}");
}

/// <summary>The voice that speaks a reply with ElevenLabs: <see cref="VoiceId"/>, the voice cloned from the owner's recording,
/// and <see cref="ModelId"/>, <see cref="ElevenLabsSetup.V4Turbo"/> or <see cref="ElevenLabsSetup.V3Conversational"/>.</summary>
public sealed record ElevenLabsVoiceTarget(string VoiceId, string ModelId)
{
    public override string ToString() => nameof(ElevenLabsVoiceTarget);
}

/// <summary>Speaks one reply segment with ElevenLabs and returns its 24 kHz mono PCM16 bytes as they arrive. The key is
/// resolved for the segment's own one-use authorization. Failures throw <see cref="ElevenLabsException"/>.</summary>
public interface IElevenLabsSpeechClient
{
    IAsyncEnumerable<byte[]> StreamAsync(ElevenLabsVoiceTarget target, BoundedSpeechInput input, SpeechSynthesisLimits limits,
        DateTimeOffset deadline, CancellationToken cancellationToken);
}

/// <summary>An ElevenLabs request failed: <see cref="Code"/> says how, <see cref="Detail"/> is ElevenLabs' own short reason
/// (never a key or request content), for the local log, MCP and the owner.</summary>
public sealed class ElevenLabsException(ProviderFailureCode code, string? detail = null)
    : Exception(detail is null ? $"ElevenLabs failed ({code})." : $"ElevenLabs failed ({code}): {detail}")
{
    public ProviderFailureCode Code { get; } = code;
    public string? Detail { get; } = detail;
}

/// <summary>One reply segment spoken by ElevenLabs with the owner's cloned voice, bound to ElevenLabs' origin and the chosen
/// model. ElevenLabs' audio tags in the text ([laughs], [whispers]) reach it as written.</summary>
public sealed class ElevenLabsSpeechSynthesisStream : PcmChunkSpeechSynthesisStream
{
    private readonly IElevenLabsSpeechClient client;
    private readonly ElevenLabsVoiceTarget target;

    public ElevenLabsSpeechSynthesisStream(IElevenLabsSpeechClient client, ElevenLabsVoiceTarget target, ProviderRequestContext context,
        SpeechSynthesisSelection selection, BoundedSpeechInput input, SpeechSynthesisLimits limits,
        SpeechDisclosureAuthorization? authorization, TimeProvider? clock = null, CancellationToken callerToken = default)
        : base(ElevenLabsSpeechCatalog.ProviderId, CancellationCapability.RequestAbort,
            Binding(target ?? throw new ArgumentNullException(nameof(target))), context, selection, input, limits, authorization, clock,
            callerToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
        this.target = target;
    }

    /// <summary>The binding a caller authorizes for ElevenLabs: its origin, the TTS role and the model.</summary>
    public static ProviderCredentialBinding Binding(ElevenLabsVoiceTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new(ElevenLabsSpeechCatalog.Origin, ProviderRole.Tts, target.ModelId);
    }

    /// <summary>The speech selection that names <paramref name="target"/>.</summary>
    public static SpeechSynthesisSelection Selection(ElevenLabsVoiceTarget target) =>
        new(ElevenLabsSetup.Alias, target.ModelId, target.VoiceId, SpeechOutputFormat.Pcm24KhzMono16Le);

    private protected override IAsyncEnumerable<byte[]> Chunks(ProviderRequestContext context, BoundedSpeechInput input,
        SpeechSynthesisLimits limits, DateTimeOffset deadline, CancellationToken cancellationToken) =>
        client.StreamAsync(target, input, limits, deadline, cancellationToken);

    public override string ToString() => nameof(ElevenLabsSpeechSynthesisStream);
}
