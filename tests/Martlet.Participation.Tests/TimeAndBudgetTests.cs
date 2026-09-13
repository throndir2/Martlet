namespace Martlet.Participation.Tests;

public sealed class TimeAndBudgetTests
{
    [Theory]
    [InlineData(ParticipationMode.NameAddressed, "Martlet, can you help?", 600)]
    [InlineData(ParticipationMode.Conversational, "Does anyone know the next move?", 1200)]
    public void Gap_opens_at_exact_monotonic_boundary(ParticipationMode mode, string text, int milliseconds)
    {
        var clock = new ManualClock();
        var policy = PolicyFixtures.Policy(clock, mode);
        var intent = policy.CreateIntent(PolicyFixtures.Ambient(text));
        clock.Advance(TimeSpan.FromMilliseconds(milliseconds) - TimeSpan.FromTicks(1));
        var waiting = policy.Evaluate(intent);
        Assert.Equal(PolicyReason.InsufficientGap, waiting.Reason);
        Assert.Equal(TimeSpan.FromTicks(1), waiting.RetryAfter);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(DecisionKind.Allow, policy.Evaluate(intent).Kind);
    }

    [Fact]
    public void Cooldown_opens_at_eight_seconds_since_any_accepted_dispatch()
    {
        var clock = new ManualClock();
        var policy = PolicyFixtures.Policy(clock);
        policy.Release(policy.Accept(PolicyFixtures.Ptt()));
        clock.Advance(TimeSpan.FromSeconds(8) - TimeSpan.FromTicks(1));
        Assert.Equal(PolicyReason.Cooldown, policy.Decision(PolicyFixtures.Ambient()).Reason);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(DecisionKind.Allow, policy.Decision(PolicyFixtures.Ambient()).Kind);
        Assert.Equal(DecisionKind.Allow, policy.Decision(PolicyFixtures.Ptt()).Kind);
    }

    [Fact]
    public void Sliding_minute_is_two_unsolicited_dispatches_and_exact_cutoff_expires_oldest()
    {
        var clock = new ManualClock();
        var policy = PolicyFixtures.Policy(clock);
        clock.Advance(TimeSpan.FromSeconds(2));
        policy.Release(policy.Accept(PolicyFixtures.Ambient()));
        clock.Advance(TimeSpan.FromSeconds(8));
        policy.Release(policy.Accept(PolicyFixtures.Ambient()));
        clock.Advance(TimeSpan.FromSeconds(8));
        Assert.Equal(PolicyReason.RateLimit, policy.Decision(PolicyFixtures.Ambient()).Reason);
        clock.Advance(TimeSpan.FromSeconds(44) - TimeSpan.FromTicks(1));
        Assert.Equal(PolicyReason.RateLimit, policy.Decision(PolicyFixtures.Ambient()).Reason);
        clock.Advance(TimeSpan.FromTicks(1));
        policy.Release(policy.Accept(PolicyFixtures.Ambient()));
        clock.Advance(TimeSpan.FromSeconds(8));
        policy.Release(policy.Accept(PolicyFixtures.Ambient()));
        clock.Advance(TimeSpan.FromSeconds(8));
        Assert.Equal(PolicyReason.RateLimit, policy.Decision(PolicyFixtures.Ambient()).Reason);
    }

    [Fact]
    public void Explicit_and_name_addressed_turns_do_not_spend_unsolicited_rate()
    {
        var clock = new ManualClock();
        var policy = PolicyFixtures.Policy(clock);
        policy.Release(policy.Accept(PolicyFixtures.Typed()));
        clock.Advance(TimeSpan.FromSeconds(8));
        policy.Release(policy.Accept(PolicyFixtures.Ambient("Martlet, can you help?")));
        clock.Advance(TimeSpan.FromSeconds(8));
        policy.Release(policy.Accept(PolicyFixtures.Ambient()));
        clock.Advance(TimeSpan.FromSeconds(8));
        Assert.Equal(DecisionKind.Allow, policy.Decision(PolicyFixtures.Ambient()).Kind);
    }

    [Fact]
    public void Intent_expiry_is_original_creation_not_renewed_by_evaluation()
    {
        var clock = new ManualClock();
        var policy = PolicyFixtures.Policy(clock);
        var intent = policy.CreateIntent(PolicyFixtures.Ptt());
        clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        var allowed = policy.Evaluate(intent);
        Assert.Equal(DecisionKind.Allow, allowed.Kind);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(PolicyReason.IntentExpired, policy.Evaluate(intent).Reason);
        Assert.Equal(PolicyReason.IntentExpired, policy.TryCommit(allowed).Reason);
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(PolicyReason.IntentExpired, policy.Evaluate(intent).Reason);
        Assert.Equal(DecisionKind.Allow, policy.Decision(PolicyFixtures.Ptt()).Kind);
    }

