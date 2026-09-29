using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Contracts;
using Martlet.Gateway.F5;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Martlet.Gateway.Tests;

// NOT AI: a controlled HTTP fixture stands in for the f5 host role's loopback service (martlet_f5_host.py), replaying the
// worker events it forwards. Set MARTLET_F5_LIVE_ENDPOINT to a real service provisioned with "martlet-f5 provision
// --fixture" to run the last test against the actual worker (deterministic fake engine).
public sealed class F5RelayTests
{
    private const string FixtureModel = "fixture-model-weights";
    private static readonly string FixtureRevision = new('2', 40);
    private static readonly string FixtureSha256 =
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes("model_weights-fixture-2")));

    private sealed class FakeF5 : IAsyncDisposable
    {
        private readonly WebApplication app;
        internal List<JsonDocument> Requests { get; } = [];
        internal Uri Endpoint { get; private set; } = null!;

        private FakeF5(WebApplication app) => this.app = app;

        internal static async Task<FakeF5> StartAsync(Func<JsonElement, IEnumerable<string>> respond)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var fake = new FakeF5(app);
            app.MapPost("/synthesize", async context =>
            {
                var request = await JsonDocument.ParseAsync(context.Request.Body);
                fake.Requests.Add(request);
                context.Response.ContentType = "application/x-ndjson";
                foreach (var line in respond(request.RootElement))
                {
                    await context.Response.WriteAsync(line + "\n");
                    await context.Response.Body.FlushAsync();
                }
            });
            app.MapPost("/cancel", () => Results.Ok());
            await app.StartAsync();
            fake.Endpoint = new Uri(app.Urls.First() + "/");
            return fake;
        }

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            foreach (var request in Requests) request.Dispose();
        }
    }

    // The worker event shape martlet_f5_worker emits (contract.make_event), as the host service forwards it.
    private static string WorkerEvent(JsonElement request, string kind, long sequence, object? frame = null, int? chunk = null,
        long? final = null, object? error = null) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["cancellation"] = null, ["chunk_index"] = chunk, ["contract_id"] = "martlet.f5.worker", ["error"] = error,
        ["final_sample_count"] = final, ["frame"] = frame,
        ["ids"] = JsonSerializer.Deserialize<Dictionary<string, object?>>(request.GetProperty("ids").GetRawText()),
        ["kind"] = kind, ["protocol_version"] = new { major = 1, minor = 0 },
        ["reference_revision"] = request.GetProperty("reference").GetProperty("reference_revision").GetString(),
        ["sequence"] = sequence, ["type"] = "event",
        ["worker"] = new
        {
            artifacts = new[] { new { artifact_id = FixtureModel, revision = FixtureRevision, role = "model_weights", sha256 = FixtureSha256 } }
        }
    });

    private static object Frame(long sequence, long offset, byte[] pcm) => new
    {
        chunk_index = 0, data_base64 = Convert.ToBase64String(pcm), sample_count = pcm.Length / 2, sample_offset = offset, sequence
    };

    private static byte[] Pcm(int samples, int seed) => Enumerable.Range(0, samples * 2).Select(i => (byte)(i * 7 + seed)).ToArray();

    // One second of a quiet 24 kHz mono PCM16 tone: a reference recording the gateway and worker accept.
    private static HostSpeechReference Reference()
    {
        const int rate = 24_000;
        var wav = new byte[44 + rate * 2];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)(wav.Length - 8));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), rate);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), rate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), rate * 2);
        for (var i = 0; i < rate; i++)
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + i * 2), (short)(Math.Sin(i * 0.05) * 3000));
        const string transcript = "A rights-cleared fixture reference.";
        var audioSha = SHA256.HashData(wav);
        var transcriptSha = SHA256.HashData(Encoding.UTF8.GetBytes(transcript));
        var revision = Convert.ToHexStringLower(SHA256.HashData([.. audioSha, .. transcriptSha]));
        return new(Guid.NewGuid(), revision, Convert.ToHexStringLower(audioSha), transcript,
            Convert.ToHexStringLower(transcriptSha), wav);
    }

    private static CorrelationIds NewIds() => new() { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };

    private static async Task<(Audio2FaceHostConnection Connection, HostRoute Route)> ConnectAsync(GatewayTestHost host)
    {
        var card = host.OpenPairing(GatewayRole.Voice, "desktop-test");
        var (pairing, secret) = await Audio2FaceHostClient.PairAsync(host.Origin.CanonicalOrigin, card.HostId,
            card.SpkiFingerprint, "desktop-test", card.PairingId, card.Token.Reveal());
        var connection = new Audio2FaceHostConnection(pairing, secret, host.Clock);
        var route = Assert.Single(await connection.ReadRoutesAsync(), r => r.RouteId == HostRoute.F5RouteId);
        return (connection, route);
    }

    [Fact]
    public async Task Paired_desktop_speaks_a_reply_with_the_hosts_f5_voice_through_the_gateway()
    {
        var first = Pcm(4_800, 1);
        var second = Pcm(1_200, 9);
        await using var f5 = await FakeF5.StartAsync(request =>
        [
            WorkerEvent(request, "started", 0),
            WorkerEvent(request, "audio_frame", 1, Frame(0, 0, first)),
            WorkerEvent(request, "audio_frame", 2, Frame(1, 4_800, second)),
            WorkerEvent(request, "chunk_completed", 3, chunk: 0, final: 6_000),
            WorkerEvent(request, "completed", 4, final: 6_000)
        ]);
        await using var worker = new F5RelayWorker(f5.Endpoint, FixtureModel, FixtureRevision, FixtureSha256);
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var (connection, route) = await ConnectAsync(host);
        using var owned = connection;
        Assert.Equal(FixtureModel, route.ModelId);
        Assert.Equal("discard_only", route.Cancellation);

        var reference = Reference();
        var pcm = new List<byte>();
        await foreach (var frame in connection.StreamSpeechAsync(route, NewIds(), 1, host.Clock.GetUtcNow().AddSeconds(30),
            reference, "Hello from the host."))
            pcm.AddRange(frame);

        Assert.Equal([.. first, .. second], pcm.ToArray());
        var sent = Assert.Single(f5.Requests).RootElement;
        Assert.Equal("Hello from the host.", sent.GetProperty("chunks")[0].GetProperty("text").GetString());
        Assert.Equal(reference.ReferenceRevision, sent.GetProperty("reference").GetProperty("reference_revision").GetString());
        Assert.Equal(reference.Audio.ToArray(),
            Convert.FromBase64String(sent.GetProperty("reference").GetProperty("audio_base64").GetString()!));
    }

    [Fact]
    public async Task Relay_rejects_a_worker_with_other_model_weights_and_maps_worker_failures()
    {
        await using var f5 = await FakeF5.StartAsync(request =>
        [
            WorkerEvent(request, "started", 0),
            WorkerEvent(request, "failed", 1, error: new { code = "model_not_ready", summary = "Not warmed up." })
        ]);
        await using var worker = new F5RelayWorker(f5.Endpoint, FixtureModel, FixtureRevision, FixtureSha256);
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var (connection, route) = await ConnectAsync(host);
        using var owned = connection;
        var failure = await Assert.ThrowsAsync<Audio2FaceHostException>(async () =>
        {
            await foreach (var _ in connection.StreamSpeechAsync(route, NewIds(), 0, host.Clock.GetUtcNow().AddSeconds(30),
                Reference(), "Hello")) { }
        });
        Assert.Equal("worker.unavailable", failure.Code);

        await using var other = new F5RelayWorker(f5.Endpoint, FixtureModel, FixtureRevision, new string('0', 64));
        await using var otherHost = await GatewayTestHost.StartAsync(inferenceWorkers: [other]);
        var (otherConnection, otherRoute) = await ConnectAsync(otherHost);
        using var otherOwned = otherConnection;
        var mismatch = await Assert.ThrowsAsync<Audio2FaceHostException>(async () =>
        {
            await foreach (var _ in otherConnection.StreamSpeechAsync(otherRoute, NewIds(), 0,
                otherHost.Clock.GetUtcNow().AddSeconds(30), Reference(), "Hello")) { }
        });
        Assert.Equal("worker.failed", mismatch.Code);
    }

    [Fact]
    public void Worker_only_relays_to_loopback_and_pins_the_role_model()
    {
        Assert.Throws<ArgumentException>(() => new F5RelayWorker(new Uri("http://192.168.1.5:50080/"), F5RelayWorker.DefaultModel));
        Assert.Throws<ArgumentException>(() => new F5RelayWorker(new Uri("http://127.0.0.1:50080/"), "unknown-model"));
        var worker = new F5RelayWorker(new Uri("http://127.0.0.1:50080/"), F5RelayWorker.DefaultModel);
        Assert.Equal("670900fd14e6c458b95da6e9ed317cdb20dbaf7a1c02ac06a05475a9d32b6a38", worker.Route.ModelSha256);
    }

    [Fact]
    public async Task Live_fixture_worker_speaks_through_the_relay_when_configured()
    {
        if (Environment.GetEnvironmentVariable("MARTLET_F5_LIVE_ENDPOINT") is not { Length: > 0 } endpoint) return;
        await using var worker = new F5RelayWorker(new Uri(endpoint), FixtureModel, FixtureRevision, FixtureSha256);
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var (connection, route) = await ConnectAsync(host);
        using var owned = connection;
        var samples = 0;
        await foreach (var frame in connection.StreamSpeechAsync(route, NewIds(), 1, host.Clock.GetUtcNow().AddSeconds(30),
            Reference(), "Fixture sentence."))
            samples += frame.Length / 2;
        Assert.Equal(997, samples);
    }
}
