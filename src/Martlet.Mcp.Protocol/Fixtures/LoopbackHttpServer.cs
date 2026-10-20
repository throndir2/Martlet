using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Martlet.Mcp;

/// <summary>FIXTURE: an HTTP/1.1 server on 127.0.0.1 that the checks put in place of a model, a web site or a service (NOT the
/// real one). It reads one request on each connection (a Content-Length or chunked body), gives it to the handler and closes the
/// connection after the answer. Serial (the default) answers one connection after another, as a single-threaded server does;
/// concurrent answers each connection at once on its own task. A handler's failure only closes its connection.</summary>
internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();
    private readonly Func<LoopbackExchange, Task> handle;
    private readonly bool concurrent;
    private readonly List<Task> handling = [];
    private readonly Task accepting;

    /// <param name="handle">Answers one request (<see cref="LoopbackExchange"/>).</param>
    /// <param name="concurrent">Answer each connection at once instead of one after another.</param>
    /// <param name="port">The port on 127.0.0.1; 0 (the default) takes a free one.</param>
    internal LoopbackHttpServer(Func<LoopbackExchange, Task> handle, bool concurrent = false, int port = 0)
    {
        this.handle = handle;
        this.concurrent = concurrent;
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        accepting = AcceptAsync();
    }

    /// <summary>A server whose handler only chooses a status, a content type and a body.</summary>
    internal static LoopbackHttpServer Answering(Func<LoopbackRequest, (int Status, string ContentType, string Body)> answer,
        bool concurrent = false, int port = 0) =>
        new(exchange =>
        {
            var (status, type, body) = answer(exchange.Request);
            return exchange.RespondAsync(status, body, type);
        }, concurrent, port);

    internal int Port { get; }

    /// <summary>http://127.0.0.1:port, without a slash at the end.</summary>
    internal string Origin => $"http://127.0.0.1:{Port}";

    /// <summary>The Chat Completions base address: <see cref="Origin"/>/v1.</summary>
    internal string V1 => Origin + "/v1";

    /// <summary>Stops taking new connections; the requests already taken finish.</summary>
    internal void Stop()
    {
        stop.Cancel();
        listener.Stop();
    }

    private async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
            if (!concurrent)
            {
                await ServeAsync(client);
                continue;
            }
            var task = Task.Run(() => ServeAsync(client));
            lock (handling)
            {
                handling.RemoveAll(t => t.IsCompleted);
                handling.Add(task);
            }
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                var request = await LoopbackRequest.ReadAsync(stream, stop.Token);
                var exchange = new LoopbackExchange(request, stream, stop.Token);
                await handle(exchange);
                await exchange.EndAsync();
            }
            // The fixture only stands in for a server: any problem with one request closes that connection, and the client sees it.
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { Stop(); }
        catch (ObjectDisposedException) { }
        await accepting.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Task[] running;
        lock (handling) running = [.. handling];
        await Task.WhenAll(running).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        stop.Dispose();
    }

    /// <summary>A free port on 127.0.0.1 at this moment.</summary>
    internal static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }
}

/// <summary>FIXTURE: one HTTP request as a <see cref="LoopbackHttpServer"/> read it.</summary>
internal sealed class LoopbackRequest
{
    /// <summary>The most a request's head may hold.</summary>
    internal const int MaximumHeadBytes = 64 * 1024;

    /// <summary>The most a request's body may hold (pictures and recordings in a request stay far below it).</summary>
    internal const int MaximumBodyBytes = 256 * 1024 * 1024;

    private string? text;

    private LoopbackRequest(string method, string target, IReadOnlyDictionary<string, string> headers, byte[] body)
    {
        Method = method;
        Target = target;
        Headers = headers;
        Body = body;
    }

    internal string Method { get; }

    /// <summary>The request target as sent: the path with its query.</summary>
    internal string Target { get; }

    /// <summary>The path without the query.</summary>
    internal string Path => Target.Split('?', 2)[0];

    /// <summary>The query without the '?', or "".</summary>
    internal string Query => Target.Split('?', 2).ElementAtOrDefault(1) ?? "";

    /// <summary>The headers by name (any case); a repeated header keeps its last value.</summary>
    internal IReadOnlyDictionary<string, string> Headers { get; }

    internal byte[] Body { get; }

    /// <summary>The body as UTF-8 text.</summary>
    internal string Text => text ??= Encoding.UTF8.GetString(Body);

    internal string? Header(string name) => Headers.GetValueOrDefault(name);

