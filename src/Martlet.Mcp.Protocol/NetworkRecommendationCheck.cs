using Martlet.Core.Cluster;
using Martlet.Core.Planning;

namespace Martlet.Mcp;

/// <summary>network_recommendation_check: runs the production network recommender (<see cref="NetworkRecommender"/>, Home's
/// Recommended setup) on built-in fixture networks and reports each rule's outcome with the change list. In-process; it reads
/// nothing and contacts nothing. The fixtures are NOT real computers.</summary>
internal static class NetworkRecommendationCheck
{
    private static MachineGpu Nvidia(double gb, string name) => new(name, GpuVendor.Nvidia, gb);

    private static NetworkMachine Host(string id, params MachineGpu[] gpus) =>
        new(new MachineSpecs(id, id) { Gpus = gpus, RamGb = 64, CpuThreads = 16, Platform = "linux" }, NetworkMachineKind.Host) { HasHostService = true };

    private static NetworkMachine WindowsHost(string id, params MachineGpu[] gpus) =>
        new(new MachineSpecs(id, id) { Gpus = gpus, RamGb = 64, CpuThreads = 16, Platform = "windows" }, NetworkMachineKind.Host)
        {
            HasHostService = true, OnWindows = true
        };

    private static NetworkMachine Companion(string id, bool hostService = false, params MachineGpu[] gpus) =>
        new(new MachineSpecs(id, id) { Gpus = gpus, RamGb = 32, CpuThreads = 16, IsPrimary = true }, NetworkMachineKind.Companion)
        {
            HasHostService = hostService
        };

    private static HostedRolePlacement Role(string kind, string? model = null, int? gpu = null) => new(kind, model, gpu);

    private static NetworkSetupRequest Network(params NetworkMachine[] machines) => new(machines) { VoiceEngine = "chatterbox" };

    private static NetworkSetupRequest Apply(NetworkSetupRequest request, NetworkRecommendation recommendation) => request with
    {
        Machines = request.Machines.Select(m => m with { Roles = recommendation.Target.Machine(m.Specs.Id)?.Roles ?? m.Roles }).ToArray(),
        CurrentJobs = recommendation.Target.Jobs,
        CurrentThinkingPool = recommendation.Target.ThinkingPool
    };

    private static object Report(NetworkRecommendation recommendation) => new
    {
        recommendation.Fingerprint, recommendation.AlreadyOptimal, recommendation.WorthAsking,
        changes = recommendation.Changes.Select(c =>
            $"{c.Kind} {(c.MachineId.Length == 0 ? "(no host)" : c.MachineId)} [{c.Benefit}{(c.NeedsSomeoneThere ? ", someone there" : "")}]: {c.Summary} Why: {c.Why}"),
        target = new
        {
            machines = recommendation.Target.Machines.Select(m => new
            {
                m.MachineId, kind = m.Kind.ToString(),
                roles = m.Roles.Select(r => r.Kind + (r.Model is null ? "" : "=" + r.Model) + (r.GpuIndex is { } g ? $"@gpu{g}" : "")),
                m.Why
            }),
            jobs = recommendation.Target.Jobs.Select(j => new { j.Job, j.HostId, j.Off, j.OptionId, j.Pool }),
            thinkingPool = recommendation.Target.ThinkingPool
        },
        recommendation.CannotReply,
        recommendation.CannotSpeak,
        offline = recommendation.Offline.Select(o => o.Id),
        recommendation.Notes
    };

    private static bool OneModelPerCard(NetworkRecommendation recommendation) => recommendation.Target.Machines.All(m =>
    {
        var models = m.Roles.Where(r => r.Kind is NetworkRecommender.ThinkingRole or NetworkRecommender.DeepThinkingRole).ToList();
        return models.Count < 2 || models.All(r => r.GpuIndex is not null) && models.Select(r => r.GpuIndex).Distinct().Count() == models.Count;
    });

    private static bool WithinCapacity(NetworkRecommendation recommendation) =>
        recommendation.Target.Machines.Where(m => m.Usage is not null).All(m => m.Usage!.Gpus.All(g => g.Vram.Used <= g.Vram.Capacity + 1e-6));

