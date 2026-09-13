using Martlet.Audio;
using Martlet.Core.Contracts;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Conversation.Tests;

public sealed class ReviewRegressionTests
{
    [Theory]
    [InlineData(ProviderRole.Llm, true)]
    [InlineData(ProviderRole.Tts, true)]
    [InlineData(ProviderRole.Llm, false)]
    [InlineData(ProviderRole.Tts, false)]
    public async Task Original_caller_cancellation_precedes_dispatch_while_a_newer_callback_is_blocked(
        ProviderRole role, bool resumeAuthorization)
    {
        await using var h = new Harness();
        h.Clock.DeferCallbacks = resumeAuthorization;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();
        using var caller = new CancellationTokenSource();
        if (role == ProviderRole.Llm)
            h.Permissions.Text = async (action, _) =>
            {
                var permission = h.Permissions.Allow(action);
                entered.TrySetResult();
                await resume.Task;
                return permission;
            };
        else
            h.Permissions.Speech = async (action, _) =>
            {
                var permission = h.Permissions.Allow(action);
                entered.TrySetResult();
                await resume.Task;
                return permission;
            };
        var turn = h.Start(Harness.Request(speech: role == ProviderRole.Tts), caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        using var registration = caller.Token.Register(() =>
        {
            callbackEntered.TrySetResult();
            releaseCallback.Wait();
        });
        var cancellation = caller.CancelAsync();
        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (resumeAuthorization) resume.TrySetResult();
            var result = await Harness.Finish(turn, resumeAuthorization ? null : h.Clock);
            Assert.Equal(role == ProviderRole.Llm ? 0 : 1, h.Credentials.Calls);
            Assert.Equal(role == ProviderRole.Llm ? 0 : 1, h.Llm.Calls);
            Assert.Equal(0, h.Tts.Calls);
            Assert.Equal(0, h.Device.Opens);
            Assert.Equal(ConversationState.Canceled, result.State);
            Assert.True(h.Runtime.CurrentEpoch > turn.Epoch);
            Assert.False(cancellation.IsCompleted);
            if (!resumeAuthorization) Assert.False(turn.OwnershipRelease.IsCompleted);
        }
        finally
        {
            releaseCallback.Set();
            resume.TrySetResult();
            await cancellation;
        }
        await turn.OwnershipRelease.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Theory]
    [InlineData(ProviderRole.Llm)]
    [InlineData(ProviderRole.Tts)]
    public async Task Short_permission_can_use_only_its_unexpired_remainder_after_rollback(ProviderRole role)
    {
        await using var h = new Harness();
        h.Clock.DeferCallbacks = true;
        void Wait()
        {
            h.Clock.Advance(TimeSpan.FromSeconds(4));
            h.Clock.ShiftUtc(TimeSpan.FromSeconds(-10));
        }
        if (role == ProviderRole.Llm)
            h.Permissions.Text = (action, _) =>
            {
                var result = h.Permissions.Allow(action, h.Clock.GetUtcNow().AddSeconds(5));
                Wait();
                return ValueTask.FromResult<AuthorizedTextOperation?>(result);
            };
        else
            h.Permissions.Speech = (action, _) =>
            {
                var result = h.Permissions.Allow(action, h.Clock.GetUtcNow().AddSeconds(5));
                Wait();
                return ValueTask.FromResult<AuthorizedSpeechOperation?>(result);
            };
        var result = await Harness.Finish(h.Start(Harness.Request(speech: role == ProviderRole.Tts)));
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.Equal(1, h.Llm.Calls);
        Assert.Equal(role == ProviderRole.Tts ? 1 : 0, h.Tts.Calls);
    }

    [Fact]
    public async Task Text_only_does_not_require_unused_voice_prebuffer_compatibility()
    {
        await using var h = new Harness(playback: new() { MaximumQueuedFrames = 1 });
        Assert.Equal(ConversationState.Completed, (await Harness.Finish(h.Start(Harness.Request(false)))).State);
        Assert.Empty(h.Permissions.SpeechActions);
        Assert.Equal(0, h.Tts.Calls);
        Assert.Equal(0, h.Device.Opens);
    }

