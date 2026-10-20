using System.Net;
using System.Text.Json;
using Martlet.Providers;
using Martlet.Providers.Ollama;

namespace Martlet.Gateway.Ollama;

/// <summary>The deep-thinking-app role (<see cref="OllamaRelayWorker.App"/>): a Thinking pool request goes to the owner's own model
/// app on this host through its OpenAI-compatible Chat Completions API, streamed (server-sent events), and comes back as the same
/// text events Martlet's own Ollama gives.</summary>
public sealed partial class OllamaRelayWorker
{
    private async Task<(HttpResponseMessage? Response, string? Failure)> SendAppAsync(Uri target, GatewayOllamaChatPayload payload,
        CancellationToken token)
    {
        var body = AppRequest(model, payload);
        var response = await PostAsync(target, body, token).ConfigureAwait(false);
        if (response is null) return (null, "worker.unavailable");
        if (response.StatusCode == HttpStatusCode.OK) return (response, null);
        var error = await ErrorAsync(response, token).ConfigureAwait(false);
        var status = response.StatusCode;
        response.Dispose();
        // A model that doesn't hear or see refuses the recording: request.invalid tells the client to send the words alone.
        if (payload.Audio is not null && status == HttpStatusCode.BadRequest && RefusesAudio(error)) return (null, "request.invalid");
        // 404: the app no longer serves the model (it was unloaded or removed there).
        return (null, status is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable ? "worker.unavailable" : "worker.failed");
    }

    /// <summary>The OpenAI-compatible request for <paramref name="payload"/>: the system text, the history and the user's message
    /// (with its pictures as data: URLs and a recording as <c>input_audio</c>), streamed, with the reply budget and sampling.</summary>
    public static byte[] AppRequest(string model, GatewayOllamaChatPayload payload)
    {
        using var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteStartArray("messages");
            if (payload.System is { } system) Message(writer, "system", system);
            foreach (var message in payload.History)
                Message(writer, message.Role == TextHistoryRole.User ? "user" : "assistant", message.Text);
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            if (payload.Images.Count == 0 && payload.Audio is null) writer.WriteString("content", payload.Input);
            else
            {
                writer.WriteStartArray("content");
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", payload.Input);
                writer.WriteEndObject();
                foreach (var image in payload.Images)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "image_url");
                    writer.WriteStartObject("image_url");
                    writer.WriteString("url", "data:" + ImageType(image) + ";base64," + image);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                if (payload.Audio is { } audio)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "input_audio");
                    writer.WriteStartObject("input_audio");
                    writer.WriteString("data", audio);
                    writer.WriteString("format", "wav");
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteBoolean("stream", true);
            writer.WriteNumber("temperature", payload.Temperature);
            writer.WriteNumber("max_tokens", payload.MaximumOutputTokens);
            var sampling = payload.Sampling;
            if (sampling.TopP is { } topP) writer.WriteNumber("top_p", topP);
            if (sampling.FrequencyPenalty is { } frequency) writer.WriteNumber("frequency_penalty", frequency);
            if (sampling.PresencePenalty is { } presence) writer.WriteNumber("presence_penalty", presence);
            writer.WriteEndObject();
        }
        return body.ToArray();
    }

    /// <summary>The media type of a base64 picture from its first bytes (PNG, JPEG, GIF or WebP; PNG when unknown).</summary>
    private static string ImageType(string base64) => base64 switch
    {
        _ when base64.StartsWith("/9j/", StringComparison.Ordinal) => "image/jpeg",
        _ when base64.StartsWith("R0lG", StringComparison.Ordinal) => "image/gif",
        _ when base64.StartsWith("UklG", StringComparison.Ordinal) => "image/webp",
        _ => "image/png"
    };

    // Reads one line of the app's server-sent events: "data: {...}" with choices[0].delta.content, "data: [DONE]" at the end,
    // blank lines and ": comments" between, or {"error":...}.
    private static async Task<(string? Text, bool Done, string? Error)> NextAppAsync(LineReader reader, bool audio, CancellationToken token)
    {
        byte[]? line;
        try { line = await reader.ReadLineAsync(token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidDataException)
        {
            token.ThrowIfCancellationRequested();
            return (null, false, "worker.failed");
        }
        if (line is null) return (null, true, null);
        return AppLine(line, audio);
    }

    /// <summary>What one line of an app's stream says (see <see cref="NextAppAsync"/>).</summary>
    public static (string? Text, bool Done, string? Error) AppLine(ReadOnlySpan<byte> line, bool audio)
    {
        var text = System.Text.Encoding.UTF8.GetString(line).TrimEnd('\r');
        if (text.Length == 0 || text.StartsWith(':')) return (null, false, null);
        if (!text.StartsWith("data:", StringComparison.Ordinal)) return (null, false, null);
        var data = text[5..].Trim();
        if (data == "[DONE]") return (null, true, null);
        try
        {
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, false, "worker.failed");
            if (root.TryGetProperty("error", out var refusal))
                return (null, false, audio && RefusesAudio(refusal.ToString()) ? "request.invalid" : "worker.failed");
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return (null, false, null);
            var choice = choices[0];
            string? content = null;
            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object &&
                delta.TryGetProperty("content", out var piece) && piece.ValueKind == JsonValueKind.String)
                content = piece.GetString();
            // The answer ends with finish_reason, and [DONE] follows; an app that closes the stream after it ends there too.
            return (content, false, null);
        }
        catch (JsonException) { return (null, false, "worker.failed"); }
    }
}
