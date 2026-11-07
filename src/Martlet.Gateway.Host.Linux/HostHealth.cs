using System.Net;

namespace Martlet.Gateway.Host.Linux;

internal static class HostHealth
{
    internal static async Task<bool> ProbeAsync(HostConfiguration config, ServiceApproval approval,
        CancellationToken cancellation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
        using var client = PinnedGatewayClient.Create(config.Binding.Origin, approval.Identity, dial: Dial(config.Binding));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            config.Binding.Origin.CanonicalOrigin + "/health/ready");
        using var response = await client.SendAsync(request, linked.Token);
        if (response.StatusCode != HttpStatusCode.OK ||
            response.Content.Headers.ContentType?.MediaType != "application/json" ||
            response.Content.Headers.ContentLength is > 2048)
            return false;
        using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
        var buffer = new byte[2049];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), linked.Token);
            if (read == 0) break;
            count += read;
        }
        if (count > 2048) return false;
        return IsReady(buffer[..count]);
    }

    /// <summary>Where the health check connects: a gateway published from a container listens on every address of its own
    /// network, so the check uses that network's loopback. The published origin can be an address this computer no longer
    /// has (martlet-host home keeps the paired origin when the computer gets a new address). Null: connect to the origin.</summary>
    internal static IPEndPoint? Dial(Martlet.Gateway.Persistence.GatewayHostBinding binding) =>
        binding.ListenAddress is null ? null
            : new(binding.ListenAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? IPAddress.IPv6Loopback : IPAddress.Loopback, binding.Origin.Port);

    internal static bool IsReady(byte[] bytes)
    {
        using var document = StrictJson.Parse(bytes);
        var root = document.RootElement;
        StrictJson.Properties(root, "schema_version", "scope", "listener", "auth_admission", "model_readiness");
        return StrictJson.Number(root, "schema_version") == 1 &&
            StrictJson.Text(root, "scope") == "listener-auth-admission" &&
            StrictJson.Text(root, "listener") == "listening" &&
            StrictJson.Text(root, "auth_admission") == "open" &&
            StrictJson.Text(root, "model_readiness") == "not-probed";
    }
}
