using System.Net;
using System.Text;
using Martlet.Core.Network;

namespace Martlet.Core.Tests;

/// <summary>Opening a port on the home router over UPnP (docs/NETWORK.md, "Opening a port on your router"), against a fake
/// router: discovery answers, the device description, SOAP requests and the router's errors.</summary>
public sealed class RouterPortsTests
{
    private const string Description = """
        <?xml version="1.0"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <device>
            <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>
            <friendlyName>Home router</friendlyName>
            <deviceList><device><deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType>
              <deviceList><device><deviceType>urn:schemas-upnp-org:device:WANConnectionDevice:1</deviceType>
                <serviceList>
                  <service><serviceType>urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1</serviceType><controlURL>/ctl/CmnIfCfg</controlURL></service>
                  <service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType><controlURL>/ctl/IPConn</controlURL></service>
                </serviceList>
              </device></deviceList>
            </device></deviceList>
          </device>
        </root>
        """;

    /// <summary>A router that keeps port mappings and answers SOAP like miniupnpd; <see cref="PermanentOnly"/> refuses leases.</summary>
    private sealed class FakeRouter : HttpMessageHandler
    {
        internal readonly Dictionary<int, (string Client, int Port, string Lease)> Mappings = [];
        internal string External = "203.0.113.7";
        internal bool PermanentOnly;
        internal readonly List<string> Actions = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var action = request.Headers.GetValues("SOAPAction").Single().Trim('"').Split('#')[1];
            Actions.Add(action);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            string Arg(string name) => body.Split("<" + name + ">")[1].Split("</" + name + ">")[0];
            return action switch
            {
                "GetExternalIPAddress" => Ok(action, $"<NewExternalIPAddress>{External}</NewExternalIPAddress>"),
                "GetSpecificPortMappingEntry" => Mappings.TryGetValue(int.Parse(Arg("NewExternalPort")), out var found)
                    ? Ok(action, $"<NewInternalPort>{found.Port}</NewInternalPort><NewInternalClient>{found.Client}</NewInternalClient><NewEnabled>1</NewEnabled>")
                    : Error(714, "NoSuchEntryInArray"),
                "AddPortMapping" when PermanentOnly && Arg("NewLeaseDuration") != "0" => Error(725, "OnlyPermanentLeasesSupported"),
                "AddPortMapping" when Mappings.TryGetValue(int.Parse(Arg("NewExternalPort")), out var taken) && taken.Client != Arg("NewInternalClient")
                    => Error(718, "ConflictInMappingEntry"),
                "AddPortMapping" => Map(),
                "DeletePortMapping" => Mappings.Remove(int.Parse(Arg("NewExternalPort"))) ? Ok(action, "") : Error(714, "NoSuchEntryInArray"),
                _ => Error(401, "Invalid Action")
            };

            HttpResponseMessage Map()
            {
                Mappings[int.Parse(Arg("NewExternalPort"))] = (Arg("NewInternalClient"), int.Parse(Arg("NewInternalPort")), Arg("NewLeaseDuration"));
                return Ok(action, "");
            }
        }

        private static HttpResponseMessage Ok(string action, string values) => new(HttpStatusCode.OK)
        {
            Content = new StringContent($"""<?xml version="1.0"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><u:{action}Response xmlns:u="urn:schemas-upnp-org:service:WANIPConnection:1">{values}</u:{action}Response></s:Body></s:Envelope>""", Encoding.UTF8, "text/xml")
        };