    internal static object Run()
    {
        List<object> steps = [];
        var ok = true;
        void Step(string rule, string name, bool passed, object detail)
        {
            ok &= passed;
            steps.Add(new { rule, name, passed, detail });
        }

        // Two companion PCs and two hosts with nothing set up yet.
        var fresh = Network(Companion("desk-1", true, Nvidia(12, "RTX 4070")), Companion("desk-2", true, Nvidia(12, "RTX 4070")),
            Host("gpu-box", Nvidia(24, "RTX 4090")), Host("voice-box", Nvidia(16, "RTX 4080")));
        var plan = NetworkRecommender.Recommend(fresh);
        var speaking = plan.Target.Job(ClusterJobs.Speaking);
        var thinking = plan.Target.Job(ClusterJobs.Thinking);
        Step("6", "Two companion PCs and two hosts: the companion PCs run no host roles; the hosts take every job",
            plan.Target.Machines.Where(m => m.Kind == NetworkMachineKind.Companion).All(m => m.Roles.Count == 0) &&
            speaking?.HostId is "gpu-box" or "voice-box" && thinking?.HostId is "gpu-box" or "voice-box", Report(plan));
        Step("7", "New Thinking: the fastest model that hears (Gemma 4 E2B) on a card of its own; lip-sync goes beside the voice",
            thinking?.OptionId == "gemma4:e2b" && thinking.HostId != speaking?.HostId && plan.Target.Job(ClusterJobs.LipSync)?.HostId == speaking?.HostId,
            new { thinking, speaking = speaking?.HostId, lipSync = plan.Target.Job(ClusterJobs.LipSync)?.HostId });
        Step("9", "Pools: one more voice for the second companion PC, Thinking's job unshared, Deep thinking on a free card, no Thinking pool change",
            speaking?.Pool.Count == 1 && thinking?.Pool.Count == 0 && plan.Target.ThinkingPool.Count > 0 &&
            !plan.Changes.Any(c => c.Kind is SetupChangeKind.JoinPool or SetupChangeKind.LeavePool && c.Job is null),
            new { speakingPool = speaking?.Pool, listeningPool = plan.Target.Job(ClusterJobs.Listening)?.Pool, plan.Target.ThinkingPool });

        // Rule 1: a host with two NVIDIA cards running Thinking and Deep thinking unpinned.
        var twoCards = Network(Companion("desk-1"), Host("gpu-box", Nvidia(24, "RTX 4090"), Nvidia(24, "RTX 3090")) with
        {
            Roles = [Role("ollama", "gemma4:e2b"), Role("deep-thinking", "gemma4:12b")]
        }) with { CurrentJobs = [new JobPlan(ClusterJobs.Thinking, "gpu-box", OptionId: "gemma4:e2b")] };
        var pinned = NetworkRecommender.Recommend(twoCards);
        Step("1", "One language model per graphics card: Thinking and Deep thinking pinned to cards of their own",
            OneModelPerCard(pinned) && pinned.Changes.Any(c => c.Kind == SetupChangeKind.MoveToGpu), Report(pinned));

        // Rules 2 and 3: a Windows host whose voice shares its card with listening; a Linux host has a free card.
        var windows = Network(Companion("desk-1"), WindowsHost("win-box", Nvidia(12, "RTX 4070")) with
        {
            Roles = [Role("chatterbox", "chatterbox-turbo"), Role("stt", "large-v3-turbo")]
        }, Host("linux-box", Nvidia(12, "RTX 3060"))) with
        {
            CurrentJobs =
            [
                new JobPlan(ClusterJobs.Speaking, "win-box", OptionId: "chatterbox-turbo"),
                new JobPlan(ClusterJobs.Listening, "win-box", OptionId: "whisper-large-v3-turbo-cuda")
            ],
            Wanted = [PlanComponent.Voice, PlanComponent.Listening]
        };
        var isolated = NetworkRecommender.Recommend(windows);
        Step("2, 3", "The voice on a Windows host gets a card of its own (the same engine, Chatterbox Turbo), listening stays",
            isolated.Target.Job(ClusterJobs.Speaking)?.HostId == "linux-box" && isolated.Target.Job(ClusterJobs.Listening)?.HostId == "win-box" &&
            isolated.Target.Machines.All(m => m.Roles.Count(r => r.Kind.StartsWith("chatterbox", StringComparison.Ordinal)) <= 1),
            Report(isolated));

