using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Diagnostics;
using Martlet.Participation;
using Martlet.Support;

namespace Martlet.Desktop;

internal sealed record ConversationSupportObservation(Guid OperationId, Guid? TurnId,
    LiveConversationStatus Status, EvidenceProvenance Provenance, int Segments, int QueueDepth);

internal sealed class LiveSupportProjection
{
    private Guid operation;
    private LiveConversationStatus? previous;
    private int transitions;

    internal void Observe(SupportController support, ConversationSupportObservation value)
    {
        if (operation != value.OperationId) { operation = value.OperationId; previous = null; transitions = 0; }
        if (previous == value.Status) return;
        previous = value.Status;
        try
        {
            var metadata = Project(value, DateTimeOffset.UtcNow);
            support.ObserveConversation(metadata);
            if (!support.Recording) return;
            if (++transitions > 32) { support.Drop(); return; }
            support.Record([metadata]);
        }
        catch (SupportException) { support.Omit(); }
    }

    internal static DiagnosticEvent Project(ConversationSupportObservation value, DateTimeOffset timestamp)
    {
        var status = value.Status;
        if (status.Policy is { } policy && !Enum.IsDefined(policy) ||
            status.ProviderFailure is { } provider && !Enum.IsDefined(provider) ||
            status.AudioFailure is { } audio && !Enum.IsDefined(audio))
            throw new SupportException(SupportFailure.InvalidData);
        // Exact authored input labels only. They are never copied into exported identifiers.
        var (code, stage, state) = status.Code switch
        {
            "conversation.authorizing" => ("conversation.running", Stage.Application, MetadataState.Running),
            "mic.capturing" or "mic.transferred_and_cleared" => ("conversation.running", Stage.Capture, MetadataState.Running),
            "stt.uploading" => ("conversation.running", Stage.Transcription, MetadataState.Running),
            "runtime.Idle" or "runtime.Authorizing" or "runtime.Generating" => ("conversation.running", Stage.Generation, MetadataState.Running),
            "runtime.Synthesizing" => ("conversation.running", Stage.Synthesis, MetadataState.Running),
            "runtime.Playing" => ("conversation.running", Stage.Playback, MetadataState.Running),
            "runtime.Completed" => ("conversation.completed", Stage.Generation, MetadataState.Completed),
            "runtime.Partial" => ("conversation.partial", Stage.Generation, MetadataState.Failed),
            "runtime.Failed" => ("conversation.failed", Stage.Generation, MetadataState.Failed),
            "runtime.Refused" => ("conversation.refused", Stage.Generation, MetadataState.Refused),
            "runtime.Canceled" => ("conversation.canceled", Stage.Generation, MetadataState.Canceled),
            "stt.NoSpeech" => ("conversation.suppressed", Stage.Transcription, MetadataState.Suppressed),
            "stt.Failed" or "stt.DeadlineExceeded" or "stt.deadline_exceeded" => ("conversation.failed", Stage.Transcription, MetadataState.Failed),
            "stt.Canceled" => ("conversation.canceled", Stage.Transcription, MetadataState.Canceled),
            "mic.NoFrames" or "mic.Failed" => ("conversation.failed", Stage.Application, MetadataState.Failed),
            "mic.Canceled" => ("conversation.canceled", Stage.Application, MetadataState.Canceled),
            "conversation.cleanup_quarantined" or "mic.cleanup_quarantined" => ("conversation.quarantined", Stage.Application, MetadataState.Failed),
            "conversation.invalid_input" or "conversation.audio_not_authorized" or "policy.invalid_input" =>
                ("conversation.failed", Stage.Application, MetadataState.Failed),
            "conversation.canceled" or "conversation.revoked" or "conversation.expired" or "conversation.configuration_changed" or
                "conversation.locked" or "conversation.paused" or "conversation.muted" or "conversation.focus_lost" or
                "conversation.deactivated" or "conversation.closed" or "conversation.output_changed" =>
                ("conversation.canceled", Stage.Application, MetadataState.Canceled),
            _ when status.Policy is { } reason && status.Code == "policy." + reason =>
                (status.Finished ? "conversation.suppressed" : "conversation.running", Stage.TurnPolicy,
                    status.Finished ? MetadataState.Suppressed : MetadataState.Running),
            _ => throw new SupportException(SupportFailure.InvalidData)
        };
        if (status.Quarantined) code = "conversation.quarantined";
        var finding = DiagnosticCatalog.Finding(code);
        var result = new DiagnosticEvent
        {
            SchemaVersion = 1, TimestampUtc = timestamp, Component = SupportComponent.Conversation,
            Stage = stage, Code = code, ActionId = finding.ActionId,
            Severity = finding.Outcome == ProbeOutcome.Failed ? DiagnosticSeverity.Error :
                finding.Outcome == ProbeOutcome.Passed ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning,
            Provenance = value.Provenance,
            Freshness = value.Provenance is EvidenceProvenance.Live or EvidenceProvenance.Fixture ? EvidenceFreshness.Current : EvidenceFreshness.Unknown,
            Adapter = value.Provenance == EvidenceProvenance.Fixture ? AdapterAlias.Fixture : AdapterAlias.None,
            TraceId = value.OperationId, TurnId = value.TurnId, Count = value.Segments, QueueDepth = value.QueueDepth, State = state
        };
        result.Validate();
        return result;
    }
}
