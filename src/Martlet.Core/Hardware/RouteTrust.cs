using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Martlet.Core.Network;

namespace Martlet.Core.Hardware;

/// <summary>How this computer reaches an address, for the fallback check of a reinstalled host without a TPM: only a route
/// the owner's own network vouches for counts.</summary>
public enum RouteTrust
{
    /// <summary>On the same home subnet as one of this computer's network adapters.</summary>
    Home,
    /// <summary>A Tailscale address (100.64.0.0/10) while this computer is on Tailscale too: only devices in the owner's
    /// tailnet have one.</summary>
    Tailnet,
    /// <summary>Anything else (the internet, another subnet, a VPN Martlet doesn't know).</summary>
    Other,
}

public static class RouteTrusts
{
    /// <summary>The route to <paramref name="address"/> from this computer's adapters (<paramref name="adapters"/>: address
    /// and prefix length; null reads this computer's).</summary>
    public static RouteTrust Of(IPAddress address, IReadOnlyList<(IPAddress Address, int Prefix)>? adapters = null)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return RouteTrust.Other;
        adapters ??= Adapters();
        if (IsTailscale(address))
            return adapters.Any(a => IsTailscale(a.Address)) ? RouteTrust.Tailnet : RouteTrust.Other;
        if (!HostFind.IsPrivate(address)) return RouteTrust.Other;
        return adapters.Any(a => !IsTailscale(a.Address) && a.Prefix is >= 8 and <= 32 && Same(a.Address, address, a.Prefix))
            ? RouteTrust.Home : RouteTrust.Other;
    }

    public static bool IsTailscale(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 && b[0] == 100 && (b[1] & 0xC0) == 64;
    }

    private static bool Same(IPAddress a, IPAddress b, int prefix)
    {
        var x = BitConverter.ToUInt32(a.GetAddressBytes().Reverse().ToArray());
        var y = BitConverter.ToUInt32(b.GetAddressBytes().Reverse().ToArray());
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return (x & mask) == (y & mask);
    }

    private static IReadOnlyList<(IPAddress, int)> Adapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(u => (u.Address, u.PrefixLength)).ToArray();
        }
        catch (NetworkInformationException) { return []; }
    }
}
