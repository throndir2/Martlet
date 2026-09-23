namespace Martlet.Perception;

public sealed record PerceptionJobIds
{
    public required Guid SessionId { get; init; }
    public required Guid TurnId { get; init; }
    public required Guid RequestId { get; init; }
    public Guid? ParentRequestId { get; init; }

    internal void Validate()
    {
        PerceptionWorkerGuard.Require(SessionId != Guid.Empty &&
            TurnId != Guid.Empty &&
            RequestId != Guid.Empty);
        PerceptionWorkerGuard.Require(ParentRequestId is null ||
            ParentRequestId.Value != Guid.Empty);
    }
}

public sealed class PerceptionTaskDefinition
{
    private PerceptionTaskDefinition(PerceptionRole role, string? question)
    {
        Role = role;
        Question = question;
        Validate();
    }

    public PerceptionRole Role { get; }
    public string? Question { get; }

    public static PerceptionTaskDefinition ReadVisibleText() =>
        new(PerceptionRole.Ocr, null);

    public static PerceptionTaskDefinition AskAboutSelectedWindow(string question) =>
        new(PerceptionRole.VisualQuestionAnswering, question);

    internal void Validate()
    {
        PerceptionWorkerGuard.Defined(Role);
        if (Role == PerceptionRole.Ocr)
        {
            PerceptionWorkerGuard.Require(Question is null);
            return;
        }

        PerceptionWorkerGuard.Utf8Text(
            Question,
            PerceptionProtocol.MaximumQuestionCharacters,
            PerceptionProtocol.MaximumQuestionUtf8Bytes,
            allowNewLines: false);
    }

    public override string ToString() =>
        $"Perception task {{ Role = {Role}, question = omitted }}";
}

public enum PerceptionVisionAuthorizationDecision
{
    No,
    Allow
}

public sealed class PerceptionJobIntent
{
    public PerceptionJobIntent(
        PerceptionJobIds ids,
        string destinationId,
        PerceptionWorkerIdentity expectedWorker,
        SelectedWindowFrame frame,
        PerceptionTaskDefinition task,
        DateTimeOffset createdAtUtc,
        DateTimeOffset deadlineUtc,
        TimeSpan maximumFrameAge)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(expectedWorker);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(task);
        ids.Validate();
        PerceptionWorkerGuard.Identifier(destinationId, 128);
        task.Validate();
        PerceptionWorkerGuard.Require(expectedWorker.Role == task.Role,
            PerceptionWorkerFailure.RoleMismatch);
        PerceptionWorkerGuard.Utc(createdAtUtc);
        PerceptionWorkerGuard.Utc(deadlineUtc);
        PerceptionWorkerGuard.Require(frame.CapturedAtUtc <= createdAtUtc);
        PerceptionWorkerGuard.Require(deadlineUtc > createdAtUtc &&
            deadlineUtc - createdAtUtc <= PerceptionProtocol.MaximumJobDuration,
            PerceptionWorkerFailure.DeadlineExceeded);
        PerceptionWorkerGuard.Require(maximumFrameAge > TimeSpan.Zero &&
            maximumFrameAge <= PerceptionProtocol.MaximumFrameAge);
        PerceptionWorkerGuard.Require(frame.Content.ByteCount <=
            expectedWorker.Limits.MaximumInputBytes &&
            frame.Content.Width <= expectedWorker.Limits.MaximumWidth &&
            frame.Content.Height <= expectedWorker.Limits.MaximumHeight &&
            checked((long)frame.Content.Width * frame.Content.Height) <=
                expectedWorker.Limits.MaximumPixels,
            PerceptionWorkerFailure.LimitExceeded);
        if (frame.Content.Kind == PerceptionFrameContentKind.EphemeralGatewayReference)
        {
            PerceptionWorkerGuard.Require(frame.Content.ReferenceExpiresAtUtc > createdAtUtc,
                PerceptionWorkerFailure.InvalidFrameReference);
        }

