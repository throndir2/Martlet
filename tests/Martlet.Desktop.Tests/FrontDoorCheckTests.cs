using System.Net;
using System.Net.Sockets;
using System.Text;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

// NOT AI: a raw loopback HTTP server stands in for what a public address answers. The tests check what Check address
// (Share › Share your language models) tells the owner for each answer.
public sealed class FrontDoorCheckTests
{
    /// <summary>Answers one request with <paramref name="status"/> and <paramref name="body"/>; the address to check.</summary>
    private static (string Address, Task Served) Serve(int status, string body, string? location = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var served = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                var buffer = new byte[4096];
                var read = 0;
                while (!Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n", StringComparison.Ordinal))
                    read += await stream.ReadAsync(buffer.AsMemory(read));
                var bytes = Encoding.UTF8.GetBytes(body);
                var head = $"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n" +
                    (location is null ? "" : $"Location: {location}\r\n") + "\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(bytes);
            }
            finally { listener.Stop(); }
        });
        return ($"http://127.0.0.1:{port}/v1", served);
    }

    [Theory]
    [InlineData(401, """{"error":{"message":"m","type":"authentication_error","code":"missing_api_key"}}""", "Works: ")]
    [InlineData(401, """{"error":{"message":"m","type":"authentication_error","code":"auth.missing"}}""", "reaches a Martlet gateway, but not its front door")]
    [InlineData(401, """{"code":"auth.missing","trace_id":"x"}""", "reaches a Martlet gateway, but not its front door")]
    [InlineData(502, "<html>Bad Gateway</html>", "can't reach Martlet")]
    [InlineData(200, "<html>Welcome to nginx!</html>", "but not as Martlet")]
    [InlineData(404, "Not found.", "but not as Martlet")]
    public async Task Check_address_says_what_answered(int status, string body, string expected)
    {
        var (address, served) = Serve(status, body);
        var text = await FrontDoorCheck.RunAsync(address, "https://192.168.1.20:9445", CancellationToken.None);
        await served;
        Assert.Contains(expected, text, StringComparison.Ordinal);
        if (!expected.StartsWith("Works", StringComparison.Ordinal)) Assert.Contains("https://192.168.1.20:9445", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_address_names_a_redirect_and_a_dead_address()
    {
        var (address, served) = Serve(301, "", "https://martlet.example.com/v1/models");
        Assert.Contains("redirects (301) to https://martlet.example.com/v1/models", await FrontDoorCheck.RunAsync(address, null, CancellationToken.None),
            StringComparison.Ordinal);
        await served;

        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var closed = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Assert.StartsWith("Nothing answered at", await FrontDoorCheck.RunAsync($"http://127.0.0.1:{closed}/v1", null, CancellationToken.None),
            StringComparison.Ordinal);
    }
}
