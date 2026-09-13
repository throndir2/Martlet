using Martlet.Core.Contracts;

namespace Martlet.Core.Streaming;

public enum SequenceDecision { Accepted, DuplicateDiscarded, StaleDiscarded, ClosedDiscarded, Backpressured, Terminal }
public enum SequenceIssue
{
    None, InvalidInput, WrongCorrelation, WrongEpoch, WrongProvenance, SequenceGap,
    ConflictingDuplicate, InvalidOrder, EmptyCompletion, UnsupportedCapability,
    UnknownCapability, EventLimit, TextLimit, QueueOverflow, FirstEventDeadline,
    IdleDeadline, TotalDeadline, MissingTerminal, ProviderFailure
}
public enum TextOverflowPolicy { Backpressure, Fail }
public enum CancellationRequest { None, LocalDiscardOnly, AbortRequest, CooperativeComputeCancel }
public enum AttemptTransition { Retry, Replace }

public sealed record SequenceLimits : IContract
{
    public int MaxIngressEvents { get; init; } = 1024;
    public int MaxTextCharacters { get; init; } = 65_536;
    public int MaxQueuedChunks { get; init; } = 2;
    public int MaxAttempts { get; init; } = 32;
    public TimeSpan FirstEventTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public void Validate()
    {
        ContractRules.Require(MaxIngressEvents is >= 2 and <= 4096 &&
            MaxTextCharacters is >= 1 and <= 262_144 && MaxQueuedChunks is >= 1 and <= 128 &&
            MaxAttempts is >= 1 and <= 64, "Sequence resource limits are out of range.");
        foreach (var timeout in new[] { FirstEventTimeout, IdleTimeout, TotalTimeout })
            ContractRules.Require(timeout > TimeSpan.Zero && timeout <= TimeSpan.FromMinutes(5),
                "Sequence deadlines must be positive and at most five minutes.");
    }
}

public sealed record TextStreamRequest : IContract
{
    public required CorrelationIds Ids { get; init; }
    public required long Epoch { get; init; }
    public required ProviderCapabilities Capabilities { get; init; }

    public void Validate()
    {
        ContractRules.Require(Ids is not null && Capabilities is not null, "A correlated route is required.");
        Ids!.Validate();
        Capabilities!.Validate();
        ContractRules.Require(Epoch is >= 0 and < int.MaxValue, "An attempt epoch must leave room for local stop.");
        ContractRules.Require(Capabilities.Role is ProviderRole.Stt or ProviderRole.Llm,
            "This validator supports STT/LLM text events, not audio transport.", ErrorCode.NotImplemented);
        ContractRules.Require(Capabilities.Provenance is EvidenceProvenance.Fixture or EvidenceProvenance.Live,
            "A stream must declare observed route provenance.");
    }
}

public sealed record ValidatedTextChunk(CorrelationIds Ids, long Epoch, long Sequence, string Text);

// This metadata intentionally contains no generated/transcribed text or provider error bodies.
public sealed record SequenceSnapshot(
    CorrelationIds Ids,
    long RequestEpoch,
    long CurrentEpoch,
    ProviderEventKind? ProviderTerminal,
    TurnResult? Result,
    SequenceIssue Issue,
    CancellationRequest CancellationRequested,
    CancellationCapability CancellationCapability,
    int IngressEvents,
    int AcceptedEvents,
    int TextCharacters,
    int DeliveredChunks,
    int QueuedChunks,
    int PeakQueuedChunks,
    bool LocallyStopped);

public sealed record SequenceUpdate(SequenceDecision Decision, SequenceSnapshot Snapshot);
