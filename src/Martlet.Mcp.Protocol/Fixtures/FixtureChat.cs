using System.Text.Json;

namespace Martlet.Mcp;

/// <summary>FIXTURE - NOT AI: the server-sent events of a streamed Chat Completions answer, as the checks' loopback models send
/// them (<see cref="LoopbackHttpServer"/>).</summary>
internal static class FixtureChat
{
    private const string Chunk = "{\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"fixture\",\"choices\":[{\"index\":0,";

    /// <summary>The usual canned reply's words.</summary>
    internal const string Words = "Fixture reply (not AI).";

    /// <summary>One chunk with this delta object (JSON), not finished.</summary>
    internal static string Delta(string delta) => "data: " + Chunk + "\"delta\":" + delta + ",\"finish_reason\":null}]}\n\n";

    /// <summary>One chunk with these words; <paramref name="role"/> marks the first chunk of the answer.</summary>
    internal static string Content(string words, bool role = true) =>
        Delta("{" + (role ? "\"role\":\"assistant\"," : "") + "\"content\":" + JsonSerializer.Serialize(words) + "}");

    /// <summary>The first chunk of a reasoning model: hidden reasoning, no words yet.</summary>
    internal static string Reasoning(string reasoning) =>
        Delta("{\"role\":\"assistant\",\"content\":\"\",\"reasoning\":" + JsonSerializer.Serialize(reasoning) + "}");

    /// <summary>The last chunk (finish_reason) and [DONE].</summary>
    internal static string Finish(string reason = "stop") =>
        "data: " + Chunk + "\"delta\":{},\"finish_reason\":\"" + reason + "\"}]}\n\ndata: [DONE]\n\n";

    /// <summary>A whole answer: these words in one chunk, then the end.</summary>
    internal static string Reply(string words = Words) => Content(words) + Finish();

    /// <summary>A Chat Completions model on 127.0.0.1 that records each request's body in <paramref name="requests"/> and answers
    /// it at once with <paramref name="events"/> (default <see cref="Reply"/>), one request after another.</summary>
    internal static LoopbackHttpServer Recording(List<byte[]> requests, Func<LoopbackRequest, string>? events = null, bool concurrent = false) =>
        new(exchange =>
        {
            lock (requests) requests.Add(exchange.Request.Body);
            return exchange.RespondAsync(200, events?.Invoke(exchange.Request) ?? Reply(), "text/event-stream");
        }, concurrent);
}