        private static HttpResponseMessage Error(int code, string text) => new(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent($"""<?xml version="1.0"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail><UPnPError xmlns="urn:schemas-upnp-org:control-1-0"><errorCode>{code}</errorCode><errorDescription>{text}</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>""", Encoding.UTF8, "text/xml")
        };
    }

    private static readonly RouterGateway Gateway = new(new Uri("http://192.168.1.1:5000/ctl/IPConn"), "urn:schemas-upnp-org:service:WANIPConnection:1",
        "192.168.1.10", "Home router");

    [Fact]
    public void Discovery_answers_and_descriptions_find_the_routers_connection_service()
    {
        Assert.Equal(new Uri("http://192.168.1.1:5000/rootDesc.xml"), RouterPorts.Location(
            "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=120\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\nLocation: http://192.168.1.1:5000/rootDesc.xml\r\n\r\n"));
        Assert.Null(RouterPorts.Location("HTTP/1.1 200 OK\r\nLOCATION: https://example.net/x\r\n\r\n"));
        var gateway = RouterPorts.Parse(Description, new Uri("http://192.168.1.1:5000/rootDesc.xml"), "192.168.1.10");
        Assert.NotNull(gateway);
        Assert.Equal((new Uri("http://192.168.1.1:5000/ctl/IPConn"), "urn:schemas-upnp-org:service:WANIPConnection:1", "Home router"),
            (gateway!.ControlUrl, gateway.ServiceType, gateway.Name));
        // A description that sends control requests to the internet, or to this computer itself, is not a home router's.
        Assert.Null(RouterPorts.Parse(Description.Replace("/ctl/IPConn", "http://203.0.113.9/ctl/IPConn"), new Uri("http://192.168.1.1:5000/rootDesc.xml"), "192.168.1.10"));
        Assert.Null(RouterPorts.Parse(Description.Replace("/ctl/IPConn", "http://127.0.0.1:5000/ctl/IPConn"), new Uri("http://192.168.1.1:5000/rootDesc.xml"), "192.168.1.10"));
        Assert.True(RouterPorts.IsHomeNetwork(IPAddress.Parse("192.168.1.1")));
        Assert.False(RouterPorts.IsHomeNetwork(IPAddress.Parse("169.254.1.1")));
        Assert.False(RouterPorts.IsHomeNetwork(IPAddress.Parse("8.8.8.8")));
        Assert.Throws<System.Xml.XmlException>(() => RouterPorts.Parse("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><root>&e;</root>",
            new Uri("http://192.168.1.1/"), "192.168.1.10"));
    }

    [Fact]
    public async Task Ports_open_with_a_lease_or_permanently_and_router_errors_say_what_happened()
    {
        var router = new FakeRouter();
        using var http = new HttpClient(router);
        Assert.Equal("203.0.113.7", await RouterPorts.ExternalAddressAsync(http, Gateway, CancellationToken.None));
        Assert.Null(await RouterPorts.MappingAsync(http, Gateway, 9443, CancellationToken.None));
        await RouterPorts.AddAsync(http, Gateway, 9443, "192.168.1.10", 9443, "Martlet host", CancellationToken.None);
        Assert.Equal(("192.168.1.10", 9443, "604800"), router.Mappings[9443]);
        Assert.Equal(new RouterPortMapping(9443, "192.168.1.10", 9443, null), await RouterPorts.MappingAsync(http, Gateway, 9443, CancellationToken.None));

        // Another device holds the port: the router says so.
        router.Mappings[9444] = ("192.168.1.99", 22, "0");
        var conflict = await Assert.ThrowsAsync<RouterPortException>(() => RouterPorts.AddAsync(http, Gateway, 9444, "192.168.1.10", 9443, "Martlet host", CancellationToken.None));
        Assert.Equal(718, conflict.Code);

        // A router that takes only permanent mappings gets one.
        router.PermanentOnly = true;
        await RouterPorts.AddAsync(http, Gateway, 9445, "192.168.1.10", 9443, "Martlet host", CancellationToken.None);
        Assert.Equal("0", router.Mappings[9445].Lease);
        await RouterPorts.RemoveAsync(http, Gateway, 9445, CancellationToken.None);
        await RouterPorts.RemoveAsync(http, Gateway, 9445, CancellationToken.None);
        Assert.False(router.Mappings.ContainsKey(9445));
        Assert.Contains("<NewPortMappingDescription>a &amp; b</NewPortMappingDescription>",
            RouterPorts.Envelope(Gateway.ServiceType, "AddPortMapping", [("NewPortMappingDescription", "a & b")]));
    }

    [Fact]
    public void Addresses_that_the_internet_cant_reach_are_told_apart()
    {
        Assert.Equal(RouterAddressKind.Public, RouterPorts.Kind(IPAddress.Parse("203.0.113.7")));
        Assert.Equal(RouterAddressKind.SharedByProvider, RouterPorts.Kind(IPAddress.Parse("100.72.1.2")));
        Assert.Equal(RouterAddressKind.Private, RouterPorts.Kind(IPAddress.Parse("192.168.0.2")));
        Assert.Equal(RouterAddressKind.Private, RouterPorts.Kind(IPAddress.Parse("10.1.2.3")));
        Assert.Equal(RouterAddressKind.Private, RouterPorts.Kind(IPAddress.Parse("172.20.0.1")));
        Assert.Equal(RouterAddressKind.Public, RouterPorts.Kind(IPAddress.Parse("172.32.0.1")));
        Assert.Equal(RouterAddressKind.Unknown, RouterPorts.Kind(IPAddress.Parse("0.0.0.0")));
    }
}
