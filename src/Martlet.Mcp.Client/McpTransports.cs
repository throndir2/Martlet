using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Martlet.Mcp.Client;

/// <summary>An MCP failure with a user-facing message; <see cref="Diagnostics"/> is the server's recent error output.</summary>
public sealed class McpException(string message, string? diagnostics = null) : Exception(message)
{
    public string? Diagnostics { get; } = diagnostics;
}

internal interface IMcpTransport : IAsyncDisposable
{
    /// <summary>Starts the server or connection; each received JSON-RPC message goes to <paramref name="receive"/>.</summary>
    Task StartAsync(Func<JsonNode, ValueTask> receive, CancellationToken token);
    Task SendAsync(JsonObject message, CancellationToken token);
    /// <summary>Completes when the server can no longer be reached (its process exited or the session ended).</summary>
    Task Completion { get; }
    string? Diagnostics { get; }
    string StoppedReason { get; }
    void UseProtocolVersion(string version);
}

internal static class McpJson
{
    internal const int MaxMessageCharacters = 8 * 1024 * 1024;
    internal static readonly JsonDocumentOptions Reading = new() { MaxDepth = 128 };

    internal static JsonNode? TryParse(string text)
    {
        try { return JsonNode.Parse(text, documentOptions: Reading); }
        catch (JsonException) { return null; }
    }
}

/// <summary>A server program on this PC speaking newline-delimited JSON-RPC on stdin/stdout; stderr is diagnostics.</summary>
internal sealed class McpStdioTransport(McpServerDefinition server) : IMcpTransport
{
    private readonly SemaphoreSlim writing = new(1, 1);
    private readonly Queue<string> errors = new();
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? process;
    private int disposed;

    public Task Completion => completion.Task;
    public string? Diagnostics { get { lock (errors) return errors.Count == 0 ? null : string.Join('\n', errors); } }
    public string StoppedReason => process is { HasExited: true } exited
        ? $"The server program exited (code {exited.ExitCode})." : "The server program stopped answering.";
    public void UseProtocolVersion(string version) { }

    public Task StartAsync(Func<JsonNode, ValueTask> receive, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info = McpProcessStart.Create(server);
        try
        {
            process = Process.Start(info) ?? throw new McpException($"\"{server.Command}\" didn't start.");
        }
        catch (Win32Exception error)
        {
            throw new McpException($"Couldn't start \"{server.Command}\": {error.Message}");
        }
        McpChildProcesses.Adopt(process);
        var output = ReadAsync(process.StandardOutput, receive);
        _ = DrainAsync(process.StandardError);
        _ = output.ContinueWith(_ => completion.TrySetResult(), TaskScheduler.Default);
        return Task.CompletedTask;
    }

