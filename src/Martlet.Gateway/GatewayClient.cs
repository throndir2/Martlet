using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using Martlet.Core.Contracts;
using Martlet.F5;
using Martlet.Providers;
using Martlet.Core.Settings;
using Martlet.Providers.Ollama;

namespace Martlet.Gateway;

public sealed class GatewayRemoteException : Exception
{
    public string Code { get; }

    internal GatewayRemoteException(string code)
        : base("The pinned gateway refused the bounded request.")
    {
        Code = code;
    }
}

public sealed record GatewayCapabilityReport
{
    public required GatewayProtocolVersion ProtocolVersion { get; init; }
    public required string HostId { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required GatewayRole AuthorizedRole { get; init; }
    public required string RegistryId { get; init; }
    public required string RegistryVersion { get; init; }
    public required IReadOnlyList<GatewayWorkerCapabilities> Workers { get; init; }
    public required IReadOnlyList<GatewayInferenceRouteCapability> Routes { get; init; }
}

public static class GatewayPairingClient
{
    public static async Task<ScopedGatewayCredential> PairAsync(
        GatewayOrigin origin,
        GatewayHostIdentity identity,
        string pairingId,
        string deviceId,
        SecretLease pairingToken,
        CancellationToken cancellationToken = default)
    {
        using var client = PinnedGatewayClient.Create(origin, identity);
        return await PairAsync(client, origin, identity, pairingId, deviceId, pairingToken,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<ScopedGatewayCredential> PairAsync(
        PinnedGatewayClient client, GatewayOrigin origin, GatewayHostIdentity identity,
        string pairingId, string deviceId, SecretLease pairingToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(origin);
        identity.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = [];
        pairingToken.Use(token =>
        {
            var proof = new GatewayPairingProof
            {
                ProtocolVersion = GatewayProtocolVersion.Current,
                PairingId = pairingId, DeviceId = deviceId,
                HostId = identity.HostId, SpkiFingerprint = identity.SpkiFingerprint,
                PairingToken = new string(token)
            };
            proof.Validate();
            bytes = JsonSerializer.SerializeToUtf8Bytes(proof, GatewayClientJson.Options);
        });
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                origin.CanonicalOrigin + "/martlet/v1/pair")
            {
                Content = GatewayClientJson.Content(bytes)
            };
            using var response = await client.SendAsync(request, linked.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Created)
                throw await GatewayClientJson.FailureAsync(response, linked.Token).ConfigureAwait(false);
            var document = await GatewayClientJson.ReadAsync<PairingResponse>(
                response, GatewayHttpApplication.MaximumPairingRequestBytes, linked.Token).ConfigureAwait(false);
            try { GatewayClientJson.Protocol(document.ProtocolVersion); }
            catch (GatewayProtocolException) { throw new GatewayProtocolException("response.invalid"); }
            GatewayRules.Require(document.HostId == identity.HostId &&
                document.DeviceId == deviceId &&
                document.Roles.SequenceEqual([GatewayRole.Voice]) &&
                document.Lifetime is PairedDeviceLifetime,
                "response.invalid");
            cancellationToken.ThrowIfCancellationRequested();
            return ScopedGatewayCredential.Restore(origin, identity, document.ProtocolVersion,
                document.CredentialId, document.DeviceId, GatewayRole.Voice,
                document.Lifetime, document.CredentialSecret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record PairingResponse
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required string CredentialId { get; init; }
        public required string CredentialSecret { get; init; }
        public required string DeviceId { get; init; }
        public required GatewayRole[] Roles { get; init; }
        public required GatewayCredentialLifetime Lifetime { get; init; }
    }
}

public sealed class GatewayAuthenticatedClient : IDisposable
{
    private readonly PinnedGatewayClient client;
    private readonly GatewayRequestSigner signer;
    private readonly GatewayOrigin origin;
    private readonly GatewayHostIdentity identity;
    private readonly TimeProvider clock;
    private readonly bool ownsClient;
    private int disposed;

    private GatewayAuthenticatedClient(PinnedGatewayClient client, GatewayRequestSigner signer,
        GatewayOrigin origin, GatewayHostIdentity identity, TimeProvider clock, bool ownsClient)
    {
        this.client = client;
        this.signer = signer;
        this.origin = origin;
        this.identity = identity;
        this.clock = clock;
        this.ownsClient = ownsClient;
    }

    public static GatewayAuthenticatedClient Create(
        ScopedGatewayCredential credential,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var selectedClock = clock ?? TimeProvider.System;
        var signer = credential.CreateSigner(selectedClock);
        try
        {
            var client = PinnedGatewayClient.Create(credential.Origin, credential.Identity, selectedClock);
            return new(client, signer, credential.Origin, credential.Identity, selectedClock, ownsClient: true);
        }
        catch { signer.Dispose(); throw; }
    }

    internal static GatewayAuthenticatedClient CreateForFixture(
        PinnedGatewayClient client,
        ScopedGatewayCredential credential,
        TimeProvider? clock = null) =>
        new(client, credential.CreateSigner(clock), credential.Origin, credential.Identity,
            clock ?? TimeProvider.System, ownsClient: false);

    public async Task<GatewayCapabilityReport> ReadCapabilitiesAsync(
        GatewayRole role,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        GatewayRules.Require(role == GatewayRole.Voice, "auth.role");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var request = SignedGet("/martlet/v1/capabilities", role);
        using var response = await client.SendAsync(request, linked.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw await GatewayClientJson.FailureAsync(response, linked.Token).ConfigureAwait(false);
        var document = await GatewayClientJson.ReadAsync<CapabilitiesResponse>(
            response, GatewayRules.MaximumResponseBytes, linked.Token).ConfigureAwait(false);
        GatewayClientJson.Protocol(document.ProtocolVersion);
        GatewayRules.Require(document.HostId == identity.HostId &&
            document.AuthorizedRole == role &&
            document.RegistryId == GatewayInferenceProtocol.RegistryId &&
            document.RegistryVersion == GatewayInferenceProtocol.RegistryVersion &&
            document.Routes.Length <= GatewayInferenceProtocol.MaximumRoutes &&
            document.Workers.Length <= GatewayInferenceProtocol.MaximumRoutes &&
            document.GeneratedAt.Offset == TimeSpan.Zero,
            "worker.invalid");
        foreach (var route in document.Routes)
            _ = GatewayInferenceRoute.FromCapability(route);
        foreach (var worker in document.Workers)
        {
            worker.Validate();
            GatewayRules.Require(worker.RequiredRole == role, "auth.role");
        }
        return new()
        {
            ProtocolVersion = document.ProtocolVersion,
            HostId = document.HostId,
            GeneratedAt = document.GeneratedAt,
            AuthorizedRole = document.AuthorizedRole,
            RegistryId = document.RegistryId,
            RegistryVersion = document.RegistryVersion,
            Workers = Array.AsReadOnly(document.Workers),
            Routes = Array.AsReadOnly(document.Routes)
        };
    }

    public IAsyncEnumerable<GatewayInferenceEvent> StreamOllamaAsync(
        GatewayInferenceRouteCapability capability,
        CorrelationIds ids,
        long epoch,
        DateTimeOffset deadlineUtc,
        BoundedTextInput input,
        TextGenerationLimits limits,
        double temperature,
        CancellationToken cancellationToken = default,
        Martlet.Core.Settings.GenerationSettings? sampling = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        sampling?.Validate();
        var route = GatewayInferenceRoute.FromCapability(capability);
        GatewayRules.Require(route.Kind == GatewayInferenceKind.OllamaChat &&
            input.Utf8Bytes <= route.MaximumInputBytes &&
            double.IsFinite(temperature) && temperature is >= 0 and <= 2,
            "request.invalid");
        return StreamAsync(new(
            route,
            ids.SessionId,
            ids.TurnId,
            ids.RequestId,
            null,
            epoch,
            deadlineUtc,
            new GatewayOllamaChatPayload(
                input.UserText,
                temperature,
                limits.MaxOutputTokens,
                limits.MaxContextTokens,
                input.PersonalityWithNotes,
                input.History,
                input.Image is { } image ? [image.ToBase64()] : null,
                GatewayOllamaSampling.From(sampling))), cancellationToken);
    }

    public IAsyncEnumerable<GatewayInferenceEvent> StreamF5Async(
        GatewayInferenceRouteCapability capability,
        F5RequestIds ids,
        long epoch,
        DateTimeOffset deadlineUtc,
        F5ReferenceInput reference,
        IReadOnlyList<F5TextChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(chunks);
        var route = GatewayInferenceRoute.FromCapability(capability);
        GatewayRules.Require(route.Kind == GatewayInferenceKind.F5Synthesis,
            "request.invalid");
        var payload = new GatewayF5SynthesisPayload(
            reference.PresetId,
            reference.ReferenceRevision,
            reference.AudioSha256,
            reference.Transcript,
            reference.TranscriptRevision,
            reference.Audio.ToArray(),
            chunks.Select(chunk => new GatewayF5TextChunk(
                chunk.Index, chunk.ChunkId, chunk.Text)).ToArray());
        return StreamAsync(new(
            route,
            ids.SessionId,
            ids.TurnId,
            ids.RequestId,
            ids.ParentRequestId,
            epoch,
            deadlineUtc,
            payload), cancellationToken);
    }

    public IAsyncEnumerable<GatewayInferenceEvent> StreamAudio2FaceAsync(
        GatewayInferenceRouteCapability capability,
        CorrelationIds ids,
        long epoch,
        DateTimeOffset deadlineUtc,
        int sampleRate,
        ReadOnlyMemory<byte> pcm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var route = GatewayInferenceRoute.FromCapability(capability);
        GatewayRules.Require(route.Kind == GatewayInferenceKind.Audio2Face &&
            sampleRate is 16_000 or 24_000 or 44_100 or 48_000 &&
            pcm.Length is > 0 && pcm.Length % 2 == 0 && pcm.Length <= route.MaximumInputBytes,
            "request.invalid");
        return StreamAsync(new(
            route,
            ids.SessionId,
            ids.TurnId,
            ids.RequestId,
            null,
            epoch,
            deadlineUtc,
            new GatewayAudio2FacePayload(sampleRate, pcm.ToArray())), cancellationToken);
    }

    /// <summary>Transcribes one 16 kHz mono PCM16 utterance on the host; text events carry the final transcript.</summary>
    public IAsyncEnumerable<GatewayInferenceEvent> StreamTranscriptionAsync(
        GatewayInferenceRouteCapability capability,
        CorrelationIds ids,
        long epoch,
        DateTimeOffset deadlineUtc,
        ReadOnlyMemory<byte> pcm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var route = GatewayInferenceRoute.FromCapability(capability);
        GatewayRules.Require(route.Kind == GatewayInferenceKind.Transcription &&
            pcm.Length is > 0 && pcm.Length % 2 == 0 && pcm.Length <= route.MaximumInputBytes,
            "request.invalid");
        return StreamAsync(new(
            route,
            ids.SessionId,
            ids.TurnId,
            ids.RequestId,
            null,
            epoch,
            deadlineUtc,
            new GatewayTranscriptionPayload(GatewayInferenceRoute.TranscriptionSampleRate, pcm.ToArray())), cancellationToken);
    }

    internal async IAsyncEnumerable<GatewayInferenceEvent> StreamAsync(
        GatewayInferenceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureOpen();
        GatewayRules.Require(request.Route.RequiredRole == GatewayRole.Voice,
            "auth.role");
        var bytes = GatewayClientJson.Request(request);
        try
        {
            var verified = GatewayInferenceJson.ParseRequest(
                bytes, request.Route, clock.GetUtcNow());
            verified.Payload.Clear();
            var remaining = request.DeadlineUtc - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                throw new GatewayClientException("gateway.deadline");
            using var deadline = new CancellationTokenSource(remaining, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, deadline.Token);
            var ioToken = linked.Token;
            var startedAt = clock.GetTimestamp();
            void CheckActive()
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureOpen();
                if (deadline.IsCancellationRequested || clock.GetUtcNow() >= request.DeadlineUtc ||
                    clock.GetElapsedTime(startedAt) >= remaining)
                    throw new GatewayClientException("gateway.deadline");
            }
            HttpResponseMessage response;
            using (var message = new HttpRequestMessage(HttpMethod.Post,
                origin.CanonicalOrigin + request.Route.Path)
            {
                Content = GatewayClientJson.Content(bytes)
            })
            {
                signer.Sign(message, request.Route.RequiredRole, bytes);
                response = await WithDeadlineAsync(
                    client.SendAsync(message, ioToken),
                    deadline,
                    cancellationToken).ConfigureAwait(false);
            }
            using (response)
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw await WithDeadlineAsync(
                        GatewayClientJson.FailureAsync(response, ioToken),
                        deadline,
                        cancellationToken).ConfigureAwait(false);
                GatewayRules.Require(response.Content.Headers.ContentType?.MediaType == "application/x-ndjson" &&
                    response.Content.Headers.ContentEncoding.Count == 0 &&
                    (response.Content.Headers.ContentLength is null ||
                        response.Content.Headers.ContentLength <= request.Route.MaximumStreamBytes),
                    "stream.invalid");
                await using var stream = await WithDeadlineAsync(
                    response.Content.ReadAsStreamAsync(ioToken),
                    deadline,
                    cancellationToken).ConfigureAwait(false);
                using var reader = new BoundedNdjsonReader(
                    stream,
                    request.Route.MaximumEventBytes,
                    request.Route.MaximumStreamBytes);
                var expected = 0L;
                var state = new GatewayInferenceEventValidator.GatewayInferenceStreamState(request, identity.HostId, clock);
                while (true)
                {
                    CheckActive();
                    var line = await WithDeadlineAsync(
                        reader.ReadLineAsync(ioToken),
                        deadline,
                        cancellationToken).ConfigureAwait(false);
                    if (line is null)
                        break;
                    GatewayInferenceEvent item;
                    try
                    {
                        item = GatewayClientJson.Event(line, request);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(line);
                    }
                    GatewayInferenceEventValidator.Validate(item, request, expected++);
                    state.Accept(item);
                    GatewayRules.Require(expected <= request.Route.MaximumEvents,
                        "stream.limit");
                    if (item.IsTerminal)
                    {
                        var late = await WithDeadlineAsync(
                            reader.ReadLineAsync(ioToken),
                            deadline,
                            cancellationToken).ConfigureAwait(false);
                        if (late is not null)
                        {
                            CryptographicOperations.ZeroMemory(late);
                            throw new GatewayProtocolException("stream.late");
                        }
                        CheckActive();
                        yield return item;
                        yield break;
                    }
                    CheckActive();
                    yield return item;
                }
                throw new GatewayProtocolException("stream.truncated");
            }
        }

        finally
        {
            request.Payload.Clear();
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static async Task<T> WithDeadlineAsync<T>(
        Task<T> task,
        CancellationTokenSource deadline,
        CancellationToken caller)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested && !caller.IsCancellationRequested)
        {
            throw new GatewayClientException("gateway.deadline");
        }
    }

    private static async Task<T> WithDeadlineAsync<T>(
        ValueTask<T> task,
        CancellationTokenSource deadline,
        CancellationToken caller)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested && !caller.IsCancellationRequested)
        {
            throw new GatewayClientException("gateway.deadline");
        }
    }