        ActionId = Guid.NewGuid();
        Ids = ids;
        DestinationId = destinationId;
        ExpectedWorker = expectedWorker;
        Frame = frame;
        Task = task;
        CreatedAtUtc = createdAtUtc;
        DeadlineUtc = deadlineUtc;
        MaximumFrameAge = maximumFrameAge;
    }

    public Guid ActionId { get; }
    public PerceptionJobIds Ids { get; }
    public string DestinationId { get; }
    public PerceptionWorkerIdentity ExpectedWorker { get; }
    public SelectedWindowFrame Frame { get; }
    public PerceptionTaskDefinition Task { get; }
    public long Epoch => Frame.CaptureEpoch;
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset DeadlineUtc { get; }
    public TimeSpan MaximumFrameAge { get; }

    public PerceptionVisionAuthorization Authorize(
        PerceptionVisionAuthorizationDecision decision,
        Guid permissionId,
        DateTimeOffset expiresAtUtc)
    {
        PerceptionWorkerGuard.Defined(decision);
        PerceptionWorkerGuard.Require(decision == PerceptionVisionAuthorizationDecision.Allow,
            PerceptionWorkerFailure.PermissionRequired);
        PerceptionWorkerGuard.Require(permissionId != Guid.Empty);
        PerceptionWorkerGuard.Utc(expiresAtUtc);
        PerceptionWorkerGuard.Require(expiresAtUtc <= DeadlineUtc,
            PerceptionWorkerFailure.PermissionMismatch);
        return new(this, permissionId, expiresAtUtc);
    }

    public override string ToString() =>
        $"Perception job intent {{ ActionId = {ActionId}, RequestId = {Ids.RequestId}, Role = {Task.Role}, Epoch = {Epoch}, image/question = omitted }}";
}

public sealed class PerceptionVisionAuthorization
{
    internal PerceptionVisionAuthorization(
        PerceptionJobIntent request,
        Guid permissionId,
        DateTimeOffset expiresAtUtc)
    {
        Request = request;
        PermissionId = permissionId;
        ExpiresAtUtc = expiresAtUtc;
    }

    internal PerceptionJobIntent Request { get; }
    private int state;
    public Guid PermissionId { get; }
    public string DestinationId => Request.DestinationId;
    public Guid SelectionId => Request.Frame.Source.SelectionId;
    public Guid FrameId => Request.Frame.FrameId;
    public string FrameSha256 => Request.Frame.Content.Sha256;
    public long Epoch => Request.Epoch;
    public PerceptionRole Role => Request.Task.Role;
    public string WorkerId => Request.ExpectedWorker.WorkerId;
    public string ModelRevision => Request.ExpectedWorker.Model.ModelRevision;
    public DateTimeOffset ExpiresAtUtc { get; }

    internal bool IsAvailable => Volatile.Read(ref state) == 0;

    internal bool TryReserve() =>
        Interlocked.CompareExchange(ref state, 1, 0) == 0;

    internal bool TryConsume()
    {
        while (true)
        {
            var current = Volatile.Read(ref state);
            if (current is 1 or 2)
                return false;
            if (Interlocked.CompareExchange(ref state, 2, current) == current)
                return true;
        }
    }

    internal bool TryOpenReservation() =>
        Interlocked.CompareExchange(ref state, 3, 1) == 1;

    internal void FinalizeReservation()
    {
        Interlocked.CompareExchange(ref state, 2, 1);
        Interlocked.CompareExchange(ref state, 2, 3);
    }

    public override string ToString() =>
        $"One-use perception vision authorization {{ PermissionId = {PermissionId}, DestinationId = {DestinationId}, FrameId = {FrameId}, content = omitted }}";
}

public sealed class PerceptionExecutionPolicy
{
    internal static PerceptionExecutionPolicy Strict { get; } = new();
    private PerceptionExecutionPolicy() { }