    public async Task SendAsync(JsonObject message, CancellationToken token)
    {
        var running = process ?? throw new McpException("The server isn't running.");
        var line = message.ToJsonString();
        await writing.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (completion.Task.IsCompleted) throw new McpException(StoppedReason, Diagnostics);
            await running.StandardInput.WriteAsync(line.AsMemory(), token).ConfigureAwait(false);
            await running.StandardInput.WriteAsync("\n".AsMemory(), token).ConfigureAwait(false);
            await running.StandardInput.FlushAsync(token).ConfigureAwait(false);
        }
        catch (IOException) { throw new McpException(StoppedReason, Diagnostics); }
        catch (ObjectDisposedException) { throw new McpException(StoppedReason, Diagnostics); }
        finally { writing.Release(); }
    }

    private async Task ReadAsync(StreamReader reader, Func<JsonNode, ValueTask> receive)
    {
        var buffer = new char[8192];
        var line = new StringBuilder();
        bool oversized = false;
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (count == 0) break;
                for (var i = 0; i < count; i++)
                {
                    var c = buffer[i];
                    if (c != '\n')
                    {
                        if (line.Length < McpJson.MaxMessageCharacters) line.Append(c);
                        else oversized = true;
                        continue;
                    }
                    var text = line.ToString().TrimEnd('\r');
                    line.Clear();
                    if (oversized)
                    {
                        oversized = false;
                        Note("(Martlet ignored a message larger than 8 MB.)");
                        continue;
                    }
                    if (text.Length == 0) continue;
                    if (McpJson.TryParse(text) is { } message) await receive(message).ConfigureAwait(false);
                    // Some servers print logs on stdout; keep them as diagnostics instead of failing.
                    else Note(text);
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private async Task DrainAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } text)
                if (text.Trim().Length > 0) Note(text);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private void Note(string text)
    {
        lock (errors)
        {
            errors.Enqueue(text.Length > 400 ? text[..400] + "..." : text);
            while (errors.Count > 12) errors.Dequeue();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0 || process is not { } running) return;
        try
        {
            // Closing stdin asks a well-behaved server to exit; anything left after 2 s is ended with its children.
            try { running.StandardInput.Close(); } catch (IOException) { }
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await running.WaitForExitAsync(wait.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            if (!running.HasExited)
            {
                try { running.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
            }
        }
        finally
        {
            completion.TrySetResult();
            running.Dispose();
            writing.Dispose();
        }
    }
}

/// <summary>A server reached over MCP's streamable HTTP transport: each message is a POST; the answer is JSON or an
/// event stream. The server-to-client GET stream is not opened (Martlet never needs server-initiated requests).</summary>
internal sealed class McpHttpTransport(McpServerDefinition server) : IMcpTransport
{
    private readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly CancellationTokenSource closing = new();
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<JsonNode, ValueTask>? receive;
    private string? session, version, stopped;
    private int disposed;

    public Task Completion => completion.Task;
    public string? Diagnostics => null;
    public string StoppedReason => stopped ?? "The server ended the connection.";
    public void UseProtocolVersion(string value) => version = value;

    public Task StartAsync(Func<JsonNode, ValueTask> receiver, CancellationToken token)
    {
        receive = receiver;
        return Task.CompletedTask;
    }

    public async Task SendAsync(JsonObject message, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, closing.Token);
        var request = new HttpRequestMessage(HttpMethod.Post, server.Url)
        {
            Content = new StringContent(message.ToJsonString(), new UTF8Encoding(false), "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        Decorate(request);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false); }
        catch (HttpRequestException error)
        {
            request.Dispose();
            throw new McpException($"Couldn't reach {server.Url!.GetLeftPart(UriPartial.Authority)}: {error.Message}");
        }
        catch (OperationCanceledException)
        {
            request.Dispose();
            throw;
        }
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids) && ids.FirstOrDefault() is { Length: > 0 } id)
            session ??= id;
        var streaming = false;
        try
        {
            if (response.StatusCode == HttpStatusCode.Accepted) return;
            if (response.StatusCode == HttpStatusCode.NotFound && session is not null)
            {
                stopped = "The server ended this session.";
                completion.TrySetResult();
                throw new McpException(stopped);
            }
            if (!response.IsSuccessStatusCode)
                throw new McpException($"The server answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}." +
                    (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? " Check the \"headers\" (for example an Authorization token) in mcp.json." : ""));
            var media = response.Content.Headers.ContentType?.MediaType;
            if (string.Equals(media, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                // The answer arrives on the stream; read it in the background so long tool calls don't block sends.
                streaming = true;
                _ = ReadEventsAsync(response, request, closing.Token);
                return;
            }
            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            if (body.Length > McpJson.MaxMessageCharacters) throw new McpException("The server's answer was larger than 8 MB.");
            if (body.Trim().Length == 0) return;
            switch (McpJson.TryParse(body))
            {
                case JsonArray batch:
                    foreach (var item in batch) if (item is not null) await receive!(item).ConfigureAwait(false);
                    break;
                case { } single:
                    await receive!(single).ConfigureAwait(false);
                    break;
                default:
                    throw new McpException("The server's answer wasn't JSON.");
            }
        }
        finally
        {
            if (!streaming)
            {
                response.Dispose();
                request.Dispose();
            }
        }
    }

    private void Decorate(HttpRequestMessage request)
    {
        foreach (var (name, value) in server.Headers) request.Headers.TryAddWithoutValidation(name, value);
        if (session is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        if (version is not null) request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", version);
    }

    private async Task ReadEventsAsync(HttpResponseMessage response, HttpRequestMessage request, CancellationToken token)
    {
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            var data = new StringBuilder();
            while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    if (data.Length > 0 && McpJson.TryParse(data.ToString()) is { } message) await receive!(message).ConfigureAwait(false);
                    data.Clear();
                    continue;
                }
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line.AsSpan(line.Length > 5 && line[5] == ' ' ? 6 : 5));
                    if (data.Length > McpJson.MaxMessageCharacters) data.Clear();
                }
            }
            if (data.Length > 0 && McpJson.TryParse(data.ToString()) is { } last) await receive!(last).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            response.Dispose();
            request.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (session is not null)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, server.Url);
                Decorate(request);
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var _ = await http.SendAsync(request, wait.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
        }
        await closing.CancelAsync().ConfigureAwait(false);
        completion.TrySetResult();
        http.Dispose();
        closing.Dispose();
    }
}
