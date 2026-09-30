using System.Net;
using System.Text.Json;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>The host's copy of the shared cluster plan (who does what across the owner's computers).</summary>
public sealed partial class Audio2FaceHostConnection
{
    private const string ClusterPath = "/martlet/v1/cluster";

    /// <summary>Reads the host's copy of the cluster plan. Hosts older than cluster sync refuse with code
    /// <c>request.invalid</c>.</summary>
    public async Task<ClusterPlan> ReadClusterAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + ClusterPath);
        Sign(request, []);
        return await ClusterAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Merges <paramref name="plan"/> into the host's copy and returns the merged plan, which may include newer
    /// changes another computer made.</summary>
    public async Task<ClusterPlan> MergeClusterAsync(ClusterPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var body = plan.Write();
        using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + ClusterPath)
        {
            Content = Audio2FaceHostClient.JsonContent(body)
        };
        Sign(request, body);
        return await ClusterAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ClusterPlan> ClusterAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await Audio2FaceHostClient.ReadJson(response, 64 * 1024, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw Audio2FaceHostClient.Remote(document.RootElement);
        try
        {
            var root = document.RootElement;
            if (root.GetProperty("host_id").GetString() != pairing.HostId)
                throw new Audio2FaceHostException("response.invalid", "The host identity changed; pair again.");
            return ClusterPlan.Parse(JsonSerializer.SerializeToUtf8Bytes(root.GetProperty("plan")));
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or ContractException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's copy of who does what was invalid.");
        }
    }
}