    private HttpRequestMessage SignedGet(string path, GatewayRole role)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, origin.CanonicalOrigin + path);
        signer.Sign(request, role);
        return request;
    }

    private void EnsureOpen() => ObjectDisposedException.ThrowIf(disposed != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        signer.Dispose();
        if (ownsClient)
            client.Dispose();
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record CapabilitiesResponse
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required DateTimeOffset GeneratedAt { get; init; }
        public required GatewayRole AuthorizedRole { get; init; }
        public required string RegistryId { get; init; }
        public required string RegistryVersion { get; init; }
        public required GatewayWorkerCapabilities[] Workers { get; init; }
        public required GatewayInferenceRouteCapability[] Routes { get; init; }
    }

    private sealed class BoundedNdjsonReader : IDisposable
    {
        private readonly Stream stream;
        private readonly byte[] input = new byte[4096];
        private readonly byte[] line;
        private readonly int maximumStreamBytes;
        private int inputOffset;
        private int inputCount;
        private int observedBytes;
        private bool disposed;

        internal BoundedNdjsonReader(
            Stream stream,
            int maximumEventBytes,
            int maximumStreamBytes)
        {
            this.stream = stream;
            line = new byte[maximumEventBytes];
            maximumStreamBytes = Math.Max(maximumStreamBytes, maximumEventBytes);
            this.maximumStreamBytes = maximumStreamBytes;
        }

        internal async Task<byte[]?> ReadLineAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var length = 0;
            while (true)
            {
                if (inputOffset == inputCount)
                {
                    inputCount = await stream.ReadAsync(input, cancellationToken)
                        .ConfigureAwait(false);
                    inputOffset = 0;
                    if (inputCount == 0)
                    {
                        if (length != 0)
                            throw new GatewayProtocolException("stream.truncated");
                        return null;
                    }
                }

                var value = input[inputOffset++];
                observedBytes = checked(observedBytes + 1);
                if (observedBytes > maximumStreamBytes)
                    throw new GatewayProtocolException("stream.limit");
                if (value == (byte)'\n')
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (length == 0)
                        throw new GatewayProtocolException("stream.invalid");
                    return line.AsSpan(0, length).ToArray();
                }
                if (length == line.Length)
                    throw new GatewayProtocolException("stream.limit");
                line[length++] = value;
            }
        }

        public void Dispose()
        {
            disposed = true;
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(line);
        }
    }
}

