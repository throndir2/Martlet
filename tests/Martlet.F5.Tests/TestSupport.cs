using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Martlet.F5;

namespace Martlet.F5.Tests;

internal sealed class F5TestScope : IDisposable
{
    internal F5TestScope()
    {
        Root = Path.Combine(Path.GetTempPath(), "Martlet.F5.Tests", Guid.NewGuid().ToString("N"));
        StoreDirectory = Path.Combine(Root, "store");
        SourcePath = Path.Combine(Root, "reference.wav");
        Directory.CreateDirectory(Root);
    }

    internal string Root { get; }
    internal string StoreDirectory { get; }
    internal string SourcePath { get; }

    internal F5ReferencePresetStore OpenStore(TimeProvider? clock = null) =>
        F5ReferencePresetStore.Open(StoreDirectory, clock ?? F5TestData.Clock);

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class RecordingPcmSink : IF5PcmFrameSink
{
    private readonly object gate = new();
    private readonly List<F5PcmFrame> frames = [];
    internal TaskCompletionSource FirstFrame { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal IReadOnlyList<F5PcmFrame> Frames
    {
        get
        {
            lock (gate)
                return frames.ToArray();
        }
    }

    public ValueTask WriteAsync(F5PcmFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            frames.Add(frame);
        FirstFrame.TrySetResult();
        return ValueTask.CompletedTask;
    }
}

internal static class F5TestData
{
    internal static readonly DateTimeOffset Now =
        new(2026, 9, 22, 6, 30, 0, TimeSpan.Zero);
    internal static readonly TimeProvider Clock = new FixedTimeProvider(Now);
    internal const string Destination = "fixture-local-gateway";

    internal static F5VoiceRightsAcknowledgement Rights(
        string destination = Destination,
        bool confirmed = true) => new()
        {
            AcknowledgementId = Guid.NewGuid(),
            Basis = F5VoiceRightsBasis.OwnVoice,
            StatementVersion = F5ReferenceLimits.RightsStatementVersion,
            ProcessingDestinationId = destination,
            AcknowledgedAtUtc = Now,
            Confirmed = confirmed
        };

    internal static F5RequestIds Ids() => new()
    {
        SessionId = Guid.NewGuid(),
        TurnId = Guid.NewGuid(),
        RequestId = Guid.NewGuid()
    };

    internal static F5ConversationSynthesis Conversation(
        F5WorkerIdentity worker,
        string referenceRevision,
        DateTimeOffset? deadline = null,
        string text = "A deterministic fixture response.") => new(
            Ids(),
            Destination,
            worker,
            referenceRevision,
            deadline ?? Now.AddSeconds(30),
            [new F5TextChunk(0, "chunk-0", text)]);

    internal static F5PreviewSynthesis Preview(
        F5WorkerIdentity worker,
        F5ReferenceSnapshot snapshot,
        DateTimeOffset? deadline = null,
        string text = "Preview fixture.") => new(
            Ids(),
            Destination,
            worker,
            snapshot.PresetId,
            snapshot.ReferenceRevision,
            deadline ?? Now.AddSeconds(20),
            text);

    internal static async Task<F5ReferenceSnapshot> SnapshotAsync(
        F5ReferencePresetStore store,
        string sourcePath,
        Guid? presetId = null,
        string name = "Fixture voice",
        string transcript = "A rights-cleared fixture reference.") =>
        await store.SnapshotAsync(new()
        {
            PresetId = presetId,
            PresetName = name,
            AbsoluteSourcePath = sourcePath,
            Transcript = transcript,
            Rights = Rights()
        });

    internal static async Task<F5ReferenceApplyReceipt> ApplyAsync(
        F5ReferencePresetStore store,
        F5ReferenceSnapshot snapshot)
    {
        var preview = await store.CreateApplyPreviewAsync(
            snapshot.PresetId, snapshot.ReferenceRevision);
        return await store.ApplyAsync(preview, preview.Authorize(F5ApplyDecision.Allow));
    }

    internal static void WriteWav(
        string path,
        int seed = 1,
        int sampleRate = 24_000,
        int durationMilliseconds = 1_000,
        ushort formatTag = 1,
        bool silence = false)
    {
        var sampleCount = checked(sampleRate * durationMilliseconds / 1000);
        var dataBytes = checked(sampleCount * 2);
        var bytes = new byte[44 + dataBytes];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), bytes.Length - 8);
        "WAVE"u8.CopyTo(bytes.AsSpan(8));
        "fmt "u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), formatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28, 4), sampleRate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34, 2), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40, 4), dataBytes);
        for (var index = 0; index < sampleCount; index++)
        {
            var sample = silence ? (short)0 : (short)(((index + seed) % 127 + 1) * 64);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44 + index * 2, 2), sample);
        }
        File.WriteAllBytes(path, bytes);
    }

    internal static async Task<F5Exception> FailureAsync(
        F5Failure expected,
        Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<F5Exception>(action);
        Assert.Equal(expected, error.Failure);
        return error;
    }

    internal static F5WorkerIdentity WithCancellation(
        F5WorkerIdentity identity,
        F5CancellationCapability cancellation) => new(
            identity.WorkerId,
            identity.Evidence,
            identity.Runtime,
            identity.Artifacts,
            cancellation);
}

