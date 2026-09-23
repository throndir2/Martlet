namespace Martlet.Perception;

internal delegate void AuthorizedPixelCopy(Span<byte> destination);

internal sealed class OneUseAuthorizationState
{
    private readonly object gate = new();
    private readonly long startedAt;
    private readonly TimeSpan lifetime;
    private bool consumed;
    private bool revoked;

    internal TimeProvider Clock { get; }
    internal DateTimeOffset ExpiresAtUtc { get; }
    internal bool IsConsumed
    {
        get
        {
            lock (gate)
                return consumed;
        }
    }

    internal bool IsRevoked
    {
        get
        {
            lock (gate)
                return revoked;
        }
    }

    internal OneUseAuthorizationState(
        DateTimeOffset expiresAtUtc,
        TimeSpan maximumLifetime,
        TimeProvider? timeProvider)
    {
        Clock = timeProvider ?? TimeProvider.System;
        PerceptionGuard.Utc(expiresAtUtc);
        startedAt = Clock.GetTimestamp();
        lifetime = expiresAtUtc - Clock.GetUtcNow();
        PerceptionGuard.Require(lifetime > TimeSpan.Zero && lifetime <= maximumLifetime,
            PerceptionFailureCode.DeadlineExceeded);
        ExpiresAtUtc = expiresAtUtc;
    }

    internal void ValidateClock(TimeProvider clock) =>
        PerceptionGuard.Require(ReferenceEquals(Clock, clock),
            PerceptionFailureCode.InvalidBinding);

    internal void Check()
    {
        lock (gate)
            CheckUnderLock();
    }

    internal void Consume()
    {
        lock (gate)
        {
            PerceptionGuard.Require(!consumed,
                PerceptionFailureCode.AuthorizationConsumed);
            CheckUnderLock();
            consumed = true;
        }
    }

    internal void Commit(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (gate)
        {
            CheckUnderLock();
            action();
        }
    }

    internal void Revoke()
    {
        lock (gate)
            revoked = true;
    }

    internal void CommitCopy(Span<byte> destination, AuthorizedPixelCopy copy)
    {
        lock (gate)
        {
            CheckUnderLock();
            copy(destination);
        }
    }

    private void CheckUnderLock()
    {
        PerceptionGuard.Require(!revoked, PerceptionFailureCode.Canceled);
        PerceptionGuard.Require(Clock.GetUtcNow() < ExpiresAtUtc &&
            Clock.GetElapsedTime(startedAt) < lifetime,
            PerceptionFailureCode.DeadlineExceeded);
    }
}

public sealed class SourceEnumerationAuthorization
{
    private readonly OneUseAuthorizationState state;

    public SourceEnumerationBinding Binding { get; }
    public bool SelectedWindowEnumerationRequested { get; }
    public DateTimeOffset ExpiresAtUtc => state.ExpiresAtUtc;
    public bool IsConsumed => state.IsConsumed;
    public bool IsRevoked => state.IsRevoked;

    public SourceEnumerationAuthorization(
        SourceEnumerationBinding binding,
        DateTimeOffset expiresAtUtc,
        bool selectedWindowEnumerationRequested = false,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        binding.Validate();
        Binding = binding;
        SelectedWindowEnumerationRequested = selectedWindowEnumerationRequested;
        state = new(expiresAtUtc, TimeSpan.FromSeconds(30), timeProvider);
    }

    public void Revoke() => state.Revoke();

    internal void ValidateFor(
        Guid sessionId,
        Guid configurationRevision,
        Guid authorizationRevision,
        TimeProvider clock)
    {
        PerceptionGuard.Require(SelectedWindowEnumerationRequested,
            PerceptionFailureCode.AuthorizationRequired);
        PerceptionGuard.Require(!state.IsConsumed,
            PerceptionFailureCode.AuthorizationConsumed);
        PerceptionGuard.Require(Binding.SessionId == sessionId &&
            Binding.ConfigurationRevision == configurationRevision &&
            Binding.AuthorizationRevision == authorizationRevision,
            PerceptionFailureCode.InvalidBinding);
        state.ValidateClock(clock);
        state.Check();
    }

    internal void Check() => state.Check();
    internal void Consume() => state.Consume();
    internal void Commit(Action action) => state.Commit(action);

    public override string ToString() =>
        "Explicit selected-window enumeration authorization (source labels omitted)";
}

public sealed class WindowCaptureAuthorization
{
    private readonly OneUseAuthorizationState state;
    private readonly WindowCaptureSource source;
    private readonly CaptureDestination destination;

    public WindowCaptureBinding Binding { get; }
    public PerceptionOptions Options { get; }
    public bool SelectedWindowCaptureRequested { get; }
    public bool ContinuousCaptureRequested { get; }
    public DateTimeOffset ExpiresAtUtc => state.ExpiresAtUtc;
    public bool IsConsumed => state.IsConsumed;
    public bool IsRevoked => state.IsRevoked;

    public WindowCaptureAuthorization(
        WindowCaptureSource source,
        CaptureDestination destination,
        WindowCaptureBinding binding,
        PerceptionOptions options,
        DateTimeOffset expiresAtUtc,
        bool selectedWindowCaptureRequested = false,
        bool continuousCaptureRequested = false,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(options);
        binding.Validate();
        destination.Validate();
        options.Validate();
        this.source = source;
        this.destination = destination;
        Binding = binding;
        Options = options;
        SelectedWindowCaptureRequested = selectedWindowCaptureRequested;
        ContinuousCaptureRequested = continuousCaptureRequested;
        state = new(expiresAtUtc, PerceptionOptions.HardMaximumSessionLifetime, timeProvider);
    }

