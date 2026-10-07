using Martlet.Avatar.Audio2Face.Remote;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// gpu_priority_status with a desktop data directory: GPU priority (live turn first) on every host paired there, read through
/// each host's own gateway (pinned TLS, the pairing secret from Windows Credential Manager, used only to sign the request):
/// GET /martlet/v1/priority, which holds the GPU map (each route's lane and graphics cards), each card's hold state, the holds,
/// the last preemptions and refusals with their counts, and placement warnings. A host older than GPU priority says so.
/// Read-only: it takes no hold and starts no work.
/// </summary>
internal static class GpuPriorityStatus
{
    internal static async Task<object> RunAsync(string dataDirectory, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(dataDirectory) || !Directory.Exists(dataDirectory))
            throw new ArgumentException("gpu-priority-status needs the absolute path of a Martlet desktop data directory.");
        var hosts = new List<object>();
        foreach (var host in SingingCheck.PairedHosts(dataDirectory))
        {
            try
            {
                using var connection = SingingCheck.Connect(host);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    hosts.Add(new { host = host.HostId, reachable = true, gpuPriority = true, priority = await connection.ReadPriorityAsync(timeout.Token) });
                }
                catch (Audio2FaceHostException error) when (error.Code == "request.invalid")
                {
                    hosts.Add(new
                    {
                        host = host.HostId, reachable = true, gpuPriority = false,
                        problem = "This host's Martlet is older than GPU priority: it neither keeps a graphics card for live turns nor " +
                            "stops Thinking pool work for them. Update it."
                    });
                }
            }
            catch (Exception error) when (error is Audio2FaceHostException or HttpRequestException or IOException or
                InvalidOperationException or OperationCanceledException && !token.IsCancellationRequested)
            {
                hosts.Add(new { host = host.HostId, reachable = false, problem = error.Message });
            }
        }
        return new { dataDirectory, hosts };
    }
}