    public bool SelectedWindowOnly => true;
    public bool ScreenWideFallbackAllowed => false;
    public bool ArtifactDownloadAllowed => false;
    public bool ModelFallbackAllowed => false;
    public bool ProviderFallbackAllowed => false;
    public bool RedirectAllowed => false;
    public bool PersistentImageAllowed => false;
    public bool ArbitraryEndpointAllowed => false;
    public bool ArbitraryPathAllowed => false;

    public override string ToString() =>
        "Strict selected-window, preprovisioned, no-fallback perception policy";
}

public sealed class PerceptionWorkerRequest
{
    internal PerceptionWorkerRequest(PerceptionJobIntent intent)
    {
        ActionId = intent.ActionId;
        Ids = intent.Ids;
        DestinationId = intent.DestinationId;
        ExpectedWorker = intent.ExpectedWorker;
        Frame = intent.Frame.SnapshotForDispatch();
        Task = intent.Task;
        Epoch = intent.Epoch;
        DeadlineUtc = intent.DeadlineUtc;
        MaximumFrameAge = intent.MaximumFrameAge;
        Policy = PerceptionExecutionPolicy.Strict;
        Validate();
    }

    public PerceptionProtocolVersion ProtocolVersion => PerceptionProtocolVersion.Current;
    public string ContractId => PerceptionProtocol.ContractId;
    public Guid ActionId { get; }
    public PerceptionJobIds Ids { get; }
    public string DestinationId { get; }
    public PerceptionWorkerIdentity ExpectedWorker { get; }
    public SelectedWindowFrame Frame { get; }
    public PerceptionTaskDefinition Task { get; }
    public long Epoch { get; }
    public DateTimeOffset DeadlineUtc { get; }
    public TimeSpan MaximumFrameAge { get; }
    public PerceptionExecutionPolicy Policy { get; }

    internal void Validate()
    {
        ProtocolVersion.Validate();
        PerceptionWorkerGuard.Require(ContractId == PerceptionProtocol.ContractId);
        PerceptionWorkerGuard.Require(ActionId != Guid.Empty);
        Ids.Validate();
        PerceptionWorkerGuard.Identifier(DestinationId, 128);
        PerceptionWorkerGuard.Require(ExpectedWorker.Role == Task.Role,
            PerceptionWorkerFailure.RoleMismatch);
        PerceptionWorkerGuard.Require(Epoch == Frame.CaptureEpoch);
        PerceptionWorkerGuard.Utc(DeadlineUtc);
        PerceptionWorkerGuard.Require(MaximumFrameAge > TimeSpan.Zero &&
            MaximumFrameAge <= PerceptionProtocol.MaximumFrameAge);
        Frame.Source.Validate();
        Frame.Content.Validate();
        Task.Validate();
    }

    public override string ToString() =>
        $"Perception worker request {{ ActionId = {ActionId}, RequestId = {Ids.RequestId}, Role = {Task.Role}, Epoch = {Epoch}, image/question = omitted }}";
}

public enum PerceptionConfidenceKind
{
    Unavailable,
    Uncalibrated,
    Calibrated
}

public sealed record PerceptionConfidence
{
    public required PerceptionConfidenceKind Kind { get; init; }
    public double? Value { get; init; }

    public static PerceptionConfidence Unavailable() =>
        new() { Kind = PerceptionConfidenceKind.Unavailable };

    public static PerceptionConfidence Uncalibrated(double value) =>
        new() { Kind = PerceptionConfidenceKind.Uncalibrated, Value = value };

    public static PerceptionConfidence Calibrated(double value) =>
        new() { Kind = PerceptionConfidenceKind.Calibrated, Value = value };

    internal void Validate()
    {
        PerceptionWorkerGuard.Require(
            Enum.IsDefined(Kind) &&
            (Kind == PerceptionConfidenceKind.Unavailable
            ? Value is null
            : Value is >= 0 and <= 1 && double.IsFinite(Value.Value)),
            PerceptionWorkerFailure.UnknownOutput);
    }
}

public sealed record PerceptionBoundingBox
{
    public required int Left { get; init; }
    public required int Top { get; init; }
    public required int Right { get; init; }
    public required int Bottom { get; init; }

