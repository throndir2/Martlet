using Martlet.Core.Contracts;

namespace Martlet.Providers;

public static class OpenAiTextGenerationCatalog
{
    public const string ProviderId = "openai";
    public const string AdapterVersion = "0.2.0";
    public static Uri Origin => OpenAiTransport.Origin;
    internal static Uri Endpoint { get; } = new(Origin, "/v1/responses");
    public static DateOnly DocumentationDate { get; } = new(2026, 9, 21);
    public static IReadOnlyList<string> SupportedModelIds { get; } =
        System.Array.AsReadOnly(new[]
        {
            "gpt-4.1-mini-2025-04-14",
            "gpt-4.1-2025-04-14"
        });

    /// <summary>Prefilled suggestion in Setup; never applied without the owner's consent.</summary>
    public const string DefaultModelId = "gpt-4.1-mini-2025-04-14";

    public static bool SupportsModel(string? upstreamModelId) =>
        SupportedModelIds.Contains(upstreamModelId, StringComparer.Ordinal);

    public static ProviderCapabilities Describe(TextModelSelection model)
    {
        ArgumentNullException.ThrowIfNull(model);
        ContractRules.Identifier(model.ModelAlias);
        ContractRules.Require(SupportsModel(model.UpstreamModelId),
            "The configured OpenAI model is unsupported by this text adapter.", ErrorCode.ProviderCapability);
        return Capabilities(model.ModelAlias, EvidenceProvenance.NotRun);
    }

    internal static ProviderCapabilities AttemptCapabilities(string alias, EvidenceProvenance provenance) =>
        Capabilities(alias, provenance);

    private static ProviderCapabilities Capabilities(string alias, EvidenceProvenance provenance)
    {
        var result = new ProviderCapabilities
        {
            Version = ContractVersion.Current, ProviderId = ProviderId, AdapterVersion = AdapterVersion,
            ModelId = alias, Role = ProviderRole.Llm, Provenance = provenance,
            SttPartials = CapabilitySupport.Unsupported,
            LlmTextDeltas = provenance == EvidenceProvenance.NotRun ? CapabilitySupport.Unknown : CapabilitySupport.Supported,
            TtsAudioTransport = CapabilitySupport.Unsupported, TtsIncrementalSynthesis = CapabilitySupport.Unsupported,
            Cancellation = provenance == EvidenceProvenance.NotRun ? CancellationCapability.Unknown : CancellationCapability.RequestAbort,
            MaxInputBytes = BoundedTextInput.HardMaxInputUtf8Bytes
        };
        result.Validate();
        return result;
    }
}