    [Theory]
    [InlineData(ProviderRole.Llm, false)]
    [InlineData(ProviderRole.Tts, false)]
    [InlineData(ProviderRole.Llm, true)]
    [InlineData(ProviderRole.Tts, true)]
    public async Task Short_permission_keeps_pre_authorizer_monotonic_origin_across_clock_rollback(
        ProviderRole role, bool expiresInCredentials)
    {
        await using var h = new Harness();
        h.Clock.DeferCallbacks = true;
        void AuthorizationWait()
        {
            h.Clock.Advance(TimeSpan.FromSeconds(expiresInCredentials ? 4 : 6));
            h.Clock.ShiftUtc(TimeSpan.FromSeconds(-10));
        }
        if (role == ProviderRole.Llm)
            h.Permissions.Text = (action, _) =>
            {
                var permission = h.Permissions.Allow(action, h.Clock.GetUtcNow().AddSeconds(5));
                AuthorizationWait();
                return ValueTask.FromResult<AuthorizedTextOperation?>(permission);
            };
        else
            h.Permissions.Speech = (action, _) =>
            {
                var permission = h.Permissions.Allow(action, h.Clock.GetUtcNow().AddSeconds(5));
                AuthorizationWait();
                return ValueTask.FromResult<AuthorizedSpeechOperation?>(permission);
            };
        h.Credentials.Resolve = (binding, _) =>
        {
            if (expiresInCredentials && binding.Role == role)
                h.Clock.Advance(TimeSpan.FromSeconds(2));
            return ValueTask.FromResult<BoundProviderCredential?>(new(binding, ProviderFixtures.Secret));
        };
        var result = await Harness.Finish(h.Start(Harness.Request(speech: role == ProviderRole.Tts)));
        Assert.Equal(role == ProviderRole.Llm ? 0 : 1, h.Llm.Calls);
        Assert.Equal(0, h.Tts.Calls);
        Assert.Equal((role == ProviderRole.Tts ? 1 : 0) + (expiresInCredentials ? 1 : 0), h.Credentials.Calls);
        Assert.Equal(role == ProviderRole.Llm ? ConversationState.Failed : ConversationState.Partial, result.State);
        Assert.Equal(ConversationFailure.AuthorizationExpired, result.Failure);
        Assert.Equal(0, h.Device.Opens);
    }

    [Theory]
    [InlineData(1, 150, 5000)]
    [InlineData(7, 150, 5000)]
    [InlineData(8, 150, 150)]
    [InlineData(8, 150, 159)]
    [InlineData(1, 20.001, 100)]
    [InlineData(256, 100.001, 119)]
    public async Task Incompatible_pcm_prebuffer_is_rejected_before_any_action(int frames, double prebufferMs, double capacityMs)
    {
        await using var h = new Harness(playback: new()
        {
            MaximumQueuedFrames = frames,
            Prebuffer = TimeSpan.FromMilliseconds(prebufferMs),
            Capacity = TimeSpan.FromMilliseconds(capacityMs)
        });
        Assert.Throws<ContractException>(() => h.Start());
        Assert.Empty(h.Permissions.TextActions);
        Assert.Empty(h.Permissions.SpeechActions);
        Assert.Equal(0, h.Credentials.Calls);
        Assert.Equal(0, h.Llm.Calls);
        Assert.Equal(0, h.Tts.Calls);
        Assert.Equal(0, h.Device.Opens);
        Assert.Equal(ConversationState.Idle, h.Runtime.State);
    }

    [Theory]
    [InlineData(1, 0, 100)]
    [InlineData(1, 20, 100)]
    [InlineData(2, 20.001, 100)]
    [InlineData(8, 150, 160)]
    [InlineData(256, 100.001, 120)]
    public async Task Compatible_rounded_prebuffer_streams_actual_multi_frame_pcm(int frames, double prebufferMs, double capacityMs)
    {
        await using var h = new Harness(playback: new()
        {
            MaximumQueuedFrames = frames,
            Prebuffer = TimeSpan.FromMilliseconds(prebufferMs),
            Capacity = TimeSpan.FromMilliseconds(capacityMs)
        });
        var audio = SpeechFixtures.Audio(4800);
        h.Tts.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(new FragmentedTextBody(audio)));
        var result = await Harness.Finish(h.Start(), h.Clock);
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.Equal(audio, h.Device.Bytes);
        Assert.Equal(1, h.Tts.Calls);
        Assert.True(result.Playback!.DeviceDrainObserved);
    }
}
