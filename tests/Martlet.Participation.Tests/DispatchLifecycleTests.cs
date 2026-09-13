namespace Martlet.Participation.Tests;

public sealed class DispatchLifecycleTests
{
    [Fact]
    public void Polling_does_not_spend_but_only_one_commit_can_dispatch()
    {
        var clock = new ManualClock();
        var policy = PolicyFixtures.Policy(clock);
        clock.Advance(TimeSpan.FromSeconds(2));
        var intent = policy.CreateIntent(PolicyFixtures.Ambient());
        var first = policy.Evaluate(intent);
        var second = policy.Evaluate(intent);
        Assert.Equal(DecisionKind.Allow, first.Kind);
        Assert.Equal(DecisionKind.Allow, second.Kind);
        var accepted = policy.TryCommit(first);
        Assert.True(accepted.Accepted);
        Assert.Equal(PolicyReason.AlreadyDispatched, policy.TryCommit(second).Reason);
        Assert.True(policy.Release(accepted.Lease!));
        Assert.Equal(PolicyReason.AlreadyDispatched, policy.Evaluate(intent).Reason);
        Assert.Equal(PolicyReason.Cooldown, policy.Decision(PolicyFixtures.Ambient()).Reason);
    }

    [Fact]
    public async Task Parallel_evaluations_and_commits_cannot_claim_more_than_one_slot()
    {
        var clock = new ManualClock();
        var policy = PolicyFixtures.Policy(clock);
        clock.Advance(TimeSpan.FromSeconds(2));
        var intents = Enumerable.Range(0, 16).Select(_ => policy.CreateIntent(PolicyFixtures.Ambient())).ToArray();
        var proposals = intents.Select(policy.Evaluate).ToArray();
        Assert.All(proposals, proposal => Assert.Equal(DecisionKind.Allow, proposal.Kind));
        var commits = await Task.WhenAll(proposals.Select(proposal => Task.Run(() => policy.TryCommit(proposal))));
        var accepted = Assert.Single(commits, commit => commit.Accepted);
        Assert.True(policy.Release(accepted.Lease!));
        Assert.All(proposals, proposal => Assert.False(policy.TryCommit(proposal).Accepted));
    }

