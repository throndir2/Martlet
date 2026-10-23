using System.Net;
using System.Text.Json;
using Martlet.Core.Hardware;

namespace Martlet.Gateway.Tests;

public sealed class HardwareProofRouteTests
{
    [Fact]
    public async Task Anyone_gets_a_proof_that_binds_the_hosts_TLS_key_to_its_TPM_keys()
    {
        await using var host = await GatewayTestHost.StartAsync([]);
        var chip = new SimulatedTpm();
        host.Server.Hardware = HardwareKeys.From(chip.Open);
        var nonce = HardwareProof.NewNonce();

        using var response = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            host.Origin.CanonicalOrigin + HardwareProof.Target(nonce)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var proof = JsonSerializer.Deserialize<HardwareProofDocument>(await response.Content.ReadAsStringAsync(), HardwareProof.Json)!;
        Assert.Equal(host.Identity.HostId, proof.HostId);
        Assert.Equal(host.Identity.SpkiFingerprint, proof.Spki);
        Assert.Equal(HardwareCheck.Proven, HardwareProof.Check(proof, host.Identity.HostId, host.Identity.SpkiFingerprint, nonce,
            HardwareKeys.From(chip.Open).Keys));
    }

    [Fact]
    public async Task A_host_without_a_TPM_says_why_and_a_bad_nonce_is_refused()
    {
        await using var host = await GatewayTestHost.StartAsync([]);
        host.Server.Hardware = HardwareKeys.None("This computer has no TPM 2.0.");

        using (var response = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            host.Origin.CanonicalOrigin + HardwareProof.Target(HardwareProof.NewNonce()))))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var proof = JsonSerializer.Deserialize<HardwareProofDocument>(await response.Content.ReadAsStringAsync(), HardwareProof.Json)!;
            Assert.Empty(proof.Keys);
            Assert.Equal("This computer has no TPM 2.0.", proof.Unavailable);
        }
        using (var response = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            host.Origin.CanonicalOrigin + HardwareProof.Path + "?nonce=short")))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Proofs_are_limited_to_30_a_minute()
    {
        await using var host = await GatewayTestHost.StartAsync([]);
        host.Server.Hardware = HardwareKeys.None("none");
        for (var i = 0; i < 30; i++)
        {
            using var ok = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get,
                host.Origin.CanonicalOrigin + HardwareProof.Target(HardwareProof.NewNonce())));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        using var limited = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            host.Origin.CanonicalOrigin + HardwareProof.Target(HardwareProof.NewNonce())));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        host.Clock.Advance(TimeSpan.FromMinutes(1.1));
        using var again = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            host.Origin.CanonicalOrigin + HardwareProof.Target(HardwareProof.NewNonce())));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }
}
