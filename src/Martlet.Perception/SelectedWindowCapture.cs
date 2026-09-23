namespace Martlet.Perception;

public sealed class SelectedWindowCapture : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Guid ownerId = Guid.NewGuid();
    private readonly Guid sessionId;
    private readonly INativeWindowCaptureFactory? factory;
    private readonly PerceptionOptions options;
    private readonly TimeProvider clock;
    private readonly CaptureRateGate rateGate;
    private Guid configurationRevision;
    private Guid authorizationRevision;
    private long enumerationGeneration;
    private long highestEpoch = -1;
    private IReadOnlyList<WindowCaptureSource> sources =
        Array.Empty<WindowCaptureSource>();
    private WindowSourceEnumerationOperation? activeEnumeration;
    private WindowCaptureOperation? active;
    private WindowCaptureSource? selectedSource;
    private CaptureDestination? selectedDestination;
    private bool sourceSelectionKnown;
    private bool paused;
    private bool locked;
    private bool disposed;

    public WindowsCaptureAvailability Availability =>
        WindowsCaptureEligibility.Production;

    public Guid AuthorizationRevision
    {
        get
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return authorizationRevision;
            }
        }
    }

    internal Task<WindowCaptureOwnershipRelease>? ActiveEnumerationRelease
    {
        get
        {
            lock (gate)
                return activeEnumeration?.OwnershipRelease;
        }
    }

    public SelectedWindowCapture(
        Guid sessionId,
        Guid configurationRevision,
        PerceptionOptions? options = null,
        TimeProvider? timeProvider = null)
        : this(
            sessionId,
            configurationRevision,
            factory: null,
            options,
            timeProvider)
    {
    }

    internal SelectedWindowCapture(
        Guid sessionId,
        Guid configurationRevision,
        INativeWindowCaptureFactory? factory,
        PerceptionOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        PerceptionGuard.Require(sessionId != Guid.Empty &&
            configurationRevision != Guid.Empty,
            PerceptionFailureCode.InvalidBinding);
        this.sessionId = sessionId;
        this.configurationRevision = configurationRevision;
        authorizationRevision = Guid.NewGuid();
        this.factory = factory;
        this.options = options ?? new();
        this.options.Validate();
        clock = timeProvider ?? TimeProvider.System;
        rateGate = new(clock, this.options.FrameInterval);
    }

    public async Task<IReadOnlyList<WindowCaptureSource>> EnumerateSourcesAsync(
        SourceEnumerationAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        // This production gate precedes authorization consumption and every native call.
        if (factory is null)
            throw new PerceptionException(
                PerceptionFailureCode.NativePrivacyUnqualified);

        ArgumentNullException.ThrowIfNull(authorization);
        WindowSourceEnumerationOperation operation;
        long generation;
        Guid enumerationRevision;
        Guid expectedConfigurationRevision;
        Guid expectedAuthorizationRevision;
        lock (gate)
        {
            ThrowIfUnavailable();
            authorization.ValidateFor(
                sessionId,
                configurationRevision,
                authorizationRevision,
                clock);
            cancellationToken.ThrowIfCancellationRequested();
            PerceptionGuard.Require(
                activeEnumeration is null ||
                activeEnumeration.OwnershipRelease.IsCompletedSuccessfully &&
                activeEnumeration.OwnershipRelease.Result.Released,
                PerceptionFailureCode.Busy);
            PerceptionGuard.Require(
                active is null ||
                active.OwnershipRelease.IsCompletedSuccessfully &&
                active.OwnershipRelease.Result.Released,
                PerceptionFailureCode.Busy);
            authorization.Consume();
            AdvanceAuthorizationRevision();
            generation = checked(++enumerationGeneration);
            enumerationRevision = Guid.NewGuid();
            expectedConfigurationRevision = configurationRevision;
            expectedAuthorizationRevision = authorizationRevision;
            sources = Array.Empty<WindowCaptureSource>();
            selectedSource = null;
            sourceSelectionKnown = false;
            active?.Stop(new(
                PerceptionFailureCode.SourceSelectionChanged));
            operation = new(
                factory,
                authorization,
                options.MaximumSources,
                clock,
                cancellationToken);
            activeEnumeration = operation;
        }

        operation.Begin();
        var discovered = await operation.Completion.ConfigureAwait(false);

        lock (gate)
        {
            ThrowIfUnavailable();
            PerceptionGuard.Require(
                ReferenceEquals(activeEnumeration, operation) &&
                generation == enumerationGeneration &&
                expectedConfigurationRevision == configurationRevision &&
                expectedAuthorizationRevision == authorizationRevision &&
                authorization.Binding.ConfigurationRevision ==
                    expectedConfigurationRevision,
                PerceptionFailureCode.ConfigurationChanged);
            PerceptionGuard.Require(
                discovered.Count <= options.MaximumSources,
                PerceptionFailureCode.EnumerationFailed);

            IReadOnlyList<WindowCaptureSource>? committed = null;
            authorization.Commit(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var unique = new HashSet<NativeWindowSource>(
                    ReferenceEqualityComparer.Instance);
                var materialized =
                    new WindowCaptureSource[discovered.Count];
                for (var index = 0; index < discovered.Count; index++)
                {
                    var native = discovered[index];
                    PerceptionGuard.Require(
                        native is not null && unique.Add(native),
                        PerceptionFailureCode.EnumerationFailed);
                    if (native is null)
                        throw new PerceptionException(
                            PerceptionFailureCode.EnumerationFailed);
                    native.Validate();
                    materialized[index] = new(
                        ownerId,
                        generation,
                        enumerationRevision,
                        expectedAuthorizationRevision,
                        native);
                }

                sources = Array.AsReadOnly(materialized);
                committed = sources;
            });
            return committed!;
        }
    }

    public WindowCaptureOperation Start(
        WindowCaptureSource source,
        CaptureDestination destination,
        WindowCaptureAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        // There is no public production native composition. Fail before consuming consent.
        if (factory is null)
            throw new PerceptionException(
                PerceptionFailureCode.NativePrivacyUnqualified);

        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(authorization);
        WindowCaptureOperation operation;
        lock (gate)
        {
            ThrowIfUnavailable();
            destination.Validate();
            PerceptionGuard.Require(
                source.BelongsTo(ownerId, enumerationGeneration) &&
                sources.Any(candidate =>
                    ReferenceEquals(candidate, source)),
                PerceptionFailureCode.SourceUnavailable);
            if (sourceSelectionKnown)
            {
                PerceptionGuard.Require(
                    ReferenceEquals(selectedSource, source),
                    PerceptionFailureCode.SourceSelectionChanged);
            }
            if (selectedDestination is not null)
            {
                PerceptionGuard.Require(
                    ReferenceEquals(selectedDestination, destination),
                    PerceptionFailureCode.DestinationChanged);
            }
            authorization.ValidateFor(
                source,
                destination,
                sessionId,
                configurationRevision,
                authorizationRevision,
                options,
                clock);
            PerceptionGuard.Require(
                authorization.Binding.Epoch > highestEpoch,
                PerceptionFailureCode.InvalidBinding);
            cancellationToken.ThrowIfCancellationRequested();
            PerceptionGuard.Require(
                active is null ||
                active.OwnershipRelease.IsCompletedSuccessfully &&
                active.OwnershipRelease.Result.Released,
                PerceptionFailureCode.Busy);
            rateGate.RequireStartAllowed();
            active?.Stop(new(PerceptionFailureCode.Canceled));
            authorization.Consume();
            highestEpoch = authorization.Binding.Epoch;
            if (!sourceSelectionKnown)
            {
                selectedSource = source;
                sourceSelectionKnown = true;
            }
            selectedDestination ??= destination;
            operation = new(
                source,
                destination,
                authorization,
                factory,
                rateGate,
                options,
                clock,
                cancellationToken);
            active = operation;
        }

        operation.Begin();
        return operation;
    }

    public Task<WindowCaptureCompletion?> StopAsync()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            AdvanceAuthorizationRevision();
            activeEnumeration?.Cancel(
                new(PerceptionFailureCode.Canceled));
            return StopActive(new(PerceptionFailureCode.Canceled));
        }
    }

    public Task<WindowCaptureCompletion?> SetPausedAsync(bool value)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (paused == value)
                return Task.FromResult<WindowCaptureCompletion?>(null);
            paused = value;
            AdvanceAuthorizationRevision();
            if (!value)
                return Task.FromResult<WindowCaptureCompletion?>(null);
            activeEnumeration?.Cancel(
                new(PerceptionFailureCode.Paused));
            return StopActive(new(PerceptionFailureCode.Paused));
        }
    }

    public Task<WindowCaptureCompletion?> SetSessionLockedAsync(bool value)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (locked == value)
                return Task.FromResult<WindowCaptureCompletion?>(null);
            locked = value;
            AdvanceAuthorizationRevision();
            if (!value)
                return Task.FromResult<WindowCaptureCompletion?>(null);
            ClearSources();
            activeEnumeration?.Cancel(
                new(PerceptionFailureCode.Locked));
            return StopActive(new(PerceptionFailureCode.Locked));
        }
    }

    public Task<WindowCaptureCompletion?> NotifyConfigurationChangedAsync(
        Guid newConfigurationRevision)
    {
        PerceptionGuard.Require(newConfigurationRevision != Guid.Empty,
            PerceptionFailureCode.InvalidBinding);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (configurationRevision == newConfigurationRevision)
                return Task.FromResult<WindowCaptureCompletion?>(null);
            configurationRevision = newConfigurationRevision;
            AdvanceAuthorizationRevision();
            ClearSources();
            activeEnumeration?.Cancel(
                new(PerceptionFailureCode.ConfigurationChanged));
            return StopActive(
                new(PerceptionFailureCode.ConfigurationChanged));
        }
    }

    public Task<WindowCaptureCompletion?> NotifyDestinationChangedAsync(
        CaptureDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Validate();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (ReferenceEquals(selectedDestination, destination))
                return Task.FromResult<WindowCaptureCompletion?>(null);
            selectedDestination = destination;
            AdvanceAuthorizationRevision();
            return StopActive(
                new(PerceptionFailureCode.DestinationChanged));
        }
    }

    public Task<WindowCaptureCompletion?> NotifySourceSelectionChangedAsync(
        WindowCaptureSource? source)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (source is not null)
            {
                PerceptionGuard.Require(
                    source.BelongsTo(ownerId, enumerationGeneration) &&
                    sources.Any(candidate =>
                        ReferenceEquals(candidate, source)),
                    PerceptionFailureCode.SourceUnavailable);
            }
            if (sourceSelectionKnown &&
                ReferenceEquals(selectedSource, source))
            {
                return Task.FromResult<WindowCaptureCompletion?>(null);
            }
            sourceSelectionKnown = true;
            selectedSource = source;
            AdvanceAuthorizationRevision();
            return StopActive(
                new(PerceptionFailureCode.SourceSelectionChanged));
        }
    }

    public async ValueTask DisposeAsync()
    {
        WindowCaptureOperation? ownedCapture;
        WindowSourceEnumerationOperation? ownedEnumeration;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            AdvanceAuthorizationRevision();
            ClearSources();
            ownedCapture = active;
            ownedEnumeration = activeEnumeration;
            ownedEnumeration?.Cancel(
                new(PerceptionFailureCode.Disposed));
            ownedCapture?.Stop(
                new(PerceptionFailureCode.Disposed));
        }

        await ObserveReleaseAsync(
            ownedEnumeration?.OwnershipRelease).ConfigureAwait(false);
        await ObserveReleaseAsync(
            ownedCapture?.OwnershipRelease).ConfigureAwait(false);
        if (ownedCapture is not null)
        {
            try
            {
                await ownedCapture.LifetimeRelease.WaitAsync(
                    options.ShutdownObservationWait,
                    clock).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
    }

    private Task<WindowCaptureCompletion?> StopActive(
        PerceptionFailure failure) =>
        active is null
            ? Task.FromResult<WindowCaptureCompletion?>(null)
            : AwaitNullable(active.Stop(failure));

    private static async Task<WindowCaptureCompletion?> AwaitNullable(
        Task<WindowCaptureCompletion> completion) =>
        await completion.ConfigureAwait(false);

    private async Task ObserveReleaseAsync(
        Task<WindowCaptureOwnershipRelease>? release)
    {
        if (release is null)
            return;
        try
        {
            await release.WaitAsync(
                options.ShutdownObservationWait,
                clock).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }

    private void ThrowIfUnavailable()
    {
        PerceptionGuard.Require(!disposed, PerceptionFailureCode.Disposed);
        PerceptionGuard.Require(!paused, PerceptionFailureCode.Paused);
        PerceptionGuard.Require(!locked, PerceptionFailureCode.Locked);
    }

    private void AdvanceAuthorizationRevision() =>
        authorizationRevision = Guid.NewGuid();

    private void ClearSources()
    {
        enumerationGeneration++;
        sources = Array.Empty<WindowCaptureSource>();
        selectedSource = null;
        sourceSelectionKnown = false;
    }
}