        // Rule 4: a crowded network keeps every card within its capacity.
        var crowded = NetworkRecommender.Recommend(Network(Companion("desk-1"), Companion("desk-2"), Companion("desk-3"),
            Host("small-box", Nvidia(8, "RTX 3070")), Host("two-box", Nvidia(12, "RTX 3060"), Nvidia(6, "GTX 1660")),
            WindowsHost("win-box", Nvidia(16, "RTX 4080"))) with { Preference = HostingPreference.PreferLocal });
        Step("4", "Headroom: no card is planned over its capacity (10% or 0.8 GB kept), one language model per card",
            WithinCapacity(crowded) && OneModelPerCard(crowded),
            crowded.Target.Machines.Where(m => m.Usage is not null).Select(m => new
            {
                m.MachineId, cards = m.Usage!.Gpus.Select(g => $"{g.Name}: {g.Vram.Used} of {g.Vram.Capacity} GB")
            }));

        // Rule 5: Deep thinking beside the voice on a two-card host.
        var lanes = NetworkRecommender.Recommend(Network(Companion("desk-1"), Host("gpu-box", Nvidia(16, "RTX 4080"), Nvidia(16, "RTX 4080")) with
        {
            Roles = [Role("chatterbox", "chatterbox-turbo", 0), Role("deep-thinking", "gemma4:e4b", 0)]
        }) with
        {
            CurrentJobs = [new JobPlan(ClusterJobs.Speaking, "gpu-box", OptionId: "chatterbox-turbo")],
            Wanted = [PlanComponent.Voice, PlanComponent.DeepThinking]
        });
        Step("5", "Live lane first: Deep thinking moves to the card no live job uses",
            lanes.Target.Machine("gpu-box")?.Roles.FirstOrDefault(r => r.Kind == "deep-thinking")?.GpuIndex == 1, Report(lanes));

        // Rule 5: needed jobs before optional extras. One companion PC runs Singing and a Listening pool place; both hosts
        // have been away for 155 minutes (the owner's report). No provider key: PreferLocal, as Desktop plans it.
        NetworkSetupRequest Stranded(double cardGb) => Network(Companion("desk-1", true, Nvidia(cardGb, $"RTX {cardGb} GB")) with
        {
            Roles = [Role("singing", "ace-step-v15-soulx-svc"), Role("stt", "large-v3-turbo")]
        }, Host("gpu-box", Nvidia(16, "RTX 4080")) with
        {
            Online = false, OfflineFor = TimeSpan.FromMinutes(155),
            Roles = [Role("ollama", "gemma4:e2b"), Role("chatterbox", "chatterbox-turbo"), Role("audio2face")]
        }, Host("ear-box", Nvidia(12, "RTX 3060")) with
        {
            Online = false, OfflineFor = TimeSpan.FromMinutes(155), Roles = [Role("stt", "large-v3-turbo")]
        }) with
        {
            CurrentJobs =
            [
                new JobPlan(ClusterJobs.Thinking, "gpu-box", OptionId: "gemma4:e2b"),
                new JobPlan(ClusterJobs.Speaking, "gpu-box", OptionId: "chatterbox-turbo"),
                new JobPlan(ClusterJobs.Listening, "ear-box", OptionId: "whisper-large-v3-turbo-cuda") { Pool = ["desk-1"] },
                new JobPlan(ClusterJobs.LipSync, "gpu-box", OptionId: "audio2face-3d")
            ],
            Preference = HostingPreference.PreferLocal
        };
        var stranded = NetworkRecommender.Recommend(Stranded(12));
        Step("5, 6", "Needed jobs first: a companion PC whose hosts are gone thinks on its own card first, then the voice joins it; " +
            "lip-sync follows the voice's loudness (only Thinking and the voice use a companion PC's card); Singing (optional) yields and goes first",
            stranded.Target.Job(ClusterJobs.Thinking) is { HostId: null, OptionId: "gemma4:e2b" } &&
            stranded.Target.Job(ClusterJobs.Speaking)?.HostId == "desk-1" && stranded.Target.Job(ClusterJobs.LipSync)?.Off == true &&
            stranded.Target.Machine("desk-1")?.Roles.Any(r => r.Kind is "audio2face" or "singing" or "stt") == false &&
            stranded.Changes.Any(c => c.Kind == SetupChangeKind.RemoveRole && c.RoleKind == "singing" && c.Benefit == SetupChangeBenefit.Required) &&
            stranded.Changes.FirstOrDefault()?.RoleKind == "singing" && stranded.Changes.ElementAtOrDefault(1)?.Job == ClusterJobs.Thinking &&
            WithinCapacity(stranded), Report(stranded));
        var keyed = NetworkRecommender.Recommend(Stranded(8) with { Preference = HostingPreference.Balanced, ConfiguredProviders = ["nvidia-build"] });
        Step("5, 8", "A saved free provider key: on a smaller card Thinking uses the free hosted model, so the voice gets the card",
            keyed.Target.Job(ClusterJobs.Thinking)?.OptionId == "hosted:nvidia-build" && keyed.Target.Job(ClusterJobs.Speaking)?.HostId == "desk-1" &&
            keyed.Target.Job(ClusterJobs.LipSync)?.Off == true && WithinCapacity(keyed), Report(keyed));

