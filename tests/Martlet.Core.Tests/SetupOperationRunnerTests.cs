using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class SetupOperationRunnerTests
{
    [Fact]
    public async Task CancellationCallbacksKeepSlotAndSecretUntilTheyActuallyRelease()
    {
        var runner = new SetupOperationRunner();
        var secret = new SecretLease("SYNTHETIC-CANARY");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returnWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callbackRelease = new ManualResetEventSlim();
        var operation = runner.TryStart(async token =>
        {
            token.Register(() =>
            {
                callbackEntered.TrySetResult();
                Assert.True(callbackRelease.Wait(TimeSpan.FromSeconds(5)));
            });
            entered.TrySetResult();
            await returnWork.Task;
            return new SetupWorkResult(SetupWorkOutcome.Completed);
        }, secret)!;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            operation.RequestCancellation();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            returnWork.TrySetResult();
            await Task.Delay(30);
            Assert.True(runner.IsRunning);
            Assert.False(operation.Completion.IsCompleted);
            secret.Use(value => Assert.Equal("SYNTHETIC-CANARY", new string(value)));
            var rejectedSecret = new SecretLease("REJECTED-CANARY");
            Assert.Null(runner.TryStart(_ => throw new InvalidOperationException("Must never start."), rejectedSecret));
            Assert.Throws<ObjectDisposedException>(() => rejectedSecret.Use(_ => { }));
            callbackRelease.Set();
            Assert.Equal(SetupWorkOutcome.Completed, (await operation.Completion.WaitAsync(TimeSpan.FromSeconds(3))).Outcome);
            Assert.False(runner.IsRunning);
            Assert.Throws<ObjectDisposedException>(() => secret.Use(_ => { }));
        }
        finally { callbackRelease.Set(); returnWork.TrySetResult(); await operation.Completion; }
    }

    [Fact]
    public async Task UnobservedLateFaultIsSanitizedAndReleasesOwnedLease()
    {
        var runner = new SetupOperationRunner();
        var secret = new SecretLease("SYNTHETIC-CANARY");
        var operation = runner.TryStart(_ => throw new InvalidOperationException("SYNTHETIC-CANARY"), secret)!;
        var result = await operation.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(SetupWorkOutcome.Failed, result.Outcome);
        Assert.DoesNotContain("SYNTHETIC-CANARY", result.ToString());
        Assert.False(runner.IsRunning);
        Assert.Throws<ObjectDisposedException>(() => secret.Use(_ => { }));
    }
}
