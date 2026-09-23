namespace Martlet.F5;

public enum F5ConversationAuthorizationDecision
{
    No,
    Allow
}

public enum F5PreviewAuthorizationDecision
{
    No,
    Allow
}

public sealed class F5ConversationSynthesis
{
    private readonly F5TextChunk[] chunks;

    public F5ConversationSynthesis(
        F5RequestIds ids,
        string destinationId,
        F5WorkerIdentity expectedWorker,
        string expectedReferenceRevision,
        DateTimeOffset deadlineUtc,
        IEnumerable<F5TextChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(expectedWorker);
        ArgumentNullException.ThrowIfNull(chunks);
        ids.Validate();
        F5Guard.Identifier(destinationId, 128);
        F5Guard.Sha256(expectedReferenceRevision);
        F5Guard.Utc(deadlineUtc);
        var materialized = chunks.ToArray();
        ValidateChunks(materialized, F5WorkerProtocol.MaximumTextChunks,
            F5WorkerProtocol.MaximumTextUtf8Bytes);
        ActionId = Guid.NewGuid();
        Ids = ids;
        DestinationId = destinationId;
        ExpectedWorker = expectedWorker;
        ExpectedReferenceRevision = expectedReferenceRevision;
        DeadlineUtc = deadlineUtc;
        this.chunks = materialized;
        Chunks = Array.AsReadOnly(this.chunks);
    }

    public Guid ActionId { get; }
    public F5RequestIds Ids { get; }
    public string DestinationId { get; }
    public F5WorkerIdentity ExpectedWorker { get; }
    public string ExpectedReferenceRevision { get; }
    public DateTimeOffset DeadlineUtc { get; }
    public IReadOnlyList<F5TextChunk> Chunks { get; }

    public F5ConversationAuthorization Authorize(
        F5ConversationAuthorizationDecision decision,
        DateTimeOffset expiresAtUtc)
    {
        F5Guard.Defined(decision);
        F5Guard.Require(decision == F5ConversationAuthorizationDecision.Allow,
            F5Failure.AuthorizationRequired);
        F5Guard.Utc(expiresAtUtc);
        F5Guard.Require(expiresAtUtc <= DeadlineUtc, F5Failure.AuthorizationMismatch);
        return new(this, expiresAtUtc);
    }

    internal static void ValidateChunks(F5TextChunk[] chunks, int maximumChunks, int maximumBytes)
    {
        F5Guard.Require(chunks.Length is > 0 && chunks.Length <= maximumChunks,
            F5Failure.LimitExceeded);
        var bytes = 0;
        for (var index = 0; index < chunks.Length; index++)
        {
            F5Guard.Require(chunks[index].Index == index);
            bytes = checked(bytes + F5Guard.Utf8Text(chunks[index].Text,
                F5WorkerProtocol.MaximumChunkCharacters,
                F5WorkerProtocol.MaximumChunkUtf8Bytes));
        }
        F5Guard.Require(chunks.Select(chunk => chunk.ChunkId)
            .Distinct(StringComparer.Ordinal).Count() == chunks.Length);
        F5Guard.Require(bytes <= maximumBytes, F5Failure.LimitExceeded);
    }

    public override string ToString() =>
        $"F5 conversation synthesis {{ ActionId = {ActionId}, RequestId = {Ids.RequestId}, content = omitted }}";
}

public sealed class F5ConversationAuthorization
{
    internal F5ConversationAuthorization(F5ConversationSynthesis request, DateTimeOffset expiresAtUtc)
    {
        Request = request;
        ExpiresAtUtc = expiresAtUtc;
    }

    internal F5ConversationSynthesis Request { get; }
    internal int Used;
    public DateTimeOffset ExpiresAtUtc { get; }
    public override string ToString() => "One-use exact F5 conversation authorization";
}

public sealed class F5PreviewSynthesis
{
    private readonly F5TextChunk[] chunks;

    public F5PreviewSynthesis(
        F5RequestIds ids,
        string destinationId,
        F5WorkerIdentity expectedWorker,
        Guid presetId,
        string referenceRevision,
        DateTimeOffset deadlineUtc,
        string previewText)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(expectedWorker);
        ids.Validate();
        F5Guard.Identifier(destinationId, 128);
        F5Guard.Require(presetId != Guid.Empty);
        F5Guard.Sha256(referenceRevision);
        F5Guard.Utc(deadlineUtc);
        F5Guard.Utf8Text(previewText, 280, 512);
        ActionId = Guid.NewGuid();
        Ids = ids;
        DestinationId = destinationId;
        ExpectedWorker = expectedWorker;
        PresetId = presetId;
        ReferenceRevision = referenceRevision;
        DeadlineUtc = deadlineUtc;
        chunks = [new F5TextChunk(0, "preview", previewText)];
        Chunks = Array.AsReadOnly(chunks);
    }

    public Guid ActionId { get; }
    public F5RequestIds Ids { get; }
    public string DestinationId { get; }
    public F5WorkerIdentity ExpectedWorker { get; }
    public Guid PresetId { get; }
    public string ReferenceRevision { get; }
    public DateTimeOffset DeadlineUtc { get; }
    public IReadOnlyList<F5TextChunk> Chunks { get; }

    public F5PreviewAuthorization Authorize(
        F5PreviewAuthorizationDecision decision,
        DateTimeOffset expiresAtUtc)
    {
        F5Guard.Defined(decision);
        F5Guard.Require(decision == F5PreviewAuthorizationDecision.Allow,
            F5Failure.AuthorizationRequired);
        F5Guard.Utc(expiresAtUtc);
        F5Guard.Require(expiresAtUtc <= DeadlineUtc, F5Failure.AuthorizationMismatch);
        return new(this, expiresAtUtc);
    }

    public override string ToString() =>
        $"F5 preview synthesis {{ ActionId = {ActionId}, RequestId = {Ids.RequestId}, content = omitted }}";
}

public sealed class F5PreviewAuthorization
{
    internal F5PreviewAuthorization(F5PreviewSynthesis request, DateTimeOffset expiresAtUtc)
    {
        Request = request;
        ExpiresAtUtc = expiresAtUtc;
    }

    internal F5PreviewSynthesis Request { get; }
    internal int Used;
    public DateTimeOffset ExpiresAtUtc { get; }
    public override string ToString() => "One-use exact F5 preview authorization";
}

public interface IF5PcmFrameSink
{
    ValueTask WriteAsync(F5PcmFrame frame, CancellationToken cancellationToken);
}

public sealed class F5PcmSinkException : Exception
{
    public F5PcmSinkException(string message) : base(message) { }
    public F5PcmSinkException(string message, Exception innerException) : base(message, innerException) { }
}

public enum F5SynthesisOutcome
{
    Completed,
    Canceled,
    DeadlineExceeded,
    Failed
}

public sealed record F5SynthesisResult
{
    public required F5RequestIds Ids { get; init; }
    public required Guid ActionId { get; init; }
    public required F5SynthesisOutcome Outcome { get; init; }
    public required F5Failure? Failure { get; init; }
    public required string ReferenceRevision { get; init; }
    public required long DeliveredSamples { get; init; }
    public required int DeliveredFrames { get; init; }
    public required int DiscardedLateFrames { get; init; }
    public required bool LocalOutputDiscarded { get; init; }
    public required F5CancellationCapability? ComputeCancellation { get; init; }
    public required bool WorkerMayContinue { get; init; }
    public required F5WorkerError? WorkerError { get; init; }

    public override string ToString() =>
        $"F5 synthesis result {{ ActionId = {ActionId}, Outcome = {Outcome}, Failure = {Failure}, content = omitted }}";
}