        // The priority list: every part in order, Off for the optional ones the owner turned off.
        var turnedOff = NetworkRecommender.Recommend(Stranded(12) with { Off = [PlanComponent.Singing, PlanComponent.DeepThinking, PlanComponent.LipSync] });
        var parts = turnedOff.Components;
        Step("13", "Every part in priority order; the parts the owner turned off are Off and their roles go",
            parts.Select(p => p.Component).SequenceEqual(ComponentRanking.All.Select(i => i.Component)) &&
            parts.Where(p => p.OwnerOff).Select(p => p.Component).Order().SequenceEqual(new[] { PlanComponent.LipSync, PlanComponent.DeepThinking, PlanComponent.Singing }.Order()) &&
            parts.Where(p => p.OwnerOff).All(p => !p.On && p.Where.StartsWith("Off:", StringComparison.Ordinal)) &&
            parts.Single(p => p.Component == PlanComponent.Thinking).On &&
            turnedOff.Changes.Single(c => c.RoleKind == "singing").Why.StartsWith("You turned", StringComparison.Ordinal),
            parts.Select(p => $"{p.Rank}. {p.Name}{(p.CanBeOff ? " (optional)" : "")}: {p.Where}"));

        // Rule 6: heavy roles on a companion PC move to a host.
        var heavy = Network(Companion("desk-1", true, Nvidia(12, "RTX 4070")) with
        {
            Roles = [Role("chatterbox", "chatterbox-turbo"), Role("ollama", "gemma4:e2b")]
        }, Host("gpu-box", Nvidia(24, "RTX 4090"))) with
        {
            CurrentJobs =
            [
                new JobPlan(ClusterJobs.Thinking, "desk-1", OptionId: "gemma4:e2b"),
                new JobPlan(ClusterJobs.Speaking, "desk-1", OptionId: "chatterbox-turbo")
            ],
            Wanted = [PlanComponent.Thinking, PlanComponent.Voice]
        };
        var relieved = NetworkRecommender.Recommend(heavy);
        Step("6", "Companion relief: Thinking and the voice leave the companion PC for the host (Improvement)",
            relieved.Target.Machine("desk-1")?.Roles.Count == 0 && relieved.Target.Job(ClusterJobs.Thinking)?.HostId == "gpu-box" &&
            relieved.Changes.Where(c => c.MachineId == "desk-1").All(c => c.Kind == SetupChangeKind.RemoveRole && c.Benefit == SetupChangeBenefit.Improvement),
            Report(relieved));
        Step("12", "Setup order: job by job, Thinking first; each job makes before it breaks (its host role, its move, then the companion PC's role)",
            relieved.Changes.FirstOrDefault() is { Kind: SetupChangeKind.AddRole, RoleKind: "ollama" } &&
            ClusterJobs.All.All(job =>
            {
                var mine = relieved.Changes.Select((c, i) => (c, i)).Where(x => x.c.Job == job || x.c.RoleKind == (job == ClusterJobs.Thinking ? "ollama" : job == ClusterJobs.Speaking ? "chatterbox" : "-")).ToList();
                var add = mine.Where(x => x.c.Kind == SetupChangeKind.AddRole).Select(x => x.i).DefaultIfEmpty(-1).Max();
                var assign = mine.Where(x => x.c.Kind == SetupChangeKind.AssignJob).Select(x => x.i).DefaultIfEmpty(-1).Max();
                var remove = mine.Where(x => x.c.Kind == SetupChangeKind.RemoveRole).Select(x => x.i).DefaultIfEmpty(int.MaxValue).Min();
                return add <= assign && Math.Max(add, assign) < remove;
            }),
            relieved.Changes.Select(c => $"{c.Kind} {c.MachineId} {c.RoleKind ?? c.Job}"));

