using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Providers;

/// <summary>A model app that can serve an OpenAI-compatible Chat Completions API on this computer: where it answers by default
/// (<see cref="Port"/> and <see cref="BasePath"/> on 127.0.0.1) and how to start its server. <see cref="OwnedBy"/> is the
/// <c>owned_by</c> its model list reports, set only when other apps use the same port, so the list tells them apart.</summary>
public sealed record LocalModelApp(string Id, string Name, int Port, string BasePath, string HowToStart)
{
    public string? OwnedBy { get; init; }
    /// <summary>Ollama: its own model list (<c>/api/tags</c>) names every downloaded model.</summary>
    public bool OllamaTags { get; init; }
    public string BaseUrl => $"http://127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}{BasePath}";
}

/// <summary>A model app answering on this computer, with the Chat Completions base URL Thinking takes and the models it lists.
/// <see cref="NeedsKey"/>: it answered but asks for an API key before it lists them. <see cref="Unusable"/>: models it lists
/// with names Martlet can't send (see <see cref="ChatCompletionsSetup.ModelId"/>).</summary>
public sealed record LocalModelServer(string Id, string Name, string ChatCompletionsBaseUrl, IReadOnlyList<string> Models)
{
    public bool NeedsKey { get; init; }
    public IReadOnlyList<string> Unusable { get; init; } = [];
    public string? HowToStart { get; init; }
}

public enum LocalServerAnswerKind
{
    /// <summary>It listed its models (maybe none).</summary>
    Models,
    /// <summary>It answered like a model server but asks for an API key (or refused the one sent).</summary>
    NeedsKey,
    /// <summary>Something answers there, but not with a model list.</summary>
    NotAModelServer,
    /// <summary>Nothing answers there.</summary>
    NoAnswer
}

/// <summary>What a server on this computer said when asked for its models. <see cref="Problem"/> says why it isn't usable, in
/// words, when <see cref="Kind"/> isn't <see cref="LocalServerAnswerKind.Models"/>.</summary>
public sealed record LocalServerAnswer(LocalServerAnswerKind Kind, IReadOnlyList<string> Models, string? OwnedBy, string? Problem)
{
    public IReadOnlyList<string> Unusable { get; init; } = [];
}

/// <summary>What testing a model in a model app on this computer found: the summary in words, whether it works with a
/// <see cref="Warning"/> (slow, or it refused tools), and whether it refused the tools a reply offers.</summary>
public sealed record LocalServerTestResult(string Summary, bool Warning, bool ToolsRejected, TimeSpan FirstWords, TimeSpan Total);

/// <summary>Finds and checks model apps on this computer: Ollama, LM Studio, llama.cpp's server, KoboldCpp, Jan, vLLM, Lemonade,
/// SGLang, text-generation-webui, GPT4All, Docker Model Runner, LiteLLM and any other app with an OpenAI-compatible server.
/// Each one boils down to a Chat Completions endpoint on a loopback port, so Thinking uses them all through the same route.
/// Everything here asks 127.0.0.1 (or the loopback address typed) only, never the network; nothing is started or installed.
/// The macOS and Linux companion and the Windows desktop share it.</summary>
public static class LocalModelServers
{
    /// <summary>How long one app is given to answer while looking for them all (they are asked at the same time).</summary>
    public static readonly TimeSpan LookTimeout = TimeSpan.FromSeconds(1.5);

    private const int MaximumListBytes = 4_194_304;
    private static readonly TimeSpan AnswerLimit = TimeSpan.FromMinutes(2);
    internal const string TestPrompt = "Say hello in five words or fewer.";

