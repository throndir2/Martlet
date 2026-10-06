using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Martlet.Messaging;

/// <summary>A message from WhatsApp with the ID Meta gave it (to mark it read and show "typing").</summary>
public sealed record WhatsAppMessage(InboundMessage Message, string MessageId);

/// <summary>Martlet's WhatsApp webhook on this PC: a small HTTP listener on localhost (no administrator rights needed) that a
/// tunnel or the owner's own public address forwards to. It answers Meta's verification request with the verify token and
/// accepts only message deliveries signed with the app secret (X-Hub-Signature-256) at a secret path, for one phone number.</summary>
public sealed class WhatsAppWebhook : IDisposable
{
    private const int MaximumBody = 1 << 20;
    private readonly HttpListener listener = new();
    private readonly byte[] appSecret;
    private readonly string phoneNumberId;
    private readonly Channel<WhatsAppMessage> inbox = Channel.CreateUnbounded<WhatsAppMessage>();
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly Queue<string> seenOrder = new();
    private readonly CancellationTokenSource stop = new();
    private Task? serving;

    public WhatsAppWebhook(int port, string appSecret, string phoneNumberId)
    {
        Port = port;
        this.appSecret = Encoding.UTF8.GetBytes(appSecret);
        this.phoneNumberId = phoneNumberId;
        Path = "/whatsapp/" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        VerifyToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        // Tunnels may send either name as the Host; http.sys takes both from a normal user.
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public int Port { get; }
    /// <summary>The secret path the webhook answers at; anything else gets 404.</summary>
    public string Path { get; }
    /// <summary>The token Meta must send back when it checks the webhook.</summary>
    public string VerifyToken { get; }
    /// <summary>A verification request with the right token was answered (Martlet's own reachability check or Meta's).</summary>
    public bool Verified { get; private set; }
    public ChannelReader<WhatsAppMessage> Messages => inbox.Reader;

    /// <summary>A free TCP port on this PC.</summary>
    public static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    public void Start()
    {
        if (serving is not null) return;
        try { listener.Start(); }
        catch (HttpListenerException error)
        {
            throw new MessagingException(MessagingFailure.Network, $"Couldn't listen for WhatsApp on port {Port} ({error.Message.Trim()}).");
        }
        serving = Task.Run(ServeAsync);
    }

    private async Task ServeAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var request = context.Request;
            if (request.Url?.AbsolutePath.TrimEnd('/').EndsWith(Path, StringComparison.Ordinal) != true)
            {
                response.StatusCode = 404;
                return;
            }
            if (request.HttpMethod == "GET")
            {
                var query = request.QueryString;
                if (query["hub.mode"] == "subscribe" && query["hub.verify_token"] is { } token &&
                    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(VerifyToken)) &&
                    query["hub.challenge"] is { Length: > 0 and <= 200 } challenge)
                {
                    Verified = true;
                    var bytes = Encoding.UTF8.GetBytes(challenge);
                    response.ContentType = "text/plain";
                    response.StatusCode = 200;
                    await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                }
                else response.StatusCode = 403;
                return;
            }
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                return;
            }
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int read;
            while ((read = await request.InputStream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaximumBody)
                {
                    response.StatusCode = 413;
                    return;
                }
                buffer.Write(chunk, 0, read);
            }
            var body = buffer.ToArray();
            if (!SignatureValid(appSecret, body, request.Headers["X-Hub-Signature-256"]))
            {
                response.StatusCode = 401;
                return;
            }
            response.StatusCode = 200;
            foreach (var message in Parse(body, phoneNumberId))
            {
                lock (seen)
                {
                    // Meta delivers a message again when an answer was slow; each is handled once.
                    if (!seen.Add(message.MessageId)) continue;
                    seenOrder.Enqueue(message.MessageId);
                    if (seenOrder.Count > 1000) seen.Remove(seenOrder.Dequeue());
                }
                inbox.Writer.TryWrite(message);
            }
        }
        catch (Exception error) when (error is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException) { }
        finally
        {
            try { response.Close(); }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    /// <summary>Whether <paramref name="header"/> (sha256=&lt;hex&gt;) is the app secret's HMAC-SHA256 of <paramref name="body"/>.</summary>
    public static bool SignatureValid(byte[] appSecret, byte[] body, string? header)
    {
        if (header is null || !header.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)) return false;
        byte[] given;
        try { given = Convert.FromHexString(header.AsSpan(7)); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(appSecret, body), given);
    }

    /// <summary>The messages in one webhook delivery for <paramref name="phoneNumberId"/>: text keeps its body, anything else
    /// (a photo, a voice note, a sticker) has none. WhatsApp chats with a business number are always one-to-one.</summary>
    public static IReadOnlyList<WhatsAppMessage> Parse(byte[] body, string phoneNumberId)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(body) as JsonObject; }
        catch (JsonException) { return []; }
        var messages = new List<WhatsAppMessage>();
        foreach (var change in (root?["entry"] as JsonArray ?? []).OfType<JsonObject>().SelectMany(entry => (entry["changes"] as JsonArray ?? []).OfType<JsonObject>()))
        {
            if ((string?)change["field"] != "messages" || change["value"] is not JsonObject value) continue;
            if ((string?)value["metadata"]?["phone_number_id"] != phoneNumberId) continue;
            var names = (value["contacts"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(contact => (string?)contact["wa_id"] is { Length: > 0 })
                .GroupBy(contact => (string)contact["wa_id"]!)
                .ToDictionary(group => group.Key, group => (string?)group.First()["profile"]?["name"]);
            foreach (var message in (value["messages"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if ((string?)message["from"] is not { Length: > 0 } from || (string?)message["id"] is not { Length: > 0 } id) continue;
                var text = (string?)message["type"] == "text" ? (string?)message["text"]?["body"] : null;
                var name = names.GetValueOrDefault(from) is { Length: > 0 } known ? known : "+" + from;
                messages.Add(new(new(from, name, text, Private: true, MessageId: id), id));
            }
        }
        return messages;
    }

    public void Dispose()
    {
        stop.Cancel();
        try { listener.Close(); }
        catch (ObjectDisposedException) { }
        inbox.Writer.TryComplete();
        stop.Dispose();
    }
}
