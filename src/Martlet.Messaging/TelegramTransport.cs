using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Martlet.Messaging;

/// <summary>Telegram's Bot API (https://core.telegram.org/bots/api) with long polling: Martlet asks Telegram for new messages
/// (getUpdates, waiting up to <see cref="PollSeconds"/> for one), so it needs no public address, open port or webhook. The bot
/// token goes only to api.telegram.org, inside the request path, and never into an exception or log line.</summary>
public sealed partial class TelegramTransport : IMessagingTransport
{
    public static readonly Uri DefaultApi = new("https://api.telegram.org/");
    /// <summary>How long one getUpdates waits for a message before answering with none.</summary>
    public const int PollSeconds = 50;
    private readonly HttpClient http;
    private readonly string token;
    private readonly Uri api;
    private readonly int pollSeconds;
    private long offset;

    public TelegramTransport(string token, HttpMessageHandler? handler = null, Uri? api = null, int pollSeconds = PollSeconds)
    {
        if (!IsToken(token)) throw new ArgumentException("That doesn't look like a Telegram bot token (like 123456789:AA...).", nameof(token));
        this.token = token.Trim();
        this.api = api ?? DefaultApi;
        this.pollSeconds = Math.Clamp(pollSeconds, 0, 50);
        http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public MessagingApp App => MessagingApp.Telegram;
    public int MaximumMessageLength => 4096;

    /// <summary>A bot token as BotFather gives it: the bot's number, a colon and its secret.</summary>
    public static bool IsToken(string? value) => value is not null && TokenPattern().IsMatch(value.Trim());

    [GeneratedRegex(@"^\d{3,20}:[A-Za-z0-9_-]{20,100}$")]
    private static partial Regex TokenPattern();

    public async Task<BotIdentity> ConnectAsync(CancellationToken cancellation)
    {
        var me = await CallAsync("getMe", new JsonObject(), TimeSpan.FromSeconds(20), cancellation).ConfigureAwait(false) as JsonObject
            ?? throw new MessagingException(MessagingFailure.Protocol, "Telegram didn't describe a bot for this token.");
        var name = (string?)me["first_name"] ?? "";
        var username = (string?)me["username"] ?? "";
        if (me["is_bot"]?.GetValue<bool>() != true || username.Length == 0)
            throw new MessagingException(MessagingFailure.Protocol, "Telegram didn't describe a bot for this token.");
        return new(name.Length > 0 ? name : username, username);
    }

    public async Task<IReadOnlyList<InboundMessage>> ReceiveAsync(CancellationToken cancellation)
    {
        var body = new JsonObject
        {
            ["offset"] = offset, ["timeout"] = pollSeconds, ["allowed_updates"] = new JsonArray("message")
        };
        var result = await CallAsync("getUpdates", body, TimeSpan.FromSeconds(pollSeconds + 20), cancellation).ConfigureAwait(false);
        if (result is not JsonArray updates) throw new MessagingException(MessagingFailure.Protocol, "Telegram sent updates Martlet couldn't read.");
        var messages = new List<InboundMessage>();
        foreach (var update in updates.OfType<JsonObject>())
        {
            if (update["update_id"] is JsonValue id && id.TryGetValue<long>(out var number)) offset = Math.Max(offset, number + 1);
            if (update["message"] is not JsonObject message || message["chat"] is not JsonObject chat) continue;
            var chatId = chat["id"]?.ToJsonString();
            if (string.IsNullOrEmpty(chatId)) continue;
            var type = (string?)chat["type"] ?? "";
            var from = message["from"] as JsonObject;
            var name = string.Join(" ", new[] { (string?)chat["first_name"] ?? (string?)from?["first_name"], (string?)chat["last_name"] ?? (string?)from?["last_name"] }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
            if (name.Length == 0) name = (string?)chat["title"] ?? (string?)chat["username"] ?? chatId;
            messages.Add(new(chatId, name, (string?)message["text"], type == "private"));
        }
        return messages;
    }

    public Task SendAsync(string chatId, string text, CancellationToken cancellation) =>
        CallAsync("sendMessage", new JsonObject { ["chat_id"] = JsonNode.Parse(chatId), ["text"] = text }, TimeSpan.FromSeconds(30), cancellation);

    public Task TypingAsync(string chatId, CancellationToken cancellation) =>
        CallAsync("sendChatAction", new JsonObject { ["chat_id"] = JsonNode.Parse(chatId), ["action"] = "typing" }, TimeSpan.FromSeconds(15), cancellation);

    private async Task<JsonNode?> CallAsync(string method, JsonObject body, TimeSpan limit, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(limit);
        HttpResponseMessage response;
        try
        {
            // Built as text: "bot123:AA..." alone would read as a URI scheme. The body is sent whole, with its length.
            using var content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            response = await http.PostAsync(new Uri($"{api.AbsoluteUri.TrimEnd('/')}/bot{token}/{method}"), content, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new MessagingException(MessagingFailure.Network, "Telegram didn't answer in time.");
        }
        catch (HttpRequestException error)
        {
            // HttpRequestException never carries the request URI (and so the token); its message is safe to show.
            throw new MessagingException(MessagingFailure.Network, $"Couldn't reach Telegram ({error.HttpRequestError}).");
        }
        using (response)
        {
            JsonObject? answer = null;
            try { answer = JsonNode.Parse(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false)) as JsonObject; }
            catch (JsonException) { }
            if (answer?["ok"]?.GetValue<bool>() == true) return answer["result"];
            var description = (string?)answer?["description"] ?? response.ReasonPhrase ?? "no answer";
            var retry = answer?["parameters"]?["retry_after"] is JsonValue seconds && seconds.TryGetValue<int>(out var wait)
                ? TimeSpan.FromSeconds(wait) : (TimeSpan?)null;
            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.NotFound =>
                    new MessagingException(MessagingFailure.Unauthorized, "Telegram didn't accept this bot token. Copy it again from BotFather."),
                HttpStatusCode.Conflict =>
                    new MessagingException(MessagingFailure.Conflict, "Another program (or another Martlet) is reading this bot's messages. Only one can."),
                HttpStatusCode.TooManyRequests =>
                    new MessagingException(MessagingFailure.RateLimited, "Telegram asked Martlet to slow down.", retry),
                _ => new MessagingException(MessagingFailure.Protocol, $"Telegram refused {method}: {Clip(description)}")
            };
        }
    }

    private static string Clip(string text) => text.Length <= 200 ? text : text[..200] + "…";

    public void Dispose() => http.Dispose();
}