    internal void Validate() =>
        PerceptionWorkerGuard.Require(
            Left is >= 0 and <= 10_000 &&
            Top is >= 0 and <= 10_000 &&
            Right is >= 0 and <= 10_000 &&
            Bottom is >= 0 and <= 10_000 &&
            Left < Right &&
            Top < Bottom,
            PerceptionWorkerFailure.UnknownOutput);
}

public sealed record PerceptionTextRegion
{
    public override string ToString() => "Perception text region { content = omitted }";

    public required int Index { get; init; }
    public required string Text { get; init; }
    public required PerceptionBoundingBox Bounds { get; init; }
    public required PerceptionConfidence Confidence { get; init; }

    internal int ValidateAndCountBytes(int expectedIndex)
    {
        PerceptionWorkerGuard.Require(Index == expectedIndex, PerceptionWorkerFailure.UnknownOutput);
        var bytes = PerceptionWorkerGuard.Utf8Text(
            Text,
            PerceptionProtocol.MaximumRegionTextCharacters,
            PerceptionProtocol.MaximumRegionTextUtf8Bytes);
        ArgumentNullException.ThrowIfNull(Bounds);
        ArgumentNullException.ThrowIfNull(Confidence);
        Bounds.Validate();
        Confidence.Validate();
        return bytes;
    }
}

public sealed record PerceptionOcrOutput
{
    public override string ToString() => "Perception OCR output { content = omitted }";

    public required IReadOnlyList<PerceptionTextRegion> Regions { get; init; }
    public string? DetectedLanguage { get; init; }

    internal PerceptionOcrOutput ValidateAndClone(int maximumOutputBytes)
    {
        var regions = Regions ??
            throw new PerceptionWorkerException(PerceptionWorkerFailure.UnknownOutput);
        var count = regions.Count;
        PerceptionWorkerGuard.Require(
            count is >= 0 and <= PerceptionProtocol.MaximumOcrRegions,
            PerceptionWorkerFailure.UnknownOutput);
        var copy = new PerceptionTextRegion[count];
        var bytes = 0;
        for (var index = 0; index < count; index++)
        {
            var region = regions[index];
            ArgumentNullException.ThrowIfNull(region);
            bytes = checked(bytes + region.ValidateAndCountBytes(index));
            copy[index] = region with
            {
                Bounds = region.Bounds with { },
                Confidence = region.Confidence with { }
            };
        }
        PerceptionWorkerGuard.Require(regions.Count == count,
            PerceptionWorkerFailure.UnknownOutput);
        if (DetectedLanguage is not null)
        {
            PerceptionWorkerGuard.Identifier(DetectedLanguage, 16);
            bytes = checked(bytes + DetectedLanguage.Length);
        }
        PerceptionWorkerGuard.Require(bytes <= maximumOutputBytes &&
            bytes <= PerceptionProtocol.MaximumOutputUtf8Bytes,
            PerceptionWorkerFailure.LimitExceeded);
        return new()
        {
            Regions = Array.AsReadOnly(copy),
            DetectedLanguage = DetectedLanguage
        };
    }
}

public sealed record PerceptionVlmOutput
{
    public override string ToString() => "Perception VLM output { content = omitted }";

    public required string Answer { get; init; }
    public required string Uncertainty { get; init; }
    public required PerceptionConfidence Confidence { get; init; }

    internal PerceptionVlmOutput ValidateAndClone(int maximumOutputBytes)
    {
        var bytes = PerceptionWorkerGuard.Utf8Text(Answer, 4096, 8192);
        bytes = checked(bytes + PerceptionWorkerGuard.Utf8Text(Uncertainty, 512, 1024));
        PerceptionWorkerGuard.Require(bytes <= maximumOutputBytes &&
            bytes <= PerceptionProtocol.MaximumOutputUtf8Bytes,
            PerceptionWorkerFailure.LimitExceeded);
        ArgumentNullException.ThrowIfNull(Confidence);
        Confidence.Validate();
        return this with { Confidence = Confidence with { } };
    }
}

