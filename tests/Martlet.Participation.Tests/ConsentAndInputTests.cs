namespace Martlet.Participation.Tests;

public sealed class ConsentAndInputTests
{
    [Fact]
    public void Default_is_control_only_not_spoken_push_to_talk()
    {
        var clock = new ManualClock();
        var policy = new ParticipationPolicy(Guid.NewGuid(), new(), PolicyFixtures.Consented, clock);
        Assert.Equal(ParticipationMode.PushToTalkOnly, policy.Configuration.Mode);
        Assert.Equal(PolicyReason.PolicyDisabled,
            policy.Decision(PolicyFixtures.Ambient("Push to talk. Martlet, can you help?")).Reason);
        Assert.Equal(PolicyReason.ExplicitPushToTalk, policy.Decision(PolicyFixtures.Ptt()).Reason);
        Assert.Equal(PolicyReason.ExplicitTypedAddress, policy.Decision(PolicyFixtures.Typed()).Reason);
        Assert.Equal(PolicyReason.PolicyDisabled,
            policy.Decision(new(InputSource.TypedControl, new("Please reply"))).Reason);
    }

    [Fact]
    public void Privacy_controls_dominate_all_words_and_confidence()
    {
        var clock = new ManualClock();
        var policy = PolicyFixtures.Policy(clock, state: PolicyFixtures.Consented with { Paused = true, Muted = true });
        var input = PolicyFixtures.Ptt("Martlet, can you help?", 0.1);
        Assert.Equal(PolicyReason.Paused, policy.Decision(input).Reason);
        policy.SetState(PolicyFixtures.Consented with { Muted = true });
        Assert.Equal(PolicyReason.Muted, policy.Decision(input).Reason);
        policy.SetState(PolicyFixtures.Consented with { TextDestinationAuthorized = false });
        Assert.Equal(PolicyReason.ConsentMissing, policy.Decision(input).Reason);
    }

    [Theory]
    [InlineData("capture", false, true, true, false, false)]
    [InlineData("transcription", true, false, true, false, false)]
    [InlineData("text destination", true, true, false, false, false)]
    [InlineData("speech destination", true, true, true, true, false)]
    public void Each_required_role_has_separate_consent(string label, bool capture, bool stt, bool text,
        bool speechRequested, bool speech)
    {
        Assert.NotEmpty(label);
        var policy = PolicyFixtures.Policy(new(), state: PolicyFixtures.Consented with
        {
            CaptureAuthorized = capture, TranscriptionAuthorized = stt, TextDestinationAuthorized = text,
            SpeechOutputRequested = speechRequested, SpeechDestinationAuthorized = speech
        });
        Assert.Equal(PolicyReason.ConsentMissing, policy.Decision(PolicyFixtures.Ptt()).Reason);
    }

    [Fact]
    public void Mode_change_does_not_create_capture_or_destination_consent()
    {
        var clock = new ManualClock();
        var policy = new ParticipationPolicy(Guid.NewGuid(), new(), new(), clock);
        policy.Reconfigure(new(mode: ParticipationMode.Conversational));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(PolicyReason.ConsentMissing, policy.Decision(PolicyFixtures.Ambient()).Reason);
    }

