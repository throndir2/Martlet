using System.Net;
using System.Text.Json;
using Martlet.Core.Logs;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

public sealed class LogsTests
{
    private sealed class MemoryStorage : IGatewayLogStorage
    {
        internal byte[]? Saved { get; private set; }
        public byte[]? Load() => Saved;
        public void Save(byte[] bytes) => Saved = bytes;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task Host_keeps_each_shared_line_once_and_serves_every_computer()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var storage = new MemoryStorage();
        host.Server.AttachLogStorage(storage);
        var credential = await host.PairAsync(GatewayRole.Voice, "desktop-a");
        var signer = new GatewayRequestSigner(host.Identity, credential, host.Clock);
        var now = host.Clock.GetUtcNow();
        LogRecord Line(string source, string component, int minutes, string level, string text) => new()
        {
            Source = source, Component = component, Seq = LogRules.Seq(now.AddMinutes(minutes), 0), At = now.AddMinutes(minutes),
            Level = level, Message = text
        };
        var batch = new LogBatch
        {
            SchemaVersion = 1,
            Streams = [new() { Source = "desktop-a", Component = LogComponents.Desktop }, new() { Source = "gpu-box", Component = LogComponents.Gateway }],
            Entries =
            [
                Line("desktop-a", LogComponents.Desktop, -5, LogLevels.Info, "desktop started"),
                Line("desktop-a", LogComponents.Desktop, -4, LogLevels.Error, "Unhandled UI exception\nSystem.InvalidOperationException: boom"),
                Line("gpu-box", LogComponents.Gateway, -3, LogLevels.Warn, "ollama-chat request from desktop-b failed")
            ]
        };

        // Delivered twice (two desktops relaying the same host, or a retry): kept once, and the marks say how far each stream got.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var post = host.SignedPost("/martlet/v1/logs", GatewayRole.Voice, signer, batch.Write());
            using var response = await host.Client.SendAsync(post);
            var root = await JsonAsync(response);
            Assert.Equal(attempt == 0 ? 3 : 0, root.GetProperty("accepted").GetInt32());
            var marks = root.GetProperty("marks").Deserialize<LogMark[]>(LogRules.Json)!;
            Assert.Equal(batch.Entries[1].Seq, marks.Single(m => m.Source == "desktop-a").Seq);
            Assert.Equal(batch.Entries[2].Seq, marks.Single(m => m.Source == "gpu-box").Seq);
        }

        // Every computer's lines, with the relayed host line marked as passed on by the desktop.
        using (var read = host.SignedGet("/martlet/v1/logs?after=0&limit=100", GatewayRole.Voice, signer))
        using (var response = await host.Client.SendAsync(read))
        {
            var root = await JsonAsync(response);
            var entries = root.GetProperty("entries").Deserialize<LogRecord[]>(LogRules.Json)!;
            Assert.Contains(entries, e => e.Source == "fixture-host" && e.Message.Contains("started on", StringComparison.Ordinal));
            Assert.Contains(entries, e => e.Source == "fixture-host" && e.Message.StartsWith("Paired device desktop-a", StringComparison.Ordinal));
            Assert.Null(entries.Single(e => e.Level == LogLevels.Error).RelayedBy);
            Assert.Equal("desktop-a", entries.Single(e => e.Source == "gpu-box").RelayedBy);
            Assert.False(root.GetProperty("more").GetBoolean());
        }

        // A refused request becomes a line in the host's own log.
        using (var bad = host.SignedGet("/martlet/v1/logs?after=0&after=1", GatewayRole.Voice, signer))
        using (var response = await host.Client.SendAsync(bad))
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        using (var own = host.SignedGet("/martlet/v1/logs?own_after=0", GatewayRole.Voice, signer))
        using (var response = await host.Client.SendAsync(own))
        {
            var entries = (await JsonAsync(response)).GetProperty("entries").Deserialize<LogRecord[]>(LogRules.Json)!;
            Assert.All(entries, e => Assert.Equal(("fixture-host", LogComponents.Gateway), (e.Source, e.Component)));
            Assert.Contains(entries, e => e.Level == LogLevels.Warn && e.Message.Contains("request.invalid", StringComparison.Ordinal));
        }

        using var anonymous = new HttpRequestMessage(HttpMethod.Get, host.Origin.CanonicalOrigin + "/martlet/v1/logs");
        using (var rejected = await host.Client.SendAsync(anonymous))
            Assert.NotEqual(HttpStatusCode.OK, rejected.StatusCode);

        // Stopping the listener saves the log; a restarted gateway keeps the lines and still refuses duplicates.
        await host.Listener.DisposeAsync();
        Assert.NotNull(storage.Saved);
        var restarted = new GatewayLogStore("fixture-host", host.Clock);
        restarted.Attach(storage);
        Assert.Contains(restarted.Read(0, 1000, 1_000_000).Entries, e => e.Source == "gpu-box");
        Assert.Equal(0, restarted.Accept(batch, "desktop-a").Accepted);
    }
}
