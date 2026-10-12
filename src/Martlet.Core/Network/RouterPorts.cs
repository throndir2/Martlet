using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace Martlet.Core.Network;

/// <summary>A router that opens ports on request (UPnP Internet Gateway Device): where to send requests
/// (<see cref="ControlUrl"/>, <see cref="ServiceType"/>), and this computer's address on the router's side
/// (<see cref="LocalAddress"/>), which a port is opened to.</summary>
public sealed record RouterGateway(Uri ControlUrl, string ServiceType, string LocalAddress, string? Name);

/// <summary>A port the router forwards: from <see cref="ExternalPort"/> on its internet address to
/// <see cref="InternalClient"/>:<see cref="InternalPort"/>.</summary>
public sealed record RouterPortMapping(int ExternalPort, string InternalClient, int InternalPort, string? Description);

/// <summary>A router's answer that refused a request: the UPnP error code (718 another mapping uses the port, 725 only
/// permanent mappings, 606 not allowed) and its text.</summary>
public sealed class RouterPortException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>What kind of address a router says it has on the internet side.</summary>
public enum RouterAddressKind { Public, SharedByProvider, Private, Unknown }

/// <summary>
/// Opens a port on the home router so a friend outside your home can reach a host (docs/NETWORK.md, "Opening a port on your
/// router"). Uses UPnP IGD (Internet Gateway Device), which most home routers support when "UPnP" is on in their settings:
/// SSDP discovery on the home network (239.255.255.250:1900), the router's device description, then SOAP requests to its
/// WANIPConnection or WANPPPConnection service. Nothing here runs unless the owner asks for it.
/// </summary>
public static class RouterPorts
{
    private static readonly IPEndPoint Multicast = new(IPAddress.Parse("239.255.255.250"), 1900);
    private static readonly string[] Services =
    [
        "urn:schemas-upnp-org:service:WANIPConnection:2", "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1"
    ];
    private const string Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string Control = "urn:schemas-upnp-org:control-1-0";
    /// <summary>The longest lease UPnP IGD version 2 routers take (seven days); Martlet renews well before it ends.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromDays(7);

