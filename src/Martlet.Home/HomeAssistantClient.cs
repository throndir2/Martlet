using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Martlet.Core.Settings;

namespace Martlet.Home;

public enum HomeAssistantFailure { InvalidAddress, Unreachable, Timeout, Unauthorized, NotHomeAssistant, Redirected, ServerError, BadResponse }

public sealed class HomeAssistantException(HomeAssistantFailure failure, string message) : Exception(message)
{
    public HomeAssistantFailure Failure { get; } = failure;
}

public sealed record HomeAssistantInfo(string LocationName, string Version);

public enum HomeResponseKind { ActionDone, QueryAnswer, Error }

/// <summary>Something Home Assistant acted on: an area, floor, domain, device class, device or entity.</summary>
public sealed record HomeTarget(string Name, string Type, string? Id)
{
    /// <summary>The entity domain ("light", "lock", "cover"...) for entity targets.</summary>
    public string? Domain => Type == "entity" && Id is { } id && id.IndexOf('.') is > 0 and var dot ? id[..dot] : null;
}

/// <summary>What Home Assistant's built-in Assist did with one sentence.</summary>
public sealed record HomeCommandResult(HomeResponseKind Kind, string Speech, string? ErrorCode,
    IReadOnlyList<HomeTarget> Succeeded, IReadOnlyList<HomeTarget> Failed)
{
    /// <summary>Assist matched the sentence to a home intent (even if it then could not carry it out).</summary>
    public bool Recognized => Kind switch
    {
        HomeResponseKind.Error => ErrorCode is "no_valid_targets" or "failed_to_handle",
        // "Never mind" and similar intents report success with nothing to say and nothing touched.
        _ => Speech.Length > 0 || Succeeded.Count > 0 || Failed.Count > 0
    };
}

public sealed record HomeCamera(string EntityId, string Name);

/// <summary>Talks to one Home Assistant over its REST API with a long-lived access token: <c>/api/config</c> to check the
/// connection, <c>/api/conversation/process</c> with the built-in (local, non-LLM) Assist agent, and <c>/api/states</c> to
/// list cameras. No redirects are followed and every answer is size- and time-bounded.</summary>
public sealed class HomeAssistantClient : IDisposable
{
    /// <summary>The built-in Assist agent: local sentence matching, limited to entities exposed to voice assistants.</summary>
    public const string BuiltInAgent = "conversation.home_assistant";
    public const int MaximumSentenceCharacters = 1024;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private const int MaximumSmallResponse = 262_144;
    private const int MaximumStatesResponse = 16 * 1024 * 1024;
    private readonly HttpClient http;

    public HomeAssistantClient(HttpMessageHandler? handler = null)
    {
        http = new(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        }, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<HomeAssistantInfo> GetInfoAsync(Uri baseUri, SecretLease token, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Get, baseUri, "api/config", token, null, MaximumSmallResponse,
            cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Text(root, "version") is not { Length: > 0 } version)
            throw NotHomeAssistant();
        return new(Clean(Text(root, "location_name"), 64) is { Length: > 0 } name ? name : "Home", Clean(version, 32));
    }

