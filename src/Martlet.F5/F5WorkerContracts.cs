namespace Martlet.F5;

public static class F5WorkerProtocol
{
    public const string ContractId = "martlet.f5.worker";
    public const int MaximumTextChunks = 16;
    public const int MaximumChunkCharacters = 2048;
    public const int MaximumChunkUtf8Bytes = 4096;
    public const int MaximumTextUtf8Bytes = 16 * 1024;
    public const int MaximumFrameSamples = 4_800;
    public const int MaximumFrameBytes = MaximumFrameSamples * 2;
    public const int MaximumEvents = 16_384;
    public const long MaximumSamples = 24_000L * 90;
    public static readonly TimeSpan MaximumRequestDuration = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan MaximumCancelDuration = TimeSpan.FromSeconds(2);
}

public sealed record F5RequestIds
{
    public required Guid SessionId { get; init; }
    public required Guid TurnId { get; init; }
    public required Guid RequestId { get; init; }
    public Guid? ParentRequestId { get; init; }

    internal void Validate()
    {
        F5Guard.Require(SessionId != Guid.Empty && TurnId != Guid.Empty && RequestId != Guid.Empty);
        F5Guard.Require(ParentRequestId is null || ParentRequestId != Guid.Empty);
    }
}

public sealed class F5TextChunk
{
    public F5TextChunk(int index, string chunkId, string text)
    {
        F5Guard.Require(index >= 0);
        F5Guard.Identifier(chunkId);
        F5Guard.Utf8Text(text, F5WorkerProtocol.MaximumChunkCharacters,
            F5WorkerProtocol.MaximumChunkUtf8Bytes);
        Index = index;
        ChunkId = chunkId;
        Text = text;
    }

    public int Index { get; }
    public string ChunkId { get; }
    public string Text { get; }
    public override string ToString() => $"F5 text chunk {Index} (text omitted)";
}

public enum F5PcmEncoding
{
    Signed16LittleEndian
}

public sealed record F5PcmFormat
{
    public static F5PcmFormat Transport { get; } = new()
    {
        SampleRate = 24_000,
        Channels = 1,
        BitsPerSample = 16,
        Encoding = F5PcmEncoding.Signed16LittleEndian
    };

    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public required int BitsPerSample { get; init; }
    public required F5PcmEncoding Encoding { get; init; }

    internal void ValidateTransport() =>
        F5Guard.Require(this == Transport, F5Failure.InvalidFrame);
}

public sealed class F5ExecutionPolicy
{
    internal static F5ExecutionPolicy Strict { get; } = new();
    private F5ExecutionPolicy() { }

    public bool ReferenceTranscriptSupplied => true;
    public bool AutomaticTranscriptionAllowed => false;
    public bool ArtifactDownloadAllowed => false;
    public bool DefaultVoiceAllowed => false;
    public bool ModelFallbackAllowed => false;
    public bool ProviderFallbackAllowed => false;
    public override string ToString() => "Strict preprovisioned F5 execution policy";
}

public sealed class F5ReferenceInput
{
    private readonly ReadOnlyMemory<byte> audio;

    internal F5ReferenceInput(
        Guid presetId,
        string referenceRevision,
        string audioSha256,
        string transcript,
        string transcriptRevision,
        F5ReferenceAudioFormat sourceFormat,
        ReadOnlyMemory<byte> audio)
    {
        PresetId = presetId;
        ReferenceRevision = referenceRevision;
        AudioSha256 = audioSha256;
        Transcript = transcript;
        TranscriptRevision = transcriptRevision;
        SourceFormat = sourceFormat;
        this.audio = audio;
    }

    public Guid PresetId { get; }
    public string ReferenceRevision { get; }
    public string AudioSha256 { get; }
    public string Transcript { get; }
    public string TranscriptRevision { get; }
    public F5ReferenceAudioFormat SourceFormat { get; }
    public ReadOnlyMemory<byte> Audio => audio;
    public override string ToString() =>
        $"F5 reference input {{ PresetId = {PresetId}, ReferenceRevision = {ReferenceRevision}, content = omitted }}";
}

public sealed class F5SynthesisRequest
{
    private readonly F5TextChunk[] chunks;

