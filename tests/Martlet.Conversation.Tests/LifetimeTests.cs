using System.Text;
using Martlet.Audio;
using Martlet.Audio.Tests;
using Martlet.Core.Contracts;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Conversation.Tests;

public sealed class LifetimeTests
{
    [Theory]
    [InlineData(ProviderRole.Llm)]
    [InlineData(ProviderRole.Tts)]
    public async Task Noncooperative_credentials_keep_ownership_and_late_credentials_are_disposed_without_send(ProviderRole role)
    {
        await using var h = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BoundProviderCredential? held = null;
        h.Credentials.Resolve = async (binding, _) =>
        {
            if (binding.Role != role) return new(binding, ProviderFixtures.Secret);
            held = new(binding, ProviderFixtures.Secret);
            entered.TrySetResult();
            await release.Task;
            return held;
        };
        var turn = h.Start();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var stopped = turn.StopAsync();
            Assert.Equal(ConversationState.Canceled, turn.Snapshot.State);
            await Harness.Until(() => stopped.IsCompleted, h.Clock);
            Assert.False((await stopped).OwnershipReleased);
            Assert.NotNull(held);
            Assert.NotNull(held.CreateAuthorization());
            Assert.Throws<InvalidOperationException>(() => h.Start());
        }
        finally { release.TrySetResult(); }
        await turn.OwnershipRelease.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Throws<CredentialUnavailableException>(() => held!.CreateAuthorization());
        Assert.Equal(role == ProviderRole.Llm ? 0 : 1, h.Llm.Calls);
        Assert.Equal(0, h.Tts.Calls);
        Assert.Equal(0, h.Device.Opens);
    }

    [Fact]
    public async Task Synchronous_authorization_callback_cannot_block_start_or_stop()
    {
        await using var h = new Harness(textOnly: true);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Permissions.Text = (a, _) =>
        {
            entered.TrySetResult();
            release.Wait();
            return ValueTask.FromResult<AuthorizedTextOperation?>(h.Permissions.Allow(a));
        };
        var starting = Task.Run(() => h.Start(Harness.Request(false)));
        ConversationTurn? turn = null;
        try
        {
            turn = await starting.WaitAsync(TimeSpan.FromSeconds(20));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var stopping = turn.StopAsync();
            await Harness.Until(() => stopping.IsCompleted, h.Clock);
            Assert.False((await stopping).OwnershipReleased);
        }
        finally { release.Set(); }
        await turn.OwnershipRelease.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, h.Llm.Calls);
    }

    [Theory]
    [InlineData("text-authorization")]
    [InlineData("text-credentials")]
    [InlineData("text-http")]
    [InlineData("text-body")]
    [InlineData("speech-authorization")]
    [InlineData("speech-credentials")]
    [InlineData("speech-http")]
    [InlineData("speech-body")]
    [InlineData("playback")]
    [InlineData("between-segments")]
    public async Task Caller_cancellation_invalidates_every_stage_and_never_retries(string stage)
    {
        await using var h = new Harness(new() { BlockWrite = stage == "playback" });
        h.Answer("One. Two.");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Block(CancellationToken token)
        {
            entered.TrySetResult();
            await unblock.Task.WaitAsync(token);
        }
        if (stage == "text-authorization")
            h.Permissions.Text = async (a, token) => { await Block(token); return h.Permissions.Allow(a); };
        if (stage is "speech-authorization" or "between-segments")
            h.Permissions.Speech = async (a, token) =>
            {
                if (stage != "between-segments" || a.Segment == 2) await Block(token);
                return h.Permissions.Allow(a);
            };
        if (stage is "text-credentials" or "speech-credentials")
            h.Credentials.Resolve = async (binding, token) =>
            {
                if (binding.Role == (stage == "text-credentials" ? ProviderRole.Llm : ProviderRole.Tts)) await Block(token);
                return new(binding, ProviderFixtures.Secret);
            };
        if (stage == "text-http")
            h.Llm.Respond = async (_, token) => { await Block(token); return TextRecordingHandler.Sse(Harness.Trace("Answer.")); };
        if (stage == "speech-http")
            h.Tts.Respond = async (_, token) => { await Block(token); return SpeechFixtures.Pcm(new MemoryStream(SpeechFixtures.Audio())); };
        if (stage == "text-body")
            h.Llm.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(new FragmentedTextBody(
                Encoding.UTF8.GetBytes(Harness.Trace("Answer."))) { BeforeRead = Block }));
        if (stage == "speech-body")
            h.Tts.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(
                new FragmentedTextBody(SpeechFixtures.Audio()) { BeforeRead = Block }));
        using var caller = new CancellationTokenSource();
        var turn = h.Start(token: caller.Token);
        await (stage == "playback" ? h.Device.EnteredBlockedWrite.Task : entered.Task).WaitAsync(TimeSpan.FromSeconds(20));
        await caller.CancelAsync();
        Assert.True(h.Runtime.CurrentEpoch > turn.Epoch);
        Assert.Equal(0, turn.Snapshot.QueuedSegments);
        unblock.TrySetResult();
        var result = await Harness.Finish(turn, h.Clock);
        await turn.OwnershipRelease.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(ConversationState.Canceled, result.State);
        Assert.InRange(h.Llm.Calls, 0, 1);
        Assert.InRange(h.Tts.Calls, 0, 1);
        Assert.InRange(h.Device.Opens, 0, 1);
        if (stage == "between-segments") Assert.True(result.MayHavePlayed);
    }

    [Fact]
    public async Task Stop_of_noncooperative_authorizer_reports_outstanding_work_and_old_stop_cannot_cancel_next_turn()
    {
        await using var h = new Harness(textOnly: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Permissions.Text = async (a, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return h.Permissions.Allow(a);
        };
        var first = h.Start(Harness.Request(false));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var stopped = first.StopAsync();
        Assert.True(h.Runtime.CurrentEpoch > first.Epoch);
        await Harness.Until(() => stopped.IsCompleted, h.Clock);
        Assert.False((await stopped).OwnershipReleased);
        Assert.False(first.OwnershipRelease.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => h.Start(Harness.Request(false)));
        Assert.Equal(0, h.Credentials.Calls);
        release.TrySetResult();
        await first.OwnershipRelease.WaitAsync(TimeSpan.FromSeconds(20));
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Permissions.Text = async (a, token) =>
        {
            secondEntered.TrySetResult();
            await secondRelease.Task.WaitAsync(token);
            return h.Permissions.Allow(a);
        };
        var second = h.Start(Harness.Request(false));
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await first.StopAsync();
        Assert.Equal(second.Epoch, h.Runtime.CurrentEpoch);
        secondRelease.TrySetResult();
        Assert.Equal(ConversationState.Completed, (await Harness.Finish(second)).State);
        Assert.Equal(1, h.Llm.Calls);
    }

    [Fact]
    public async Task Noncooperative_cancellation_callback_retains_ownership_until_callback_exits()
    {
        await using var h = new Harness(textOnly: true);
        using var callbackRelease = new ManualResetEventSlim();
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authorizerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Permissions.Text = async (a, token) =>
        {
            using var registration = token.Register(() =>
            {
                callbackEntered.TrySetResult();
                callbackRelease.Wait();
            });
            registered.TrySetResult();
            await authorizerRelease.Task;
            return h.Permissions.Allow(a);
        };
        var turn = h.Start(Harness.Request(false));
        await registered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var stop = turn.StopAsync();
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await Harness.Until(() => stop.IsCompleted, h.Clock);
        Assert.False((await stop).OwnershipReleased);
        Assert.Throws<InvalidOperationException>(() => h.Start(Harness.Request(false)));
        callbackRelease.Set();
        authorizerRelease.TrySetResult();
        await turn.OwnershipRelease.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, h.Credentials.Calls);
        Assert.Equal(0, h.Llm.Calls);
    }

    [Fact]
    public async Task A_stalled_authorizer_obeys_original_stage_deadline_without_waiting_for_whole_turn()
    {
        await using var h = new Harness(textOnly: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Permissions.Text = async (a, _) => { entered.TrySetResult(); await release.Task; return h.Permissions.Allow(a); };
        var turn = h.Start(Harness.Request(false, textLimits: new() { MaxRequestTime = TimeSpan.FromMilliseconds(100) }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var result = await Harness.Finish(turn, h.Clock);
        Assert.Equal(ConversationFailure.DeadlineExceeded, result.Failure);
        Assert.Equal(ConversationState.Failed, result.State);
        Assert.False(result.OwnershipReleased);
        Assert.True(h.Clock.GetTimestamp() < TimeSpan.FromSeconds(90).Ticks);
        release.TrySetResult();
        await turn.OwnershipRelease.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Slow_sink_bounds_pcm_and_two_pending_segments_and_backpressures_llm_transport()
    {
        await using var h = new Harness(new() { AutoConsume = false }, new()
        {
            Capacity = TimeSpan.FromMilliseconds(100), Prebuffer = TimeSpan.Zero, MaximumQueuedFrames = 2
        });
        string trace = Harness.Trace(Enumerable.Range(0, 12).Select(i => $"Sentence {i}! ").ToArray());
        var llmBody = new FragmentedTextBody(Encoding.UTF8.GetBytes(trace), 1);
        var pcmBody = new FragmentedTextBody(SpeechFixtures.Audio(4800), 73);
        h.Llm.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(llmBody));
        h.Tts.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(pcmBody));
        var turn = h.Start();
        await h.Device.EnteredWrite.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await Harness.Until(() => turn.Snapshot.QueuedSegments == 2 && turn.Snapshot.AcceptedSamples >= 1440, h.Clock);
        Assert.Equal(2, turn.Snapshot.PeakQueuedSegments);
        Assert.InRange(turn.Snapshot.AcceptedSamples - turn.Snapshot.DeviceConsumedSamples, 1, 2400);
        Assert.InRange(pcmBody.BytesRead, 1, 5760);
        Assert.True(llmBody.BytesRead < Encoding.UTF8.GetByteCount(trace));
        Assert.Equal(1, h.Llm.Calls);
        Assert.Equal(1, h.Tts.Calls);
        var stop = turn.StopAsync();
        await Harness.Until(() => stop.IsCompleted, h.Clock);
        Assert.Equal(ConversationState.Canceled, (await stop).State);
        Assert.True(llmBody.Disposed);
        Assert.True(pcmBody.Disposed);
        Assert.Equal(0, turn.Snapshot.QueuedSegments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Endpoint_loss_or_cleanup_failure_preserves_text_and_never_replays(bool cleanup)
    {
        await using var h = new Harness(new() { BlockWrite = !cleanup, FailDispose = cleanup });
        h.Answer("A complete answer.");
        var turn = h.Start();
        if (!cleanup)
        {
            await h.Device.EnteredBlockedWrite.Task.WaitAsync(TimeSpan.FromSeconds(20));
            h.Device.PaddingError = ErrorCode.AudioDeviceLost;
            h.Device.Release.Set();
        }
        var result = await Harness.Finish(turn);
        Assert.Equal(ConversationState.Partial, result.State);
        Assert.Equal(ConversationFailure.PlaybackFailed, result.Failure);
        Assert.Equal("A complete answer.", turn.Content.Text);
        Assert.Equal(1, h.Tts.Calls);
        Assert.Equal(1, h.Device.Opens);
        Assert.Equal(cleanup, result.Quarantined);
        if (cleanup) Assert.Throws<InvalidOperationException>(() => h.Start());
    }

    [Fact]
    public async Task Native_work_that_ignores_stop_is_quarantined_without_fake_release_or_restart()
    {
        await using var h = new Harness(new() { BlockWrite = true, IgnoreCancellation = true });
        var turn = h.Start();
        await h.Device.EnteredBlockedWrite.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var stopped = turn.StopAsync();
        await Harness.Until(() => stopped.IsCompleted, h.Clock);
        var result = await stopped;
        Assert.False(result.OwnershipReleased);
        Assert.False(turn.OwnershipRelease.IsCompleted);
        Assert.True(result.MayHavePlayed);
        Assert.Throws<InvalidOperationException>(() => h.Start());
        h.Device.Release.Set();
        await turn.OwnershipRelease.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(turn.Snapshot.Quarantined);
        Assert.Equal(0, h.Device.Starts);
        Assert.Throws<InvalidOperationException>(() => h.Start());
    }
}
