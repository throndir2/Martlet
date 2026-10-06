using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Pictures;

namespace Martlet.Providers.Pictures;

/// <summary>The few ComfyUI calls Martlet uses (docs/PICTURES.md), so one <see cref="ComfyPictureMaker"/> draws on a ComfyUI the
/// owner runs (<see cref="ComfyHttpApi"/>, its own HTTP API) or on Martlet's <c>pictures</c> host role (the desktop's gateway
/// client, route <c>martlet.gateway.picture.v1</c>, whose operations are these). Failures throw <see cref="PictureException"/>.</summary>
public interface IComfyApi
{
    string Where { get; }

    /// <summary>ComfyUI's state: <c>state</c> (ready, loading or not_provisioned), <c>comfyui_version</c>, <c>models</c> (folder →
    /// file names) and <c>devices</c>.</summary>
    Task<JsonObject> StatusAsync(CancellationToken cancellationToken);

    /// <summary>Queues an API-format workflow and returns its prompt ID. A workflow ComfyUI rejects throws
    /// <see cref="PictureErrorCodes.RequestInvalid"/> with ComfyUI's reason.</summary>
    Task<string> QueueAsync(JsonObject workflow, CancellationToken cancellationToken);

    /// <summary>The history entry of <paramref name="promptId"/> (its <c>outputs</c> and <c>status</c>), or null while it isn't done.</summary>
    Task<JsonObject?> HistoryAsync(string promptId, CancellationToken cancellationToken);

    /// <summary>ComfyUI's queue: <c>queue_running</c> and <c>queue_pending</c> (lists of [number, prompt_id, ...]).</summary>
    Task<JsonObject> QueueStateAsync(CancellationToken cancellationToken);

    /// <summary>The bytes of an output file.</summary>
    Task<byte[]> ViewAsync(string filename, string subfolder, string type, CancellationToken cancellationToken);

    /// <summary>Takes <paramref name="promptId"/> off the queue, or interrupts it while it runs.</summary>
    Task CancelAsync(string promptId, CancellationToken cancellationToken);

    /// <summary>Unloads ComfyUI's models so the graphics card is free for other work.</summary>
    Task FreeAsync(CancellationToken cancellationToken);
}

/// <summary>A ComfyUI the owner runs, through its own HTTP API at <see cref="Address"/> (this PC, another computer on the network
/// or a server). Nothing but the workflow and the picture's file name is sent.</summary>
public sealed class ComfyHttpApi : IComfyApi, IDisposable
{
    public const int MaximumJsonBytes = 4 * 1024 * 1024;
    private static readonly string[] Folders = ["checkpoints", "diffusion_models", "text_encoders", "vae", "loras"];
    private readonly HttpClient http;
    private readonly string clientId = "martlet-" + Guid.NewGuid().ToString("N")[..12];

    public ComfyHttpApi(string address, HttpMessageHandler? handler = null)
    {
        Address = PicturesSettings.ComfyAddress(address);
        http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = DecompressionMethods.All
        }, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public string Address { get; }
    public string Where => $"ComfyUI at {Address}";

    private Uri At(string path) => new(Address + "/" + path);

    public async Task<JsonObject> StatusAsync(CancellationToken cancellationToken)
    {
        var stats = await JsonAsync(HttpMethod.Get, "system_stats", null, cancellationToken).ConfigureAwait(false) as JsonObject ?? [];
        var models = new JsonObject();
        foreach (var folder in Folders)
        {
            try
            {
                if (await JsonAsync(HttpMethod.Get, "models/" + folder, null, cancellationToken).ConfigureAwait(false) is JsonArray files)
                    models[folder] = files.DeepClone();
            }
            catch (PictureException error) when (error.Code == PictureErrorCodes.Failed) { }
        }
        var devices = stats["devices"] is JsonArray list ? list.DeepClone() : new JsonArray();
        return new JsonObject
        {
            ["state"] = "ready",
            ["engine"] = "comfyui",
            ["comfyui_version"] = stats["system"]?["comfyui_version"]?.DeepClone(),
            ["models"] = models,
            ["devices"] = devices
        };
    }

