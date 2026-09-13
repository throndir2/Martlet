using Martlet.Core.Contracts;
using Martlet.Core.Streaming;

namespace Martlet.Fixtures;

public enum FixtureAction { Event, RawEvent, Drain, Poll, Stop, Cancel, End, Suppress, Retry, Replace }

public sealed record FixtureStep : IContract
{
    public required int AtMilliseconds { get; init; }
    public required FixtureAction Action { get; init; }
    public ProviderEvent? Event { get; init; }
    public string? RawEvent { get; init; }
    public TextStreamRequest? NextRequest { get; init; }
    public SuppressionReason? Suppression { get; init; }
    public TextOverflowPolicy Overflow { get; init; } = TextOverflowPolicy.Backpressure;
    public int ReadCount { get; init; } = 1;

    public void Validate()
    {
        ContractRules.Defined(Action);
        ContractRules.Defined(Overflow);
        ContractRules.Require(AtMilliseconds is >= 0 and <= 900_000 && ReadCount is >= 1 and <= 128,
            "Fixture schedule or read count is out of range.");
        ContractRules.Require((Action == FixtureAction.Event) == (Event is not null) &&
            (Action == FixtureAction.RawEvent) == (RawEvent is not null) &&
            (Action is FixtureAction.Retry or FixtureAction.Replace) == (NextRequest is not null) &&
            (Action == FixtureAction.Suppress) == (Suppression is not null), "Fixture action payload does not match.");
        if (Event is not null)
        {
            Event.Validate();
            ContractRules.Require(Event.Provenance == EvidenceProvenance.Fixture, "Scripted events must be FIXTURE.");
        }
        if (RawEvent is not null)
            ContractRules.Text(RawEvent, 65_536);
        if (NextRequest is not null)
        {
            NextRequest.Validate();
            ContractRules.Require(NextRequest.Capabilities.Provenance == EvidenceProvenance.Fixture,
                "A fixture cannot select a live route.");
        }
        if (Suppression is { } reason)
            ContractRules.Defined(reason);
    }
}

public sealed record FixtureScenario : IContract
{
    public const string EvidenceLabel = "FIXTURE - NOT AI";
    public required ContractVersion Version { get; init; }
    public required string Name { get; init; }
    public required string Label { get; init; }
    public required EvidenceProvenance Provenance { get; init; }
    public required TextStreamRequest Request { get; init; }
    public required SequenceLimits Limits { get; init; }
    public required IReadOnlyList<FixtureStep> Steps { get; init; }

    public void Validate()
    {
        ContractRules.Require(Version is not null && Request is not null && Limits is not null &&
            Steps is { Count: >= 1 and <= 128 }, "A bounded fixture script is required.");
        Version!.Validate();
        ContractRules.Identifier(Name);
        ContractRules.Require(Label == EvidenceLabel && Provenance == EvidenceProvenance.Fixture,
            "Fixture evidence must be visibly labeled FIXTURE - NOT AI.");
        Request!.Validate();
        Limits!.Validate();
        ContractRules.Require(Request.Capabilities.Provenance == EvidenceProvenance.Fixture, "A fixture cannot select a live route.");
        var previous = 0;
        foreach (var step in Steps!)
        {
            ContractRules.Require(step is not null, "A fixture step is required.");
            step!.Validate();
            ContractRules.Require(step.AtMilliseconds >= previous, "Fixture time cannot move backwards.");
            previous = step.AtMilliseconds;
        }
    }
}
