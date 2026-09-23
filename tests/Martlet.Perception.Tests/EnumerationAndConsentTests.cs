using static Martlet.Perception.Tests.CaptureFixtures;

namespace Martlet.Perception.Tests;

public sealed class EnumerationAndConsentTests
{
    [Fact]
    public async Task EnumerationConsentCannotTransferToAnotherOwnerWithSameConfiguration()
    {
        var factory = new ControlledNativeFactory();
        var clock = new ManualCaptureClock();
        await using var first = Owner(factory, clock, Options());
        await using var second = Owner(factory, clock, Options());
        var authorization = new SourceEnumerationAuthorization(
            new(SessionId, Guid.NewGuid(), ConfigurationRevision,
                first.AuthorizationRevision),
            clock.GetUtcNow().AddSeconds(10), true, clock);

        Assert.Equal(PerceptionFailureCode.InvalidBinding,
            await FailureAsync(async () => await second.EnumerateSourcesAsync(authorization)));
        Assert.False(authorization.IsConsumed);
        Assert.Equal(0, factory.Enumerations);
    }

    [Fact]
    public async Task EnumerationRequiresFreshExactOneUseConsent()
    {
        var factory = new ControlledNativeFactory();
        var clock = new ManualCaptureClock();
        await using var capture = Owner(factory, clock, Options());
        var binding = new SourceEnumerationBinding(
            SessionId,
            Guid.NewGuid(),
            ConfigurationRevision,
            capture.AuthorizationRevision);
        var denied = new SourceEnumerationAuthorization(
            binding,
            clock.GetUtcNow().AddSeconds(10),
            timeProvider: clock);

        Assert.Equal(
            PerceptionFailureCode.AuthorizationRequired,
            Failure(() => capture.EnumerateSourcesAsync(denied)
                .GetAwaiter()
                .GetResult()));
        Assert.False(denied.IsConsumed);
        Assert.Equal(0, factory.Enumerations);

        var allowed = new SourceEnumerationAuthorization(
            binding,
            clock.GetUtcNow().AddSeconds(10),
            selectedWindowEnumerationRequested: true,
            clock);
        Assert.Single(await capture.EnumerateSourcesAsync(allowed));
        Assert.True(allowed.IsConsumed);
        Assert.Equal(
            PerceptionFailureCode.AuthorizationConsumed,
            Failure(() => capture.EnumerateSourcesAsync(allowed)
                .GetAwaiter()
                .GetResult()));
        Assert.Equal(1, factory.Enumerations);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("revision")]
    [InlineData("clock")]
    public async Task EnumerationScopeMismatchDoesNotConsumeConsent(
        string changed)
    {
        var factory = new ControlledNativeFactory();
        var clock = new ManualCaptureClock();
        await using var capture = Owner(factory, clock, Options());
        var binding = new SourceEnumerationBinding(
            changed == "session" ? Guid.NewGuid() : SessionId,
            Guid.NewGuid(),
            changed == "revision"
                ? Guid.NewGuid()
                : ConfigurationRevision,
            capture.AuthorizationRevision);
        var authorization = new SourceEnumerationAuthorization(
            binding,
            clock.GetUtcNow().AddSeconds(10),
            selectedWindowEnumerationRequested: true,
            changed == "clock" ? new ManualCaptureClock() : clock);

        Assert.Equal(
            PerceptionFailureCode.InvalidBinding,
            Failure(() => capture.EnumerateSourcesAsync(authorization)
                .GetAwaiter()
                .GetResult()));
        Assert.False(authorization.IsConsumed);
        Assert.Equal(0, factory.Enumerations);
    }

    [Fact]
    public async Task EnumeratedIdentityIsOpaqueAndOldEnumerationCannotCapture()
    {
        var factory = new ControlledNativeFactory();
        factory.Sources.Clear();
        factory.Sources.Add(new(Canary, Canary));
        var clock = new ManualCaptureClock();
        var options = Options();
        await using var capture = Owner(factory, clock, options);
        var first = Enumerate(capture, clock);
        var second = Enumerate(capture, clock);
        var destination = RemoteDestination();
        var permission = Authorize(
            first,
            destination,
            clock,
            options);

        Assert.DoesNotContain(Canary, first.Id.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, first.ToString(),
            StringComparison.Ordinal);
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(
            first.EnumerationRevision,
            second.EnumerationRevision);
        Assert.Equal(
            PerceptionFailureCode.SourceUnavailable,
            Failure(() => capture.Start(first, destination, permission)));
        Assert.False(permission.IsConsumed);
        Assert.Equal(0, factory.Opens);
    }

