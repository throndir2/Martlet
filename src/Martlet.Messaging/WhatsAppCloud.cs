using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Martlet.Messaging;

/// <summary>The WhatsApp Business account Martlet answers as: the Meta app, the WhatsApp Business account (WABA), the business
/// phone number's ID, its number as shown and its name.</summary>
public sealed record WhatsAppAccount(string AppId, string BusinessAccountId, string PhoneNumberId, string Number, string Name);

/// <summary>What Martlet keeps in Windows Credential Manager for WhatsApp: the access token and the Meta app's secret.</summary>
public sealed record WhatsAppSecrets(string AccessToken, string AppSecret)
{
    /// <summary>One line each, the way Credential Manager keeps them.</summary>
    public string Pack() => AccessToken + "\n" + AppSecret;

    public static WhatsAppSecrets? Unpack(string? packed) =>
        packed?.Split('\n') is [{ Length: > 0 } token, { Length: > 0 } secret] ? new(token, secret) : null;
}

/// <summary>Meta's WhatsApp Cloud API on the Graph API (https://developers.facebook.com/docs/whatsapp/cloud-api): finding the
/// account a token reaches, pointing the app's webhook at Martlet, sending text and showing "typing". The access token and app
/// secret go only to graph.facebook.com, in the Authorization header, and never into an exception or log line.</summary>
public sealed class WhatsAppCloud : IDisposable
{
    public static readonly Uri DefaultApi = new("https://graph.facebook.com/");
    public const string Version = "v25.0";
    private readonly HttpClient http;
    private readonly WhatsAppSecrets secrets;
    private readonly Uri api;

