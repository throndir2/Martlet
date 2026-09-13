using System.Text;
using Martlet.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Streaming;
using Martlet.Sessions;

namespace Martlet.Diagnostics;

// Explicit session observation, never a runnable read-only probe or a live readiness update.
public static class FixtureDiagnostics
{
    public static DoctorReport Report(FixtureSessionSnapshot fixture, TimeProvider? timeProvider = null)
    {
        fixture.Validate();
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var finished = fixture.Stage == FixtureSessionStage.Finished;
        var error = fixture.PlaybackError ?? fixture.Sequence.Result?.Error;
        var code = fixture.Stopped ? "fixture.stopped"
            : fixture.Stage == FixtureSessionStage.Playback ? "fixture.playback"
            : !finished ? "fixture.running"
            : error is not null ? fixture.PlaybackError is not null ? "fixture.audio_failed"
                : fixture.Sequence.Issue is SequenceIssue.FirstEventDeadline or SequenceIssue.IdleDeadline or SequenceIssue.TotalDeadline
                    ? "fixture.deadline" : "fixture.failed"
            : fixture.ToneRequested && fixture.Sequence.Result?.Outcome == TurnOutcome.Completed &&
                fixture.Playback?.State != PlaybackState.Completed ? "fixture.audio_incomplete"
            : fixture.Sequence.Result?.Outcome switch
            {
                TurnOutcome.Completed => "fixture.completed",
                TurnOutcome.Refused => "fixture.refused",
                TurnOutcome.Canceled => "fixture.stopped",
                TurnOutcome.Suppressed => fixture.Sequence.Result.Suppression == SuppressionReason.NoSpeech
                    ? "fixture.no_speech" : "fixture.not_addressed",
                _ => "fixture.failed"
            };
        var finding = DiagnosticCatalog.Finding(code);
        var outcome = error is not null ? ProbeOutcome.Failed : finding.Outcome;
        return new()
        {
            Version = ContractVersion.Current, ApplicationVersion = FoundationStatusService.ApplicationVersion,
            CreatedAt = now, Fixture = fixture,
            Probes =
            [
                new()
                {
                    Id = "fixture.session", Stage = error?.Stage ?? Stage.Application, Required = true,
                    Outcome = outcome, Provenance = EvidenceProvenance.Fixture, Freshness = EvidenceFreshness.Current,
                    ObservedAt = now, Summary = finding.Summary, Error = error,
                    AgeMilliseconds = 0, MaximumAgeMilliseconds = 60_000,
                    DiagnosticCode = code, ActionId = finding.ActionId,
                    Remedy = DiagnosticCatalog.Remedy(finding.ActionId).Guidance,
                    Effects = fixture.ToneRequested ? [ProbeEffect.Permissioned, ProbeEffect.Device] : [ProbeEffect.LocalReadOnly]
                }
            ]
        };
    }

    public static string Describe(FixtureSessionSnapshot fixture)
    {
        var text = new StringBuilder();
        text.AppendLine($"{fixture.Label} | Scenario: {fixture.Scenario} | Stage: {fixture.Stage}");
        text.AppendLine("Scripted content only. No microphone, transcription, AI, provider, network or GPU was used.");
        text.AppendLine($"Session: {fixture.Sequence.Ids.SessionId}; turn: {fixture.Sequence.Ids.TurnId}; request: {fixture.Sequence.Ids.RequestId}");
        text.AppendLine($"Epoch: {fixture.Sequence.RequestEpoch}; current: {fixture.Sequence.CurrentEpoch}; terminal: {fixture.Sequence.ProviderTerminal}; outcome: {fixture.Sequence.Result?.Outcome}; silence: {fixture.Sequence.Result?.Suppression}");
        text.AppendLine($"Issue: {fixture.Sequence.Issue}; accepted events: {fixture.Sequence.AcceptedEvents}; queued text: {fixture.Sequence.QueuedChunks}; partial: {fixture.Partial}");
        text.AppendLine($"Synthetic text{(fixture.Partial ? " (partial; not a completed answer)" : "")}: {fixture.Text}");
        if (fixture.RefusalText is not null)
            text.AppendLine($"Separate scripted refusal (never read aloud): {fixture.RefusalText}");
        if (fixture.Playback is { } playback)
            text.AppendLine($"200 ms synthetic tone, not speech: {playback.State}; admitted {playback.AcceptedSamples}; submitted {playback.SubmittedSamples}; device-consumed {playback.DeviceConsumedSamples}; drain observed {playback.DeviceDrainObserved}; released {playback.DeviceReleased}; audible samples unknown.");
        else
            text.AppendLine(fixture.ToneRequested
                ? "Tone permitted for this action only; not started. Only an ordinary completed fixture is eligible."
                : "Audio OFF / not run. No output device was opened.");
        text.AppendLine("Upstream text completion is separate from playback completion. Fixture passes do not configure or qualify a real provider.");
        return text.ToString();
    }
}
