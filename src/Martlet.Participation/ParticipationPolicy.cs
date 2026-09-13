namespace Martlet.Participation;

public sealed class ParticipationPolicy
{
    private readonly object sync = new();
    private readonly TimeProvider clock;
    private readonly long origin, frequency;
    private long lastTimestamp, intentSequence, epoch = 1, dispatchRevision, acceptedHighWater;
    private TimeSpan lastSpeechActivity;
    private TimeSpan? lastAccepted;
    private readonly Queue<TimeSpan> unsolicitedDispatches = new(2);
    private ParticipationConfiguration configuration;
    private ParticipationState state;
    private DispatchLease? active;

    public Guid SessionId { get; }
    public long CurrentEpoch { get { lock (sync) return epoch; } }
    public ParticipationConfiguration Configuration { get { lock (sync) return configuration; } }
    public ParticipationSnapshot Snapshot
    {
        get
        {
            lock (sync)
            {
                var now = Now();
                var cooldown = lastAccepted is { } accepted ? configuration.AutomaticCooldown - (now - accepted) : TimeSpan.Zero;
                return new(SessionId, epoch, intentSequence, acceptedHighWater, active?.IntentId,
                    unsolicitedDispatches.Count(timestamp => now - timestamp < TimeSpan.FromMinutes(1)),
                    unsolicitedDispatches.Count, cooldown > TimeSpan.Zero ? cooldown : TimeSpan.Zero,
                    now - lastSpeechActivity);
            }
        }
    }

    public ParticipationPolicy(Guid sessionId, ParticipationConfiguration configuration,
        ParticipationState state, TimeProvider? clock = null)
    {
        PolicyChecks.Require(sessionId != Guid.Empty && configuration is not null,
            PolicyValidationCode.InvalidConfiguration);
        PolicyChecks.Require(state is not null, PolicyValidationCode.InvalidState);
        state!.Validate();
        SessionId = sessionId;
        this.configuration = configuration!;
        this.state = state;
        this.clock = clock ?? TimeProvider.System;
        frequency = this.clock.TimestampFrequency;
        PolicyChecks.Require(frequency > 0, PolicyValidationCode.InvalidTimeSource);
        origin = lastTimestamp = this.clock.GetTimestamp();
    }

    public ParticipationIntent CreateIntent(ParticipationInput input)
    {
        PolicyChecks.Require(input is not null, PolicyValidationCode.InvalidInput);
        lock (sync)
        {
            var now = Now();
            intentSequence = PolicyChecks.Next(intentSequence);
            return new(this, intentSequence, epoch, input!, now, IsBusy);
        }
    }

    public PolicyStateChange SetState(ParticipationState newState)
    {
        PolicyChecks.Require(newState is not null, PolicyValidationCode.InvalidState);
        newState!.Validate();
        lock (sync)
        {
            PolicyChecks.Require(newState.AuthorizationRevision >= state.AuthorizationRevision,
                PolicyValidationCode.AuthorizationRevisionReversed);
            if (newState != state)
            {
                epoch = PolicyChecks.Next(epoch);
                state = newState;
            }
            // This is an instruction to the owner, never a claim that runtime work was stopped.
            var stop = active is not null && (state.Paused || state.Muted ||
                !state.TextDestinationAuthorized ||
                active.RequiresAudioConsent && (!state.CaptureAuthorized || !state.TranscriptionAuthorized) ||
                active.RequiresSpeechConsent && (!state.SpeechOutputRequested || !state.SpeechDestinationAuthorized) ||
                state.AuthorizationRevision != active.AuthorizationRevision);
            return new(epoch, stop ? PolicyAction.StopActiveTurn : PolicyAction.None);
        }
    }

    public PolicyStateChange Reconfigure(ParticipationConfiguration newConfiguration)
    {
        PolicyChecks.Require(newConfiguration is not null, PolicyValidationCode.InvalidConfiguration);
        lock (sync)
        {
            epoch = PolicyChecks.Next(epoch);
            configuration = newConfiguration!;
            // Keep active ownership, cooldown and rate history across name/mode changes.
            return new(epoch, active is not null ? PolicyAction.StopActiveTurn : PolicyAction.None);
        }
    }

