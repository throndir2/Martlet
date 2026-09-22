using System.Net;
using System.Security.Cryptography.X509Certificates;
using Martlet.Gateway;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway.Tests;

public sealed class BindingAndPinningTests
{
    [Theory]
    [InlineData("http://127.0.0.1:7443")]
    [InlineData("https://0.0.0.0:7443")]
    [InlineData("https://[::]:7443")]
    [InlineData("https://8.8.8.8:7443")]
    [InlineData("https://localhost:7443")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://127.0.0.1:7443/")]
    [InlineData("https://user@127.0.0.1:7443")]
    [InlineData("https://127.0.0.1:7443/path")]
    [InlineData("https://127.0.0.1:7443?token=secret")]
    [InlineData("HTTPS://127.0.0.1:7443")]
    public void Unsafe_public_or_noncanonical_origins_are_rejected(string value)
    {
        var error = Assert.Throws<GatewayProtocolException>(() => new GatewayOrigin(value));
        Assert.Equal("binding.unsafe", error.Failure.Code);
    }

    [Theory]
    [InlineData("https://127.0.0.1:7443")]
    [InlineData("https://10.0.0.5:7443")]
    [InlineData("https://172.31.255.254:7443")]
    [InlineData("https://192.168.1.10:7443")]
    [InlineData("https://[::1]:7443")]
    [InlineData("https://[fd00::1]:7443")]
    public void Exact_private_and_loopback_https_origins_are_accepted(string value) =>
        Assert.Equal(value, new GatewayOrigin(value).CanonicalOrigin);

    [Fact]
    public async Task Pinned_client_accepts_only_the_presented_host_key()
    {
        await using var host = await GatewayTestHost.StartAsync();
        using var response = await host.Client.SendAsync(new(
            HttpMethod.Get, host.Origin.CanonicalOrigin + "/health/live"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var wrongIdentity = host.Identity with
        {
            SpkiFingerprint = "sha256:" + new string('0', 64)
        };
        using var wrongClient = PinnedGatewayClient.Create(
            host.Origin, wrongIdentity, host.Clock);
        var rejected = await Assert.ThrowsAsync<GatewayClientException>(() =>
            wrongClient.SendAsync(new(
                HttpMethod.Get, host.Origin.CanonicalOrigin + "/health/live")).AsTask());
        Assert.Equal("gateway.connection_failed", rejected.Failure.Code);
    }

    [Fact]
    public async Task Server_uses_the_injected_tls_listener_without_public_or_admin_side_effects()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var binding = new GatewayTlsBinding(
            host.Origin, host.Identity, host.Certificate, host.Clock);
        var audit = new RecordingAuditSink();
        var server = new GatewayServer(host.Identity, host.Origin, [], audit, host.Clock);
        var listener = new CapturingListener();
        await using var handle = await server.StartAsync(binding, listener);
        Assert.Same(binding, listener.Binding);
        Assert.NotNull(listener.Application);
        Assert.Equal(host.Origin.CanonicalOrigin, handle.Origin.CanonicalOrigin);
    }

    [Fact]
    public async Task Binding_rejects_a_missing_private_key_and_a_changed_host_pin()
    {
        await using var host = await GatewayTestHost.StartAsync();
        using var publicOnly = X509CertificateLoader.LoadCertificate(host.Certificate.RawData);
        var missingKey = Assert.Throws<GatewayProtocolException>(() => new GatewayTlsBinding(
            host.Origin, host.Identity, publicOnly, host.Clock));
        Assert.Equal("binding.certificate_invalid", missingKey.Failure.Code);

        var changedPin = host.Identity with
        {
            SpkiFingerprint = "sha256:" + new string('0', 64)
        };
        var mismatch = Assert.Throws<GatewayProtocolException>(() => new GatewayTlsBinding(
            host.Origin, changedPin, host.Certificate, host.Clock));
        Assert.Equal("host.pin_mismatch", mismatch.Failure.Code);

        using var withoutSan = GatewayTestHost.CreateCertificate(
            host.Clock.GetUtcNow(), includeIpSan: false);
        var withoutSanIdentity = GatewayHostIdentity.FromCertificate(
            "fixture-host", withoutSan);
        var invalidName = Assert.Throws<GatewayProtocolException>(() => new GatewayTlsBinding(
            host.Origin, withoutSanIdentity, withoutSan, host.Clock));
        Assert.Equal("binding.certificate_invalid", invalidName.Failure.Code);
    }

    [Fact]
    public async Task Redirects_and_cross_origin_requests_are_rejected_without_following()
    {
        var origin = new GatewayOrigin("https://127.0.0.1:7443");
        var handler = new RedirectHandler();
        using var client = PinnedGatewayClient.CreateForFixture(origin, handler);
        var redirect = await Assert.ThrowsAsync<GatewayClientException>(() =>
            client.SendAsync(new(HttpMethod.Get,
                origin.CanonicalOrigin + "/martlet/v1/status")).AsTask());
        Assert.Equal("gateway.redirect_rejected", redirect.Failure.Code);
        Assert.Equal(1, handler.Calls);

        var unsafeOrigin = await Assert.ThrowsAsync<GatewayProtocolException>(() =>
            client.SendAsync(new(HttpMethod.Get,
                "https://192.168.1.9:7443/martlet/v1/status")).AsTask());
        Assert.Equal("binding.unsafe", unsafeOrigin.Failure.Code);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Headers = { Location = new("https://8.8.8.8:7443/steal") }
            });
        }
    }

    private sealed class CapturingListener : IGatewayListenerFactory
    {
        internal GatewayTlsBinding? Binding { get; private set; }
        internal RequestDelegate? Application { get; private set; }

        public ValueTask<GatewayListenerHandle> StartAsync(
            GatewayTlsBinding binding,
            RequestDelegate application,
            CancellationToken cancellationToken)
        {
            Binding = binding;
            Application = application;
            return ValueTask.FromResult<GatewayListenerHandle>(new Handle(binding.Origin));
        }

        private sealed class Handle(GatewayOrigin origin) : GatewayListenerHandle
        {
            public override GatewayOrigin Origin { get; } = origin;
            public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
