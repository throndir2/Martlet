using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Martlet.Mcp.Client;

/// <summary>A tool an MCP server offers. <see cref="InputSchema"/> is its JSON Schema for the arguments.</summary>
public sealed record McpTool(string Name, string? Title, string? Description, JsonObject InputSchema)
{
    public override string ToString() => $"MCP tool {Name}";
}

/// <summary>What a tool returned, flattened to text for a language model.</summary>
public sealed record McpToolResult(string Text, bool IsError)
{
    public override string ToString() => $"MCP tool result (error: {IsError})";
}

/// <summary>One initialized MCP session (protocol 2025-06-18, accepting older 2025-03-26 and 2024-11-05 servers). Martlet
/// declares no client capabilities: it never offers sampling, roots or elicitation.</summary>
public sealed class McpClient : IAsyncDisposable
{
    public const string LatestProtocolVersion = "2025-06-18";
    public const int MaxTools = 256;
    private readonly IMcpTransport transport;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> pending = new();
    private long nextId;
    private int disposed;

    public string? ServerName { get; private set; }
    public string? ServerVersion { get; private set; }
    public string? Instructions { get; private set; }
    public string ProtocolVersion { get; private set; } = LatestProtocolVersion;
    public Task Completion => transport.Completion;
    public string? Diagnostics => transport.Diagnostics;
    public string StoppedReason => transport.StoppedReason;
    /// <summary>Raised (on a background thread) when the server says its tool list changed.</summary>
    public event Action? ToolsChanged;

    private McpClient(IMcpTransport transport) => this.transport = transport;