public sealed record PerceptionObservationProvenance
{
    public required Guid FrameId { get; init; }
    public required long CaptureEpoch { get; init; }
    public required Guid SelectionId { get; init; }
    public required string SourceRevision { get; init; }
    public required string CapturePermissionRevision { get; init; }
    public required string FrameSha256 { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public required DateTimeOffset ProcessedAtUtc { get; init; }
    public required string DestinationId { get; init; }
    public required string HostId { get; init; }
    public required string WorkerId { get; init; }
    public required PerceptionEvidenceKind Evidence { get; init; }
    public required PerceptionRole Role { get; init; }
    public required string ModelId { get; init; }
    public required string ModelRevision { get; init; }
    public required string ModelSha256 { get; init; }
}

public sealed record PerceptionObservation
{
    public required PerceptionRole Role { get; init; }
    public required PerceptionObservationProvenance Provenance { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public PerceptionOcrOutput? Ocr { get; init; }
    public PerceptionVlmOutput? Vlm { get; init; }
}

public enum PerceptionWorkerOutcome
{
    Completed,
    Canceled,
    Failed
}

public enum PerceptionWorkerErrorCode
{
    InvalidRequest,
    InvalidImage,
    ModelNotReady,
    ResourceExhausted,
    DeadlineExceeded,
    Canceled,
    InternalFailure
}

public sealed record PerceptionWorkerError
{
    public override string ToString() =>
        $"Perception worker error {{ Code = {Code}, content = omitted }}";

    public required PerceptionWorkerErrorCode Code { get; init; }
    public required string Summary { get; init; }
    public required string RemedyCode { get; init; }

    internal void Validate()
    {
        PerceptionWorkerGuard.Require(Enum.IsDefined(Code),
            PerceptionWorkerFailure.UnknownOutput);
        PerceptionWorkerGuard.Utf8Text(Summary, 256, 512, allowNewLines: false);
        PerceptionWorkerGuard.Identifier(RemedyCode, 96);
    }
}

public sealed record PerceptionWorkerResponse
{
    public required PerceptionJobIds Ids { get; init; }
    public required Guid ActionId { get; init; }
    public required long Epoch { get; init; }
    public required PerceptionWorkerIdentity Worker { get; init; }
    public required PerceptionWorkerOutcome Outcome { get; init; }
    public PerceptionObservation? Observation { get; init; }
    public PerceptionWorkerError? Error { get; init; }
    public PerceptionCancellationCapability? ComputeCancellation { get; init; }
    public required bool WorkerMayContinue { get; init; }
}

public sealed record PerceptionCancelRequest
{
    public required PerceptionJobIds Ids { get; init; }
    public required Guid ActionId { get; init; }
    public required long Epoch { get; init; }
}

public sealed record PerceptionCancelResponse
{
    public required PerceptionJobIds Ids { get; init; }
    public required Guid ActionId { get; init; }
    public required long Epoch { get; init; }
    public required bool LocalDiscardAcknowledged { get; init; }
    public required PerceptionCancellationCapability ComputeCancellation { get; init; }
    public required bool WorkerMayContinue { get; init; }
}

internal sealed class PerceptionJobClock(TimeProvider clock)
{
    private readonly DateTimeOffset admittedAt = clock.GetUtcNow();
    private readonly long admittedTimestamp = clock.GetTimestamp();

    internal DateTimeOffset GetUtcNow()
    {
        var utc = clock.GetUtcNow();
        PerceptionWorkerGuard.Utc(utc);
        var elapsed = clock.GetElapsedTime(admittedTimestamp);
        PerceptionWorkerGuard.Require(elapsed >= TimeSpan.Zero,
            PerceptionWorkerFailure.ClockSkew);
        var monotonicUtc = admittedAt + elapsed;
        return utc > monotonicUtc ? utc : monotonicUtc;
    }
}
