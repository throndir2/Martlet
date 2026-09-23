namespace Martlet.Perception;

public enum WindowCaptureMode
{
    SingleFrame,
    Continuous
}

public enum WindowFrameFormat
{
    Bgra32Premultiplied
}

public enum CaptureDestinationKind
{
    LocalOnly,
    RemoteProcessor
}

public enum WindowCaptureOutcome
{
    Captured,
    Canceled,
    Failed
}

public enum WindowCaptureOwnershipState
{
    Pending,
    Released,
    Quarantined
}

public sealed record SourceEnumerationBinding(
    Guid SessionId,
    Guid RequestId,
    Guid ConfigurationRevision,
    Guid AuthorizationRevision)
{
    public void Validate()
    {
        PerceptionGuard.Require(SessionId != Guid.Empty && RequestId != Guid.Empty &&
            ConfigurationRevision != Guid.Empty && AuthorizationRevision != Guid.Empty,
            PerceptionFailureCode.InvalidBinding);
    }
}

public sealed record WindowCaptureBinding(
    Guid SessionId,
    Guid CaptureId,
    long Epoch,
    Guid SourceId,
    Guid SourceEnumerationRevision,
    Guid DestinationId,
    Guid DestinationRevision,
    Guid ConfigurationRevision,
    Guid AuthorizationRevision,
    WindowCaptureMode Mode,
    TimeSpan SessionLifetime)
{
    public void Validate()
    {
        PerceptionGuard.Defined(Mode);
        PerceptionGuard.Require(SessionId != Guid.Empty && CaptureId != Guid.Empty &&
            Epoch is >= 0 and <= int.MaxValue && SourceId != Guid.Empty &&
            SourceEnumerationRevision != Guid.Empty && DestinationId != Guid.Empty &&
            DestinationRevision != Guid.Empty && ConfigurationRevision != Guid.Empty &&
            AuthorizationRevision != Guid.Empty &&
            SessionLifetime >= TimeSpan.FromSeconds(1) &&
            SessionLifetime <= PerceptionOptions.HardMaximumSessionLifetime,
            PerceptionFailureCode.InvalidBinding);
    }
}

public sealed record CaptureDestination(
    Guid Id,
    Guid Revision,
    CaptureDestinationKind Kind,
    string DisplayName)
{
    public void Validate()
    {
        PerceptionGuard.Defined(Kind);
        PerceptionGuard.Require(Id != Guid.Empty && Revision != Guid.Empty,
            PerceptionFailureCode.InvalidBinding);
        PerceptionGuard.SafeLabel(DisplayName, 256);
    }

    public override string ToString() =>
        $"Capture destination {{ Id = {Id}, Revision = {Revision}, Kind = {Kind}, DisplayName = [redacted] }}";
}

public sealed class WindowCaptureSource
{
    private readonly Guid ownerId;
    internal NativeWindowSource NativeSource { get; }
    internal long Generation { get; }
    internal Guid AuthorizationRevision { get; }

    public Guid Id { get; }
    public Guid EnumerationRevision { get; }
    public string ApplicationName { get; }
    public string WindowTitle { get; }

    internal WindowCaptureSource(
        Guid ownerId,
        long generation,
        Guid enumerationRevision,
        Guid authorizationRevision,
        NativeWindowSource nativeSource)
    {
        this.ownerId = ownerId;
        Generation = generation;
        EnumerationRevision = enumerationRevision;
        AuthorizationRevision = authorizationRevision;
        NativeSource = nativeSource;
        Id = Guid.NewGuid();
        ApplicationName = nativeSource.ApplicationName;
        WindowTitle = nativeSource.WindowTitle;
    }

    internal bool BelongsTo(Guid expectedOwner, long expectedGeneration) =>
        ownerId == expectedOwner && Generation == expectedGeneration;

    public override string ToString() =>
        $"Selected window source {{ Id = {Id}, EnumerationRevision = {EnumerationRevision}, Labels = [redacted] }}";
}

public sealed record WindowFrameProvenance
{
    public required Guid SessionId { get; init; }
    public required Guid CaptureId { get; init; }
    public required long CaptureEpoch { get; init; }
    public required long FrameSequence { get; init; }
    public required Guid SourceId { get; init; }
    public required Guid SourceEnumerationRevision { get; init; }
    public required Guid DestinationId { get; init; }
    public required Guid DestinationRevision { get; init; }
    public required CaptureDestinationKind DestinationKind { get; init; }
    public required Guid ConfigurationRevision { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public required DateTimeOffset FreshUntilUtc { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int ByteCount { get; init; }
    public WindowFrameFormat Format { get; init; } = WindowFrameFormat.Bgra32Premultiplied;

    internal void Validate()
    {
        PerceptionGuard.Defined(DestinationKind);
        PerceptionGuard.Defined(Format);
        PerceptionGuard.Require(SessionId != Guid.Empty && CaptureId != Guid.Empty &&
            CaptureEpoch is >= 0 and <= int.MaxValue && FrameSequence > 0 &&
            SourceId != Guid.Empty && SourceEnumerationRevision != Guid.Empty &&
            DestinationId != Guid.Empty && DestinationRevision != Guid.Empty &&
            ConfigurationRevision != Guid.Empty && Width > 0 && Height > 0 &&
            ByteCount > 0 && FreshUntilUtc > CapturedAtUtc,
            PerceptionFailureCode.InvalidBinding);
        PerceptionGuard.Utc(CapturedAtUtc);
        PerceptionGuard.Utc(FreshUntilUtc);
    }

    public override string ToString() =>
        $"Window frame provenance {{ CaptureId = {CaptureId}, FrameSequence = {FrameSequence}, " +
        $"SourceId = {SourceId}, DestinationId = {DestinationId}, CapturedAtUtc = {CapturedAtUtc:O}, " +
        $"FreshUntilUtc = {FreshUntilUtc:O}, Width = {Width}, Height = {Height}, ByteCount = {ByteCount} }}";
}

public sealed record WindowCaptureCompletion(
    WindowCaptureOutcome Outcome,
    WindowCaptureOwnershipState Ownership,
    PerceptionFailure? Failure);

public sealed record WindowCaptureOwnershipRelease(
    bool Released,
    PerceptionFailure? Failure);

public sealed record WindowCaptureSnapshot(
    bool IsTerminal,
    WindowCaptureOutcome? Outcome,
    WindowCaptureOwnershipState Ownership,
    PerceptionFailure? Failure,
    bool HasFreshFrame,
    int RetainedFrameBytes,
    long AcceptedFrames,
    long DroppedFrames);

public sealed class WindowsCaptureAvailability
{
    public bool Eligible => false;
    public PerceptionFailure Failure { get; } =
        new(PerceptionFailureCode.NativePrivacyUnqualified);

    internal WindowsCaptureAvailability()
    {
    }
}

internal static class WindowsCaptureEligibility
{
    internal static WindowsCaptureAvailability Production { get; } = new();
}
