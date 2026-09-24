using System.Net;

namespace Martlet.Gateway.Persistence;

public sealed class GatewayHostBinding
{
    public GatewayOrigin Origin { get; }
    private GatewayHostBinding(GatewayOrigin origin) => Origin = origin;

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
}