    /// <summary>The apps Martlet looks for, by default address. Apps that share a port are told apart by their model list's
    /// <c>owned_by</c>; an app on another port is found through the address the owner types.</summary>
    public static IReadOnlyList<LocalModelApp> Apps { get; } = Array.AsReadOnly<LocalModelApp>(
    [
        new("ollama", "Ollama", 11434, "/v1", "Martlet installs and starts Ollama itself; or start the Ollama app.") { OllamaTags = true },
        new("lm-studio", "LM Studio", 1234, "/v1",
            "In LM Studio, open Developer (or Local Server) and turn on Start server. Load a model, or turn on loading models on demand."),
        new("llama-cpp", "llama.cpp server", 8080, "/v1",
            "Start llama-server with your model, for example llama-server -m model.gguf --jinja (--jinja lets the model use tools).")
        { OwnedBy = "llamacpp" },
        new("koboldcpp", "KoboldCpp", 5001, "/v1", "Start KoboldCpp with your model; its OpenAI-compatible API is on by default."),
        new("jan", "Jan", 1337, "/v1", "In Jan, open Settings › Local API Server and start the server."),
        new("vllm", "vLLM", 8000, "/v1", "Start it with vllm serve <model> (in WSL or Linux on this computer).") { OwnedBy = "vllm" },
        new("lemonade", "Lemonade Server", 13305, "/api/v1", "Start Lemonade Server and load a model."),
        new("lemonade-8000", "Lemonade Server", 8000, "/api/v1", "Start Lemonade Server and load a model."),
        new("sglang", "SGLang", 30000, "/v1", "Start it with python -m sglang.launch_server --model-path <model>."),
        new("text-generation-webui", "text-generation-webui or TabbyAPI", 5000, "/v1",
            "Start text-generation-webui with --api (or start TabbyAPI), then load a model."),
        new("gpt4all", "GPT4All", 4891, "/v1", "In GPT4All, open Settings › Application and turn on Enable Local API Server."),
        new("docker-model-runner", "Docker Model Runner", 12434, "/engines/v1",
            "In Docker Desktop, open Settings › AI and turn on host-side TCP support for Docker Model Runner (port 12434)."),
        new("litellm", "LiteLLM", 4000, "/v1", "Start the LiteLLM proxy. It may send requests on to cloud providers.")
    ]);

