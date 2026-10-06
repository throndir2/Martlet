using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Cluster;
using Martlet.Core.Installation;
using Martlet.Core.Settings;
using Martlet.Core.Sync;
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
        // The logs entry older desktops may have left in the plan is not a job: it doesn't make its host look busier.
        var withLogs = plan.Assign(ClusterJobs.Logs, "gpu-d", false, false, null, "desktop-old", Now);
        Assert.Equal("gpu-d", ClusterSync.FailoverTarget(withLogs, ClusterJobs.Thinking, "gpu-a", probes, hardware));
        Assert.Null(ClusterSync.FailoverTarget(plan, ClusterJobs.Listening, "gpu-a", probes, hardware));
        Assert.Null(ClusterSync.FailoverTarget(plan, ClusterJobs.Thinking, "gpu-a", [Probe("gpu-a", false), Probe("gpu-e", true)], hardware));
    }

    private static AppSettings LocalOllama(string model) =>
        ChatCompletionsSetup.SelectRoute(SetupSettings.Begin(null), MainWindow.LocalOllamaBaseUrl, model);

    private static SharedSetting Shared(SharedRoute route, string by, DateTimeOffset at) => new()
    {
        Key = "thinking", Value = AppSettingsSections.Write(route), Revision = at.ToUnixTimeMilliseconds(), UpdatedAt = at, UpdatedBy = by
    };

    [Fact]
    public void A_new_computer_never_records_Martlet_defaults_over_the_network_choice()
    {
        // A computer that just joined does nothing yet: it records nothing, so the plan's host reaches it a check later.
        Assert.False(ClusterSync.Seeds(ClusterSync.Local(ClusterJobs.Thinking, SetupSettings.Begin(null), null)));
        Assert.False(ClusterSync.Seeds(ClusterSync.Local(ClusterJobs.LipSync, null, null)));
        Assert.True(ClusterSync.Seeds(new LocalJob("diva-host", false)));
        Assert.True(ClusterSync.Seeds(new LocalJob(null, true)));
    }

    [Fact]
    public void Thinking_with_this_PCs_own_Ollama_is_done_by_this_PC_for_every_computer_through_its_host_service()
    {
        var settings = LocalOllama("gemma4:e4b");
        Assert.Equal(new LocalJob(null, false), ClusterSync.Local(ClusterJobs.Thinking, settings, null));
        var here = ClusterSync.Local(ClusterJobs.Thinking, settings, null, "diva-host");
        Assert.Equal(new LocalJob("diva-host", false, true), here);
        // Other jobs and other routes stay as they are.
        Assert.Equal(new LocalJob(null, false), ClusterSync.Local(ClusterJobs.Listening, settings, null, "diva-host"));
        Assert.Equal(new LocalJob(null, false),
            ClusterSync.Local(ClusterJobs.Thinking, ChatCompletionsSetup.SelectRoute(SetupSettings.Begin(null), "https://openrouter.ai/api/v1",
                "google/gemma-4-26b-a4b-it"), null, "diva-host"));

        // It keeps its direct route when the plan names its own host service, or leaves the job to each computer's choice.
        var plan = ClusterPlan.Empty.Assign(ClusterJobs.Thinking, "diva-host", false, false, null, "desktop-diva", Now);
        Assert.True(ClusterSync.Matches(plan.For(ClusterJobs.Thinking)!, here));
        Assert.True(ClusterSync.Matches(plan.Assign(ClusterJobs.Thinking, null, false, false, null, "desktop-a", Now).For(ClusterJobs.Thinking)!, here));
        Assert.False(ClusterSync.Matches(plan.Assign(ClusterJobs.Thinking, "gpu-b", false, false, null, "desktop-a", Now).For(ClusterJobs.Thinking)!, here));
        Assert.False(ClusterSync.Matches(plan.Assign(ClusterJobs.Thinking, null, false, false, null, "desktop-a", Now).For(ClusterJobs.Thinking)!,
            new LocalJob("diva-host", false)));
    }

    [Fact]
    public void The_computer_Thinking_was_set_up_on_takes_it_on_for_every_computer()
    {
        var settings = LocalOllama("gemma4:e4b");
        var mine = ClusterSync.OwnOllama(settings);
        var here = ClusterSync.Local(ClusterJobs.Thinking, settings, null, "diva-host");
        var route = SharedRoute.From(mine!)!;
        var setUpHere = Shared(route, "desktop-diva", Now.AddDays(-2));
        var checkedAt = Now;
        // A new computer's old default ("each computer's own choice") written after the owner set Thinking up on Diva.
        var seeded = ClusterPlan.Empty.Assign(ClusterJobs.Thinking, null, false, false, null, "desktop-new", Now.AddDays(-1)).For(ClusterJobs.Thinking);

        Assert.True(ClusterSync.Claims(ClusterJobs.Thinking, seeded, here, setUpHere, mine, "desktop-diva", checkedAt));
        Assert.True(ClusterSync.Claims(ClusterJobs.Thinking, null, here, setUpHere, mine, "desktop-diva", checkedAt));
        // Not when another computer chose the shared route, a host does the job, the route differs, or this PC knows no host service.
        Assert.False(ClusterSync.Claims(ClusterJobs.Thinking, seeded, here, Shared(route, "desktop-other", Now.AddDays(-2)), mine, "desktop-diva", checkedAt));
        Assert.False(ClusterSync.Claims(ClusterJobs.Thinking, seeded, here, Shared(route with { Model = "gemma4:12b" }, "desktop-diva", Now.AddDays(-2)),
            mine, "desktop-diva", checkedAt));
        var hosted = ClusterPlan.Empty.Assign(ClusterJobs.Thinking, "gpu-b", false, false, null, "desktop-other", Now.AddDays(-1)).For(ClusterJobs.Thinking);
        Assert.False(ClusterSync.Claims(ClusterJobs.Thinking, hosted, here, setUpHere, mine, "desktop-diva", checkedAt));
        Assert.False(ClusterSync.Claims(ClusterJobs.Thinking, seeded, new LocalJob(null, false), setUpHere, mine, "desktop-diva", checkedAt));
        Assert.False(ClusterSync.Claims(ClusterJobs.Listening, seeded, here, setUpHere, mine, "desktop-diva", checkedAt));
        // A plan change newer than this PC's last look at the shared settings waits: the shared route may have changed with it.
        Assert.False(ClusterSync.Claims(ClusterJobs.Thinking, seeded, here, setUpHere, mine, "desktop-diva", null));
        var fresh = ClusterPlan.Empty.Assign(ClusterJobs.Thinking, null, false, false, null, "desktop-other", Now).For(ClusterJobs.Thinking);
        Assert.False(ClusterSync.Claims(ClusterJobs.Thinking, fresh, here, setUpHere, mine, "desktop-diva", checkedAt));
    }

    [Fact]
    public void Plan_copy_and_choice_are_kept_next_to_the_other_preferences()
    {
        using var scope = new AvatarHostingTests.Scope();
        Assert.True(ClusterSync.LoadEnabled(scope.DirectoryPath));
        Assert.Same(ClusterPlan.Empty, ClusterSync.LoadPlan(scope.DirectoryPath));
        ClusterSync.SaveEnabled(scope.DirectoryPath, false);
        Assert.False(ClusterSync.LoadEnabled(scope.DirectoryPath));
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
