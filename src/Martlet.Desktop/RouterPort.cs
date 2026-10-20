using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Network;

namespace Martlet.Desktop;

/// <summary>
/// Opening a port on your router for this PC's host service (docs/NETWORK.md, "Opening a port on your router"): Martlet asks the
/// router over UPnP to forward one TCP port to the host's gateway, checks the router really has an internet address (not one
/// its provider shares, which no friend can reach), and keeps the mapping renewed while Martlet runs. Only when the owner turns
/// it on in Outside access.
/// </summary>
internal static class RouterPort
{
    internal const string FileName = RouterPortFile.FileName;
    internal const string Description = "Martlet host";
    /// <summary>How often Martlet renews the mapping while it runs (the lease is seven days).</summary>
    internal static readonly TimeSpan RenewEvery = TimeSpan.FromHours(6);

    internal static RouterPortState? Load(string dataDirectory) => RouterPortFile.Load(dataDirectory);

    internal static void Save(string dataDirectory, RouterPortState? state) => RouterPortFile.Save(dataDirectory, state);

    internal static HttpClient Http() => new(new SocketsHttpHandler
    {
        UseProxy = false, AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(5)
    }) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 256 * 1024 };

    /// <summary>Why a router address can't be reached from the internet, in plain words, or null when it can.</summary>
    internal static string? Unreachable(string externalAddress) => !IPAddress.TryParse(externalAddress, out var address) ? "the router gave no usable internet address"
        : RouterPorts.Kind(address) switch
        {
            RouterAddressKind.SharedByProvider => $"your router's internet address ({externalAddress}) is shared by your internet provider with " +
                "other homes (carrier-grade NAT), so nobody outside can reach a port opened on it",
            RouterAddressKind.Private => $"your router's internet address ({externalAddress}) is a private one: another router is in front of it, " +
                "so a port opened here doesn't reach the internet",
            RouterAddressKind.Unknown => $"your router's internet address ({externalAddress}) isn't one the internet can reach",
            _ => null
        };

    /// <summary>Asks the router to forward a port to <paramref name="origin"/>'s gateway (this PC's host service): finds the
    /// router, checks its internet address, picks the gateway's own port number when it is free there (else the next free one
    /// up to ten above) and opens it. Throws <see cref="InvalidOperationException"/> with what to do when it can't.</summary>
    internal static async Task<RouterPortState> OpenAsync(string hostId, string origin, CancellationToken token)
    {
        using var http = Http();
        var gateway = await RouterPorts.DiscoverAsync(http, TimeSpan.FromSeconds(4), token)
            ?? throw new InvalidOperationException("Martlet found no router that opens ports on request (UPnP). Turn on UPnP in your router's " +
                $"settings and try again, or forward a port to {new Uri(origin).Authority} by hand in the router, or use Tailscale.");
        return await OpenAsync(http, gateway, hostId, origin, token);
    }

    internal static async Task<RouterPortState> OpenAsync(HttpClient http, RouterGateway gateway, string hostId, string origin, CancellationToken token,
        int? preferredPort = null)
    {
        var external = await RouterPorts.ExternalAddressAsync(http, gateway, token);
        if (Unreachable(external) is { } why)
            throw new InvalidOperationException($"Opening a port won't help: {why}. Use Tailscale (or another overlay network) to share over the internet instead.");
        var uri = new Uri(origin);
        var internalPort = uri.Port;
        // The router forwards to the address it sees this PC at (strict routers allow nothing else); Docker publishes the gateway on all of them.
        var client = IPAddress.TryParse(uri.Host, out var home) && home.ToString() == gateway.LocalAddress ? uri.Host : gateway.LocalAddress;
        int? chosen = null;
        foreach (var candidate in new[] { preferredPort ?? internalPort }.Concat(Enumerable.Range(internalPort, 11)).Distinct())
        {
            var existing = await RouterPorts.MappingAsync(http, gateway, candidate, token);
            if (existing is null || existing.InternalClient == client && existing.InternalPort == internalPort) { chosen = candidate; break; }
        }
        if (chosen is not { } port)
            throw new InvalidOperationException($"Other devices already use ports {internalPort} to {internalPort + 10} on your router. Free one in the " +
                "router's settings, or forward a port by hand.");
        await RouterPorts.AddAsync(http, gateway, port, client, internalPort, Description, token);
        var now = DateTimeOffset.Now;
        return new()
        {
            HostId = hostId, ExternalAddress = external, ExternalPort = port, InternalClient = client, InternalPort = internalPort,
            ControlUrl = gateway.ControlUrl.AbsoluteUri, ServiceType = gateway.ServiceType, RouterName = gateway.Name, OpenedAt = now, RenewedAt = now,
            AnsweredFromInside = await AnswersAsync(external, port, token)
        };
    }

    /// <summary>Renews the mapping (the router may have restarted or the lease run out) and reads the internet address again,
    /// at the router Martlet used last time, else the one it finds now.</summary>
    internal static async Task<RouterPortState> RenewAsync(RouterPortState state, string origin, CancellationToken token)
    {
        using var http = Http();
        var control = new Uri(state.ControlUrl);
        // This PC may have a new home address since (DHCP): forward to the address it has now, and drop the old forward.
        var local = IPAddress.TryParse(control.Host, out var router) ? RouterPorts.LocalAddressFacing(router) : state.InternalClient;
        var known = new RouterGateway(control, state.ServiceType, local, state.RouterName);
        if (local != state.InternalClient)
        {
            try { await RouterPorts.RemoveAsync(http, known, state.ExternalPort, token); }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or RouterPortException && !token.IsCancellationRequested) { }
        }
        try { return await OpenAsync(http, known, state.HostId, origin, token, state.ExternalPort) with { OpenedAt = state.OpenedAt }; }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or RouterPortException && !token.IsCancellationRequested)
        {
            var found = await RouterPorts.DiscoverAsync(http, TimeSpan.FromSeconds(4), token)
                ?? throw new InvalidOperationException("Your router no longer answers UPnP requests; the port may be closed. " + error.Message);
            return await OpenAsync(http, found, state.HostId, origin, token, state.ExternalPort) with { OpenedAt = state.OpenedAt };
        }
    }

    internal static async Task CloseAsync(RouterPortState state, CancellationToken token)
    {
        using var http = Http();
        await RouterPorts.RemoveAsync(http, new RouterGateway(new Uri(state.ControlUrl), state.ServiceType, state.InternalClient, state.RouterName),
            state.ExternalPort, token);
    }

    /// <summary>Whether something answers on the router's internet address and port from here (a TCP connection within three
    /// seconds). Many routers don't loop back, so false doesn't mean a friend can't reach it.</summary>
    internal static async Task<bool> AnswersAsync(string address, int port, CancellationToken token)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await socket.ConnectAsync(IPAddress.Parse(address), port, wait.Token);
            return true;
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException && !token.IsCancellationRequested) { return false; }
    }

    /// <summary>The outside addresses with the router's old address for this port replaced by the new one (or added).</summary>
    internal static IReadOnlyList<string> WithAddress(IReadOnlyList<string> addresses, string? previous, string next) =>
        addresses.Where(a => a != previous && a != next).Prepend(next).Take(NetworkRoster.MaximumAddresses).ToArray();
}
