using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Martlet.Gateway;
using Martlet.Gateway.Persistence;

namespace Martlet.Gateway.Persistence.Portable.Tests;

internal static class OwnerScenario
{
    internal sealed class Audit : IGatewayAuditSink
    {
        public void Record(GatewayAuditEvent gatewayEvent) { }
    }

    internal static async Task RunAsync(string path, ILinuxFileSystem? fileSystem, bool faultNonce, bool renew)
    {
        var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var origin = new GatewayOrigin($"https://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}");
        port.Stop();
        var armed = false;
        IssuedDeviceCredential credential;
        GatewayHostIdentity identity;
        HttpRequestMessage signed;
        await using (var owner = Open(HostOpenMode.Create, step =>
        {
            if (armed && step == StoreStep.PendingFlushed) throw new IOException("Synthetic commit interruption.");
        }))
        {
            identity = owner.Identity!;
            await owner.StartAsync();
            using var client = PinnedGatewayClient.Create(origin, identity);
            credential = await Pair(owner, client, origin);
            signed = Sign(origin, identity, credential);
            armed = faultNonce;
            using var request = Clone(signed);
            using var response = await client.SendAsync(request);
            Assert.Equal(faultNonce ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, response.StatusCode);
            if (!faultNonce) await owner.CloseCleanlyAsync();
        }
        using (signed)
        await using (var owner = Open(renew ? HostOpenMode.RenewCertificate : HostOpenMode.Open))
        {
            Assert.Equal(identity, owner.Identity);
            Assert.IsType<PairedDeviceLifetime>(Assert.Single(owner.ListRegistrations()).Lifetime);
            await owner.StartAsync();
            using var client = PinnedGatewayClient.Create(origin, identity);
            using var replay = Clone(signed);
            using var response = await client.SendAsync(replay);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var request = Sign(origin, identity, credential);
            using var fresh = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
            Assert.Equal(1, owner.RevokeDevice("fixture-device"));
            await owner.CloseCleanlyAsync();
        }
        await using var revoked = Open(HostOpenMode.Open);
        Assert.Empty(revoked.ListRegistrations());
        Assert.Equal(identity, revoked.Identity);
        await revoked.CloseCleanlyAsync();

        DurableGatewayHost Open(HostOpenMode mode, Action<StoreStep>? fault = null) =>
            DurableGatewayHost.Open(path, "fixture-host", origin, [], new Audit(), LocalGatewayDecision.Enable,
                mode, TimeProvider.System, fault, storageBackend: GatewayStorageBackend.LinuxServicePermissions,
                linuxFileSystem: fileSystem);
    }

    internal static async Task<IssuedDeviceCredential> Pair(DurableGatewayHost owner, PinnedGatewayClient client,
        GatewayOrigin origin)
    {
        var card = owner.OpenPairing(new()
        {
            DeviceId = "fixture-device", DisplayName = "Fixture", Roles = [GatewayRole.Voice]
        });
        var payload = JsonSerializer.Serialize(new
        {
            protocol_version = new { major = 2, minor = 0 }, pairing_id = card.PairingId,
            pairing_token = card.Token.Reveal(), host_id = card.HostId,
            spki_fingerprint = card.SpkiFingerprint, device_id = "fixture-device"
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, origin.CanonicalOrigin + "/martlet/v1/pair")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = json.RootElement;
        Assert.Equal(2, result.GetProperty("protocol_version").GetProperty("major").GetInt32());
        Assert.Equal("paired", result.GetProperty("lifetime").GetProperty("kind").GetString());
        Assert.False(result.GetProperty("lifetime").TryGetProperty("expires_at", out _));
        return new()
        {
            CredentialId = result.GetProperty("credential_id").GetString()!,
            DeviceId = "fixture-device", Roles = [GatewayRole.Voice],
            Lifetime = new PairedDeviceLifetime(), Secret = new(result.GetProperty("credential_secret").GetString()!)
        };
    }

    internal static HttpRequestMessage Sign(GatewayOrigin origin, GatewayHostIdentity identity, IssuedDeviceCredential credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, origin.CanonicalOrigin + "/martlet/v1/version");
        new GatewayRequestSigner(identity, credential).Sign(request, GatewayRole.Voice);
        return request;
    }

    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return copy;
    }
}
