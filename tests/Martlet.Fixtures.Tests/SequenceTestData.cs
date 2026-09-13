using Martlet.Core.Contracts;
using Martlet.Core.Streaming;

namespace Martlet.Fixtures.Tests;

internal static class SequenceTestData
{
    internal static Guid Id(int value) => new(value, 0, 0, new byte[8]);
    internal static TextStreamRequest Request(ProviderRole role = ProviderRole.Llm, long epoch = 0,
        int turn = 2, int attempt = 3, CancellationCapability cancellation = CancellationCapability.DiscardOnly) => new()
    {
        Ids = new() { SessionId = Id(1), TurnId = Id(turn), RequestId = Id(attempt) },
        Epoch = epoch,
        Capabilities = new()
        {
            Version = ContractVersion.Current, ProviderId = "fixture", AdapterVersion = "f03a",
            ModelId = "synthetic", Role = role, Provenance = EvidenceProvenance.Fixture,
            SttPartials = role == ProviderRole.Stt ? CapabilitySupport.Supported : CapabilitySupport.Unsupported,
            LlmTextDeltas = role == ProviderRole.Llm ? CapabilitySupport.Supported : CapabilitySupport.Unsupported,
            TtsAudioTransport = CapabilitySupport.Unsupported, TtsIncrementalSynthesis = CapabilitySupport.Unsupported,
            Cancellation = cancellation, MaxInputBytes = 1024
        }
    };

    internal static ProviderEvent Event(TextStreamRequest request, ProviderEventKind kind, long seq, string? text = null) => new()
    {
        Version = ContractVersion.Current, Ids = request.Ids, ProviderId = request.Capabilities.ProviderId,
        Epoch = request.Epoch, Sequence = seq, Provenance = EvidenceProvenance.Fixture, Kind = kind, Text = text,
        Error = kind == ProviderEventKind.Failed ? new()
        {
            Code = ErrorCode.ProviderFailed,
            Stage = request.Capabilities.Role == ProviderRole.Stt ? Stage.Transcription : Stage.Generation,
            Retryable = false, Summary = "Synthetic fixture provider failure.", ActionId = "fixture.failure"
        } : null
    };

    internal static FixtureScenario Scenario(string name, TextStreamRequest request, params FixtureStep[] steps) => new()
    {
        Version = ContractVersion.Current, Name = name, Label = FixtureScenario.EvidenceLabel,
        Provenance = EvidenceProvenance.Fixture, Request = request, Limits = new(), Steps = steps
    };

    internal static FixtureStep Emit(TextStreamRequest request, ProviderEventKind kind, long seq,
        string? text = null, int at = 0) => new() { AtMilliseconds = at, Action = FixtureAction.Event, Event = Event(request, kind, seq, text) };
    internal static FixtureStep Act(FixtureAction action, int at = 0) => new() { AtMilliseconds = at, Action = action };
}

internal sealed class ManualClock : TimeProvider
{
    private long ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => ticks;
    internal void Advance(TimeSpan time) => ticks += time.Ticks;
}
