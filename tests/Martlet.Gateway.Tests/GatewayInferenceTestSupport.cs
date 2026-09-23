using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Martlet.F5;
using Martlet.Gateway;
using Martlet.Perception;
using Martlet.Providers.Ollama;

namespace Martlet.Gateway.Tests;

public enum SyntheticInferenceFault
{
    None,
    Truncated,
    IdentityDrift,
    LateAfterTerminal,
    OversizedEvent,
    TooManyEvents,
    WorkerFailed,
    OptionalContextUnavailable,
    BlockUntilCanceled,
    NonCompletingCancel,
    F5ExtraChunk,
    MalformedPerception,
    WrongPerceptionProvenance,
    OversizedPerceptionOutput,
    LateOnCancel
}

internal sealed class SyntheticInferenceWorker(
    GatewayInferenceRoute route,
    SyntheticInferenceFault fault = SyntheticInferenceFault.None,
    TimeSpan? eventDelay = null,
    int dataEvents = 1) :
    IOllamaGatewayInferenceWorker,
    IF5GatewayInferenceWorker,
    IPerceptionGatewayInferenceWorker
{
    private int calls;
    private int cancelCalls;
    private int active;
    private int maximumActive;

    public GatewayInferenceRoute Route { get; } = route;
    internal int Calls => Volatile.Read(ref calls);
    internal int CancelCalls => Volatile.Read(ref cancelCalls);
    internal int MaximumActive => Volatile.Read(ref maximumActive);
    internal TaskCompletionSource Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<GatewayInferenceCancellationReceipt> ReleaseCancellation { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource CancelObserved { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<GatewayInferencePermissionLease> AcquirePermissionAsync(
        GatewayInferenceRequest request, GatewayPrincipal principal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<GatewayInferencePermissionLease>(new SyntheticPermission(request, principal));
    }

    private sealed class SyntheticPermission(GatewayInferenceRequest request, GatewayPrincipal principal)
        : GatewayInferencePermissionLease(request, principal)
    {
        public override CancellationToken Revoked => CancellationToken.None;
        public override void Validate() { }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<GatewayInferenceEvent> ExecuteAsync(
        GatewayInferenceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref calls);
        var current = Interlocked.Increment(ref active);
        UpdateMaximum(current);
        try
        {
            if (fault == SyntheticInferenceFault.OptionalContextUnavailable)
                throw new GatewayInferenceWorkerException(
                    GatewayInferenceWorkerFailure.OptionalContextUnavailable);

            cancellationToken.ThrowIfCancellationRequested();
            yield return Event(
                request,
                GatewayInferenceEventKind.Started,
                0);
            Started.TrySetResult();
            if (fault == SyntheticInferenceFault.Truncated)
                yield break;
            if (fault is SyntheticInferenceFault.BlockUntilCanceled or
                SyntheticInferenceFault.NonCompletingCancel)
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
                yield break;
            }
            if (fault == SyntheticInferenceFault.LateOnCancel)
            {
                await CancelObserved.Task;
                yield return Event(
                    request,
                    DataKind(request.Route.Kind),
                    1,
                    Data(request),
                    drift: true);
                yield break;
            }
            if (eventDelay is { } delay && delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);
            if (fault == SyntheticInferenceFault.WorkerFailed)
            {
                yield return Event(
                    request,
                    GatewayInferenceEventKind.Failed,
                    1,
                    errorCode: "worker.failed");
                yield break;
            }
            if (fault is SyntheticInferenceFault.MalformedPerception or
                SyntheticInferenceFault.WrongPerceptionProvenance or
                SyntheticInferenceFault.OversizedPerceptionOutput)
            {
                var output = fault switch
                {
                    SyntheticInferenceFault.MalformedPerception =>
                        "{}"u8.ToArray(),
                    SyntheticInferenceFault.WrongPerceptionProvenance =>
                        WrongProvenance(request),
                    _ => Encoding.UTF8.GetBytes(
                        "{\"padding\":\"" +
                        new string('x', request.Route.MaximumOutputBytes) +
                        "\"}")
                };
                yield return Event(
                    request,
                    GatewayInferenceEventKind.Observation,
                    1,
                    output);
                yield return Event(
                    request,
                    GatewayInferenceEventKind.Completed,
                    2);
                yield break;
            }
            if (fault == SyntheticInferenceFault.OversizedEvent)
            {
                yield return Event(
                    request,
                    DataKind(request.Route.Kind),
                    1,
                    new byte[request.Route.MaximumEventBytes + 1]);
                yield break;
            }
            if (fault == SyntheticInferenceFault.TooManyEvents)
            {
                for (var index = 1; index < request.Route.MaximumEvents; index++)
                {
                    yield return Event(
                        request,
                        DataKind(request.Route.Kind),
                        index,
                        request.Route.Kind == GatewayInferenceKind.OllamaChat
                            ? "x"u8.ToArray()
                            : Data(request));
                }
                yield return Event(
                    request,
                    GatewayInferenceEventKind.Completed,
                    request.Route.MaximumEvents);
                yield break;
            }

            if (request.Route.Kind == GatewayInferenceKind.F5Synthesis)
            {
                yield return Event(
                    request,
                    GatewayInferenceEventKind.AudioFrame,
                    1,
                    Data(request),
                    drift: fault == SyntheticInferenceFault.IdentityDrift,
                    frameSequence: 0,
                    chunkIndex: 0,
                    sampleOffset: 0,
                    sampleCount: 2);
                yield return Event(
                    request,
                    GatewayInferenceEventKind.ChunkCompleted,
                    2,
                    chunkIndex: 0,
                    finalSampleCount: 2);
                if (fault == SyntheticInferenceFault.F5ExtraChunk)
                {
                    yield return Event(
                        request,
                        GatewayInferenceEventKind.AudioFrame,
                        3,
                        Data(request),
                        frameSequence: 1,
                        chunkIndex: 1,
                        sampleOffset: 2,
                        sampleCount: 2);
                    yield return Event(
                        request,
                        GatewayInferenceEventKind.Completed,
                        4,
                        finalSampleCount: 4);
                    yield break;
                }
                yield return Event(
                    request,
                    GatewayInferenceEventKind.Completed,
                    3,
                    finalSampleCount: 2);
            }
            else
            {
                for (var index = 0; index < dataEvents; index++)
                {
                    yield return Event(
                        request,
                        DataKind(request.Route.Kind),
                        index + 1,
                        Data(request),
                        drift: fault == SyntheticInferenceFault.IdentityDrift &&
                            index == 0);
                }
                yield return Event(
                    request,
                    GatewayInferenceEventKind.Completed,
                    dataEvents + 1);
                if (fault == SyntheticInferenceFault.LateAfterTerminal)
                {
                    yield return Event(
                        request,
                        DataKind(request.Route.Kind),
                        dataEvents + 2,
                        Data(request));
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref active);
        }
    }

    public ValueTask<GatewayInferenceCancellationReceipt> CancelAsync(
        GatewayInferenceCancellationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref cancelCalls);
        CancelObserved.TrySetResult();
        if (fault == SyntheticInferenceFault.NonCompletingCancel)
        {
            return new(ReleaseCancellation.Task);
        }
        return ValueTask.FromResult(new GatewayInferenceCancellationReceipt
        {
            RouteId = request.RouteId,
            RequestId = request.RequestId,
            LocalDiscardAcknowledged = true,
            ComputeCancellation = Route.Cancellation,
            WorkerMayContinue = Route.Cancellation !=
                GatewayCancellationCapability.CooperativeComputeCancel
        });
    }

    private void UpdateMaximum(int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref maximumActive);
            if (candidate <= current ||
                Interlocked.CompareExchange(
                    ref maximumActive,
                    candidate,
                    current) == current)
                return;
        }
    }

    private static GatewayInferenceEvent Event(
        GatewayInferenceRequest request,
        GatewayInferenceEventKind kind,
        long sequence,
        ReadOnlyMemory<byte> payload = default,
        string? errorCode = null,
        bool drift = false,
        long? frameSequence = null,
        int? chunkIndex = null,
        long? sampleOffset = null,
        int? sampleCount = null,
        long? finalSampleCount = null) => new(
            kind,
            sequence,
            request.Route.RouteId,
            request.Route.DestinationId,
            drift ? request.Route.WorkerId + "-drift" : request.Route.WorkerId,
            request.Route.ModelId,
            request.Route.ModelRevision,
            request.Route.ModelSha256,
            request.Route.ArtifactIdentitySha256,
            request.SessionId,
            request.TurnId,
            request.RequestId,
            request.Epoch,
            payload,
            errorCode,
            frameSequence,
            chunkIndex,
            sampleOffset,
            sampleCount,
            finalSampleCount);

    private static GatewayInferenceEventKind DataKind(
        GatewayInferenceKind kind) => kind switch
        {
            GatewayInferenceKind.OllamaChat =>
                GatewayInferenceEventKind.TextDelta,
            GatewayInferenceKind.F5Synthesis =>
                GatewayInferenceEventKind.AudioFrame,
            GatewayInferenceKind.PerceptionOcr or
                GatewayInferenceKind.PerceptionVlm =>
                GatewayInferenceEventKind.Observation,
            _ => throw new InvalidOperationException()
        };

    private static byte[] Data(GatewayInferenceRequest request) =>
        request.Route.Kind switch
        {
            GatewayInferenceKind.OllamaChat =>
                Encoding.UTF8.GetBytes("Fixture response."),
            GatewayInferenceKind.F5Synthesis => [1, 0, 2, 0],
            GatewayInferenceKind.PerceptionOcr or
                GatewayInferenceKind.PerceptionVlm =>
                Observation(request),
            _ => throw new InvalidOperationException()
        };

    private static byte[] Observation(GatewayInferenceRequest request)
    {
        var payload = Assert.IsType<GatewayPerceptionPayload>(request.Payload);
        var role = request.Route.Kind == GatewayInferenceKind.PerceptionOcr
            ? "ocr"
            : "visual_question_answering";
        var processedAt = payload.Frame.CapturedAtUtc.AddMilliseconds(10);
        var expiresAt = payload.Frame.CapturedAtUtc +
            payload.MaximumFrameAge;
        var provenance = new
        {
            frame_id = payload.Frame.FrameId.ToString("D"),
            capture_epoch = payload.Frame.CaptureEpoch,
            selection_id = payload.Frame.Source.SelectionId.ToString("D"),
            source_revision = payload.Frame.Source.SourceRevision,
            capture_permission_revision =
                payload.Frame.Source.CapturePermissionRevision,
            frame_sha256 = payload.Frame.Content.Sha256,
            captured_at_utc = payload.Frame.CapturedAtUtc.ToString(
                "O",
                System.Globalization.CultureInfo.InvariantCulture),
            processed_at_utc = processedAt.ToString(
                "O",
                System.Globalization.CultureInfo.InvariantCulture),
            destination_id = request.Route.DestinationId,
            host_id = "fixture-host",
            worker_id = request.Route.WorkerId,
            evidence = "synthetic_fixture",
            role,
            model_id = request.Route.ModelId,
            model_revision = request.Route.ModelRevision,
            model_sha256 = request.Route.ModelSha256
        };
        object document = request.Route.Kind == GatewayInferenceKind.PerceptionOcr
            ? new
            {
                role,
                provenance,
                expires_at_utc = expiresAt.ToString(
                    "O",
                    System.Globalization.CultureInfo.InvariantCulture),
                ocr = new
                {
                    regions = new[]
                    {
                        new
                        {
                            index = 0,
                            text = "FIXTURE - NOT AI",
                            bounds = new
                            {
                                left = 0,
                                top = 0,
                                right = 10_000,
                                bottom = 10_000
                            },
                            confidence = new { kind = "unavailable" }
                        }
                    },
                    detected_language = "fixture"
                }
            }
            : new
            {
                role,
                provenance,
                expires_at_utc = expiresAt.ToString(
                    "O",
                    System.Globalization.CultureInfo.InvariantCulture),
                vlm = new
                {
                    answer = "FIXTURE - NOT AI",
                    uncertainty = "Synthetic fixture output.",
                    confidence = new { kind = "unavailable" }
                }
            };
        return JsonSerializer.SerializeToUtf8Bytes(
            document,
            GatewayTestHost.Json);
    }

    private static byte[] WrongProvenance(GatewayInferenceRequest request)
    {
        var valid = Encoding.UTF8.GetString(Observation(request));
        return Encoding.UTF8.GetBytes(valid.Replace(
            request.Route.ModelRevision,
            request.Route.ModelRevision + "-drift",
            StringComparison.Ordinal));
    }
}