internal static class GatewayClientJson
{
    internal static JsonSerializerOptions Options { get; } = CreateOptions();

    internal static ByteArrayContent Content(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };
        return content;
    }

    // Optional persona and history are omitted when empty so single-message requests keep their original shape.
    private static Dictionary<string, object> OllamaPayload(GatewayOllamaChatPayload ollama)
    {
        var payload = new Dictionary<string, object>
        {
            ["input"] = ollama.Input,
            ["temperature"] = ollama.Temperature,
            ["maximum_output_tokens"] = ollama.MaximumOutputTokens,
            ["maximum_context_tokens"] = ollama.MaximumContextTokens
        };
        if (ollama.System is { } system) payload["system"] = system;
        if (ollama.History.Count > 0)
            payload["history"] = ollama.History.Select(message => new Dictionary<string, string>
            {
                ["role"] = message.Role == TextHistoryRole.User ? "user" : "assistant",
                ["text"] = message.Text
            }).ToArray();
        if (ollama.Images.Count > 0) payload["images"] = ollama.Images.ToArray();
        var sampling = ollama.Sampling;
        if (sampling.TopP is { } topP) payload["top_p"] = topP;
        if (sampling.TopK is { } topK) payload["top_k"] = topK;
        if (sampling.MinP is { } minP) payload["min_p"] = minP;
        if (sampling.RepeatPenalty is { } repeat) payload["repeat_penalty"] = repeat;
        if (sampling.FrequencyPenalty is { } frequency) payload["frequency_penalty"] = frequency;
        if (sampling.PresencePenalty is { } presence) payload["presence_penalty"] = presence;
        if (sampling.ContextTokens is { } context) payload["context_tokens"] = context;
        if (sampling.Think is { } think) payload["think"] = think;
        return payload;
    }

    private static Dictionary<string, object> SongPayload(GatewaySongPayload song)
    {
        var payload = new Dictionary<string, object>();
        switch (song.Operation)
        {
            case GatewaySongOperation.Start:
                var start = song.Start ?? throw new GatewayProtocolException("request.invalid");
                payload["operation"] = "start";
                payload["lyrics"] = start.Lyrics;
                payload["style"] = start.Style;
                payload["duration_seconds"] = start.DurationSeconds;
                payload["language"] = start.Language;
                payload["quality"] = start.Quality;
                payload["voice_match"] = start.VoiceMatch;
                payload["voice_id"] = start.VoiceId;
                if (start.Bpm is { } bpm) payload["bpm"] = bpm;
                if (start.Key is { } key) payload["key"] = key;
                if (start.Seed is { } seed) payload["seed"] = seed;
                if (!song.ReferenceAudio.IsEmpty) payload["reference_audio_base64"] = Convert.ToBase64String(song.ReferenceAudio.Span);
                break;
            case GatewaySongOperation.Result:
                payload["operation"] = "result";
                payload["job_id"] = song.JobId!;
                payload["track"] = song.Track!;
                payload["offset_frames"] = song.OffsetFrames;
                payload["maximum_frames"] = song.MaximumFrames;
                break;
            default:
                payload["operation"] = song.Operation == GatewaySongOperation.Status ? "status" : "cancel";
                if (song.JobId is { } jobId) payload["job_id"] = jobId;
                break;
        }
        return payload;
    }

    private static Dictionary<string, object?> PicturePayload(GatewayPicturePayload picture)
    {
        var payload = new Dictionary<string, object?>
        {
            ["operation"] = picture.Operation switch
            {
                GatewayPictureOperation.Status => "status",
                GatewayPictureOperation.Prompt => "prompt",
                GatewayPictureOperation.History => "history",
                GatewayPictureOperation.Queue => "queue",
                GatewayPictureOperation.View => "view",
                GatewayPictureOperation.Free => "free",
                _ => "cancel"
            }
        };
        switch (picture.Operation)
        {
            case GatewayPictureOperation.Prompt:
                payload["prompt"] = picture.Prompt ?? throw new GatewayProtocolException("request.invalid");
                break;
            case GatewayPictureOperation.History:
            case GatewayPictureOperation.Cancel:
                payload["prompt_id"] = picture.PromptId;
                break;
            case GatewayPictureOperation.View:
                payload["filename"] = picture.Filename;
                payload["subfolder"] = picture.Subfolder ?? "";
                payload["type"] = picture.ImageType;
                payload["offset"] = picture.Offset;
                payload["maximum"] = picture.Maximum;
                break;
        }
        return payload;
    }

    private static Dictionary<string, object?> OcrPayload(GatewayOcrPayload ocr)
    {
        var payload = new Dictionary<string, object?>
        {
            ["operation"] = ocr.Operation == GatewayOcrOperation.Status ? "status" : "read"
        };
        if (ocr.Operation == GatewayOcrOperation.Read)
        {
            payload["media_type"] = ocr.MediaType;
            payload["image_base64"] = Convert.ToBase64String(ocr.Image.Span);
        }
        return payload;
    }

    internal static byte[] Request(GatewayInferenceRequest request)
    {
        object payload = request.Payload switch
        {
            GatewayOllamaChatPayload ollama => OllamaPayload(ollama),
            GatewayF5SynthesisPayload f5 => new
            {
                preset_id = f5.PresetId,
                reference_revision = f5.ReferenceRevision,
                reference_audio_sha256 = f5.ReferenceAudioSha256,
                transcript = f5.Transcript,
                transcript_revision = f5.TranscriptRevision,
                reference_audio_base64 = Convert.ToBase64String(f5.ReferenceAudio.Span),
                chunks = f5.Chunks.Select(chunk => new
                {
                    index = chunk.Index,
                    chunk_id = chunk.ChunkId,
                    text = chunk.Text
                }).ToArray()
            },
            GatewayAudio2FacePayload face => new
            {
                sample_rate = face.SampleRate,
                pcm_base64 = Convert.ToBase64String(face.Pcm.Span)
            },
            GatewayTranscriptionPayload speech => new
            {
                sample_rate = speech.SampleRate,
                pcm_base64 = Convert.ToBase64String(speech.Pcm.Span)
            },
            GatewaySongPayload song => SongPayload(song),
            GatewayPicturePayload picture => PicturePayload(picture),
            GatewayOcrPayload ocr => OcrPayload(ocr),
            _ => throw new GatewayProtocolException("request.invalid")
        };
        var document = new Dictionary<string, object?>
        {
            ["protocol_version"] = request.ProtocolVersion,
            ["route_id"] = request.Route.RouteId,
            ["contract_id"] = request.Route.ContractId,
            ["contract_version"] = request.Route.ContractVersion,
            ["destination_id"] = request.Route.DestinationId,
            ["worker_id"] = request.Route.WorkerId,
            ["adapter_version"] = request.Route.AdapterVersion,
            ["model_id"] = request.Route.ModelId,
            ["model_revision"] = request.Route.ModelRevision,
            ["model_sha256"] = request.Route.ModelSha256,
            ["artifact_identity_sha256"] = request.Route.ArtifactIdentitySha256,
            ["session_id"] = request.SessionId,
            ["turn_id"] = request.TurnId,
            ["request_id"] = request.RequestId,
            ["epoch"] = request.Epoch,
            ["deadline_utc"] = request.DeadlineUtc.ToString(
                "O", CultureInfo.InvariantCulture),
            ["payload"] = payload
        };
        if (request.ParentRequestId is { } parentRequestId)
            document["parent_request_id"] = parentRequestId;
        return JsonSerializer.SerializeToUtf8Bytes(document, Options);
    }

    internal static GatewayInferenceEvent Event(ReadOnlyMemory<byte> line, GatewayInferenceRequest request)
    {
        InferenceEventDocument document;
        try
        {
            InspectJson(line);
            document = JsonSerializer.Deserialize<InferenceEventDocument>(line.Span, Options) ??
                throw new JsonException();
        }
        catch (JsonException)
        {
            throw new GatewayProtocolException("stream.invalid");
        }
        Protocol(document.ProtocolVersion);
        GatewayRules.Require(document.RegistryVersion == GatewayInferenceProtocol.RegistryVersion,
            "protocol.unsupported");
        GatewayRules.Require(document.TraceId != Guid.Empty, "stream.invalid");
        switch (document.Type)
        {
            case GatewayInferenceEventKind.TextDelta:
                GatewayRules.Require(document.Text is not null &&
                    document.DataBase64 is null && document.DataMediaType is null,
                    "stream.invalid");
                break;
            case GatewayInferenceEventKind.AudioFrame:
                GatewayRules.Require(document.Text is null &&
                    document.DataBase64 is not null &&
                    document.DataMediaType == "audio/L16;rate=24000;channels=1",
                    "stream.invalid");
                break;
            case GatewayInferenceEventKind.Observation:
            case GatewayInferenceEventKind.FaceFrame:
                GatewayRules.Require(document.Text is null &&
                    document.DataBase64 is not null &&
                    document.DataMediaType == "application/json",
                    "stream.invalid");
                break;
            default:
                GatewayRules.Require(document.Text is null &&
                    document.DataBase64 is null && document.DataMediaType is null,
                    "stream.invalid");
                break;
        }
        GatewayRules.Require(document.Type == GatewayInferenceEventKind.Failed
            ? document.Code is not null && document.Summary is not null && document.Remedy is not null
            : document.Code is null && document.Summary is null && document.Remedy is null,
            "stream.invalid");
        byte[] payload = [];
        if (document.Text is not null)
            payload = new UTF8Encoding(false, true).GetBytes(document.Text);
        else if (document.DataBase64 is not null)
        {
            try
            {
                payload = Convert.FromBase64String(document.DataBase64);
                GatewayRules.Require(Convert.ToBase64String(payload) == document.DataBase64,
                    "stream.invalid");
            }
            catch (FormatException)
            {
                throw new GatewayProtocolException("stream.invalid");
            }
        }
        var item = new GatewayInferenceEvent(
            document.Type,
            document.Sequence,
            document.RouteId,
            document.DestinationId,
            document.WorkerId,
            document.ModelId,
            document.ModelRevision,
            document.ModelSha256,
            document.ArtifactIdentitySha256,
            document.SessionId,
            document.TurnId,
            document.RequestId,
            document.Epoch,
            payload,
            document.Code,
            document.FrameSequence,
            document.ChunkIndex,
            document.SampleOffset,
            document.SampleCount,
            document.FinalSampleCount);
        CryptographicOperations.ZeroMemory(payload);
        return item;
    }

    internal static void Protocol(GatewayProtocolVersion protocol)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        protocol.Validate();
        GatewayRules.Require(protocol.Major == GatewayProtocolVersion.Current.Major,
            "protocol.unsupported");
    }

    internal static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        int maximum,
        CancellationToken cancellationToken) where T : class
    {
        GatewayRules.Require(response.Content.Headers.ContentType?.MediaType == "application/json" &&
            response.Content.Headers.ContentEncoding.Count == 0 &&
            (response.Content.Headers.ContentLength is null or >= 1) &&
            (response.Content.Headers.ContentLength is null ||
                response.Content.Headers.ContentLength <= maximum),
            "response.invalid");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        byte[] bytes = [];
        try
        {
            while (true)
            {
                var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                GatewayRules.Require(output.Length + count <= maximum, "response.invalid");
                output.Write(buffer, 0, count);
            }
            bytes = output.ToArray();
            try
            {
                InspectJson(bytes);
                return JsonSerializer.Deserialize<T>(bytes, Options) ??
                    throw new GatewayProtocolException("response.invalid");
            }
            catch (JsonException)
            {
                throw new GatewayProtocolException("response.invalid");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(buffer);
            if (output.TryGetBuffer(out var segment))
                CryptographicOperations.ZeroMemory(segment.AsSpan(0, checked((int)output.Length)));
        }
    }

    internal static async Task<GatewayRemoteException> FailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var failure = await ReadAsync<FailureDocument>(
                response, GatewayRules.MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
            Protocol(failure.ProtocolVersion);
            GatewayRules.Identifier(failure.Code, 96);
            try { _ = GatewayFailures.Get(failure.Code); }
            catch (ArgumentException) { return new("response.invalid"); }
            return new(failure.Code);
        }
        catch (GatewayProtocolException)
        {
            return new("response.invalid");
        }
    }

    private static void InspectJson(ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        Inspect(document.RootElement);
        static void Inspect(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new JsonException();
                    Inspect(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) Inspect(item);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 12
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record InferenceEventDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string RegistryVersion { get; init; }
        public required GatewayInferenceEventKind Type { get; init; }
        public required long Sequence { get; init; }
        public required string RouteId { get; init; }
        public required string DestinationId { get; init; }
        public required string WorkerId { get; init; }
        public required string ModelId { get; init; }
        public required string ModelRevision { get; init; }
        public required string ModelSha256 { get; init; }
        public required string ArtifactIdentitySha256 { get; init; }
        public required Guid SessionId { get; init; }
        public required Guid TurnId { get; init; }
        public required Guid RequestId { get; init; }
        public required long Epoch { get; init; }
        public string? Text { get; init; }
        public string? DataBase64 { get; init; }
        public string? DataMediaType { get; init; }
        public long? FrameSequence { get; init; }
        public int? ChunkIndex { get; init; }
        public long? SampleOffset { get; init; }
        public int? SampleCount { get; init; }
        public long? FinalSampleCount { get; init; }
        public string? Code { get; init; }
        public string? Summary { get; init; }
        public string? Remedy { get; init; }
        public required Guid TraceId { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record FailureDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string Code { get; init; }
        public required string Summary { get; init; }
        public required string Remedy { get; init; }
        /// <summary>Optional, from hosts with GPU priority ("live" on a pool-lane job.busy).</summary>
        public string? Detail { get; init; }
        public required Guid TraceId { get; init; }
    }
}