    [Fact]
    public void Deliberate_text_only_input_does_not_require_microphone_or_STT_permission()
    {
        var state = new ParticipationState { TextDestinationAuthorized = true, AuthorizationRevision = 1 };
        var policy = PolicyFixtures.Policy(new(), state: state);
        var lease = policy.Accept(PolicyFixtures.Typed());
        Assert.Equal(PolicyAction.None, policy.SetState(state).Action);
        Assert.True(policy.Release(lease));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    [InlineData("...?!")]
    [InlineData("[no speech]")]
    [InlineData("[BLANK_AUDIO]")]
    [InlineData("(silence)")]
    [InlineData("[music]")]
    [InlineData("[inaudible]")]
    public void No_speech_markers_never_generate_even_for_PTT(string text)
    {
        var policy = PolicyFixtures.Policy(new());
        Assert.Equal(PolicyReason.NoSpeech, policy.Decision(PolicyFixtures.Ptt(text)).Reason);
    }

    [Fact]
    public void Supplied_no_speech_evidence_overrides_hallucination_like_words()
    {
        var policy = PolicyFixtures.Policy(new());
        var input = new ParticipationInput(InputSource.PushToTalkControl,
            new("Thank you for watching", evidence: SpeechEvidence.NoSpeech, confidence: 0.99));
        Assert.Equal(PolicyReason.NoSpeech, policy.Decision(input).Reason);
    }

    [Fact]
    public void Unknown_is_unknown_and_only_manual_input_can_bypass_unknown_confidence()
    {
        var clock = new ManualClock();
        var policy = PolicyFixtures.Policy(clock);
        clock.Advance(TimeSpan.FromSeconds(2));
        var manual = policy.Decision(PolicyFixtures.Ptt());
        Assert.Equal(DecisionKind.Allow, manual.Kind);
        Assert.Null(manual.SuppliedConfidence);
        Assert.Equal(ConfidenceKind.Unknown, manual.ConfidenceKind);
        Assert.Equal(PolicyReason.ConfidenceUnknown,
            policy.Decision(PolicyFixtures.Ambient("Martlet, can you help?", null)).Reason);
        Assert.Equal(PolicyReason.LowConfidence, policy.Decision(PolicyFixtures.Ptt(confidence: 0.699)).Reason);
        Assert.Equal(DecisionKind.Allow, policy.Decision(PolicyFixtures.Ptt(confidence: 0.7)).Kind);
    }

    [Fact]
    public void Partial_and_explicitly_uncertain_transcripts_do_not_generate()
    {
        var policy = PolicyFixtures.Policy(new());
        Assert.Equal(PolicyReason.UncertainTranscript, policy.Decision(new(InputSource.PushToTalkControl,
            new("Can you help?", uncertain: true))).Reason);
        var partial = policy.Decision(new(InputSource.PushToTalkControl, new("Can you", isFinal: false)));
        Assert.Equal(DecisionKind.Wait, partial.Kind);
        Assert.Equal(PolicyAction.AwaitFinalTranscript, partial.Action);
        Assert.False(policy.TryCommit(partial).Accepted);
    }

    [Theory]
    [InlineData(AudioOrigin.OwnPlayback)]
    [InlineData(AudioOrigin.KnownLoopback)]
    public void Known_self_audio_is_not_rescued_by_a_manual_flag(AudioOrigin origin)
    {
        var policy = PolicyFixtures.Policy(new());
        var input = new ParticipationInput(InputSource.PushToTalkControl, new("Can you help?"), origin);
        Assert.Equal(PolicyReason.SelfAudio, policy.Decision(input).Reason);
    }

    [Fact]
    public void Own_playback_requires_stop_then_fresh_PTT_capture_not_echo_capture()
    {
        var clock = new ManualClock();
        var policy = PolicyFixtures.Policy(clock, state: PolicyFixtures.Consented with { Activity = ResponseActivity.Playing });
        var intent = policy.CreateIntent(PolicyFixtures.Ptt());
        var decision = policy.Evaluate(intent);
        Assert.Equal(DecisionKind.Wait, decision.Kind);
        Assert.Equal(PolicyAction.StopPlaybackThenRecapture, decision.Action);
        Assert.Equal(PolicyReason.SelfAudio, policy.Decision(PolicyFixtures.Ambient()).Reason);
        Assert.Equal(PolicyAction.RequestExplicitReplacement, policy.Decision(PolicyFixtures.Typed()).Action);
        Assert.False(policy.TryCommit(decision).Accepted);
        policy.SetState(PolicyFixtures.Consented);
        Assert.Equal(PolicyReason.StaleEpoch, policy.Evaluate(intent).Reason);
        Assert.Equal(DecisionKind.Allow, policy.Decision(PolicyFixtures.Ptt()).Kind);
    }

    [Fact]
    public void Unknown_audio_origin_is_not_automatic_external_speech()
    {
        var clock = new ManualClock();
        var policy = PolicyFixtures.Policy(clock);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(PolicyReason.AudioOriginUnknown,
            policy.Decision(PolicyFixtures.Ambient(origin: AudioOrigin.Unknown)).Reason);
    }

    [Fact]
    public void Named_mode_and_zero_rate_do_not_allow_unsolicited_turns()
    {
        var clock = new ManualClock();
        var policy = PolicyFixtures.Policy(clock, ParticipationMode.NameAddressed);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(PolicyReason.NotAddressed, policy.Decision(PolicyFixtures.Ambient()).Reason);
        policy.Reconfigure(new(mode: ParticipationMode.Conversational, unsolicitedTurnsPerMinute: 0));
        Assert.Equal(PolicyReason.PolicyDisabled, policy.Decision(PolicyFixtures.Ambient()).Reason);
        Assert.Equal(PolicyReason.NameAddressed, policy.Decision(PolicyFixtures.Ambient("Martlet, can you help?")).Reason);
        policy.Reconfigure(new(mode: ParticipationMode.Disabled));
        Assert.Equal(PolicyReason.PolicyDisabled, policy.Decision(PolicyFixtures.Ptt()).Reason);
    }

    [Fact]
    public void Revoking_voice_output_requests_stop_of_the_existing_voice_turn()
    {
        var state = PolicyFixtures.Consented with { SpeechOutputRequested = true, SpeechDestinationAuthorized = true };
        var policy = PolicyFixtures.Policy(new(), state: state);
        var lease = policy.Accept(PolicyFixtures.Ptt());
        Assert.Equal(PolicyAction.StopActiveTurn, policy.SetState(state with { SpeechOutputRequested = false }).Action);
        Assert.True(policy.Release(lease));
    }
}