        // Rule 7: Thinking on the companion PC's own card stays when the only host has no graphics card.
        var slow = NetworkRecommender.Recommend(Network(Companion("desk-1", false, Nvidia(12, "RTX 4070")), Host("cpu-box")) with
        {
            CurrentJobs = [new JobPlan(ClusterJobs.Thinking, null, OptionId: "gemma4:e2b")],
            Wanted = [PlanComponent.Thinking]
        });
        Step("7", "Never add latency: Thinking doesn't move to a host's processor (about 2.5 s instead of 0.15 s)",
            slow.AlreadyOptimal && slow.Target.Job(ClusterJobs.Thinking)?.HostId is null, Report(slow));

        // Rule 8: a hosted Thinking provider the owner chose stays.
        var hostedRequest = Network(Companion("desk-1"), Host("gpu-box", Nvidia(24, "RTX 4090"))) with
        {
            CurrentJobs = [new JobPlan(ClusterJobs.Thinking, null, OptionId: "hosted:openai")],
            ConfiguredProviders = ["openai"], Wanted = [PlanComponent.Thinking]
        };
        var hosted = NetworkRecommender.Recommend(hostedRequest);
        var local = NetworkRecommender.Recommend(hostedRequest with { Preference = HostingPreference.PreferLocal });
        Step("8", "The owner's choices: hosted Thinking stays (Balanced); keeping everything local moves it to the host",
            hosted.AlreadyOptimal && local.Target.Job(ClusterJobs.Thinking)?.HostId == "gpu-box", new { balanced = Report(hosted), preferLocal = Report(local) });

        // Rule 9: the owner left a host out of the Thinking pool.
        var optOut = NetworkRecommender.Recommend(Network(Companion("desk-1"), Host("gpu-a", Nvidia(16, "RTX 4080")), Host("gpu-b", Nvidia(16, "RTX 4080"))) with
        {
            Wanted = [PlanComponent.DeepThinking], ThinkingPoolOptOut = ["gpu-a"]
        });
        Step("9", "The Thinking pool skips the host the owner left out", optOut.Target.ThinkingPool.SequenceEqual(["gpu-b"]), Report(optOut));

