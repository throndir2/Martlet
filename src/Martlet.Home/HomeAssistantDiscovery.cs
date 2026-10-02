using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Martlet.Home;

/// <summary>A Home Assistant that answered on the local network.</summary>
public sealed record FoundHomeAssistant(string Name, Uri Address, string? Version, string? Uuid);

/// <summary>Finds Home Assistant on the local network the way its own apps do: one multicast DNS question for its service
/// type <c>_home-assistant._tcp.local</c> on each local network, answered by every Home Assistant that hears it. Asked only
/// when the user presses Find; nothing is scanned, and the answers are just listed for the user to pick.</summary>
public static class HomeAssistantDiscovery
{
    public const string ServiceType = "_home-assistant._tcp.local";
    private static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);
    private const int MaximumPacket = 9000;

    public static async Task<IReadOnlyList<FoundHomeAssistant>> FindAsync(TimeSpan listen, CancellationToken cancellationToken)
    {
        var query = Query();
        var sockets = new List<Socket>();
        try
        {
            foreach (var address in LocalAddresses())
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                try
                {
                    socket.Bind(new IPEndPoint(address, 0));
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                    await socket.SendToAsync(query, SocketFlags.None, Group, cancellationToken).ConfigureAwait(false);
                    sockets.Add(socket);
                }
                catch (SocketException) { socket.Dispose(); }
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(listen);
            var answers = new Answers();
            await Task.WhenAll(sockets.Select(socket => ReceiveAsync(socket, answers, timeout.Token))).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return answers.Results();
        }
        finally
        {
            foreach (var socket in sockets) socket.Dispose();
        }
    }

    private static async Task ReceiveAsync(Socket socket, Answers answers, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaximumPacket];
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken)
                    .ConfigureAwait(false);
                answers.Read(buffer.AsSpan(0, received.ReceivedBytes));
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }
        }
    }

    private static IEnumerable<IPAddress> LocalAddresses()
    {
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { yield break; }
        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || !nic.SupportsMulticast ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) continue;
                var bytes = address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue;
                yield return address;
            }
        }
    }

    // One question, PTR for the service type, with the "unicast response" bit set so answers come straight back here.
    internal static byte[] Query()
    {
        var packet = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in ServiceType.Split('.'))
        {
            packet.Add((byte)label.Length);
            packet.AddRange(Encoding.ASCII.GetBytes(label));
        }
        packet.AddRange(new byte[] { 0, 0, 12, 0x80, 1 });
        return [.. packet];
    }

    /// <summary>Collects the records answers carry (PTR, SRV, TXT, A) and joins them into one entry per instance.</summary>
    internal sealed class Answers
    {
        private readonly Lock gate = new();
        private readonly HashSet<string> instances = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string Target, int Port)> services = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, string>> texts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IPAddress> hosts = new(StringComparer.OrdinalIgnoreCase);

        internal void Read(ReadOnlySpan<byte> packet)
        {
            try { Parse(packet); }
            catch (Exception error) when (error is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException) { }
        }

        private void Parse(ReadOnlySpan<byte> packet)
        {
            if (packet.Length < 12 || (packet[2] & 0x80) == 0) return;
            var questions = U16(packet, 4);
            var records = U16(packet, 6) + U16(packet, 8) + U16(packet, 10);
            var offset = 12;
            for (var i = 0; i < questions; i++)
            {
                Name(packet, ref offset);
                offset += 4;
            }
            for (var i = 0; i < records && i < 256; i++)
            {
                var owner = Name(packet, ref offset);
                var type = U16(packet, offset);
                var length = U16(packet, offset + 8);
                var start = offset + 10;
                if (start + length > packet.Length) throw new InvalidDataException();
                var data = start;
                lock (gate)
                    switch (type)
                    {
                        case 12 when owner.Equals(ServiceType, StringComparison.OrdinalIgnoreCase):
                            instances.Add(Name(packet, ref data));
                            break;
                        case 33 when length >= 7:
                            var port = U16(packet, start + 4);
                            data = start + 6;
                            services[owner] = (Name(packet, ref data), port);
                            break;
                        case 16:
                            texts[owner] = Text(packet.Slice(start, length));
                            break;
                        case 1 when length == 4:
                            hosts[owner] = new IPAddress(packet.Slice(start, 4));
                            break;
                    }
                offset = start + length;
            }
        }

        internal IReadOnlyList<FoundHomeAssistant> Results()
        {
            lock (gate)
            {
                var found = new List<FoundHomeAssistant>();
                foreach (var instance in instances.Union(services.Keys.Where(k => k.EndsWith("." + ServiceType, StringComparison.OrdinalIgnoreCase)),
                    StringComparer.OrdinalIgnoreCase))
                {
                    var text = texts.GetValueOrDefault(instance) ?? new Dictionary<string, string>();
                    Uri? address = null;
                    if (Url(text.GetValueOrDefault("internal_url")) is { Scheme: "https" } secure) address = secure;
                    else if (services.TryGetValue(instance, out var service) && hosts.TryGetValue(service.Target, out var ip))
                        address = new Uri($"http://{ip}:{service.Port}/");
                    else address = Url(text.GetValueOrDefault("internal_url")) ?? Url(text.GetValueOrDefault("base_url"));
                    if (address is null) continue;
                    var label = instance.EndsWith("." + ServiceType, StringComparison.OrdinalIgnoreCase)
                        ? instance[..^(ServiceType.Length + 1)] : instance;
                    var name = HomeAssistantClient.Clean(text.GetValueOrDefault("location_name") ?? label, 64);
                    var uuid = text.GetValueOrDefault("uuid") is { Length: > 0 and <= 64 } id ? id : null;
                    var version = HomeAssistantClient.Clean(text.GetValueOrDefault("version"), 32);
                    found.Add(new(name.Length > 0 ? name : "Home Assistant", address, version.Length > 0 ? version : null, uuid));
                }
                return found.DistinctBy(f => f.Uuid ?? f.Address.AbsoluteUri).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            }
        }

        private static Uri? Url(string? text) =>
            Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0 && uri.UserInfo.Length == 0
                ? new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/") : null;

        private static Dictionary<string, string> Text(ReadOnlySpan<byte> data)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var offset = 0;
            while (offset < data.Length)
            {
                var length = data[offset++];
                if (offset + length > data.Length) break;
                var entry = Encoding.UTF8.GetString(data.Slice(offset, length));
                offset += length;
                var equals = entry.IndexOf('=');
                if (equals > 0) values.TryAdd(entry[..equals], entry[(equals + 1)..]);
            }
            return values;
        }

        private static int U16(ReadOnlySpan<byte> packet, int offset) => packet[offset] << 8 | packet[offset + 1];

        // A domain name, following compression pointers; offset moves past the name as it appears at offset.
        private static string Name(ReadOnlySpan<byte> packet, ref int offset)
        {
            var labels = new List<string>();
            var position = offset;
            var jumped = false;
            for (var steps = 0; steps < 128; steps++)
            {
                var length = packet[position];
                if (length == 0)
                {
                    if (!jumped) offset = position + 1;
                    return string.Join('.', labels);
                }
                if ((length & 0xC0) == 0xC0)
                {
                    var target = (length & 0x3F) << 8 | packet[position + 1];
                    if (!jumped) offset = position + 2;
                    jumped = true;
                    position = target;
                    continue;
                }
                if (length > 63) throw new InvalidDataException();
                labels.Add(Encoding.UTF8.GetString(packet.Slice(position + 1, length)));
                position += length + 1;
            }
            throw new InvalidDataException();
        }
    }
}
