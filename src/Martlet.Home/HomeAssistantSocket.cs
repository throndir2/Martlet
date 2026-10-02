using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Settings;

namespace Martlet.Home;

/// <summary>One authenticated connection to Home Assistant's WebSocket API (<c>/api/websocket</c>) for the commands that
/// have no REST form: minting tokens, setup steps, discovered devices, backups. Commands run one at a time; every message
/// is size- and time-bounded and nothing is logged.</summary>
public sealed class HomeAssistantSocket : IAsyncDisposable
{
    private const int MaximumMessageBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);
    private readonly ClientWebSocket socket;
    private readonly SemaphoreSlim gate = new(1, 1);
    private int nextId;

    private HomeAssistantSocket(ClientWebSocket socket, string version)
    {
        this.socket = socket;
        Version = version;
    }

    /// <summary>The Home Assistant version the server reported during authentication.</summary>
    public string Version { get; }

    public static Task<HomeAssistantSocket> ConnectAsync(Uri baseUri, SecretLease token, CancellationToken cancellationToken)
    {
        string? bearer = null;
        token.Use(value => bearer = new string(value));
        return ConnectAsync(baseUri, bearer!, cancellationToken);
    }

    /// <summary>Connects with an access token: a long-lived token, or the short-lived one from signing in or setting up.</summary>
    public static async Task<HomeAssistantSocket> ConnectAsync(Uri baseUri, string accessToken, CancellationToken cancellationToken)
    {
        var builder = new UriBuilder(new Uri(baseUri, "api/websocket")) { Scheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws" };
        var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HomeAssistantClient.RequestTimeout);
        try
        {
            await socket.ConnectAsync(builder.Uri, timeout.Token).ConfigureAwait(false);
            using (var greeting = await ReceiveAsync(socket, timeout.Token).ConfigureAwait(false))
                if (Type(greeting.RootElement) != "auth_required") throw NotHomeAssistant();
            await SendAsync(socket, new JsonObject { ["type"] = "auth", ["access_token"] = accessToken }, timeout.Token).ConfigureAwait(false);
            using var answer = await ReceiveAsync(socket, timeout.Token).ConfigureAwait(false);
            var type = Type(answer.RootElement);
            if (type == "auth_invalid")
                throw new HomeAssistantException(HomeAssistantFailure.Unauthorized,
                    "Home Assistant rejected the access token. Sign in again or paste a new long-lived access token.");
            if (type != "auth_ok") throw NotHomeAssistant();
            var version = answer.RootElement.TryGetProperty("ha_version", out var reported) && reported.ValueKind == JsonValueKind.String
                ? HomeAssistantClient.Clean(reported.GetString(), 32) : "";
            return new(socket, version);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new HomeAssistantException(HomeAssistantFailure.Timeout,
                "Home Assistant didn't answer in time. Check that it is running and that this PC is on the same network.");
        }
        catch (WebSocketException)
        {
            socket.Dispose();
            throw new HomeAssistantException(HomeAssistantFailure.Unreachable,
                "Couldn't open Home Assistant's live connection. Check the address and that this PC is on the same network.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Sends one command (<paramref name="message"/> without an id) and returns a copy of its <c>result</c>.
    /// A refused command throws <see cref="HomeAssistantException"/>: <see cref="HomeAssistantFailure.Unauthorized"/> when
    /// the token's user may not run it (most management commands need an administrator).</summary>
    public async Task<JsonElement> CommandAsync(JsonObject message, CancellationToken cancellationToken, TimeSpan? limit = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit ?? CommandTimeout);
        try
        {
            var id = ++nextId;
            message["id"] = id;
            await SendAsync(socket, message, timeout.Token).ConfigureAwait(false);
            while (true)
            {
                using var document = await ReceiveAsync(socket, timeout.Token).ConfigureAwait(false);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var answered) ||
                    answered.ValueKind != JsonValueKind.Number || answered.GetInt32() != id || Type(root) != "result")
                    continue;
                if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True)
                    return root.TryGetProperty("result", out var result) ? result.Clone() : default;
                var code = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                var text = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) &&
                    m.ValueKind == JsonValueKind.String ? HomeAssistantClient.Clean(m.GetString(), 200) : "";
                throw code switch
                {
                    "unauthorized" => new HomeAssistantException(HomeAssistantFailure.Unauthorized,
                        "Home Assistant says this account isn't an administrator. Managing Home Assistant needs an administrator's sign-in."),
                    "unknown_command" => new HomeAssistantException(HomeAssistantFailure.BadResponse,
                        "This Home Assistant doesn't support that yet. Update Home Assistant, then try again."),
                    _ => new HomeAssistantException(HomeAssistantFailure.BadResponse,
                        text.Length > 0 ? $"Home Assistant refused: {text}" : "Home Assistant refused the request.")
                };
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HomeAssistantException(HomeAssistantFailure.Timeout, "Home Assistant didn't answer in time. Try again.");
        }
        catch (WebSocketException)
        {
            throw new HomeAssistantException(HomeAssistantFailure.Unreachable, "The connection to Home Assistant broke off. Try again.");
        }
        finally { gate.Release(); }
    }

    private static async Task SendAsync(ClientWebSocket socket, JsonObject message, CancellationToken cancellationToken) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, cancellationToken)
            .ConfigureAwait(false);

    private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var received = await socket.ReceiveAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (received.MessageType == WebSocketMessageType.Close)
                throw new HomeAssistantException(HomeAssistantFailure.Unreachable, "Home Assistant closed the connection. Try again.");
            if (buffer.Length + received.Count > MaximumMessageBytes)
                throw new HomeAssistantException(HomeAssistantFailure.BadResponse, "Home Assistant's answer was too large.");
            buffer.Write(chunk, 0, received.Count);
            if (received.EndOfMessage) break;
        }
        try { return JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException) { throw NotHomeAssistant(); }
    }

    private static string? Type(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            ? type.GetString() : null;

    private static HomeAssistantException NotHomeAssistant() => new(HomeAssistantFailure.NotHomeAssistant,
        "That address answered, but not like Home Assistant. Check the address.");

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        socket.Dispose();
        gate.Dispose();
    }
}
