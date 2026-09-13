using Martlet.Core.Contracts;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Conversation.Tests;

public sealed class AuthorizationTests
{
    [Theory]
    [InlineData("llm", "missing")]
    [InlineData("llm", "budget")]
    [InlineData("llm", "expired-budget")]
    [InlineData("llm", "wrong-budget")]
    [InlineData("llm", "expired-consent")]
    [InlineData("llm", "disclosure")]
    [InlineData("llm", "charges")]
    [InlineData("llm", "wrong-role")]
    [InlineData("tts", "missing")]
    [InlineData("tts", "budget")]
    [InlineData("tts", "expired-budget")]
    [InlineData("tts", "wrong-budget")]
    [InlineData("tts", "expired-consent")]
    [InlineData("tts", "disclosure")]
    [InlineData("tts", "charges")]
    [InlineData("tts", "voice-disclosure")]
    [InlineData("tts", "wrong-role")]
    [InlineData("tts", "wrong-input")]
    [InlineData("tts", "wrong-voice")]
    public async Task Denial_stops_before_secret_lookup_and_transport(string role, string reason)
    {
        await using var h = new Harness();
        h.Answer("A final fixture answer.");
        if (role == "llm")
            h.Permissions.Text = (a, _) =>
            {
                if (reason == "missing") return ValueTask.FromResult<AuthorizedTextOperation?>(null);
                var allowed = h.Permissions.Allow(a);
                var old = allowed.Authorization;
                return ValueTask.FromResult<AuthorizedTextOperation?>(new(new(
                    reason == "wrong-role" ? TextFixtures.Binding with { Role = ProviderRole.Tts } : old.Binding,
                    old.Model, old.Ids, old.Epoch, old.Limits,
                    reason == "expired-consent" ? h.Clock.GetUtcNow() : old.ExpiresAt,
                    reason != "disclosure", reason != "charges"), Reservation(allowed.Reservation, reason, h.Clock)));
            };
        else
            h.Permissions.Speech = (a, _) =>
            {
                if (reason == "missing") return ValueTask.FromResult<AuthorizedSpeechOperation?>(null);
                var allowed = h.Permissions.Allow(a);
                var old = allowed.Authorization;
                return ValueTask.FromResult<AuthorizedSpeechOperation?>(new(new(
                    reason == "wrong-role" ? SpeechFixtures.Binding with { Role = ProviderRole.Llm } : old.Binding,
                    reason == "wrong-voice" ? old.Selection with { Voice = "coral" } : old.Selection,
                    reason == "wrong-input" ? new BoundedSpeechInput(a.Input.Text) : a.Input,
                    old.Ids, old.Epoch, old.Limits,
                    reason == "expired-consent" ? h.Clock.GetUtcNow() : old.ExpiresAt,
                    reason != "disclosure", reason != "charges", reason != "voice-disclosure"),
                    Reservation(allowed.Reservation, reason, h.Clock)));
            };
        var result = await Harness.Finish(h.Start());
        Assert.Equal(role == "llm" ? ConversationState.Failed : ConversationState.Partial, result.State);
        Assert.Equal(role == "llm" ? 0 : 1, h.Credentials.Calls);
        Assert.Equal(role == "llm" ? 0 : 1, h.Llm.Calls);
        Assert.Equal(0, h.Tts.Calls);
        Assert.Equal(0, h.Device.Opens);
    }

    private static BudgetReservation Reservation(BudgetReservation original, string reason, RuntimeClock clock) => reason switch
    {
        "budget" => null!,
        "expired-budget" => original with { ExpiresAt = clock.GetUtcNow() },
        "wrong-budget" => original with { Reserved = original.Reserved with { Requests = 2 } },
        _ => original
    };

