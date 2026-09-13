using Martlet.Core.Contracts;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Conversation.Tests;

public sealed class CredentialCancellationRegressionTests
{
    [Theory]
    [InlineData(ProviderRole.Llm)]
    [InlineData(ProviderRole.Tts)]
    public async Task Original_caller_cancellation_is_observed_after_noncooperative_credentials(ProviderRole role)
    {
        await using var h = new Harness();
        h.Clock.DeferCallbacks = true;
        using var caller = new CancellationTokenSource();
        using var callbackRelease = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Credentials.Resolve = async (binding, _) =>
        {
            if (binding.Role == role)
            {
                entered.TrySetResult();
                await resume.Task;
            }
            return new(binding, ProviderFixtures.Secret);
        };
        var turn = h.Start(Harness.Request(speech: role == ProviderRole.Tts), caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        using var registration = caller.Token.Register(() =>
        {
            callbackEntered.TrySetResult();
            callbackRelease.Wait();
        });
        var cancellation = caller.CancelAsync();
        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            resume.TrySetResult();
            var result = await Harness.Finish(turn);
            Assert.Equal(role == ProviderRole.Llm ? 0 : 1, h.Llm.Calls);
            Assert.Equal(0, h.Tts.Calls);
            Assert.Equal(0, h.Device.Opens);
            Assert.Equal(ConversationState.Canceled, result.State);
            Assert.False(cancellation.IsCompleted);
        }
        finally
        {
            callbackRelease.Set();
            resume.TrySetResult();
            await cancellation;
        }
        await turn.OwnershipRelease.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
