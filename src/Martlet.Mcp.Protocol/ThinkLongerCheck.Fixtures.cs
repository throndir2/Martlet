using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;
using Martlet.Providers.Ollama;

namespace Martlet.Mcp;

internal static partial class ThinkLongerCheck
{
    private sealed class Tools(Func<TextToolCall, ConversationToolResult> call) : IConversationToolHost
    {
        public ValueTask<ConversationToolResult> CallAsync(TextToolCall textCall, CancellationToken cancellationToken) =>
            ValueTask.FromResult(call(textCall));
    }

    private sealed class NoCredentials : IProviderCredentialSource
    {
        public ValueTask<BoundProviderCredential?> ResolveAsync(ProviderCredentialBinding binding, CancellationToken cancellationToken) =>
            ValueTask.FromResult<BoundProviderCredential?>(null);
    }

    // Allows exactly what was asked, bound to the fixture endpoint, as the desktop's own authorization does.
    private sealed class Permissions(Uri baseUri) : IConversationAuthorizationSource
    {
        public ValueTask<AuthorizedTextOperation?> AuthorizeTextAsync(TextAuthorizationAction action, CancellationToken cancellationToken) =>
            ValueTask.FromResult<AuthorizedTextOperation?>(new(new TextDisclosureAuthorization(
                new(baseUri, ProviderRole.Llm, action.Model.UpstreamModelId), action.Model, action.Context.Ids, action.Context.Epoch,
                action.Limits, action.Context.Deadline, true, true), new(action.Budget, action.Context.Deadline)));

        public ValueTask<AuthorizedSpeechOperation?> AuthorizeSpeechAsync(SpeechAuthorizationAction action, CancellationToken cancellationToken) =>
            ValueTask.FromResult<AuthorizedSpeechOperation?>(null);
    }

    private sealed record ServedRequest(string Kind, string Body, long StartMs, long EndMs, bool Aborted, bool Done);

    /// <summary>A Chat Completions endpoint on 127.0.0.1 (canned replies, NOT AI), one connection at a time each on its own task:
    /// the reply says it'll think it over and calls think_longer, then has nothing to add; the background think streams hidden
    /// reasoning for the given time, then the lyrics; Martlet's report says it finished. It notes each request and whether the
    /// client hung up on it.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly LoopbackHttpServer server;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<ServedRequest> served = [];
        private readonly Dictionary<int, (string Kind, string Body, long StartMs)> open = [];
        private readonly TimeSpan reasoning;
        private int next;

        internal Fixture(TimeSpan reasoning)
        {
            this.reasoning = reasoning;
            server = new LoopbackHttpServer(HandleAsync, concurrent: true);
        }

        internal string BaseUrl => server.V1;
        internal long Now => clock.ElapsedMilliseconds;

        internal IReadOnlyList<ServedRequest> Served(string kind) { lock (served) return [.. served.Where(s => s.Kind == kind)]; }
        internal IReadOnlyList<string> Bodies(string kind)
        {
            lock (served)
                return [.. served.Where(s => s.Kind == kind).Select(s => (s.StartMs, s.Body))
                    .Concat(open.Values.Where(o => o.Kind == kind).Select(o => (o.StartMs, o.Body))).OrderBy(s => s.StartMs).Select(s => s.Body)];
        }

        internal int Count(string kind, bool inFlight = false)
        {
            lock (served) return open.Values.Count(o => o.Kind == kind) + (inFlight ? 0 : served.Count(s => s.Kind == kind));
        }

        private async Task HandleAsync(LoopbackExchange exchange)
        {
            var number = Interlocked.Increment(ref next);
            string kind = "?", body = "";
            long start = Now;
            bool aborted = false, done = false;
            try
            {
                body = exchange.Request.Text;
                kind = Classify(body);
                start = Now;
                lock (served) open[number] = (kind, body, start);
                await exchange.StartAsync();
                switch (kind)
                {
                    case "reply":
                        await ChunkAsync(exchange, "{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(Acknowledged) + "}");
                        var arguments = JsonSerializer.Serialize(new { task = TaskText, reason = "Song lyrics need real writing." });
                        await ChunkAsync(exchange, "{\"tool_calls\":[{\"index\":0,\"id\":\"call_think\",\"type\":\"function\",\"function\":" +
                            "{\"name\":\"think_longer\",\"arguments\":" + JsonSerializer.Serialize(arguments) + "}}]}");
                        await FinishAsync(exchange, "tool_calls");
                        break;
                    case "after-tool":
                        await FinishAsync(exchange, "stop");
                        break;
                    case "think":
                        // Hidden reasoning a little at a time (it shows the model is working), then the lyrics.
                        var thought = Stopwatch.StartNew();
                        await ChunkAsync(exchange, "{\"role\":\"assistant\",\"content\":\"\",\"reasoning\":\"Thinking about rhymes.\"}");
                        while (thought.Elapsed < reasoning)
                        {
                            await Task.Delay(50, exchange.Cancellation);
                            await ChunkAsync(exchange, "{\"reasoning\":\" more.\"}");
                        }
                        foreach (var line in Lyrics.Split('\n'))
                            await ChunkAsync(exchange, "{\"content\":" + JsonSerializer.Serialize(line == Lyrics.Split('\n')[^1] ? line : line + "\n") + "}");
                        await FinishAsync(exchange, "stop");
                        break;
                    case "chat":
                        await ChunkAsync(exchange, "{\"role\":\"assistant\",\"content\":\"Sure thing, here you go.\"}");
                        await FinishAsync(exchange, "stop");
                        break;
                    default:
                        await ChunkAsync(exchange, "{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(Report) + "}");
                        await FinishAsync(exchange, "stop");
                        break;
                }
                done = true;
            }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                aborted = !exchange.Cancellation.IsCancellationRequested;
            }
            finally
            {
                lock (served)
                {
                    open.Remove(number);
                    if (kind != "?") served.Add(new(kind, body, start, Now, aborted, done));
                }
            }
        }

        // What the request is: the reply (it ends with what the user asked), its next round (after the tool result), the
        // background think (its last message is the task) or Martlet's report (its last message is Martlet's note).
        private static string Classify(string body)
        {
            var last = JsonNode.Parse(body)?["messages"]?.AsArray().LastOrDefault();
            var role = (string?)last?["role"];
            var content = last?["content"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
            return role == "tool" ? "after-tool"
                : content.Contains("A background task from Martlet", StringComparison.Ordinal) ? "think"
                : content.Contains("Martlet's note", StringComparison.Ordinal) ? "report"
                : JsonNode.Parse(body)?["tools"] is null ? "chat" : "reply";
        }

        private static Task ChunkAsync(LoopbackExchange exchange, string delta) => exchange.WriteAsync(FixtureChat.Delta(delta));

        private static Task FinishAsync(LoopbackExchange exchange, string reason) => exchange.WriteAsync(FixtureChat.Finish(reason));

        public ValueTask DisposeAsync() => server.DisposeAsync();
    }

    // A clock whose timers fail while Broken is set (as a disposed clock would at shutdown).
    private sealed class BreakingClock : TimeProvider
    {
        internal volatile bool Broken;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Broken ? throw new ObjectDisposedException("clock") : System.CreateTimer(callback, state, dueTime, period);
    }
}
