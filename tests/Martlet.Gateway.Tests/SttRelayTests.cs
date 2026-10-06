using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Contracts;
using Martlet.Gateway.Stt;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Martlet.Gateway.Tests;

// NOT AI: a controlled HTTP fixture stands in for the host's loopback whisper.cpp /inference.
public sealed class SttRelayTests
{
    private sealed class FakeWhisper : IAsyncDisposable
    {
        private readonly WebApplication app;
        internal List<(byte[] Wave, string Format)> Requests { get; } = [];
        internal Uri Endpoint { get; private set; } = null!;

        private FakeWhisper(WebApplication app) => this.app = app;

        internal static async Task<FakeWhisper> StartAsync(int status, string body)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var fake = new FakeWhisper(app);
            app.MapPost("/inference", async context =>
            {
                var form = await context.Request.ReadFormAsync();
                using var file = new MemoryStream();
                await form.Files["file"]!.CopyToAsync(file);
                fake.Requests.Add((file.ToArray(), form["response_format"].ToString()));
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(body);
            });
            await app.StartAsync();
            fake.Endpoint = new Uri(app.Urls.First() + "/");
            return fake;
        }

        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }

    private static CorrelationIds NewIds() => new() { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };

    private static readonly byte[] Utterance = Enumerable.Range(0, 16_000).SelectMany(i => BitConverter.GetBytes((short)(i % 200 - 100))).ToArray();

    private static async Task<(Audio2FaceHostConnection Connection, HostRoute Route)> ConnectAsync(GatewayTestHost host)
    {
        var card = host.OpenPairing(GatewayRole.Voice, "desktop-test");
        var (pairing, secret) = await Audio2FaceHostClient.PairAsync(host.Origin.CanonicalOrigin, card.HostId,
            card.SpkiFingerprint, "desktop-test", card.PairingId, card.Token.Reveal());
        var connection = new Audio2FaceHostConnection(pairing, secret, host.Clock);
        var route = Assert.Single(await connection.ReadRoutesAsync(), r => r.RouteId == Audio2FaceHostConnection.TranscriptionRouteId);
        return (connection, route);
    }

    [Fact]
    public async Task Paired_desktop_transcribes_an_utterance_with_the_hosts_whisper_through_the_gateway()
    {
        await using var whisper = await FakeWhisper.StartAsync(200, "{\"text\":\" Hello there. [BLANK_AUDIO]\\n\"}");
        await using var worker = new SttRelayWorker(whisper.Endpoint, "small");
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var (connection, route) = await ConnectAsync(host);
        using var owned = connection;
        Assert.Equal("small", route.ModelId);

        var text = await connection.TranscribeAsync(route, NewIds(), 1, host.Clock.GetUtcNow().AddSeconds(30), Utterance);

        Assert.Equal("Hello there.", text);
        var (wave, format) = Assert.Single(whisper.Requests);
        Assert.Equal("json", format);
        Assert.Equal(44 + Utterance.Length, wave.Length);
        Assert.Equal(16_000, BitConverter.ToInt32(wave, 24));
        Assert.Equal(Utterance, wave[44..]);
    }

    [Fact]
    public async Task Silence_is_an_empty_transcript_and_a_stopped_server_is_unavailable()
    {
        await using (var whisper = await FakeWhisper.StartAsync(200, "{\"text\":\" [BLANK_AUDIO]\\n\"}"))
        await using (var worker = new SttRelayWorker(whisper.Endpoint, "base"))
        await using (var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]))
        {
            var (connection, route) = await ConnectAsync(host);
            using var owned = connection;
            Assert.Equal("", await connection.TranscribeAsync(route, NewIds(), 0, host.Clock.GetUtcNow().AddSeconds(30), Utterance));
        }

        await using var stopped = new SttRelayWorker(new Uri("http://127.0.0.1:1/"), "base");
        await using var gateway = await GatewayTestHost.StartAsync(inferenceWorkers: [stopped]);
        var (client, stt) = await ConnectAsync(gateway);
        using var ownedClient = client;
        var failure = await Assert.ThrowsAsync<Audio2FaceHostException>(() =>
            client.TranscribeAsync(stt, NewIds(), 0, gateway.Clock.GetUtcNow().AddSeconds(30), Utterance));
        Assert.Equal("worker.unavailable", failure.Code);
    }

    [Fact]
    public async Task A_hosts_parakeet_answers_the_same_route_with_its_own_engine_release()
    {
        await using var parakeet = await FakeWhisper.StartAsync(200, "{\"text\":\"Can you remind me to call my sister?\"}");
        await using var worker = new SttRelayWorker(parakeet.Endpoint, "parakeet-tdt-0.6b-v3-int8");
        Assert.Equal(SttRelayWorker.ParakeetModelRevision, worker.Route.ModelRevision);
        Assert.Equal(SttRelayWorker.ModelRevision, SttRelayWorker.RevisionFor("large-v3-turbo"));
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var (connection, route) = await ConnectAsync(host);
        using var owned = connection;
        Assert.Equal("parakeet-tdt-0.6b-v3-int8", route.ModelId);

        Assert.Equal("Can you remind me to call my sister?",
            await connection.TranscribeAsync(route, NewIds(), 1, host.Clock.GetUtcNow().AddSeconds(30), Utterance));
        Assert.Equal(44 + Utterance.Length, Assert.Single(parakeet.Requests).Wave.Length);
    }

    [Fact]
    public void Worker_only_relays_to_loopback_and_cleans_non_speech_tags()
    {
        Assert.Throws<ArgumentException>(() => new SttRelayWorker(new Uri("http://192.168.1.5:8178/"), "small"));
        Assert.Throws<ArgumentException>(() => new SttRelayWorker(new Uri("http://127.0.0.1:8178/"), "../small"));
        Assert.Equal("", SttRelayWorker.Clean(" (music) "));
        Assert.Equal("Ask not what your country can do.", SttRelayWorker.Clean(" Ask not what\n your country can do. [ Silence ]"));
    }
}
