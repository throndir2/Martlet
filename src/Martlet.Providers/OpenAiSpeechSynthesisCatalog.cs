using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Providers;

public static class OpenAiSpeechSynthesisCatalog
{
    public const string ProviderId = "openai";
    public const string AdapterVersion = "0.1.0";
    public static Uri Origin => OpenAiTransport.Origin;
    internal static Uri Endpoint { get; } = new(Origin, "/v1/audio/speech");
    public static DateOnly DocumentationDate { get; } = new(2026, 9, 12);
    public static IReadOnlyList<string> SupportedModelIds { get; } =
        Array.AsReadOnly(new[] { "gpt-4o-mini-tts-2025-12-15" });
    public static IReadOnlyList<string> SupportedVoices { get; } = Array.AsReadOnly(new[] { "alloy", "coral" });

    /// <summary>Prefilled suggestions in Setup; never applied without the owner's consent.</summary>
    public const string DefaultModelId = "gpt-4o-mini-tts-2025-12-15";
    public const string DefaultVoice = "alloy";
    public static PcmFormat PcmFormat { get; } = new()
    {
        SampleRate = 24_000, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian
    };

    public static bool SupportsModel(string? model) => SupportedModelIds.Contains(model, StringComparer.Ordinal);
    public static bool SupportsVoice(string? voice) => SupportedVoices.Contains(voice, StringComparer.Ordinal);
    public static bool SupportsFormat(SpeechOutputFormat format) => format == SpeechOutputFormat.Pcm24KhzMono16Le;

    public static ProviderCapabilities Describe(SpeechSynthesisSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ContractRules.Identifier(selection.ModelAlias);
        ContractRules.Require(SupportsModel(selection.UpstreamModelId) && SupportsVoice(selection.Voice) &&
            SupportsFormat(selection.OutputFormat), "The speech model, voice or format is unsupported by this adapter.",
            ErrorCode.ProviderCapability);
        return Capabilities(selection.ModelAlias, EvidenceProvenance.NotRun);
    }

    internal static ProviderCapabilities AttemptCapabilities(string alias, EvidenceProvenance provenance) =>
        Capabilities(alias, provenance);

    private static ProviderCapabilities Capabilities(string alias, EvidenceProvenance provenance)
    {
        var result = new ProviderCapabilities
        {
            Version = ContractVersion.Current, ProviderId = ProviderId, AdapterVersion = AdapterVersion,
            ModelId = alias, Role = ProviderRole.Tts, Provenance = provenance,
            SttPartials = CapabilitySupport.Unsupported, LlmTextDeltas = CapabilitySupport.Unsupported,
            TtsAudioTransport = provenance == EvidenceProvenance.NotRun ? CapabilitySupport.Unknown : CapabilitySupport.Supported,
            TtsIncrementalSynthesis = CapabilitySupport.Unknown,
            Cancellation = provenance == EvidenceProvenance.NotRun ? CancellationCapability.Unknown : CancellationCapability.RequestAbort,
            MaxInputBytes = BoundedSpeechInput.HardMaxUtf8Bytes
        };
        result.Validate();
        return result;
    }
}
