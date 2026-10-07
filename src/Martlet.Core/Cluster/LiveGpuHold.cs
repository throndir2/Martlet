namespace Martlet.Core.Cluster;

/// <summary>Asks a paired host to keep the GPUs behind the live routes free of pool work.</summary>
public interface ILiveGpuHold
{
    /// <summary>Holds the GPUs that serve these routes on this host for ttl (at most 15 s). Call again to renew.</summary>
    Task HoldAsync(string hostId, IReadOnlyList<string> routeIds, TimeSpan ttl, CancellationToken token);

    /// <summary>Ends this client's hold on the host early.</summary>
    Task ReleaseAsync(string hostId, CancellationToken token);
}

/// <summary>Used when no paired host supports holds.</summary>
public sealed class NoLiveGpuHold : ILiveGpuHold
{
    public static readonly NoLiveGpuHold Instance = new();
    public Task HoldAsync(string hostId, IReadOnlyList<string> routeIds, TimeSpan ttl, CancellationToken token) => Task.CompletedTask;
    public Task ReleaseAsync(string hostId, CancellationToken token) => Task.CompletedTask;
}