    [Fact]
    public async Task Parallel_replay_of_same_permit_is_one_use()
    {
        var policy = PolicyFixtures.Policy(new());
        var permit = policy.Decision(PolicyFixtures.Ptt());
        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => policy.TryCommit(permit))));
        Assert.Single(results, result => result.Accepted);
    }

    [Fact]
    public void Older_intent_cannot_replay_after_newer_dispatch_and_release()
    {
        var policy = PolicyFixtures.Policy(new());
        var old = policy.CreateIntent(PolicyFixtures.Ptt());
        var lease = policy.Accept(PolicyFixtures.Ptt());
        Assert.True(policy.Release(lease));
        Assert.Equal(PolicyReason.SupersededIntent, policy.Evaluate(old).Reason);
        var newer = policy.Accept(PolicyFixtures.Ptt());
        Assert.False(policy.Release(lease));
        Assert.Equal(PolicyReason.Busy, policy.Decision(PolicyFixtures.Ptt()).Reason);
        Assert.True(policy.Release(newer));
    }

    [Fact]
    public void A_decision_from_before_dispatch_cannot_become_permission_after_release()
    {
        var policy = PolicyFixtures.Policy(new());
        var firstIntent = policy.CreateIntent(PolicyFixtures.Ptt());
        var secondIntent = policy.CreateIntent(PolicyFixtures.Ptt());
        var first = policy.Evaluate(firstIntent);
        var second = policy.Evaluate(secondIntent);
        var lease = policy.TryCommit(first).Lease!;
        Assert.True(policy.Release(lease));
        Assert.Equal(PolicyReason.StaleDecision, policy.TryCommit(second).Reason);
        Assert.True(policy.TryCommit(policy.Evaluate(secondIntent)).Accepted);
    }

    [Fact]
    public void Busy_explicit_requires_new_deliberate_intent_and_noise_never_replaces()
    {
        var policy = PolicyFixtures.Policy(new());
        var active = policy.Accept(PolicyFixtures.Ptt());
        var busyIntent = policy.CreateIntent(PolicyFixtures.Ptt());
        var busy = policy.Evaluate(busyIntent);
        Assert.Equal(DecisionKind.Wait, busy.Kind);
        Assert.Equal(PolicyAction.RequestExplicitReplacement, busy.Action);
        var noise = policy.Decision(PolicyFixtures.Ambient("Watch out!"));
        Assert.Equal(DecisionKind.Suppress, noise.Kind);
        Assert.Equal(PolicyAction.None, noise.Action);
        Assert.True(policy.Release(active));
        Assert.Equal(PolicyReason.FreshIntentRequired, policy.Evaluate(busyIntent).Reason);
        Assert.False(policy.TryCommit(busy).Accepted);
        Assert.Equal(DecisionKind.Allow, policy.Decision(PolicyFixtures.Ptt()).Kind);
    }

    [Fact]
    public void New_activity_invalidates_waiting_intents_instead_of_replying_over_new_speech()
    {
        var clock = new ManualClock();
        var policy = PolicyFixtures.Policy(clock);
        var intent = policy.CreateIntent(PolicyFixtures.Ambient());
        Assert.Equal(PolicyReason.InsufficientGap, policy.Evaluate(intent).Reason);
        clock.Advance(TimeSpan.FromSeconds(1));
        policy.NoteSpeechActivity();
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(PolicyReason.StaleEpoch, policy.Evaluate(intent).Reason);
        Assert.Equal(DecisionKind.Allow, policy.Decision(PolicyFixtures.Ambient()).Kind);
    }

    [Fact]
    public void Mute_revokes_pending_proposal_and_unmute_does_not_resurrect_it()
    {
        var policy = PolicyFixtures.Policy(new());
        var intent = policy.CreateIntent(PolicyFixtures.Ptt());
        var permit = policy.Evaluate(intent);
        policy.SetState(PolicyFixtures.Consented with { Muted = true });
        Assert.Equal(PolicyReason.Muted, policy.TryCommit(permit).Reason);
        policy.SetState(PolicyFixtures.Consented);
        Assert.Equal(PolicyReason.StaleEpoch, policy.Evaluate(intent).Reason);
        Assert.Equal(DecisionKind.Allow, policy.Decision(PolicyFixtures.Ptt()).Kind);
    }

    [Fact]
    public void Active_mute_requests_stop_but_keeps_ownership_until_released()
    {
        var policy = PolicyFixtures.Policy(new());
        var lease = policy.Accept(PolicyFixtures.Ptt());
        Assert.Equal(PolicyAction.StopActiveTurn, policy.SetState(PolicyFixtures.Consented with { Muted = true }).Action);
        policy.SetState(PolicyFixtures.Consented);
        Assert.Equal(PolicyReason.Busy, policy.Decision(PolicyFixtures.Ptt()).Reason);
        Assert.True(policy.Release(lease));
    }

    [Fact]
    public void Destination_revision_and_role_revocation_invalidate_proposals_and_stop_active_work()
    {
        var policy = PolicyFixtures.Policy(new());
        var proposal = policy.Decision(PolicyFixtures.Ptt());
        policy.SetState(PolicyFixtures.Consented with { AuthorizationRevision = 2 });
        Assert.Equal(PolicyReason.StaleEpoch, policy.TryCommit(proposal).Reason);
        var lease = policy.Accept(PolicyFixtures.Ptt());
        var change = policy.SetState(PolicyFixtures.Consented with { AuthorizationRevision = 2, TranscriptionAuthorized = false });
        Assert.Equal(PolicyAction.StopActiveTurn, change.Action);
        Assert.True(policy.Release(lease));
    }

    [Fact]
    public void Reconfiguration_invalidates_old_names_permits_and_preserves_budget()
    {
        var clock = new ManualClock();
        var policy = PolicyFixtures.Policy(clock);
        clock.Advance(TimeSpan.FromSeconds(2));
        var old = policy.CreateIntent(PolicyFixtures.Ptt());
        var first = policy.Accept(PolicyFixtures.Ambient());
        policy.Release(first);
        clock.Advance(TimeSpan.FromSeconds(8));
        policy.Release(policy.Accept(PolicyFixtures.Ambient()));
        var before = policy.CreateIntent(PolicyFixtures.Ptt());
        policy.Reconfigure(new("Robin", mode: ParticipationMode.Conversational));
        Assert.Equal(PolicyReason.StaleEpoch, policy.Evaluate(before).Reason);
        Assert.Equal(PolicyReason.StaleEpoch, policy.Evaluate(old).Reason);
        clock.Advance(TimeSpan.FromSeconds(8));
        Assert.Equal(PolicyReason.NotAddressed, policy.Decision(PolicyFixtures.Ambient("Martlet, can you help?")).Reason);
        Assert.Equal(PolicyReason.NameAddressed, policy.Decision(PolicyFixtures.Ambient("Robin, can you help?")).Reason);
        Assert.Equal(PolicyReason.RateLimit, policy.Decision(PolicyFixtures.Ambient()).Reason);
    }

    [Fact]
    public void Failed_attempt_before_commit_spends_nothing_but_accepted_dispatch_is_not_refunded()
    {
        var clock = new ManualClock();
        var policy = PolicyFixtures.Policy(clock);
        var wait = policy.Decision(PolicyFixtures.Ambient());
        Assert.False(policy.TryCommit(wait).Accepted);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(PolicyReason.DecisionNotAllowed, policy.TryCommit(wait).Reason);
        var dispatch = policy.Accept(PolicyFixtures.Ambient());
        // The future runtime might deny/fail. Release ownership, but do not manufacture a budget refund.
        policy.Release(dispatch);
        Assert.Equal(PolicyReason.Cooldown, policy.Decision(PolicyFixtures.Ambient()).Reason);
    }
}
