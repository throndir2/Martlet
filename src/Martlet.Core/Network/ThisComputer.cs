using System.Net;
using System.Net.Sockets;

namespace Martlet.Core.Network;

/// <summary>The name this computer gives itself, as people see it: its host name in the case its owner set ("Miko", "Nahida"),
/// the same on Windows, Linux and macOS. On Windows that is the DNS host name, not the NetBIOS name
/// (<see cref="Environment.MachineName"/>), which Windows always writes in capitals and cuts at 15 characters. IDs made from the
/// computer's name (device IDs, host IDs) keep the NetBIOS name, so they never change.</summary>
public static class ThisComputer
{
    private static readonly Lazy<string> name = new(() => FromHostName(ReadHostName()) ?? Environment.MachineName);

    public static string Name => name.Value;

    /// <summary>The computer name in a host name: the part before the first dot, when it is a name a host may report
    /// (<see cref="HostIds.CleanComputerName"/>); else null.</summary>
    public static string? FromHostName(string? hostName) => HostIds.CleanComputerName(hostName?.Split('.')[0]);

    private static string? ReadHostName()
    {
        try { return Dns.GetHostName(); }
        catch (SocketException) { return null; }
    }
}
