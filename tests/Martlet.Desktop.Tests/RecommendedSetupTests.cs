using System.Globalization;
using System.IO;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Installation;
using Martlet.Core.Planning;
using Martlet.Core.Settings;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>Home's Recommended setup: the request builder, the review's words, the declined-setup memory and which companion PC asks.</summary>
public sealed class RecommendedSetupTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 18, 0, 0, TimeSpan.Zero);

    public RecommendedSetupTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    private static AvatarRemoteHost Remote(string id, string ip) => new()
    {
        Origin = $"https://{ip}:9443", HostId = id, SpkiFingerprint = "sha256:" + new string('a', 64),
        DeviceId = "desktop-test", CredentialId = new string('B', 22)
    };

    private static HostHardware Report(string id, string gpu, int vramGb, string? kernel = null) =>
        new(id, $"https://{id}:9443", Now, Now, "docker", "Ubuntu 24.04", kernel, "CPU", 32, 64, "docker", "yes",
            [new HostGpu(gpu, "nvidia", vramGb * 1024, "570")]) { Platform = "linux" };

    /// <summary>This PC (DESK, its own host service desk-host), gpu-box (a Linux host over SSH), diva-host (the host service of
    /// DIVA, a member companion PC), lost-box (paired over SSH without a target, no hardware report) and LAPTOP (a member companion
    /// PC without a host service).</summary>
    private static NetworkInputs Network(ClusterPlan? plan = null, IReadOnlyDictionary<string, HostCheck>? checks = null)
    {
        var machine = new MachineInfo("DESK", "Windows 11", "CPU", 24, 32, [new GpuInfo("NVIDIA GeForce RTX 4080", 16)], "192.168.1.10", true, true);
        PairedHost[] hosts =
        [
            new() { Pairing = Remote("desk-host", "192.168.1.10"), Method = HostSetupMethod.ThisPcDocker },
            new() { Pairing = Remote("gpu-box", "192.168.1.20"), Method = HostSetupMethod.SshDocker, SshTarget = "me@gpu-box" },
            new() { Pairing = Remote("diva-host", "192.168.1.30"), Method = HostSetupMethod.Agent },
            new() { Pairing = Remote("lost-box", "192.168.1.40"), Method = HostSetupMethod.SshDocker }
        ];
        MartletComputer[] computers =
        [
            new("desktop-diva", "DIVA", ComputerStanding.Member, null, true, Role: DeviceRole.Companion, HostId: "diva-host"),
            new("desktop-laptop", "LAPTOP", ComputerStanding.Member, null, true, Role: DeviceRole.Companion),
            new("desktop-guest", "GUEST", ComputerStanding.Outside, null, true, Role: DeviceRole.Companion)
        ];
        checks ??= new Dictionary<string, HostCheck>
        {
            ["desk-host"] = new(true, "Connected", new Dictionary<string, string> { ["ollama"] = "gemma4:12b", ["chatterbox"] = "chatterbox-turbo" }),
            ["gpu-box"] = new(false, "Not reachable"),
            ["diva-host"] = new(true, "Connected", new Dictionary<string, string> { ["chatterbox"] = "chatterbox-turbo" })
        };
        return new NetworkInputs(machine, DeviceRole.Companion, null, null, false, checks,
            [Report("gpu-box", "NVIDIA GeForce RTX 4090", 24), Report("diva-host", "NVIDIA GeForce RTX 3080", 10, "5.15.167.4-microsoft-standard-WSL2")],
            hosts, Plan: plan, Computers: computers);
    }

    private static ClusterPlan Plan() => ClusterPlan.Empty
        .Assign(ClusterJobs.Thinking, "desk-host", false, true, null, "desktop-desk", Now)
        .Assign(ClusterJobs.Speaking, "desk-host", false, true, null, "desktop-desk", Now)
        .Observe("gpu-box", "https://192.168.1.20:9443", [new ClusterNodeRole { Kind = "stt", Model = "whisper-large-v3-turbo" },
            new ClusterNodeRole { Kind = "chatterbox", Model = "chatterbox-turbo" }], false, "desktop-desk", Now);

    private static SetupRequestBuild Build(ClusterPlan? plan = null, WorkSharingSettings? sharing = null, IReadOnlyCollection<string>? optOut = null,
        Func<string, TimeSpan?>? offlineFor = null, IReadOnlyCollection<string>? providers = null) =>
        RecommendedSetupInputs.Request(RecommendedSetupInputs.Sources(Network(plan), null, "desktop-desk", "desk-host", 400, offlineFor, sharing,
            ["diva-host", "gpu-box"], optOut, "chatterbox", providers ?? ["openrouter"]));

    [Fact]
    public void Every_computer_gets_the_cluster_plans_id_and_its_kind()
    {
        var build = Build(Plan());
        var machines = build.Request.Machines;
        Assert.Equal(["desk-host", "gpu-box", "diva-host"], machines.Select(m => m.Specs.Id));
        var desk = machines[0];
        Assert.Equal(NetworkMachineKind.Companion, desk.Kind);
        Assert.Equal("This PC", desk.Specs.Name);
        Assert.True(desk.Specs.IsPrimary);
        Assert.Equal(16, desk.Specs.BestGpuGb);
        Assert.True(desk.HasHostService);
        Assert.True(desk.OnWindows);
        Assert.Equal(["chatterbox", "ollama"], desk.Roles.Select(r => r.Kind));
        // DIVA says it is a companion PC; its host service is its machine.
        var diva = machines.Single(m => m.Specs.Id == "diva-host");
        Assert.Equal(NetworkMachineKind.Companion, diva.Kind);
        Assert.Equal("DIVA", diva.Specs.Name);
        Assert.True(diva.OnWindows);
        var gpu = machines.Single(m => m.Specs.Id == "gpu-box");
        Assert.Equal(NetworkMachineKind.Host, gpu.Kind);
        Assert.False(gpu.Specs.IsPrimary);
        Assert.False(gpu.OnWindows);
        Assert.Equal("This PC", build.Names["desk-host"]);
        Assert.Equal("LAPTOP", build.Names["desktop-laptop"]);
        Assert.DoesNotContain("desktop-guest", build.Names.Keys);
    }

    [Fact]
    public void Computers_without_a_hardware_report_or_host_service_are_left_as_they_are_with_a_note()
    {
        var build = Build(Plan());
        Assert.DoesNotContain(build.Request.Machines, m => m.Specs.Id is "lost-box" or "desktop-laptop");
        Assert.Contains("lost-box hasn't reported its hardware yet, so the recommendation leaves it as it is.", build.Notes);
        Assert.Contains("LAPTOP runs only the parts inside Martlet (no host service), so nothing changes there.", build.Notes);
    }

    [Fact]
    public void Online_offline_time_and_manageable_follow_the_last_checks_and_the_devices_rules()
    {
        var build = Build(Plan(), offlineFor: id => id == "gpu-box" ? TimeSpan.FromMinutes(12) : null);
        var gpu = build.Request.Machines.Single(m => m.Specs.Id == "gpu-box");
        Assert.False(gpu.Online);
        Assert.Equal(TimeSpan.FromMinutes(12), gpu.OfflineFor);
        Assert.True(gpu.Manageable);
        // Not checked: its roles are the shared plan's record.
        Assert.Equal(["chatterbox", "stt"], gpu.Roles.Select(r => r.Kind));
        Assert.True(build.Request.Machines.Single(m => m.Specs.Id == "diva-host").Online);

        // The presence record wins over an older connection check: diva-host answered its last check, but it isn't answering now.
        var away = Build(Plan(), offlineFor: id => id == "diva-host" ? TimeSpan.FromSeconds(40) : null).Request.Machines.Single(m => m.Specs.Id == "diva-host");
        Assert.False(away.Online);
        Assert.Equal(TimeSpan.FromSeconds(40), away.OfflineFor);

        // Paired over SSH without a target: Martlet can't change it from here.
        var sources = RecommendedSetupInputs.Sources(Network(Plan()), null, "desktop-desk", "desk-host", 400);
        Assert.False(sources.Computers.Single(c => c.Id == "lost-box").Manageable);
        Assert.True(sources.Computers.Single(c => c.Id == "desk-host").Manageable);
    }

    [Fact]
    public void This_pc_stays_in_the_plan_when_its_own_host_service_doesnt_answer()
    {
        var checks = new Dictionary<string, HostCheck>
        {
            ["desk-host"] = new(false, "Not reachable"),
            ["diva-host"] = new(true, "Connected", new Dictionary<string, string> { ["chatterbox"] = "chatterbox-turbo" })
        };
        var build = RecommendedSetupInputs.Request(RecommendedSetupInputs.Sources(Network(Plan(), checks), null, "desktop-desk", "desk-host", 400,
            id => id == "desk-host" ? TimeSpan.FromMinutes(3) : null));
        Assert.True(build.Request.Machines.Single(m => m.Specs.Id == "desk-host").Online);
        Assert.Contains("This PC's host service isn't answering, so changes to it wait until it runs again.", build.Notes);
    }

    [Fact]
    public void Todays_jobs_come_from_the_plan_and_a_job_nobody_set_up_is_left_out()
    {
        var jobs = Build(Plan()).Request.CurrentJobs;
        Assert.Equal([ClusterJobs.Thinking, ClusterJobs.Speaking, ClusterJobs.LipSync], jobs.Select(j => j.Job));
        Assert.Equal("desk-host", jobs.Single(j => j.Job == ClusterJobs.Thinking).HostId);
        // Lip-sync: no remote host and not off, so this PC does it.
        Assert.Null(jobs.Single(j => j.Job == ClusterJobs.LipSync).HostId);
        Assert.False(jobs.Single(j => j.Job == ClusterJobs.LipSync).Off);
    }

    [Fact]
    public void A_jobs_pool_is_the_other_computers_running_its_engine_in_sharing_order()
    {
        var speaking = Build(Plan()).Request.CurrentJobs.Single(j => j.Job == ClusterJobs.Speaking);
        // diva-host's last check and gpu-box's plan record both run Chatterbox; desk-host does the job, so it isn't in its pool.
        Assert.Equal(["diva-host", "gpu-box"], speaking.Pool);
        var ordered = new WorkSharingSettings().With(new WorkSharingJob { Job = ClusterJobs.Speaking, Order = ["gpu-box"] });
        Assert.Equal(["gpu-box", "diva-host"], Build(Plan(), ordered).Request.CurrentJobs.Single(j => j.Job == ClusterJobs.Speaking).Pool);
        var unshared = new WorkSharingSettings().With(new WorkSharingJob { Job = ClusterJobs.Speaking, Share = false });
        Assert.Empty(Build(Plan(), unshared).Request.CurrentJobs.Single(j => j.Job == ClusterJobs.Speaking).Pool);
        // Thinking is shared only when chosen.
        Assert.Empty(Build(Plan()).Request.CurrentJobs.Single(j => j.Job == ClusterJobs.Thinking).Pool);
    }

    [Fact]
    public void The_owners_choices_reach_the_request()
    {
        var request = Build(Plan(), optOut: ["gpu-box"]).Request;
        Assert.Equal(["diva-host"], request.CurrentThinkingPool);
        Assert.Equal(["gpu-box"], request.ThinkingPoolOptOut);
        Assert.Equal("chatterbox", request.VoiceEngine);
        Assert.Equal(["openrouter"], request.ConfiguredProviders);
        Assert.Equal(HostingPreference.Balanced, request.Preference);
        Assert.Equal(HostingPreference.PreferLocal, RecommendedSetupInputs.PreferenceFor([]));
    }

    // ---------- the review ----------

    private static NetworkRecommendation Recommendation(SetupRequestBuild build)
    {
        var today = NetworkRecommender.Today(build.Request);
        var target = today with
        {
            Machines = [.. today.Machines.Select(m => m.MachineId switch
            {
                "desk-host" => m with { Roles = [new HostedRolePlacement("ollama", "gemma4:12b", null)] },
                "gpu-box" => m with { Roles = [new HostedRolePlacement("chatterbox", null, 0), new HostedRolePlacement("stt", "whisper-large-v3-turbo", 0)] },
                _ => m
            })],
            Jobs = [.. today.Jobs.Select(j => j.Job == ClusterJobs.Speaking ? j with { HostId = "gpu-box", Pool = ["diva-host"] } : j)]
        };
        return new NetworkRecommendation(today, target,
        [
            new SetupChange(SetupChangeKind.AssignJob, "gpu-box", "gpu-box does Speaking.", "This PC runs games.") { Job = ClusterJobs.Speaking, FromMachineId = "desk-host" },
            new SetupChange(SetupChangeKind.RemoveRole, "desk-host", "Remove Chatterbox Turbo from this PC.", "") { Benefit = SetupChangeBenefit.Minor, RoleKind = "chatterbox" },
            new SetupChange(SetupChangeKind.AddRole, "lost-box", "Install Listening on lost-box.", "Its card is free.")
            {
                Benefit = SetupChangeBenefit.Required, RoleKind = "stt", DownloadGb = 3.5, NeedsSomeoneThere = true
            },
            new SetupChange(SetupChangeKind.AddRole, "gpu-box", "Install Chatterbox Turbo on gpu-box.", "") { RoleKind = "chatterbox", DownloadGb = 8 }
        ]) { Fingerprint = "abc123", Notes = ["gpu-box hasn't answered for 12 minutes."] };
    }

    [Fact]
    public void The_review_lists_changes_by_benefit_with_downloads_and_steps_needing_someone_there()
    {
        var build = Build(Plan());
        var review = RecommendedSetupReview.From(Recommendation(build), build);
        Assert.False(review.AlreadyOptimal);
        Assert.Equal("A better setup is ready for your computers", review.Title);
        Assert.StartsWith("4 changes on 3 computers.", review.Summary);
        // In the order Reconfigure makes them (the recommender's setup order), each with how much it matters.
        Assert.Equal(["Improvement", "Tidier", "Needed", "Improvement"], review.Changes.Select(c => c.Benefit));
        Assert.Equal("gpu-box does Speaking.", review.Changes[0].Summary);
        Assert.Equal(["On lost-box: Install Listening on lost-box."], review.Manual);
        Assert.Equal("Downloads about 12 GB: lost-box 3.5 GB, gpu-box 8 GB.", review.Downloads);
        Assert.Equal(["gpu-box hasn't answered for 12 minutes.", "lost-box hasn't reported its hardware yet, so the recommendation leaves it as it is.",
            "LAPTOP runs only the parts inside Martlet (no host service), so nothing changes there."], review.Notes);
        Assert.Equal("abc123", review.Fingerprint);
    }

    [Fact]
    public void Computers_that_arent_answering_get_one_sentence_instead_of_the_same_words_on_every_change()
    {
        var build = Build(Plan(), offlineFor: id => id == "gpu-box" ? TimeSpan.FromMinutes(155) : null);
        const string speaking = "gpu-box hasn't answered for 155 minutes, so speaking moves.";
        const string lipSync = "gpu-box hasn't answered for 155 minutes, so lip-sync moves.";
        const string away = "gpu-box hasn't answered for 155 minutes, so Martlet plans without it.";
        var recommendation = Recommendation(build) with
        {
            Changes =
            [
                new SetupChange(SetupChangeKind.AssignJob, "desk-host", "This PC does Speaking.", $"{speaking} This PC has room.")
                {
                    Job = ClusterJobs.Speaking, Away = speaking
                },
                new SetupChange(SetupChangeKind.AssignJob, "desk-host", "This PC does lip-sync.", lipSync) { Job = ClusterJobs.Speaking, Away = lipSync }
            ],
            Notes = [away, "Downloads wait for a fast connection."],
            Offline = [new OfflineComputer("gpu-box", TimeSpan.FromMinutes(155), away)]
        };

        var review = RecommendedSetupReview.From(recommendation, build);
        Assert.Equal(["This PC has room.", ""], review.Changes.Select(c => c.Why));
        Assert.Equal("gpu-box hasn't answered for 2 hours, so Martlet plans without it.", review.Offline);
        Assert.Equal("Recommended: left out while it isn't answering", review.Computers.Single(c => c.Id == "gpu-box").Recommended);
        Assert.Equal("gpu-box isn't answering, so Martlet plans without it.", RecommendedSetupReview.OfflineSentence([("gpu-box", TimeSpan.Zero)]));
        Assert.Equal("MIKU and IMOUTO aren't answering, so Martlet plans without them.",
            RecommendedSetupReview.OfflineSentence([("MIKU", TimeSpan.FromSeconds(20)), ("IMOUTO", TimeSpan.FromSeconds(40))]));
        Assert.Contains("Downloads wait for a fast connection.", review.Notes);
        Assert.DoesNotContain(review.Notes, n => n.Contains("hasn't answered", StringComparison.Ordinal));
        // The recommender decides who is away: with nobody away, nothing is condensed.
        Assert.Null(RecommendedSetupReview.From(recommendation with { Offline = [] }, build).Offline);
    }

    [Fact]
    public void The_offline_fixture_review_reads_the_recommenders_fields_not_its_sentences()
    {
        var build = RecommendedSetupInputs.Request(RecommendedSetupInputs.OfflineFixture(DateTimeOffset.UtcNow));
        var recommendation = NetworkRecommender.Recommend(build.Request, FootprintCatalog.Default);
        var review = RecommendedSetupReview.From(recommendation, build);

        Assert.Equal(["imouto-host", "miku-host"], recommendation.Offline.Select(o => o.Id));
        Assert.Matches("^(MIKU and IMOUTO|IMOUTO and MIKU) haven't answered for 2 hours, so Martlet plans without them\\.$", review.Offline);
        Assert.DoesNotContain(review.Changes, c => c.Why.Contains("hasn't answered", StringComparison.Ordinal));
        Assert.DoesNotContain(review.Notes, n => n.Contains("hasn't answered", StringComparison.Ordinal));
        Assert.True(recommendation.CannotReply);
        Assert.True(review.CannotReply);
        Assert.Contains("no free API key is saved", recommendation.CannotReplyNote, StringComparison.Ordinal);
        Assert.DoesNotContain(review.Notes, n => n.Contains("can't reply until", StringComparison.Ordinal));
        Assert.DoesNotContain(review.Changes, c => c.Summary.Contains("Whisper small", StringComparison.Ordinal));
    }

    [Fact]
    public void The_offline_sentence_names_every_computer_and_each_time_only_when_they_differ()
    {
        Assert.Null(RecommendedSetupReview.OfflineSentence([]));
        Assert.Equal("MIKU and IMOUTO haven't answered for 2 hours, so Martlet plans without them.",
            RecommendedSetupReview.OfflineSentence([("MIKU", TimeSpan.FromMinutes(155)), ("IMOUTO", TimeSpan.FromMinutes(150))]));
        Assert.Equal("MIKU (3 hours), IMOUTO (20 minutes) and DIVA (1 minute) haven't answered, so Martlet plans without them.",
            RecommendedSetupReview.OfflineSentence([("MIKU", TimeSpan.FromHours(3)), ("IMOUTO", TimeSpan.FromMinutes(20)), ("DIVA", TimeSpan.FromMinutes(1))]));
    }

    [Fact]
    public void Nobody_doing_thinking_is_the_cant_reply_problem_and_offers_the_free_key_only_without_a_saved_key()
    {
        var build = Build(Plan(), providers: []);
        var today = NetworkRecommender.Today(build.Request) with { Jobs = [new JobPlan(ClusterJobs.Thinking, "desk-host")] };
        var target = today with { Jobs = [new JobPlan(ClusterJobs.Thinking, null)] };
        const string cannot = "No computer has room for a Thinking model, and no free API key is saved. Martlet can't reply until one is set up.";
        var recommendation = new NetworkRecommendation(today, target,
        [
            new SetupChange(SetupChangeKind.AssignJob, "", "Nobody does thinking.", "No computer has room for a Thinking model, and no free API key is saved.")
            {
                Job = ClusterJobs.Thinking
            }
        ]) { Fingerprint = "nobody", Notes = [cannot], CannotReplyNote = cannot };

        var review = RecommendedSetupReview.From(recommendation, build);
        Assert.True(review.CannotReply);
        Assert.True(review.OffersFreeKey);
        Assert.DoesNotContain(cannot, review.Notes);
        Assert.Equal(FreeKeyUse.Thinking, FreeKeyPrompt.Use(review.OffersFreeKey, review.CannotReply));

        // A saved key (OpenRouter here): no prompt, and the problem points to Companion › Thinking.
        Assert.False(RecommendedSetupReview.From(recommendation, Build(Plan())).OffersFreeKey);
        Assert.EndsWith("Choose where Thinking runs in Companion › Thinking.", FreeKeyPrompt.Problem(offerKey: false), StringComparison.Ordinal);
        // The recommender says whether Martlet can reply; the review doesn't read its sentences.
        Assert.False(RecommendedSetupReview.From(recommendation with { CannotReplyNote = null }, build).CannotReply);
    }

    [Fact]
    public void Add_your_key_fills_thinking_only_when_martlet_cant_reply_else_if_thinking_fails()
    {
        Assert.Equal(FreeKeyUse.Fallback, FreeKeyPrompt.Use(offerKey: true, cannotReply: false));
        Assert.Equal(FreeKeyUse.Thinking, FreeKeyPrompt.Use(offerKey: true, cannotReply: true));
        Assert.Equal(FreeKeyUse.None, FreeKeyPrompt.Use(offerKey: false, cannotReply: true));
        Assert.True(FreeKeyPrompt.Shows([]));
        Assert.False(FreeKeyPrompt.Shows(["nvidia-build"]));
        Assert.True(FreeKeyPrompt.IsFree(Martlet.Core.Settings.ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl));
        Assert.False(FreeKeyPrompt.IsFree("https://openrouter.ai/api/v1"));
        Assert.DoesNotContain("graphics card", FreeKeyPrompt.Tip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_review_shows_each_computer_today_and_recommended_and_who_does_each_job()
    {
        var build = Build(Plan());
        var review = RecommendedSetupReview.From(Recommendation(build), build);
        var desk = review.Computers.Single(c => c.Id == "desk-host");
        Assert.Equal("Companion PC, kept light for games", desk.Kind);
        Assert.Equal("Today: Chatterbox Turbo, Thinking (gemma4:12b)", desk.Today);
        Assert.Equal("Recommended: Thinking (gemma4:12b)", desk.Recommended);
        var gpu = review.Computers.Single(c => c.Id == "gpu-box");
        Assert.Equal("Host PC, not answering", gpu.Kind);
        // A computer that isn't answering is never part of the plan.
        Assert.Equal("Recommended: left out while it isn't answering", gpu.Recommended);
        Assert.Equal("Recommended: no change", review.Computers.Single(c => c.Id == "diva-host").Recommended);
        Assert.Contains("Speaking: gpu-box (today: This PC). When it is busy: DIVA.", review.Jobs);
        Assert.Contains("Thinking: This PC.", review.Jobs);
        Assert.Contains("Lip-sync: each companion PC itself.", review.Jobs);
        Assert.Contains("Thinking pool (background thinking): DIVA, gpu-box.", review.Jobs);
    }

    [Fact]
    public void A_job_that_moves_between_options_without_a_host_says_where_it_runs_before_and_after()
    {
        var catalog = new FootprintCatalog(
        [
            new ComponentOption { Id = "hosted:openai", Component = PlanComponent.Thinking, DisplayName = "OpenAI GPT-5 mini", Hosting = OptionHosting.External, ProviderId = "openai" },
            new ComponentOption { Id = "think:e2b", Component = PlanComponent.Thinking, DisplayName = "Gemma 4 E2B", ModelId = "gemma4:e2b", HostRoleKind = "ollama" }
        ]);
        var build = Build(Plan());
        var today = NetworkRecommender.Today(build.Request) with { Jobs = [new JobPlan(ClusterJobs.Thinking, null, OptionId: "hosted:openai")] };
        var target = today with { Jobs = [new JobPlan(ClusterJobs.Thinking, null, OptionId: "think:e2b")] };
        var recommendation = new NetworkRecommendation(today, target,
        [
            new SetupChange(SetupChangeKind.AssignJob, "", "Each companion PC thinks with Gemma 4 E2B in its own Ollama.", "You keep everything on your computers.")
            {
                Job = ClusterJobs.Thinking, OptionId = "think:e2b", DownloadGb = 7.2
            }
        ]) { Fingerprint = "e2b" };

        var review = RecommendedSetupReview.From(recommendation, build, catalog);
        Assert.StartsWith("1 change. ", review.Summary);
        Assert.Equal("your companion PCs", review.Changes.Single().Computer);
        Assert.Equal("Downloads about 7.2 GB: your companion PCs 7.2 GB.", review.Downloads);
        Assert.Contains("Thinking: each companion PC itself (Gemma 4 E2B) (today: OpenAI GPT-5 mini).", review.Jobs);
    }

    [Fact]
    public void An_optimal_setup_reads_so_and_has_nothing_to_change()
    {
        var build = Build(Plan());
        var review = RecommendedSetupReview.From(NetworkRecommender.Recommend(build.Request) with { Changes = [] }, build);
        Assert.True(review.AlreadyOptimal);
        Assert.Equal("Your computers already use the recommended setup.", review.Title);
        Assert.Equal("Martlet checked 3 computers against the recommended setup. Nothing needs to change.", review.Summary);
        Assert.Empty(review.Changes);
        Assert.Null(review.Downloads);
    }

    [Fact]
    public void The_review_draws_the_load_the_recommended_setup_makes()
    {
        var build = Build(Plan());
        var recommendation = Recommendation(build);
        var usage = new MachineUsage("gpu-box", "gpu-box", "linux", false,
            [new GpuUsage(0, "NVIDIA GeForce RTX 4090", GpuVendor.Nvidia, 24, new ResourceGauge(22, 11))],
            new ResourceGauge(60, 6), new ResourceGauge(32, 4), new ResourceGauge(0, 0),
            [new UsageItem(PlanComponent.Voice, "voice:chatterbox-turbo", 0, new ResourceUse(11, 6, 4, 8))]);
        recommendation = recommendation with
        {
            Target = recommendation.Target with { Machines = [.. recommendation.Target.Machines.Select(m => m.MachineId == "gpu-box" ? m with { Usage = usage } : m)] }
        };
        var gpu = RecommendedSetupReview.From(recommendation, build).Computers.Single(c => c.Id == "gpu-box");
        // gpu-box isn't answering, so it has no load today.
        Assert.Equal("Recommended load: 46% graphics memory, 9% memory, 12% processor.", gpu.Load);
        Assert.Equal([CapacityResource.GraphicsMemory, CapacityResource.Memory, CapacityResource.Processor], gpu.Bars.Select(b => b.Resource));
        Assert.Equal(11, gpu.Bars[0].Planned);
    }

    // ---------- parts that are off, and the priority list ----------

    [Fact]
    public void Parts_turned_off_are_kept_on_this_PC_and_reach_the_request()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-recommended-" + Guid.NewGuid().ToString("N"));
        try
        {
            var memory = new RecommendedSetupMemory().WithOff(PlanComponent.Singing, true).WithOff(PlanComponent.LipSync, true)
                .WithOff(PlanComponent.Thinking, true).Decline("abc", Now);
            Assert.Equal([PlanComponent.LipSync, PlanComponent.Singing], memory.OffParts.Order());
            Assert.True(memory.Save(directory));
            var loaded = RecommendedSetupMemory.Load(directory);
            Assert.Equal([PlanComponent.LipSync, PlanComponent.Singing], loaded.OffParts.Order());
            Assert.True(loaded.WasDeclined("abc"));
            Assert.Equal([PlanComponent.Singing], loaded.WithOff(PlanComponent.LipSync, false).OffParts);
            // A name Martlet doesn't know, or a part that can't be off, is dropped.
            File.WriteAllText(Path.Combine(directory, RecommendedSetupMemory.FileName), """{ "Off": ["Pictures", "Thinking", "Nonsense"] }""");
            Assert.Equal([PlanComponent.Pictures], RecommendedSetupMemory.Load(directory).OffParts);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        var sources = RecommendedSetupInputs.Sources(Network(Plan()), null, "desktop-desk", "desk-host", 400, off: [PlanComponent.DeepThinking]);
        Assert.Equal([PlanComponent.DeepThinking], RecommendedSetupInputs.Request(sources).Request.Off);
    }

    [Fact]
    public void The_review_lists_every_part_in_priority_order_with_off_for_the_optional_ones()
    {
        var build = Build(Plan());
        var recommendation = Recommendation(build) with
        {
            Components =
            [
                new ComponentStatus(PlanComponent.Thinking, 1, ComponentNecessity.Required, true, "Gemma 4 E2B in Ollama on This PC's RTX 4080", "Fast."),
                new ComponentStatus(PlanComponent.Singing, 7, ComponentNecessity.Optional, false, "Off: Martlet doesn't sing.", "You turned singing off.") { OwnerOff = true }
            ]
        };

        var parts = RecommendedSetupReview.From(recommendation, build).Parts;

        Assert.Equal("1. Thinking (needed): Gemma 4 E2B in Ollama on This PC's RTX 4080.", parts[0].Text);
        Assert.False(parts[0].OffChoice);
        Assert.Equal("7. Singing (optional): Off: Martlet doesn't sing.", parts[1].Text);
        Assert.True(parts[1].OffChoice);
        Assert.True(parts[1].OwnerOff);
        Assert.Equal("Singing", parts[1].Key);
    }

    [Fact]
    public void A_part_set_on_its_Companion_page_is_optional_but_has_no_Off_in_the_review()
    {
        var build = Build(Plan());
        var recommendation = Recommendation(build) with
        {
            Components =
            [
                new ComponentStatus(PlanComponent.Reading, 7, ComponentNecessity.Optional, true, "Windows OCR inside Martlet on this PC's processor",
                    "It is fast and free, and nothing leaves this PC.")
            ]
        };

        var part = RecommendedSetupReview.From(recommendation, build).Parts.Single();

        Assert.Equal("7. Reading (optional): Windows OCR inside Martlet on this PC's processor.", part.Text);
        Assert.False(part.OffChoice);
        // The review's Off never keeps a part this PC sets on its page.
        Assert.Empty(new RecommendedSetupMemory().WithOff(PlanComponent.Reading, true).WithOff(PlanComponent.Vision, true).OffParts);
        var sources = RecommendedSetupInputs.Sources(Network(Plan()), null, "desktop-desk", "desk-host", 400, off: [PlanComponent.SmartHome, PlanComponent.Pictures]);
        Assert.Equal([PlanComponent.Pictures], RecommendedSetupInputs.Request(sources).Request.Off);
    }

    [Fact]
    public void This_PCs_page_choices_reach_the_request()
    {
        var senses = new SenseModels
        {
            Image = new SenseModel
            {
                Source = SenseSource.Own,
                Own = new DeepThinkingSettings { Place = DeepThinkingPlace.Endpoint, Origin = "http://127.0.0.1:11434/v1", ModelId = "qwen2.5vl:7b" }
            },
            Audio = new SenseModel
            {
                Source = SenseSource.Own,
                Own = new DeepThinkingSettings { Place = DeepThinkingPlace.Endpoint, Origin = "https://api.example.com/v1", ModelId = "omni-1" }
            }
        };
        var choices = RecommendedSetupInputs.Choices(watch: true, hearVoice: null, senses,
            new Martlet.Core.Reading.ReadingSettings { Place = Martlet.Core.Reading.ReadingPlace.Host, HostId = "cpu-box" },
            "http://homeassistant.local:8123").ToDictionary(c => c.Component);

        Assert.Equal([PlanComponent.Vision, PlanComponent.Reading, PlanComponent.Hearing, PlanComponent.SmartHome], choices.Keys);
        Assert.Equal("vision:qwen2.5vl:7b", choices[PlanComponent.Vision].OptionId);
        Assert.True(choices[PlanComponent.Hearing].On);
        Assert.Null(choices[PlanComponent.Hearing].OptionId);
        Assert.Equal("omni-1, online (api.example.com)", choices[PlanComponent.Hearing].Where);
        Assert.Equal("reading:rapidocr", choices[PlanComponent.Reading].OptionId);
        Assert.Equal("cpu-box", choices[PlanComponent.Reading].HostId);
        Assert.Equal("Your own Home Assistant at homeassistant.local", choices[PlanComponent.SmartHome].Where);

        var defaults = RecommendedSetupInputs.Choices(watch: false, hearVoice: false, new SenseModels(), new Martlet.Core.Reading.ReadingSettings(), "")
            .ToDictionary(c => c.Component);
        Assert.False(defaults[PlanComponent.Vision].On);
        Assert.Equal("vision:thinking", defaults[PlanComponent.Vision].OptionId);
        Assert.False(defaults[PlanComponent.Hearing].On);
        Assert.Equal("reading:windows-ocr", defaults[PlanComponent.Reading].OptionId);
        Assert.False(defaults[PlanComponent.SmartHome].On);

        var sources = RecommendedSetupInputs.Sources(Network(Plan()), null, "desktop-desk", "desk-host", 400, choices: [.. choices.Values]);
        var request = RecommendedSetupInputs.Request(sources).Request;
        Assert.Equal(4, request.Choices.Count);
        Assert.True(request.CompanionPcs >= 1);
    }

    [Fact]
    public void A_companion_PC_card_says_what_it_does_itself()
    {
        var machine = new NetworkMachine(MachineSpecs.ThisPc([new MachineGpu("RTX 4070", GpuVendor.Nvidia, 12)], 32, 16), NetworkMachineKind.Companion)
        {
            HasHostService = true
        };
        JobPlan[] jobs =
        [
            new(ClusterJobs.Thinking, null, OptionId: "gemma4:e4b"),
            new(ClusterJobs.Listening, null, OptionId: "parakeet-tdt-0.6b-v3-cpu"),
            new(ClusterJobs.Speaking, "this-pc", OptionId: "chatterbox-turbo"),
            new(ClusterJobs.LipSync, null, Off: true)
        ];
        Assert.Equal("Chatterbox Turbo; on the PC itself: Thinking (Gemma 4 E4B in Ollama), Listening (Parakeet on the processor)",
            RecommendedSetupReview.Runs([new HostedRolePlacement("chatterbox", "chatterbox-turbo", null)], machine, jobs, FootprintCatalog.Default));
        Assert.Equal("on the PC itself: Thinking (Gemma 4 E4B in Ollama), Listening (Parakeet on the processor)",
            RecommendedSetupReview.Runs([], machine, jobs, FootprintCatalog.Default));
    }

    // ---------- declined setups and who asks ----------

    [Fact]
    public void Declined_setups_are_kept_per_PC_newest_first()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-recommended-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(RecommendedSetupMemory.Load(directory).WasDeclined("abc"));
            var memory = new RecommendedSetupMemory();
            for (var i = 0; i < 25; i++) memory = memory.Decline($"setup-{i}", Now.AddMinutes(i));
            memory = memory.Decline("setup-3", Now.AddHours(1));
            Assert.True(memory.Save(directory));
            var loaded = RecommendedSetupMemory.Load(directory);
            Assert.Equal(RecommendedSetupMemory.Kept, loaded.Declined.Count);
            Assert.Equal("setup-3", loaded.Declined[0].Fingerprint);
            Assert.True(loaded.WasDeclined("setup-24"));
            Assert.False(loaded.WasDeclined("setup-0"));
            Assert.False(loaded.WasDeclined(""));
            File.WriteAllText(Path.Combine(directory, RecommendedSetupMemory.FileName), "{ not json");
            Assert.Empty(RecommendedSetupMemory.Load(directory).Declined);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Only_a_companion_PC_someone_uses_asks_about_a_setup_worth_asking_and_not_declined()
    {
        var build = Build(Plan());
        var worth = Recommendation(build);
        var minor = worth with { Changes = [.. worth.Changes.Select(c => c with { Benefit = SetupChangeBenefit.Minor })] };
        var optimal = worth with { Changes = [] };
        var memory = new RecommendedSetupMemory();
        var active = TimeSpan.FromMinutes(2);

        Assert.Equal(SetupAskStep.Ask, SetupAskRule.Decide(worth, memory, companion: true, active).Step);
        Assert.Equal(SetupAskStep.Wait, SetupAskRule.Decide(worth, memory, companion: true, SetupAskRule.InUse).Step);
        Assert.Equal(SetupAskStep.Nothing, SetupAskRule.Decide(worth, memory, companion: false, active).Step);
        Assert.Equal(SetupAskStep.Nothing, SetupAskRule.Decide(minor, memory, companion: true, active).Step);
        Assert.Equal(SetupAskStep.Nothing, SetupAskRule.Decide(optimal, memory, companion: true, active).Step);
        var declined = memory.Decline(worth.Fingerprint, Now);
        Assert.Equal((SetupAskStep.Nothing, "you declined this setup on this PC before"), SetupAskRule.Decide(worth, declined, companion: true, active));
        // Something changed: a new recommended setup is asked about again.
        Assert.Equal(SetupAskStep.Ask, SetupAskRule.Decide(worth with { Fingerprint = "def456" }, declined, companion: true, active).Step);
    }
}
