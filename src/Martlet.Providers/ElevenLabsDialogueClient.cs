using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Martlet.Core.Settings;

namespace Martlet.Providers;

/// <summary>How long the last ElevenLabs segment took: connecting (TCP, TLS and the WebSocket upgrade), from connecting to
/// the first audio, and in all, with the audio bytes it returned.</summary>
public sealed record ElevenLabsSegmentTimings(double ConnectMs, double FirstAudioMs, double TotalMs, long AudioBytes);

/// <summary>Speaks reply segments over ElevenLabs' Text to Dialogue WebSocket (<c>wss://api.elevenlabs.io/v1/text-to-dialogue/stream-input</c>),
/// exactly as its documentation describes the protocol (read 2026-10-07): the key in the <c>xi-api-key</c> header,
/// <c>model_id</c> and <c>output_format=pcm_24000</c> in the query, a first message that registers the one voice
/// (<c>{"voices":[id]}</c>), the segment as one <c>inputs</c> entry, then <c>close_socket</c>, which makes ElevenLabs say the
/// buffered text at once (no 40-character wait), send the rest of the audio as base64 <c>audio</c> messages and end with
/// <c>is_final</c>. One connection per segment: each segment has its own one-use authorization and key. An error message
/// (<c>{message, error, code}</c>) or an early close fails the segment with ElevenLabs' reason in the local log; a model the
/// WebSocket refuses (its API reference names only eleven_v3 models, its guide eleven_v4 too) fails as ModelUnsupported with
/// what to choose instead, never silently. Never run against the live service: there is no ElevenLabs account to test with.</summary>
public sealed class ElevenLabsDialogueClient : IElevenLabsSpeechClient
{
    public const string Api = "ElevenLabs Text to Dialogue";
    /// <summary>The largest single message accepted (a base64 audio chunk); a larger one fails the segment.</summary>
    public const int MaximumMessageBytes = 4 * 1024 * 1024;
    private readonly IProviderCredentialSource credentials;
    private readonly Uri origin;
    private readonly TimeProvider clock;
    private ElevenLabsSegmentTimings? last;

    /// <param name="origin">ElevenLabs' own origin (the default), or a numeric loopback origin for a local fixture that follows
    /// the documented protocol.</param>
    public ElevenLabsDialogueClient(IProviderCredentialSource credentials, Uri? origin = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        origin ??= ElevenLabsSpeechCatalog.Origin;
        if (!ElevenLabsSpeechCatalog.IsAllowedOrigin(origin))
            throw new ArgumentException("ElevenLabs speech goes only to api.elevenlabs.io or a local fixture on a loopback address.", nameof(origin));
        this.credentials = credentials;
        this.origin = origin;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>The timings of the last segment that finished speaking, or null.</summary>
    public ElevenLabsSegmentTimings? LastSegment => Volatile.Read(ref last);

    public async IAsyncEnumerable<byte[]> StreamAsync(ElevenLabsVoiceTarget target, BoundedSpeechInput input, SpeechSynthesisLimits limits,
        DateTimeOffset deadline, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);
        if (!ElevenLabsSpeechCatalog.SupportsModel(target.ModelId))
            throw Fail(ProviderFailureCode.ModelUnsupported, target.ModelId,
                $"Martlet speaks with {string.Join(" or ", ElevenLabsSetup.ModelIds)} only.");
        if (!ElevenLabsSetup.IsVoiceId(target.VoiceId))
            throw Fail(ProviderFailureCode.VoiceUnsupported, target.ModelId, "The cloned voice ID is not a valid ElevenLabs voice_id.");
        var started = clock.GetTimestamp();
        var key = await KeyAsync(target, cancellationToken).ConfigureAwait(false);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("xi-api-key", key);
        socket.Options.CollectHttpResponseDetails = true;
        await ConnectAsync(socket, target, cancellationToken).ConfigureAwait(false);
        var connected = clock.GetTimestamp();
        double? firstAudio = null;
        long audioBytes = 0;
        var finished = false;
        try
        {
            await SendAsync(socket, writer =>
            {
                writer.WriteStartArray("voices");
                writer.WriteStringValue(target.VoiceId);
                writer.WriteEndArray();
            }, cancellationToken).ConfigureAwait(false);
            await SendAsync(socket, writer =>
            {
                writer.WriteStartArray("inputs");
                writer.WriteStartObject();
                writer.WriteString("text", input.Text);
                writer.WriteString("voice_id", target.VoiceId);
                writer.WriteEndObject();
                writer.WriteEndArray();
            }, cancellationToken).ConfigureAwait(false);
            await SendAsync(socket, writer => writer.WriteBoolean("close_socket", true), cancellationToken).ConfigureAwait(false);

            byte? carry = null;
            using var message = new MemoryStream();
            while (true)
            {
                var received = await ReceiveAsync(socket, message, firstAudio is null ? limits.FirstAudioTimeout : limits.IdleTimeout,
                    firstAudio is null, target, audioBytes, cancellationToken).ConfigureAwait(false);
                if (received is not { } whole) break;
                var (audio, final) = Parse(whole, target);
                if (audio is { Length: > 0 })
                {
                    firstAudio ??= clock.GetElapsedTime(connected).TotalMilliseconds;
                    audioBytes += audio.Length;
                    // ElevenLabs' chunks need not end on a whole sample; carry a lone byte to the next chunk.
                    byte[] pcm = carry is { } kept ? [kept, .. audio] : audio;
                    carry = pcm.Length % 2 == 1 ? pcm[^1] : null;
                    if (pcm.Length >= 2) yield return pcm.Length % 2 == 1 ? pcm[..^1] : pcm;
                }
                if (final) break;
            }
            if (audioBytes == 0) throw Fail(ProviderFailureCode.EmptyAudio, target.ModelId, "ElevenLabs ended the segment without audio.");
            finished = true;
            Volatile.Write(ref last, new(clock.GetElapsedTime(started, connected).TotalMilliseconds, firstAudio ?? 0,
                clock.GetElapsedTime(started).TotalMilliseconds, audioBytes));
        }
        finally
        {
            await CloseAsync(socket, finished).ConfigureAwait(false);
        }
    }