    internal F5SynthesisRequest(
        Guid actionId,
        F5RequestIds ids,
        string destinationId,
        F5WorkerIdentity expectedWorker,
        DateTimeOffset deadlineUtc,
        F5ReferenceInput reference,
        IEnumerable<F5TextChunk> chunks)
    {
        ActionId = actionId;
        Ids = ids;
        DestinationId = destinationId;
        ExpectedWorker = expectedWorker;
        DeadlineUtc = deadlineUtc;
        Reference = reference;
        this.chunks = chunks.ToArray();
        Chunks = Array.AsReadOnly(this.chunks);
        Policy = F5ExecutionPolicy.Strict;
        OutputFormat = F5PcmFormat.Transport;
        Validate();
    }

    public F5ProtocolVersion ProtocolVersion => F5ProtocolVersion.Current;
    public string ContractId => F5WorkerProtocol.ContractId;
    public Guid ActionId { get; }
    public F5RequestIds Ids { get; }
    public string DestinationId { get; }
    public F5WorkerIdentity ExpectedWorker { get; }
    public DateTimeOffset DeadlineUtc { get; }
    public F5ReferenceInput Reference { get; }
    public IReadOnlyList<F5TextChunk> Chunks { get; }
    public F5ExecutionPolicy Policy { get; }
    public F5PcmFormat OutputFormat { get; }

    internal void Validate()
    {
        F5Guard.Require(ActionId != Guid.Empty);
        Ids.Validate();
        F5Guard.Identifier(DestinationId, 128);
        ArgumentNullException.ThrowIfNull(ExpectedWorker);
        F5Guard.Utc(DeadlineUtc);
        F5Guard.Require(Reference.PresetId != Guid.Empty);
        F5Guard.Sha256(Reference.ReferenceRevision);
        F5Guard.Sha256(Reference.AudioSha256);
        F5Guard.Sha256(Reference.TranscriptRevision);
        F5Guard.Utf8Text(Reference.Transcript, F5ReferenceLimits.MaximumTranscriptCharacters,
            F5ReferenceLimits.MaximumTranscriptUtf8Bytes);
        F5Guard.Require(Reference.Audio.Length is > 0 and <= F5ReferenceLimits.MaximumAudioFileBytes);
        Reference.SourceFormat.Validate();
        OutputFormat.ValidateTransport();
        F5Guard.Require(chunks.Length is > 0 and <= F5WorkerProtocol.MaximumTextChunks,
            F5Failure.LimitExceeded);
        var bytes = 0;
        for (var index = 0; index < chunks.Length; index++)
        {
            F5Guard.Require(chunks[index].Index == index);
            bytes = checked(bytes + F5Guard.Utf8Text(chunks[index].Text,
                F5WorkerProtocol.MaximumChunkCharacters, F5WorkerProtocol.MaximumChunkUtf8Bytes));
        }
        F5Guard.Require(chunks.Select(chunk => chunk.ChunkId).Distinct(StringComparer.Ordinal).Count() ==
            chunks.Length);
        F5Guard.Require(bytes <= F5WorkerProtocol.MaximumTextUtf8Bytes, F5Failure.LimitExceeded);
    }

    public override string ToString() =>
        $"F5 synthesis request {{ ActionId = {ActionId}, RequestId = {Ids.RequestId}, content = omitted }}";
}

public sealed class F5PcmFrame
{
    private readonly byte[] data;

    public F5PcmFrame(long sequence, int chunkIndex, long sampleOffset, int sampleCount,
        F5PcmFormat format, ReadOnlySpan<byte> data)
    {
        Sequence = sequence;
        ChunkIndex = chunkIndex;
        SampleOffset = sampleOffset;
        SampleCount = sampleCount;
        Format = format;
        this.data = data.ToArray();
        Validate();
    }

    public long Sequence { get; }
    public int ChunkIndex { get; }
    public long SampleOffset { get; }
    public int SampleCount { get; }
    public F5PcmFormat Format { get; }
    public ReadOnlyMemory<byte> Data => data;

