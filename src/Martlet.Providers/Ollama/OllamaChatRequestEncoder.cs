using System.Net.Http.Headers;
using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Providers.Ollama;

internal static class OllamaChatRequestEncoder
{
    internal const int MaxRequestBytes = 131_072;

    internal static HttpContent Encode(OllamaChatAction action, Action ensureActive)
    {
        ensureActive();
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteString("model", action.Selection.RequestModel);
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", action.Input.UserText);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteBoolean("stream", true);
            writer.WriteBoolean("think", false);
            writer.WriteStartArray("tools");
            writer.WriteEndArray();
            writer.WriteBoolean("truncate", false);
            writer.WriteBoolean("shift", false);
            writer.WriteBoolean("logprobs", false);
            writer.WriteStartObject("options");
            writer.WriteNumber("temperature", action.Options.Temperature);
            writer.WriteNumber("num_predict", action.Limits.MaxOutputTokens);
            writer.WriteNumber("num_ctx", action.Limits.MaxContextTokens);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        ContractRules.Require(bytes.Length <= MaxRequestBytes, "Encoded Ollama input exceeds its byte bound.",
            ErrorCode.PayloadTooLarge);
        var content = new ByteArrayContent(bytes.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return new SingleSendContent(content, ensureActive);
    }
}
