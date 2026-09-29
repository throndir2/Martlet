using System.Net;
using System.Net.Sockets;

namespace Martlet.Avatar.Audio2Face;

/// <summary>Cheap presence check: is anything accepting connections on the configured loopback endpoint?</summary>
/// <remarks>A successful connect is not proof of a working Audio2Face service; callers must still handle
/// animation failure (Martlet falls back to loudness lip-sync).</remarks>
public static class Audio2FaceProbe
{
    public static async Task<bool> IsListeningAsync(Audio2FaceOptions options, TimeSpan timeout, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var address = IPAddress.Parse(options.Endpoint.Host.Trim('[', ']'));
        using var client = new TcpClient(address.AddressFamily);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(address, options.Endpoint.Port, limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }
}
