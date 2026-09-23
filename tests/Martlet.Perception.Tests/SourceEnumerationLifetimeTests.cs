using System.Reflection;
using static Martlet.Perception.Tests.CaptureFixtures;

namespace Martlet.Perception.Tests;

public sealed class SourceEnumerationLifetimeTests
{
    [Fact]
    public async Task CallerCancellationAfterNativeRetirementPreventsSourcePublication()
    {
        using var native = new ManualResetEventSlim();
        using var retired = new ManualResetEventSlim();
        using var caller = new CancellationTokenSource();
        var factory = new ControlledNativeFactory { OnEnumerate = native.Wait };
        var clock = new ManualCaptureClock();
        await using var capture = Owner(factory, clock, Options());
        var enumeration = capture.EnumerateSourcesAsync(
            EnumerationAuthorization(capture, clock, TimeSpan.FromSeconds(10)),
            caller.Token);
        await Until(() => factory.Enumerations == 1);
        var release = capture.ActiveEnumerationRelease!;
        var ownerGate = typeof(SelectedWindowCapture)
            .GetField("gate", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(capture)!;

        try
        {
            var publicationBlock = Task.Run(() =>
            {
                lock (ownerGate)
                {
                    native.Set();
                    Assert.True(retired.Wait(Wait));
                    caller.Cancel();
                }
            });
            Assert.True((await release.WaitAsync(Wait)).Released);
            retired.Set();
            await publicationBlock.WaitAsync(Wait);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await enumeration.WaitAsync(Wait));
        }
        finally
        {
            native.Set();
            retired.Set();
        }
    }

    [Theory]
    [InlineData("pause", PerceptionFailureCode.Paused)]
    [InlineData("lock", PerceptionFailureCode.Locked)]
    [InlineData("revoke", PerceptionFailureCode.Canceled)]
    [InlineData("expiry", PerceptionFailureCode.DeadlineExceeded)]
    public async Task BlockedEnumerationIsCanceledAndQuarantinesItsSlot(
        string cause,
        PerceptionFailureCode expected)
    {
        using var native = new ManualResetEventSlim();
        var factory = new ControlledNativeFactory
        {
            OnEnumerate = native.Wait
        };
        var clock = new ManualCaptureClock();
        var options = Options();
        var capture = Owner(factory, clock, options);
        var authorization = EnumerationAuthorization(
            capture,
            clock,
            cause == "expiry"
                ? TimeSpan.FromSeconds(1)
                : TimeSpan.FromSeconds(10));
        var enumeration = capture.EnumerateSourcesAsync(authorization);
        await Until(() => factory.Enumerations == 1);
        var release = capture.ActiveEnumerationRelease!;

        if (cause == "pause")
            await capture.SetPausedAsync(true);
        else if (cause == "lock")
            await capture.SetSessionLockedAsync(true);
        else if (cause == "revoke")
        {
            authorization.Revoke();
            clock.Advance(TimeSpan.FromMilliseconds(20));
        }
        else
        {
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var failure = await Assert.ThrowsAsync<PerceptionException>(
            async () => await enumeration);
        Assert.Equal(expected, failure.Failure.Code);
        Assert.False(release.IsCompleted);

        if (cause == "pause")
            await capture.SetPausedAsync(false);
        if (cause == "lock")
            await capture.SetSessionLockedAsync(false);
        var fresh = EnumerationAuthorization(
            capture,
            clock,
            TimeSpan.FromSeconds(10));
        Assert.Equal(
            PerceptionFailureCode.Busy,
            await FailureAsync(async () =>
                await capture.EnumerateSourcesAsync(fresh)));
        Assert.False(fresh.IsConsumed);

        native.Set();
        Assert.True((await release.WaitAsync(Wait)).Released);
        factory.OnEnumerate = null;
        Assert.Single(
            await capture.EnumerateSourcesAsync(fresh));
        await capture.DisposeAsync();
    }

    [Fact]
    public async Task DisposalCancelsBlockedEnumerationButDoesNotClaimRelease()
    {
        using var native = new ManualResetEventSlim();
        var factory = new ControlledNativeFactory
        {
            OnEnumerate = native.Wait
        };
        var clock = new ManualCaptureClock();
        var capture = Owner(factory, clock, Options());
        var enumeration = capture.EnumerateSourcesAsync(
            EnumerationAuthorization(
                capture,
                clock,
                TimeSpan.FromSeconds(10)));
        await Until(() => factory.Enumerations == 1);
        var release = capture.ActiveEnumerationRelease!;
        var disposing = capture.DisposeAsync().AsTask();

        var failure = await Assert.ThrowsAsync<PerceptionException>(
            async () => await enumeration);
        Assert.Equal(
            PerceptionFailureCode.Disposed,
            failure.Failure.Code);
        Assert.False(release.IsCompleted);
        Assert.False(disposing.IsCompleted);

        native.Set();
        Assert.True((await release.WaitAsync(Wait)).Released);
        await disposing.WaitAsync(Wait);
    }

    private static SourceEnumerationAuthorization EnumerationAuthorization(
        SelectedWindowCapture capture,
        ManualCaptureClock clock,
        TimeSpan lifetime) =>
        new(
            new(
                SessionId,
                Guid.NewGuid(),
                ConfigurationRevision,
                capture.AuthorizationRevision),
            clock.GetUtcNow() + lifetime,
            selectedWindowEnumerationRequested: true,
            clock);
}
