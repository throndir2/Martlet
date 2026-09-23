using System.Net;
using System.Net.Sockets;

namespace Martlet.Host.Setup;

internal static class ArtifactDownloadConnectionPolicy
{
    internal static ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken) =>
        ResolveAndConnectAsync(context.DnsEndPoint, cancellationToken,
            static (host, token) => Dns.GetHostAddressesAsync(host, token), ConnectAddressAsync);

    internal static async ValueTask<Stream> ResolveAndConnectAsync(
        DnsEndPoint endpoint,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>> connect)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (endpoint.Port != 443 ||
            endpoint.Host is not ("api.github.com" or "release-assets.githubusercontent.com"))
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.RedirectRejected);
        return await ConnectVettedEndpointAsync(endpoint, cancellationToken, resolve, connect).ConfigureAwait(false);
    }

    internal static async ValueTask<Stream> ConnectVettedEndpointAsync(
        DnsEndPoint endpoint, CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>> connect)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var addresses = await resolve(endpoint.Host, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (addresses.Length == 0 || addresses.Any(address => !IsGloballyReachable(address)))
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.TransportFailed);
        // Fail closed on any mixed DNS answer; connect exactly once to an already-vetted address.
        // SocketsHttpHandler retains the original hostname for TLS/SNI/certificate validation.
        return await connect(addresses[0], endpoint.Port, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<Stream> ConnectAddressAsync(
        IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static bool IsGloballyReachable(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var a = bytes[0];
            var b = bytes[1];
            var c = bytes[2];
            return a != 0 && a != 10 && a != 127 && a < 224 &&
                !(a == 100 && b is >= 64 and <= 127) &&
                !(a == 169 && b == 254) &&
                !(a == 172 && b is >= 16 and <= 31) &&
                !(a == 192 && (b == 168 || (b == 0 && c is 0 or 2) || (b == 88 && c == 99))) &&
                !(a == 198 && (b is 18 or 19 || (b == 51 && c == 100))) &&
                !(a == 203 && b == 0 && c == 113);
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || address.ScopeId != 0)
            return false;
        // Only ordinary global unicast; exclude special-purpose, documentation and 6to4 space.
        return (bytes[0] & 0xe0) == 0x20 &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] < 2) &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) &&
            !(bytes[0] == 0x20 && bytes[1] == 0x02) &&
            !(bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0);
    }
}