    public static async Task<McpClient> ConnectAsync(McpServerDefinition server, string clientName, string clientVersion,
        TimeSpan timeout, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server.Problem is { } problem) throw new McpException(problem);
        IMcpTransport transport = server.Transport == McpTransportKind.Http
            ? new McpHttpTransport(server)
            : new McpStdioTransport(server);
        var client = new McpClient(transport);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(timeout);
            await transport.StartAsync(client.ReceiveAsync, limit.Token).ConfigureAwait(false);
            _ = client.WatchAsync();
            JsonNode? result;
            try
            {
                result = await client.RequestAsync("initialize", new JsonObject
                {
                    ["protocolVersion"] = LatestProtocolVersion,
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = clientName, ["version"] = clientVersion }
                }, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new McpException($"The server didn't finish starting within {timeout.TotalSeconds:0} seconds.", client.Diagnostics);
            }
            if (result is JsonObject answer)
            {
                if (Text(answer["protocolVersion"]) is { Length: > 0 } version) client.ProtocolVersion = version;
                if (answer["serverInfo"] is JsonObject info)
                {
                    client.ServerName = Text(info["name"]);
                    client.ServerVersion = Text(info["version"]);
                }
                client.Instructions = Text(answer["instructions"]);
            }
            transport.UseProtocolVersion(client.ProtocolVersion);
            await client.NotifyAsync("notifications/initialized", null, limit.Token).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken token)
    {
        var tools = new List<McpTool>();
        string? cursor = null;
        for (var page = 0; page < 32; page++)
        {
            var parameters = cursor is null ? null : new JsonObject { ["cursor"] = cursor };
            var result = await RequestAsync("tools/list", parameters, token).ConfigureAwait(false) as JsonObject;
            if (result?["tools"] is JsonArray items)
                foreach (var item in items.OfType<JsonObject>())
                {
                    if (Text(item["name"]) is not { Length: > 0 } name || tools.Count >= MaxTools) continue;
                    var schema = item["inputSchema"] is JsonObject declared
                        ? (JsonObject)declared.DeepClone()
                        : new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
                    tools.Add(new(name, Text(item["title"]) ?? Text((item["annotations"] as JsonObject)?["title"]),
                        Text(item["description"]), schema));
                }
            cursor = Text(result?["nextCursor"]);
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return tools;
    }

    public async Task<McpToolResult> CallToolAsync(string name, JsonObject? arguments, CancellationToken token)
    {
        var result = await RequestAsync("tools/call", new JsonObject
        {
            ["name"] = name,
            ["arguments"] = arguments ?? new JsonObject()
        }, token).ConfigureAwait(false) as JsonObject;
        return new(Flatten(result), result?["isError"] is JsonValue flag && flag.TryGetValue<bool>(out var error) && error);
    }

    /// <summary>Text for a language model: text content as is, other content described in brackets.</summary>
    internal static string Flatten(JsonObject? result)
    {
        var text = new StringBuilder();
        if (result?["content"] is JsonArray content)
            foreach (var item in content.OfType<JsonObject>())
            {
                if (text.Length > 0) text.Append('\n');
                switch (Text(item["type"]))
                {
                    case "text":
                        text.Append(Text(item["text"]));
                        break;
                    case "image":
                        text.Append($"[image ({Text(item["mimeType"]) ?? "unknown type"}) not shown]");
                        break;
                    case "audio":
                        text.Append($"[audio ({Text(item["mimeType"]) ?? "unknown type"}) not played]");
                        break;
                    case "resource" when item["resource"] is JsonObject resource:
                        text.Append(Text(resource["text"]) ?? $"[binary resource {Text(resource["uri"])}]");
                        break;
                    case "resource_link":
                        text.Append($"[resource {Text(item["name"]) ?? ""} {Text(item["uri"])}]".Replace("  ", " "));
                        break;
                    case { } other:
                        text.Append($"[{other} content]");
                        break;
                }
            }
        if (text.Length == 0 && result?["structuredContent"] is JsonNode structured)
            text.Append(structured.ToJsonString());
        return text.Length == 0 ? "(no output)" : text.ToString();
    }

    private async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        var id = Interlocked.Increment(ref nextId);
        var answer = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = answer;
        try
        {
            if (transport.Completion.IsCompleted) throw new McpException(transport.StoppedReason, transport.Diagnostics);
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters is not null) message["params"] = parameters;
            await transport.SendAsync(message, token).ConfigureAwait(false);
            using var registration = token.Register(() => answer.TrySetCanceled(token));
            return await answer.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested && method != "initialize")
        {
            // Tell the server to stop work nobody is waiting for any more.
            _ = NotifyQuietlyAsync("notifications/cancelled", new JsonObject { ["requestId"] = id, ["reason"] = "Canceled by Martlet." });
            throw;
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    private Task NotifyAsync(string method, JsonObject? parameters, CancellationToken token)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null) message["params"] = parameters;
        return transport.SendAsync(message, token);
    }

    private async Task NotifyQuietlyAsync(string method, JsonObject parameters)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await NotifyAsync(method, parameters, limit.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is McpException or OperationCanceledException or ObjectDisposedException) { }
    }

    private ValueTask ReceiveAsync(JsonNode message)
    {
        if (message is not JsonObject value) return ValueTask.CompletedTask;
        if (Text(value["method"]) is { } method)
        {
            if (value["id"] is { } requestId)
            {
                // Martlet declares no client capabilities; only ping is answered.
                var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = requestId.DeepClone() };
                if (method == "ping") response["result"] = new JsonObject();
                else response["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"Martlet doesn't support {method}." };
                _ = ReplyQuietlyAsync(response);
            }
            else if (method == "notifications/tools/list_changed") ToolsChanged?.Invoke();
            return ValueTask.CompletedTask;
        }
        if (Id(value["id"]) is not { } id || !pending.TryGetValue(id, out var answer)) return ValueTask.CompletedTask;
        if (value["error"] is JsonObject error)
        {
            var text = Text(error["message"]) ?? "The server reported an error.";
            answer.TrySetException(new McpException(text.Length > 600 ? text[..600] + "..." : text));
        }
        else answer.TrySetResult(value["result"]?.DeepClone());
        return ValueTask.CompletedTask;
    }

    private async Task ReplyQuietlyAsync(JsonObject response)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await transport.SendAsync(response, limit.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is McpException or OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task WatchAsync()
    {
        await transport.Completion.ConfigureAwait(false);
        foreach (var answer in pending.Values)
            answer.TrySetException(new McpException(transport.StoppedReason, transport.Diagnostics));
    }

    private static long? Id(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<long>(out var number) => number,
        JsonValue value when value.TryGetValue<string>(out var text) && long.TryParse(text, out var parsed) => parsed,
        _ => null
    };

    internal static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await transport.DisposeAsync().ConfigureAwait(false);
        foreach (var answer in pending.Values) answer.TrySetCanceled();
    }
}