internal static class GatewayInferenceTestData
{
    internal const string DestinationHost1 = "fixture-host-1";
    internal const string DestinationHost2 = "fixture-host-2";
    internal const string OllamaModelRevision = "fixture-model-revision-001";
    internal const string OllamaModelSha256 =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string SourceRevision =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    internal const string CapturePermissionRevision =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    internal static GatewayInferenceRoute OllamaRoute() =>
        GatewayInferenceRoute.OllamaChat(
            DestinationHost1,
            "ollama-private",
            new OllamaChatModelSelection("fixture-llm", "fixture-model:v1"),
            OllamaModelRevision,
            OllamaModelSha256);

    internal static GatewayInferenceRoute F5Route() =>
        GatewayInferenceRoute.F5Synthesis(
            DestinationHost1,
            DeterministicF5FixtureIdentity.Create());

    internal static GatewayInferenceRoute PerceptionRoute(
        PerceptionRole role) =>
        GatewayInferenceRoute.Perception(
            DestinationHost2,
            DeterministicPerceptionFixtureIdentity.Create(role));

    internal static byte[] Request(
        GatewayInferenceRoute route,
        DateTimeOffset now,
        object? payload = null,
        Guid? requestId = null,
        DateTimeOffset? deadline = null)
    {
        payload ??= Payload(route);
        return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["protocol_version"] = GatewayProtocolVersion.Current,
            ["route_id"] = route.RouteId,
            ["contract_id"] = route.ContractId,
            ["contract_version"] = route.ContractVersion,
            ["destination_id"] = route.DestinationId,
            ["worker_id"] = route.WorkerId,
            ["adapter_version"] = route.AdapterVersion,
            ["model_id"] = route.ModelId,
            ["model_revision"] = route.ModelRevision,
            ["model_sha256"] = route.ModelSha256,
            ["artifact_identity_sha256"] = route.ArtifactIdentitySha256,
            ["session_id"] = System.Guid.NewGuid().ToString("D"),
            ["turn_id"] = System.Guid.NewGuid().ToString("D"),
            ["request_id"] = (requestId ?? System.Guid.NewGuid()).ToString("D"),
            ["epoch"] = 1,
            ["deadline_utc"] = (deadline ?? now.AddSeconds(
                Math.Min(10, route.MaximumDuration.TotalSeconds)))
                .ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["payload"] = payload
        }, GatewayTestHost.Json);
    }

    internal static byte[] Cancellation(
        GatewayInferenceRoute route,
        Guid requestId) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["protocol_version"] = GatewayProtocolVersion.Current,
            ["route_id"] = route.RouteId,
            ["request_id"] = requestId.ToString("D")
        }, GatewayTestHost.Json);

    internal static Guid RequestId(byte[] request)
    {
        using var document = JsonDocument.Parse(request);
        return document.RootElement.GetProperty("request_id").GetGuid();
    }

    internal static async Task<JsonElement[]> ReadEventsAsync(
        HttpResponseMessage response)
    {
        var events = new List<JsonElement>();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: false);
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0)
                continue;
            using var document = JsonDocument.Parse(line);
            events.Add(document.RootElement.Clone());
        }
        return events.ToArray();
    }

    internal static object Payload(GatewayInferenceRoute route) =>
        route.Kind switch
        {
            GatewayInferenceKind.OllamaChat => new
            {
                input = "Fixture prompt.",
                temperature = 0.25,
                maximum_output_tokens = 128,
                maximum_context_tokens = 1024
            },
            GatewayInferenceKind.F5Synthesis => F5Payload(),
            GatewayInferenceKind.PerceptionOcr => PerceptionPayload(
                includeQuestion: false),
            GatewayInferenceKind.PerceptionVlm => PerceptionPayload(
                includeQuestion: true),
            _ => throw new InvalidOperationException()
        };

    private static object F5Payload()
    {
        var audio = Wav();
        var transcript = "A rights-cleared fixture reference.";
        var audioSha = Convert.ToHexStringLower(SHA256.HashData(audio));
        var transcriptSha = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(transcript)));
        Span<byte> revision = stackalloc byte[64];
        Convert.FromHexString(audioSha).CopyTo(revision);
        Convert.FromHexString(transcriptSha).CopyTo(revision[32..]);
        return new
        {
            preset_id = System.Guid.NewGuid().ToString("D"),
            reference_revision = Convert.ToHexStringLower(
                SHA256.HashData(revision)),
            reference_audio_sha256 = audioSha,
            transcript,
            transcript_revision = transcriptSha,
            reference_audio_base64 = Convert.ToBase64String(audio),
            chunks = new[]
            {
                new
                {
                    index = 0,
                    chunk_id = "chunk-0",
                    text = "Fixture synthesis."
                }
            }
        };
    }

    private static object PerceptionPayload(bool includeQuestion)
    {
        var png = Png();
        var common = new Dictionary<string, object?>
        {
            ["frame_id"] = System.Guid.NewGuid().ToString("D"),
            ["capture_epoch"] = 1,
            ["selection_id"] = System.Guid.NewGuid().ToString("D"),
            ["source_revision"] = SourceRevision,
            ["capture_permission_revision"] = CapturePermissionRevision,
            ["captured_at_utc"] = new DateTimeOffset(
                2026, 9, 21, 20, 0, 0, TimeSpan.Zero)
                .ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["maximum_frame_age_milliseconds"] = 5_000,
            ["frame_sha256"] = Convert.ToHexStringLower(SHA256.HashData(png)),
            ["frame_byte_count"] = png.Length,
            ["width"] = 2,
            ["height"] = 2,
            ["frame_base64"] = Convert.ToBase64String(png)
        };
        if (includeQuestion)
            common["question"] = "What synthetic state is visible?";
        return common;
    }

    private static byte[] Wav()
    {
        const int sampleRate = 24_000;
        const int samples = sampleRate;
        var bytes = new byte[44 + samples * 2];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(4, 4),
            bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(24, 4),
            sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(28, 4),
            sampleRate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34, 2), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(40, 4),
            samples * 2);
        for (var index = 0; index < samples; index++)
            BinaryPrimitives.WriteInt16LittleEndian(
                bytes.AsSpan(44 + index * 2, 2),
                (short)((index % 127 + 1) * 32));
        return bytes;
    }

    private static byte[] Png()
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 2);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), 2);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR"u8, header);
        byte[] raw =
        [
            0, 32, 64, 96, 255, 33, 65, 96, 255,
            0, 34, 64, 96, 255, 35, 65, 96, 255
        ];
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(
            compressed,
            CompressionLevel.SmallestSize,
            leaveOpen: true))
        {
            zlib.Write(raw);
        }
        WriteChunk(output, "IDAT"u8, compressed.ToArray());
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(
        Stream output,
        ReadOnlySpan<byte> type,
        ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        output.Write(length);
        output.Write(type);
        output.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(type, data));
        output.Write(crc);
    }

    private static uint Crc32(
        ReadOnlySpan<byte> type,
        ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in type.ToArray().Concat(data.ToArray()))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) == 0
                    ? crc >> 1
                    : (crc >> 1) ^ 0xedb88320U;
        }
        return ~crc;
    }
}