    // The caller supplies real activity events; the library neither runs VAD nor infers silence from missing text.
    public void NoteSpeechActivity()
    {
        lock (sync)
        {
            var now = Now();
            epoch = PolicyChecks.Next(epoch);
            lastSpeechActivity = now;
        }
    }

    public ParticipationDecision Evaluate(ParticipationIntent intent)
    {
        ValidateIntent(intent);
        lock (sync) return EvaluateCore(intent, Now());
    }

    public DispatchCommit TryCommit(ParticipationDecision decision)
    {
        PolicyChecks.Require(decision is not null && ReferenceEquals(decision.Owner, this),
            PolicyValidationCode.InvalidHandle);
        lock (sync)
        {
            var now = Now();
            var current = EvaluateCore(decision!.Intent, now);
            if (current.Kind != DecisionKind.Allow) return new(false, current.Reason, null);
            if (decision.Kind != DecisionKind.Allow) return new(false, PolicyReason.DecisionNotAllowed, null);
            if (decision.Epoch != epoch || decision.DispatchRevision != dispatchRevision)
                return new(false, PolicyReason.StaleDecision, null);

            var revision = PolicyChecks.Next(dispatchRevision);
            var lease = new DispatchLease(this, current, state.SpeechOutputRequested);
            // Accepted dispatch is the accounting boundary, even if subsequent runtime authorization fails.
            if (current.Unsolicited)
            {
                while (unsolicitedDispatches.TryPeek(out var oldest) && now - oldest >= TimeSpan.FromMinutes(1))
                    unsolicitedDispatches.Dequeue();
                unsolicitedDispatches.Enqueue(now);
            }
            lastAccepted = now;
            acceptedHighWater = decision.IntentId;
            dispatchRevision = revision;
            active = lease;
            return new(true, PolicyReason.DispatchAccepted, lease);
        }
    }

    // Release only after actual runtime ownership ends, not on a timeout or a stop request.
    public bool Release(DispatchLease lease)
    {
        PolicyChecks.Require(lease is not null && ReferenceEquals(lease.Owner, this),
            PolicyValidationCode.InvalidHandle);
        lock (sync)
        {
            if (!ReferenceEquals(active, lease)) return false;
            dispatchRevision = PolicyChecks.Next(dispatchRevision);
            active = null;
            return true;
        }
    }

    private bool IsBusy => active is not null || state.Activity != ResponseActivity.Idle;

    private void ValidateIntent(ParticipationIntent intent) =>
        PolicyChecks.Require(intent is not null && ReferenceEquals(intent.Owner, this),
            PolicyValidationCode.InvalidHandle);

