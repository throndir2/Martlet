using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Martlet.Core.Settings;

namespace Martlet.Providers;

/// <summary>What testing whether a model calls tools found: <see cref="Tools"/> true when it called the test tool, false when it
/// answered in words or its server refused the tool, null when the test couldn't tell (a key, a missing or retired model, a busy
/// or unreachable server). <see cref="Summary"/> says it in words, naming the model but never a key. <see cref="Reply"/> is what
/// it answered (the tool's name and word, or at most 80 characters of text), <see cref="Milliseconds"/> how long it took and
/// <see cref="Status"/> the server's HTTP status (410: the model is retired).</summary>
public sealed record ToolTestReport(bool? Tools, string Summary, bool Reached, string? Reply = null, long? Milliseconds = null)
{
    public int? Status { get; init; }
}

/// <summary>Test tools (Companion › Thinking): asks a model's own Chat Completions server, with one made-up tool
/// (<see cref="ToolName"/>, a test word as its one argument) and a request to call it with a word Martlet chose, whether it
/// calls tools. Nothing the owner said, typed or showed is sent. Thinking steps are asked Off, so the answer is quick. The key
/// goes only to its own base URL and redirects aren't followed (use <see cref="ModelContextProbe.CreateClient"/>).</summary>
public static class ModelToolTest
{
    public const string ToolName = "say_test_word";

    public static string Question(string word) =>
        $"This is a test of tool calls. Call the {ToolName} tool with the word \"{word}\". Don't answer in words.";

    public static async Task<ToolTestReport> RunAsync(HttpClient client, string baseUrl, string modelId, string? apiKey, string word,
        string serverName, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(client);
        var root = baseUrl.TrimEnd('/');
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(90));
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(root + "/chat/completions"))
            {
                Content = new ByteArrayContent(Body(root, modelId, word))
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (apiKey is { Length: > 0 }) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            var text = await ModelHearingTest.ReadAsync(response, limit.Token).ConfigureAwait(false);
            var took = started.ElapsedMilliseconds;
            var code = (int)response.StatusCode;
            ToolTestReport Report(bool? tools, string summary, string? reply = null) =>
                new(tools, summary, true, reply, took) { Status = code };
            if (response.IsSuccessStatusCode)
            {
                if (CallOf(text) is { } called)
                    return Report(true, $"{modelId} called the test tool ({called}), so it calls tools.", called);
                return ModelHearingTest.ReplyOf(text) is { Length: > 0 } words
                    ? Report(false, $"{modelId} answered \"{ModelHearingTest.Shorten(words)}\" instead of calling the test tool, so it " +
                        "doesn't seem to call tools. Test again to be sure.", ModelHearingTest.Shorten(words))
                    : Report(null, $"{serverName} answered, but not with a reply Martlet could read, so it can't tell whether {modelId} calls tools.");
            }
            if (code is 401 or 403) return Report(null, $"{serverName} refused the test (error {code}); check the API key.");
            if (code == 410) return Report(null, $"{serverName} retired {modelId} (error 410).");
            if (code is 400 or 404 or 422 or 500 or 501 && RefusesTools(ModelHearingTest.WithoutModel(text, modelId)))
                return Report(false, $"{serverName} refused the tool for {modelId} (error {code}{ModelHearingTest.Detail(text)}), so it can't call tools.");
            if (code == 404) return Report(null, $"{serverName} doesn't have {modelId} (error 404).");
            return Report(null, $"{serverName} answered error {code}{ModelHearingTest.Detail(text)}, so Martlet can't tell whether {modelId} calls tools.");
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

    /// <summary>The test request: the request to call the tool, the one made-up tool, Thinking steps Off the way
    /// <paramref name="baseUrl"/>'s server family reads it, and a small reply budget where the server takes one.</summary>
    internal static byte[] Body(string baseUrl, string modelId, string word)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", modelId);
            writer.WriteBoolean("stream", false);
            writer.WriteNumber("temperature", 0);
            if (GenerationSupport.SendsReplyBudget(baseUrl, null)) writer.WriteNumber("max_tokens", 64);
            GenerationSupport.WriteReasoning(writer, GenerationSupport.ChatReasoning(baseUrl), false);
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", Question(word));
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteStartArray("tools");
            writer.WriteStartObject();
            writer.WriteString("type", "function");
            writer.WriteStartObject("function");
            writer.WriteString("name", ToolName);
            writer.WriteString("description", "Says the test word. Martlet uses it only to test whether the model calls tools.");
            writer.WriteStartObject("parameters");
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");
            writer.WriteStartObject("word");
            writer.WriteString("type", "string");
            writer.WriteString("description", "The test word.");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteStartArray("required");
            writer.WriteStringValue("word");
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    // choices[0].message.tool_calls[0]: the tool's name and its word, when the model called the test tool.
    internal static string? CallOf(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0 || !choices[0].TryGetProperty("message", out var message) ||
                !message.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var call in calls.EnumerateArray())
                if (call.ValueKind == JsonValueKind.Object && call.TryGetProperty("function", out var function) &&
                    function.ValueKind == JsonValueKind.Object && function.TryGetProperty("name", out var name) &&
                    name.ValueKind == JsonValueKind.String && name.GetString() == ToolName)
                {
                    var arguments = function.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
                    return ModelHearingTest.Shorten($"{ToolName} {arguments ?? ""}".Trim());
                }
            return null;
        }
        catch (JsonException) { return null; }
    }

    // An error that is about the tool: the server or model takes no tools.
    internal static bool RefusesTools(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("tool", StringComparison.Ordinal) || lower.Contains("function", StringComparison.Ordinal);
    }
}
