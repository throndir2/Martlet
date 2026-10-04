using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Singing;
using Martlet.Core.Voices;
using Martlet.F5;
using Martlet.Gateway.Singing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Martlet.Gateway.Tests;

// NOT AI: a controlled HTTP fixture stands in for the singing role's loopback service (workers/singing/martlet_singing/host.py),
// serving a FIXTURE - NOT AI song made by FixtureSongMaker.Compose.
public sealed class SongRelayTests
{
    private sealed class FakeSinging : IAsyncDisposable
    {
        private readonly WebApplication app;
        private SongResult? song;
        private int polls;
        internal List<JsonElement> Starts { get; } = [];
        internal List<string> Canceled { get; } = [];
        internal bool Busy { get; set; }
        internal bool NeverFinishes { get; set; }
        internal Uri Endpoint { get; private set; } = null!;

        private FakeSinging(WebApplication app) => this.app = app;

        internal static async Task<FakeSinging> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var fake = new FakeSinging(app);
            app.MapGet("/status", () => Results.Json(new Dictionary<string, object?>
            {
                ["state"] = "ready", ["ready"] = true, ["engine"] = "fixture", ["voice_matches"] = new[] { "soulx" }, ["queue"] = 0
            }));
            app.MapPost("/jobs", async context =>
            {
                using var document = await JsonDocument.ParseAsync(context.Request.Body);
                var body = document.RootElement.Clone();
                fake.Starts.Add(body);
                if (fake.Busy)
                {
                    await context.Response.WriteAsJsonAsync(new { error = new { code = "singing.busy", summary = "Too many songs are waiting." } });
                    return;
                }
                fake.song = FixtureSongMaker.Compose(new SongRequest
                {
                    Lyrics = body.GetProperty("lyrics").GetString()!, Style = body.GetProperty("style").GetString()!,
                    VoiceId = body.GetProperty("voice_id").GetString()!, DurationSeconds = body.GetProperty("duration_seconds").GetInt32(),
                    Seed = body.GetProperty("seed").GetInt64()
                });
                await context.Response.WriteAsJsonAsync(new { job_id = "song-1", state = "queued", stage = "queued", fraction = 0, queue_position = 0 });
            });
            app.MapGet("/jobs/{id}", (string id) =>
            {
                if (fake.song is not { } song || id != "song-1")
                    return Results.Json(new { job_id = id, state = "unknown", error = new { code = "job.unknown", summary = "No such song." } });
                if (fake.NeverFinishes || fake.polls++ == 0)
                    return Results.Json(new { job_id = id, state = "running", stage = "writing_music", fraction = 0.3 });
                return Results.Json(new
                {
                    job_id = id, state = "completed", stage = "completed", fraction = 1.0,
                    result = new
                    {
                        engine = new { generator = song.Engine.Generator, separator = "FIXTURE - NOT AI", converter = "FIXTURE - NOT AI",
                            quality = "fast", voice_match = "soulx", fixture = true },
                        frames = song.Mix.Frames, seed = song.Seed, bpm = song.Bpm, key = (string?)null, beats_per_bar = song.BeatsPerBar,
                        beats = song.Beats.Select(b => b.TotalSeconds), downbeats = song.Downbeats.Select(b => b.TotalSeconds),
                        lyrics = song.LyricTimestamps.Select(l => new { start = l.Start.TotalSeconds, end = l.End?.TotalSeconds, text = l.Text, section = l.Section }),
                        words = song.Words.Select(w => new { start = w.Start.TotalSeconds, end = w.End.TotalSeconds, text = w.Text, line = w.LineIndex }),
                        word_timing_source = song.WordTimingSource,
                        timings = new[] { new { stage = "writing_music", seconds = 1.5 }, new { stage = "aligning", seconds = 0.2 }, new { stage = "total", seconds = 2.0 } }
                    }
                });
            });
            app.MapGet("/jobs/{id}/tracks/{track}", async (HttpContext context, string id, string track) =>
            {
                var song = fake.song!;
                var source = track switch { "mix" => song.Mix, "vocals" => song.Vocals, _ => song.Backing };
                var frameBytes = 2 * source.Channels;
                var offset = long.Parse(context.Request.Query["offset"]!, CultureInfo.InvariantCulture);
                var frames = Math.Min(long.Parse(context.Request.Query["frames"]!, CultureInfo.InvariantCulture), source.Frames - offset);
                context.Response.ContentType = "application/octet-stream";
                context.Response.Headers["X-Song-Sample-Rate"] = "48000";
                context.Response.Headers["X-Song-Channels"] = source.Channels.ToString(CultureInfo.InvariantCulture);
                context.Response.Headers["X-Song-Total-Frames"] = source.Frames.ToString(CultureInfo.InvariantCulture);
                await context.Response.Body.WriteAsync(source.Pcm16.Slice((int)(offset * frameBytes), (int)(frames * frameBytes)));
            });
            app.MapPost("/jobs/{id}/cancel", (string id) =>
            {
                fake.Canceled.Add(id);
                return Results.Json(new { job_id = id, state = "canceled" });
            });
            await app.StartAsync();
            fake.Endpoint = new Uri(app.Urls.First() + "/");
            return fake;
        }

        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }

    private static readonly F5BundledVoice Voice = F5BundledVoices.Default;
    private static readonly string VoiceId = SpeakingVoiceLibrary.ReferenceId(Voice.AudioSha256, Voice.Transcript);

    private static SongRequest Request(string? voiceId = null) => new()
    {
        Lyrics = "[verse]\nMorning light is on the window\nCoffee steaming by the door\n[chorus]\nSing it with me",
        Style = "acoustic pop", VoiceId = voiceId ?? VoiceId, DurationSeconds = 30, Seed = 7
    };

    private static async Task<(Audio2FaceHostConnection Connection, HostRoute Route)> ConnectAsync(GatewayTestHost host)
    {
        var card = host.OpenPairing(GatewayRole.Voice, "desktop-test");
        var (pairing, secret) = await Audio2FaceHostClient.PairAsync(host.Origin.CanonicalOrigin, card.HostId,
            card.SpkiFingerprint, "desktop-test", card.PairingId, card.Token.Reveal());
        var connection = new Audio2FaceHostConnection(pairing, secret, host.Clock);
        var route = Assert.Single(await connection.ReadRoutesAsync(), r => r.RouteId == Audio2FaceHostConnection.SongRouteId);
        await connection.MergeSpeakingVoicesAsync(SpeakingVoiceLibrary.Empty.Seed(F5SharedVoices.Starters));
        return (connection, route);
    }

    private sealed class Cancel(CancellationTokenSource stop) : IProgress<SongProgress>
    {
        public void Report(SongProgress value)
        {
            if (value.Stage == SongStage.WritingMusic) stop.Cancel();
        }
    }

    private sealed class Stages : IProgress<SongProgress>
    {
        internal List<SongStage> Seen { get; } = [];
        public void Report(SongProgress value) => Seen.Add(value.Stage);
    }

    [Fact]
    public async Task Paired_desktop_makes_a_song_in_a_shared_voice_through_the_gateway()
    {
        await using var singing = await FakeSinging.StartAsync();
        await using var worker = new SongRelayWorker(singing.Endpoint);
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var (connection, route) = await ConnectAsync(host);
        using var owned = connection;
        Assert.Equal(SongRelayWorker.DefaultModel, route.ModelId);
        var sent = 0;
        var stages = new Stages();

        // The host lacks the voice's recording until the desktop sends it once (reference.missing, then with the recording).
        var song = await connection.MakeSongAsync(route, Request(), _ => { sent++; return Task.FromResult<byte[]?>(Voice.ReadAudio()); },
            stages, CancellationToken.None, TimeSpan.FromMilliseconds(20));

        Assert.Equal(1, sent);
        var start = Assert.Single(singing.Starts);
        Assert.Equal(VoiceId, start.GetProperty("voice_id").GetString());
        Assert.Equal(Voice.AudioSha256, start.GetProperty("reference").GetProperty("audio_sha256").GetString());
        Assert.Equal(Voice.AudioSha256, Convert.ToHexStringLower(SHA256.HashData(
            Convert.FromBase64String(start.GetProperty("reference").GetProperty("audio_base64").GetString()!))));
        var expected = FixtureSongMaker.Compose(Request());
        Assert.Equal(expected.Mix.Pcm16.ToArray(), song.Mix.Pcm16.ToArray());
        Assert.Equal(expected.Vocals.Pcm16.ToArray(), song.Vocals.Pcm16.ToArray());
        Assert.Equal(expected.Backing.Pcm16.ToArray(), song.Backing.Pcm16.ToArray());
        Assert.Equal(30, song.Duration.TotalSeconds, 3);
        Assert.True(song.Engine.Fixture);
        Assert.Equal(expected.Beats, song.Beats);
        Assert.Equal(expected.Downbeats, song.Downbeats);
        Assert.Equal(["verse", "verse", "chorus"], song.LyricTimestamps.Select(l => l.Section));
        Assert.Equal(expected.Words, song.Words);
        Assert.Equal("fixture", song.WordTimingSource);
        Assert.Contains(song.StageTimings, t => t.Stage == SongStage.WritingMusic && t.Duration == TimeSpan.FromSeconds(1.5));
        Assert.Contains(song.StageTimings, t => t.Stage == SongStage.Delivering);
        Assert.Equal([SongStage.Queued, SongStage.WritingMusic, SongStage.Delivering, SongStage.Completed], stages.Seen.Distinct());

        // The next song names the voice only: the host kept its recording.
        _ = await connection.MakeSongAsync(route, Request(), _ => throw new InvalidOperationException("not needed"), null,
            CancellationToken.None, TimeSpan.FromMilliseconds(20));
        Assert.False(singing.Starts[1].GetProperty("reference").GetProperty("audio_base64").GetString()!.Length == 0);

        var status = Assert.Single(await connection.SongOperationAsync(route, new Dictionary<string, object> { ["operation"] = "status" }));
        Assert.Equal("fixture", status.GetProperty("engine").GetString());
    }

    [Fact]
    public async Task Job_failures_unknown_voices_and_cancellation_reach_the_desktop()
    {
        await using var singing = await FakeSinging.StartAsync();
        await using var worker = new SongRelayWorker(singing.Endpoint);
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var (connection, route) = await ConnectAsync(host);
        using var owned = connection;
        Task<byte[]?> Recording(CancellationToken _) => Task.FromResult<byte[]?>(Voice.ReadAudio());

        var unknown = await Assert.ThrowsAsync<SongException>(() => connection.MakeSongAsync(route, Request("0123abcd"), Recording, null,
            CancellationToken.None));
        Assert.Equal(SongErrorCodes.VoiceMissing, unknown.Code);

        singing.Busy = true;
        var busy = await Assert.ThrowsAsync<SongException>(() => connection.MakeSongAsync(route, Request(), Recording, null,
            CancellationToken.None, TimeSpan.FromMilliseconds(20)));
        Assert.Equal(SongErrorCodes.Busy, busy.Code);

        singing.Busy = false;
        singing.NeverFinishes = true;
        using var stop = new CancellationTokenSource();
        var cancelWhenRunning = new Cancel(stop);
        var error = await Record.ExceptionAsync(() => connection.MakeSongAsync(route, Request(), Recording, cancelWhenRunning,
            stop.Token, TimeSpan.FromMilliseconds(20)));
        Assert.True(error is OperationCanceledException, error?.ToString());
        Assert.Equal(["song-1"], singing.Canceled);
    }

    [Fact]
    public async Task A_stopped_service_is_unavailable_and_the_relay_only_reaches_loopback()
    {
        await using var stopped = new SongRelayWorker(new Uri("http://127.0.0.1:1/"));
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [stopped]);
        var (connection, route) = await ConnectAsync(host);
        using var owned = connection;
        var failure = await Assert.ThrowsAsync<SongException>(() => connection.MakeSongAsync(route, Request(),
            _ => Task.FromResult<byte[]?>(Voice.ReadAudio()), null, CancellationToken.None));
        Assert.Equal(SongErrorCodes.Unavailable, failure.Code);

        Assert.Throws<ArgumentException>(() => new SongRelayWorker(new Uri("http://192.168.1.5:50085/")));
        Assert.Throws<ArgumentException>(() => new SongRelayWorker(new Uri("http://127.0.0.1:50085/"), "other-model"));
    }
}
