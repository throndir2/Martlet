using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Audio2Face.Tests;
using Martlet.Core.Contracts;
using Martlet.Gateway;
using Martlet.Gateway.Audio2Face;
using NvidiaAce.AnimationData.V1;
using NvidiaAce.Status.V1;
using Output = NvidiaAce.Controller.V1.AnimationDataStream;

namespace Martlet.Gateway.Tests;

// NOT AI: a controlled Audio2Face gRPC fixture stands in for the host's NIM service.
public sealed class Audio2FaceRelayTests
{
    private static Task<ProtocolFixture> StartNim() => ProtocolFixture.StartAsync(async (writer, _) =>
    {
        await writer.WriteAsync(new Output { AnimationDataStreamHeader = new()
        {
            SkelAnimationHeader = new() { BlendShapes = { "JawOpen", "MouthFunnel" } }
        } });
        await writer.WriteAsync(new Output { AnimationData = new()
        {
            SkelAnimation = new() { BlendShapeWeights =
            {
                new FloatArrayWithTimeCode { TimeCode = 0, Values = { 0.75f, 0.25f } },
                new FloatArrayWithTimeCode { TimeCode = 0.02, Values = { 0.5f, 0.1f } }
            } }
        } });
        await writer.WriteAsync(new Output { Status = new() { Code = Status.Types.Code.Success } });
    });

    private static CorrelationIds NewIds() => new() { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };

    [Fact]
    public async Task Paired_desktop_client_relays_generated_speech_through_the_gateway_to_the_hosts_audio2face()
    {
        await using var nim = await StartNim();
        await using var worker = new Audio2FaceRelayWorker(nim.Endpoint, "claire", "1.3");
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var card = host.OpenPairing(GatewayRole.Voice, "desktop-test");
        var (pairing, secret) = await Audio2FaceHostClient.PairAsync(host.Origin.CanonicalOrigin, card.HostId,
            card.SpkiFingerprint, "desktop-test", card.PairingId, card.Token.Reveal());
        Assert.Equal(host.Identity.SpkiFingerprint, pairing.SpkiFingerprint);

        using var connection = new Audio2FaceHostConnection(pairing, secret, host.Clock);
        var route = await connection.ReadRouteAsync();
        Assert.NotNull(route);
        Assert.Equal("claire", route!.ModelId);

        var frames = new List<RemoteFaceFrame>();
        await foreach (var frame in connection.AnimateAsync(route, NewIds(), 3, 24_000, new byte[24_000]))
            frames.Add(frame);
        Assert.Equal(2, frames.Count);
        Assert.Equal(0.75, frames[0].Blendshapes["jawOpen"], 3);
        Assert.Equal(0.25, frames[0].Blendshapes["mouthFunnel"], 3);
        Assert.Equal(0, frames[0].SampleOffset);
        Assert.Equal(480, frames[1].SampleOffset);
        Assert.Contains(nim.Requests, input => input.AudioWithEmotion is not null);

        // A second chunk for the same sentence works; the route admits one request at a time.
        await foreach (var _ in connection.AnimateAsync(route, NewIds(), 3, 24_000, new byte[4_800])) { }
    }

    [Fact]
    public async Task Relay_reports_an_unreachable_audio2face_service_and_rejects_unpaired_or_wrong_credentials()
    {
        var closed = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        closed.Start();
        var port = ((System.Net.IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        await using var worker = new Audio2FaceRelayWorker(new Uri($"http://127.0.0.1:{port}/"), "claire", "1.3");
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var card = host.OpenPairing(GatewayRole.Voice, "desktop-test");
        var (pairing, secret) = await Audio2FaceHostClient.PairAsync(host.Origin.CanonicalOrigin, card.HostId,
            card.SpkiFingerprint, "desktop-test", card.PairingId, card.Token.Reveal());
        using var connection = new Audio2FaceHostConnection(pairing, secret, host.Clock);
        var route = (await connection.ReadRouteAsync())!;
        var failure = await Assert.ThrowsAsync<Audio2FaceHostException>(async () =>
        {
            await foreach (var _ in connection.AnimateAsync(route, NewIds(), 0, 24_000, new byte[4_800])) { }
        });
        Assert.Equal("worker.unavailable", failure.Code);

        var wrongSecret = new string('A', 43);
        using var impostor = new Audio2FaceHostConnection(pairing, wrongSecret, host.Clock);
        var denied = await Assert.ThrowsAsync<Audio2FaceHostException>(() => impostor.ReadRouteAsync());
        Assert.StartsWith("auth.", denied.Code, StringComparison.Ordinal);

        var wrongPin = pairing with { SpkiFingerprint = "sha256:" + new string('0', 64) };
        using var spoofed = new Audio2FaceHostConnection(wrongPin, secret, host.Clock);
        Assert.Equal("host.unreachable", (await Assert.ThrowsAsync<Audio2FaceHostException>(() => spoofed.ReadRouteAsync())).Code);
    }

    [Fact]
    public async Task Host_without_the_relay_advertises_no_audio2face_route()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var card = host.OpenPairing(GatewayRole.Voice, "desktop-test");
        var (pairing, secret) = await Audio2FaceHostClient.PairAsync(host.Origin.CanonicalOrigin, card.HostId,
            card.SpkiFingerprint, "desktop-test", card.PairingId, card.Token.Reveal());
        using var connection = new Audio2FaceHostConnection(pairing, secret, host.Clock);
        Assert.Null(await connection.ReadRouteAsync());
    }

    [Fact]
    public async Task Paired_desktop_reads_the_hardware_the_host_reported()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var card = host.OpenPairing(GatewayRole.Voice, "desktop-test");
        var (pairing, secret) = await Audio2FaceHostClient.PairAsync(host.Origin.CanonicalOrigin, card.HostId,
            card.SpkiFingerprint, "desktop-test", card.PairingId, card.Token.Reveal());
        using var connection = new Audio2FaceHostConnection(pairing, secret, host.Clock);
        Assert.Null(await connection.ReadMachineAsync());

        host.Server.Machine = GatewayMachineReport.Parse(System.Text.Encoding.UTF8.GetBytes(
            "{\"collected_at\":\"2026-09-29T18:00:00Z\",\"method\":\"docker\",\"operating_system\":\"Docker Desktop\"," +
            "\"processor_threads\":24,\"memory_gb\":31.2,\"nvidia_containers\":\"yes\",\"gpus\":[" +
            "{\"name\":\"NVIDIA GeForce RTX 4080\",\"vendor\":\"nvidia\",\"memory_mb\":16376,\"driver\":\"566.03\"}," +
            "{\"name\":\"AMD Radeon RX 6800\",\"vendor\":\"amd\",\"memory_mb\":16368}]}"));
        var report = await connection.ReadMachineAsync();

        Assert.NotNull(report);
        Assert.Equal(pairing.HostId, report!.HostId);
        Assert.Equal("docker", report.Method);
        Assert.Equal(2, report.Gpus.Count);
        Assert.Equal("NVIDIA GeForce RTX 4080", report.BestGpu!.Name);
        Assert.Equal(Martlet.Core.Installation.AdvisorGpu.Nvidia16, report.AdvisorGpu);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 18, 0, 0, TimeSpan.Zero), report.CollectedAt);
    }
}