    /// <summary>One query parameter, unescaped ('+' is a space), or null.</summary>
    internal string? QueryValue(string name)
    {
        foreach (var pair in Query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0].Replace('+', ' ')) == name)
                return parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
        }
        return null;
    }

    /// <summary>Reads one request: its head, then a Content-Length or chunked body. A body that ends early is kept as far as it
    /// came; a head or body length that is not valid or too large ends the connection (IOException).</summary>
    internal static async Task<LoopbackRequest> ReadAsync(Stream stream, CancellationToken cancellation)
    {
        var buffer = new MemoryStream();
        var one = new byte[16 * 1024];
        int end;
        while ((end = buffer.GetBuffer().AsSpan(0, (int)buffer.Length).IndexOf("\r\n\r\n"u8)) < 0)
        {
            if (buffer.Length > MaximumHeadBytes) throw new IOException("The request head is too large.");
            var read = await stream.ReadAsync(one, cancellation);
            if (read == 0) throw new IOException("The request ended early.");
            buffer.Write(one, 0, read);
        }
        var lines = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, end).Split("\r\n");
        var start = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var rest = new MemoryStream();
        rest.Write(buffer.GetBuffer(), end + 4, (int)buffer.Length - end - 4);
        async Task<bool> More()
        {
            var read = await stream.ReadAsync(one, cancellation);
            if (read == 0) return false;
            rest.Write(one, 0, read);
            return true;
        }

        byte[] body;
        if (headers.TryGetValue("Transfer-Encoding", out var encoding) && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            body = await ChunkedAsync(rest, More);
        else
        {
            var length = 0;
            if (headers.TryGetValue("Content-Length", out var given) &&
                (!int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length > MaximumBodyBytes))
                throw new IOException("The request's Content-Length is not valid.");
            while (rest.Length < length) if (!await More()) break;
            body = rest.ToArray()[..Math.Min((int)rest.Length, length)];
        }
        return new(start[0], start.Length > 1 ? start[1] : "/", headers, body);
    }

    // Chunked: size lines and data until the zero-size chunk.
    private static async Task<byte[]> ChunkedAsync(MemoryStream rest, Func<Task<bool>> more)
    {
        var body = new MemoryStream();
        var at = 0;
        while (true)
        {
            int line;
            while ((line = rest.GetBuffer().AsSpan(at, (int)rest.Length - at).IndexOf("\r\n"u8)) < 0)
                if (!await more()) return body.ToArray();
            var sizeText = Encoding.ASCII.GetString(rest.GetBuffer(), at, line).Split(';')[0].Trim();
            if (!int.TryParse(sizeText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size) || size < 0 ||
                body.Length + size > MaximumBodyBytes)
                throw new IOException("A chunk size of the request is not valid.");
            at += line + 2;
            if (size == 0) return body.ToArray();
            while (rest.Length - at < size + 2) if (!await more()) return body.ToArray();
            body.Write(rest.GetBuffer(), at, size);
            at += size + 2;
        }
    }
}

/// <summary>FIXTURE: one request and the way to answer it: a whole answer (RespondAsync), or a stream (StartAsync, then
/// WriteAsync or EventAsync) that ends when the connection closes, or with chunked encoding.</summary>
internal sealed class LoopbackExchange(LoopbackRequest request, NetworkStream stream, CancellationToken cancellation)
{
    private bool chunked;

    internal LoopbackRequest Request => request;

    /// <summary>Cancelled when the server stops.</summary>
    internal CancellationToken Cancellation => cancellation;

    /// <summary>Answers with a whole body (Content-Length) and closes the connection.</summary>
    internal Task RespondAsync(int status, string body, string contentType = "application/json", params (string Name, string Value)[] headers) =>
        RespondAsync(status, Encoding.UTF8.GetBytes(body), contentType, headers);

    internal async Task RespondAsync(int status, byte[] body, string contentType, params (string Name, string Value)[] headers)
    {
        await HeadAsync(status, contentType, headers, $"Content-Length: {body.Length}\r\n");
        await stream.WriteAsync(body, cancellation);
        await stream.FlushAsync(cancellation);
    }

    /// <summary>Sends the head of a streamed answer (no Content-Length; Cache-Control: no-store); its body is what WriteAsync and
    /// EventAsync send after it. With <paramref name="chunked"/> each write is one chunk, and the end chunk follows the handler.</summary>
    internal Task StartAsync(int status = 200, string contentType = "text/event-stream", bool chunked = false,
        params (string Name, string Value)[] headers)
    {
        this.chunked = chunked;
        return HeadAsync(status, contentType, headers, "Cache-Control: no-store\r\n" + (chunked ? "Transfer-Encoding: chunked\r\n" : ""));
    }

    /// <summary>Sends text (UTF-8) of a streamed answer at once.</summary>
    internal Task WriteAsync(string text) => WriteAsync(Encoding.UTF8.GetBytes(text));

    internal async Task WriteAsync(ReadOnlyMemory<byte> bytes)
    {
        if (chunked)
        {
            if (bytes.Length == 0) return;
            await stream.WriteAsync(Encoding.ASCII.GetBytes(bytes.Length.ToString("x", CultureInfo.InvariantCulture) + "\r\n"), cancellation);
            await stream.WriteAsync(bytes, cancellation);
            await stream.WriteAsync("\r\n"u8.ToArray(), cancellation);
        }
        else await stream.WriteAsync(bytes, cancellation);
        await stream.FlushAsync(cancellation);
    }

    /// <summary>Sends one server-sent event: "data: ..." and a blank line.</summary>
    internal Task EventAsync(string data) => WriteAsync("data: " + data + "\n\n");

    /// <summary>Completes when the client closes the connection (it stopped reading the answer).</summary>
    internal async Task ClosedAsync()
    {
        var buffer = new byte[256];
        try
        {
            while (await stream.ReadAsync(buffer, cancellation) > 0) { }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    /// <summary>Ends a chunked answer (the server does this after the handler).</summary>
    internal async Task EndAsync()
    {
        if (!chunked) return;
        chunked = false;
        await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), cancellation);
        await stream.FlushAsync(cancellation);
    }

    private async Task HeadAsync(int status, string contentType, (string Name, string Value)[] headers, string framing)
    {
        var head = new StringBuilder($"HTTP/1.1 {status} {Reason(status)}\r\n");
        if (contentType.Length > 0) head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        foreach (var (name, value) in headers) head.Append(name).Append(": ").Append(value).Append("\r\n");
        head.Append(framing).Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancellation);
        await stream.FlushAsync(cancellation);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        301 => "Moved Permanently",
        302 => "Found",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        _ => "Status"
    };
}
