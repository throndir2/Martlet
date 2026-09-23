using System.Net;
using System.Text;
using System.Text.Json;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

public sealed class CapabilityStatusTests
{
    [Fact]
    public async Task Authenticated_capability_and_status_are_role_filtered_and_bounded()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var credential = await host.PairAsync(GatewayRole.Voice);
        var signer = new GatewayRequestSigner(host.Identity, credential, host.Clock);

        using var capabilityRequest = host.SignedGet(
            "/martlet/v1/capabilities", GatewayRole.Voice, signer);
        using var capabilityResponse = await host.Client.SendAsync(capabilityRequest);
        var capabilityBody = await capabilityResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, capabilityResponse.StatusCode);
        Assert.InRange(Encoding.UTF8.GetByteCount(capabilityBody), 1, 65_536);
        Assert.Contains("\"worker_id\":\"ollama-private\"", capabilityBody, StringComparison.Ordinal);
        Assert.Contains("\"worker_id\":\"f5-private\"", capabilityBody, StringComparison.Ordinal);
        Assert.DoesNotContain("vision-private", capabilityBody, StringComparison.Ordinal);
        Assert.DoesNotContain("memory-private", capabilityBody, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", capabilityBody, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", capabilityBody, StringComparison.Ordinal);

        using var statusRequest = host.SignedGet(
            "/martlet/v1/status", GatewayRole.Voice, signer);
        using var statusResponse = await host.Client.SendAsync(statusRequest);
        using var status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, status.RootElement.GetProperty("workers").GetArrayLength());
        Assert.All(status.RootElement.GetProperty("workers").EnumerateArray(),
            worker => Assert.Equal("ready", worker.GetProperty("state").GetString()));
    }

    [Fact]
    public async Task Private_worker_failure_is_redacted_with_an_exact_remedy()
    {
        var worker = new UnavailableWorker(GatewayTestHost.Capabilities(
            "ollama-private", GatewayWorkerKind.OllamaLlm, GatewayRole.Voice));
        await using var host = await GatewayTestHost.StartAsync([worker]);
        var credential = await host.PairAsync();
        var signer = new GatewayRequestSigner(host.Identity, credential, host.Clock);
        using var request = host.SignedGet(
            "/martlet/v1/status", GatewayRole.Voice, signer);
        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("\"code\":\"worker.unavailable\"", body, StringComparison.Ordinal);
        Assert.Contains("Check only the named private worker", body, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(GatewayWorkerUnavailableException), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pairing_parser_rejects_unknown_duplicate_and_oversized_input_without_echo()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var card = host.OpenPairing();
        var token = card.Token.Reveal();
        var unknown = $$"""
            {"protocol_version":{"major":2,"minor":0},"pairing_id":"{{card.PairingId}}",
             "pairing_token":"{{token}}","host_id":"{{card.HostId}}",
             "spki_fingerprint":"{{card.SpkiFingerprint}}","device_id":"fixture-device",
             "redirect":"https://8.8.8.8/"}
            """;
        using var unknownResponse = await SendRawPairing(host, unknown);
        var unknownBody = await unknownResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, unknownResponse.StatusCode);
        Assert.DoesNotContain(token, unknownBody, StringComparison.Ordinal);
        Assert.DoesNotContain("8.8.8.8", unknownBody, StringComparison.Ordinal);

        var duplicate = unknown.Replace(
            "\"redirect\":\"https://8.8.8.8/\"",
            "\"device_id\":\"fixture-device\"", StringComparison.Ordinal);
        using var duplicateResponse = await SendRawPairing(host, duplicate);
        Assert.Equal("request.invalid", await GatewayTestHost.FailureCode(duplicateResponse));

        using var oversizedResponse = await SendRawPairing(host, new string('x', 8193));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedResponse.StatusCode);
        Assert.Equal("request.too_large", await GatewayTestHost.FailureCode(oversizedResponse));
    }

    [Fact]
    public void Worker_registry_rejects_unbounded_inventory_and_invalid_role_mapping()
    {
        var tooMany = Enumerable.Range(0, GatewayWorkerRegistry.MaximumWorkers + 1)
            .Select(index => (IGatewayWorker)new SyntheticWorker(
                GatewayTestHost.Capabilities(
                    $"worker-{index}", GatewayWorkerKind.Memory, GatewayRole.Memory),
                () => new()
                {
                    State = GatewayWorkerState.Ready,
                    QueueDepth = 0,
                    ObservedAt = DateTimeOffset.UtcNow
                }));
        Assert.Equal("worker.invalid",
            Assert.Throws<GatewayProtocolException>(() => new GatewayWorkerRegistry(tooMany))
                .Failure.Code);

        var invalid = GatewayTestHost.Capabilities(
            "raw-f5", GatewayWorkerKind.F5Tts, GatewayRole.Memory);
        Assert.Equal("worker.invalid",
            Assert.Throws<GatewayProtocolException>(() => new GatewayWorkerRegistry(
                [new SyntheticWorker(invalid, () => throw new InvalidOperationException())]))
                .Failure.Code);
    }

    [Fact]
    public void Worker_capabilities_are_validated_and_frozen_at_registration()
    {
        var worker = new ChangingCapabilitiesWorker(
            GatewayTestHost.Capabilities(
                "ollama-private", GatewayWorkerKind.OllamaLlm, GatewayRole.Voice));
        var registry = new GatewayWorkerRegistry([worker]);
        var capabilities = Assert.Single(registry.CapabilitiesFor(GatewayRole.Voice));
        Assert.Equal("ollama-private", capabilities.WorkerId);
        Assert.Equal(1, worker.CapabilityReads);
    }

    private static ValueTask<HttpResponseMessage> SendRawPairing(
        GatewayTestHost host,
        string body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, host.Origin.CanonicalOrigin + "/martlet/v1/pair")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        return host.Client.SendAsync(request);
    }

    private sealed class ChangingCapabilitiesWorker(
        GatewayWorkerCapabilities capabilities) : IGatewayWorker
    {
        internal int CapabilityReads { get; private set; }

        public GatewayWorkerCapabilities Capabilities
        {
            get
            {
                CapabilityReads++;
                if (CapabilityReads != 1)
                    throw new InvalidOperationException("Capabilities were read after registration.");
                return capabilities;
            }
        }

        public ValueTask<GatewayWorkerStatus> ReadStatusAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new GatewayWorkerStatus
            {
                State = GatewayWorkerState.Ready,
                QueueDepth = 0,
                ObservedAt = DateTimeOffset.UtcNow
            });
    }
}