        // Rule 10: a computer that isn't answering is not part of the network, from its first missed check.
        NetworkSetupRequest Away(int? minutes) => Network(Companion("desk-1"), Companion("desk-2"), Host("voice-a", Nvidia(12, "RTX 3060")) with
        {
            Online = false, OfflineFor = minutes is { } m ? TimeSpan.FromMinutes(m) : null, Roles = [Role("chatterbox", "chatterbox-turbo")]
        }, Host("voice-b", Nvidia(12, "RTX 3060")) with { Roles = [Role("chatterbox", "chatterbox-turbo")] }) with
        {
            CurrentJobs = [new JobPlan(ClusterJobs.Speaking, "voice-a", OptionId: "chatterbox-turbo") { Pool = ["voice-b"] }],
            Wanted = [PlanComponent.Voice]
        };
        bool Moved(NetworkRecommendation r) => r.Target.Job(ClusterJobs.Speaking)?.HostId == "voice-b" &&
            r.Changes.Any(c => c.Kind == SetupChangeKind.AssignJob && c.Benefit == SetupChangeBenefit.Required) &&
            !r.Changes.Any(c => c.MachineId == "voice-a" && c.Kind is SetupChangeKind.AddRole or SetupChangeKind.RemoveRole);
        var missed = NetworkRecommender.Recommend(Away(null));
        var brief = NetworkRecommender.Recommend(Away(4));
        var gone = NetworkRecommender.Recommend(Away(25));
        Step("10", "Not answering (just now, 4 minutes, 25 minutes): planned without it, Speaking moves (Required), nothing changes there",
            Moved(missed) && Moved(brief) && Moved(gone),
            new { justNow = Report(missed), fourMinutes = Report(brief), twentyFiveMinutes = Report(gone) });

        // Rule 11: stability and determinism.
        var again = NetworkRecommender.Recommend(Apply(fresh, plan));
        var reversed = NetworkRecommender.Recommend(fresh with { Machines = [.. fresh.Machines.Reverse()] });
        Step("11", "Stability: the applied recommendation has nothing to change; the same network in another order gives the same fingerprint",
            again.AlreadyOptimal && again.Fingerprint == plan.Fingerprint && reversed.Fingerprint == plan.Fingerprint &&
            reversed.Changes.Select(c => c.Summary).SequenceEqual(plan.Changes.Select(c => c.Summary)),
            new { plan.Fingerprint, applied = again.Fingerprint, reversed = reversed.Fingerprint, leftOver = again.Changes.Select(c => c.Summary) });

        // The voice fallback: the owner's engine, else Chatterbox Nano on a card, else Nano on the processor, else a hosted
        // voice with a saved key, else a note that Martlet can't speak yet. There are no Windows voices.
        NetworkSetupRequest Lone(bool hostService, params MachineGpu[] gpus) =>
            Network(Companion("desk-1", hostService, gpus)) with { Wanted = [PlanComponent.Voice] };
        string? Option(NetworkRecommendation r) => r.Target.Job(ClusterJobs.Speaking)?.OptionId;
        var smallCard = NetworkRecommender.Recommend(Lone(true, Nvidia(4, "GTX 1650")));
        Step("voice", "Fallback 1: a 4 GB card has no room for Chatterbox Turbo, so Chatterbox Nano speaks on that card",
            Option(smallCard) == FootprintCatalog.FallbackVoiceKind && smallCard.Notes.Any(n => n.Contains("Chatterbox Nano", StringComparison.Ordinal)),
            Report(smallCard));
        var processor = NetworkRecommender.Recommend(Lone(true));
        Step("voice", "Fallback 2: no graphics card, so Chatterbox Nano speaks on the processor (about 8 threads)",
            Option(processor) == "chatterbox-nano-cpu" && processor.Target.Job(ClusterJobs.Speaking)?.HostId == "desk-1", Report(processor));
        var hostedVoice = NetworkRecommender.Recommend(Lone(false) with { ConfiguredProviders = ["openai"] });
        Step("voice", "Fallback 3: no computer can run a voice engine, so the hosted voice with your saved key speaks",
            Option(hostedVoice) == FootprintCatalog.OpenAiVoiceId, Report(hostedVoice));
        var mute = NetworkRecommender.Recommend(Lone(false));
        Step("voice", "Fallback 4: nothing can speak, so a note says how to set up the host service for Chatterbox Nano (never silent)",
            Option(mute) is null && mute.CannotSpeak && mute.Notes.Contains(mute.CannotSpeakNote) &&
                mute.CannotSpeakNote!.Contains("host service", StringComparison.Ordinal) && !processor.CannotSpeak, Report(mute));

        return new { ok, fixture = "built-in fixture networks (NOT real computers)", steps };
    }
}
