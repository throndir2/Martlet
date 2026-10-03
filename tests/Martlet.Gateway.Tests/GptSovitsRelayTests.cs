using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.F5;
using Martlet.Gateway.F5;
using Martlet.Gateway.GptSovits;
using Xunit.Abstractions;

namespace Martlet.Gateway.Tests;

// The gpt-sovits role's relay is the F5 relay on GPT-SoVITS's own route, which also requires the pinned GPT weights. Set
// MARTLET_GPT_SOVITS_LIVE_ENDPOINT to a running martlet_gpt_sovits host provisioned with the pinned v2Pro pair to speak
// through the real model.
public sealed class GptSovitsRelayTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Host_offers_gpt_sovits_on_its_own_route_next_to_f5()
    {
        await using var f5 = new F5RelayWorker(new Uri("http://127.0.0.1:50080/"), F5RelayWorker.DefaultModel);
        await using var gptSovits = GptSovitsRelay.Create(new Uri("http://127.0.0.1:50082/"), GptSovitsRelay.DefaultModel);
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [f5, gptSovits]);
        using var connection = await ConnectAsync(host);
        var routes = await connection.ReadRoutesAsync();
        var voice = Assert.Single(routes, r => r.RouteId == HostRoute.GptSovitsRouteId);
        Assert.Equal(SpeechEngines.GptSovits.Path, voice.Path);
        Assert.Equal("gpt-sovits-v2pro", voice.ModelId);
        Assert.Equal("0f8ead815234365edf045c6d86370ed6e4f440e8195be77ff0ea72684ad406a5", voice.ModelSha256);
        Assert.Equal(SpeechEngines.VoiceDestination, voice.DestinationId);
        Assert.Single(routes, r => r.RouteId == HostRoute.F5RouteId);
        Assert.Throws<ArgumentException>(() => GptSovitsRelay.Create(new Uri("http://127.0.0.1:50082/"), "unknown-model"));
    }

    [Fact]
    public async Task Voices_outside_three_to_ten_seconds_are_refused_before_sending()
    {
        await using var gptSovits = GptSovitsRelay.Create(new Uri("http://127.0.0.1:50082/"), GptSovitsRelay.DefaultModel);
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [gptSovits]);
        using var connection = await ConnectAsync(host);
        var route = Assert.Single(await connection.ReadRoutesAsync(), r => r.RouteId == HostRoute.GptSovitsRouteId);
        // "Bee (cute, bubbly)" is 10.6 seconds long.
        var error = await Assert.ThrowsAsync<Audio2FaceHostException>(async () =>
        {
            await foreach (var _ in connection.StreamSpeechAsync(route, Ids(), 1, host.Clock.GetUtcNow().AddSeconds(30),
                Reference(F5BundledVoices.All.Single(v => v.Key == "librivox-woollybee")), "Hello."))
            {
            }
        });
        Assert.Equal("request.invalid", error.Code);
        Assert.Equal("en", SpeechEngines.ReferenceLanguage(F5BundledVoices.Default.Transcript));
        Assert.Equal("ja", SpeechEngines.ReferenceLanguage("こんにちは、元気ですか。"));
    }

    [Fact]
    public async Task Live_gpt_sovits_speaks_through_the_gateway_when_configured()
    {
        if (Environment.GetEnvironmentVariable("MARTLET_GPT_SOVITS_LIVE_ENDPOINT") is not { Length: > 0 } endpoint) return;
        await using var worker = GptSovitsRelay.Create(new Uri(endpoint), GptSovitsRelay.DefaultModel);
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker],
            clock: new ManualGatewayClock(DateTimeOffset.UtcNow));
        using var connection = await ConnectAsync(host);
        var route = Assert.Single(await connection.ReadRoutesAsync(), r => r.RouteId == HostRoute.GptSovitsRouteId);
        // Japanese needs pyopenjtalk, which the role image has (MARTLET_GPT_SOVITS_LIVE_JAPANESE=1 when testing against it).
        var texts = Environment.GetEnvironmentVariable("MARTLET_GPT_SOVITS_LIVE_JAPANESE") == "1"
            ? new[] { "Hello! It's lovely to hear from you again. Shall we have some tea?", "こんにちは！今日はいい天気ですね。" }
            : ["Hello! It's lovely to hear from you again. Shall we have some tea?"];
        foreach (var text in texts)
        {
            var clock = Stopwatch.StartNew();
            TimeSpan? first = null;
            var samples = 0;
            await foreach (var frame in connection.StreamSpeechAsync(route, Ids(), 1, DateTimeOffset.UtcNow.AddSeconds(85),
                Reference(F5BundledVoices.Default), text))
            {
                first ??= clock.Elapsed;
                samples += frame.Length / 2;
            }
            output.WriteLine($"GPT-SoVITS through the gateway: first audio after {first?.TotalMilliseconds:0} ms, " +
                $"{samples / 24_000d:0.00} s of 24 kHz audio in {clock.Elapsed.TotalMilliseconds:0} ms");
            Assert.True(samples > 24_000);
        }
    }

    private static async Task<Audio2FaceHostConnection> ConnectAsync(GatewayTestHost host)
    {
        var card = host.OpenPairing(GatewayRole.Voice, "desktop-test");
        var (pairing, secret) = await Audio2FaceHostClient.PairAsync(host.Origin.CanonicalOrigin, card.HostId,
            card.SpkiFingerprint, "desktop-test", card.PairingId, card.Token.Reveal());
        return new Audio2FaceHostConnection(pairing, secret, host.Clock);
    }

    private static CorrelationIds Ids() => new() { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };

    private static HostSpeechReference Reference(F5BundledVoice voice)
    {
        var wav = voice.ReadAudio();
        var audioSha = SHA256.HashData(wav);
        var transcriptSha = SHA256.HashData(Encoding.UTF8.GetBytes(voice.Transcript));
        return new HostSpeechReference(Guid.NewGuid(),
            Convert.ToHexStringLower(SHA256.HashData([.. audioSha, .. transcriptSha])), Convert.ToHexStringLower(audioSha),
            voice.Transcript, Convert.ToHexStringLower(transcriptSha), wav);
    }
}
