using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Martlet.Gateway.Host.Linux;
using Martlet.Gateway.Persistence;
using Martlet.Gateway.Persistence.Portable.Tests;

namespace Martlet.Gateway.Host.Linux.Tests;

public sealed class BindingHealthTests
{
    [Theory]
    [InlineData("10.0.0.3")]
    [InlineData("172.31.0.2")]
    [InlineData("192.168.12.3")]
    [InlineData("fd12::3")]
    public void Exact_private_SAN_survives_strict_load_and_same_key_renewal(string ip)
    {
        var address = IPAddress.Parse(ip);
        using var first = HostCertificate.Create(DateTimeOffset.UtcNow, privateAddress: address);
        var encoded = first.Export(X509ContentType.Pkcs12);
        try
        {
            using var loaded = HostCertificate.Load(encoded);
            Assert.Equal(address, HostCertificate.PrivateAddress(loaded));
            using var key = loaded.GetECDsaPrivateKey();
            using var renewed = HostCertificate.Create(DateTimeOffset.UtcNow, key, HostCertificate.PrivateAddress(loaded));
            Assert.Equal(new SystemGatewayCrypto().SpkiFingerprint(first), new SystemGatewayCrypto().SpkiFingerprint(renewed));
            Assert.Equal(address, HostCertificate.PrivateAddress(renewed));
            Assert.Equal(3, renewed.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateIPAddresses().Count());
        }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    [Fact]
    public async Task Rebind_preserves_permanent_pairing_and_pin_without_any_LAN_listener()
    {
        using var fs = new FakeLinuxFileSystem();
        var loopback = FixturePlatform.FreeOrigin();
        var privateOrigin = new GatewayOrigin("https://192.168.20.3:9443");
        GatewayHostIdentity identity;
        await using (var owner = Open(loopback, HostOpenMode.Create))
        {
            identity = owner.Identity!;
            await owner.StartAsync();
            using var client = PinnedGatewayClient.Create(loopback, identity);
            _ = await OwnerScenario.Pair(owner, client, loopback);
            await owner.CloseCleanlyAsync();
        }
        await using (var owner = Open(privateOrigin, HostOpenMode.Rebind, identity))
        {
            Assert.Equal(identity, owner.Identity);
            Assert.IsType<PairedDeviceLifetime>(Assert.Single(owner.ListRegistrations()).Lifetime);
            await owner.CloseCleanlyAsync();
        }
        await using (var owner = Open(privateOrigin, HostOpenMode.RenewCertificate, identity))
        {
            Assert.Equal(identity, owner.Identity);
            await owner.CloseCleanlyAsync();
        }
        Assert.Throws<GatewayPersistenceException>(() => DurableGatewayHost.Open("/srv/martlet/store",
            "fixture-host", privateOrigin, [], new QuietAudit(), LocalGatewayDecision.Enable, HostOpenMode.Open,
            TimeProvider.System, storageBackend: GatewayStorageBackend.LinuxServicePermissions, linuxFileSystem: fs));
        var bytes = fs.Store.Children["authority.bin"].Bytes.ToArray();
        Assert.Throws<GatewayPersistenceException>(() => Open(privateOrigin, HostOpenMode.Rebind,
            identity with { SpkiFingerprint = "sha256:" + new string('b', 64) }));
        Assert.Equal(bytes, fs.Store.Children["authority.bin"].Bytes);
        await using (var owner = Open(loopback, HostOpenMode.Rebind, identity))
        {
            await owner.StartAsync();
            using var client = PinnedGatewayClient.Create(loopback, identity);
            using var health = new HttpRequestMessage(HttpMethod.Get, loopback.CanonicalOrigin + "/health/ready");
            using var response = await client.SendAsync(health);
            Assert.True(HostHealth.IsReady(await response.Content.ReadAsByteArrayAsync()));
            Assert.Single(owner.ListRegistrations());
            await owner.CloseCleanlyAsync();
        }

        DurableGatewayHost Open(GatewayOrigin origin, HostOpenMode mode, GatewayHostIdentity? expected = null) =>
            DurableGatewayHost.Open("/srv/martlet/store", "fixture-host", origin, [], new QuietAudit(),
                LocalGatewayDecision.Enable, mode, TimeProvider.System,
                storageBackend: GatewayStorageBackend.LinuxServicePermissions, linuxFileSystem: fs,
                explicitBinding: true, expectedIdentity: expected);
    }

    [Fact]
    public async Task Missing_private_SAN_is_not_silently_rebound()
    {
        using var fs = new FakeLinuxFileSystem();
        await using (var owner = DurableGatewayHost.Open("/srv/martlet/store", "fixture-host",
            FixturePlatform.FreeOrigin(), [], new QuietAudit(), LocalGatewayDecision.Enable, HostOpenMode.Create,
            TimeProvider.System, storageBackend: GatewayStorageBackend.LinuxServicePermissions, linuxFileSystem: fs))
            await owner.CloseCleanlyAsync();
        var bytes = fs.Store.Children["authority.bin"].Bytes.ToArray();
        Assert.Throws<GatewayPersistenceException>(() => DurableGatewayHost.Open("/srv/martlet/store",
            "fixture-host", new("https://10.0.0.2:9443"), [], new QuietAudit(), LocalGatewayDecision.Enable,
            HostOpenMode.Open, TimeProvider.System, storageBackend: GatewayStorageBackend.LinuxServicePermissions,
            linuxFileSystem: fs, explicitBinding: true));
        Assert.Equal(bytes, fs.Store.Children["authority.bin"].Bytes);
    }

    [Fact]
    public async Task Interrupted_prepared_rebind_recovers_same_pin_and_pairings_without_a_LAN_listener()
    {
        using var fs = new FakeLinuxFileSystem();
        var loopback = FixturePlatform.FreeOrigin();
        var lan = new GatewayOrigin("https://192.168.30.4:9443");
        GatewayHostIdentity identity;
        await using (var owner = Open(loopback, HostOpenMode.Create))
        {
            identity = owner.Identity!;
            await owner.StartAsync();
            using var client = PinnedGatewayClient.Create(loopback, identity);
            _ = await OwnerScenario.Pair(owner, client, loopback);
            await owner.CloseCleanlyAsync();
        }
        Assert.Throws<GatewayPersistenceException>(() => Open(lan, HostOpenMode.Rebind, identity,
            step => { if (step == StoreStep.PendingFlushed) throw new IOException(); }));
        Assert.True(fs.Store.Children.ContainsKey("pending.bin"));
        await using var recovered = Open(lan, HostOpenMode.Open, identity);
        Assert.Equal(identity, recovered.Identity);
        Assert.IsType<PairedDeviceLifetime>(Assert.Single(recovered.ListRegistrations()).Lifetime);
        Assert.False(fs.Store.Children.ContainsKey("pending.bin"));
        await recovered.CloseCleanlyAsync();

        DurableGatewayHost Open(GatewayOrigin origin, HostOpenMode mode, GatewayHostIdentity? expected = null,
            Action<StoreStep>? fault = null) =>
            DurableGatewayHost.Open("/srv/martlet/store", "fixture-host", origin, [], new QuietAudit(),
                LocalGatewayDecision.Enable, mode, TimeProvider.System, fault,
                storageBackend: GatewayStorageBackend.LinuxServicePermissions, linuxFileSystem: fs,
                explicitBinding: true, expectedIdentity: expected);
    }

    [Fact]
    public async Task Actual_readiness_does_not_admit_nonce_or_claim_models_and_closes_on_storage_fault()
    {
        using var fs = new FakeLinuxFileSystem();
        var origin = FixturePlatform.FreeOrigin();
        var armed = false;
        await using var owner = DurableGatewayHost.Open("/srv/martlet/store", "fixture-host", origin,
            [], new QuietAudit(), LocalGatewayDecision.Enable, HostOpenMode.Create, TimeProvider.System,
            step => { if (armed && step == StoreStep.PendingFlushed) throw new IOException(); },
            storageBackend: GatewayStorageBackend.LinuxServicePermissions, linuxFileSystem: fs);
        await owner.StartAsync();
        using var client = PinnedGatewayClient.Create(origin, owner.Identity!);
        var credential = await OwnerScenario.Pair(owner, client, origin);
        var before = fs.Store.Children["authority.bin"].Bytes.ToArray();
        using (var request = new HttpRequestMessage(HttpMethod.Get, origin.CanonicalOrigin + "/health/ready"))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.True(HostHealth.IsReady(bytes));
            Assert.DoesNotContain("fixture-host", Encoding.UTF8.GetString(bytes));
        }
        Assert.Equal(before, fs.Store.Children["authority.bin"].Bytes);
        armed = true;
        using (var request = OwnerScenario.Sign(origin, owner.Identity!, credential))
        using (var response = await client.SendAsync(request))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        // Reuse the established TLS connection: a new handshake itself correctly refuses a closed authority.
        using (var request = new HttpRequestMessage(HttpMethod.Get, origin.CanonicalOrigin + "/health/ready"))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("closed", json.RootElement.GetProperty("auth_admission").GetString());
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, origin.CanonicalOrigin + "/health/live"))
        using (var response = await client.SendAsync(request))
            Assert.Equal("{\"status\":\"live\"}", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("{\"status\":\"live\"}")]
    [InlineData("{\"schema_version\":1,\"scope\":\"listener-auth-admission\",\"listener\":\"listening\",\"auth_admission\":\"open\",\"model_readiness\":\"ready\"}")]
    [InlineData("{\"schema_version\":1,\"schema_version\":1,\"scope\":\"listener-auth-admission\",\"listener\":\"listening\",\"auth_admission\":\"open\",\"model_readiness\":\"not-probed\"}")]
    public void Generic_or_malformed_health_is_never_ready(string json)
    {
        var result = false;
        try { result = HostHealth.IsReady(Encoding.UTF8.GetBytes(json)); }
        catch (HostInputException) { }
        Assert.False(result);
    }
}
