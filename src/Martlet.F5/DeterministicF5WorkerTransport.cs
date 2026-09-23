using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Martlet.F5;

public sealed record DeterministicF5WorkerOptions
{
    public int FramesPerChunk { get; init; } = 2;
    public int? TruncateAfterFrames { get; init; }
    public int? WaitForCancellationAfterFrames { get; init; }
    public int LateFramesAfterCancellation { get; init; }
    public int? FailAfterFrames { get; init; }
    public F5WorkerErrorCode FailureCode { get; init; } = F5WorkerErrorCode.InternalFailure;

    internal void Validate()
    {
        F5Guard.Require(FramesPerChunk is >= 1 and <= 8);
        F5Guard.Require(TruncateAfterFrames is null or >= 1);
        F5Guard.Require(WaitForCancellationAfterFrames is null or >= 1);
        F5Guard.Require(LateFramesAfterCancellation is >= 0 and <= 8);
        F5Guard.Require(FailAfterFrames is null or >= 0);
        F5Guard.Defined(FailureCode);
    }
}

public static class DeterministicF5FixtureIdentity
{
    private const string FixtureRevision = "1111111111111111111111111111111111111111";

    public static F5WorkerIdentity Create()
    {
        static F5ArtifactIdentity Artifact(F5ArtifactRole role, string id, char digest) => new()
        {
            Role = role,
            ArtifactId = id,
            Revision = FixtureRevision,
            Sha256 = new string(digest, 64),
            Bytes = 1,
            LicenseId = "fixture-only"
        };

        return new(
            "martlet-f5-deterministic-fixture",
            F5EvidenceKind.SyntheticFixture,
            new()
            {
                WorkerBuildId = "martlet-f5-fixture",
                WorkerBuildRevision = FixtureRevision,
                F5PackageVersion = "0.0.0-fixture",
                F5SourceRevision = FixtureRevision,
                PythonVersion = "0.0.0-fixture",
                TorchVersion = "0.0.0-fixture",
                TorchaudioVersion = "0.0.0-fixture",
                CudaRuntimeVersion = "0.0.0-fixture"
            },
            [
                Artifact(F5ArtifactRole.RuntimeImage, "fixture-runtime", '1'),
                Artifact(F5ArtifactRole.ModelWeights, "fixture-model", '2'),
                Artifact(F5ArtifactRole.Vocabulary, "fixture-vocabulary", '3'),
                Artifact(F5ArtifactRole.VocoderWeights, "fixture-vocoder", '4'),
                Artifact(F5ArtifactRole.VocoderConfiguration, "fixture-vocoder-config", '5')
            ],
            F5CancellationCapability.DiscardOnly);
    }
}

public sealed class DeterministicF5WorkerTransport : IF5WorkerTransport
{
    private readonly object gate = new();
    private readonly F5WorkerIdentity identity;
    private readonly DeterministicF5WorkerOptions options;
    private readonly Dictionary<string, string> conditioningCache = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, TaskCompletionSource> cancellationSignals = [];
    private readonly List<F5CacheInvalidationRequest> invalidations = [];
    private int streamCalls;
    private int cancelCalls;
    private int cacheHits;
    private int cacheMisses;

    public DeterministicF5WorkerTransport(
        F5WorkerIdentity identity,
        DeterministicF5WorkerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        F5Guard.Require(identity.Evidence == F5EvidenceKind.SyntheticFixture);
        this.identity = identity;
        this.options = options ?? new();
        this.options.Validate();
    }

    public int StreamCalls { get { lock (gate) return streamCalls; } }
    public int CancelCalls { get { lock (gate) return cancelCalls; } }
    public int CacheHits { get { lock (gate) return cacheHits; } }
    public int CacheMisses { get { lock (gate) return cacheMisses; } }

    public IReadOnlyList<string> CachedReferenceRevisions
    {
        get
        {
            lock (gate)
                return Array.AsReadOnly(conditioningCache.Keys.Order(StringComparer.Ordinal).ToArray());
        }
    }

