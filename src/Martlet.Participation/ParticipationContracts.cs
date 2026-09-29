using System.Text.Json.Serialization;

namespace Martlet.Participation;

public enum ParticipationMode { PushToTalkOnly, NameAddressed, Conversational, Disabled }
public enum PolicyLanguage { English }
// HandsFreeListening: an utterance endpointed by voice activity inside a listening session the user explicitly started.
public enum InputSource { AmbientSpeech, PushToTalkControl, TypedControl, HandsFreeListening }
public enum AudioOrigin { Unknown, External, OwnPlayback, KnownLoopback }
public enum SpeechEvidence { Unknown, Speech, NoSpeech }
public enum ResponseActivity { Idle, Responding, Playing }
[JsonConverter(typeof(JsonStringEnumConverter<DecisionKind>))]
public enum DecisionKind { Suppress, Wait, Allow }
public enum ConfidenceKind { Unknown, Supplied }
[JsonConverter(typeof(JsonStringEnumConverter<PolicyAction>))]
public enum PolicyAction
{
    None, CommitBeforeDispatch, AwaitFinalTranscript, ReevaluateAfterGap,
    RequestExplicitReplacement, StopPlaybackThenRecapture, StopActiveTurn
}

// Policy-local metadata, not additions to Core's serialized SuppressionReason contract.
[JsonConverter(typeof(JsonStringEnumConverter<PolicyReason>))]
public enum PolicyReason
{
    Paused, Muted, ConsentMissing, SelfAudio, AudioOriginUnknown, NoSpeech,
    LowConfidence, ConfidenceUnknown, UncertainTranscript, FinalTranscriptRequired,
    ExplicitPushToTalk, ExplicitTypedAddress, NameAddressed, GroupInvitation,
    PolicyDisabled, NotAddressed, InsufficientGap, Cooldown, RateLimit, Busy,
    FreshIntentRequired, IntentExpired, StaleEpoch, StaleDecision, AlreadyDispatched,
    SupersededIntent, DecisionNotAllowed, DispatchAccepted, ExplicitHandsFree
}

public enum PolicyValidationCode
{
    InvalidConfiguration, InvalidInput, InvalidState, InvalidHandle,
    InvalidTimeSource, SequenceExhausted, AuthorizationRevisionReversed
}

public sealed class PolicyValidationException(PolicyValidationCode code)
    : ArgumentException($"Participation validation failed: {code}.")
{
    public PolicyValidationCode Code { get; } = code;
}

public sealed class Transcript
{
    [JsonIgnore] public string Text { get; }
    public bool IsFinal { get; }
    public SpeechEvidence Evidence { get; }
    public double? Confidence { get; }
    public bool Uncertain { get; }

    public Transcript(string text, bool isFinal = true, SpeechEvidence evidence = SpeechEvidence.Unknown,
        double? confidence = null, bool uncertain = false)
    {
        PolicyChecks.Require(text is not null && text.Length <= 4096 && TextRules.ValidUnicode(text) &&
            Enum.IsDefined(evidence) && (confidence is null || double.IsFinite(confidence.Value) &&
            confidence.Value is >= 0 and <= 1), PolicyValidationCode.InvalidInput);
        Text = text!;
        IsFinal = isFinal;
        Evidence = evidence;
        Confidence = confidence;
        Uncertain = uncertain;
    }

    public override string ToString() => nameof(Transcript);
}

public sealed class ParticipationInput
{
    public InputSource Source { get; }
    [JsonIgnore] public Transcript Transcript { get; }
    public AudioOrigin AudioOrigin { get; }
    public bool TrustedTypedAddress { get; }

    public ParticipationInput(InputSource source, Transcript transcript,
        AudioOrigin audioOrigin = AudioOrigin.Unknown, bool trustedTypedAddress = false)
    {
        PolicyChecks.Require(Enum.IsDefined(source) && Enum.IsDefined(audioOrigin) &&
            transcript is not null && (!trustedTypedAddress || source == InputSource.TypedControl) &&
            (source != InputSource.TypedControl || audioOrigin == AudioOrigin.Unknown &&
                transcript!.IsFinal), PolicyValidationCode.InvalidInput);
        Source = source;
        Transcript = transcript!;
        AudioOrigin = audioOrigin;
        TrustedTypedAddress = trustedTypedAddress;
    }

    public override string ToString() => nameof(ParticipationInput);
}

// Signals attest to caller-owned controls and the exact selected destinations; they grant no provider permission.
public sealed record ParticipationState
{
    public bool Paused { get; init; }
    public bool Muted { get; init; }
    public bool CaptureAuthorized { get; init; }
    public bool TranscriptionAuthorized { get; init; }
    public bool TextDestinationAuthorized { get; init; }
    public bool SpeechOutputRequested { get; init; }
    public bool SpeechDestinationAuthorized { get; init; }
    public long AuthorizationRevision { get; init; }
    public ResponseActivity Activity { get; init; }