    internal void Validate()
    {
        F5Guard.Require(Sequence >= 0 && ChunkIndex >= 0 && SampleOffset >= 0 &&
            SampleCount is > 0 and <= F5WorkerProtocol.MaximumFrameSamples,
            F5Failure.InvalidFrame);
        Format.ValidateTransport();
        F5Guard.Require(data.Length == checked(SampleCount * Format.Channels * (Format.BitsPerSample / 8)) &&
            data.Length <= F5WorkerProtocol.MaximumFrameBytes, F5Failure.InvalidFrame);
    }

    public override string ToString() =>
        $"F5 PCM frame {{ Sequence = {Sequence}, SampleOffset = {SampleOffset}, SampleCount = {SampleCount}, data = omitted }}";
}

public enum F5WorkerEventKind
{
    Started,
    AudioFrame,
    ChunkCompleted,
    Completed,
    Canceled,
    Failed
}

public enum F5WorkerErrorCode
{
    InvalidRequest,
    UnsupportedVersion,
    IdentityMismatch,
    ReferenceRejected,
    Busy,
    ModelNotReady,
    DeadlineExceeded,
    GpuOutOfMemory,
    InternalFailure
}

public sealed record F5WorkerError
{
    public required F5WorkerErrorCode Code { get; init; }
    public required string Stage { get; init; }
    public required bool Retryable { get; init; }
    public required string Summary { get; init; }
    public required string ActionId { get; init; }

    internal void Validate()
    {
        F5Guard.Defined(Code);
        F5Guard.Identifier(Stage);
        F5Guard.Utf8Text(Summary, 256, 512, allowNewLines: false);
        F5Guard.Identifier(ActionId);
    }
}

public sealed class F5WorkerEvent
{
    private F5WorkerEvent(
        F5WorkerEventKind kind,
        long sequence,
        F5RequestIds ids,
        F5WorkerIdentity worker,
        string referenceRevision,
        F5PcmFrame? frame = null,
        int? chunkIndex = null,
        long? finalSampleCount = null,
        F5CancellationCapability? cancellation = null,
        F5WorkerError? error = null)
    {
        Kind = kind;
        Sequence = sequence;
        Ids = ids;
        Worker = worker;
        ReferenceRevision = referenceRevision;
        Frame = frame;
        ChunkIndex = chunkIndex;
        FinalSampleCount = finalSampleCount;
        Cancellation = cancellation;
        Error = error;
        ValidateShape();
    }

    public F5ProtocolVersion ProtocolVersion => F5ProtocolVersion.Current;
    public string ContractId => F5WorkerProtocol.ContractId;
    public F5WorkerEventKind Kind { get; }
    public long Sequence { get; }
    public F5RequestIds Ids { get; }
    public F5WorkerIdentity Worker { get; }
    public string ReferenceRevision { get; }
    public F5PcmFrame? Frame { get; }
    public int? ChunkIndex { get; }
    public long? FinalSampleCount { get; }
    public F5CancellationCapability? Cancellation { get; }
    public F5WorkerError? Error { get; }
    public bool IsTerminal => Kind is F5WorkerEventKind.Completed or F5WorkerEventKind.Canceled or F5WorkerEventKind.Failed;

    public static F5WorkerEvent Started(long sequence, F5SynthesisRequest request, F5WorkerIdentity worker) =>
        new(F5WorkerEventKind.Started, sequence, request.Ids, worker, request.Reference.ReferenceRevision);

    public static F5WorkerEvent Audio(long sequence, F5SynthesisRequest request,
        F5WorkerIdentity worker, F5PcmFrame frame) =>
        new(F5WorkerEventKind.AudioFrame, sequence, request.Ids, worker,
            request.Reference.ReferenceRevision, frame: frame);

    public static F5WorkerEvent ChunkDone(long sequence, F5SynthesisRequest request,
        F5WorkerIdentity worker, int chunkIndex, long finalSampleCount) =>
        new(F5WorkerEventKind.ChunkCompleted, sequence, request.Ids, worker,
            request.Reference.ReferenceRevision, chunkIndex: chunkIndex, finalSampleCount: finalSampleCount);

    public static F5WorkerEvent Completed(long sequence, F5SynthesisRequest request,
        F5WorkerIdentity worker, long finalSampleCount) =>
        new(F5WorkerEventKind.Completed, sequence, request.Ids, worker,
            request.Reference.ReferenceRevision, finalSampleCount: finalSampleCount);

