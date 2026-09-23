using System.Net;
using System.Security.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Martlet.Gateway;

public interface IGatewayListenerFactory
{
    ValueTask<GatewayListenerHandle> StartAsync(
        GatewayTlsBinding binding,
        RequestDelegate application,
        CancellationToken cancellationToken);
}

public abstract class GatewayListenerHandle : IAsyncDisposable
{
    public abstract GatewayOrigin Origin { get; }
    public abstract ValueTask DisposeAsync();
}

public sealed class GatewayServer
{
    private readonly GatewayHostIdentity identity;
    private readonly GatewayOrigin origin;
    private readonly GatewayHttpApplication application;
    private int started;

    public GatewayCredentialStore Credentials { get; }
    public GatewayPairingService Pairing { get; }

    public GatewayServer(
        GatewayHostIdentity identity,
        GatewayOrigin origin,
        IEnumerable<IGatewayWorker> workers,
        IGatewayAuditSink audit,
        TimeProvider? clock = null,
        IGatewayCrypto? crypto = null,
        TimeSpan? credentialLifetime = null,
        TimeSpan? pairingWindow = null)
        : this(identity, origin, workers, audit, clock, crypto, credentialLifetime, pairingWindow, null)
    {
    }

    internal GatewayServer(
        GatewayHostIdentity identity,
        GatewayOrigin origin,
        IEnumerable<IGatewayWorker> workers,
        IGatewayAuditSink audit,
        TimeProvider? clock,
        IGatewayCrypto? crypto,
        TimeSpan? credentialLifetime,
        TimeSpan? pairingWindow,
        GatewayCredentialStore? ownedCredentials)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(workers);
        ArgumentNullException.ThrowIfNull(audit);
        identity.Validate();
        this.identity = identity;
        this.origin = origin;
        var effectiveClock = clock ?? TimeProvider.System;
        var effectiveCrypto = crypto ?? new SystemGatewayCrypto();
        Credentials = ownedCredentials ?? new(identity, effectiveClock, effectiveCrypto, credentialLifetime);
        Pairing = new(identity, origin, Credentials, effectiveClock, effectiveCrypto, pairingWindow);
        application = new(identity, Pairing, Credentials, new(workers),
            effectiveClock, effectiveCrypto, audit);
    }

    public async ValueTask<GatewayListenerHandle> StartAsync(
        GatewayTlsBinding binding,
        IGatewayListenerFactory listenerFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(listenerFactory);
        GatewayRules.Require(binding.Identity == identity &&
            binding.Origin.CanonicalOrigin == origin.CanonicalOrigin, "binding.unsafe");
        GatewayRules.Require(Interlocked.CompareExchange(ref started, 1, 0) == 0, "request.invalid");
        try
        {
            return await listenerFactory.StartAsync(
                binding, application.InvokeAsync, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Exchange(ref started, 0);
            throw;
        }
    }
}

public sealed class KestrelGatewayListenerFactory : IGatewayListenerFactory
{
    public async ValueTask<GatewayListenerHandle> StartAsync(
        GatewayTlsBinding binding,
        RequestDelegate application,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(application);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(KestrelGatewayListenerFactory).Assembly.FullName,
            Args = []
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = GatewayHttpApplication.MaximumPairingRequestBytes;
            options.Limits.MaxConcurrentConnections = 64;
            options.Limits.MaxRequestHeaderCount = 32;
            options.Limits.MaxRequestHeadersTotalSize = 16_384;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            options.Listen(binding.Origin.Address, binding.Origin.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = binding.Certificate,
                    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateMode = ClientCertificateMode.NoCertificate
                });
            });
        });
        var app = builder.Build();
        app.Run(application);
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            GatewayRules.Require(addresses is { Count: 1 } &&
                addresses.Single() == binding.Origin.CanonicalOrigin, "binding.unsafe");
            return new KestrelGatewayListenerHandle(binding.Origin, app);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class KestrelGatewayListenerHandle(
        GatewayOrigin origin,
        WebApplication application) : GatewayListenerHandle
    {
        private int disposed;
        public override GatewayOrigin Origin { get; } = origin;

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            await application.StopAsync().ConfigureAwait(false);
            await application.DisposeAsync().ConfigureAwait(false);
        }
    }
}