    public IReadOnlyList<F5CacheInvalidationRequest> InvalidationRequests
    {
        get
        {
            lock (gate)
                return Array.AsReadOnly(invalidations.ToArray());
        }
    }

    public async IAsyncEnumerable<F5WorkerEvent> StreamAsync(
        F5SynthesisRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            streamCalls++;
            cancellationSignals[request.Ids.RequestId] = signal;
        }

        try
        {
            var eventSequence = 0L;
            if (!request.ExpectedWorker.Matches(identity))
            {
                yield return F5WorkerEvent.Failed(eventSequence, request, identity, Error(
                    F5WorkerErrorCode.IdentityMismatch,
                    "The fixture worker identity does not match the requested pin.",
                    "f5.review-worker"));
                yield break;
            }
            yield return F5WorkerEvent.Started(eventSequence++, request, identity);

            if (!ReferenceMatches(request.Reference))
            {
                yield return F5WorkerEvent.Failed(eventSequence, request, identity, Error(
                    F5WorkerErrorCode.ReferenceRejected,
                    "The supplied reference bytes, transcript, or revisions do not agree.",
                    "f5.reload-reference"));
                yield break;
            }

            var cacheMaterial = request.Reference.AudioSha256 + ":" +
                request.Reference.TranscriptRevision;
            lock (gate)
            {
                if (conditioningCache.TryGetValue(request.Reference.ReferenceRevision, out var cached))
                {
                    if (cached != cacheMaterial)
                        throw new F5TransportException(
                            "A reference revision was reused for different conditioning bytes.");
                    cacheHits++;
                }
                else
                {
                    conditioningCache.Add(request.Reference.ReferenceRevision, cacheMaterial);
                    cacheMisses++;
                }
            }

            var frameSequence = 0L;
            var sampleOffset = 0L;
            var emittedFrames = 0;

            for (var chunkIndex = 0; chunkIndex < request.Chunks.Count; chunkIndex++)
            {
                for (var frameIndex = 0; frameIndex < options.FramesPerChunk; frameIndex++)
                {
                    if (options.WaitForCancellationAfterFrames is { } waitAt &&
                        emittedFrames >= waitAt && !IsCanceled(request.Ids.RequestId, cancellationToken))
                    {
                        await Task.WhenAny(
                            signal.Task,
                            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
                            Task.Delay(TimeSpan.FromSeconds(10)));
                    }

                    if (IsCanceled(request.Ids.RequestId, cancellationToken))
                    {
                        for (var late = 0; late < options.LateFramesAfterCancellation; late++)
                        {
                            var lateFrame = Frame(request, chunkIndex, frameSequence++,
                                sampleOffset, emittedFrames++);
                            sampleOffset += lateFrame.SampleCount;
                            yield return F5WorkerEvent.Audio(eventSequence++, request, identity, lateFrame);
                        }
                        yield return F5WorkerEvent.Canceled(eventSequence, request, identity,
                            sampleOffset, F5CancellationCapability.DiscardOnly);
                        yield break;
                    }

                    if (options.FailAfterFrames == emittedFrames)
                    {
                        yield return F5WorkerEvent.Failed(eventSequence, request, identity,
                            Error(options.FailureCode,
                                "The deterministic fixture emitted its configured failure.",
                                "f5.review-fixture"));
                        yield break;
                    }

                    var frame = Frame(request, chunkIndex, frameSequence++, sampleOffset,
                        emittedFrames++);
                    sampleOffset += frame.SampleCount;
                    yield return F5WorkerEvent.Audio(eventSequence++, request, identity, frame);
                    if (options.TruncateAfterFrames == emittedFrames)
                        yield break;
                }

                yield return F5WorkerEvent.ChunkDone(eventSequence++, request, identity,
                    chunkIndex, sampleOffset);
            }

            if (options.FailAfterFrames == emittedFrames)
            {
                yield return F5WorkerEvent.Failed(eventSequence, request, identity,
                    Error(options.FailureCode,
                        "The deterministic fixture emitted its configured failure.",
                        "f5.review-fixture"));
                yield break;
            }
            yield return F5WorkerEvent.Completed(eventSequence, request, identity, sampleOffset);
        }
        finally
        {
            lock (gate)
                cancellationSignals.Remove(request.Ids.RequestId);
        }
    }

    public ValueTask<F5CancelResponse> CancelAsync(
        F5CancelRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request.Ids.Validate();
        F5Guard.Require(request.ActionId != Guid.Empty);
        F5Guard.Identifier(request.DestinationId, 128);
        F5Guard.Sha256(request.ReferenceRevision);
        lock (gate)
        {
            cancelCalls++;
            if (cancellationSignals.TryGetValue(request.Ids.RequestId, out var signal))
                signal.TrySetResult();
        }
        return ValueTask.FromResult(new F5CancelResponse
        {
            Ids = request.Ids,
            LocalDiscardAcknowledged = true,
            ComputeCancellation = F5CancellationCapability.DiscardOnly,
            WorkerMayContinue = true
        });
    }

    public ValueTask<F5CacheInvalidationResponse> InvalidateReferenceCacheAsync(
        F5CacheInvalidationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        F5Guard.Require(request.RequestId != Guid.Empty &&
            request.ExpectedWorker.Matches(identity), F5Failure.IdentityMismatch);
        F5Guard.Identifier(request.DestinationId, 128);
        F5Guard.Require(request.ReferenceRevisions is { Count: > 0 and <= 2 });
        var revisions = request.ReferenceRevisions
            .Order(StringComparer.Ordinal)
            .ToArray();
        F5Guard.Require(revisions.Distinct(StringComparer.Ordinal).Count() == revisions.Length);
        foreach (var revision in revisions)
            F5Guard.Sha256(revision);
        lock (gate)
        {
            invalidations.Add(request);
            foreach (var revision in revisions)
                conditioningCache.Remove(revision);
        }
        return ValueTask.FromResult(new F5CacheInvalidationResponse
        {
            RequestId = request.RequestId,
            Worker = identity,
            InvalidatedReferenceRevisions = Array.AsReadOnly(revisions)
        });
    }

    private bool IsCanceled(Guid requestId, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return true;
        lock (gate)
            return cancellationSignals.TryGetValue(requestId, out var signal) &&
                signal.Task.IsCompleted;
    }

    private static bool ReferenceMatches(F5ReferenceInput reference)
    {
        var audio = Convert.ToHexStringLower(SHA256.HashData(reference.Audio.Span));
        var transcript = F5ReferenceDigests.TranscriptRevision(reference.Transcript);
        var revision = F5ReferenceDigests.ReferenceRevision(audio, transcript);
        return F5Guard.FixedTimeEquals(audio, reference.AudioSha256) &&
            F5Guard.FixedTimeEquals(transcript, reference.TranscriptRevision) &&
            F5Guard.FixedTimeEquals(revision, reference.ReferenceRevision);
    }

    private static F5PcmFrame Frame(
        F5SynthesisRequest request,
        int chunkIndex,
        long sequence,
        long sampleOffset,
        int salt)
    {
        const int samples = 480;
        var seedText = request.Reference.ReferenceRevision + "\n" +
            request.Chunks[chunkIndex].Text + "\n" + salt;
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(seedText));
        var bytes = new byte[samples * 2];
        for (var index = 0; index < samples; index++)
        {
            var value = (short)((seed[index % seed.Length] - 128) * 64);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * 2, 2), value);
        }
        return new(sequence, chunkIndex, sampleOffset, samples,
            F5PcmFormat.Transport, bytes);
    }

    private static F5WorkerError Error(
        F5WorkerErrorCode code,
        string summary,
        string actionId) => new()
        {
            Code = code,
            Stage = "synthesis",
            Retryable = false,
            Summary = summary,
            ActionId = actionId
        };
}
