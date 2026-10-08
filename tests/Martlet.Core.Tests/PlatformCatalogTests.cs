using Martlet.Core.Cluster;
using Martlet.Core.Installation;
using Martlet.Core.Platforms;

namespace Martlet.Core.Tests;

public sealed class PlatformCatalogTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static HostHardware Report(string id, params HostGpu[] gpus) =>
        new(id, "https://192.168.1.20:9443", Now, Now, "native", "Ubuntu 24.04", null, null, 8, 32, null, "yes", gpus);

    [Fact]
    public void Gpu_roles_are_refused_with_the_reason_unknown_without_a_report_and_allowed_when_they_fit()
    {
        var amd = PlatformDevice.FromHost("gpu-1", Report("gpu-1", new HostGpu("Radeon RX 6600", "amd", 8192, "amdgpu")));
        var f5 = PlatformCatalog.Check("f5", PlatformSide.Host, amd);
        Assert.Equal(PlatformVerdict.No, f5.Verdict);
        Assert.Contains("NVIDIA GPU with 6 GB+", f5.Reason, StringComparison.Ordinal);
        Assert.Contains("Radeon RX 6600", f5.Reason, StringComparison.Ordinal);
        Assert.True(PlatformCatalog.Check("ollama", PlatformSide.Host, amd).Allowed);

        var unreported = PlatformCatalog.Check("audio2face", PlatformSide.Host, PlatformDevice.FromHost("old", null));
        Assert.Equal(PlatformVerdict.Unknown, unreported.Verdict);
        Assert.True(unreported.Allowed);

        var small = PlatformDevice.FromHost("gpu-2", Report("gpu-2", new HostGpu("RTX 3050", "nvidia", 4096, "560")));
        Assert.Equal(PlatformVerdict.Yes, PlatformCatalog.Check("audio2face", PlatformSide.Host, small).Verdict);
        Assert.Equal(PlatformVerdict.No, PlatformCatalog.Check("f5", PlatformSide.Host, small).Verdict);
    }

    [Fact]
    public void Linux_and_mac_companions_offer_cloud_routes_and_refuse_windows_and_nvidia_engines()
    {
        var linux = new PlatformDevice { Platform = DevicePlatform.Linux, Name = "This computer", Arm64 = false, Gpus = [] };
        var intelMac = new PlatformDevice { Platform = DevicePlatform.MacOs, Name = "This computer", Arm64 = false, Gpus = [] };
        foreach (var device in new[] { linux, intelMac })
        {
            foreach (var engine in new[] { "openai-llm", "chat-completions", "openai-stt", "openai-tts", "loudness-lipsync", "character-overlay" })
                Assert.Equal(PlatformVerdict.Yes, PlatformCatalog.Check(engine, PlatformSide.Companion, device).Verdict);
            Assert.Equal(PlatformVerdict.No, PlatformCatalog.Check("windows-speech", PlatformSide.Companion, device).Verdict);
            Assert.Equal(PlatformVerdict.NotYet, PlatformCatalog.Check("hands-free", PlatformSide.Companion, device).Verdict);
        }
        Assert.Contains("NVIDIA", PlatformCatalog.Check("audio2face", PlatformSide.Companion, intelMac).Reason, StringComparison.Ordinal);
        Assert.Equal(PlatformVerdict.No, PlatformCatalog.Check("f5", PlatformSide.Companion, intelMac).Verdict);
        Assert.Contains("Apple silicon", PlatformCatalog.Check("mlx-llm", PlatformSide.Companion, linux).Reason, StringComparison.Ordinal);
        var intelMlx = PlatformCatalog.Check("mlx-llm", PlatformSide.Companion, intelMac);
        Assert.Equal(PlatformVerdict.No, intelMlx.Verdict);
        Assert.Contains("Intel processor", intelMlx.Reason, StringComparison.Ordinal);
        Assert.Equal(PlatformVerdict.No, PlatformCatalog.Check("apple-on-device-llm", PlatformSide.Companion, intelMac with { OsVersion = new(26, 0) }).Verdict);
        Assert.Contains("Linux and macOS desktop plan", PlatformCatalog.Check("screen-watch", PlatformSide.Companion, linux).Reason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Phone_hosts_report_their_platform_and_cannot_take_desktop_engines()
    {
        var iphone = PlatformDevice.FromHost("iphone", Report("iphone") with
        {
            Method = "app", Platform = "ios", OsVersion = "26.1", Architecture = "arm64",
            Features = [PlatformFeatures.ForegroundOnly]
        });
        Assert.Equal(DevicePlatform.Ios, iphone.Platform);
        Assert.True(iphone.ForegroundOnly);
        Assert.False(PlatformCatalog.ManagesRolesRemotely(iphone));
        Assert.Equal(PlatformVerdict.No, PlatformCatalog.Check("ollama", PlatformSide.Host, iphone).Verdict);
        Assert.Equal(PlatformVerdict.NotYet, PlatformCatalog.Check("apple-speech", PlatformSide.Host, iphone).Verdict);
        Assert.Contains(PlatformCatalog.HostNotes(iphone), note => note.Contains("only while Martlet is open", StringComparison.Ordinal));
        Assert.True(PlatformCatalog.ManagesRolesRemotely(PlatformDevice.FromHost("linux", null)));
    }

    [Fact]
    public void Mac_hosts_serve_ollama_and_whisper_but_never_nvidia_engines()
    {
        var mac = PlatformDevice.FromHost("studio", Report("studio", new HostGpu("Apple M2 Max GPU", "apple", 49152, "Metal")) with
        {
            Platform = "macos", OsVersion = "15.5", Architecture = "arm64"
        });
        Assert.Equal(DevicePlatform.MacOs, mac.Platform);
        Assert.Equal(PlatformVerdict.Yes, PlatformCatalog.Check("ollama", PlatformSide.Host, mac).Verdict);
        Assert.Equal(PlatformVerdict.Yes, PlatformCatalog.Check("whisper", PlatformSide.Host, mac).Verdict);
        Assert.Equal(PlatformVerdict.Yes, PlatformCatalog.Check("host-service", PlatformSide.Host, mac).Verdict);
        var f5 = PlatformCatalog.Check("f5", PlatformSide.Host, mac);
        Assert.Equal(PlatformVerdict.No, f5.Verdict);
        Assert.Contains("NVIDIA", f5.Reason, StringComparison.Ordinal);
        Assert.Equal(PlatformVerdict.No, PlatformCatalog.Check("audio2face", PlatformSide.Host, mac).Verdict);
        Assert.False(PlatformCatalog.ManagesRolesRemotely(mac));
    }

    [Fact]
    public void Arm64_linux_hosts_serve_ollama_and_whisper_but_not_the_x86_64_nvidia_containers()
    {
        var spark = PlatformDevice.FromHost("spark", Report("spark", new HostGpu("NVIDIA GB10", "nvidia", 122880, "580")) with
        {
            Architecture = "aarch64"
        });
        Assert.Equal(DevicePlatform.Linux, spark.Platform);
        Assert.True(spark.Arm64);
        Assert.Equal(PlatformVerdict.Yes, PlatformCatalog.Check("ollama", PlatformSide.Host, spark).Verdict);
        Assert.Equal(PlatformVerdict.Yes, PlatformCatalog.Check("whisper", PlatformSide.Host, spark).Verdict);
        foreach (var engine in new[] { "f5", "chatterbox", "xtts", "gpt-sovits", "dia", "singing", "pictures", "audio2face" })
        {
            var check = PlatformCatalog.Check(engine, PlatformSide.Host, spark);
            Assert.Equal(PlatformVerdict.No, check.Verdict);
            Assert.Contains("x86_64", check.Reason, StringComparison.Ordinal);
            Assert.Contains("ARM64", check.Reason, StringComparison.Ordinal);
        }

        var pc = PlatformDevice.FromHost("gpu-1", Report("gpu-1", new HostGpu("RTX 4070", "nvidia", 12288, "580")) with { Architecture = "x64" });
        Assert.Equal(PlatformVerdict.Yes, PlatformCatalog.Check("f5", PlatformSide.Host, pc).Verdict);
    }

    [Fact]
    public void Every_engine_names_a_known_job_and_at_most_one_entry_per_platform_and_side()
    {
        foreach (var engine in PlatformCatalog.Engines)
        {
            Assert.Contains(engine.Job, ClusterJobs.All.Append(PlatformCatalog.Feature));
            Assert.Equal(engine.Support.Count, engine.Support.DistinctBy(s => (s.Platform, s.Side)).Count());
            Assert.All(engine.Support.Where(s => s.Availability == PlatformAvailability.Planned), s => Assert.False(string.IsNullOrEmpty(s.Slice)));
        }
    }

    [Fact]
    public void Coverage_names_the_problem_effect_and_fixes_for_a_silent_thinking_host()
    {
        var job = new JobSituation
        {
            Job = ClusterJobs.Thinking, Doer = JobDoer.Host, DoerName = "gpu-1", Fallback = "Cloud: OpenRouter", FallbackIsCloud = true,
            Host = new() { HostId = "gpu-1", Reachable = false, Engine = "Ollama", SyncOn = true, Failover = true, FailoverTarget = "gpu-2" }
        };
        var coverage = JobCoverageRules.Evaluate(job);
        Assert.Equal(CoverageState.Unavailable, coverage.State);
        Assert.Contains("gpu-1 isn't answering", coverage.Problem, StringComparison.Ordinal);
        Assert.Contains("moves it to gpu-2", coverage.Problem, StringComparison.Ordinal);
        Assert.Equal(JobCoverageRules.Effect(ClusterJobs.Thinking), coverage.Effect);
        Assert.Equal([CoverageFix.CheckHost, CoverageFix.UseFallback, CoverageFix.OpenDevices], coverage.Fixes);
        Assert.Equal("Martlet can't reply right now", JobCoverageRules.Headline([coverage]));

        var unchecked_ = JobCoverageRules.Evaluate(job with { Host = job.Host! with { Reachable = null } });
        Assert.Equal(CoverageState.Unknown, unchecked_.State);
        Assert.Null(JobCoverageRules.Headline([unchecked_]));

        var lipSync = JobCoverageRules.Evaluate(new()
        {
            Job = ClusterJobs.LipSync, Doer = JobDoer.Host, Host = new() { HostId = "gpu-1", Reachable = true, Serves = false, Engine = "Audio2Face" }
        });
        Assert.Equal(CoverageState.Limited, lipSync.State);
        Assert.Contains("gpu-1 answers but doesn't run Audio2Face", lipSync.Problem, StringComparison.Ordinal);
        Assert.Equal([CoverageFix.InstallRole, CoverageFix.OpenDevices], lipSync.Fixes);
    }

    [Fact]
    public void Listening_whose_host_is_down_works_in_a_reduced_way_while_parakeet_on_this_device_stands_in()
    {
        var job = new JobSituation
        {
            Job = ClusterJobs.Listening, Doer = JobDoer.Host, DoerName = "gpu-1",
            Host = new() { HostId = "gpu-1", Reachable = false, Engine = "whisper", SyncOn = true }
        };
        var down = JobCoverageRules.Evaluate(job);
        Assert.Equal(CoverageState.Unavailable, down.State);
        Assert.Equal(JobCoverageRules.Effect(ClusterJobs.Listening), down.Effect);
        Assert.Equal("Martlet can't hear you right now", JobCoverageRules.Headline([down]));

        var standIn = JobCoverageRules.Evaluate(job with { StandIn = "Parakeet TDT 110M (English)" });
        Assert.Equal(CoverageState.Limited, standIn.State);
        Assert.Equal("gpu-1 isn't answering. Failover is off for this job.", standIn.Problem);
        Assert.Equal("Parakeet TDT 110M (English) hears you on this device's processor meanwhile.", standIn.Effect);
        Assert.Equal([CoverageFix.CheckHost, CoverageFix.OpenDevices], standIn.Fixes);
        Assert.Equal("Something is working in a reduced way", JobCoverageRules.Headline([standIn]));
        var missing = JobCoverageRules.Evaluate(job with { StandIn = "Parakeet", Host = job.Host! with { Reachable = true, Serves = false } });
        Assert.Equal(CoverageState.Limited, missing.State);
        // A host that answers needs no stand-in; only listening has one; and turned off in Setup, nothing listens at all.
        Assert.Equal(CoverageState.Ready, JobCoverageRules.Evaluate(job with { StandIn = "Parakeet", Host = job.Host! with { Reachable = true, Serves = true } }).State);
        Assert.Equal(CoverageState.Unavailable, JobCoverageRules.Evaluate(job with { Job = ClusterJobs.Thinking, StandIn = "Parakeet" }).State);
        Assert.Equal(CoverageState.Unavailable, JobCoverageRules.Evaluate(job with { StandIn = "Parakeet", Enabled = false }).State);
    }

    [Fact]
    public void Coverage_names_this_PCs_own_host_service_and_offers_the_step_that_fixes_it()
    {
        // The user's case: a companion PC (IMOUTO) that also hosts gives lip-sync to its own host service, imouto-host.
        var job = new JobSituation
        {
            Job = ClusterJobs.LipSync, Doer = JobDoer.Host, DoerName = "imouto-host", Fallback = "this PC",
            Host = new()
            {
                HostId = "imouto-host", Reachable = false, Engine = "Audio2Face", SyncOn = true, ThisPc = true,
                Trouble = LocalHostService.Trouble(new() { Stage = LocalHostServiceStage.DockerNotRunning })
            }
        };
        var down = JobCoverageRules.Evaluate(job);
        Assert.Equal(CoverageState.Limited, down.State);
        Assert.True(down.OwnHost);
        Assert.Equal("This PC's host service can't do it: Docker Desktop isn't running. Failover is off for this job.", down.Problem);
        Assert.DoesNotContain("imouto-host", down.Problem, StringComparison.Ordinal);
        Assert.Equal([CoverageFix.RepairHostService, CoverageFix.CheckHost, CoverageFix.UseFallback], down.Fixes);

        // Docker reads it as ready but the network check failed: still named as this PC's host service, never by its ID.
        var silent = JobCoverageRules.Evaluate(job with { Host = job.Host! with { Trouble = null } });
        Assert.StartsWith("This PC's host service isn't answering.", silent.Problem, StringComparison.Ordinal);
        Assert.Equal([CoverageFix.CheckHost, CoverageFix.UseFallback, CoverageFix.OpenDevices], silent.Fixes);

        // Once the pairing check reaches it, a stale Docker reading doesn't override it.
        Assert.Equal(CoverageState.Ready, JobCoverageRules.Evaluate(job with { Host = job.Host! with { Reachable = true, Serves = true } }).State);
        var missing = JobCoverageRules.Evaluate(job with { Host = job.Host! with { Reachable = true, Serves = false, Trouble = null } });
        Assert.Contains("isn't installed on this PC", missing.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Own_host_service_trouble_follows_the_host_dashboard_stages()
    {
        Assert.Equal("Docker Desktop isn't installed", LocalHostService.Trouble(new() { Stage = LocalHostServiceStage.DockerMissing }));
        Assert.Equal("it isn't set up on this PC", LocalHostService.Trouble(new() { Stage = LocalHostServiceStage.NotSetUp }));
        Assert.Equal("it is stopped", LocalHostService.Trouble(new() { Stage = LocalHostServiceStage.Stopped }));
        Assert.Equal("this PC's network address changed since it was set up",
            LocalHostService.Trouble(new() { Stage = LocalHostServiceStage.Running, Answering = true, AddressOnThisPc = false }));
        Assert.Equal("it runs but isn't answering on your network yet",
            LocalHostService.Trouble(new() { Stage = LocalHostServiceStage.Running, Answering = false }));
        Assert.Null(LocalHostService.Trouble(new() { Stage = LocalHostServiceStage.Running, Answering = true, AddressOnThisPc = true }));
    }

    [Fact]
    public void Forget_and_remove_say_where_each_job_goes_before_anything_changes()
    {
        var thinking = new JobSituation
        {
            Job = ClusterJobs.Thinking, Doer = JobDoer.Host, Fallback = "Cloud: OpenRouter", FallbackIsCloud = true,
            Host = new() { HostId = "gpu-1", Reachable = true, Engine = "Ollama", SyncOn = true }
        };
        var listening = new JobSituation
        {
            Job = ClusterJobs.Listening, Doer = JobDoer.Host, Host = new() { HostId = "gpu-1", Reachable = true, Engine = "whisper" }
        };
        var forget = JobCoverageRules.ForgetImpact([thinking, listening], "gpu-1");
        Assert.Contains(forget, line => line.StartsWith("Thinking goes back to your Setup choice, Cloud: OpenRouter", StringComparison.Ordinal) &&
            line.Contains("may cost money", StringComparison.Ordinal));
        Assert.Contains(forget, line => line.StartsWith("Listening: nobody will do it", StringComparison.Ordinal));
        Assert.Contains(forget, line => line.Contains("other computers", StringComparison.Ordinal));
        Assert.Empty(JobCoverageRules.ForgetImpact([thinking], "gpu-9"));

        Assert.True(JobCoverageRules.HandBackFirst(thinking, "gpu-1"));
        var withFailover = thinking with { Host = thinking.Host! with { Failover = true, FailoverTarget = "gpu-2" } };
        Assert.False(JobCoverageRules.HandBackFirst(withFailover, "gpu-1"));
        Assert.Contains(JobCoverageRules.RemoveRoleImpact(withFailover, "gpu-1", sharedPlanUsesHost: true),
            line => line.Contains("moves to gpu-2", StringComparison.Ordinal));
    }
}
