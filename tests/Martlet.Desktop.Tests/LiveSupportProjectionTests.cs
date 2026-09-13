using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Desktop;
using Martlet.Diagnostics;
using Martlet.Participation;
using Martlet.Support;

namespace Martlet.Desktop.Tests;

public sealed class LiveSupportProjectionTests
{
    public static IEnumerable<object[]> RuntimeStates() =>
        Enum.GetValues<ConversationState>().SelectMany(state => new[] { EvidenceProvenance.Live, EvidenceProvenance.Fixture }
            .Select(provenance => new object[] { state, provenance }));

    [Theory]
    [MemberData(nameof(RuntimeStates))]
    public void RuntimeEnumsPreserveProvenanceStateAndBoundedCorrelation(ConversationState state, EvidenceProvenance provenance)
    {
        var source = new ConversationSupportObservation(Guid.NewGuid(), Guid.NewGuid(),
            new("runtime." + state), provenance, 2, 1);
        var value = LiveSupportProjection.Project(source, DateTimeOffset.UtcNow);
        var bytes = SupportJson.WriteEvent(value);
        Assert.Equal(value, SupportJson.ReadEvent(bytes));
        Assert.Equal(provenance, value.Provenance);
        Assert.Equal(source.OperationId, value.TraceId);
        Assert.Equal(source.TurnId, value.TurnId);
        Assert.Equal(source.Segments, value.Count);
        Assert.Equal(source.QueueDepth, value.QueueDepth);
        Assert.Equal(state switch
        {
            ConversationState.Completed => "conversation.completed",
            ConversationState.Partial => "conversation.partial",
            ConversationState.Failed => "conversation.failed",
            ConversationState.Refused => "conversation.refused",
            ConversationState.Canceled => "conversation.canceled",
            _ => "conversation.running"
        }, value.Code);
        Assert.Contains(value.ActionId, DiagnosticCatalog.Remedies.Select(r => r.Id));
    }

    [Fact]
    public void EveryPolicyEnumUsesAuthoredMappingRatherThanInputText()
    {
        foreach (var reason in Enum.GetValues<PolicyReason>())
        {
            var status = new LiveConversationStatus("policy." + reason, Finished: reason != PolicyReason.DispatchAccepted, Policy: reason);
            var value = LiveSupportProjection.Project(new(Guid.NewGuid(), null, status, EvidenceProvenance.Live, 0, 0), DateTimeOffset.UtcNow);
            Assert.Equal(reason == PolicyReason.DispatchAccepted ? "conversation.running" : "conversation.suppressed", value.Code);
            Assert.Equal(Stage.TurnPolicy, value.Stage);
        }
    }

    [Theory]
    [InlineData("stt.uploading", "conversation.running")]
    [InlineData("stt.NoSpeech", "conversation.suppressed")]
    [InlineData("stt.Failed", "conversation.failed")]
    [InlineData("stt.DeadlineExceeded", "conversation.failed")]
    [InlineData("stt.deadline_exceeded", "conversation.failed")]
    [InlineData("stt.Canceled", "conversation.canceled")]
    [InlineData("mic.capturing", "conversation.running")]
    [InlineData("mic.transferred_and_cleared", "conversation.running")]
    [InlineData("mic.NoFrames", "conversation.failed")]
    [InlineData("mic.Failed", "conversation.failed")]
    [InlineData("mic.Canceled", "conversation.canceled")]
    [InlineData("mic.cleanup_quarantined", "conversation.quarantined")]
    [InlineData("conversation.cleanup_quarantined", "conversation.quarantined")]
    [InlineData("conversation.authorizing", "conversation.running")]
    [InlineData("conversation.invalid_input", "conversation.failed")]
    [InlineData("conversation.audio_not_authorized", "conversation.failed")]
    [InlineData("policy.invalid_input", "conversation.failed")]
    [InlineData("conversation.canceled", "conversation.canceled")]
    [InlineData("conversation.revoked", "conversation.canceled")]
    [InlineData("conversation.expired", "conversation.canceled")]
    [InlineData("conversation.configuration_changed", "conversation.canceled")]
    [InlineData("conversation.locked", "conversation.canceled")]
    [InlineData("conversation.paused", "conversation.canceled")]
    [InlineData("conversation.muted", "conversation.canceled")]
    [InlineData("conversation.focus_lost", "conversation.canceled")]
    [InlineData("conversation.deactivated", "conversation.canceled")]
    [InlineData("conversation.closed", "conversation.canceled")]
    [InlineData("conversation.output_changed", "conversation.canceled")]
    public void SupportedLocalStageTransitionsHaveClosedMetadata(string source, string expected)
    {
        var value = LiveSupportProjection.Project(new(Guid.NewGuid(), null, new(source), EvidenceProvenance.Live, 0, 0), DateTimeOffset.UtcNow);
        Assert.Equal(expected, value.Code);
        value.Validate();
    }

    [Fact]
    public void UnknownInputAndUnobservedCompletionAreOmittedNotLoggedAsSuccess()
    {
        var controller = new SupportController(null);
        var projector = new LiveSupportProjection();
        projector.Observe(controller, new(Guid.NewGuid(), null, new("PRIVATE-RAW-PROVIDER-ERROR"),
            EvidenceProvenance.Live, 0, 0));
        Assert.Equal(1, controller.Omitted);
        Assert.DoesNotContain("PRIVATE-RAW", controller.Status);
        Assert.False(controller.HasResources);
        Assert.Throws<SupportException>(() => LiveSupportProjection.Project(new(Guid.NewGuid(), null,
            new("runtime.Completed"), EvidenceProvenance.NotRun, 0, 0), DateTimeOffset.UtcNow));
        Assert.Throws<SupportException>(() => LiveSupportProjection.Project(new(Guid.NewGuid(), null,
            new("runtime.Generating"), EvidenceProvenance.Live, 0, 65_537), DateTimeOffset.UtcNow));
    }
}