    /// <summary>The app at exactly this base URL when no other app Martlet knows uses its port (so the name is certain), else
    /// null.</summary>
    public static LocalModelApp? AppAt(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || !IsLoopback(uri)) return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        // An app known by its owned_by shares its port with apps Martlet can't name, so a saved address alone doesn't say which.
        return Apps.Where(a => a.Port == uri.Port && a.BasePath == path).ToList() is [{ OwnedBy: null } only] ? only : null;
    }

    /// <summary>The app's name for a base URL on this computer: "LM Studio", or "the model app on port 8080" when the port
    /// could be more than one app.</summary>
    public static string Name(string baseUrl) =>
        AppAt(baseUrl)?.Name ??
        (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? $"the model app on port {uri.Port.ToString(CultureInfo.InvariantCulture)}" : "the model app");

    /// <summary>Whether the base URL is a server on this computer (a loopback address).</summary>
    public static bool IsOnThisComputer(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && IsLoopback(uri);

    private static bool IsLoopback(Uri uri) =>
        uri.Scheme is "http" or "https" && IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);

    /// <summary>Turns what the owner typed for an app on this computer ("1234", "localhost:1234", "http://localhost:8080/v1/",
    /// "127.0.0.1:12434") into the canonical base URL Thinking takes ("http://127.0.0.1:1234/v1"). Without a path it uses the
    /// path of the app Martlet knows on that port, else /v1. Null, with <paramref name="problem"/> in words, for an address that
    /// isn't on this computer or isn't a base URL.</summary>
    public static string? Normalize(string? typed, out string problem)
    {
        problem = "";
        var text = (typed ?? "").Trim();
        if (text.Length == 0)
        {
            problem = "Enter the address the app shows, for example localhost:1234.";
            return null;
        }
        if (text.All(char.IsAsciiDigit)) text = "127.0.0.1:" + text;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            problem = "That isn't an address Martlet can use. Enter it like localhost:1234 or http://127.0.0.1:8080/v1.";
            return null;
        }
        var host = uri.IdnHost.Trim('[', ']');
        string authorityHost;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) authorityHost = "127.0.0.1";
        else if (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address))
            authorityHost = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        else
        {
            problem = $"{uri.Host} isn't this computer. For an app on another computer, set that computer up as a Martlet host, " +
                "or use its HTTPS address as a custom server under A cloud provider.";
            return null;
        }
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            problem = "Leave out any name, password, ? or # part; enter only the address and port.";
            return null;
        }
        if (uri.IsDefaultPort && !text.Contains(":" + uri.Port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            problem = "Add the app's port, for example localhost:1234.";
            return null;
        }
        var path = uri.AbsolutePath.TrimEnd('/');
        foreach (var tail in new[] { "/chat/completions", "/models" })
            if (path.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) path = path[..^tail.Length];
        if (path.Length == 0)
            path = Apps.Where(a => a.Port == uri.Port).Select(a => a.BasePath).Distinct().ToList() is [var only] ? only : "/v1";
        var baseUrl = $"{uri.Scheme}://{authorityHost}:{uri.Port.ToString(CultureInfo.InvariantCulture)}{path}";
        try
        {
            _ = ChatCompletionsSetup.BaseUri(baseUrl);
            return baseUrl;
        }
        catch (ContractException error)
        {
            problem = error.Message;
            return null;
        }
    }

    /// <summary>Asks every app Martlet knows at once, each for at most <paramref name="timeout"/> (<see cref="LookTimeout"/> by
    /// default), and returns those that answer like a model server, in <see cref="Apps"/> order.</summary>
    public static async Task<IReadOnlyList<LocalModelServer>> DetectAsync(HttpMessageHandler? handler = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(handler);
        var probes = Apps.GroupBy(a => (a.Port, a.BasePath, a.OllamaTags)).Select(group => group.ToList()).ToList();
        var answers = await Task.WhenAll(probes.Select(async apps =>
        {
            var first = apps[0];
            var answer = await AskAsync(client, first.BaseUrl, null, first.OllamaTags, timeout ?? LookTimeout, cancellationToken)
                .ConfigureAwait(false);
            return (apps, answer);
        })).ConfigureAwait(false);
        var found = new List<LocalModelServer>();
        var seen = new HashSet<int>();
        foreach (var (apps, answer) in answers)
        {
            if (answer.Kind is not (LocalServerAnswerKind.Models or LocalServerAnswerKind.NeedsKey)) continue;
            var app = apps.FirstOrDefault(a => a.OwnedBy is not null && string.Equals(a.OwnedBy, answer.OwnedBy, StringComparison.OrdinalIgnoreCase))
                ?? apps.FirstOrDefault(a => a.OwnedBy is null);
            // Two paths on one port (vLLM's /v1 and Lemonade's /api/v1) are one server: keep the first that answered.
            if (!seen.Add(apps[0].Port)) continue;
            var name = app?.Name ?? $"Model app on port {apps[0].Port.ToString(CultureInfo.InvariantCulture)}";
            found.Add(new(app?.Id ?? $"port-{apps[0].Port.ToString(CultureInfo.InvariantCulture)}", name, apps[0].BaseUrl, answer.Models)
            {
                NeedsKey = answer.Kind == LocalServerAnswerKind.NeedsKey, Unusable = answer.Unusable, HowToStart = app?.HowToStart
            });
        }
        return found;
    }

    /// <summary>Asks the server at <paramref name="baseUrl"/> (on this computer only) which models it has: <c>GET {base}/models</c>,
    /// with <paramref name="apiKey"/> as a bearer token when given. Never throws for an unreachable or odd server.</summary>
    public static async Task<LocalServerAnswer> AskAsync(string baseUrl, string? apiKey, HttpMessageHandler? handler = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!IsOnThisComputer(baseUrl))
            return new(LocalServerAnswerKind.NoAnswer, [], null, "Martlet only looks for model apps on this computer.");
        using var client = CreateClient(handler);
        return await AskAsync(client, baseUrl, apiKey, ollamaTags: false, timeout ?? TimeSpan.FromSeconds(5), cancellationToken)
            .ConfigureAwait(false);
    }

    private static HttpClient CreateClient(HttpMessageHandler? handler) =>
        handler is null
            ? new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) })
                { Timeout = Timeout.InfiniteTimeSpan }
            : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    private static async Task<LocalServerAnswer> AskAsync(HttpClient client, string baseUrl, string? apiKey, bool ollamaTags,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        var url = ollamaTags ? new Uri(new Uri(baseUrl).GetLeftPart(UriPartial.Authority) + "/api/tags") : new Uri(baseUrl.TrimEnd('/') + "/models");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            var body = await ReadLimitedAsync(response.Content, limit.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return LooksLikeApiError(body)
                    ? new(LocalServerAnswerKind.NeedsKey, [], null, string.IsNullOrEmpty(apiKey)
                        ? "It asks for an API key. Enter the key you set in the app."
                        : "It refused this key. Check the key you set in the app.")
                    : new(LocalServerAnswerKind.NotAModelServer, [], null, "Something answers there, but it isn't a model app.");
            if (!response.IsSuccessStatusCode || body is null)
                return new(LocalServerAnswerKind.NotAModelServer, [], null,
                    $"Something answers there, but it didn't list models (error {(int)response.StatusCode}). Check the address and port.");
            var (models, ownedBy) = ParseList(body, ollamaTags);
            if (models is null)
                return new(LocalServerAnswerKind.NotAModelServer, [], null, "Something answers there, but it isn't a model app's API. Check the port and path.");
            var usable = models.Where(Usable).ToArray();
            return new(LocalServerAnswerKind.Models, usable, ownedBy, null) { Unusable = [.. models.Where(m => !Usable(m))] };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(LocalServerAnswerKind.NoAnswer, [], null, "Nothing answers there. Start the app's server, then look again.");
        }
        catch (HttpRequestException)
        {
            return new(LocalServerAnswerKind.NoAnswer, [], null, "Nothing answers there. Start the app's server, then look again.");
        }
    }

    private static bool Usable(string model)
    {
        try { ChatCompletionsSetup.ModelId(model); return true; }
        catch (ContractException) { return false; }
    }

    private static async Task<string?> ReadLimitedAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength > MaximumListBytes) return null;
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16_384];
        int read;
        while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumListBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static bool LooksLikeApiError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                (document.RootElement.TryGetProperty("error", out _) || document.RootElement.TryGetProperty("detail", out _));
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Model names from Ollama's /api/tags ({"models":[{"name"}]}) or an OpenAI-style /models ({"data":[{"id"}]});
    /// null when the reply is neither (another program on that port).</summary>
    internal static IReadOnlyList<string>? ParseModels(string json, bool ollamaTags) => ParseList(json, ollamaTags).Models;

    private static (IReadOnlyList<string>? Models, string? OwnedBy) ParseList(string json, bool ollamaTags)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var (list, field) = ollamaTags ? ("models", "name") : ("data", "id");
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(list, out var items) || items.ValueKind != JsonValueKind.Array)
                return (null, null);
            string? ownedBy = null;
            var names = new List<string>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (item.TryGetProperty(field, out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } value &&
                    names.Count < 200 && !names.Contains(value, StringComparer.Ordinal))
                    names.Add(value);
                if (ownedBy is null && item.TryGetProperty("owned_by", out var owner) && owner.ValueKind == JsonValueKind.String)
                    ownedBy = owner.GetString();
            }
            return (names, ownedBy);
        }
        catch (JsonException) { return (null, null); }
    }

    /// <summary>Checks that <paramref name="model"/> answers in the app at <paramref name="baseUrl"/> (this computer only) the way
    /// a reply asks it: streamed, with the reply budget (<paramref name="replyTokens"/>), the Thinking steps choice
    /// (<paramref name="reasoning"/>, written for the route's <paramref name="control"/>) and a tool offered, as replies offer
    /// tools. An app that refuses the tool is asked again without it, as replies do, and the result says so. A model that
    /// works but starts slower than <paramref name="firstWordsLimit"/> passes with a warning; anything that stops a reply throws
    /// <see cref="InvalidOperationException"/> with what went wrong, in the app's own words when it gave any.</summary>
    public static async Task<LocalServerTestResult> TestAsync(string baseUrl, string model, string? apiKey, int? replyTokens,
        ReasoningControl control, bool? reasoning, TimeSpan firstWordsLimit, IProgress<string> output, HttpMessageHandler? handler = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!IsOnThisComputer(baseUrl)) throw new InvalidOperationException("Martlet tests only model apps on this computer.");
        try { ChatCompletionsSetup.ModelId(model); }
        catch (ContractException error) { throw new InvalidOperationException(error.Message); }
        var name = Name(baseUrl);
        using var client = CreateClient(handler);
        output.Report($"Asking {name} at {baseUrl} for its models...");
        var answer = await AskAsync(client, baseUrl, apiKey, ollamaTags: false, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        switch (answer.Kind)
        {
            case LocalServerAnswerKind.NoAnswer:
                throw new InvalidOperationException($"Nothing answers at {baseUrl}. Start " +
                    $"{(AppAt(baseUrl) is { } app ? app.Name + "'s" : "the app's")} server, then test again.");
            case LocalServerAnswerKind.NotAModelServer:
                throw new InvalidOperationException($"{baseUrl}: {answer.Problem}");
            case LocalServerAnswerKind.NeedsKey:
                throw new InvalidOperationException(string.IsNullOrEmpty(apiKey)
                    ? $"{Capital(name)} asks for an API key. Enter the key you set in the app, then test again."
                    : $"{Capital(name)} refused this key. Check the key you set in the app, then test again.");
        }
        if (answer.Models.Count == 0) output.Report($"{Capital(name)} lists no models. It may load {model} when asked.");
        else if (!answer.Models.Contains(model, StringComparer.Ordinal))
            output.Report($"{Capital(name)} doesn't list {model} (it lists {string.Join(", ", answer.Models.Take(8))}). It may still load it when asked.");
        else output.Report($"{Capital(name)} lists {model}.");

        output.Report($"Asking {model} for a test reply (the first one may wait while {name} loads it)...");
        Reply reply;
        var toolsRejected = false;
        try
        {
            reply = await AskModelAsync(client, baseUrl, model, apiKey, replyTokens, control, reasoning, tools: true, name, output, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RefusedException refused)
        {
            output.Report($"{Capital(name)} refused the request with a tool ({refused.Message}). Asking again without tools, as replies do...");
            toolsRejected = true;
            reply = await AskModelAsync(client, baseUrl, model, apiKey, replyTokens, control, reasoning, tools: false, name, output, cancellationToken)
                .ConfigureAwait(false);
        }
        output.Report($"First words after {Seconds(reply.FirstWords)}. Finished after {Seconds(reply.Total)}.");
        output.Report(reply.CalledTool ? "It answered by calling the offered tool, so it can use tools." : $"Reply: {reply.Text}");

        var tools = toolsRejected
            ? $" It doesn't take tools, so Martlet answers without them (no memory search, reminders or other tools). " +
              (AppAt(baseUrl)?.Id == "llama-cpp" || answer.OwnedBy == "llamacpp" ? "Start llama-server with --jinja to give it tools." :
                  "Turn on tool use in the app if it has the option.")
            : "";
        if (reply.FirstWords > firstWordsLimit)
            return new($"{model} works in {name}, but starts too slowly for Martlet. First words took {Seconds(reply.FirstWords)}. " +
                $"Martlet waits {Seconds(firstWordsLimit)}. Choose a smaller model.{tools}", true, toolsRejected, reply.FirstWords, reply.Total);
        return new($"{model} works in {name} on this PC. First words after {Seconds(reply.FirstWords)}; finished after {Seconds(reply.Total)}.{tools}",
            toolsRejected, toolsRejected, reply.FirstWords, reply.Total);
    }

    private sealed record Reply(string Text, TimeSpan FirstWords, TimeSpan Total, bool CalledTool);

    /// <summary>The app refused the request itself (an HTTP error before any answer), which a request with tools retries without.</summary>
    private sealed class RefusedException(string message) : Exception(message);

    private static async Task<Reply> AskModelAsync(HttpClient client, string baseUrl, string model, string? apiKey, int? replyTokens,
        ReasoningControl control, bool? reasoning, bool tools, string name, IProgress<string> output, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(AnswerLimit);
        var clock = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/chat/completions")
            {
                Content = new StringContent(Request(model, replyTokens, control, reasoning, tools), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrEmpty(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadLimitedAsync(response.Content, limit.Token).ConfigureAwait(false);
                var refusal = ErrorText(body) ?? $"{name} gave no reason";
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new InvalidOperationException($"{Capital(name)} refused the key (error {(int)response.StatusCode}): {refusal}.");
                // A server error (500) is about the tool only when it says so (llama.cpp: "tools param requires --jinja flag").
                if (tools && ((int)response.StatusCode is 400 or 422 or 501 ||
                        response.StatusCode == HttpStatusCode.InternalServerError && MentionsTools(refusal)))
                    throw new RefusedException($"error {(int)response.StatusCode}: {refusal}");
                throw new InvalidOperationException($"{model} didn't answer in {name} (error {(int)response.StatusCode}): {refusal}.");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = new StringBuilder();
            TimeSpan? first = null;
            string? finish = null;
            bool done = false, thinking = false, calledTool = false;
            while (await reader.ReadLineAsync(limit.Token).ConfigureAwait(false) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") { done = true; break; }
                using var chunk = JsonDocument.Parse(data);
                var root = chunk.RootElement;
                if (root.TryGetProperty("error", out _))
                    throw new InvalidOperationException($"{model} stopped answering in {name}: {ErrorText(data) ?? "no reason given"}.");
                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) continue;
                var choice = choices[0];
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    if (JsonText(delta, "content") is { Length: > 0 } content)
                    {
                        first ??= clock.Elapsed;
                        if (text.Length < 2000) text.Append(content);
                    }
                    if (JsonText(delta, "reasoning") is { Length: > 0 } || JsonText(delta, "reasoning_content") is { Length: > 0 })
                    {
                        first ??= clock.Elapsed;
                        if (!thinking) output.Report($"{model} is thinking first...");
                        thinking = true;
                    }
                    if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() > 0)
                    {
                        first ??= clock.Elapsed;
                        calledTool = true;
                    }
                }
                finish = JsonText(choice, "finish_reason") ?? finish;
            }
            // Some apps end the stream without [DONE] after the last chunk with a finish reason.
            if (!done && finish is null) throw new InvalidOperationException($"{model}'s answer was cut off: {name} ended the reply early.");
            if (finish == "length")
                throw new InvalidOperationException($"{model} used its whole reply budget{(replyTokens is { } cap ? $" ({cap.ToString(CultureInfo.InvariantCulture)} tokens)" : "")} before finishing" +
                    (thinking ? ", thinking before it answered" : "") + ". Raise or clear Max reply length in Companion › Replies, or choose a chat model.");
            var said = text.ToString().Trim();
            if (said.Length == 0 && !calledTool) throw new InvalidOperationException($"{model} answered with nothing in {name}.");
            return new(said.Length > 300 ? said[..300] + "…" : said, first ?? clock.Elapsed, clock.Elapsed, calledTool);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{model} didn't answer in {name} within {AnswerLimit.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} minutes. " +
                "If a game or other programs are using the graphics card, choose a smaller model or close them, then test again.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"{Capital(name)} sent a response Martlet couldn't read.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            throw new InvalidOperationException($"{Capital(name)} stopped answering while {model} replied ({error.Message}).");
        }
    }

    /// <summary>The test request: one user message, streamed, shaped like a reply's (its reply budget, Thinking steps choice and,
    /// with <paramref name="tools"/>, one harmless tool).</summary>
    internal static string Request(string model, int? replyTokens, ReasoningControl control, bool? reasoning, bool tools)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteBoolean("stream", true);
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", TestPrompt);
            writer.WriteEndObject();
            writer.WriteEndArray();
            if (replyTokens is { } budget) writer.WriteNumber("max_tokens", budget);
            if (reasoning is { } think) GenerationSupport.WriteReasoning(writer, control, think);
            if (tools)
            {
                writer.WriteStartArray("tools");
                writer.WriteStartObject();
                writer.WriteString("type", "function");
                writer.WriteStartObject("function");
                writer.WriteString("name", "get_time");
                writer.WriteString("description", "Tells the current time. Use it only when asked for the time.");
                writer.WriteStartObject("parameters");
                writer.WriteString("type", "object");
                writer.WriteStartObject("properties");
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The message in an error reply: OpenAI's {"error":{"message"}}, {"error":"..."}, {"detail":"..."} or {"message"}.</summary>
    internal static string? ErrorText(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var text = root.TryGetProperty("error", out var error)
                ? error.ValueKind == JsonValueKind.String ? error.GetString() : error.ValueKind == JsonValueKind.Object ? JsonText(error, "message") : null
                : JsonText(root, "detail") ?? JsonText(root, "message");
            return text is null ? null : text.Length > 300 ? text[..300] + "…" : text.Trim().TrimEnd('.');
        }
        catch (JsonException)
        {
            var plain = body.Trim();
            return plain.Length == 0 || plain.StartsWith('<') ? null : plain.Length > 300 ? plain[..300] + "…" : plain;
        }
    }

    private static bool MentionsTools(string text) =>
        text.Contains("tool", StringComparison.OrdinalIgnoreCase) || text.Contains("jinja", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("function", StringComparison.OrdinalIgnoreCase);

    private static string? JsonText(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Seconds(TimeSpan time) => time.TotalSeconds < 10
        ? $"{time.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s"
        : $"{time.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s";
}
