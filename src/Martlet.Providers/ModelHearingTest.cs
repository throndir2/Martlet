using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Martlet.Core.Settings;

namespace Martlet.Providers;

/// <summary>What testing whether a Thinking model hears found: <see cref="Hears"/> true when it said the test word, false when
/// it answered something else or its server refused the recording, null when the test couldn't tell (a key, a missing model,
/// a busy or unreachable server). <see cref="Summary"/> says it in words, naming the model but never a key.
/// <see cref="Reply"/> is what the model answered (at most 80 characters), <see cref="Milliseconds"/> how long it took.</summary>
public sealed record HearingTestReport(bool? Hears, string Summary, bool Reached, string? Reply = null, long? Milliseconds = null);

/// <summary>Companion › Listening › Test hearing: asks the Thinking model's own Chat Completions server, with one short recording
/// of a single word (made by Windows speech, never anyone's voice) sent as an <c>input_audio</c> WAV part, which word it says.
/// A model that hears says it; one that doesn't answers something else, or its server refuses the audio. Thinking steps are
/// asked Off, so the answer is quick. The key goes only to its own base URL and redirects aren't followed (use
/// <see cref="ModelContextProbe.CreateClient"/>).</summary>
public static class ModelHearingTest
{
    /// <summary>Distinct, easy words; one is chosen per test, so a model that can't hear can't guess it.</summary>
    public static IReadOnlyList<string> Words { get; } = ["pineapple", "umbrella", "butterfly", "lighthouse", "chocolate", "elephant", "volcano", "penguin"];

    public const string Question = "The attached recording says one English word. Reply with only that word.";

    /// <summary>What Windows speech says for <paramref name="word"/>: the word twice, so a short clip is still clear.</summary>
    public static string Spoken(string word) => $"{word}. {word}.";

    public static async Task<HearingTestReport> RunAsync(HttpClient client, string baseUrl, string modelId, string? apiKey,
        BoundedWaveAudio clip, string word, string serverName, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(clip);
        var root = baseUrl.TrimEnd('/');
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(90));
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(root + "/chat/completions"))
            {
                Content = new ByteArrayContent(Body(root, modelId, clip))
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (apiKey is { Length: > 0 }) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            var text = await ReadAsync(response, limit.Token).ConfigureAwait(false);
            var took = started.ElapsedMilliseconds;
            var code = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                var reply = ReplyOf(text);
                if (reply is null)
                    return new(null, $"{serverName} answered, but not with a reply Martlet could read, so it can't tell whether {modelId} hears.", true,
                        null, took);
                var shown = Shorten(reply);
                return Letters(reply).Contains(Letters(word), StringComparison.Ordinal)
                    ? new(true, $"{modelId} heard the test word (it said \"{shown}\").", true, shown, took)
                    : new(false, $"{modelId} answered \"{shown}\" instead of the test word, so it doesn't seem to hear recordings. Test again to be sure.",
                        true, shown, took);
            }
            if (code is 401 or 403)
                return new(null, $"{serverName} refused the test (error {code}); check the API key.", true, null, took);
            if (code is 400 or 404 or 415 or 422 or 500 or 501 && RefusesAudio(WithoutModel(text, modelId)))
                return new(false, $"{serverName} refused the recording for {modelId} (error {code}{Detail(text)}), so it can't hear.", true, null, took);
            if (code == 404)
                return new(null, $"{serverName} doesn't have {modelId} (error 404).", true, null, took);
            return new(null, $"{serverName} answered error {code}{Detail(text)}, so Martlet can't tell whether {modelId} hears.", true, null, took);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(null, $"{serverName} didn't answer within 90 seconds.", false);
        }
        catch (HttpRequestException error)
        {
            return new(null, $"Couldn't reach {serverName}: {(error.HttpRequestError switch
            {
                HttpRequestError.ConnectionError => "nothing answered at its address",
                HttpRequestError.NameResolutionError => "its address couldn't be found",
                HttpRequestError.SecureConnectionError => "its secure connection failed",
                _ => "the connection failed"
            })}.", false);
        }
        catch (IOException) { return new(null, $"The connection to {serverName} dropped.", false); }
    }

    /// <summary>The test request: the question and the recording, Thinking steps Off the way <paramref name="baseUrl"/>'s server
    /// family reads it, and a small reply budget where the server takes one.</summary>
    internal static byte[] Body(string baseUrl, string modelId, BoundedWaveAudio clip)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", modelId);
            writer.WriteBoolean("stream", false);
            writer.WriteNumber("temperature", 0);
            if (GenerationSupport.SendsReplyBudget(baseUrl, null)) writer.WriteNumber("max_tokens", 32);
            GenerationSupport.WriteReasoning(writer, GenerationSupport.ChatReasoning(baseUrl), false);
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteStartArray("content");
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", Question);
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("type", "input_audio");
            writer.WriteStartObject("input_audio");
            writer.WriteString("data", clip.ToBase64());
            writer.WriteString("format", "wav");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    // Shared with ModelVisionTest: the answer's body (at most 256 KiB), its reply text, the server's error message, the letters
    // of a reply and a one-line reply of at most 80 characters.
    internal static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var chunk = new byte[16_384];
        using var body = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0 && body.Length < 262_144) body.Write(chunk, 0, read);
        return Encoding.UTF8.GetString(body.ToArray());
    }

    // choices[0].message.content: a string, or a list of parts with text.
    internal static string? ReplyOf(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0 || !choices[0].TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content)) return null;
            return content.ValueKind switch
            {
                JsonValueKind.String => content.GetString(),
                JsonValueKind.Array => string.Concat(content.EnumerateArray()
                    .Where(p => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    .Select(p => p.GetProperty("text").GetString())),
                _ => null
            };
        }
        catch (JsonException) { return null; }
    }

    // An error that is about the recording: the server or model takes no audio.
    internal static bool RefusesAudio(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("audio", StringComparison.Ordinal) || lower.Contains("modalit", StringComparison.Ordinal) ||
            lower.Contains("multimodal", StringComparison.Ordinal);
    }

    // An error's text without the model's name (its full ID, the part after the last slash and the part before a tag), so a model
    // named for audio or vision that the server doesn't have ("model 'llama3.2-vision' not found") doesn't read as a refused
    // recording or picture. Shared with ModelVisionTest.
    internal static string WithoutModel(string text, string modelId)
    {
        var last = modelId[(modelId.LastIndexOf('/') + 1)..];
        foreach (var name in new[] { modelId, last, last.Split(':')[0] }.Where(n => n.Length > 0).Distinct().OrderByDescending(n => n.Length))
            text = text.Replace(name, "", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    internal static string Detail(string text)
    {
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var error = document.RootElement.TryGetProperty("error", out var e) ? e : document.RootElement;
            message = error.ValueKind == JsonValueKind.String ? error.GetString()
                : error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()
                : null;
        }
        catch (JsonException) { }
        return message is { Length: > 0 } ? ": " + Shorten(message) : "";
    }

    internal static string Letters(string text) => new([.. text.Where(char.IsLetter).Select(char.ToLowerInvariant)]);

    internal static string Shorten(string text)
    {
        var one = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return one.Length <= 80 ? one : one[..79] + "…";
    }
}
