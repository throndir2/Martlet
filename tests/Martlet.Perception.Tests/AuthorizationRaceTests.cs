using static Martlet.Perception.Tests.CaptureFixtures;

namespace Martlet.Perception.Tests;

public sealed class AuthorizationRaceTests
{
    [Fact]
    public async Task CommitAndRevocationHaveOneLinearizedOrder()
    {
        var clock = new ManualCaptureClock();
        var state = new OneUseAuthorizationState(
            clock.GetUtcNow().AddSeconds(10),
            TimeSpan.FromSeconds(30),
            clock);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var committed = false;
        var commit = Task.Run(() => state.Commit(() =>
        {
            entered.Set();
            release.Wait();
            committed = true;
        }));
        await Until(() => entered.IsSet);

        var revoke = Task.Run(state.Revoke);
        await Task.Delay(20);
        Assert.False(revoke.IsCompleted);
        release.Set();
        await Task.WhenAll(commit, revoke).WaitAsync(Wait);

        Assert.True(committed);
        Assert.True(state.IsRevoked);
        Assert.Equal(
            PerceptionFailureCode.Canceled,
            Failure(state.Check));
    }

    [Fact]
    public void RevocationWinsBeforeAtomicConsumption()
    {
        var clock = new ManualCaptureClock();
        var state = new OneUseAuthorizationState(
            clock.GetUtcNow().AddSeconds(10),
            TimeSpan.FromSeconds(30),
            clock);
        state.Revoke();

        Assert.Equal(
            PerceptionFailureCode.Canceled,
            Failure(state.Consume));
        Assert.False(state.IsConsumed);
    }
}
