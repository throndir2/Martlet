using Martlet.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Streaming;
using Martlet.Fixtures;

namespace Martlet.Sessions;

public enum FixtureSessionStage { Script, Playback, Finished }

public sealed record FixtureSessionSnapshot : IContract
{
    public required string Scenario { get; init; }
    public string Label => FixtureScenario.EvidenceLabel;
    public EvidenceProvenance Provenance => EvidenceProvenance.Fixture;
    public required SequenceSnapshot Sequence { get; init; }
    public required FixtureSessionStage Stage { get; init; }
    public string Text { get; init; } = "";
    public string? RefusalText { get; init; }
    public bool Stopped { get; init; }
    public bool ToneRequested { get; init; }
    public PlaybackSnapshot? Playback { get; init; }
    public MartletError? PlaybackError { get; init; }
    public FixtureTrace? Trace { get; init; }
    public bool Partial => Sequence.DeliveredChunks > 0 && (Stopped || Sequence.Result?.Outcome != TurnOutcome.Completed);

    public void Validate()
    {
        ContractRules.Require(FixtureSession.Scenarios.Contains(Scenario, StringComparer.Ordinal),
            "Unknown fixture session scenario.");
        ContractRules.Defined(Stage);
        ContractRules.Require(Sequence is not null, "A fixture sequence is required.");
        Sequence!.Ids.Validate();
        Sequence.Result?.Validate();
        ContractRules.Require(Sequence.Result is null || Sequence.Result.Provenance == EvidenceProvenance.Fixture &&
            Sequence.Result.Ids == Sequence.Ids,
            "A fixture session cannot contain a live result.");
        ContractRules.Text(Text, 4096);
        if (RefusalText is not null)
        {
            ContractRules.Text(RefusalText, 4096);
            ContractRules.Require(Sequence.Result?.Outcome == TurnOutcome.Refused, "Refusal text is separate from success.");
        }
        Trace?.Validate();
        ContractRules.Require(Stage != FixtureSessionStage.Finished || Trace is not null,
            "A finished fixture requires its production trace.");
        ContractRules.Require(Trace is null || Trace.Final == Sequence, "Fixture trace and current sequence must agree.");
        ContractRules.Require(Playback is null || ToneRequested && Playback.Ids == Sequence.Ids &&
            Playback.Epoch == Sequence.RequestEpoch && Sequence.Result?.Outcome == TurnOutcome.Completed,
            "Tone playback requires explicit permission and the completed current fixture.");
        PlaybackError?.Validate();
        ContractRules.Require(PlaybackError is null || ToneRequested && PlaybackError.Stage == Core.Contracts.Stage.Playback,
            "Playback errors require the explicit tone action.");
    }
}