    [Fact]
    public void UTC_rollback_and_forward_do_not_change_expiry_cooldown_or_rate()
    {
        var clock = new ManualClock();
        var policy = PolicyFixtures.Policy(clock);
        clock.Advance(TimeSpan.FromSeconds(2));
        var intent = policy.CreateIntent(PolicyFixtures.Ambient());
        clock.Utc = DateTimeOffset.UnixEpoch.AddYears(-20);
        var lease = policy.TryCommit(policy.Evaluate(intent)).Lease!;
        policy.Release(lease);
        clock.Utc = DateTimeOffset.UnixEpoch.AddYears(50);
        Assert.Equal(PolicyReason.Cooldown, policy.Decision(PolicyFixtures.Ambient()).Reason);
        var expiring = policy.CreateIntent(PolicyFixtures.Ptt());
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(PolicyReason.IntentExpired, policy.Evaluate(expiring).Reason);
        clock.Advance(TimeSpan.FromSeconds(3));
        policy.Release(policy.Accept(PolicyFixtures.Ambient()));
        clock.Advance(TimeSpan.FromSeconds(8));
        Assert.Equal(PolicyReason.RateLimit, policy.Decision(PolicyFixtures.Ambient()).Reason);
        Assert.Equal(0, clock.UtcReads);
    }

    [Fact]
    public void Broken_monotonic_source_fails_explicitly_without_resetting_history()
    {
        var clock = new ManualClock();
        var policy = PolicyFixtures.Policy(clock);
        clock.Advance(TimeSpan.FromSeconds(2));
        var intent = policy.CreateIntent(PolicyFixtures.Ptt());
        clock.Advance(TimeSpan.FromSeconds(-1));
        Assert.Equal(PolicyValidationCode.InvalidTimeSource,
            Assert.Throws<PolicyValidationException>(() => policy.Evaluate(intent)).Code);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.Zero, policy.Evaluate(intent).IntentAge);
    }

    [Fact]
    public void Budget_history_stays_bounded_and_expiry_does_not_restore_old_ids()
    {
        var clock = new ManualClock();
        var policy = PolicyFixtures.Policy(clock);
        var old = policy.CreateIntent(PolicyFixtures.Ptt());
        for (var minute = 0; minute < 4; minute++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            policy.Release(policy.Accept(PolicyFixtures.Ambient()));
            clock.Advance(TimeSpan.FromSeconds(8));
            policy.Release(policy.Accept(PolicyFixtures.Ambient()));
            Assert.Equal(2, policy.Snapshot.RetainedHistoryEntries);
            Assert.Equal(2, policy.Snapshot.RecentUnsolicitedDispatches);
        }
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, policy.Snapshot.RecentUnsolicitedDispatches);
        Assert.Equal(2, policy.Snapshot.RetainedHistoryEntries);
        Assert.Equal(PolicyReason.IntentExpired, policy.Evaluate(old).Reason);
    }

    [Fact]
    public void Configured_rate_and_cooldown_are_applied_not_only_default_values()
    {
        var clock = new ManualClock();
        var policy = new ParticipationPolicy(Guid.NewGuid(),
            new(mode: ParticipationMode.Conversational, unsolicitedTurnsPerMinute: 1,
                automaticCooldown: TimeSpan.FromSeconds(10)), PolicyFixtures.Consented, clock);
        clock.Advance(TimeSpan.FromSeconds(2));
        policy.Release(policy.Accept(PolicyFixtures.Ambient()));
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(PolicyReason.Cooldown, policy.Decision(PolicyFixtures.Ambient()).Reason);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(PolicyReason.RateLimit, policy.Decision(PolicyFixtures.Ambient()).Reason);
    }

    [Fact]
    public void Timestamp_frequency_conversion_uses_exact_integer_ticks()
    {
        var clock = new VariableClock { Frequency = 1_000_000_000 };
        var policy = new ParticipationPolicy(Guid.NewGuid(), new(), PolicyFixtures.Consented, clock);
        var intent = policy.CreateIntent(PolicyFixtures.Ptt());
        clock.Timestamp = 4_999_999_999;
        Assert.Equal(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1), policy.Evaluate(intent).IntentAge);
        Assert.Equal(DecisionKind.Allow, policy.Evaluate(intent).Kind);
        clock.Timestamp++;
        Assert.Equal(PolicyReason.IntentExpired, policy.Evaluate(intent).Reason);
    }

    [Fact]
    public void Invalid_frequency_changed_frequency_and_elapsed_overflow_fail_closed()
    {
        var clock = new VariableClock { Frequency = 0 };
        Assert.Throws<PolicyValidationException>(() =>
            new ParticipationPolicy(Guid.NewGuid(), new(), PolicyFixtures.Consented, clock));
        clock.Frequency = 1;
        var policy = new ParticipationPolicy(Guid.NewGuid(), new(), PolicyFixtures.Consented, clock);
        var intent = policy.CreateIntent(PolicyFixtures.Ptt());
        clock.Frequency = 2;
        Assert.Equal(PolicyValidationCode.InvalidTimeSource,
            Assert.Throws<PolicyValidationException>(() => policy.Evaluate(intent)).Code);
        clock.Frequency = 1;
        clock.Timestamp = long.MaxValue;
        Assert.Equal(PolicyValidationCode.InvalidTimeSource,
            Assert.Throws<PolicyValidationException>(() => policy.Evaluate(intent)).Code);
    }

    private sealed class VariableClock : TimeProvider
    {
        internal long Frequency { get; set; }
        internal long Timestamp { get; set; }
        public override long TimestampFrequency => Frequency;
        public override long GetTimestamp() => Timestamp;
    }
}
