using System.Net;
using System.Text.Json;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

public sealed class AdmissionHealthTests
{
    [Theory]
    [InlineData("stopping")]
    [InlineData("closed")]
    [InlineData("clock")]
    public async Task Ready_is_distinct_from_live_and_never_probes_workers(string failure)
    {
        var worker = new SyntheticWorker(GatewayTestHost.Capabilities("worker", GatewayWorkerKind.OllamaLlm,
            GatewayRole.Voice), () => throw new InvalidOperationException("Must not probe workers."));
        await using var host = await GatewayTestHost.StartAsync([worker]);
        _ = host.OpenPairing();
        Assert.True(host.Server.Credentials.AdmissionsOpen);
        using (var request = new HttpRequestMessage(HttpMethod.Get, host.Origin.CanonicalOrigin + "/health/ready"))
        using (var response = await host.Client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("{\"schema_version\":1,\"scope\":\"listener-auth-admission\",\"listener\":\"listening\",\"auth_admission\":\"open\",\"model_readiness\":\"not-probed\"}",
                await response.Content.ReadAsStringAsync());
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        }
        switch (failure)
        {
            case "stopping": host.Server.Credentials.StopAdmissions(); break;
            case "closed": host.Server.Credentials.Close(); break;
            case "clock": host.Clock.Advance(TimeSpan.FromSeconds(-1)); break;
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, host.Origin.CanonicalOrigin + "/health/ready"))
        using (var response = await host.Client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("closed", json.RootElement.GetProperty("auth_admission").GetString());
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, host.Origin.CanonicalOrigin + "/health/live"))
        using (var response = await host.Client.SendAsync(request))
            Assert.Equal("{\"status\":\"live\"}", await response.Content.ReadAsStringAsync());
    }
}