    public async Task<string> QueueAsync(JsonObject workflow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var body = new JsonObject { ["prompt"] = workflow.DeepClone(), ["client_id"] = clientId };
        var answer = await JsonAsync(HttpMethod.Post, "prompt", body, cancellationToken, acceptBadRequest: true).ConfigureAwait(false) as JsonObject;
        if (answer?["prompt_id"] is JsonValue value && value.ToString() is { Length: > 0 and <= 128 } id) return id;
        throw new PictureException(PictureErrorCodes.RequestInvalid, $"{Where} rejected the workflow: {ComfyErrors.Describe(answer)}");
    }

    public async Task<JsonObject?> HistoryAsync(string promptId, CancellationToken cancellationToken)
    {
        var answer = await JsonAsync(HttpMethod.Get, "history/" + Uri.EscapeDataString(promptId), null, cancellationToken).ConfigureAwait(false);
        return answer?[promptId] as JsonObject;
    }

    public async Task<JsonObject> QueueStateAsync(CancellationToken cancellationToken) =>
        await JsonAsync(HttpMethod.Get, "queue", null, cancellationToken).ConfigureAwait(false) as JsonObject ?? [];

    public async Task<byte[]> ViewAsync(string filename, string subfolder, string type, CancellationToken cancellationToken)
    {
        var query = string.Create(CultureInfo.InvariantCulture,
            $"view?filename={Uri.EscapeDataString(filename)}&subfolder={Uri.EscapeDataString(subfolder)}&type={Uri.EscapeDataString(type)}");
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, At(query)), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new PictureException(PictureErrorCodes.Failed, $"{Where} couldn't hand over the picture (HTTP {(int)response.StatusCode}).");
        return await ReadAsync(response.Content, PictureImages.MaximumBytes, cancellationToken).ConfigureAwait(false) ??
            throw new PictureException(PictureErrorCodes.Failed, $"{Where} returned a picture that is too large.");
    }

    public async Task CancelAsync(string promptId, CancellationToken cancellationToken)
    {
        var queue = await QueueStateAsync(cancellationToken).ConfigureAwait(false);
        await JsonAsync(HttpMethod.Post, "queue", new JsonObject { ["delete"] = new JsonArray(promptId) }, cancellationToken).ConfigureAwait(false);
        if (ComfyErrors.Running(queue).Contains(promptId))
            await JsonAsync(HttpMethod.Post, "interrupt", new JsonObject(), cancellationToken).ConfigureAwait(false);
    }

    public async Task FreeAsync(CancellationToken cancellationToken) =>
        await JsonAsync(HttpMethod.Post, "free", new JsonObject { ["unload_models"] = true, ["free_memory"] = true }, cancellationToken)
            .ConfigureAwait(false);

    private async Task<JsonNode?> JsonAsync(HttpMethod method, string path, JsonObject? body, CancellationToken token, bool acceptBadRequest = false)
    {
        var request = new HttpRequestMessage(method, At(path));
        if (body is not null)
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToJsonString())) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
        using var response = await SendAsync(request, token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK && !(acceptBadRequest && response.StatusCode == HttpStatusCode.BadRequest))
            throw new PictureException(PictureErrorCodes.Failed, $"{Where} answered HTTP {(int)response.StatusCode} to {path.Split('?')[0]}.");
        var bytes = await ReadAsync(response.Content, MaximumJsonBytes, token).ConfigureAwait(false) ??
            throw new PictureException(PictureErrorCodes.Failed, $"{Where} sent too much for {path}.");
        if (bytes.Length == 0) return null;
        try { return JsonNode.Parse(bytes); }
        catch (JsonException) { throw new PictureException(PictureErrorCodes.Failed, $"{Where} didn't answer like ComfyUI."); }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using (request)
        {
            try { return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
            catch (HttpRequestException error)
            {
                throw new PictureException(PictureErrorCodes.Unavailable, $"{Where} isn't reachable ({error.Message}).", error);
            }
            catch (TaskCanceledException error) when (!token.IsCancellationRequested)
            {
                throw new PictureException(PictureErrorCodes.Unavailable, $"{Where} didn't answer in time.", error);
            }
        }
    }

    private static async Task<byte[]?> ReadAsync(HttpContent content, int maximum, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maximum) return null;
        }
        return buffer.ToArray();
    }

    public void Dispose() => http.Dispose();
}

