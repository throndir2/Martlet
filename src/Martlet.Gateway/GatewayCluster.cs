using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway;

/// <summary>Where a gateway keeps its copy of the shared cluster plan between restarts (cluster.json beside host.json on
/// Linux hosts). <see cref="Load"/> returns null when there is none; either call may throw on storage failure.</summary>
public interface IGatewayClusterStorage
{
    byte[]? Load();
    void Save(byte[] bytes);
}

/// <summary>This host's copy of who does what across the owner's computers. Paired desktops read it and merge their
/// changes into it; the host itself never acts on it. A copy that cannot be saved is still served from memory, and
/// desktops push it again.</summary>
internal sealed class GatewayClusterStore
{
    private readonly object gate = new();
    private ClusterPlan plan = ClusterPlan.Empty;
    private string digest = ClusterPlan.Empty.Digest();
    private IGatewayClusterStorage? storage;

    internal ClusterPlan Current { get { lock (gate) return plan; } }

    internal void Attach(IGatewayClusterStorage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ClusterPlan? saved = null;
        try { if (value.Load() is { } bytes) saved = ClusterPlan.Parse(bytes); }
        // An unreadable or malformed copy is replaced by the next desktop that syncs.
        catch (Exception) { }
        lock (gate)
        {
            storage = value;
            if (saved is not null) Replace(ClusterPlan.Merge(plan, saved), save: false);
        }
    }

    internal ClusterPlan Merge(ClusterPlan incoming)
    {
        lock (gate)
        {
            Replace(ClusterPlan.Merge(plan, incoming), save: true);
            return plan;
        }
    }

    private void Replace(ClusterPlan next, bool save)
    {
        var nextDigest = next.Digest();
        if (nextDigest == digest) return;
        plan = next;
        digest = nextDigest;
        if (!save || storage is null) return;
        try { storage.Save(next.Write()); }
        catch (Exception) { }
    }
}

internal sealed partial class GatewayHttpApplication
{
    internal const string ClusterPath = "/martlet/v1/cluster";

    internal GatewayClusterStore Cluster { get; } = new();

    /// <summary>GET returns this host's copy of the cluster plan; POST merges a desktop's copy into it and returns the
    /// merged result. Any paired device may do either; the plan holds no secrets and grants no authority.</summary>
    private async ValueTask InvokeClusterAsync(HttpContext context)
    {
        ClusterPlan result;
        if (context.Request.Method == HttpMethods.Get)
        {
            EnsureEmptyRequest(context.Request);
            _ = Authorize(context.Request, GatewayApiAccess.Read);
            result = Cluster.Current;
        }
        else if (context.Request.Method == HttpMethods.Post)
        {
            var bytes = await ReadInferenceBodyAsync(context.Request, ClusterPlan.MaximumBytes, context.RequestAborted)
                .ConfigureAwait(false);
            _ = authenticator.Authenticate(context.Request, crypto.Sha256(bytes));
            ClusterPlan incoming;
            try { incoming = ClusterPlan.Parse(bytes); }
            catch (ContractException) { throw new GatewayProtocolException("request.invalid"); }
            result = Cluster.Merge(incoming);
        }
        else throw new GatewayProtocolException("request.invalid");
        await WriteJsonAsync(context, StatusCodes.Status200OK, new ClusterDocument
        {
            ProtocolVersion = GatewayProtocolVersion.Current,
            HostId = identity.HostId,
            Plan = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(result.Write())
        }).ConfigureAwait(false);
    }

    private sealed record ClusterDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required System.Text.Json.JsonElement Plan { get; init; }
    }
}
