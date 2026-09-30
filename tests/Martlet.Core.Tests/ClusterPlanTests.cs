using System.Text;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;

namespace Martlet.Core.Tests;

public sealed class ClusterPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Merge_keeps_the_newest_entry_per_job_and_host_in_any_order()
    {
        var a = ClusterPlan.Empty
            .Assign(ClusterJobs.Thinking, "gpu-a", false, true, null, "desktop-a", Now)
            .Observe("gpu-a", "https://192.168.1.20:9443", [new() { Kind = "ollama", Model = "qwen2.5:7b" }], false, "desktop-a", Now);
        var b = ClusterPlan.Empty
            .Assign(ClusterJobs.Thinking, "gpu-b", false, false, null, "desktop-b", Now.AddSeconds(5))
            .Assign(ClusterJobs.LipSync, "gpu-a", false, false, null, "desktop-b", Now.AddSeconds(5));
        var c = a.Observe("gpu-a", null, [], true, "desktop-c", Now.AddSeconds(9));

        var left = ClusterPlan.Merge(ClusterPlan.Merge(a, b), c);
        var right = ClusterPlan.Merge(c, ClusterPlan.Merge(b, a));
        Assert.Equal(left.Digest(), right.Digest());
        Assert.Equal(left.Digest(), ClusterPlan.Merge(left, left).Digest());
        Assert.Equal("gpu-b", left.For(ClusterJobs.Thinking)!.HostId);
        Assert.Equal("gpu-a", left.For(ClusterJobs.LipSync)!.HostId);
        Assert.True(left.Node("gpu-a")!.Removed);
    }

    [Fact]
    public void Revisions_stay_ahead_of_a_copy_written_with_a_fast_clock()
    {
        var future = ClusterPlan.Empty.Assign(ClusterJobs.Speaking, "gpu-a", false, false, null, "desktop-fast", Now.AddDays(3));
        var later = future.Assign(ClusterJobs.Speaking, "gpu-b", false, false, null, "desktop-slow", Now);
        Assert.True(later.For(ClusterJobs.Speaking)!.Revision > future.For(ClusterJobs.Speaking)!.Revision);
        Assert.Equal("gpu-b", ClusterPlan.Merge(future, later).For(ClusterJobs.Speaking)!.HostId);
    }

    [Fact]
    public void Plans_round_trip_and_reject_unknown_or_newer_content()
    {
        var plan = ClusterPlan.Empty.Assign(ClusterJobs.LipSync, null, true, false, null, "desktop-a", Now);
        var parsed = ClusterPlan.Parse(plan.Write());
        Assert.Equal(plan.Digest(), parsed.Digest());
        Assert.True(parsed.For(ClusterJobs.LipSync)!.Off);

        var json = Encoding.UTF8.GetString(plan.Write());
        Assert.Throws<ContractException>(() => ClusterPlan.Parse(Encoding.UTF8.GetBytes(json.Replace("\"schema_version\":1", "\"schema_version\":2"))));
        Assert.Throws<ContractException>(() => ClusterPlan.Parse(Encoding.UTF8.GetBytes(json.Replace("{\"schema_version\"", "{\"secret\":\"x\",\"schema_version\""))));
        Assert.Throws<ContractException>(() => ClusterPlan.Parse(Encoding.UTF8.GetBytes(json.Replace("\"desktop-a\"", "\"../etc\""))));
        Assert.Throws<ContractException>(() => ClusterPlan.Parse("not json"u8));
    }
}
