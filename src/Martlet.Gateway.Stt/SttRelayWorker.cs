using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Martlet.Gateway.Stt;

/// <summary>
/// Gateway relay from a paired client to this host's own loopback speech-to-text server (the <c>stt</c> host role): whisper.cpp,
/// or Martlet's Parakeet service (workers/parakeet), which answers the same <c>/inference</c> contract.
/// Enabling it in the host configuration is the host owner's standing permission for paired <c>voice</c> devices to
/// send one bounded microphone utterance at a time for transcription by the model the owner installed; nothing else
/// on the host is reachable through it. The utterance is held in memory only and never written to disk.
/// </summary>
public sealed partial class SttRelayWorker : ITranscriptionGatewayInferenceWorker, IAsyncDisposable
{
    public const string DefaultDestinationId = "stt-host";
    public const string DefaultWorkerId = "whisper-relay";
    /// <summary>The engine release a whisper model runs on.</summary>
    public const string ModelRevision = "whisper.cpp-1.9.4";
    /// <summary>The engine release a Parakeet model (<c>parakeet-...</c>) runs on.</summary>
    public const string ParakeetModelRevision = "sherpa-onnx-1.13.8";
    private const int MaximumResponseBytes = 256 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, false);
    private readonly Uri inference;
    private readonly HttpClient http;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> running = new();

    public SttRelayWorker(Uri endpoint, string model,
        string destinationId = DefaultDestinationId, string workerId = DefaultWorkerId, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) ||
            !IPAddress.IsLoopback(address) || endpoint.AbsolutePath != "/")
            throw new ArgumentException("The speech-to-text relay only reaches a loopback http://127.0.0.1:<port>/ server.", nameof(endpoint));
        if (model is not { Length: > 0 and <= 64 } || !ModelPattern().IsMatch(model))
            throw new ArgumentException("Use a speech-to-text model name such as small, large-v3-turbo or parakeet-tdt-110m-en.", nameof(model));
        inference = new Uri(endpoint, "inference");
        Route = GatewayInferenceRoute.Transcription(destinationId, workerId, model, RevisionFor(model));
        http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false, Credentials = null,
            AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(5)
        }, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public GatewayInferenceRoute Route { get; }

    /// <summary>The engine release <paramref name="model"/> runs on: sherpa-onnx for a Parakeet model, else whisper.cpp.</summary>
    public static string RevisionFor(string model) =>
        model.StartsWith("parakeet-", StringComparison.Ordinal) ? ParakeetModelRevision : ModelRevision;

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9.-]*\z")]
    private static partial Regex ModelPattern();

    // whisper.cpp marks non-speech with bracketed tags such as [BLANK_AUDIO]; a lone "(music)" is not speech either.
    [GeneratedRegex(@"\[[^\]]{0,64}\]")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\A\s*[\(\*][^\)\*]{0,64}[\)\*]\s*\z")]
    private static partial Regex SoundOnly();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public ValueTask<GatewayInferencePermissionLease> AcquirePermissionAsync(
        GatewayInferenceRequest request, GatewayPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(principal);
        cancellationToken.ThrowIfCancellationRequested();
        if (lifetime.IsCancellationRequested)
            throw new GatewayInferenceWorkerException(GatewayInferenceWorkerFailure.Unavailable);
        if (!ReferenceEquals(request.Route, Route) || principal.Role != GatewayRole.Voice ||
            request.Payload is not GatewayTranscriptionPayload)
            throw new GatewayInferenceWorkerException(GatewayInferenceWorkerFailure.PermissionDenied);
        return ValueTask.FromResult<GatewayInferencePermissionLease>(new Lease(request, principal, this));
    }

    public async IAsyncEnumerable<GatewayInferenceEvent> ExecuteAsync(
        GatewayInferenceRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = (GatewayTranscriptionPayload)request.Payload;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        running[request.RequestId] = stop;
        try
        {
            yield return Event(request, GatewayInferenceEventKind.Started, 0);
            var (text, failure) = await TranscribeAsync(payload, stop.Token).ConfigureAwait(false);
            stop.Token.ThrowIfCancellationRequested();
            if (failure is not null)
            {
                yield return Event(request, GatewayInferenceEventKind.Failed, 1, errorCode: failure);
                yield break;
            }
            var sequence = 1L;
            if (text.Length > 0)
                yield return Event(request, GatewayInferenceEventKind.TextDelta, sequence++, Utf8.GetBytes(text));
            yield return Event(request, GatewayInferenceEventKind.Completed, sequence);
        }
        finally
        {
            running.TryRemove(request.RequestId, out _);
        }
    }

    private async Task<(string Text, string? Failure)> TranscribeAsync(GatewayTranscriptionPayload payload, CancellationToken token)
    {
        var wave = Wave(payload.SampleRate, payload.Pcm.Span);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, inference);
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(wave);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", "utterance.wav");
            form.Add(new StringContent("0.0"), "temperature");
            form.Add(new StringContent("json"), "response_format");
            request.Content = form;
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                token.ThrowIfCancellationRequested();
                return ("", "worker.unavailable");
            }
            using (response)
            {
                // 503 is whisper.cpp still loading its model.
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable) return ("", "worker.unavailable");
                if (response.StatusCode != HttpStatusCode.OK) return ("", "worker.failed");
                byte[] body;
                try { body = await ReadBoundedAsync(response.Content, token).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or HttpRequestException or InvalidDataException)
                {
                    token.ThrowIfCancellationRequested();
                    return ("", "worker.failed");
                }
                return Parse(body) is { } text ? (Clean(text), null) : ("", "worker.failed");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wave);
        }
    }

    private static string? Parse(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("error", out _) &&
                root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The transcript without non-speech tags, on one line, within the route's output bound.</summary>
    internal static string Clean(string text)
    {
        var cleaned = Spaces().Replace(Tags().Replace(text, " "), " ").Trim();
        if (SoundOnly().IsMatch(cleaned)) return "";
        var cleanedText = new string(cleaned.Where(c => !char.IsControl(c)).ToArray());
        while (Utf8.GetByteCount(cleanedText) > GatewayInferenceRoute.TranscriptionMaximumTextBytes)
            cleanedText = cleanedText[..^1];
        return cleanedText;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaximumResponseBytes) throw new InvalidDataException("The transcription response is too large.");
        }
        return buffer.ToArray();
    }

    /// <summary>A canonical mono PCM16 WAV around the utterance, as whisper.cpp reads it.</summary>
    internal static byte[] Wave(int sampleRate, ReadOnlySpan<byte> pcm)
    {
        var wave = new byte[44 + pcm.Length];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4), (uint)(wave.Length - 8));
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(28), (uint)(sampleRate * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34), 16);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40), (uint)pcm.Length);
        pcm.CopyTo(wave.AsSpan(44));
        return wave;
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
        http.Dispose();
        return ValueTask.CompletedTask;
    }

    private GatewayInferenceEvent Event(GatewayInferenceRequest request, GatewayInferenceEventKind kind, long sequence,
        ReadOnlyMemory<byte> payload = default, string? errorCode = null) =>
        new(kind, sequence, Route.RouteId, Route.DestinationId, Route.WorkerId, Route.ModelId, Route.ModelRevision,
            Route.ModelSha256, Route.ArtifactIdentitySha256, request.SessionId, request.TurnId, request.RequestId,
            request.Epoch, payload, errorCode);

    private sealed class Lease(GatewayInferenceRequest request, GatewayPrincipal principal, SttRelayWorker owner)
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