    private async Task<string> KeyAsync(ElevenLabsVoiceTarget target, CancellationToken token)
    {
        var binding = ElevenLabsSpeechSynthesisStream.Binding(target);
        BoundProviderCredential? credential;
        try { credential = await credentials.ResolveAsync(binding, token).ConfigureAwait(false); }
        catch (CredentialUnavailableException) { throw new ElevenLabsException(ProviderFailureCode.CredentialUnavailable); }
        using (credential)
        {
            if (credential is null) throw new ElevenLabsException(ProviderFailureCode.CredentialUnavailable);
            if (credential.Binding != binding) throw new ElevenLabsException(ProviderFailureCode.CredentialBindingMismatch);
            return credential.Reveal();
        }
    }

    private async Task ConnectAsync(ClientWebSocket socket, ElevenLabsVoiceTarget target, CancellationToken token)
    {
        try
        {
            await socket.ConnectAsync(ElevenLabsSpeechCatalog.DialogueUri(origin, target.ModelId), token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is WebSocketException or HttpRequestException or IOException && !token.IsCancellationRequested)
        {
            var status = (int)socket.HttpStatusCode;
            var code = status switch
            {
                401 => ProviderFailureCode.Authentication,
                403 => ProviderFailureCode.PermissionDenied,
                402 => ProviderFailureCode.QuotaExceeded,
                429 => ProviderFailureCode.RateLimited,
                404 => ProviderFailureCode.ModelNotFound,
                400 or 422 => ProviderFailureCode.RequestRejected,
                >= 500 => ProviderFailureCode.Server,
                _ => ProviderFailureCode.Network
            };
            throw Fail(code, target.ModelId, status == 0 ? "Martlet couldn't connect to ElevenLabs." : $"The WebSocket upgrade got HTTP {status}.");
        }
    }

    private static async Task SendAsync(ClientWebSocket socket, Action<Utf8JsonWriter> body, CancellationToken token)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        try { await socket.SendAsync(buffer.WrittenMemory, WebSocketMessageType.Text, true, token).ConfigureAwait(false); }
        catch (WebSocketException) when (!token.IsCancellationRequested) { throw new ElevenLabsException(ProviderFailureCode.Network, "The connection closed while Martlet sent the reply text."); }
    }

