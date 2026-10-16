using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Martlet.Gateway.Ollama;

/// <summary>Translates OpenAI-compatible requests to Ollama's native API (<c>/api/chat</c>, <c>/api/generate</c>,
/// <c>/api/embed</c>) and its answers back, the way OpenAI's own clients expect them (docs/OPENAI_ENDPOINT.md).</summary>
public static class OllamaOpenAi
{
    public sealed class RefusedException(string message, string? param, int status = 400) : Exception(message)
    {
        public string? Param { get; } = param;
        public int Status { get; } = status;
    }

    internal static readonly ReadOnlyMemory<byte> Done = Encoding.UTF8.GetBytes("data: [DONE]\n\n");

    internal static byte[] Event(byte[] json)
    {
        var bytes = new byte[json.Length + 8];
        "data: "u8.CopyTo(bytes);
        json.CopyTo(bytes, 6);
        bytes[^2] = (byte)'\n';
        bytes[^1] = (byte)'\n';
        return bytes;
    }

    /// <summary>Ollama's error text from its error body (<c>{"error":"..."}</c>), short.</summary>
    public static string ErrorText(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String)
                body = error.GetString() ?? "";
        }
        catch (JsonException) { }
        body = body.Trim();
        return body.Length == 0 ? "no reason given." : body.Length > 300 ? body[..300] + "..." : body;
    }

    /// <summary>One translated request: the native path and body, whether it streams, and how each native answer becomes
    /// OpenAI's.</summary>
    public sealed class Translation
    {
        public required string NativePath { get; init; }
        public required byte[] Body { get; init; }
        public required bool Stream { get; init; }
        /// <summary>A whole native answer (no stream) as the OpenAI JSON.</summary>
        public required Func<JsonElement, byte[]> Finish { get; init; }
        /// <summary>One native stream line as zero or more SSE events, and whether it was the last.</summary>
        public Func<JsonElement, (IReadOnlyList<byte[]> Events, bool Done)> Next { get; init; } = static _ => ([], true);
    }

    private static string NewId(string prefix) => prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ---------- chat ----------

    public static Translation Chat(JsonElement request, string model, int context)
    {
        if (request.ValueKind != JsonValueKind.Object) throw new RefusedException("The request must be a JSON object.", null);
        if (!request.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0)
            throw new RefusedException("Send at least one message in messages.", "messages");
        RefuseMany(request);
        var stream = Bool(request, "stream") ?? false;
        var usage = request.TryGetProperty("stream_options", out var options) && options.ValueKind == JsonValueKind.Object &&
            (Bool(options, "include_usage") ?? false);
        using var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteStartArray("messages");
            var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var message in messages.EnumerateArray()) WriteMessage(writer, message, toolNames);
            writer.WriteEndArray();
            if (request.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0)
            {
                writer.WritePropertyName("tools");
                tools.WriteTo(writer);
            }
            WriteFormat(writer, request);
            WriteThink(writer, request);
            writer.WriteBoolean("stream", stream);
            writer.WriteNumber("keep_alive", -1);
            WriteOptions(writer, request, context);
            writer.WriteEndObject();
        }
        var id = NewId("chatcmpl-");
        var created = Now();
        var sawTools = false;
        var toolIndex = 0;
        var started = false;
        return new()
        {
            NativePath = "/api/chat",
            Body = body.ToArray(),
            Stream = stream,
            Finish = native =>
            {
                var message = native.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
                var calls = ToolCalls(message, ref toolIndex, withIndex: false);
                var reply = new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = Text(message, "content") ?? ""
                };
                if (Text(message, "thinking") is { Length: > 0 } thinking) reply["reasoning"] = thinking;
                if (calls.Count > 0) reply["tool_calls"] = calls;
                return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
                {
                    ["id"] = id, ["object"] = "chat.completion", ["created"] = created, ["model"] = model,
                    ["choices"] = new[] { new Dictionary<string, object?> { ["index"] = 0, ["message"] = reply, ["finish_reason"] = Finish(native, calls.Count > 0) } },
                    ["usage"] = Usage(native)
                });
            },
            Next = native =>
            {
                if (native.TryGetProperty("error", out var error))
                    return ([Event(GatewayOpenAiAnswer.ErrorBody(502, "The model stopped: " + ErrorText(error.ToString()), "worker.failed"))], true);
                var events = new List<byte[]>();
                var message = native.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
                var delta = new Dictionary<string, object?>();
                if (!started) { delta["role"] = "assistant"; started = true; }
                if (Text(message, "thinking") is { Length: > 0 } thinking) delta["reasoning"] = thinking;
                if (Text(message, "content") is { Length: > 0 } content) delta["content"] = content;
                var calls = ToolCalls(message, ref toolIndex, withIndex: true);
                if (calls.Count > 0) { delta["tool_calls"] = calls; sawTools = true; }
                var done = native.TryGetProperty("done", out var flag) && flag.ValueKind == JsonValueKind.True;
                if (delta.Count > 0 && (delta.Count > 1 || !done || !delta.ContainsKey("role")))
                    events.Add(Event(ChatChunk(id, created, model, delta, null)));
                if (!done) return (events, false);
                events.Add(Event(ChatChunk(id, created, model, [], Finish(native, sawTools))));
                if (usage)
                    events.Add(Event(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
                    {
                        ["id"] = id, ["object"] = "chat.completion.chunk", ["created"] = created, ["model"] = model,
                        ["choices"] = Array.Empty<object>(), ["usage"] = Usage(native)
                    })));
                return (events, true);
            }
        };
    }

    private static byte[] ChatChunk(string id, long created, string model, Dictionary<string, object?> delta, string? finish) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["id"] = id, ["object"] = "chat.completion.chunk", ["created"] = created, ["model"] = model,
            ["choices"] = new[] { new Dictionary<string, object?> { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish } }
        });

    private static string Finish(JsonElement native, bool tools) =>
        tools ? "tool_calls" : Text(native, "done_reason") == "length" ? "length" : "stop";

    private static Dictionary<string, object?> Usage(JsonElement native)
    {
        var prompt = Long(native, "prompt_eval_count") ?? 0;
        var completion = Long(native, "eval_count") ?? 0;
        return new() { ["prompt_tokens"] = prompt, ["completion_tokens"] = completion, ["total_tokens"] = prompt + completion };
    }

    private static List<Dictionary<string, object?>> ToolCalls(JsonElement message, ref int index, bool withIndex)
    {
        List<Dictionary<string, object?>> calls = [];
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("tool_calls", out var list) ||
            list.ValueKind != JsonValueKind.Array)
            return calls;
        foreach (var call in list.EnumerateArray())
        {
            if (!call.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object) continue;
            var arguments = function.TryGetProperty("arguments", out var given)
                ? given.ValueKind == JsonValueKind.String ? given.GetString()! : given.GetRawText() : "{}";
            var item = new Dictionary<string, object?>
            {
                ["id"] = NewId("call_"), ["type"] = "function",
                ["function"] = new Dictionary<string, object?> { ["name"] = Text(function, "name") ?? "", ["arguments"] = arguments }
            };
            if (withIndex) item = new() { ["index"] = index, ["id"] = item["id"], ["type"] = "function", ["function"] = item["function"] };
            index++;
            calls.Add(item);
        }
        return calls;
    }

    private static void WriteMessage(Utf8JsonWriter writer, JsonElement message, Dictionary<string, string> toolNames)
    {
        if (message.ValueKind != JsonValueKind.Object) throw new RefusedException("Each message must be a JSON object.", "messages");
        var role = Text(message, "role") switch
        {
            "system" or "developer" => "system",
            "user" => "user",
            "assistant" => "assistant",
            "tool" or "function" => "tool",
            _ => throw new RefusedException("A message's role must be system, developer, user, assistant or tool.", "messages")
        };
        var text = new StringBuilder();
        List<string> images = [];
        if (message.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String) text.Append(content.GetString());
            else if (content.ValueKind == JsonValueKind.Array)
                foreach (var part in content.EnumerateArray())
                {
                    switch (Text(part, "type"))
                    {
                        case "text" or "input_text":
                            if (text.Length > 0) text.Append('\n');
                            text.Append(Text(part, "text"));
                            break;
                        case "image_url" or "input_image":
                            var url = part.TryGetProperty("image_url", out var image)
                                ? image.ValueKind == JsonValueKind.String ? image.GetString() : Text(image, "url")
                                : Text(part, "url");
                            images.Add(DataUrl(url));
                            break;
                        case "input_audio":
                            var audio = part.TryGetProperty("input_audio", out var sound) ? Text(sound, "data") : null;
                            if (audio is not { Length: > 0 }) throw new RefusedException("input_audio needs its base64 data.", "messages");
                            images.Add(audio);
                            break;
                        default:
                            throw new RefusedException("Message parts may be text, image_url (as a data: URL) or input_audio.", "messages");
                    }
                }
            else if (content.ValueKind is not JsonValueKind.Null)
                throw new RefusedException("A message's content must be text or a list of parts.", "messages");
        }
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteString("content", text.ToString());
        if (images.Count > 0)
        {
            writer.WriteStartArray("images");
            foreach (var image in images) writer.WriteStringValue(image);
            writer.WriteEndArray();
        }
        if (role == "assistant" && message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray("tool_calls");
            foreach (var call in calls.EnumerateArray())
            {
                var function = call.TryGetProperty("function", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;
                var name = Text(function, "name") ?? "";
                if (Text(call, "id") is { } callId) toolNames[callId] = name;
                writer.WriteStartObject();
                writer.WriteStartObject("function");
                writer.WriteString("name", name);
                writer.WritePropertyName("arguments");
                WriteArguments(writer, function);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        if (role == "tool")
        {
            var name = Text(message, "name") ?? (Text(message, "tool_call_id") is { } callId && toolNames.TryGetValue(callId, out var known) ? known : null);
            if (name is not null) writer.WriteString("tool_name", name);
        }
        writer.WriteEndObject();
    }

    private static void WriteArguments(Utf8JsonWriter writer, JsonElement function)
    {
        if (function.ValueKind == JsonValueKind.Object && function.TryGetProperty("arguments", out var arguments))
        {
            if (arguments.ValueKind == JsonValueKind.Object) { arguments.WriteTo(writer); return; }
            if (arguments.ValueKind == JsonValueKind.String)
                try
                {
                    using var parsed = JsonDocument.Parse(arguments.GetString() ?? "{}");
                    if (parsed.RootElement.ValueKind == JsonValueKind.Object) { parsed.RootElement.WriteTo(writer); return; }
                }
                catch (JsonException) { }
        }
        writer.WriteStartObject();
        writer.WriteEndObject();
    }

    // A picture as Ollama takes it: the base64 of a data: URL. Martlet never fetches a web address for a request.
    private static string DataUrl(string? url)
    {
        if (url is { Length: > 5 } && url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && url.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase) is > 0 and var at)
            return url[(at + 8)..];
        throw new RefusedException("Send pictures as data: URLs (data:image/png;base64,...). The host doesn't fetch web addresses.", "messages");
    }

    private static void WriteFormat(Utf8JsonWriter writer, JsonElement request)
    {
        if (!request.TryGetProperty("response_format", out var format) || format.ValueKind != JsonValueKind.Object) return;
        switch (Text(format, "type"))
        {
            case "json_object":
                writer.WriteString("format", "json");
                break;
            case "json_schema":
                if (format.TryGetProperty("json_schema", out var wrapper) && wrapper.ValueKind == JsonValueKind.Object &&
                    wrapper.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.Object)
                {
                    writer.WritePropertyName("format");
                    schema.WriteTo(writer);
                }
                else writer.WriteString("format", "json");
                break;
        }
    }

    private static void WriteThink(Utf8JsonWriter writer, JsonElement request)
    {
        var effort = Text(request, "reasoning_effort") ??
            (request.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.Object ? Text(reasoning, "effort") : null);
        if (effort is null) return;
        if (effort is "none" or "minimal") writer.WriteBoolean("think", false);
        else if (effort is "low" or "medium" or "high") writer.WriteString("think", effort);
    }

    private static void WriteOptions(Utf8JsonWriter writer, JsonElement request, int context)
    {
        writer.WriteStartObject("options");
        // The owner's context size: a different one would make Ollama load the model again.
        writer.WriteNumber("num_ctx", context);
        Number(writer, request, "temperature", "temperature");
        Number(writer, request, "top_p", "top_p");
        Number(writer, request, "frequency_penalty", "frequency_penalty");
        Number(writer, request, "presence_penalty", "presence_penalty");
        Number(writer, request, "seed", "seed");
        if ((Long(request, "max_completion_tokens") ?? Long(request, "max_tokens")) is { } limit) writer.WriteNumber("num_predict", limit);
        if (request.TryGetProperty("stop", out var stop))
        {
            if (stop.ValueKind == JsonValueKind.String)
            {
                writer.WriteStartArray("stop");
                writer.WriteStringValue(stop.GetString());
                writer.WriteEndArray();
            }
            else if (stop.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray("stop");
                foreach (var item in stop.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Take(8)) writer.WriteStringValue(item.GetString());
                writer.WriteEndArray();
            }
        }
        writer.WriteEndObject();
    }

    private static void RefuseMany(JsonElement request)
    {
        if (Long(request, "n") is { } n && n != 1) throw new RefusedException("Martlet makes one answer per request; leave n at 1.", "n");
    }

    // ---------- completions ----------

    public static Translation Completion(JsonElement request, string model, int context)
    {
        if (request.ValueKind != JsonValueKind.Object) throw new RefusedException("The request must be a JSON object.", null);
        RefuseMany(request);
        var prompt = request.TryGetProperty("prompt", out var given) ? given.ValueKind switch
        {
            JsonValueKind.String => given.GetString(),
            JsonValueKind.Array when given.GetArrayLength() == 1 && given[0].ValueKind == JsonValueKind.String => given[0].GetString(),
            _ => null
        } : null;
        if (prompt is null) throw new RefusedException("Send the prompt as one string.", "prompt");
        var stream = Bool(request, "stream") ?? false;
        var usage = request.TryGetProperty("stream_options", out var options) && options.ValueKind == JsonValueKind.Object &&
            (Bool(options, "include_usage") ?? false);
        using var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteString("prompt", prompt);
            if (Text(request, "suffix") is { } suffix) writer.WriteString("suffix", suffix);
            writer.WriteBoolean("stream", stream);
            writer.WriteNumber("keep_alive", -1);
            WriteOptions(writer, request, context);
            writer.WriteEndObject();
        }
        var id = NewId("cmpl-");
        var created = Now();
        byte[] Chunk(string text, string? finish) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["id"] = id, ["object"] = "text_completion", ["created"] = created, ["model"] = model,
            ["choices"] = new[] { new Dictionary<string, object?> { ["text"] = text, ["index"] = 0, ["logprobs"] = null, ["finish_reason"] = finish } }
        });
        return new()
        {
            NativePath = "/api/generate",
            Body = body.ToArray(),
            Stream = stream,
            Finish = native => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
            {
                ["id"] = id, ["object"] = "text_completion", ["created"] = created, ["model"] = model,
                ["choices"] = new[] { new Dictionary<string, object?> { ["text"] = Text(native, "response") ?? "", ["index"] = 0, ["logprobs"] = null, ["finish_reason"] = Finish(native, false) } },
                ["usage"] = Usage(native)
            }),
            Next = native =>
            {
                if (native.TryGetProperty("error", out var error))
                    return ([Event(GatewayOpenAiAnswer.ErrorBody(502, "The model stopped: " + ErrorText(error.ToString()), "worker.failed"))], true);
                var done = native.TryGetProperty("done", out var flag) && flag.ValueKind == JsonValueKind.True;
                List<byte[]> events = [];
                if (Text(native, "response") is { Length: > 0 } text) events.Add(Event(Chunk(text, null)));
                if (!done) return (events, false);
                events.Add(Event(Chunk("", Finish(native, false))));
                if (usage)
                    events.Add(Event(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
                    {
                        ["id"] = id, ["object"] = "text_completion", ["created"] = created, ["model"] = model,
                        ["choices"] = Array.Empty<object>(), ["usage"] = Usage(native)
                    })));
                return (events, true);
            }
        };
    }

    // ---------- embeddings ----------

    public static Translation Embedding(JsonElement request, string model, int context)
    {
        if (request.ValueKind != JsonValueKind.Object) throw new RefusedException("The request must be a JSON object.", null);
        if (!request.TryGetProperty("input", out var input) ||
            !(input.ValueKind == JsonValueKind.String ||
              input.ValueKind == JsonValueKind.Array && input.GetArrayLength() is > 0 and <= 512 &&
              input.EnumerateArray().All(i => i.ValueKind == JsonValueKind.String)))
            throw new RefusedException("Send input as a string or a list of up to 512 strings.", "input");
        var base64 = Text(request, "encoding_format") == "base64";
        using var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WritePropertyName("input");
            input.WriteTo(writer);
            writer.WriteBoolean("truncate", true);
            if (Long(request, "dimensions") is { } dimensions) writer.WriteNumber("dimensions", dimensions);
            writer.WriteNumber("keep_alive", -1);
            writer.WriteStartObject("options");
            writer.WriteNumber("num_ctx", context);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return new()
        {
            NativePath = "/api/embed",
            Body = body.ToArray(),
            Stream = false,
            Finish = native =>
            {
                List<Dictionary<string, object?>> data = [];
                if (native.TryGetProperty("embeddings", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var vector in list.EnumerateArray())
                    {
                        var values = vector.EnumerateArray().Select(v => v.GetSingle()).ToArray();
                        object embedding = base64 ? Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan())) : values;
                        data.Add(new() { ["object"] = "embedding", ["index"] = data.Count, ["embedding"] = embedding });
                    }
                var prompt = Long(native, "prompt_eval_count") ?? 0;
                return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
                {
                    ["object"] = "list", ["data"] = data, ["model"] = model,
                    ["usage"] = new Dictionary<string, object?> { ["prompt_tokens"] = prompt, ["total_tokens"] = prompt }
                });
            }
        };
    }

    // ---------- JSON helpers ----------

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool? Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    private static long? Long(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number) ? number : null;

    private static void Number(Utf8JsonWriter writer, JsonElement request, string from, string to)
    {
        if (request.TryGetProperty(from, out var value) && value.ValueKind == JsonValueKind.Number)
        {
            writer.WritePropertyName(to);
            value.WriteTo(writer);
        }
    }
}
