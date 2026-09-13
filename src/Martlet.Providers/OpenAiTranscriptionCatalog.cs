using Martlet.Core.Contracts;

namespace Martlet.Providers;

public static class OpenAiTranscriptionCatalog
{
    public const string ProviderId = "openai";
    public const string AdapterVersion = "0.1.0";
    public static Uri Origin => OpenAiTransport.Origin;
    internal static Uri Endpoint { get; } = new(Origin, "/v1/audio/transcriptions");
    public static DateOnly DocumentationDate { get; } = new(2026, 9, 12);
    public static IReadOnlyList<string> SupportedModelIds { get; } = Array.AsReadOnly(new[]
    {
        "gpt-transcribe", "gpt-4o-transcribe", "gpt-4o-mini-transcribe",
        "gpt-4o-mini-transcribe-2025-12-15", "whisper-1"
    });

    public static bool SupportsModel(string? upstreamModelId) =>
        SupportedModelIds.Contains(upstreamModelId, StringComparer.Ordinal);

    internal static bool IsApprovedOrigin(Uri? origin) => OpenAiTransport.IsApprovedOrigin(origin);

    // ModelAlias is the Core identifier. UpstreamModelId is never inferred from it.
    public static ProviderCapabilities Describe(string modelAlias, string upstreamModelId)
    {
        ContractRules.Identifier(modelAlias);
        ContractRules.Require(SupportsModel(upstreamModelId),
            "The configured OpenAI transcription model is not supported by this adapter.", ErrorCode.ProviderCapability);
        var capabilities = new ProviderCapabilities
        {
            Version = ContractVersion.Current, ProviderId = ProviderId, AdapterVersion = AdapterVersion,
            ModelId = modelAlias, Role = ProviderRole.Stt, Provenance = EvidenceProvenance.NotRun,
            SttPartials = CapabilitySupport.Unsupported, LlmTextDeltas = CapabilitySupport.Unsupported,
            TtsAudioTransport = CapabilitySupport.Unsupported, TtsIncrementalSynthesis = CapabilitySupport.Unsupported,
            Cancellation = CancellationCapability.Unknown, MaxInputBytes = TranscriptionLimits.HardMaxAudioBytes
        };
        capabilities.Validate();
        return capabilities;
    }
}
