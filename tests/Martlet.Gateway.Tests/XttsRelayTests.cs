using System.Diagnostics;
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

// The xtts role's relay is the F5 relay on XTTS's own route. Set MARTLET_XTTS_LIVE_ENDPOINT to a running
// workers/xtts/host/martlet_xtts_host.py provisioned with the pinned XTTS-v2 files to speak through the real model.
public sealed class XttsRelayTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Host_offers_f5_and_xtts_side_by_side_on_their_own_routes()
    {
        await using var f5 = new F5RelayWorker(new Uri("http://127.0.0.1:50080/"), F5RelayWorker.DefaultModel);
        await using var xtts = XttsRelay.Create(new Uri("http://127.0.0.1:50081/"), XttsRelay.DefaultModel);
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [f5, xtts]);
        var card = host.OpenPairing(GatewayRole.Voice, "desktop-test");
        var (pairing, secret) = await Audio2FaceHostClient.PairAsync(host.Origin.CanonicalOrigin, card.HostId,
            card.SpkiFingerprint, "desktop-test", card.PairingId, card.Token.Reveal());
        using var connection = new Audio2FaceHostConnection(pairing, secret, host.Clock);
        var routes = await connection.ReadRoutesAsync();
        var voice = Assert.Single(routes, r => r.RouteId == HostRoute.XttsRouteId);
        Assert.Equal(SpeechEngines.Xtts.Path, voice.Path);
        Assert.Equal("xtts-v2", voice.ModelId);
        Assert.Equal("c7ea20001c6a0a841c77e252d8409f6a74fb423e79b3206a0771ba5989776187", voice.ModelSha256);
        Assert.Equal(SpeechEngines.VoiceDestination, voice.DestinationId);
        Assert.Single(routes, r => r.RouteId == HostRoute.F5RouteId);
        Assert.Throws<ArgumentException>(() => XttsRelay.Create(new Uri("http://127.0.0.1:50081/"), "unknown-model"));
    }

    [Fact]
    public async Task Live_xtts_speaks_through_the_gateway_when_configured()
    {
        if (Environment.GetEnvironmentVariable("MARTLET_XTTS_LIVE_ENDPOINT") is not { Length: > 0 } endpoint) return;
        await using var worker = XttsRelay.Create(new Uri(endpoint), XttsRelay.DefaultModel);
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker],
            clock: new ManualGatewayClock(DateTimeOffset.UtcNow));
        var card = host.OpenPairing(GatewayRole.Voice, "desktop-test");
        var (pairing, secret) = await Audio2FaceHostClient.PairAsync(host.Origin.CanonicalOrigin, card.HostId,
            card.SpkiFingerprint, "desktop-test", card.PairingId, card.Token.Reveal());
        using var connection = new Audio2FaceHostConnection(pairing, secret, host.Clock);
        var route = Assert.Single(await connection.ReadRoutesAsync(), r => r.RouteId == HostRoute.XttsRouteId);

        var voice = F5BundledVoices.Default;
        var wav = voice.ReadAudio();
        var audioSha = SHA256.HashData(wav);
        var transcriptSha = SHA256.HashData(Encoding.UTF8.GetBytes(voice.Transcript));
        var reference = new HostSpeechReference(Guid.NewGuid(),
            Convert.ToHexStringLower(SHA256.HashData([.. audioSha, .. transcriptSha])), Convert.ToHexStringLower(audioSha),
            voice.Transcript, Convert.ToHexStringLower(transcriptSha), wav);
        var clock = Stopwatch.StartNew();
        TimeSpan? first = null;
        var samples = 0;
        await foreach (var frame in connection.StreamSpeechAsync(route, new CorrelationIds
            {
                SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid()
            }, 1, DateTimeOffset.UtcNow.AddSeconds(80), reference, "Hello! It's lovely to hear from you again."))
        {
            first ??= clock.Elapsed;
            samples += frame.Length / 2;
        }
        output.WriteLine($"XTTS through the gateway: first audio after {first?.TotalMilliseconds:0} ms, " +
            $"{samples / 24_000d:0.00} s of audio in {clock.Elapsed.TotalMilliseconds:0} ms");
        Assert.True(samples > 24_000);
        Assert.True(first < clock.Elapsed / 2, "audio should arrive while XTTS is still generating");
    }
}
