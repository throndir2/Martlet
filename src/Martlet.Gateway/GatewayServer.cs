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
    private readonly GatewayInferenceRouteRegistry inference;
    private int started;

    public GatewayCredentialStore Credentials { get; }
    public GatewayPairingService Pairing { get; }

    /// <summary>Host-reported hardware served to paired desktops at /martlet/v1/machine; null when not collected.</summary>
    public GatewayMachineReport? Machine
    {
        get => application.Machine;
        set => application.Machine = value is null || value.IsValid() ? value : throw new ArgumentException("Invalid machine report.", nameof(value));
    }

    /// <summary>Keeps this host's copy of the shared cluster plan (served at /martlet/v1/cluster) in <paramref name="storage"/>
    /// and loads the copy saved there.</summary>
    public void AttachClusterStorage(IGatewayClusterStorage storage) => application.Cluster.Attach(storage);

    public GatewayServer(
        GatewayHostIdentity identity,
        GatewayOrigin origin,
        IEnumerable<IGatewayWorker> workers,
        IGatewayAuditSink audit,
        TimeProvider? clock = null,
        IGatewayCrypto? crypto = null,
        TimeSpan? pairingWindow = null,
        IEnumerable<IGatewayInferenceWorker>? inferenceWorkers = null)
        : this(identity, origin, workers, audit, clock, crypto, pairingWindow, null, inferenceWorkers)
    {
    }

    internal GatewayServer(
        GatewayHostIdentity identity,
        GatewayOrigin origin,
        IEnumerable<IGatewayWorker> workers,
        IGatewayAuditSink audit,
        TimeProvider? clock,
        IGatewayCrypto? crypto,
        TimeSpan? pairingWindow,
        GatewayCredentialStore? ownedCredentials,
        IEnumerable<IGatewayInferenceWorker>? inferenceWorkers = null,
        GatewayInferenceRouteRegistry? inferenceRegistry = null)
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
        Credentials = ownedCredentials ?? new(identity, effectiveClock, effectiveCrypto);
        Pairing = new(identity, origin, Credentials, effectiveClock, effectiveCrypto, pairingWindow);
        inference = inferenceRegistry ?? new(inferenceWorkers ?? [], effectiveClock);
        application = new(identity, Pairing, Credentials, Credentials, new(workers),
            inference,
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
            var listener = await listenerFactory.StartAsync(
                binding, application.InvokeAsync, cancellationToken).ConfigureAwait(false);
            return new InferenceListener(listener, inference);
        }
        catch
        {
            Interlocked.Exchange(ref started, 0);
            throw;
        }

    }

    private sealed class InferenceListener(
        GatewayListenerHandle listener, GatewayInferenceRouteRegistry inference) : GatewayListenerHandle
    {
        public override GatewayOrigin Origin => listener.Origin;
        public override async ValueTask DisposeAsync()
        {
            var closing = inference.CloseAsync().AsTask();
            await listener.DisposeAsync().ConfigureAwait(false);
            await closing.ConfigureAwait(false);
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
            options.Listen(binding.ListenAddress ?? binding.Origin.Address, binding.Origin.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = binding.Certificate,
                    ServerCertificateSelector = binding.CertificateSelector is { } select
                        ? (_, _) => select() : null,
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
            var expected = binding.ListenAddress is { } any
                ? new UriBuilder("https", any.ToString(), binding.Origin.Port).Uri.GetLeftPart(UriPartial.Authority)
                : binding.Origin.CanonicalOrigin;
            GatewayRules.Require(addresses is { Count: 1 } &&
                addresses.Single() == expected, "binding.unsafe");
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