    internal void Validate() => PolicyChecks.Require(Enum.IsDefined(Activity) && AuthorizationRevision >= 0 &&
        (!(CaptureAuthorized || TranscriptionAuthorized || TextDestinationAuthorized || SpeechDestinationAuthorized) ||
            AuthorizationRevision > 0), PolicyValidationCode.InvalidState);
}

public sealed class ParticipationIntent
{
    internal ParticipationPolicy Owner { get; }
    internal ParticipationInput Input { get; }
    internal TimeSpan CreatedAt { get; }
    internal bool CreatedWhileBusy { get; }
    public Guid SessionId => Owner.SessionId;
    public long Id { get; }
    public long Epoch { get; }

    internal ParticipationIntent(ParticipationPolicy owner, long id, long epoch, ParticipationInput input,
        TimeSpan createdAt, bool busy)
    {
        Owner = owner;
        Id = id;
        Epoch = epoch;
        Input = input;
        CreatedAt = createdAt;
        CreatedWhileBusy = busy;
    }

    public override string ToString() => $"{nameof(ParticipationIntent)}: {Id}, epoch {Epoch}";
}

public sealed class ParticipationDecision
{
    internal ParticipationPolicy Owner { get; }
    internal ParticipationIntent Intent { get; }
    internal long DispatchRevision { get; }
    public Guid SessionId => Owner.SessionId;
    public long IntentId => Intent.Id;
    public long Epoch { get; }
    public long AuthorizationRevision { get; }
    public DecisionKind Kind { get; }
    public PolicyReason Reason { get; }
    public PolicyAction Action { get; }
    public bool Unsolicited { get; }
    public ConfidenceKind ConfidenceKind => SuppliedConfidence is null ? ConfidenceKind.Unknown : ConfidenceKind.Supplied;
    public double? SuppliedConfidence { get; }
    public TimeSpan IntentAge { get; }
    public TimeSpan ConversationalGap { get; }
    public TimeSpan? RetryAfter { get; }

    internal ParticipationDecision(ParticipationPolicy owner, ParticipationIntent intent, long epoch,
        long dispatchRevision, long authorizationRevision, DecisionKind kind, PolicyReason reason,
        PolicyAction action, bool unsolicited, TimeSpan age, TimeSpan gap, TimeSpan? retryAfter)
    {
        Owner = owner;
        Intent = intent;
        Epoch = epoch;
        DispatchRevision = dispatchRevision;
        AuthorizationRevision = authorizationRevision;
        Kind = kind;
        Reason = reason;
        Action = action;
        Unsolicited = unsolicited;
        SuppliedConfidence = intent.Input.Transcript.Confidence;
        IntentAge = age;
        ConversationalGap = gap;
        RetryAfter = retryAfter;
    }

    public override string ToString() => $"{nameof(ParticipationDecision)}: {Kind}, {Reason}, {Action}";
}

public sealed class DispatchLease
{
    internal ParticipationPolicy Owner { get; }
    internal bool RequiresAudioConsent { get; }
    internal bool RequiresSpeechConsent { get; }
    public Guid SessionId => Owner.SessionId;
    public long IntentId { get; }
    public long Epoch { get; }
    public long AuthorizationRevision { get; }
    public bool Unsolicited { get; }

    internal DispatchLease(ParticipationPolicy owner, ParticipationDecision decision, bool speechRequested)
    {
        Owner = owner;
        IntentId = decision.IntentId;
        Epoch = decision.Epoch;
        AuthorizationRevision = decision.AuthorizationRevision;
        Unsolicited = decision.Unsolicited;
        RequiresAudioConsent = decision.Intent.Input.Source != InputSource.TypedControl;
        RequiresSpeechConsent = speechRequested;
    }

    public override string ToString() => $"{nameof(DispatchLease)}: {IntentId}, epoch {Epoch}";
}

public sealed record DispatchCommit(bool Accepted, PolicyReason Reason, DispatchLease? Lease);
public sealed record PolicyStateChange(long Epoch, PolicyAction Action);
public sealed record ParticipationSnapshot(Guid SessionId, long Epoch, long LatestIntentId,
    long AcceptedThroughIntentId, long? ActiveIntentId, int RecentUnsolicitedDispatches,
    int RetainedHistoryEntries, TimeSpan CooldownRemaining, TimeSpan ConversationalGap);

internal static class PolicyChecks
{
    internal static void Require(bool condition, PolicyValidationCode code)
    {
        if (!condition) throw new PolicyValidationException(code);
    }

    internal static long Next(long value)
    {
        Require(value < long.MaxValue, PolicyValidationCode.SequenceExhausted);
        return value + 1;
    }
}
