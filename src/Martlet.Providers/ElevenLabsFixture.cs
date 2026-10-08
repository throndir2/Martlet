using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Martlet.Core.Settings;

namespace Martlet.Providers;

/// <summary>How <see cref="ElevenLabsFixture"/> behaves. <see cref="RejectedModels"/> are refused on the Text to Dialogue
/// WebSocket the way its API reference describes (an error message with <c>param: model_id</c>, then a close), which rehearses
/// a model mismatch. <see cref="FirstAudioDelay"/> stands in for ElevenLabs' model latency (about 100 ms for Eleven v4 Turbo,
/// as documented); it is a fixture delay, not a measurement.</summary>
public sealed record ElevenLabsFixtureOptions
{
    public string ExpectedKey { get; init; } = "fixture-key";
    public string VoiceId { get; init; } = "fixtureVoice00000001";
    public bool RequiresVerification { get; init; }
    public IReadOnlyList<string> RejectedModels { get; init; } = [];
    public TimeSpan FirstAudioDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan ChunkDelay { get; init; } = TimeSpan.FromMilliseconds(15);
    public int Chunks { get; init; } = 4;
    public int MillisecondsPerWord { get; init; } = 60;
}

/// <summary>One voice the fixture was asked to clone: the form fields Martlet sent and whether the key matched. The recording
/// itself is not kept, only its length and whether it is a WAV file.</summary>
public sealed record ElevenLabsFixtureClone(bool KeyMatched, string? Name, string? FileName, string? ContentType, int FileBytes,
    bool Wave, string? RemoveBackgroundNoise, string? Description);

/// <summary>One Text to Dialogue connection the fixture served: the query, whether the key came in the header (and matched),
/// the first message's voices, every text Martlet sent, whether it ended with <c>close_socket</c>, and the audio and error the
/// fixture sent back.</summary>
public sealed class ElevenLabsFixtureSession
{
    public string? ModelId { get; init; }
    public string? OutputFormat { get; init; }
    public bool HeaderKeyMatched { get; init; }
    public bool BodyKey { get; internal set; }
    public List<string> Voices { get; } = [];
    public List<string> Texts { get; } = [];
    public bool CloseSocket { get; internal set; }
    public long AudioBytes { get; internal set; }
    public string? Error { get; internal set; }
}

