using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Martlet.Core.Logs;
using Martlet.Core.Network;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway;

/// <summary>
/// Keeps a host that listens on its exact home address reachable after its computer got another address on the home network
/// (a restart, a new DHCP lease; docs/NETWORK.md, "When a computer gets a new address"). While the computer doesn't have the
/// home origin's address, this listens on its current home addresses (<see cref="HostFind.HomeAddresses"/>: private IPv4 on an
/// adapter with a default gateway) on the origin's port, with the same TLS key and the same request handling. Desktops keep
/// the paired origin as the host's name and only dial the new address, so pairings, the certificate and the service approval
/// stay as they are. When the origin's address comes back, the extra listeners stop. When the main listener couldn't take the
/// origin at start (the address was missing), this listens on the origin itself once it is there.
/// It reads the computer's addresses every 10 seconds and when they change.
/// </summary>
internal sealed class GatewayHomeListeners : IAsyncDisposable
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly GatewayTlsBinding binding;
    private readonly RequestDelegate application;
    private readonly bool originHeld;
    private readonly Func<IEnumerable<IPAddress>> localAddresses;
    private readonly Func<IEnumerable<IPAddress>> homeAddresses;
    private readonly Action<string, string, string?> log;
    private readonly Dictionary<IPEndPoint, WebApplication> listeners = [];
    private readonly HashSet<IPEndPoint> failed = [];
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly Task loop;
    private volatile IReadOnlyList<string> listening = [];

    /// <param name="originHeld">Whether the main listener took the origin's address at start.</param>
    internal GatewayHomeListeners(GatewayTlsBinding binding, RequestDelegate application, bool originHeld,
        Func<IEnumerable<IPAddress>> localAddresses, Func<IEnumerable<IPAddress>> homeAddresses, Action<string, string, string?> log)
    {
        this.binding = binding;
        this.application = application;
        this.originHeld = originHeld;
        this.localAddresses = localAddresses;
        this.homeAddresses = homeAddresses;
        this.log = log;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        loop = Task.Run(RunAsync);
    }

    /// <summary>The addresses ("ip:port") this listens on now besides the main listener.</summary>
    internal IReadOnlyList<string> Listening => listening;

    private void OnAddressChanged(object? sender, EventArgs e)
    {
        try { wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task RunAsync()
    {
        var token = stopping.Token;
        while (!token.IsCancellationRequested)
        {
            try { await RefreshAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                log(LogLevels.Warn, $"Couldn't check this host's home network address: {error.GetType().Name}.", "home-check");
            }
            try { await wake.WaitAsync(Interval, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RefreshAsync(CancellationToken token)
    {
        IReadOnlyList<IPEndPoint> wanted = Select(binding.Origin, originHeld, localAddresses(), homeAddresses());
        foreach (var endpoint in listeners.Keys.Where(e => !wanted.Contains(e)).ToArray())
        {
            var app = listeners[endpoint];
            listeners.Remove(endpoint);
            await StopAsync(app).ConfigureAwait(false);
            log(LogLevels.Info, $"Stopped listening on {endpoint}: this computer has its home address {binding.Origin.Address} again, " +
                "or not that address any more.", null);
        }
        failed.IntersectWith(wanted);
        foreach (var endpoint in wanted.Where(e => !listeners.ContainsKey(e)))
        {
            token.ThrowIfCancellationRequested();
            var app = KestrelGatewayListenerFactory.Build(endpoint.Address, endpoint.Port, binding, application);
            try
            {
                await app.StartAsync(token).ConfigureAwait(false);
                listeners[endpoint] = app;
                failed.Remove(endpoint);
                log(LogLevels.Info, endpoint.Address.Equals(binding.Origin.Address)
                    ? $"Listening on the home address {endpoint} now that this computer has it."
                    : $"This computer's home address is {endpoint.Address} now (it was {binding.Origin.Address}), so the host listens there " +
                      "too. Your computers find it there by themselves; pairings stay.", null);
            }
            catch (Exception error)
            {
                await app.DisposeAsync().ConfigureAwait(false);
                if (error is OperationCanceledException && token.IsCancellationRequested) throw;
                if (failed.Add(endpoint))
                    log(LogLevels.Warn, $"Couldn't listen on {endpoint} (home address): {error.GetType().Name}. Martlet tries again every " +
                        $"{Interval.TotalSeconds:0} seconds.", "home-failed|" + endpoint);
            }
        }
        listening = listeners.Keys.Select(e => e.ToString()).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Where to listen besides the main listener: nowhere while this computer has the origin's address and the main
    /// listener holds it; the origin when the computer has its address but the main listener couldn't take it at start;
    /// otherwise each of <paramref name="homes"/> (private IPv4, never loopback) on the origin's port.</summary>
    internal static IReadOnlyList<IPEndPoint> Select(GatewayOrigin origin, bool originHeld, IEnumerable<IPAddress> local, IEnumerable<IPAddress> homes)
    {
        if (local.Select(Plain).Contains(origin.Address)) return originHeld ? [] : [new(origin.Address, origin.Port)];
        return homes.Select(Plain).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && HostFind.IsPrivate(a))
            .Distinct().Select(a => new IPEndPoint(a, origin.Port)).ToArray();
    }

    private static IPAddress Plain(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static async Task StopAsync(WebApplication app)
    {
        try { await app.StopAsync().ConfigureAwait(false); }
        finally { await app.DisposeAsync().ConfigureAwait(false); }
    }

    public async ValueTask DisposeAsync()
    {
        if (stopping.IsCancellationRequested) return;
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        stopping.Cancel();
        try { await loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        foreach (var app in listeners.Values) await StopAsync(app).ConfigureAwait(false);
        listeners.Clear();
        listening = [];
        stopping.Dispose();
        wake.Dispose();
    }
}

/// <summary>The main listener of a host whose computer doesn't have the home origin's address at start: it holds nothing, and
/// <see cref="GatewayHomeListeners"/> listens where the computer is now.</summary>
internal sealed class GatewayAwayListenerHandle(GatewayOrigin origin) : GatewayListenerHandle
{
    public override GatewayOrigin Origin { get; } = origin;
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
