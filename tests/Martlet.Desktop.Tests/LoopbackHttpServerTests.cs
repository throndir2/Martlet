using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Martlet.Mcp;

namespace Martlet.Desktop.Tests;

/// <summary>The checks' shared loopback HTTP server (FIXTURE): it reads Content-Length and chunked bodies, refuses a bad length
/// without stopping, and streams server-sent events and chunked answers.</summary>
public sealed class LoopbackHttpServerTests
{
    [Fact]
    public async Task ReadsContentLengthAndChunkedBodiesAndStreamsAnswers()
    {
        var seen = new List<(string Method, string Path, string? Query, string Body)>();
        await using var server = new LoopbackHttpServer(async exchange =>
        {
            var request = exchange.Request;
            lock (seen) seen.Add((request.Method, request.Path, request.QueryValue("q"), request.Text));
            switch (request.Path)
            {
                case "/sse":
                    await exchange.StartAsync();
                    await exchange.EventAsync("one");
                    await exchange.WriteAsync(FixtureChat.Reply("two"));
                    break;
                case "/chunked":
                    await exchange.StartAsync(contentType: "text/plain", chunked: true);
                    await exchange.WriteAsync("first ");
                    await exchange.WriteAsync("second");
                    break;
                default:
                    await exchange.RespondAsync(201, "{\"echo\":" + System.Text.Json.JsonSerializer.Serialize(request.Text) + "}", "application/json",
                        ("X-Fixture", "yes"));
                    break;
            }
        });
        using var client = new HttpClient();

        using var posted = await client.PostAsync(server.Origin + "/echo?q=a+b%21", new StringContent("hello", Encoding.UTF8, "text/plain"));
        Assert.Equal(201, (int)posted.StatusCode);
        Assert.Equal("yes", posted.Headers.GetValues("X-Fixture").Single());
        Assert.Equal("{\"echo\":\"hello\"}", await posted.Content.ReadAsStringAsync());

        // HttpClient sends a stream of unknown length chunked.
        using var streamed = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 40_000))));
        using var chunkedPost = await client.PostAsync(server.V1 + "/chunked-request", streamed);
        Assert.Contains(new string('x', 40_000), await chunkedPost.Content.ReadAsStringAsync());

        var sse = await client.GetStringAsync(server.Origin + "/sse");
        Assert.StartsWith("data: one\n\n", sse);
        Assert.EndsWith("data: [DONE]\n\n", sse);
        Assert.Contains("\"content\":\"two\"", sse);

        Assert.Equal("first second", await client.GetStringAsync(server.Origin + "/chunked"));

        lock (seen)
        {
            Assert.Equal(("POST", "/echo", "a b!", "hello"), seen[0]);
            Assert.Equal(40_000, seen[1].Body.Length);
        }
    }

    [Theory]
    [InlineData("Content-Length: nope")]
    [InlineData("Content-Length: -5")]
    [InlineData("Content-Length: 99999999999")]
    [InlineData("Transfer-Encoding: chunked\r\n\r\nzz")]
    public async Task ABadRequestLengthClosesOnlyThatConnection(string header)
    {
        var handled = 0;
        await using var server = new LoopbackHttpServer(exchange =>
        {
            Interlocked.Increment(ref handled);
            return exchange.RespondAsync(200, "{}");
        });
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync("127.0.0.1", server.Port);
            var stream = tcp.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST / HTTP/1.1\r\nHost: x\r\n{header}\r\n\r\n"));
            var buffer = new byte[64];
            // The server closes the connection without an answer.
            Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        }
        Assert.Equal(0, Volatile.Read(ref handled));

        using var client = new HttpClient();
        Assert.Equal("{}", await client.GetStringAsync(server.Origin + "/"));
        Assert.Equal(1, Volatile.Read(ref handled));
    }

    [Fact]
    public async Task AHandlerThatFailsClosesItsConnectionAndTheServerKeepsAnswering()
    {
        var calls = 0;
        await using var server = new LoopbackHttpServer(exchange =>
            Interlocked.Increment(ref calls) == 1 ? throw new InvalidOperationException("boom") : exchange.RespondAsync(200, "ok", "text/plain"));
        using var client = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(server.Origin + "/"));
        Assert.Equal("ok", await client.GetStringAsync(server.Origin + "/"));
    }
}