    private ParticipationDecision EvaluateCore(ParticipationIntent intent, TimeSpan now)
    {
        var input = intent.Input;
        var transcript = input.Transcript;
        var age = now - intent.CreatedAt;
        var gap = now - lastSpeechActivity;
        var manual = input.Source == InputSource.PushToTalkControl ||
            input.Source == InputSource.TypedControl && input.TrustedTypedAddress;
        ParticipationDecision Result(PolicyReason reason, DecisionKind kind = DecisionKind.Suppress,
            PolicyAction action = PolicyAction.None, bool unsolicited = false, TimeSpan? retry = null) =>
            new(this, intent, epoch, dispatchRevision, state.AuthorizationRevision, kind, reason, action,
                unsolicited, age, gap, retry);

        if (state.Paused) return Result(PolicyReason.Paused);
        if (state.Muted) return Result(PolicyReason.Muted);
        if (!state.TextDestinationAuthorized ||
            state.SpeechOutputRequested && !state.SpeechDestinationAuthorized ||
            input.Source != InputSource.TypedControl && (!state.CaptureAuthorized || !state.TranscriptionAuthorized))
            return Result(PolicyReason.ConsentMissing);
        if (intent.Epoch != epoch) return Result(PolicyReason.StaleEpoch);
        if (age >= configuration.IntentLifetime) return Result(PolicyReason.IntentExpired);
        if (intent.Id == acceptedHighWater) return Result(PolicyReason.AlreadyDispatched);
        if (intent.Id < acceptedHighWater) return Result(PolicyReason.SupersededIntent);
        if (input.AudioOrigin is AudioOrigin.OwnPlayback or AudioOrigin.KnownLoopback)
            return Result(PolicyReason.SelfAudio);
        if (TextRules.NoSpeech(transcript)) return Result(PolicyReason.NoSpeech);
        if (transcript.Confidence < configuration.MinimumConfidence) return Result(PolicyReason.LowConfidence);
        if (transcript.Uncertain) return Result(PolicyReason.UncertainTranscript);
        if (!transcript.IsFinal)
            return Result(PolicyReason.FinalTranscriptRequired, DecisionKind.Wait, PolicyAction.AwaitFinalTranscript);
        if (configuration.Mode == ParticipationMode.Disabled) return Result(PolicyReason.PolicyDisabled);
        if (!manual && configuration.Mode == ParticipationMode.PushToTalkOnly)
            return Result(PolicyReason.PolicyDisabled);

        if (state.Activity == ResponseActivity.Playing)
        {
            if (manual && input.Source == InputSource.PushToTalkControl)
                return Result(PolicyReason.SelfAudio, DecisionKind.Wait, PolicyAction.StopPlaybackThenRecapture);
            return manual
                ? Result(PolicyReason.Busy, DecisionKind.Wait, PolicyAction.RequestExplicitReplacement)
                : Result(PolicyReason.SelfAudio);
        }
        if (IsBusy)
            return manual
                ? Result(PolicyReason.Busy, DecisionKind.Wait, PolicyAction.RequestExplicitReplacement)
                : Result(PolicyReason.Busy);
        if (intent.CreatedWhileBusy) return Result(PolicyReason.FreshIntentRequired);
        if (manual)
            return Result(input.Source == InputSource.PushToTalkControl ? PolicyReason.ExplicitPushToTalk :
                PolicyReason.ExplicitTypedAddress, DecisionKind.Allow, PolicyAction.CommitBeforeDispatch);
        if (input.Source != InputSource.TypedControl && input.AudioOrigin != AudioOrigin.External)
            return Result(PolicyReason.AudioOriginUnknown);
        if (transcript.Confidence is null) return Result(PolicyReason.ConfidenceUnknown);

        var named = TextRules.NameAddressed(transcript, configuration);
        var unsolicited = !named && configuration.Mode == ParticipationMode.Conversational &&
            TextRules.GroupInvitation(transcript);
        if (!named && !unsolicited) return Result(PolicyReason.NotAddressed);
        if (unsolicited && configuration.UnsolicitedTurnsPerMinute == 0) return Result(PolicyReason.PolicyDisabled);
        var requiredGap = unsolicited ? configuration.UnsolicitedGap : configuration.AddressedGap;
        if (gap < requiredGap)
            return Result(PolicyReason.InsufficientGap, DecisionKind.Wait, PolicyAction.ReevaluateAfterGap,
                unsolicited, requiredGap - gap);
        if (lastAccepted is { } accepted && now - accepted < configuration.AutomaticCooldown)
            return Result(PolicyReason.Cooldown);
        var recent = unsolicitedDispatches.Count(timestamp => now - timestamp < TimeSpan.FromMinutes(1));
        if (unsolicited && recent >= configuration.UnsolicitedTurnsPerMinute) return Result(PolicyReason.RateLimit);
        return Result(unsolicited ? PolicyReason.GroupInvitation : PolicyReason.NameAddressed,
            DecisionKind.Allow, PolicyAction.CommitBeforeDispatch, unsolicited);
    }

    private TimeSpan Now()
    {
        var timestamp = clock.GetTimestamp();
        PolicyChecks.Require(clock.TimestampFrequency == frequency && timestamp >= lastTimestamp,
            PolicyValidationCode.InvalidTimeSource);
        try
        {
            var ticks = (Int128)checked(timestamp - origin) * TimeSpan.TicksPerSecond / frequency;
            PolicyChecks.Require(ticks <= long.MaxValue, PolicyValidationCode.InvalidTimeSource);
            var elapsed = TimeSpan.FromTicks((long)ticks);
            lastTimestamp = timestamp;
            return elapsed;
        }
        catch (OverflowException)
        {
            throw new PolicyValidationException(PolicyValidationCode.InvalidTimeSource);
        }
    }
}
