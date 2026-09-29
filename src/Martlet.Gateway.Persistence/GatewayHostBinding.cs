using System.Net;

namespace Martlet.Gateway.Persistence;

public sealed class GatewayHostBinding
{
    public GatewayOrigin Origin { get; }
    /// <summary>Local listen address when it differs from the advertised origin (container port publishing).</summary>
    public IPAddress? ListenAddress { get; }
    private GatewayHostBinding(GatewayOrigin origin, IPAddress? listen = null)
    {
        Origin = origin;
        ListenAddress = listen;
    }

    public static GatewayHostBinding Loopback(GatewayOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.Address.Equals(IPAddress.Loopback) && !origin.Address.Equals(IPAddress.IPv6Loopback))
            throw PersistenceFailure.Error(GatewayPersistenceFailure.InvalidState);
        return new(origin);
    }

    public static GatewayHostBinding ExactPrivateAddress(GatewayOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (IPAddress.IsLoopback(origin.Address))
            throw PersistenceFailure.Error(GatewayPersistenceFailure.InvalidState);
        // GatewayOrigin has already rejected public, wildcard and noncanonical addresses.
        return new(origin);
    }

    /// <summary>
    /// The advertised private origin is published to this process by a container runtime: listen on every
    /// address of the container's own network namespace, while the certificate and clients use the origin.
    /// Only valid inside a container, whose namespace exposes nothing except explicitly published ports.
    /// </summary>
    public static GatewayHostBinding PublishedPrivateAddress(GatewayOrigin origin, bool insideContainer)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!insideContainer || IPAddress.IsLoopback(origin.Address))
            throw PersistenceFailure.Error(GatewayPersistenceFailure.InvalidState);
        return new(origin, origin.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? IPAddress.IPv6Any : IPAddress.Any);
    }
}
