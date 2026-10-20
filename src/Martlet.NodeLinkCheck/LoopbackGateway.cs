using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Martlet.HostClient;
using Martlet.Gateway;

namespace Martlet.NodeLinkCheck;

static class GatewayCommandLimits
{
    internal const int MaximumActive = 8;
}

sealed class MemoryStorage : IGatewayCommandStorage, IGatewayHomeAssistantStorage, IGatewayLogStorage
{
    internal byte[]? Saved { get; private set; }
    internal byte[]? HomeAssistantSaved { get; private set; }
    internal byte[]? LogsSaved { get; private set; }

    byte[]? IGatewayCommandStorage.Load() => Saved;
    void IGatewayCommandStorage.Save(byte[] bytes) => Saved = bytes;

    byte[]? IGatewayHomeAssistantStorage.Load() => HomeAssistantSaved;
    void IGatewayHomeAssistantStorage.Save(byte[] bytes) => HomeAssistantSaved = bytes;

    byte[]? IGatewayLogStorage.Load() => LogsSaved;
    void IGatewayLogStorage.Save(byte[] bytes) => LogsSaved = bytes;
}

sealed class NoAudit : IGatewayAuditSink
{
    public void Record(GatewayAuditEvent gatewayEvent) { }
}

sealed class LoopbackHost(GatewayServer server, GatewayListenerHandle listener, GatewayOrigin origin, GatewayHostIdentity identity) : IAsyncDisposable
{
    internal static async Task<LoopbackHost> StartAsync(X509Certificate2 certificate, MemoryStorage storage, string token)
    {
        var origin = new GatewayOrigin($"https://127.0.0.1:{FreePort()}");
        var identity = GatewayHostIdentity.FromCertificate("check-host", certificate);
        var server = new GatewayServer(identity, origin, [], new NoAudit());
        server.AttachCommandStorage(storage, token);
        server.AttachHomeAssistantStorage(storage);
        server.AttachLogStorage(storage);
        var listener = await server.StartAsync(new GatewayTlsBinding(origin, identity, certificate), new KestrelGatewayListenerFactory());
        return new(server, listener, origin, identity);
    }

    internal async Task<(HostPairing Pairing, string Secret)> PairAsync(string device)
    {
        var card = server.Pairing.OpenWindow(new() { DeviceId = device, DisplayName = device, Roles = [GatewayRole.Voice] });
        return await HostTransport.PairAsync(origin.CanonicalOrigin, identity.HostId, identity.SpkiFingerprint, device,
            card.PairingId, card.Token.Reveal());
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    public ValueTask DisposeAsync() => listener.DisposeAsync();
}

/// <summary>Signed requests the desktop client would never send (unknown commands, extra fields), signed the same way.</summary>
static class Raw
{
    internal static async Task<(HttpStatusCode Status, string Body)> SendAsync(HostPairing pairing, string? secret, HttpMethod method,
        string path, string? json)
    {
        using var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is X509Certificate2 presented &&
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(presented.PublicKey.ExportSubjectPublicKeyInfo())) == pairing.SpkiFingerprint;
        using var http = new HttpClient(handler);
        var body = json is null ? [] : Encoding.UTF8.GetBytes(json);
        using var request = new HttpRequestMessage(method, pairing.Origin + path);
        if (json is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        }
        if (secret is not null)
        {
            var key = SHA256.HashData(Base64Url.DecodeFromChars(secret));
            var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var canonical = Encoding.ASCII.GetBytes(string.Join('\n', "martlet-request-v1", pairing.HostId, pairing.CredentialId, method.Method, path,
                "voice", timestamp, nonce, Convert.ToHexStringLower(SHA256.HashData(body))));
            request.Headers.TryAddWithoutValidation("Authorization",
                $"Martlet-HMAC {pairing.CredentialId}.{Base64Url.EncodeToString(HMACSHA256.HashData(key, canonical))}");
            request.Headers.TryAddWithoutValidation("X-Martlet-Nonce", nonce);
            request.Headers.TryAddWithoutValidation("X-Martlet-Timestamp", timestamp);
            request.Headers.TryAddWithoutValidation("X-Martlet-Role", "voice");
        }
        using var response = await http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}

static class Fixture
{
    /// <summary>A throwaway self-signed loopback certificate (FIXTURE): never a real host identity.</summary>
    internal static X509Certificate2 Certificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Martlet node link check (fixture)", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);
    }
}
