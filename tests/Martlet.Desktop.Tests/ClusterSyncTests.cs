using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Cluster;
using Martlet.Core.Installation;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class ClusterSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 18, 0, 0, TimeSpan.Zero);

    private static HostRoute Route(string routeId, string model) => new(routeId, "/p", "c", "1.0", "d", "w", "1", model, "r",
        new string('a', 64), new string('b', 64), 1, 1, 1, 1, 1, 1, TimeSpan.FromSeconds(30), "request_abort");

    private static ClusterProbe Probe(string id, bool reachable, params HostRoute[] routes) =>
        new(id, reachable, "", reachable ? routes : null, null, reachable);

    private static HostHardware Hardware(string id, int memoryMb) => new(id, $"https://{id}:9443", Now, Now, "docker", "Ubuntu",
        null, null, null, null, null, null, [new HostGpu("GPU", "nvidia", memoryMb, null)]);

    [Fact]
    public void Failover_picks_a_reachable_host_running_the_engine_with_the_fewest_other_jobs_then_the_most_memory()
    {
        var ollama = Route(HostRoute.OllamaChatRouteId, "qwen2.5:7b");
        var plan = ClusterPlan.Empty
            .Assign(ClusterJobs.Thinking, "gpu-a", false, true, null, "desktop-a", Now)
            .Assign(ClusterJobs.LipSync, "gpu-b", false, false, null, "desktop-a", Now);
        var probes = new[]
        {
            Probe("gpu-a", false),
            Probe("gpu-b", true, ollama),
            Probe("gpu-c", true, ollama),
            Probe("gpu-d", true, ollama),
            Probe("gpu-e", true)
        };
        var hardware = new[] { Hardware("gpu-b", 24_000), Hardware("gpu-c", 8_000), Hardware("gpu-d", 12_000) };

        Assert.Equal("gpu-d", ClusterSync.FailoverTarget(plan, ClusterJobs.Thinking, "gpu-a", probes, hardware));
        Assert.Null(ClusterSync.FailoverTarget(plan, ClusterJobs.Listening, "gpu-a", probes, hardware));
        Assert.Null(ClusterSync.FailoverTarget(plan, ClusterJobs.Thinking, "gpu-a", [Probe("gpu-a", false), Probe("gpu-e", true)], hardware));
    }

    [Fact]
    public void Plan_copy_and_choice_are_kept_next_to_the_other_preferences()
    {
        using var scope = new AvatarHostingTests.Scope();
        Assert.False(ClusterSync.LoadEnabled(scope.DirectoryPath));
        Assert.Same(ClusterPlan.Empty, ClusterSync.LoadPlan(scope.DirectoryPath));
        ClusterSync.SaveEnabled(scope.DirectoryPath, true);
        var plan = ClusterPlan.Empty.Assign(ClusterJobs.Speaking, "gpu-a", false, true, "gpu-b", "desktop-a", Now);
        ClusterSync.SavePlan(scope.DirectoryPath, plan);
        Assert.True(ClusterSync.LoadEnabled(scope.DirectoryPath));
        Assert.Equal(plan.Digest(), ClusterSync.LoadPlan(scope.DirectoryPath).Digest());
        File.WriteAllText(Path.Combine(scope.DirectoryPath, ClusterSync.PlanFile), "{broken");
        Assert.Same(ClusterPlan.Empty, ClusterSync.LoadPlan(scope.DirectoryPath));
        Assert.Equal(["ollama"], ClusterSync.Roles([Route(HostRoute.OllamaChatRouteId, "qwen2.5:7b"), Route("unknown.route", "x")]).Select(r => r.Kind));
    }
}
