using System.Net;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

/// <summary>A host on its exact home address follows its computer to a new home address (GatewayHomeListeners).</summary>
public sealed class HomeListenerTests
{
    private static readonly GatewayOrigin Home = new("https://192.168.1.20:9443");

    private static string[] Select(bool held, string[] local, params string[] homes) =>
        GatewayHomeListeners.Select(Home, held, local.Select(IPAddress.Parse), homes.Select(IPAddress.Parse)).Select(e => e.ToString()).ToArray();

    [Fact]
    public void A_computer_that_has_its_home_address_needs_nothing_more()
    {
        Assert.Empty(Select(true, ["192.168.1.20", "127.0.0.1", "172.17.0.1"], "192.168.1.20"));
        Assert.Empty(Select(true, ["::ffff:192.168.1.20"], "192.168.1.20"));
    }

    [Fact]
    public void A_computer_that_got_another_address_listens_there_on_the_home_port()
    {
        Assert.Equal(["192.168.1.37:9443"], Select(true, ["192.168.1.37", "127.0.0.1", "172.17.0.1"], "192.168.1.37"));
        Assert.Equal(["10.0.0.5:9443", "192.168.1.37:9443"], Select(false, ["10.0.0.5", "192.168.1.37"], "10.0.0.5", "192.168.1.37", "10.0.0.5"));
    }

    [Fact]
    public void Only_private_home_addresses_are_listened_on()
    {
        Assert.Empty(Select(true, ["203.0.113.7", "127.0.0.1"], "203.0.113.7", "127.0.0.1", "100.101.102.103"));
        Assert.Empty(Select(true, []));
    }

    [Fact]
    public void A_home_address_that_was_missing_at_start_is_listened_on_once_it_is_back()
    {
        // The main listener couldn't take it at start (the network wasn't up yet, or the address was elsewhere).
        Assert.Equal(["192.168.1.20:9443"], Select(false, ["192.168.1.20", "192.168.1.37"], "192.168.1.37"));
    }

    [Fact]
    public async Task A_gateway_whose_home_address_is_gone_starts_and_listens_where_its_computer_is()
    {
        var port = FreePort();
        var origin = new GatewayOrigin($"https://10.255.255.254:{port}");
        using var certificate = Certificate(origin.Address);
        var identity = GatewayHostIdentity.FromCertificate("moved-host", certificate);
        var server = new GatewayServer(identity, origin, [], new RecordingAuditSink());
        await using var listener = await server.StartAsync(new GatewayTlsBinding(origin, identity, certificate), new KestrelGatewayListenerFactory());
        Assert.Equal(origin.CanonicalOrigin, listener.Origin.CanonicalOrigin);
        var expected = Martlet.Core.Network.HostFind.HomeAddresses().Select(a => $"{a}:{port}").Order(StringComparer.Ordinal).ToArray();
        for (var waited = 0; waited < 50 && !server.MovedHomeListeners.SequenceEqual(expected); waited++) await Task.Delay(100);
        Assert.Equal(expected, server.MovedHomeListeners);
    }

    [Fact]
    public void Only_a_missing_address_lets_the_gateway_start_away()
    {
        Assert.True(GatewayServer.AddressMissing(new IOException("bind", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.AddressNotAvailable))));
        Assert.False(GatewayServer.AddressMissing(new IOException("bind", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.AddressAlreadyInUse))));
        Assert.False(GatewayServer.AddressMissing(null));
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Any, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate(IPAddress address)
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Martlet moved host (fixture)", key,
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
            System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
            new System.Security.Cryptography.OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var names = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
        names.AddIpAddress(address);
        request.CertificateExtensions.Add(names.Build());
        var now = DateTimeOffset.UtcNow;
        using var ephemeral = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        return System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12, password), password,
            System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.UserKeySet);
    }
}
