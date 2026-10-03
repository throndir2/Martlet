using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.F5;
using Martlet.Gateway.Dia;
using Martlet.Gateway.F5;
using Martlet.Gateway.Xtts;
using Xunit.Abstractions;

namespace Martlet.Gateway.Tests;

// The dia role's relay is the F5 relay on Dia's own route. Set MARTLET_DIA_LIVE_ENDPOINT to a running
// workers/dia martlet_dia.host service provisioned with the pinned Dia files to speak through the real model
// (MARTLET_DIA_LIVE_FIXTURE=1 when it was provisioned with --fixture: FIXTURE - NOT AI, plumbing only).
// MARTLET_DIA_LIVE_OUTPUT saves the spoken reply as a 24 kHz WAV.
public sealed class DiaRelayTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Host_offers_f5_xtts_and_dia_side_by_side_on_their_own_routes()
    {
        await using var f5 = new F5RelayWorker(new Uri("http://127.0.0.1:50080/"), F5RelayWorker.DefaultModel);
        await using var xtts = XttsRelay.Create(new Uri("http://127.0.0.1:50081/"), XttsRelay.DefaultModel);
        await using var dia = DiaRelay.Create(new Uri("http://127.0.0.1:50084/"), DiaRelay.DefaultModel);
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [f5, xtts, dia]);
        var card = host.OpenPairing(GatewayRole.Voice, "desktop-test");
        var (pairing, secret) = await Audio2FaceHostClient.PairAsync(host.Origin.CanonicalOrigin, card.HostId,
            card.SpkiFingerprint, "desktop-test", card.PairingId, card.Token.Reveal());
        using var connection = new Audio2FaceHostConnection(pairing, secret, host.Clock);
        var routes = await connection.ReadRoutesAsync();
        var voice = Assert.Single(routes, r => r.RouteId == HostRoute.DiaRouteId);
        Assert.Equal(SpeechEngines.Dia.Path, voice.Path);
        Assert.Equal("dia-1.6b-0626", voice.ModelId);
        Assert.Equal("8a5106c06899aeea013a7f6ef32e84a15a30f965be676b97996b3ddaf1eb55b9", voice.ModelSha256);
        Assert.Equal(SpeechEngines.VoiceDestination, voice.DestinationId);
        Assert.Single(routes, r => r.RouteId == HostRoute.F5RouteId);
        Assert.Single(routes, r => r.RouteId == HostRoute.XttsRouteId);
        Assert.Throws<ArgumentException>(() => DiaRelay.Create(new Uri("http://127.0.0.1:50084/"), "unknown-model"));
    }

    [Fact]
    public async Task Live_dia_speaks_through_the_gateway_when_configured()
    {
        if (Environment.GetEnvironmentVariable("MARTLET_DIA_LIVE_ENDPOINT") is not { Length: > 0 } endpoint) return;
        var fixture = Environment.GetEnvironmentVariable("MARTLET_DIA_LIVE_FIXTURE") == "1";
        await using var worker = fixture
            ? new F5RelayWorker(new Uri(endpoint), GatewayInferenceRoute.ReferenceSpeechRelay(SpeechEngines.Dia,
                SpeechEngines.VoiceDestination, DiaRelay.DefaultWorkerId, "fixture-dia-1.6b-0626", new string('0', 40), new string('0', 64)))
            : DiaRelay.Create(new Uri(endpoint), DiaRelay.DefaultModel);
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker],
            clock: new ManualGatewayClock(DateTimeOffset.UtcNow));
        var card = host.OpenPairing(GatewayRole.Voice, "desktop-test");
        var (pairing, secret) = await Audio2FaceHostClient.PairAsync(host.Origin.CanonicalOrigin, card.HostId,
            card.SpkiFingerprint, "desktop-test", card.PairingId, card.Token.Reveal());
        using var connection = new Audio2FaceHostConnection(pairing, secret, host.Clock);
        var route = Assert.Single(await connection.ReadRoutesAsync(), r => r.RouteId == HostRoute.DiaRouteId);

        var voice = F5BundledVoices.Default;
        var wav = voice.ReadAudio();
        var audioSha = SHA256.HashData(wav);
        var transcriptSha = SHA256.HashData(Encoding.UTF8.GetBytes(voice.Transcript));
        var reference = new HostSpeechReference(Guid.NewGuid(),
            Convert.ToHexStringLower(SHA256.HashData([.. audioSha, .. transcriptSha])), Convert.ToHexStringLower(audioSha),
            voice.Transcript, Convert.ToHexStringLower(transcriptSha), wav);
        var clock = Stopwatch.StartNew();
        var pcm = new MemoryStream();
        await foreach (var frame in connection.StreamSpeechAsync(route, new CorrelationIds
            {
                SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid()
            }, 1, DateTimeOffset.UtcNow.AddSeconds(85), reference, "Oh, that's so funny! (laughs) I really didn't expect that."))
            pcm.Write(frame);
        output.WriteLine($"Dia through the gateway: {pcm.Length / 48_000d:0.00} s of audio in {clock.Elapsed.TotalSeconds:0.0} s");
        Assert.True(pcm.Length > 48_000);
        if (Environment.GetEnvironmentVariable("MARTLET_DIA_LIVE_OUTPUT") is { Length: > 0 } path)
        {
            using var file = File.Create(path);
            using var writer = new BinaryWriter(file);
            writer.Write("RIFF"u8); writer.Write((int)(36 + pcm.Length)); writer.Write("WAVEfmt "u8); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(24_000); writer.Write(48_000); writer.Write((short)2);
            writer.Write((short)16); writer.Write("data"u8); writer.Write((int)pcm.Length); writer.Write(pcm.ToArray());
        }
    }
}