    [Theory]
    [InlineData("empty-label")]
    [InlineData("control-label")]
    [InlineData("too-many")]
    [InlineData("duplicate")]
    public async Task MalformedEnumerationFailsWithoutASelection(
        string fault)
    {
        var factory = new ControlledNativeFactory();
        factory.Sources.Clear();
        if (fault == "empty-label")
            factory.Sources.Add(new("", "window"));
        else if (fault == "control-label")
            factory.Sources.Add(new("application", "window\nsecret"));
        else if (fault == "duplicate")
        {
            var source = new NativeWindowSource("application", "window");
            factory.Sources.Add(source);
            factory.Sources.Add(source);
        }
        else
        {
            for (var index = 0; index < 9; index++)
                factory.Sources.Add(new($"application {index}", $"window {index}"));
        }

        var clock = new ManualCaptureClock();
        await using var capture = Owner(factory, clock, Options());
        var authorization = new SourceEnumerationAuthorization(
            new(
                SessionId,
                Guid.NewGuid(),
                ConfigurationRevision,
                capture.AuthorizationRevision),
            clock.GetUtcNow().AddSeconds(10),
            selectedWindowEnumerationRequested: true,
            clock);

        Assert.Equal(
            fault is "empty-label" or "control-label"
                ? PerceptionFailureCode.InvalidInput
                : PerceptionFailureCode.EnumerationFailed,
            Failure(() => capture.EnumerateSourcesAsync(authorization)
                .GetAwaiter()
                .GetResult()));
        Assert.True(authorization.IsConsumed);
        Assert.Equal(0, factory.Opens);
    }

    [Fact]
    public async Task CaptureRequiresExactSourceDestinationOptionsAndMode()
    {
        var factory = new ControlledNativeFactory();
        var clock = new ManualCaptureClock();
        var options = Options();
        await using var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var destination = RemoteDestination();
        var mismatchedDestination = destination with
        {
            Revision = Guid.NewGuid()
        };
        var wrongDestinationPermission = Authorize(
            source,
            destination,
            clock,
            options);

        Assert.Equal(
            PerceptionFailureCode.InvalidBinding,
            Failure(() => capture.Start(
                source,
                mismatchedDestination,
                wrongDestinationPermission)));
        Assert.False(wrongDestinationPermission.IsConsumed);
        Assert.Equal(0, factory.Opens);

        var denied = Authorize(
            source,
            destination,
            clock,
            options,
            capture: false);
        Assert.Equal(
            PerceptionFailureCode.AuthorizationRequired,
            Failure(() => capture.Start(source, destination, denied)));
        Assert.False(denied.IsConsumed);

        var continuousBinding = Binding(
            source,
            destination,
            WindowCaptureMode.Continuous,
            lifetime: options.SessionLifetime);
        var noContinuousConsent = Authorize(
            source,
            destination,
            clock,
            options,
            WindowCaptureMode.Continuous,
            continuous: false,
            binding: continuousBinding);
        Assert.Equal(
            PerceptionFailureCode.AuthorizationRequired,
            Failure(() => capture.Start(
                source,
                destination,
                noContinuousConsent)));
        Assert.False(noContinuousConsent.IsConsumed);
        Assert.Equal(0, factory.Opens);
    }

    [Fact]
    public async Task CaptureAuthorizationIsConsumedOnceAtNativeAdmission()
    {
        var factory = new ControlledNativeFactory();
        var session = new ControlledNativeSession();
        var clock = new ManualCaptureClock();
        session.Frames.Enqueue(Frame(clock));
        factory.NewSession = () => session;
        var options = Options();
        await using var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var destination = RemoteDestination();
        var authorization = Authorize(
            source,
            destination,
            clock,
            options);

        var operation = capture.Start(
            source,
            destination,
            authorization);
        Assert.True(authorization.IsConsumed);
        await operation.Completion.WaitAsync(Wait);
        await operation.OwnershipRelease.WaitAsync(Wait);

        Assert.Equal(
            PerceptionFailureCode.AuthorizationConsumed,
            Failure(() => capture.Start(
                source,
                destination,
                authorization)));
        Assert.Equal(1, factory.Opens);
    }