    public static F5WorkerEvent Canceled(long sequence, F5SynthesisRequest request,
        F5WorkerIdentity worker, long finalSampleCount, F5CancellationCapability cancellation) =>
        new(F5WorkerEventKind.Canceled, sequence, request.Ids, worker,
            request.Reference.ReferenceRevision, finalSampleCount: finalSampleCount,
            cancellation: cancellation);

    public static F5WorkerEvent Failed(long sequence, F5SynthesisRequest request,
        F5WorkerIdentity worker, F5WorkerError error) =>
        new(F5WorkerEventKind.Failed, sequence, request.Ids, worker,
            request.Reference.ReferenceRevision, error: error);

    internal void ValidateShape()
    {
        F5Guard.Defined(Kind);
        F5Guard.Require(Sequence >= 0);
        Ids.Validate();
        F5Guard.Sha256(ReferenceRevision);
        switch (Kind)
        {
            case F5WorkerEventKind.Started:
                F5Guard.Require(Frame is null && ChunkIndex is null && FinalSampleCount is null &&
                    Cancellation is null && Error is null);
                break;
            case F5WorkerEventKind.AudioFrame:
                F5Guard.Require(Frame is not null && ChunkIndex is null && FinalSampleCount is null &&
                    Cancellation is null && Error is null);
                Frame!.Validate();
                break;
            case F5WorkerEventKind.ChunkCompleted:
                F5Guard.Require(Frame is null && ChunkIndex >= 0 &&
                    FinalSampleCount is >= 0 and <= F5WorkerProtocol.MaximumSamples &&
                    Cancellation is null && Error is null);
                break;
            case F5WorkerEventKind.Completed:
                F5Guard.Require(Frame is null && ChunkIndex is null &&
                    FinalSampleCount is >= 0 and <= F5WorkerProtocol.MaximumSamples &&
                    Cancellation is null && Error is null);
                break;
            case F5WorkerEventKind.Canceled:
                F5Guard.Require(Frame is null && ChunkIndex is null &&
                    FinalSampleCount is >= 0 and <= F5WorkerProtocol.MaximumSamples &&
                    Cancellation is not null && Error is null);
                F5Guard.Defined(Cancellation!.Value);
                break;
            case F5WorkerEventKind.Failed:
                F5Guard.Require(Frame is null && ChunkIndex is null && FinalSampleCount is null &&
                    Cancellation is null && Error is not null);
                Error!.Validate();
                break;
        }
    }

    public override string ToString() =>
        $"F5 worker event {{ Kind = {Kind}, Sequence = {Sequence}, content = omitted }}";
}

public sealed record F5CancelRequest
{
    public required F5RequestIds Ids { get; init; }
    public required Guid ActionId { get; init; }
    public required string DestinationId { get; init; }
    public required string ReferenceRevision { get; init; }
}

public sealed record F5CancelResponse
{
    public required F5RequestIds Ids { get; init; }
    public required bool LocalDiscardAcknowledged { get; init; }
    public required F5CancellationCapability ComputeCancellation { get; init; }
    public required bool WorkerMayContinue { get; init; }
}

public sealed record F5CacheInvalidationRequest
{
    public required Guid RequestId { get; init; }
    public required string DestinationId { get; init; }
    public required F5WorkerIdentity ExpectedWorker { get; init; }
    public required IReadOnlyList<string> ReferenceRevisions { get; init; }
}

public sealed record F5CacheInvalidationResponse
{
    public required Guid RequestId { get; init; }
    public required F5WorkerIdentity Worker { get; init; }
    public required IReadOnlyList<string> InvalidatedReferenceRevisions { get; init; }
}

public interface IF5WorkerTransport
{
    IAsyncEnumerable<F5WorkerEvent> StreamAsync(
        F5SynthesisRequest request,
        CancellationToken cancellationToken);

    ValueTask<F5CancelResponse> CancelAsync(
        F5CancelRequest request,
        CancellationToken cancellationToken);

    ValueTask<F5CacheInvalidationResponse> InvalidateReferenceCacheAsync(
        F5CacheInvalidationRequest request,
        CancellationToken cancellationToken);
}

public sealed class F5TransportException : Exception
{
    public F5TransportException(string message) : base(message) { }
    public F5TransportException(string message, Exception innerException) : base(message, innerException) { }
}
