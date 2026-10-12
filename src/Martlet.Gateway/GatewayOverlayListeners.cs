using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Martlet.Core.Logs;
using Martlet.Core.Network;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway;

/// <summary>
/// Listeners for a host reached from outside home through an overlay network (docs/NETWORK.md, "Reaching your network from
/// outside home"). The gateway listens on its home address only, but an overlay such as Tailscale reaches the computer on
/// another of its own addresses. While the host has outside addresses (set on the host or in its roster entry), this also
/// listens on each of the computer's Tailscale addresses (100.64.0.0/10 and fd7a:115c:a1e0::/48, on the home port) and on
/// each of its addresses that an outside address names (on that port), with the same TLS key and the same request handling.
/// It reads the computer's addresses every 10 seconds and when they change, so an overlay that starts after the host, stops
/// or gets another address is followed. Connections through it come from outside home for the guard.
/// </summary>
internal sealed class GatewayOverlayListeners : IAsyncDisposable
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly GatewayTlsBinding binding;
    private readonly RequestDelegate application;
    private readonly Func<IReadOnlyList<string>> outsideAddresses;
    private readonly Func<IEnumerable<IPAddress>> localAddresses;
    private readonly Action<string, string, string?> log;
    private readonly Dictionary<IPEndPoint, WebApplication> listeners = [];
    private readonly HashSet<IPEndPoint> failed = [];
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly Task loop;
    private volatile IReadOnlyList<string> listening = [];

    internal GatewayOverlayListeners(GatewayTlsBinding binding, RequestDelegate application, Func<IReadOnlyList<string>> outsideAddresses,
        Func<IEnumerable<IPAddress>> localAddresses, Action<string, string, string?> log)
    {
        this.binding = binding;
        this.application = application;
        this.outsideAddresses = outsideAddresses;
        this.localAddresses = localAddresses;
        this.log = log;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        loop = Task.Run(RunAsync);
    }

    /// <summary>The addresses ("ip:port", "[ipv6]:port") this listens on now, besides the home origin.</summary>
    internal IReadOnlyList<string> Listening => listening;

    /// <summary>Checks again now (the host's outside addresses or the computer's addresses changed).</summary>
    internal void Wake()
    {
        try { wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private void OnAddressChanged(object? sender, EventArgs e) => Wake();

    private async Task RunAsync()
    {
        var token = stopping.Token;
        while (!token.IsCancellationRequested)
        {
            try { await RefreshAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                log(LogLevels.Warn, $"Couldn't check this host's overlay network addresses: {error.GetType().Name}.", "overlay-check");
            }
            try { await wake.WaitAsync(Interval, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RefreshAsync(CancellationToken token)
    {
        var outside = outsideAddresses();
        IReadOnlyList<IPEndPoint> wanted = outside.Count == 0 ? [] : Select(binding.Origin, localAddresses(), outside);
        foreach (var endpoint in listeners.Keys.Where(e => !wanted.Contains(e)).ToArray())
        {
            var app = listeners[endpoint];
            listeners.Remove(endpoint);
            await StopAsync(app).ConfigureAwait(false);
            log(LogLevels.Info, $"Stopped listening on {endpoint} (overlay network): " +
                (outside.Count == 0 ? "this host has no outside addresses now." : "this computer doesn't have that address now."), null);
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
                log(LogLevels.Info, $"Listening on {endpoint} too (overlay network), so your computers reach this host there from outside home.", null);
            }
            catch (Exception error)
            {
                await app.DisposeAsync().ConfigureAwait(false);
                if (error is OperationCanceledException && token.IsCancellationRequested) throw;
                if (failed.Add(endpoint))
                    log(LogLevels.Warn, $"Couldn't listen on {endpoint} (overlay network): {error.GetType().Name}. Martlet tries again every " +
                        $"{Interval.TotalSeconds:0} seconds.", "overlay-failed|" + endpoint);
            }
        }
        listening = listeners.Keys.Select(e => e.ToString()).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Where to listen besides the home origin: each of <paramref name="local"/> that an outside address names as an
    /// IP (on that address's port), or that is a Tailscale address (on the origin's port). Never the origin's own address,
    /// loopback or IPv6 link-local.</summary>
    internal static IReadOnlyList<IPEndPoint> Select(GatewayOrigin origin, IEnumerable<IPAddress> local, IEnumerable<string> outside)
    {
        var named = new Dictionary<IPAddress, int>();
        foreach (var text in outside)
            if (NetworkRoster.NormalizeAddress(text) is { } normalized && IPEndPoint.TryParse(normalized, out var endpoint))
                named.TryAdd(Plain(endpoint.Address), endpoint.Port);
        var result = new List<IPEndPoint>();
        foreach (var address in local.Select(Plain).Distinct())
        {
            if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.Equals(origin.Address)) continue;
            if (named.TryGetValue(address, out var port)) result.Add(new(address, port));
            else if (IsTailscale(address)) result.Add(new(address, origin.Port));
        }
        return result;
    }

    /// <summary>Tailscale's addresses: 100.64.0.0/10 (shared address space) and fd7a:115c:a1e0::/48.</summary>
    internal static bool IsTailscale(IPAddress address)
    {
        var bytes = Plain(address).GetAddressBytes();
        return bytes.Length == 4
            ? bytes[0] == 100 && (bytes[1] & 0xC0) == 64
            : bytes is [0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0, ..];
    }

    /// <summary>The addresses of this computer's network interfaces that are up (a tunnel such as tailscale0 reports Unknown).</summary>
    internal static IEnumerable<IPAddress> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus is OperationalStatus.Up or OperationalStatus.Unknown)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6).ToArray();
        }
        catch (NetworkInformationException) { return []; }
    }

    private static IPAddress Plain(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();
        return address.AddressFamily != AddressFamily.InterNetworkV6 || address.IsIPv6LinkLocal || address.ScopeId == 0
            ? address : new IPAddress(address.GetAddressBytes());
    }

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