    [Fact]
    public async Task LockClearsEnumeratedSourcesAndRequiresFreshDiscovery()
    {
        var factory = new ControlledNativeFactory();
        var clock = new ManualCaptureClock();
        var options = Options();
        await using var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var destination = RemoteDestination();
        await capture.SetSessionLockedAsync(true);
        await capture.SetSessionLockedAsync(false);
        var authorization = Authorize(
            source,
            destination,
            clock,
            options);

        Assert.Equal(
            PerceptionFailureCode.SourceUnavailable,
            Failure(() => capture.Start(
                source,
                destination,
                authorization)));
        Assert.False(authorization.IsConsumed);
        Assert.Equal(0, factory.Opens);
    }

    [Fact]
    public async Task IdleDestinationChangeInvalidatesPendingConsent()
    {
        var factory = new ControlledNativeFactory();
        var session = new ControlledNativeSession();
        var clock = new ManualCaptureClock();
        session.Frames.Enqueue(Frame(clock));
        factory.NewSession = () => session;
        var options = Options();
        await using var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var previousDestination = RemoteDestination();
        var pending = Authorize(
            source,
            previousDestination,
            clock,
            options);
        var selectedDestination = RemoteDestination();

        Assert.Null(
            await capture.NotifyDestinationChangedAsync(
                selectedDestination));
        Assert.Equal(
            PerceptionFailureCode.DestinationChanged,
            Failure(() => capture.Start(
                source,
                previousDestination,
                pending)));
        Assert.False(pending.IsConsumed);
        Assert.Equal(0, factory.Opens);

        var fresh = Authorize(
            source,
            selectedDestination,
            clock,
            options,
            authorizationRevision: capture.AuthorizationRevision);
        var operation = capture.Start(
            source,
            selectedDestination,
            fresh);
        Assert.Equal(
            WindowCaptureOutcome.Captured,
            (await operation.Completion.WaitAsync(Wait)).Outcome);
        Assert.True(
            (await operation.OwnershipRelease.WaitAsync(Wait)).Released);
    }

    [Fact]
    public async Task IdleSourceAndConfigurationChangesInvalidatePendingConsent()
    {
        var factory = new ControlledNativeFactory();
        var clock = new ManualCaptureClock();
        var options = Options();
        await using var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var destination = RemoteDestination();
        var pendingSource = Authorize(
            source,
            destination,
            clock,
            options);

        Assert.Null(
            await capture.NotifySourceSelectionChangedAsync(null));
        Assert.Equal(
            PerceptionFailureCode.SourceSelectionChanged,
            Failure(() => capture.Start(
                source,
                destination,
                pendingSource)));
        Assert.False(pendingSource.IsConsumed);

        await capture.NotifySourceSelectionChangedAsync(source);
        var pendingConfiguration = Authorize(
            source,
            destination,
            clock,
            options,
            authorizationRevision: capture.AuthorizationRevision);
        await capture.NotifyConfigurationChangedAsync(Guid.NewGuid());
        Assert.Equal(
            PerceptionFailureCode.SourceUnavailable,
            Failure(() => capture.Start(
                source,
                destination,
                pendingConfiguration)));
        Assert.False(pendingConfiguration.IsConsumed);
        Assert.Equal(0, factory.Opens);
    }

    [Fact]
    public async Task PauseInvalidatesIdleEnumerationConsent()
    {
        var factory = new ControlledNativeFactory();
        var clock = new ManualCaptureClock();
        await using var capture = Owner(factory, clock, Options());
        var pending = new SourceEnumerationAuthorization(
            new(
                SessionId,
                Guid.NewGuid(),
                ConfigurationRevision,
                capture.AuthorizationRevision),
            clock.GetUtcNow().AddSeconds(10),
            selectedWindowEnumerationRequested: true,
            clock);

        await capture.SetPausedAsync(true);
        await capture.SetPausedAsync(false);

        Assert.Equal(
            PerceptionFailureCode.InvalidBinding,
            await FailureAsync(async () =>
                await capture.EnumerateSourcesAsync(pending)));
        Assert.False(pending.IsConsumed);
        Assert.Equal(0, factory.Enumerations);
    }
}
