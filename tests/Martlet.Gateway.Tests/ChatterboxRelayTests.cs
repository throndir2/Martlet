using System.Security.Cryptography;
using System.Text;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.F5;
using Martlet.Gateway.F5;
using Martlet.Gateway.Xtts;
using Xunit.Abstractions;

namespace Martlet.Gateway.Tests;

// The chatterbox role's relay is the F5 relay on Chatterbox's own route. Set MARTLET_CHATTERBOX_LIVE_ENDPOINT to a running
// workers/chatterbox/martlet_chatterbox_host.py (provisioned with the pinned model, or with "provision --fixture" and
// MARTLET_CHATTERBOX_FIXTURE_REAL_IDENTITY=1 for a FIXTURE - NOT AI plumbing check) to speak through it.
public sealed class ChatterboxRelayTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Host_offers_chatterbox_next_to_f5_and_xtts_on_its_own_route()
    {
        await using var f5 = new F5RelayWorker(new Uri("http://127.0.0.1:50080/"), F5RelayWorker.DefaultModel);
        await using var xtts = XttsRelay.Create(new Uri("http://127.0.0.1:50081/"), XttsRelay.DefaultModel);
        await using var chatterbox = ChatterboxRelay.Create(new Uri("http://127.0.0.1:50082/"), ChatterboxRelay.DefaultModel);
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [f5, xtts, chatterbox]);
        var card = host.OpenPairing(GatewayRole.Voice, "desktop-test");
        var (pairing, secret) = await Audio2FaceHostClient.PairAsync(host.Origin.CanonicalOrigin, card.HostId,
            card.SpkiFingerprint, "desktop-test", card.PairingId, card.Token.Reveal());
        using var connection = new Audio2FaceHostConnection(pairing, secret, host.Clock);
        var routes = await connection.ReadRoutesAsync();
        var voice = Assert.Single(routes, r => r.RouteId == SpeechEngines.Chatterbox.RouteId);
        Assert.Equal(SpeechEngines.Chatterbox.Path, voice.Path);
        Assert.Equal("chatterbox-turbo", voice.ModelId);
        Assert.Equal("fcf1f8c1d651bb7e3acd69ee5be269b4ac10c02980b7708213d598bc9f7cdf87", voice.ModelSha256);
        Assert.Equal(SpeechEngines.VoiceDestination, voice.DestinationId);
        Assert.Equal(3, routes.Count(r => SpeechEngines.ForRoute(r.RouteId) is not null));
        Assert.Throws<ArgumentException>(() => ChatterboxRelay.Create(new Uri("http://127.0.0.1:50082/"), "unknown-model"));
    }

    [Fact]
    public async Task Live_chatterbox_speaks_tags_through_the_gateway_when_configured()
    {
        if (Environment.GetEnvironmentVariable("MARTLET_CHATTERBOX_LIVE_ENDPOINT") is not { Length: > 0 } endpoint) return;
        await using var worker = ChatterboxRelay.Create(new Uri(endpoint), ChatterboxRelay.DefaultModel);
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker],
            clock: new ManualGatewayClock(DateTimeOffset.UtcNow));
        var card = host.OpenPairing(GatewayRole.Voice, "desktop-test");
        var (pairing, secret) = await Audio2FaceHostClient.PairAsync(host.Origin.CanonicalOrigin, card.HostId,
            card.SpkiFingerprint, "desktop-test", card.PairingId, card.Token.Reveal());
        using var connection = new Audio2FaceHostConnection(pairing, secret, host.Clock);
        var route = Assert.Single(await connection.ReadRoutesAsync(), r => r.RouteId == SpeechEngines.Chatterbox.RouteId);

        var voice = F5BundledVoices.Default;
        var wav = voice.ReadAudio();
        var audioSha = SHA256.HashData(wav);
        var transcriptSha = SHA256.HashData(Encoding.UTF8.GetBytes(voice.Transcript));
        var reference = new HostSpeechReference(Guid.NewGuid(),
            Convert.ToHexStringLower(SHA256.HashData([.. audioSha, .. transcriptSha])), Convert.ToHexStringLower(audioSha),
            voice.Transcript, Convert.ToHexStringLower(transcriptSha), wav);
        var samples = 0;
        await foreach (var frame in connection.StreamSpeechAsync(route, new CorrelationIds
            {
                SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid()
            }, 1, DateTimeOffset.UtcNow.AddSeconds(80), reference, "That's hilarious [laugh] okay, so where were we?"))
            samples += frame.Length / 2;
        output.WriteLine($"Chatterbox through the gateway: {samples / 24_000d:0.00} s of 24 kHz audio");
        Assert.True(samples > 24_000);
    }
}