    [Theory]
    [InlineData(ProviderRole.Llm, false)]
    [InlineData(ProviderRole.Llm, true)]
    [InlineData(ProviderRole.Tts, false)]
    [InlineData(ProviderRole.Tts, true)]
    public async Task Consent_and_reserved_budget_expire_inside_slow_credentials_without_send(ProviderRole role, bool rollback)
    {
        await using var h = new Harness();
        h.Clock.DeferCallbacks = true;
        h.Answer("A final fixture answer.");
        h.Permissions.Text = (a, _) => ValueTask.FromResult<AuthorizedTextOperation?>(
            h.Permissions.Allow(a, h.Clock.GetUtcNow().AddSeconds(5)));
        h.Permissions.Speech = (a, _) => ValueTask.FromResult<AuthorizedSpeechOperation?>(
            h.Permissions.Allow(a, h.Clock.GetUtcNow().AddSeconds(5)));
        BoundProviderCredential? lateCredential = null;
        h.Credentials.Resolve = (binding, token) =>
        {
            if (binding.Role == role)
            {
                h.Clock.Advance(TimeSpan.FromSeconds(6));
                if (rollback) h.Clock.ShiftUtc(TimeSpan.FromHours(-1));
                Assert.False(token.IsCancellationRequested);
                return ValueTask.FromResult<BoundProviderCredential?>(lateCredential = new(binding, ProviderFixtures.Secret));
            }
            return ValueTask.FromResult<BoundProviderCredential?>(new(binding, ProviderFixtures.Secret));
        };
        var result = await Harness.Finish(h.Start());
        Assert.Equal(ConversationFailure.AuthorizationExpired, result.Failure);
        Assert.Equal(role == ProviderRole.Llm ? 0 : 1, h.Llm.Calls);
        Assert.Equal(0, h.Tts.Calls);
        Assert.Equal(0, h.Device.Opens);
        Assert.NotNull(lateCredential);
        Assert.Throws<CredentialUnavailableException>(() => lateCredential.CreateAuthorization());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Authorization_and_credentials_share_original_monotonic_stage_and_turn_budgets(bool wholeTurn, bool rollback)
    {
        await using var h = new Harness(textOnly: true);
        h.Clock.DeferCallbacks = true;
        h.Permissions.Text = (a, _) =>
        {
            var allowed = h.Permissions.Allow(a);
            h.Clock.Advance(TimeSpan.FromMilliseconds(800));
            if (rollback) h.Clock.ShiftUtc(TimeSpan.FromHours(-1));
            return ValueTask.FromResult<AuthorizedTextOperation?>(allowed);
        };
        h.Credentials.Resolve = (binding, _) =>
        {
            h.Clock.Advance(TimeSpan.FromMilliseconds(300));
            return ValueTask.FromResult<BoundProviderCredential?>(new(binding, ProviderFixtures.Secret));
        };
        var turn = h.Start(Harness.Request(false,
            new() { TurnTimeout = TimeSpan.FromSeconds(wholeTurn ? 1 : 90) },
            new() { MaxRequestTime = TimeSpan.FromSeconds(wholeTurn ? 60 : 1) }));
        var result = await Harness.Finish(turn);
        Assert.Equal(ConversationState.Failed, result.State);
        Assert.Equal(ConversationFailure.DeadlineExceeded, result.Failure);
        Assert.Equal(0, h.Llm.Calls);
    }

    [Fact]
    public async Task A_late_authorization_is_rejected_before_credentials_without_timer_delivery()
    {
        await using var h = new Harness(textOnly: true);
        h.Clock.DeferCallbacks = true;
        h.Permissions.Text = (a, _) =>
        {
            var allowed = h.Permissions.Allow(a);
            h.Clock.Advance(TimeSpan.FromSeconds(61));
            return ValueTask.FromResult<AuthorizedTextOperation?>(allowed);
        };
        var result = await Harness.Finish(h.Start(Harness.Request(false)));
        Assert.Equal(ConversationFailure.DeadlineExceeded, result.Failure);
        Assert.Equal(0, h.Credentials.Calls);
        Assert.Equal(0, h.Llm.Calls);
    }

    [Fact]
    public async Task Retry_uses_fresh_ids_and_rejects_reused_permission_without_second_send()
    {
        await using var h = new Harness(textOnly: true);
        AuthorizedTextOperation? firstPermission = null;
        h.Permissions.Text = (a, _) => ValueTask.FromResult<AuthorizedTextOperation?>(
            firstPermission ??= h.Permissions.Allow(a));
        var first = h.Start(Harness.Request(false));
        Assert.Equal(ConversationState.Completed, (await Harness.Finish(first)).State);
        // Give the new action its budget reservation but deliberately reuse consumed provider consent.
        h.Permissions.Text = (a, _) => ValueTask.FromResult<AuthorizedTextOperation?>(new(
            firstPermission!.Authorization, h.Permissions.Allow(a).Reservation));
        var retry = h.Runtime.Retry(first, Harness.Request(false), h.Permissions, false);
        var result = await Harness.Finish(retry);
        Assert.NotEqual(first.TurnId, retry.TurnId);
        Assert.NotEqual(first.TextIds.RequestId, retry.TextIds.RequestId);
        Assert.True(retry.Epoch > first.Epoch);
        Assert.Equal(first.TurnId, result.RetryOf);
        Assert.Equal(ConversationState.Failed, result.State);
        Assert.Equal(ProviderFailureCode.ConsentMismatch, result.ProviderFailure);
        Assert.Equal(1, h.Llm.Calls);
        Assert.Equal(1, h.Credentials.Calls);
    }

    [Fact]
    public async Task Explicit_retry_after_playback_requires_acknowledgment_and_fresh_actions()
    {
        await using var h = new Harness();
        var first = h.Start();
        Assert.True((await Harness.Finish(first)).MayHavePlayed);
        Assert.Throws<ContractException>(() => h.Runtime.Retry(first, Harness.Request(), h.Permissions, false));
        var next = h.Runtime.Retry(first, Harness.Request(), h.Permissions, true);
        var result = await Harness.Finish(next);
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.True(result.EarlierTurnMayHavePlayed);
        Assert.Equal(2, h.Llm.Calls);
        Assert.Equal(2, h.Tts.Calls);
        Assert.Equal(2, h.Permissions.SpeechActions.Select(x => x.Context.Ids.RequestId).Distinct().Count());
        Assert.True(result.Playback!.Epoch > first.Snapshot.Playback!.Epoch);
    }
}
