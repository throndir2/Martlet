using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Martlet.Avatar.Audio2Face;
using Martlet.Avatars;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Gateway.Audio2Face;

/// <summary>
/// Gateway relay from a paired client to this host's own loopback Audio2Face-3D service (the audio2face role's engine:
/// Martlet's local open-source SDK service or NVIDIA's NIM, both speaking the same gRPC protocol). Enabling it in
/// the host configuration is the host owner's standing permission for paired <c>voice</c> devices to
/// send generated-speech PCM chunks; nothing else on the host is reachable through it.
/// </summary>
public sealed class Audio2FaceRelayWorker : IAudio2FaceGatewayInferenceWorker, IAsyncDisposable
{
    public const string DefaultDestinationId = "audio2face-host";
    public const string DefaultWorkerId = "audio2face-relay";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly Audio2FaceOptions options;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> running = new();

    public Audio2FaceRelayWorker(Uri endpoint, string modelId, string modelRevision,
        string destinationId = DefaultDestinationId, string workerId = DefaultWorkerId)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        options = new Audio2FaceOptions { Endpoint = endpoint };
        options.Validate();
        Route = GatewayInferenceRoute.Audio2Face(destinationId, workerId, modelId, modelRevision);
    }

    public GatewayInferenceRoute Route { get; }

    public ValueTask<GatewayInferencePermissionLease> AcquirePermissionAsync(
        GatewayInferenceRequest request, GatewayPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(principal);
        cancellationToken.ThrowIfCancellationRequested();
        if (lifetime.IsCancellationRequested)
            throw new GatewayInferenceWorkerException(GatewayInferenceWorkerFailure.Unavailable);
        if (!ReferenceEquals(request.Route, Route) || principal.Role != GatewayRole.Voice ||
            request.Payload is not GatewayAudio2FacePayload)
            throw new GatewayInferenceWorkerException(GatewayInferenceWorkerFailure.PermissionDenied);
        return ValueTask.FromResult<GatewayInferencePermissionLease>(new Lease(request, principal, this));
    }

    public async IAsyncEnumerable<GatewayInferenceEvent> ExecuteAsync(
        GatewayInferenceRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = (GatewayAudio2FacePayload)request.Payload;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        running[request.RequestId] = stop;
        var sequence = 0L;
        try
        {
            yield return Event(request, GatewayInferenceEventKind.Started, sequence++);
            var clip = Clip(request, payload);
            // The gateway owns the request deadline; this bounds only the local service authorization.
            using var authorization = new Audio2FaceAuthorization(clip, options, DateTimeOffset.UtcNow.AddSeconds(60),
                allowGeneratedSpeechAnalysis: true);
            var frames = new Audio2FaceAdapter(options).AnimateAsync(clip, authorization, stop.Token)
                .GetAsyncEnumerator(stop.Token);
            await using (frames.ConfigureAwait(false))
            {
                var frameSequence = 0L;
                while (true)
                {
                    AvatarFrame frame;
                    string? failure = null;
                    try
                    {
                        if (!await frames.MoveNextAsync().ConfigureAwait(false)) break;
                        frame = frames.Current;
                    }
                    catch (Audio2FaceException error)
                    {
                        failure = error.Failure is Audio2FaceFailure.TransportFailure or Audio2FaceFailure.DeadlineExceeded
                            ? "worker.unavailable" : "worker.failed";
                        frame = null!;
                    }
                    if (failure is not null)
                    {
                        yield return Event(request, GatewayInferenceEventKind.Failed, sequence++, errorCode: failure);
                        yield break;
                    }
                    var body = JsonSerializer.SerializeToUtf8Bytes(new { blendshapes = frame.Blendshapes }, Json);
                    yield return Event(request, GatewayInferenceEventKind.FaceFrame, sequence++, body,
                        frameSequence: frameSequence++, sampleOffset: frame.SampleOffset - clip.SampleOffset);
                }
            }
            yield return Event(request, GatewayInferenceEventKind.Completed, sequence);
        }
        finally
        {
            running.TryRemove(request.RequestId, out _);
        }
    }

    public ValueTask<GatewayInferenceCancellationReceipt> CancelAsync(
        GatewayInferenceCancellationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (running.TryGetValue(request.RequestId, out var stop))
            try { stop.Cancel(); }
            catch (ObjectDisposedException) { }
        return ValueTask.FromResult(new GatewayInferenceCancellationReceipt
        {
            RouteId = request.RouteId, RequestId = request.RequestId, LocalDiscardAcknowledged = true,
            ComputeCancellation = GatewayCancellationCapability.RequestAbort, WorkerMayContinue = true
        });
    }

    public ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        foreach (var stop in running.Values)
            try { stop.Cancel(); }
            catch (ObjectDisposedException) { }
        return ValueTask.CompletedTask;
    }

    private static GeneratedSpeechClip Clip(GatewayInferenceRequest request, GatewayAudio2FacePayload payload)
    {
        var ids = new CorrelationIds { SessionId = request.SessionId, TurnId = request.TurnId, RequestId = request.RequestId };
        var format = new PcmFormat { SampleRate = payload.SampleRate, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian };
        var frames = new List<PcmFrame>();
        var pcm = payload.Pcm;
        var frameBytes = Math.Min(PcmFrame.MaxDataBytes, payload.SampleRate / 10 * 2);
        long offset = 0;
        for (var start = 0; start < pcm.Length; start += frameBytes)
        {
            var slice = pcm.Span.Slice(start, Math.Min(frameBytes, pcm.Length - start));
            frames.Add(new PcmFrame(ids, request.Epoch, frames.Count, offset, format, slice));
            offset += slice.Length / 2;
        }
        return new GeneratedSpeechClip(frames);
    }

    private GatewayInferenceEvent Event(GatewayInferenceRequest request, GatewayInferenceEventKind kind, long sequence,
        ReadOnlyMemory<byte> payload = default, string? errorCode = null, long? frameSequence = null, long? sampleOffset = null) =>
        new(kind, sequence, Route.RouteId, Route.DestinationId, Route.WorkerId, Route.ModelId, Route.ModelRevision,
            Route.ModelSha256, Route.ArtifactIdentitySha256, request.SessionId, request.TurnId, request.RequestId,
            request.Epoch, payload, errorCode, frameSequence, sampleOffset: sampleOffset);

    private sealed class Lease(GatewayInferenceRequest request, GatewayPrincipal principal, Audio2FaceRelayWorker owner)
        : GatewayInferencePermissionLease(request, principal)
    {
        public override CancellationToken Revoked => owner.lifetime.Token;
        public override void Validate()
        {
            if (owner.lifetime.IsCancellationRequested)
                throw new GatewayInferenceWorkerException(GatewayInferenceWorkerFailure.PermissionDenied);
        }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
