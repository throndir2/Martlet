using System.Text.Json.Serialization;

namespace Martlet.Core.Contracts;

public sealed record CorrelationIds : IContract
{
    public required Guid SessionId { get; init; }
    public required Guid TurnId { get; init; }
    public required Guid RequestId { get; init; }

    public void Validate() => ContractRules.Require(
        SessionId != Guid.Empty && TurnId != Guid.Empty && RequestId != Guid.Empty,
        "Session, turn and request identifiers must be nonempty UUIDs.");
}

public enum ProviderEventKind { Started, TextDelta, Completed, Refused, Canceled, Failed, NoSpeech }

public sealed record ProviderEvent : IContract
{
    public required ContractVersion Version { get; init; }
    public required CorrelationIds Ids { get; init; }
    public required string ProviderId { get; init; }
    public required long Epoch { get; init; }
    public required long Sequence { get; init; }
    public required EvidenceProvenance Provenance { get; init; }
    public required ProviderEventKind Kind { get; init; }
    public string? Text { get; init; }
    public long? FinalSampleCount { get; init; }
    public MartletError? Error { get; init; }

    [JsonIgnore]
    public bool IsTerminal => Kind is not ProviderEventKind.Started and not ProviderEventKind.TextDelta;

    public void Validate()
    {
        ContractRules.Require(Version is not null && Ids is not null, "Version and correlation are required.");
        Version!.Validate();
        Ids!.Validate();
        ContractRules.Identifier(ProviderId);
        ContractRules.Require(Epoch is >= 0 and <= int.MaxValue && Sequence is >= 0 and <= int.MaxValue,
            "Epoch or event sequence is out of range.");
        ContractRules.Defined(Provenance);
        ContractRules.Defined(Kind);
        ContractRules.Require(Provenance is EvidenceProvenance.Fixture or EvidenceProvenance.Live,
            "Emitted provider events must declare fixture or live provenance.");
        ContractRules.Require((Kind == ProviderEventKind.Failed) == (Error is not null),
            "Only a failed event must carry an error.");
        Error?.Validate();
        ContractRules.Require(Text is null || Kind is ProviderEventKind.TextDelta or ProviderEventKind.Completed or ProviderEventKind.Refused,
            "This event cannot contain text.");
        if (Text is not null)
            ContractRules.Text(Text, ContractRules.MaxTextCharacters);
        ContractRules.Require(Kind != ProviderEventKind.TextDelta || !string.IsNullOrEmpty(Text), "A text delta must not be empty.");
        ContractRules.Require(FinalSampleCount is null || (Kind == ProviderEventKind.Completed && FinalSampleCount is >= 0 and <= 4_320_000),
            "Only completion can declare a bounded final sample count.");
    }
}

public enum TurnOutcome { Completed, Suppressed, Canceled, Failed }
public enum SuppressionReason { NoSpeech, NotAddressed, PolicyDisabled, Cooldown, Busy, SelfAudio, ConsentMissing }

public sealed record TurnResult : IContract
{
    public required ContractVersion Version { get; init; }
    public required CorrelationIds Ids { get; init; }
    public required EvidenceProvenance Provenance { get; init; }
    public required TurnOutcome Outcome { get; init; }
    public SuppressionReason? Suppression { get; init; }
    public MartletError? Error { get; init; }

    public void Validate()
    {
        ContractRules.Require(Version is not null && Ids is not null, "Version and correlation are required.");
        Version!.Validate();
        Ids!.Validate();
        ContractRules.Defined(Provenance);
        ContractRules.Defined(Outcome);
        ContractRules.Require(Provenance is EvidenceProvenance.Fixture or EvidenceProvenance.Live, "A turn result needs observed provenance.");
        ContractRules.Require((Outcome == TurnOutcome.Suppressed) == (Suppression is not null), "Only suppression carries a policy reason.");
        if (Suppression is { } reason)
            ContractRules.Defined(reason);
        ContractRules.Require((Outcome == TurnOutcome.Failed) == (Error is not null), "Suppression is not a provider failure.");
        Error?.Validate();
    }
}

public enum ProviderRole { Stt, Llm, Tts }
public enum CapabilitySupport { Unknown, Unsupported, Supported }
public enum CancellationCapability { Unknown, DiscardOnly, RequestAbort, CooperativeComputeCancel }
public enum ProviderFeature { SttPartials, LlmTextDeltas, TtsAudioTransport, TtsIncrementalSynthesis }

public sealed record ProviderCapabilities : IContract
{
    public required ContractVersion Version { get; init; }
    public required string ProviderId { get; init; }
    public required string AdapterVersion { get; init; }
    public required string ModelId { get; init; }
    public required ProviderRole Role { get; init; }
    public required EvidenceProvenance Provenance { get; init; }
    public required CapabilitySupport SttPartials { get; init; }
    public required CapabilitySupport LlmTextDeltas { get; init; }
    public required CapabilitySupport TtsAudioTransport { get; init; }
    public required CapabilitySupport TtsIncrementalSynthesis { get; init; }
    public required CancellationCapability Cancellation { get; init; }
    public required int MaxInputBytes { get; init; }

    public void Validate()
    {
        ContractRules.Require(Version is not null, "Version is required.");
        Version!.Validate();
        ContractRules.Identifier(ProviderId);
        ContractRules.Identifier(AdapterVersion);
        ContractRules.Identifier(ModelId);
        ContractRules.Defined(Role);
        ContractRules.Defined(Provenance);
        ContractRules.Defined(SttPartials);
        ContractRules.Defined(LlmTextDeltas);
        ContractRules.Defined(TtsAudioTransport);
        ContractRules.Defined(TtsIncrementalSynthesis);
        ContractRules.Defined(Cancellation);
        ContractRules.Require(MaxInputBytes is > 0 and <= 16_777_216, "Provider input limit is out of range.");
        ContractRules.Require(Role == ProviderRole.Stt || SttPartials != CapabilitySupport.Supported, "STT partials require an STT role.");
        ContractRules.Require(Role == ProviderRole.Llm || LlmTextDeltas != CapabilitySupport.Supported, "LLM deltas require an LLM role.");
        ContractRules.Require(Role == ProviderRole.Tts || (TtsAudioTransport != CapabilitySupport.Supported && TtsIncrementalSynthesis != CapabilitySupport.Supported),
            "TTS features require a TTS role.");
        ContractRules.Require(Provenance is EvidenceProvenance.Fixture or EvidenceProvenance.Live ||
            (SttPartials != CapabilitySupport.Supported && LlmTextDeltas != CapabilitySupport.Supported &&
             TtsAudioTransport != CapabilitySupport.Supported && TtsIncrementalSynthesis != CapabilitySupport.Supported &&
             Cancellation == CancellationCapability.Unknown), "Unobserved capabilities cannot claim support.");
    }

    public void RequireFeature(ProviderFeature feature)
    {
        Validate();
        var support = feature switch
        {
            ProviderFeature.SttPartials => SttPartials,
            ProviderFeature.LlmTextDeltas => LlmTextDeltas,
            ProviderFeature.TtsAudioTransport => TtsAudioTransport,
            ProviderFeature.TtsIncrementalSynthesis => TtsIncrementalSynthesis,
            _ => CapabilitySupport.Unknown
        };
        ContractRules.Require(support == CapabilitySupport.Supported,
            "The selected provider feature is unsupported or unverified.", ErrorCode.ProviderCapability);
    }
}