    // One whole message, or null when ElevenLabs closed the connection normally after audio. Waiting longer than the first-audio
    // or idle limit fails the segment.
    private async Task<ReadOnlyMemory<byte>?> ReceiveAsync(ClientWebSocket socket, MemoryStream message, TimeSpan wait, bool first,
        ElevenLabsVoiceTarget target, long audioBytes, CancellationToken token)
    {
        message.SetLength(0);
        using var timeout = new CancellationTokenSource(wait, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                ValueWebSocketReceiveResult result;
                try { result = await socket.ReceiveAsync(chunk.AsMemory(), linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    throw Fail(first ? ProviderFailureCode.FirstAudioTimeout : ProviderFailureCode.IdleTimeout, target.ModelId,
                        first ? "No audio arrived in time." : "The audio stopped arriving.");
                }
                catch (WebSocketException) when (!token.IsCancellationRequested)
                {
                    throw Fail(ProviderFailureCode.Network, target.ModelId, "The connection to ElevenLabs broke.");
                }
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // A normal close after audio is the end; anything else is ElevenLabs refusing or failing the segment.
                    if (socket.CloseStatus == WebSocketCloseStatus.NormalClosure && audioBytes > 0) return null;
                    var reason = socket.CloseStatusDescription;
                    throw Fail(Classify(null, reason, null), target.ModelId,
                        $"ElevenLabs closed the connection ({(int?)socket.CloseStatus}){(string.IsNullOrWhiteSpace(reason) ? "" : ": " + reason)}.");
                }
                if (message.Length + result.Count > MaximumMessageBytes)
                    throw Fail(ProviderFailureCode.ResponseTooLarge, target.ModelId, "A message from ElevenLabs was too large.");
                message.Write(chunk, 0, result.Count);
                if (result.EndOfMessage) return message.GetBuffer().AsMemory(0, (int)message.Length);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    // A message's audio and whether it ends the segment; an error message fails it.
    private static (byte[]? Audio, bool Final) Parse(ReadOnlyMemory<byte> received, ElevenLabsVoiceTarget target)
    {
        try
        {
            using var document = JsonDocument.Parse(received, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Fail(ProviderFailureCode.ResponseSchema, target.ModelId, "ElevenLabs sent a message that isn't a JSON object.");
            if (Text(root, "error") is { } error)
            {
                var message = Text(root, "message");
                var param = Text(root, "param");
                throw Fail(Classify(error, message, param), target.ModelId,
                    $"{error}{(message is null ? "" : ": " + message)}{(param is null ? "" : $" (param {param})")}");
            }
            byte[]? audio = null;
            if (Text(root, "audio") is { Length: > 0 } encoded)
            {
                try { audio = Convert.FromBase64String(encoded); }
                catch (FormatException) { throw Fail(ProviderFailureCode.ResponseSchema, target.ModelId, "ElevenLabs sent audio that isn't base64."); }
            }
            var final = root.TryGetProperty("is_final", out var done) && done.ValueKind == JsonValueKind.True;
            return (audio, final);
        }
        catch (JsonException)
        {
            throw Fail(ProviderFailureCode.ResponseSchema, target.ModelId, "ElevenLabs sent a message that isn't JSON.");
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>What an ElevenLabs error (its <c>error</c> identifier, <c>message</c> and <c>param</c>) means for Martlet.</summary>
    internal static ProviderFailureCode Classify(string? error, string? message, string? param)
    {
        var text = $"{error} {message} {param}".ToLowerInvariant();
        if (text.Contains("auth", StringComparison.Ordinal) || text.Contains("api_key", StringComparison.Ordinal) ||
            text.Contains("api key", StringComparison.Ordinal) || text.Contains("unauthorized", StringComparison.Ordinal))
            return ProviderFailureCode.Authentication;
        if (text.Contains("permission", StringComparison.Ordinal) || text.Contains("forbidden", StringComparison.Ordinal))
            return ProviderFailureCode.PermissionDenied;
        if (text.Contains("quota", StringComparison.Ordinal) || text.Contains("credit", StringComparison.Ordinal) ||
            text.Contains("payment", StringComparison.Ordinal) || text.Contains("subscription", StringComparison.Ordinal))
            return ProviderFailureCode.QuotaExceeded;
        if (text.Contains("rate_limit", StringComparison.Ordinal) || text.Contains("rate limit", StringComparison.Ordinal) ||
            text.Contains("too_many", StringComparison.Ordinal) || text.Contains("too many", StringComparison.Ordinal) ||
            text.Contains("concurren", StringComparison.Ordinal))
            return ProviderFailureCode.RateLimited;
        if (param == "model_id" || text.Contains("model", StringComparison.Ordinal)) return ProviderFailureCode.ModelUnsupported;
        if (param is "voices" or "voice_id" || text.Contains("voice", StringComparison.Ordinal)) return ProviderFailureCode.VoiceUnsupported;
        return ProviderFailureCode.RequestRejected;
    }

    // The failure, said in the local log with what to do when the model was refused.
    private static ElevenLabsException Fail(ProviderFailureCode code, string model, string detail)
    {
        if (code == ProviderFailureCode.ModelUnsupported && model == ElevenLabsSetup.V4Turbo)
            detail += $" ElevenLabs refused {ElevenLabsSetup.V4Turbo} here; choose {ElevenLabsSetup.ModelName(ElevenLabsSetup.V3Conversational)} " +
                "in Companion › Voice.";
        ProviderDiagnostics.Report(Api, code, $"model {model}: {detail}");
        return new(code, detail);
    }

    private static async Task CloseAsync(ClientWebSocket socket, bool finished)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        if (!finished)
        {
            // A stopped or failed segment drops the connection at once, so ElevenLabs stops generating.
            socket.Abort();
            return;
        }
        using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, quick.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException) { socket.Abort(); }
    }
}