    public void Revoke() => state.Revoke();

    internal void ValidateFor(
        WindowCaptureSource candidateSource,
        CaptureDestination candidateDestination,
        Guid sessionId,
        Guid configurationRevision,
        Guid authorizationRevision,
        PerceptionOptions options,
        TimeProvider clock)
    {
        PerceptionGuard.Require(SelectedWindowCaptureRequested,
            PerceptionFailureCode.AuthorizationRequired);
        PerceptionGuard.Require(Binding.Mode != WindowCaptureMode.Continuous ||
            ContinuousCaptureRequested, PerceptionFailureCode.AuthorizationRequired);
        PerceptionGuard.Require(!state.IsConsumed,
            PerceptionFailureCode.AuthorizationConsumed);
        PerceptionGuard.Require(ReferenceEquals(source, candidateSource) &&
            ReferenceEquals(destination, candidateDestination) &&
            Binding.SessionId == sessionId &&
            Binding.SourceId == candidateSource.Id &&
            Binding.SourceEnumerationRevision == candidateSource.EnumerationRevision &&
            Binding.DestinationId == candidateDestination.Id &&
            Binding.DestinationRevision == candidateDestination.Revision &&
            Binding.ConfigurationRevision == configurationRevision &&
            Binding.AuthorizationRevision == authorizationRevision &&
            Binding.SessionLifetime <= options.SessionLifetime &&
            Options == options, PerceptionFailureCode.InvalidBinding);
        state.ValidateClock(clock);
        state.Check();
    }

    internal void Check() => state.Check();
    internal void Consume() => state.Consume();
    internal void CommitCopy(Span<byte> destination, AuthorizedPixelCopy copy) =>
        state.CommitCopy(destination, copy);

    public override string ToString() =>
        "One-use selected-window capture authorization (source and destination labels omitted)";
}

public sealed class WindowPreviewAuthorization
{
    private readonly OneUseAuthorizationState state;
    private readonly WindowFrameProvenance frame;
    internal OneUseAuthorizationState State => state;

    public bool LocalPreviewRequested { get; }
    public bool IsConsumed => state.IsConsumed;
    public bool IsRevoked => state.IsRevoked;

    public WindowPreviewAuthorization(
        WindowFrameProvenance frame,
        DateTimeOffset expiresAtUtc,
        bool localPreviewRequested = false,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        frame.Validate();
        this.frame = frame;
        LocalPreviewRequested = localPreviewRequested;
        state = new(expiresAtUtc, TimeSpan.FromSeconds(30), timeProvider);
    }

    public void Revoke() => state.Revoke();

    internal void ValidateFor(
        WindowFrameProvenance candidate,
        TimeProvider clock)
    {
        PerceptionGuard.Require(LocalPreviewRequested,
            PerceptionFailureCode.AuthorizationRequired);
        PerceptionGuard.Require(!state.IsConsumed,
            PerceptionFailureCode.AuthorizationConsumed);
        PerceptionGuard.Require(ReferenceEquals(frame, candidate),
            PerceptionFailureCode.InvalidBinding);
        state.ValidateClock(clock);
        state.Check();
    }

    internal void Consume() => state.Consume();

    public override string ToString() =>
        "One-use local window-preview authorization (pixels omitted)";
}

public sealed class WindowDisclosureAuthorization
{
    private readonly OneUseAuthorizationState state;
    private readonly WindowFrameProvenance frame;
    private readonly CaptureDestination destination;
    internal OneUseAuthorizationState State => state;

    public bool RemoteDisclosureRequested { get; }
    public bool IsConsumed => state.IsConsumed;
    public bool IsRevoked => state.IsRevoked;

    public WindowDisclosureAuthorization(
        WindowFrameProvenance frame,
        CaptureDestination destination,
        DateTimeOffset expiresAtUtc,
        bool remoteDisclosureRequested = false,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(destination);
        frame.Validate();
        destination.Validate();
        this.frame = frame;
        this.destination = destination;
        RemoteDisclosureRequested = remoteDisclosureRequested;
        state = new(expiresAtUtc, TimeSpan.FromSeconds(30), timeProvider);
    }

    public void Revoke() => state.Revoke();

    internal void ValidateFor(
        WindowFrameProvenance candidateFrame,
        CaptureDestination candidateDestination,
        TimeProvider clock)
    {
        PerceptionGuard.Require(RemoteDisclosureRequested,
            PerceptionFailureCode.AuthorizationRequired);
        PerceptionGuard.Require(candidateDestination.Kind == CaptureDestinationKind.RemoteProcessor,
            PerceptionFailureCode.InvalidBinding);
        PerceptionGuard.Require(!state.IsConsumed,
            PerceptionFailureCode.AuthorizationConsumed);
        PerceptionGuard.Require(ReferenceEquals(frame, candidateFrame) &&
            ReferenceEquals(destination, candidateDestination) &&
            candidateFrame.DestinationId == candidateDestination.Id &&
            candidateFrame.DestinationRevision == candidateDestination.Revision,
            PerceptionFailureCode.InvalidBinding);
        state.ValidateClock(clock);
        state.Check();
    }

    internal void Consume() => state.Consume();

    public override string ToString() =>
        "One-use remote window-frame disclosure authorization (pixels and destination label omitted)";
}