    public WhatsAppCloud(WhatsAppSecrets secrets, HttpMessageHandler? handler = null, Uri? api = null)
    {
        if (!IsToken(secrets.AccessToken)) throw new ArgumentException("That doesn't look like a WhatsApp access token (it starts with EA...).");
        if (!IsAppSecret(secrets.AppSecret)) throw new ArgumentException("That doesn't look like a Meta app secret (32 letters and digits from App settings › Basic).");
        this.secrets = new(secrets.AccessToken.Trim(), secrets.AppSecret.Trim());
        this.api = api ?? DefaultApi;
        http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>A Graph API access token: long, letters and digits (Meta's start with EA).</summary>
    public static bool IsToken(string? value) => value?.Trim() is { Length: >= 40 and <= 700 } token && token.All(char.IsAsciiLetterOrDigit);

    /// <summary>A Meta app secret: 32 hexadecimal characters.</summary>
    public static bool IsAppSecret(string? value) => value?.Trim() is { Length: 32 } secret && secret.All(char.IsAsciiHexDigit);

    /// <summary>A Graph API object ID (an app, a WABA or a phone number): digits only.</summary>
    public static bool IsId(string? value) => value?.Trim() is { Length: >= 5 and <= 30 } id && id.All(char.IsAsciiDigit);

    /// <summary>Finds the account the token reaches: the app it belongs to (checking the app secret against it), the WhatsApp
    /// Business account and business phone number (the given ones, or the token's first when left empty), and the number's
    /// name.</summary>
    public async Task<WhatsAppAccount> DescribeAsync(string? phoneNumberId, string? businessAccountId, CancellationToken cancellation)
    {
        phoneNumberId = phoneNumberId?.Trim();
        businessAccountId = businessAccountId?.Trim();
        if (phoneNumberId is { Length: > 0 } && !IsId(phoneNumberId)) throw new ArgumentException("The phone number ID is a number, like 106540352242922.");
        if (businessAccountId is { Length: > 0 } && !IsId(businessAccountId)) throw new ArgumentException("The WhatsApp Business account ID is a number, like 102290129340398.");
        var debug = await GetAsync($"debug_token?input_token={Uri.EscapeDataString(secrets.AccessToken)}", appToken: false, cancellation).ConfigureAwait(false);
        var data = debug["data"] as JsonObject ?? throw new MessagingException(MessagingFailure.Protocol, "Meta didn't describe this access token.");
        if (data["is_valid"]?.GetValue<bool>() != true)
            throw new MessagingException(MessagingFailure.Unauthorized, "Meta says this access token isn't valid (it may have expired). Make a new one.");
        var appId = (string?)data["app_id"] ?? throw new MessagingException(MessagingFailure.Protocol, "Meta didn't say which app this access token belongs to.");
        try { await GetAsync($"{appId}/subscriptions", appToken: true, cancellation, appId).ConfigureAwait(false); }
        catch (MessagingException error) when (error.Failure == MessagingFailure.Unauthorized)
        {
            throw new MessagingException(MessagingFailure.Unauthorized, "The app secret doesn't belong to this access token's app. Copy it again from App settings › Basic.");
        }
        if (string.IsNullOrEmpty(businessAccountId))
        {
            businessAccountId = (data["granular_scopes"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Where(scope => (string?)scope["scope"] is "whatsapp_business_management" or "whatsapp_business_messaging")
                .SelectMany(scope => (scope["target_ids"] as JsonArray ?? []).Select(id => (string?)id))
                .FirstOrDefault(IsId);
            if (businessAccountId is null)
                throw new MessagingException(MessagingFailure.Protocol, "This access token doesn't reach a WhatsApp Business account. Give it the " +
                    "whatsapp_business_management and whatsapp_business_messaging permissions, or type the account ID.");
        }
        if (string.IsNullOrEmpty(phoneNumberId))
        {
            var numbers = await GetAsync($"{businessAccountId}/phone_numbers?fields=id", appToken: false, cancellation).ConfigureAwait(false);
            phoneNumberId = (numbers["data"] as JsonArray ?? []).OfType<JsonObject>().Select(number => (string?)number["id"]).FirstOrDefault(IsId)
                ?? throw new MessagingException(MessagingFailure.Protocol, "This WhatsApp Business account has no phone number yet. Add one in API Setup.");
        }
        var phone = await GetAsync($"{phoneNumberId}?fields=display_phone_number,verified_name", appToken: false, cancellation).ConfigureAwait(false);
        var shown = (string?)phone["display_phone_number"] ?? "";
        var name = (string?)phone["verified_name"] ?? "";
        if (shown.Length == 0) throw new MessagingException(MessagingFailure.Protocol, "Meta didn't describe this phone number.");
        return new(appId, businessAccountId, phoneNumberId, shown, name.Length > 0 ? name : shown);
    }

    /// <summary>Points the app's WhatsApp webhook at <paramref name="callback"/> for messages (Meta checks it right away with
    /// <paramref name="verifyToken"/>) and subscribes the app to the WhatsApp Business account.</summary>
    public async Task SubscribeAsync(WhatsAppAccount account, Uri callback, string verifyToken, CancellationToken cancellation)
    {
        await PostAsync($"{account.AppId}/subscriptions", new JsonObject
        {
            ["object"] = "whatsapp_business_account", ["callback_url"] = callback.AbsoluteUri, ["verify_token"] = verifyToken,
            ["fields"] = "messages", ["include_values"] = true
        }, appToken: true, cancellation, account.AppId).ConfigureAwait(false);
        await PostAsync($"{account.BusinessAccountId}/subscribed_apps", new JsonObject(), appToken: false, cancellation).ConfigureAwait(false);
    }

    public Task SendTextAsync(string phoneNumberId, string to, string text, CancellationToken cancellation) =>
        PostAsync($"{phoneNumberId}/messages", new JsonObject
        {
            ["messaging_product"] = "whatsapp", ["recipient_type"] = "individual", ["to"] = to, ["type"] = "text",
            ["text"] = new JsonObject { ["preview_url"] = false, ["body"] = text }
        }, appToken: false, cancellation);

    /// <summary>Marks a message read and shows "typing" in its chat until the reply (or 25 seconds).</summary>
    public Task TypingAsync(string phoneNumberId, string messageId, CancellationToken cancellation) =>
        PostAsync($"{phoneNumberId}/messages", new JsonObject
        {
            ["messaging_product"] = "whatsapp", ["status"] = "read", ["message_id"] = messageId,
            ["typing_indicator"] = new JsonObject { ["type"] = "text" }
        }, appToken: false, cancellation);

    private Task<JsonObject> GetAsync(string path, bool appToken, CancellationToken cancellation, string? appId = null) =>
        CallAsync(HttpMethod.Get, path, null, appToken ? appId : null, cancellation);

    private Task<JsonObject> PostAsync(string path, JsonObject body, bool appToken, CancellationToken cancellation, string? appId = null) =>
        CallAsync(HttpMethod.Post, path, body, appToken ? appId : null, cancellation);

    private async Task<JsonObject> CallAsync(HttpMethod method, string path, JsonObject? body, string? appId, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(method, new Uri($"{api.AbsoluteUri.TrimEnd('/')}/{Version}/{path}"));
            // An app access token is the app's ID and secret; it is needed only to set the app's webhook.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appId is null ? secrets.AccessToken : $"{appId}|{secrets.AppSecret}");
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            response = await http.SendAsync(request, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new MessagingException(MessagingFailure.Network, "WhatsApp (Meta) didn't answer in time.");
        }
        catch (HttpRequestException error)
        {
            throw new MessagingException(MessagingFailure.Network, $"Couldn't reach WhatsApp (Meta) ({error.HttpRequestError}).");
        }
        using (response)
        {
            JsonObject? answer = null;
            try { answer = JsonNode.Parse(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false)) as JsonObject; }
            catch (JsonException) { }
            if (response.IsSuccessStatusCode && answer is not null && answer["error"] is null) return answer;
            var error = answer?["error"] as JsonObject;
            var code = error?["code"] is JsonValue number && number.TryGetValue<int>(out var value) ? value : 0;
            var description = Clip((string?)error?["error_user_msg"] ?? (string?)error?["message"] ?? response.ReasonPhrase ?? "no answer");
            var name = path.Split('?')[0].Split('/')[^1];
            throw (response.StatusCode, code) switch
            {
                (HttpStatusCode.Unauthorized, _) or (_, 190) or (_, 102) =>
                    new MessagingException(MessagingFailure.Unauthorized, $"Meta didn't accept the access token ({description}). Temporary tokens last 24 hours; " +
                        "make a permanent one for a system user."),
                (HttpStatusCode.TooManyRequests, _) or (_, 4) or (_, 17) or (_, 80007) or (_, 130429) or (_, 131056) =>
                    new MessagingException(MessagingFailure.RateLimited, "WhatsApp asked Martlet to slow down.", TimeSpan.FromSeconds(30)),
                (_, 2200) => new MessagingException(MessagingFailure.Network, $"Meta couldn't reach Martlet's webhook yet: {description}"),
                (HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError, _) =>
                    new MessagingException(MessagingFailure.Network, $"WhatsApp (Meta) had a problem: {description}"),
                _ => new MessagingException(MessagingFailure.Protocol, $"WhatsApp refused {name}: {description}")
            };
        }
    }

    private static string Clip(string text) => text.Length <= 200 ? text : text[..200] + "…";

    public void Dispose() => http.Dispose();
}