    /// <summary>Finds the router on the home network: sends SSDP searches and reads the first router's description that offers
    /// a connection service. Null when no router answers within <paramref name="timeout"/> (UPnP is off, or the network blocks
    /// it).</summary>
    public static async Task<RouterGateway?> DiscoverAsync(HttpClient http, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        foreach (var target in new[] { "urn:schemas-upnp-org:device:InternetGatewayDevice:1", "urn:schemas-upnp-org:device:InternetGatewayDevice:2" }
                     .Concat(Services))
        {
            var search = Encoding.ASCII.GetBytes($"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {target}\r\n\r\n");
            await udp.SendAsync(search, Multicast, cancellationToken).ConfigureAwait(false);
        }
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(timeout);
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            while (true)
            {
                var answer = await udp.ReceiveAsync(wait.Token).ConfigureAwait(false);
                if (Location(Encoding.ASCII.GetString(answer.Buffer)) is not { } location || !tried.Add(location.AbsoluteUri)) continue;
                // Only a device on the home network that answered from the address its description is on.
                if (!IPAddress.TryParse(location.Host, out var host) || !host.Equals(answer.RemoteEndPoint.Address) || !IsHomeNetwork(host)) continue;
                try
                {
                    if (await DescribeAsync(http, location, LocalAddressFacing(host), wait.Token).ConfigureAwait(false) is { } gateway) return gateway;
                }
                catch (Exception error) when (error is HttpRequestException or System.Xml.XmlException or FormatException or TaskCanceledException
                    && !cancellationToken.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    /// <summary>The LOCATION header of an SSDP answer, when it is an http URL.</summary>
    public static Uri? Location(string answer)
    {
        foreach (var line in answer.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || !line[..colon].Trim().Equals("LOCATION", StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.TryCreate(line[(colon + 1)..].Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp ? uri : null;
        }
        return null;
    }

    /// <summary>Reads a router's device description at <paramref name="location"/> and returns its connection service, or null
    /// when it has none.</summary>
    public static async Task<RouterGateway?> DescribeAsync(HttpClient http, Uri location, string localAddress, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(location, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Parse(text, location, localAddress);
    }

    /// <summary>The connection service in a device description (searched in every nested device), with its control URL made
    /// absolute against URLBase or the description's own URL.</summary>
    public static RouterGateway? Parse(string description, Uri location, string localAddress)
    {
        if (description.Length > 256 * 1024) return null;
        var document = Xml(description);
        XNamespace ns = document.Root?.Name.Namespace ?? XNamespace.None;
        var baseText = document.Root?.Element(ns + "URLBase")?.Value.Trim();
        var root = Uri.TryCreate(baseText, UriKind.Absolute, out var given) ? given : location;
        var name = document.Descendants(ns + "friendlyName").FirstOrDefault()?.Value.Trim();
        foreach (var type in Services)
        {
            var service = document.Descendants(ns + "service").FirstOrDefault(s => s.Element(ns + "serviceType")?.Value.Trim() == type);
            var control = service?.Element(ns + "controlURL")?.Value.Trim();
            if (control is null || !Uri.TryCreate(root, control, out var url) || url.Scheme != Uri.UriSchemeHttp) continue;
            // The router answers on the home network only (never this computer itself or the internet).
            if (!IPAddress.TryParse(url.Host, out var address) || !IsHomeNetwork(address)) continue;
            return new(url, type, localAddress, name is { Length: > 0 } ? new string(name.Take(64).ToArray()) : null);
        }
        return null;
    }

    /// <summary>A private home network address a router answers on: 10/8, 172.16/12, 192.168/16 or 100.64/10 (some providers'
    /// routers); never loopback, link-local or an internet address.</summary>
    public static bool IsHomeNetwork(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 100 && b[1] is >= 64 and <= 127;
    }

    /// <summary>This computer's IPv4 address on the network that reaches <paramref name="router"/> (no packet is sent).</summary>
    public static string LocalAddressFacing(IPAddress router)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect(router, 1900);
        return ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
    }

    /// <summary>The router's address on the internet side.</summary>
    public static async Task<string> ExternalAddressAsync(HttpClient http, RouterGateway gateway, CancellationToken cancellationToken)
    {
        var answer = await CallAsync(http, gateway, "GetExternalIPAddress", [], cancellationToken).ConfigureAwait(false);
        var address = answer.GetValueOrDefault("NewExternalIPAddress")?.Trim();
        if (address is null || !IPAddress.TryParse(address, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
            throw new RouterPortException(0, "The router didn't say its internet address.");
        return parsed.ToString();
    }

    /// <summary>The mapping of <paramref name="externalPort"/> (TCP), or null when the router forwards nothing there.</summary>
    public static async Task<RouterPortMapping?> MappingAsync(HttpClient http, RouterGateway gateway, int externalPort, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await CallAsync(http, gateway, "GetSpecificPortMappingEntry",
                [("NewRemoteHost", ""), ("NewExternalPort", externalPort.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("NewProtocol", "TCP")],
                cancellationToken).ConfigureAwait(false);
            return int.TryParse(answer.GetValueOrDefault("NewInternalPort"), out var port) && answer.GetValueOrDefault("NewInternalClient") is { Length: > 0 } client
                ? new(externalPort, client.Trim(), port, answer.GetValueOrDefault("NewPortMappingDescription"))
                : null;
        }
        catch (RouterPortException error) when (error.Code is 714 or 713 or 402) { return null; }
    }

    /// <summary>Asks the router to forward TCP <paramref name="externalPort"/> to <paramref name="internalClient"/>:<paramref
    /// name="internalPort"/> for <see cref="Lease"/>; a router that takes only permanent mappings (725) gets one.</summary>
    public static async Task AddAsync(HttpClient http, RouterGateway gateway, int externalPort, string internalClient, int internalPort, string description,
        CancellationToken cancellationToken)
    {
        IEnumerable<(string, string)> Arguments(TimeSpan lease) =>
        [
            ("NewRemoteHost", ""), ("NewExternalPort", externalPort.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("NewProtocol", "TCP"),
            ("NewInternalPort", internalPort.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("NewInternalClient", internalClient),
            ("NewEnabled", "1"), ("NewPortMappingDescription", description),
            ("NewLeaseDuration", ((long)lease.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture))
        ];
        try { await CallAsync(http, gateway, "AddPortMapping", Arguments(Lease), cancellationToken).ConfigureAwait(false); }
        catch (RouterPortException error) when (error.Code == 725)
        {
            await CallAsync(http, gateway, "AddPortMapping", Arguments(TimeSpan.Zero), cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task RemoveAsync(HttpClient http, RouterGateway gateway, int externalPort, CancellationToken cancellationToken)
    {
        try
        {
            await CallAsync(http, gateway, "DeletePortMapping",
                [("NewRemoteHost", ""), ("NewExternalPort", externalPort.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("NewProtocol", "TCP")],
                cancellationToken).ConfigureAwait(false);
        }
        catch (RouterPortException error) when (error.Code == 714) { }
    }

    /// <summary>What an internet-side address means: <see cref="RouterAddressKind.SharedByProvider"/> (100.64.0.0/10, carrier-grade
    /// NAT: the internet provider shares one address among many homes, so a port opened here is not reachable) and
    /// <see cref="RouterAddressKind.Private"/> (another router in front of this one) can't be reached from the internet.</summary>
    public static RouterAddressKind Kind(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return RouterAddressKind.Unknown;
        var b = address.GetAddressBytes();
        if (b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254 || b[0] == 127)
            return RouterAddressKind.Private;
        if (b[0] == 100 && b[1] is >= 64 and <= 127) return RouterAddressKind.SharedByProvider;
        if (b[0] == 0 || b[0] >= 224) return RouterAddressKind.Unknown;
        return RouterAddressKind.Public;
    }

    /// <summary>Parses a router's XML without DTDs or external entities (a router's answer is never trusted).</summary>
    private static XDocument Xml(string text)
    {
        using var reader = System.Xml.XmlReader.Create(new StringReader(text),
            new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 256 * 1024 });
        return XDocument.Load(reader);
    }

    /// <summary>The SOAP request body for <paramref name="action"/>.</summary>
    public static string Envelope(string serviceType, string action, IEnumerable<(string Name, string Value)> arguments)
    {
        var body = new StringBuilder();
        body.Append("<?xml version=\"1.0\"?>")
            .Append("<s:Envelope xmlns:s=\"").Append(Soap).Append("\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>")
            .Append("<u:").Append(action).Append(" xmlns:u=\"").Append(serviceType).Append("\">");
        foreach (var (name, value) in arguments) body.Append('<').Append(name).Append('>').Append(SecurityElement.Escape(value)).Append("</").Append(name).Append('>');
        body.Append("</u:").Append(action).Append("></s:Body></s:Envelope>");
        return body.ToString();
    }

    private static async Task<Dictionary<string, string>> CallAsync(HttpClient http, RouterGateway gateway, string action,
        IEnumerable<(string Name, string Value)> arguments, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, gateway.ControlUrl)
        {
            Content = new StringContent(Envelope(gateway.ServiceType, action, arguments), Encoding.UTF8)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{gateway.ServiceType}#{action}\"");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return Answer(text, (int)response.StatusCode);
    }

    /// <summary>The values of a SOAP answer, or the <see cref="RouterPortException"/> its UPnP error describes.</summary>
    public static Dictionary<string, string> Answer(string text, int status)
    {
        XDocument document;
        try { document = Xml(text.Length > 64 * 1024 ? "" : text); }
        catch (System.Xml.XmlException) { throw new RouterPortException(status, $"The router answered {status} without a readable answer."); }
        var error = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "UPnPError");
        if (error is not null || status is < 200 or > 299)
        {
            var code = int.TryParse(error?.Elements().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value, out var number) ? number : status;
            var description = error?.Elements().FirstOrDefault(e => e.Name.LocalName == "errorDescription")?.Value;
            throw new RouterPortException(code, code switch
            {
                718 => "Another device already uses that port on the router.",
                725 => "The router takes only permanent port mappings.",
                606 or 401 => "The router doesn't allow Martlet to open ports (UPnP port mapping is off or limited in its settings).",
                _ => $"The router refused the request ({code}{(description is { Length: > 0 } ? ": " + description : "")})."
            });
        }
        var body = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Body")?.Elements().FirstOrDefault();
        return body?.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value, StringComparer.Ordinal) ?? [];
    }
}
