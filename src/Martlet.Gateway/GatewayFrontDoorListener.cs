using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway;

/// <summary>The front door's listeners (docs/OPENAI_ENDPOINT.md): the gateway's TLS key on <see cref="GatewayExposure.FrontDoorPort"/>,
/// on the address the gateway listens on and on loopback (so a proxy or Tailscale Funnel on the same computer reaches it), each
/// with <see cref="GatewayHttpApplication.InvokeFrontDoorAsync"/> as its only handler. A listener that can't open is logged
/// and skipped; the gateway itself always starts.</summary>
internal sealed class GatewayFrontDoorListener : IAsyncDisposable
{
    private readonly List<(string Name, WebApplication App)> apps = [];

    internal IReadOnlyList<string> Listening => apps.Select(a => a.Name).ToArray();

    internal static async Task<GatewayFrontDoorListener> StartAsync(GatewayTlsBinding binding, int port, RequestDelegate handler,
        Action<string, string> log, CancellationToken cancellationToken)
    {
        var listener = new GatewayFrontDoorListener();
        var address = binding.ListenAddress ?? binding.Origin.Address;
        IPAddress[] addresses = address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || IPAddress.IsLoopback(address)
            ? [address] : [address, IPAddress.Loopback];
        foreach (var each in addresses)
        {
            var name = each.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{each}]:{port}" : $"{each}:{port}";
            var app = KestrelGatewayListenerFactory.Build(each, port, binding, handler);
            try
            {
                await app.StartAsync(cancellationToken).ConfigureAwait(false);
                listener.apps.Add((name, app));
            }
            catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
            {
                await app.DisposeAsync().ConfigureAwait(false);
                log(Martlet.Core.Logs.LogLevels.Warn, $"Front door: couldn't listen on {name} ({error.GetBaseException().Message}).");
            }
        }
        log(listener.apps.Count > 0 ? Martlet.Core.Logs.LogLevels.Info : Martlet.Core.Logs.LogLevels.Warn, listener.apps.Count > 0
            ? $"Front door open on {string.Join(" and ", listener.Listening)}: Martlet's web page and the OpenAI-compatible API only."
            : $"Front door: nothing listens on port {port}; choose another port in Martlet (Share › Front door).");
        return listener;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, app) in apps)
        {
            try { await app.StopAsync().ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
            await app.DisposeAsync().ConfigureAwait(false);
        }
        apps.Clear();
    }
}