/// <summary>A local stand-in for ElevenLabs that follows its documented protocol (Instant Voice Cloning and the Text to
/// Dialogue WebSocket), on a loopback address only. It is a FIXTURE, NOT ElevenLabs and NOT AI: its "voice" is a quiet tone as
/// long as the words, cut into base64 <c>pcm_24000</c> chunks at odd byte boundaries. Tests and Martlet's MCP checks use it to
/// show that Martlet speaks the protocol as documented; it proves nothing about the live service.</summary>
public sealed class ElevenLabsFixture : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();
    private readonly List<ElevenLabsFixtureClone> clones = [];
    private readonly List<ElevenLabsFixtureSession> sessions = [];
    private readonly Task serving;

    public ElevenLabsFixtureOptions Options { get; }
    /// <summary>Where the fixture listens: <c>http://127.0.0.1:port</c>.</summary>
    public Uri Origin { get; }

    private ElevenLabsFixture(ElevenLabsFixtureOptions options)
    {
        Options = options;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Origin = new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        serving = Task.Run(AcceptAsync);
    }

    public static ElevenLabsFixture Start(ElevenLabsFixtureOptions? options = null) => new(options ?? new());

    public IReadOnlyList<ElevenLabsFixtureClone> Clones { get { lock (clones) return [.. clones]; } }
    public IReadOnlyList<ElevenLabsFixtureSession> Sessions { get { lock (sessions) return [.. sessions]; } }

    private async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var (method, target, headers, body) = await ReadHeadAsync(stream).ConfigureAwait(false);
                var path = target.Split('?')[0];
                if (method == "GET" && path == ElevenLabsSpeechCatalog.DialoguePath &&
                    headers.GetValueOrDefault("upgrade")?.Equals("websocket", StringComparison.OrdinalIgnoreCase) == true)
                    await DialogueAsync(stream, target, headers).ConfigureAwait(false);
                else if (method == "POST" && path == ElevenLabsSpeechCatalog.ClonePath)
                    await CloneAsync(stream, headers, body).ConfigureAwait(false);
                else
                    await RespondAsync(stream, 404, "{\"detail\":\"Not found\"}").ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or SocketException or WebSocketException or OperationCanceledException or
                ObjectDisposedException or InvalidDataException) { }
        }
    }

    // ---------- Text to Dialogue WebSocket ----------

    private async Task DialogueAsync(NetworkStream stream, string target, Dictionary<string, string> headers)
    {
        var query = Query(target);
        var session = new ElevenLabsFixtureSession
        {
            ModelId = query.GetValueOrDefault("model_id"), OutputFormat = query.GetValueOrDefault("output_format"),
            HeaderKeyMatched = headers.GetValueOrDefault("xi-api-key") == Options.ExpectedKey
        };
        lock (sessions) sessions.Add(session);
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(
            headers.GetValueOrDefault("sec-websocket-key", "") + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var upgrade = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
        await stream.WriteAsync(upgrade, stop.Token).ConfigureAwait(false);
        using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
        var pending = new StringBuilder();
        var first = true;
        var sentAudio = false;
        while (socket.State == WebSocketState.Open)
        {
            using var message = await ReceiveAsync(socket).ConfigureAwait(false);
            if (message is null) return;
            var root = message.RootElement;
            if (first)
            {
                first = false;
                session.BodyKey = root.TryGetProperty("xi_api_key", out var bodyKey) && bodyKey.GetString() == Options.ExpectedKey;
                if (!session.HeaderKeyMatched && !session.BodyKey)
                {
                    await RefuseAsync(socket, session, "authentication_required", "A valid xi-api-key is required.", null).ConfigureAwait(false);
                    return;
                }
                if (session.ModelId is not { } model || !(model.StartsWith("eleven_v3", StringComparison.Ordinal) ||
                        model.StartsWith("eleven_v4", StringComparison.Ordinal)) || Options.RejectedModels.Contains(model))
                {
                    await RefuseAsync(socket, session, "invalid_request", "model_id must start with eleven_v3", "model_id").ConfigureAwait(false);
                    return;
                }
                if (session.OutputFormat != ElevenLabsSpeechCatalog.OutputFormat)
                {
                    await RefuseAsync(socket, session, "invalid_request", "This fixture serves pcm_24000 only.", "output_format").ConfigureAwait(false);
                    return;
                }
                if (root.TryGetProperty("voices", out var voices) && voices.ValueKind == JsonValueKind.Array)
                    session.Voices.AddRange(voices.EnumerateArray().Select(v => v.GetString() ?? ""));
                // eleven_v4_turbo and eleven_v3_conversational register exactly one voice.
                if (session.Voices.Count != 1 || session.Voices[0] != Options.VoiceId)
                {
                    await RefuseAsync(socket, session, "invalid_request", "voices must hold exactly one voice of this account.", "voices").ConfigureAwait(false);
                    return;
                }
            }
            if (root.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Array)
                foreach (var input in inputs.EnumerateArray())
                {
                    var text = input.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    if (!input.TryGetProperty("voice_id", out var voice) || !session.Voices.Contains(voice.GetString() ?? ""))
                    {
                        await RefuseAsync(socket, session, "invalid_request", "voice_id must be one of the registered voices.", "voice_id").ConfigureAwait(false);
                        return;
                    }
                    session.Texts.Add(text);
                    pending.Append(text);
                }
            var flush = root.TryGetProperty("flush", out var f) && f.ValueKind == JsonValueKind.True;
            var close = root.TryGetProperty("close_socket", out var c) && c.ValueKind == JsonValueKind.True;
            if ((flush || close) && pending.Length > 0)
            {
                if (!sentAudio) await Task.Delay(Options.FirstAudioDelay, stop.Token).ConfigureAwait(false);
                sentAudio = true;
                await SendAudioAsync(socket, session, pending.ToString()).ConfigureAwait(false);
                pending.Clear();
                await SendAsync(socket, "{\"is_final_audio_for_turn\":true}").ConfigureAwait(false);
            }
            if (close)
            {
                session.CloseSocket = true;
                await SendAsync(socket, "{\"is_final\":true}").ConfigureAwait(false);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, stop.Token).ConfigureAwait(false);
                await DrainAsync(socket).ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task<JsonDocument?> ReceiveAsync(WebSocket socket)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var result = await socket.ReceiveAsync(chunk, stop.Token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            buffer.Write(chunk, 0, result.Count);
            if (buffer.Length > 1024 * 1024) throw new InvalidDataException("Message too large.");
            if (result.EndOfMessage) return JsonDocument.Parse(buffer.ToArray());
        }
    }

    private async Task SendAudioAsync(WebSocket socket, ElevenLabsFixtureSession session, string text)
    {
        var pcm = Tone(text);
        // Cut at odd byte offsets, as a real stream may, so a sample can straddle two messages.
        var cuts = Enumerable.Range(1, Math.Max(1, Options.Chunks) - 1).Select(i => i * pcm.Length / Options.Chunks | 1)
            .Where(cut => cut > 0 && cut < pcm.Length).Distinct().Prepend(0).Append(pcm.Length).ToArray();
        for (var i = 0; i + 1 < cuts.Length; i++)
        {
            if (i > 0) await Task.Delay(Options.ChunkDelay, stop.Token).ConfigureAwait(false);
            var part = pcm.AsMemory(cuts[i], cuts[i + 1] - cuts[i]);
            await SendAsync(socket, $"{{\"audio\":\"{Convert.ToBase64String(part.Span)}\",\"alignment\":null}}").ConfigureAwait(false);
            session.AudioBytes += part.Length;
        }
    }

    // A quiet 220 Hz tone, MillisecondsPerWord long per word (at least 200 ms): 24 kHz mono signed 16-bit little-endian.
    private byte[] Tone(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var samples = 24 * Math.Max(200, words * Options.MillisecondsPerWord);
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var value = (short)(3000 * Math.Sin(2 * Math.PI * 220 * i / 24_000));
            pcm[2 * i] = (byte)value;
            pcm[2 * i + 1] = (byte)(value >> 8);
        }
        return pcm;
    }

    private async Task RefuseAsync(WebSocket socket, ElevenLabsFixtureSession session, string error, string message, string? param)
    {
        session.Error = error + (param is null ? "" : $" ({param})");
        var payload = new StringBuilder("{\"message\":").Append(JsonSerializer.Serialize(message)).Append(",\"error\":")
            .Append(JsonSerializer.Serialize(error)).Append(",\"code\":1008");
        if (param is not null) payload.Append(",\"param\":").Append(JsonSerializer.Serialize(param));
        await SendAsync(socket, payload.Append('}').ToString()).ConfigureAwait(false);
        await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, error, stop.Token).ConfigureAwait(false);
        await DrainAsync(socket).ConfigureAwait(false);
    }

    // After its close, reads what the client still sends until the client's own close (or a short wait), so the connection
    // ends with a normal close, not a reset that could drop the last message before the client reads it.
    private async Task DrainAsync(WebSocket socket)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        wait.CancelAfter(TimeSpan.FromSeconds(2));
        var buffer = new byte[8192];
        try
        {
            while (socket.State == WebSocketState.CloseSent)
                if ((await socket.ReceiveAsync(buffer, wait.Token).ConfigureAwait(false)).MessageType == WebSocketMessageType.Close) return;
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or IOException) { }
    }

    private Task SendAsync(WebSocket socket, string json) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, stop.Token);

    // ---------- Instant Voice Cloning ----------

    private async Task CloneAsync(NetworkStream stream, Dictionary<string, string> headers, byte[] start)
    {
        var length = int.TryParse(headers.GetValueOrDefault("content-length"), out var declared) ? declared : -1;
        if (length is < 0 or > 64 * 1024 * 1024)
        {
            await RespondAsync(stream, 411, "{\"detail\":\"Content-Length required\"}").ConfigureAwait(false);
            return;
        }
        var body = new byte[length];
        var have = Math.Min(start.Length, length);
        start.AsSpan(0, have).CopyTo(body);
        while (have < length)
        {
            var read = await stream.ReadAsync(body.AsMemory(have), stop.Token).ConfigureAwait(false);
            if (read == 0) throw new IOException("The request body ended early.");
            have += read;
        }
        var keyMatched = headers.GetValueOrDefault("xi-api-key") == Options.ExpectedKey;
        var parts = Multipart(headers.GetValueOrDefault("content-type"), body);
        string? Field(string name) => parts.FirstOrDefault(p => p.Name == name) is { } part ? Encoding.UTF8.GetString(part.Content) : null;
        var file = parts.FirstOrDefault(p => p.Name == "files");
        var clone = new ElevenLabsFixtureClone(keyMatched, Field("name"), file?.FileName, file?.ContentType, file?.Content.Length ?? 0,
            file is { Content.Length: > 12 } wave && Encoding.ASCII.GetString(wave.Content, 0, 4) == "RIFF" &&
                Encoding.ASCII.GetString(wave.Content, 8, 4) == "WAVE",
            Field("remove_background_noise"), Field("description"));
        lock (clones) clones.Add(clone);
        if (!keyMatched)
            await RespondAsync(stream, 401, "{\"detail\":{\"status\":\"invalid_api_key\",\"message\":\"Invalid API key\"}}").ConfigureAwait(false);
        else if (clone.Name is null || file is null)
            await RespondAsync(stream, 422, "{\"detail\":[{\"loc\":[\"body\",\"" + (clone.Name is null ? "name" : "files") +
                "\"],\"msg\":\"Field required\",\"type\":\"missing\"}]}").ConfigureAwait(false);
        else
            await RespondAsync(stream, 200, $"{{\"voice_id\":\"{Options.VoiceId}\",\"requires_verification\":" +
                $"{(Options.RequiresVerification ? "true" : "false")}}}").ConfigureAwait(false);
    }

    private sealed record Part(string? Name, string? FileName, string? ContentType, byte[] Content);

    private static List<Part> Multipart(string? contentType, byte[] body)
    {
        var parts = new List<Part>();
        var boundary = contentType?.Split(';').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase))?[9..].Trim('"');
        if (string.IsNullOrEmpty(boundary)) return parts;
        var marker = Encoding.ASCII.GetBytes("--" + boundary);
        var at = IndexOf(body, marker, 0);
        while (at >= 0)
        {
            var start = at + marker.Length;
            if (start + 2 <= body.Length && body[start] == '-' && body[start + 1] == '-') break;
            start += 2;
            var next = IndexOf(body, marker, start);
            if (next < 0) break;
            var headEnd = IndexOf(body, "\r\n\r\n"u8.ToArray(), start);
            if (headEnd < 0 || headEnd > next) break;
            var head = Encoding.UTF8.GetString(body, start, headEnd - start);
            var content = body[(headEnd + 4)..(next - 2)];
            string? Attribute(string name) => System.Text.RegularExpressions.Regex.Match(head, $"[; ]{name}=\"?([^\";\\r\\n]*)\"?") is { Success: true } m
                ? m.Groups[1].Value : null;
            var type = head.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))?[13..].Trim();
            parts.Add(new(Attribute("name"), Attribute("filename"), type, content));
            at = next;
        }
        return parts;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from)
    {
        for (var i = from; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }

    // ---------- HTTP ----------

    private async Task<(string Method, string Target, Dictionary<string, string> Headers, byte[] Body)> ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new byte[16 * 1024];
        var length = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), stop.Token).ConfigureAwait(false);
            if (read == 0) throw new IOException("The request ended early.");
            length += read;
            var end = IndexOf(buffer[..length], "\r\n\r\n"u8.ToArray(), 0);
            if (end >= 0)
            {
                var lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
                var request = lines[0].Split(' ');
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                    if (line.IndexOf(':') is var colon and > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                return (request[0], request.Length > 1 ? request[1] : "/", headers, buffer[(end + 4)..length]);
            }
            if (length == buffer.Length) throw new InvalidDataException("The request head is too large.");
        }
    }

    private static Dictionary<string, string> Query(string target)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        var at = target.IndexOf('?');
        if (at < 0) return query;
        foreach (var pair in target[(at + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var cut = pair.IndexOf('=');
            if (cut > 0) query[Uri.UnescapeDataString(pair[..cut])] = Uri.UnescapeDataString(pair[(cut + 1)..]);
        }
        return query;
    }

    private async Task RespondAsync(NetworkStream stream, int status, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var reason = status switch { 200 => "OK", 401 => "Unauthorized", 404 => "Not Found", 411 => "Length Required", _ => "Unprocessable Entity" };
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(head, stop.Token).ConfigureAwait(false);
        await stream.WriteAsync(body, stop.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        try { await serving.ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
        stop.Dispose();
    }
}