    public async Task<HomeCommandResult> ProcessAsync(Uri baseUri, SecretLease token, string sentence, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sentence);
        var text = sentence.Trim();
        if (text.Length > MaximumSentenceCharacters) text = text[..MaximumSentenceCharacters];
        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["text"] = text, ["agent_id"] = BuiltInAgent });
        using var document = await SendAsync(HttpMethod.Post, baseUri, "api/conversation/process", token, body,
            MaximumSmallResponse, cancellationToken).ConfigureAwait(false);
        return ParseConversation(document.RootElement);
    }

    public async Task<IReadOnlyList<HomeCamera>> CamerasAsync(Uri baseUri, SecretLease token, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Get, baseUri, "api/states", token, null, MaximumStatesResponse,
            cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw NotHomeAssistant();
        var cameras = new List<HomeCamera>();
        foreach (var state in document.RootElement.EnumerateArray())
        {
            if (state.ValueKind != JsonValueKind.Object || Text(state, "entity_id") is not { } id ||
                !id.StartsWith("camera.", StringComparison.Ordinal) || id.Length > 255 || !id.All(IsEntityChar))
                continue;
            var name = state.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object
                ? Clean(Text(attributes, "friendly_name"), 80) : "";
            cameras.Add(new(id, name.Length > 0 ? name : id["camera.".Length..].Replace('_', ' ')));
        }
        return cameras.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    /// <summary>The snapshot address of a camera entity, for Watch's network camera source.</summary>
    public static Uri CameraSnapshot(Uri baseUri, string entityId) => new(baseUri, "api/camera_proxy/" + entityId);

    internal static HomeCommandResult ParseConversation(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("response", out var response) ||
            response.ValueKind != JsonValueKind.Object)
            throw NotHomeAssistant();
        var kind = Text(response, "response_type") switch
        {
            "action_done" => HomeResponseKind.ActionDone,
            "query_answer" => HomeResponseKind.QueryAnswer,
            "error" => HomeResponseKind.Error,
            _ => throw new HomeAssistantException(HomeAssistantFailure.BadResponse, "Home Assistant answered in a format Martlet doesn't know.")
        };
        var speech = "";
        if (response.TryGetProperty("speech", out var said) && said.ValueKind == JsonValueKind.Object)
            foreach (var format in new[] { "plain", "ssml" })
                if (said.TryGetProperty(format, out var block) && block.ValueKind == JsonValueKind.Object)
                {
                    speech = Clean(Text(block, "speech"), 600);
                    if (format == "ssml") speech = StripMarkup(speech);
                    break;
                }
        string? code = null;
        IReadOnlyList<HomeTarget> succeeded = [], failed = [];
        if (response.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            code = Clean(Text(data, "code"), 64) is { Length: > 0 } c ? c : null;
            succeeded = Targets(data, "success");
            failed = Targets(data, "failed");
        }
        return new(kind, speech, code, succeeded, failed);
    }

    private static IReadOnlyList<HomeTarget> Targets(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) return [];
        var targets = new List<HomeTarget>();
        foreach (var item in list.EnumerateArray())
        {
            if (targets.Count == 32) break;
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = Clean(Text(item, "id"), 255);
            targets.Add(new(Clean(Text(item, "name"), 80), Clean(Text(item, "type"), 32), id.Length > 0 ? id : null));
        }
        return targets;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, Uri baseUri, string path, SecretLease token, byte[]? body,
        int maximumBytes, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(method, new Uri(baseUri, path));
        token.Use(value => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new string(value)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 300 and <= 399)
                throw new HomeAssistantException(HomeAssistantFailure.Redirected,
                    "Home Assistant answered with a redirect. Enter the exact address you open it with in the browser, including https:// if it uses it.");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new HomeAssistantException(HomeAssistantFailure.Unauthorized,
                    "Home Assistant rejected the access token. In Home Assistant open your profile, then Security, create a long-lived access token and paste it here.");
            if (response.StatusCode == HttpStatusCode.NotFound) throw NotHomeAssistant();
            if (status >= 500)
                throw new HomeAssistantException(HomeAssistantFailure.ServerError,
                    $"Home Assistant reported an error ({status}). Check its logs, then try again.");
            if (!response.IsSuccessStatusCode)
                throw new HomeAssistantException(HomeAssistantFailure.BadResponse, $"Home Assistant answered {status} {response.ReasonPhrase}.");
            if (response.Content.Headers.ContentType?.MediaType != "application/json") throw NotHomeAssistant();
            if (response.Content.Headers.ContentLength > maximumBytes) throw TooLarge();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var copy = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (copy.Length + read > maximumBytes) throw TooLarge();
                copy.Write(buffer, 0, read);
            }
            try { return JsonDocument.Parse(copy.GetBuffer().AsMemory(0, (int)copy.Length)); }
            catch (JsonException) { throw NotHomeAssistant(); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HomeAssistantException(HomeAssistantFailure.Timeout,
                "Home Assistant didn't answer in time. Check that it is running and that this PC is on the same network.");
        }
        catch (HttpRequestException error)
        {
            throw new HomeAssistantException(HomeAssistantFailure.Unreachable,
                $"Couldn't reach Home Assistant ({error.HttpRequestError}). Check the address and port (usually 8123) and that this PC is on the same network.");
        }
        catch (IOException)
        {
            throw new HomeAssistantException(HomeAssistantFailure.Unreachable, "The connection to Home Assistant broke off. Try again.");
        }
    }

    private static HomeAssistantException NotHomeAssistant() => new(HomeAssistantFailure.NotHomeAssistant,
        "That address answered, but not like Home Assistant's API. Check the address and port (usually 8123).");

    private static HomeAssistantException TooLarge() => new(HomeAssistantFailure.BadResponse, "Home Assistant's answer was too large.");

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsEntityChar(char c) => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '.';

    /// <summary>One line of plain text: control characters and runs of whitespace collapse to single spaces.</summary>
    internal static string Clean(string? value, int maximum)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = string.Join(' ', new string(value.Select(c => char.IsControl(c) || char.IsWhiteSpace(c) ? ' ' : c).ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return text.Length > maximum ? text[..maximum].TrimEnd() : text;
    }

    private static string StripMarkup(string value)
    {
        var text = new System.Text.StringBuilder(value.Length);
        var inside = false;
        foreach (var c in value)
        {
            if (c == '<') inside = true;
            else if (c == '>') inside = false;
            else if (!inside) text.Append(c);
        }
        return Clean(text.ToString(), 600);
    }

    public void Dispose() => http.Dispose();
}
