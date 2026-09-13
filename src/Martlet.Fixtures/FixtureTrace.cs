using Martlet.Core.Contracts;
using Martlet.Core.Streaming;

namespace Martlet.Fixtures;

public sealed record FixtureDelivery(CorrelationIds Ids, long Epoch, long Sequence, int Characters);
public sealed record FixtureObservation(
    int AtMilliseconds, FixtureAction Action, SequenceDecision Decision,
    SequenceSnapshot Snapshot, IReadOnlyList<FixtureDelivery> Deliveries);

public sealed record FixtureTrace : IContract
{
    public required ContractVersion Version { get; init; }
    public required string Name { get; init; }
    public required string Label { get; init; }
    public required EvidenceProvenance Provenance { get; init; }
    public required IReadOnlyList<FixtureObservation> Observations { get; init; }
    public required SequenceSnapshot Final { get; init; }

    public void Validate()
    {
        ContractRules.Require(Version is not null && Final is not null &&
            Observations is { Count: >= 1 and <= 129 }, "A bounded fixture trace is required.");
        Version!.Validate();
        ContractRules.Identifier(Name);
        ContractRules.Require(Label == FixtureScenario.EvidenceLabel && Provenance == EvidenceProvenance.Fixture,
            "A trace is fixture evidence, never provider readiness.");
        ValidateSnapshot(Final!);
        ContractRules.Require(Final!.Result is not null, "A trace must end with an explicit turn result.");
        var previous = 0;
        foreach (var item in Observations!)
        {
            ContractRules.Require(item is not null && item.Snapshot is not null &&
                item.Deliveries is { Count: <= 128 }, "A bounded observation is required.");
            ContractRules.Defined(item!.Action);
            ContractRules.Defined(item.Decision);
            ContractRules.Require(item.AtMilliseconds >= previous && item.AtMilliseconds <= 900_000,
                "Trace time must be bounded and monotonic.");
            previous = item.AtMilliseconds;
            ValidateSnapshot(item.Snapshot!);
            foreach (var delivery in item.Deliveries!)
            {
                ContractRules.Require(delivery is not null && delivery.Ids is not null, "Delivery correlation is required.");
                delivery!.Ids!.Validate();
                ContractRules.Require(delivery.Ids == item.Snapshot!.Ids && delivery.Epoch == item.Snapshot.RequestEpoch &&
                    delivery.Sequence is >= 0 and <= 4095 && delivery.Characters is >= 1 and <= ContractRules.MaxTextCharacters,
                    "Delivery must belong to the observed current attempt.");
            }
        }
        ContractRules.Require(Final == Observations[^1].Snapshot, "The final snapshot must match the last observation.");
    }

    private static void ValidateSnapshot(SequenceSnapshot value)
    {
        ContractRules.Require(value.Ids is not null, "Snapshot correlation is required.");
        value.Ids!.Validate();
        ContractRules.Defined(value.Issue);
        ContractRules.Defined(value.CancellationRequested);
        ContractRules.Defined(value.CancellationCapability);
        if (value.ProviderTerminal is { } terminal)
            ContractRules.Require(Enum.IsDefined(terminal) && terminal is not ProviderEventKind.Started and not ProviderEventKind.TextDelta,
                "A provider terminal must be terminal.");
        if (value.Result is not null)
        {
            value.Result.Validate();
            ContractRules.Require(value.Result.Ids == value.Ids && value.Result.Provenance == EvidenceProvenance.Fixture,
                "A trace result must retain fixture correlation.");
        }
        var expectedOutcome = value.ProviderTerminal switch
        {
            ProviderEventKind.Completed => TurnOutcome.Completed,
            ProviderEventKind.Refused => TurnOutcome.Refused,
            ProviderEventKind.Canceled => TurnOutcome.Canceled,
            ProviderEventKind.Failed => TurnOutcome.Failed,
            ProviderEventKind.NoSpeech => TurnOutcome.Suppressed,
            _ => (TurnOutcome?)null
        };
        ContractRules.Require(expectedOutcome is null || value.Result?.Outcome == expectedOutcome,
            "Provider terminal and turn outcome must agree.");
        ContractRules.Require(value.ProviderTerminal != ProviderEventKind.NoSpeech ||
            value.Result?.Suppression == SuppressionReason.NoSpeech, "No speech must retain its suppression reason.");
        ContractRules.Require(value.Result?.Outcome is not (TurnOutcome.Completed or TurnOutcome.Refused) ||
            value.ProviderTerminal is not null, "Completion and refusal require provider terminals.");
        ContractRules.Require(value.RequestEpoch is >= 0 and < int.MaxValue &&
            value.CurrentEpoch >= value.RequestEpoch && value.CurrentEpoch <= int.MaxValue &&
            value.IngressEvents is >= 0 and <= 4097 && value.AcceptedEvents >= 0 &&
            value.AcceptedEvents <= value.IngressEvents && value.TextCharacters is >= 0 and <= 262_144 &&
            value.DeliveredChunks >= 0 && value.DeliveredChunks <= value.AcceptedEvents &&
            value.QueuedChunks >= 0 && value.QueuedChunks <= value.PeakQueuedChunks &&
            value.PeakQueuedChunks is >= 0 and <= 128, "Trace counters are out of range.");
    }
}