/// <summary>Reading ComfyUI's answers: its validation errors in words, the queue, and a finished prompt's pictures.</summary>
public static class ComfyErrors
{
    /// <summary>ComfyUI's reason for rejecting a workflow ({"error": {...}, "node_errors": {...}}), in one bounded line.</summary>
    public static string Describe(JsonNode? answer)
    {
        var parts = new List<string>();
        if (answer?["error"] is JsonObject error)
        {
            var message = error["message"]?.ToString() ?? error["summary"]?.ToString() ?? error["type"]?.ToString();
            if (!string.IsNullOrWhiteSpace(message)) parts.Add(message.Trim().TrimEnd('.'));
            if (error["details"]?.ToString() is { Length: > 0 } details) parts.Add(details.Trim());
        }
        else if (answer?["error"]?.ToString() is { Length: > 0 } text) parts.Add(text);
        if (answer?["node_errors"] is JsonObject nodes)
            foreach (var (_, node) in nodes)
                if (node?["errors"] is JsonArray errors)
                    foreach (var item in errors.Take(3))
                        if ($"{item?["message"]}: {item?["details"]}".Trim(' ', ':') is { Length: > 0 } line) parts.Add(line);
        var joined = parts.Count == 0 ? "no reason given" : string.Join("; ", parts.Distinct());
        return joined.Length <= 300 ? joined : joined[..299] + "…";
    }

    /// <summary>The prompt IDs ComfyUI is running now.</summary>
    public static IReadOnlyList<string> Running(JsonObject queue) => Ids(queue["queue_running"]);

    /// <summary>The prompt IDs waiting, in order.</summary>
    public static IReadOnlyList<string> Pending(JsonObject queue) =>
        Ids(queue["queue_pending"] is JsonArray pending
            ? new JsonArray([.. pending.OrderBy(item => item is JsonArray entry && entry.Count > 0 && entry[0] is JsonValue number &&
                number.TryGetValue<double>(out var n) ? n : 0).Select(item => item?.DeepClone())])
            : null);

    private static IReadOnlyList<string> Ids(JsonNode? list) =>
        list is JsonArray items
            ? [.. items.OfType<JsonArray>().Where(entry => entry.Count > 1).Select(entry => entry[1]?.ToString()).OfType<string>()]
            : [];

    /// <summary>The first picture a finished prompt saved (outputs' images, an "output" one first): its file name, subfolder and type.</summary>
    public static (string Filename, string Subfolder, string Type)? Picture(JsonObject history)
    {
        var found = new List<(string, string, string)>();
        if (history["outputs"] is JsonObject outputs)
            foreach (var (_, output) in outputs)
                if (output?["images"] is JsonArray images)
                    foreach (var image in images.OfType<JsonObject>())
                        if (image["filename"]?.ToString() is { Length: > 0 } name)
                            found.Add((name, image["subfolder"]?.ToString() ?? "", image["type"]?.ToString() ?? "output"));
        return found.Count == 0 ? null : found.OrderBy(f => f.Item3 == "output" ? 0 : 1).First();
    }

    /// <summary>Why a finished prompt failed, from its status messages, or null when it didn't.</summary>
    public static string? Failure(JsonObject history)
    {
        if (history["status"] is not JsonObject status || status["status_str"]?.ToString() != "error") return null;
        if (status["messages"] is JsonArray messages)
            foreach (var message in messages.OfType<JsonArray>())
                if (message.Count > 1 && message[0]?.ToString() == "execution_error" && message[1] is JsonObject detail)
                {
                    var text = $"{detail["node_type"]}: {detail["exception_message"]}".Trim(' ', ':');
                    return text.Length <= 300 ? text : text[..299] + "…";
                }
        return "the workflow failed";
    }
}
