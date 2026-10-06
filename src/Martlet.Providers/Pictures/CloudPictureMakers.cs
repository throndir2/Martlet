using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Pictures;
using Martlet.Core.Settings;

namespace Martlet.Providers.Pictures;

/// <summary>A paid cloud picture provider: the API key comes from <paramref name="key"/> for each picture (Windows Credential
/// Manager on the desktop) and is sent only to that provider. Only the description, shape and seed are sent.</summary>
public abstract class CloudPictureMaker(string model, Func<CancellationToken, Task<string?>> key, HttpMessageHandler? handler) : IPictureMaker, IDisposable
{
    public const int MaximumAnswerBytes = 48 * 1024 * 1024;
    private readonly HttpClient http = new(handler ?? new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }, disposeHandler: true)
    {
        Timeout = TimeSpan.FromMinutes(3)
    };

    public string Model { get; } = model;
    protected abstract string Provider { get; }
    protected abstract string Engine { get; }
    public string Where => $"{Provider} ({Model})";

    public async Task<PictureMakerAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        var secret = await key(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(secret)
            ? PictureMakerAvailability.Unavailable($"Add your {Provider} API key in Companion › Pictures.")
            : new(true, null, Where);
    }

    public async Task<PictureResult> GenerateAsync(PictureRequest request, IProgress<PictureProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var clock = Stopwatch.StartNew();
        var secret = await key(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(secret))
            throw new PictureException(PictureErrorCodes.NotAuthorized, $"Add your {Provider} API key in Companion › Pictures.");
        progress?.Report(new(PictureProgress.Drawing));
        var (bytes, seed) = await DrawAsync(request, secret, cancellationToken).ConfigureAwait(false);
        var (mediaType, width, height) = PictureImages.Require(bytes, Provider);
        return new()
        {
            Image = bytes, MediaType = mediaType, Width = width, Height = height, Seed = seed, Engine = Engine, Model = Model, Where = Where,
            Took = clock.Elapsed
        };
    }

    protected abstract Task<(byte[] Image, long? Seed)> DrawAsync(PictureRequest request, string secret, CancellationToken token);

    /// <summary>Posts <paramref name="body"/> and returns the answer's JSON, or throws with the provider's reason.</summary>
    protected async Task<JsonNode> PostAsync(Uri url, JsonObject body, string secret, CancellationToken token, Action<HttpRequestMessage>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToJsonString())) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } }
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        headers?.Invoke(request);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
        catch (HttpRequestException error) { throw new PictureException(PictureErrorCodes.Unavailable, $"{Provider} isn't reachable ({error.Message}).", error); }
        catch (TaskCanceledException error) when (!token.IsCancellationRequested)
        {
            throw new PictureException(PictureErrorCodes.TimedOut, $"{Provider} didn't answer in time.", error);
        }
        using (response)
        {
            byte[] bytes;
            await using (var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            using (var buffer = new MemoryStream())
            {
                var chunk = new byte[64 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > MaximumAnswerBytes) throw new PictureException(PictureErrorCodes.Failed, $"{Provider} sent too much.");
                }
                bytes = buffer.ToArray();
            }
            JsonNode? json = null;
            try { json = bytes.Length == 0 ? null : JsonNode.Parse(bytes); }
            catch (JsonException) { }
            if (response.IsSuccessStatusCode && json is not null) return json;
            var reason = Reason(json) ?? $"HTTP {(int)response.StatusCode}";
            throw new PictureException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => PictureErrorCodes.NotAuthorized,
                HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => PictureErrorCodes.Busy,
                HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity or HttpStatusCode.PaymentRequired =>
                    Filtered(reason) ? PictureErrorCodes.Refused : PictureErrorCodes.RequestInvalid,
                _ => PictureErrorCodes.Failed
            }, $"{Provider} didn't draw it: {reason}");
        }
    }

    protected static bool Filtered(string? text) => text is not null &&
        (text.Contains("moderat", StringComparison.OrdinalIgnoreCase) || text.Contains("safety", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("content_filter", StringComparison.OrdinalIgnoreCase) || text.Contains("CONTENT_FILTERED", StringComparison.Ordinal) ||
         text.Contains("policy", StringComparison.OrdinalIgnoreCase));

    private static string? Reason(JsonNode? json)
    {
        var text = json?["error"] switch
        {
            JsonObject error => error["message"]?.ToString() ?? error.ToJsonString(),
            JsonValue value => value.ToString(),
            _ => json?["detail"]?.ToString() ?? json?["message"]?.ToString() ?? json?["title"]?.ToString()
        };
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 300 ? text : text[..299] + "…";
    }

    protected static byte[] Base64(string? text, string provider)
    {
        if (string.IsNullOrEmpty(text)) throw new PictureException(PictureErrorCodes.Failed, $"{provider} returned no picture.");
        var comma = text.StartsWith("data:", StringComparison.Ordinal) ? text.IndexOf(',', StringComparison.Ordinal) : -1;
        try { return Convert.FromBase64String(comma >= 0 ? text[(comma + 1)..] : text); }
        catch (FormatException) { throw new PictureException(PictureErrorCodes.Failed, $"{provider} returned a picture Martlet couldn't read."); }
    }

    public void Dispose()
    {
        http.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>OpenRouter's image API (POST /api/v1/images): one picture at the 1K resolution tier in the request's aspect ratio.</summary>
public sealed class OpenRouterPictureMaker(string? model, Func<CancellationToken, Task<string?>> key, HttpMessageHandler? handler = null,
    string baseUrl = ChatCompletionsEndpointCatalog.OpenRouterBaseUrl)
    : CloudPictureMaker(model ?? PicturesSettings.OpenRouterDefaultModel, key, handler)
{
    public const string EngineName = "openrouter";
    protected override string Provider => "OpenRouter";
    protected override string Engine => EngineName;

    protected override async Task<(byte[] Image, long? Seed)> DrawAsync(PictureRequest request, string secret, CancellationToken token)
    {
        var prompt = request.NegativePrompt is { Length: > 0 } avoid ? $"{request.Prompt}\nAvoid: {avoid}" : request.Prompt;
        var body = new JsonObject
        {
            ["model"] = Model, ["prompt"] = prompt, ["aspect_ratio"] = PictureShapes.Ratio(request.Shape), ["resolution"] = "1K", ["n"] = 1
        };
        var answer = await PostAsync(new Uri(baseUrl.TrimEnd('/') + "/images"), body, secret, token, message =>
        {
            message.Headers.TryAddWithoutValidation("HTTP-Referer", ChatCompletionsEndpointCatalog.OpenRouterAppUrl);
            message.Headers.TryAddWithoutValidation("X-Title", ChatCompletionsEndpointCatalog.OpenRouterAppTitle);
        }).ConfigureAwait(false);
        var first = answer["data"] is JsonArray { Count: > 0 } data ? data[0] : null;
        if (first?["media_type"]?.ToString() is { } type && !PictureImages.MediaTypes.Contains(type))
            throw new PictureException(PictureErrorCodes.RequestInvalid, $"{Model} draws {type}, which Martlet can't keep. Choose another model.");
        return (Base64(first?["b64_json"]?.ToString(), Provider), null);
    }
}

/// <summary>NVIDIA Build's image models (POST ai.api.nvidia.com/v1/genai/&lt;model&gt;, the FLUX family): one JPEG picture.</summary>
public sealed class NvidiaPictureMaker(string? model, Func<CancellationToken, Task<string?>> key, HttpMessageHandler? handler = null,
    string baseUrl = PicturesSettings.NvidiaImageBaseUrl)
    : CloudPictureMaker(model ?? PicturesSettings.NvidiaDefaultModel, key, handler)
{
    public const string EngineName = "nvidia-build";
    protected override string Provider => "NVIDIA Build";
    protected override string Engine => EngineName;

    protected override async Task<(byte[] Image, long? Seed)> DrawAsync(PictureRequest request, string secret, CancellationToken token)
    {
        var seed = request.Seed ?? RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var url = new Uri(baseUrl.TrimEnd('/') + "/" + Model);
        JsonNode answer;
        try { answer = await PostAsync(url, Body(request.Width, request.Height), secret, token).ConfigureAwait(false); }
        // Some of NVIDIA's preview models draw only 1024 x 1024.
        catch (PictureException error) when (error.Code == PictureErrorCodes.RequestInvalid && (request.Width, request.Height) != (1024, 1024))
        {
            answer = await PostAsync(url, Body(1024, 1024), secret, token).ConfigureAwait(false);
        }
        var artifact = answer["artifacts"] is JsonArray { Count: > 0 } list ? list[0] : null;
        var reason = artifact?["finishReason"]?.ToString();
        if (reason == "CONTENT_FILTERED") throw new PictureException(PictureErrorCodes.Refused, "NVIDIA Build's content filter refused that picture.");
        if (reason is not null and not "SUCCESS") throw new PictureException(PictureErrorCodes.Failed, $"NVIDIA Build couldn't draw it ({reason}).");
        long? used = artifact?["seed"] is JsonValue value && value.TryGetValue<long>(out var s) ? s : seed;
        return (Base64(artifact?["base64"]?.ToString(), Provider), used);

        JsonObject Body(int width, int height)
        {
            var body = new JsonObject { ["prompt"] = request.Prompt, ["width"] = width, ["height"] = height, ["seed"] = seed };
            if (Model.Contains("schnell", StringComparison.OrdinalIgnoreCase)) body["steps"] = 4;
            return body;
        }
    }
}