public enum AdversarialTransportFault
{
    DelayedCache,
    NullCacheResponse,
    NullCacheWorker,
    NullCacheRevisions,
    NullStreamEnumerable,
    NullStreamEvent,
    NullCancelAfterTruncation,
    StrongCancelAcknowledgement,
    RequestAbortStoppedAcknowledgement,
    StrongCanceledTerminal
}

internal sealed class AdversarialF5Transport : IF5WorkerTransport
{
    private readonly F5WorkerIdentity identity;
    private readonly AdversarialTransportFault fault;
    private readonly DeterministicF5WorkerTransport inner;
    private int streamCalls;
    private int cancelCalls;

    internal AdversarialF5Transport(
        F5WorkerIdentity identity,
        AdversarialTransportFault fault)
    {
        this.identity = identity;
        this.fault = fault;
        inner = new(identity, new()
        {
            FramesPerChunk = 2,
            WaitForCancellationAfterFrames = 1
        });
    }

    internal int StreamCalls => Volatile.Read(ref streamCalls);
    internal int CancelCalls => Volatile.Read(ref cancelCalls);

    public IAsyncEnumerable<F5WorkerEvent> StreamAsync(
        F5SynthesisRequest request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref streamCalls);
        return fault switch
        {
            AdversarialTransportFault.NullStreamEnumerable => null!,
            AdversarialTransportFault.NullStreamEvent =>
                NullStream(request, cancellationToken),
            AdversarialTransportFault.NullCancelAfterTruncation =>
                TruncatedStream(request, cancellationToken),
            AdversarialTransportFault.StrongCanceledTerminal =>
                StrongCanceledStream(request, cancellationToken),
            _ => inner.StreamAsync(request, cancellationToken)
        };
    }

    public ValueTask<F5CancelResponse> CancelAsync(
        F5CancelRequest request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref cancelCalls);
        return fault switch
        {
            AdversarialTransportFault.NullCancelAfterTruncation =>
                ValueTask.FromResult<F5CancelResponse>(null!),
            AdversarialTransportFault.StrongCancelAcknowledgement =>
                ValueTask.FromResult(Response(
                    request, F5CancellationCapability.CooperativeComputeCancel,
                    workerMayContinue: false)),
            AdversarialTransportFault.RequestAbortStoppedAcknowledgement =>
                ValueTask.FromResult(Response(
                    request, F5CancellationCapability.RequestAbort,
                    workerMayContinue: false)),
            _ => inner.CancelAsync(request, cancellationToken)
        };
    }

    public ValueTask<F5CacheInvalidationResponse> InvalidateReferenceCacheAsync(
        F5CacheInvalidationRequest request,
        CancellationToken cancellationToken) => fault switch
        {
            AdversarialTransportFault.DelayedCache =>
                new(DelayCacheAsync(cancellationToken)),
            AdversarialTransportFault.NullCacheResponse =>
                ValueTask.FromResult<F5CacheInvalidationResponse>(null!),
            AdversarialTransportFault.NullCacheWorker =>
                ValueTask.FromResult(new F5CacheInvalidationResponse
                {
                    RequestId = request.RequestId,
                    Worker = null!,
                    InvalidatedReferenceRevisions = request.ReferenceRevisions
                }),
            AdversarialTransportFault.NullCacheRevisions =>
                ValueTask.FromResult(new F5CacheInvalidationResponse
                {
                    RequestId = request.RequestId,
                    Worker = identity,
                    InvalidatedReferenceRevisions = null!
                }),
            _ => inner.InvalidateReferenceCacheAsync(request, cancellationToken)
        };

    private static async Task<F5CacheInvalidationResponse> DelayCacheAsync(
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Unreachable delayed cache fixture.");
    }

    private async IAsyncEnumerable<F5WorkerEvent> NullStream(
        F5SynthesisRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return null!;
        await Task.CompletedTask;
    }

    private async IAsyncEnumerable<F5WorkerEvent> TruncatedStream(
        F5SynthesisRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return F5WorkerEvent.Started(0, request, identity);
        await Task.CompletedTask;
    }

    private async IAsyncEnumerable<F5WorkerEvent> StrongCanceledStream(
        F5SynthesisRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return F5WorkerEvent.Started(0, request, identity);
        yield return F5WorkerEvent.Canceled(1, request, identity, 0,
            F5CancellationCapability.CooperativeComputeCancel);
        await Task.CompletedTask;
    }

    private static F5CancelResponse Response(
        F5CancelRequest request,
        F5CancellationCapability cancellation,
        bool workerMayContinue) => new()
        {
            Ids = request.Ids,
            LocalDiscardAcknowledged = true,
            ComputeCancellation = cancellation,
            WorkerMayContinue = workerMayContinue
        };
}
