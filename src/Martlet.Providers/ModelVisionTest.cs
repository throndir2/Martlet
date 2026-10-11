using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Martlet.Core.Settings;

namespace Martlet.Providers;

/// <summary>What testing whether a model sees found: <see cref="Sees"/> true when it read the test word, false when it answered
/// something else or its server refused the picture, null when the test couldn't tell (a key, a missing model, a busy or
/// unreachable server). <see cref="Summary"/> says it in words, naming the model but never a key. <see cref="Reply"/> is what
/// the model answered (at most 80 characters), <see cref="Milliseconds"/> how long it took.</summary>
public sealed record VisionTestReport(bool? Sees, string Summary, bool Reached, string? Reply = null, long? Milliseconds = null)
{
    /// <summary>The server's HTTP status (2xx: the model answered; 410: the server retired it); null when it didn't answer.</summary>
    public int? Status { get; init; }
}

/// <summary>Test vision (Companion › Vision): asks a model's own Chat Completions server, with one picture of a single word (drawn
/// on this PC, never anyone's screen) sent as an <c>image_url</c> PNG part, which word it shows. A model that sees reads it; one
/// that doesn't answers something else, or its server refuses the picture. Thinking steps are asked Off, so the answer is quick.
/// The key goes only to its own base URL and redirects aren't followed (use <see cref="ModelContextProbe.CreateClient"/>). A
/// paired computer's model gets the same question through its gateway (the desktop's image model runner), and
/// <see cref="Read"/> reads its answer the same way.</summary>
public static class ModelVisionTest
{
    /// <summary>Distinct, easy words; one is drawn per test, so a model that can't see can't guess it.</summary>
    public static IReadOnlyList<string> Words { get; } =
        ["pineapple", "umbrella", "butterfly", "lighthouse", "chocolate", "elephant", "volcano", "penguin", "giraffe", "telescope",
        "kangaroo", "snowman"];

    public const string Question = "The attached picture shows one English word. Reply with only that word.";

    public static async Task<VisionTestReport> RunAsync(HttpClient client, string baseUrl, string modelId, string? apiKey,
        BoundedImage picture, string word, string serverName, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(picture);
        var root = baseUrl.TrimEnd('/');
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(90));
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(root + "/chat/completions"))
            {
                Content = new ByteArrayContent(Body(root, modelId, picture))
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (apiKey is { Length: > 0 }) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            var text = await ModelHearingTest.ReadAsync(response, limit.Token).ConfigureAwait(false);
            var took = started.ElapsedMilliseconds;
            var code = (int)response.StatusCode;
            return Answer() with { Status = code };
            VisionTestReport Answer()
            {
                if (response.IsSuccessStatusCode) return Read(ModelHearingTest.ReplyOf(text), word, modelId, serverName, took);
                if (code is 401 or 403)
                    return new(null, $"{serverName} refused the test (error {code}); check the API key.", true, null, took);
                if (code == 410)
                    return new(null, $"{serverName} retired {modelId} (error 410).", true, null, took);
                if (code is 400 or 404 or 415 or 422 or 500 or 501 && RefusesImage(ModelHearingTest.WithoutModel(text, modelId)))
                    return new(false, $"{serverName} refused the picture for {modelId} (error {code}{ModelHearingTest.Detail(text)}), so it can't see.",
                        true, null, took);
                if (code == 404)
                    return new(null, $"{serverName} doesn't have {modelId} (error 404).", true, null, took);
                return new(null, $"{serverName} answered error {code}{ModelHearingTest.Detail(text)}, so Martlet can't tell whether {modelId} sees.",
                    true, null, took);
            }
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

    /// <summary>What a model's answer to the test question says: the test word means it sees, another answer that it doesn't seem
    /// to, and no answer that Martlet can't tell.</summary>
    public static VisionTestReport Read(string? reply, string word, string modelId, string serverName, long? milliseconds = null)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return new(null, $"{serverName} answered, but not with a reply Martlet could read, so it can't tell whether {modelId} sees.", true,
                null, milliseconds);
        var shown = ModelHearingTest.Shorten(reply);
        return ModelHearingTest.Letters(reply).Contains(ModelHearingTest.Letters(word), StringComparison.Ordinal)
            ? new(true, $"{modelId} read the test word (it said \"{shown}\"), so it sees pictures.", true, shown, milliseconds)
            : new(false, $"{modelId} answered \"{shown}\" instead of the test word, so it doesn't seem to see pictures. Test again to be sure.",
                true, shown, milliseconds);
    }

    /// <summary>The test request: the question and the picture, Thinking steps Off the way <paramref name="baseUrl"/>'s server
    /// family reads it, and a small reply budget where the server takes one.</summary>
    internal static byte[] Body(string baseUrl, string modelId, BoundedImage picture)
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
            writer.WriteString("type", "image_url");
            writer.WriteStartObject("image_url");
            writer.WriteString("url", picture.ToDataUrl());
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    // An error that is about the picture: the server or model takes no images.
    internal static bool RefusesImage(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("image", StringComparison.Ordinal) || lower.Contains("vision", StringComparison.Ordinal) ||
            lower.Contains("modalit", StringComparison.Ordinal) || lower.Contains("multimodal", StringComparison.Ordinal);
    }
}
