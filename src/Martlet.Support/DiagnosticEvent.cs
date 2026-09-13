using System.Text.Json.Serialization;
using Martlet.Core.Contracts;
using Martlet.Diagnostics;

namespace Martlet.Support;

public enum DiagnosticSeverity { Information, Warning, Error }
public enum SupportComponent { Application, Setup, Diagnostics, Audio, Conversation, Provider, Participation }
public enum AdapterAlias { None, Fixture, OpenAi }
public enum MetadataState { Idle, Running, Completed, Suppressed, Refused, Canceled, Failed }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DiagnosticEvent : IContract
{
    public required int SchemaVersion { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public double? MonotonicDurationMilliseconds { get; init; }
    public required DiagnosticSeverity Severity { get; init; }
    public required SupportComponent Component { get; init; }
    public required Stage Stage { get; init; }
    public ProviderRole? Role { get; init; }
    public required string Code { get; init; }
    public required string ActionId { get; init; }
    public required EvidenceProvenance Provenance { get; init; }
    public required EvidenceFreshness Freshness { get; init; }
    public required AdapterAlias Adapter { get; init; }
    public Guid? TraceId { get; init; }
    public Guid? TurnId { get; init; }
    public int? Count { get; init; }
    public int? QueueDepth { get; init; }
    public MetadataState? PreviousState { get; init; }
    public MetadataState? State { get; init; }
    public SuppressionReason? Suppression { get; init; }

    public void Validate()
    {
        Guard.Require(SchemaVersion == 1, SupportFailure.UnsupportedVersion);
        Guard.Utc(TimestampUtc);
        Guard.Defined(Severity); Guard.Defined(Component); Guard.Defined(Stage);
        Guard.Defined(Provenance); Guard.Defined(Freshness); Guard.Defined(Adapter);
        if (Role is { } role) Guard.Defined(role);
        Guard.Require(Role is null || Stage == Stage.Provider ||
            Role == ProviderRole.Stt && Stage == Stage.Transcription ||
            Role == ProviderRole.Llm && Stage == Stage.Generation ||
            Role == ProviderRole.Tts && Stage == Stage.Synthesis);
        if (PreviousState is { } previous) Guard.Defined(previous);
        if (State is { } state) Guard.Defined(state);
        if (Suppression is { } reason) Guard.Defined(reason);
        Guard.Require(MonotonicDurationMilliseconds is null or (>= 0 and <= 86_400_000));
        Guard.Require(TraceId != Guid.Empty && TurnId != Guid.Empty);
        Guard.Require(Count is null or (>= 0 and <= 1_000_000) && QueueDepth is null or (>= 0 and <= 65_536));
        Guard.Require(QueueDepth is null || Component is SupportComponent.Audio or SupportComponent.Conversation or SupportComponent.Provider);
        Guard.Require(PreviousState is null || State is not null && State != PreviousState);
        Guard.Require(Suppression is null || State == MetadataState.Suppressed);
        var finding = CatalogFinding(Code);
        Guard.Require(ActionId == finding.ActionId);
        Guard.Require(Provenance is EvidenceProvenance.Live or EvidenceProvenance.Fixture ||
            Freshness == EvidenceFreshness.Unknown && finding.Outcome != ProbeOutcome.Passed);
        Guard.Require(finding.Outcome != ProbeOutcome.Passed || Freshness == EvidenceFreshness.Current);
        Guard.Require(!Code.StartsWith("fixture.", StringComparison.Ordinal) || Provenance == EvidenceProvenance.Fixture);
        Guard.Require(Adapter != AdapterAlias.Fixture || Provenance == EvidenceProvenance.Fixture);
        Guard.Require(Adapter != AdapterAlias.OpenAi || Provenance == EvidenceProvenance.Live);
        Guard.Require(finding.Outcome != ProbeOutcome.Failed || Severity == DiagnosticSeverity.Error);
    }

    internal static DiagnosticFinding CatalogFinding(string code) =>
        DiagnosticCatalog.Findings.FirstOrDefault(f => f.Id == code) ?? throw new SupportException(SupportFailure.InvalidData);

    public static DiagnosticEvent FromProbe(ProbeResult probe, DateTimeOffset timestampUtc,
        Guid? traceId = null, Guid? turnId = null)
    {
        ReportProjection.ValidateProbe(probe);
        var value = new DiagnosticEvent
        {
            SchemaVersion = 1, TimestampUtc = timestampUtc, Component = SupportComponent.Diagnostics,
            Stage = probe.Stage, Code = probe.DiagnosticCode!, ActionId = probe.ActionId!,
            Provenance = probe.Provenance, Freshness = probe.Freshness,
            Adapter = probe.Provenance == EvidenceProvenance.Fixture ? AdapterAlias.Fixture : AdapterAlias.None,
            Severity = probe.Outcome == ProbeOutcome.Failed ? DiagnosticSeverity.Error :
                probe.Outcome == ProbeOutcome.Passed ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning,
            MonotonicDurationMilliseconds = probe.DurationMilliseconds, TraceId = traceId, TurnId = turnId
        };
        value.Validate();
        return value;
    }
}

public static class SupportJson
{
    public const int MaximumEventBytes = 4096;
    public static byte[] WriteEvent(DiagnosticEvent value) => Write(value, MaximumEventBytes);
    public static DiagnosticEvent ReadEvent(ReadOnlyMemory<byte> bytes) => Read<DiagnosticEvent>(bytes, MaximumEventBytes);

    internal static byte[] Write<T>(T value, int maximum = ContractRules.MaxJsonBytes) where T : IContract
    {
        try { return ContractJson.Write(value, maximum); }
        catch (ContractException) { throw new SupportException(SupportFailure.InvalidData); }
    }

    internal static T Read<T>(ReadOnlyMemory<byte> bytes, int maximum = ContractRules.MaxJsonBytes) where T : IContract
    {
        try { return ContractJson.Read<T>(bytes, maximum); }
        catch (ContractException) { throw new SupportException(SupportFailure.InvalidData); }
    }
}
