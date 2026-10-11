using Martlet.Audio;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Planning;
using Martlet.Core.Settings;
using Martlet.Mcp.Shared;

namespace Martlet.Mcp;

/// <summary>situation_status and situation_check: the While gaming and Host away plans (docs/RECOMMENDATION_DESIGN.md, stage 2).
/// The status reads situation.json, which the desktop writes when the situation changes on a companion PC. The check rehearses
/// the production rules (LiveSituations), the routes a situation moves live Thinking to (SituationRoutes), the game watch
/// (GameWatch, on FIXTURE apps and a simulated clock) and Recommended setup's three plans (SituationPlans) on a FIXTURE network.
/// Nothing is played, contacted or saved.</summary>
internal static class SituationCheck
{
    internal static object Status(string dataDirectory)
    {
        var report = SituationReport.Load(dataDirectory);
        return new
        {
            report = report is null ? "none: the desktop writes situation.json when it starts on a companion PC and when the situation changes" : "loaded",
            updatedAt = report?.UpdatedAt,
            situation = report?.Situation.ToString(),
            title = report is null ? null : LiveSituations.Title(report.Situation),
            normal = report?.Normal,
            now = report?.Now,
            nowKind = report?.NowKind?.ToString(),
            nowHost = report?.NowHost,
            order = report?.Order ?? [],
            why = report?.Why,
            applied = report?.Applied,
            playsGames = report?.PlaysGames,
            game = report?.Game,
            away = report?.Away ?? [],
            backupAllowed = report?.BackupAllowed,
            online = report?.Online,
            card = report?.Card,
            simulated = report?.Simulated,
            rules = Rules()
        };
    }

    private static object Rules() => new
    {
        gameLookSeconds = GameWatch.Every.TotalSeconds,
        gameHoldSeconds = LiveSituations.GameHold.TotalSeconds,
        hostMissingAfterSeconds = PresenceWatch.MissingAfter.TotalSeconds,
        hostBackAfterSeconds = PresenceWatch.BackAfter.TotalSeconds,
        shortOfMemoryGb = LiveSituations.ShortBytes / 1073741824d,
        order = "While gaming: a host that answers, the hosted backup (If Thinking fails) unless online services are Never, then this " +
            "PC's own model. Host away: another host, the allowed hosted backup, then this PC's own model in Ollama. The model on a " +
            "gaming PC's card stays loaded while the game leaves room; Martlet unloads it only when the card is short of memory.",
        switches = "An open conversation switches between replies; a reply in progress keeps its route."
    };

    internal static object Run()
    {
        var steps = new List<object>();
        var ok = true;
        void Step(string name, bool passed, object detail)
        {
            ok &= passed;
            steps.Add(new { step = name, passed, detail });
        }

        var gpuBox = new LiveHost("gpu-box", "gpu-box", "qwen3:8b");
        var desk = new LiveHost("desk-host", "desk-host", "gemma4:e4b");
        var gaming = new SituationFacts
        {
            Home = LiveHome.ThisPc, HomeModel = "gemma4:e2b", PlaysGames = true, Hosts = [gpuBox], Backup = "NVIDIA Build",
            BackupModel = "nvidia/model", LocalModel = "gemma4:e2b"
        };
        var host = new SituationFacts
        {
            Home = LiveHome.Host, HomeHost = "miku", HomeName = "MIKU", HomeModel = "qwen3:14b", Hosts = [desk], Backup = "NVIDIA Build",
            BackupModel = "nvidia/model", LocalModel = "gemma4:e2b"
        };
        void Decides(string name, SituationFacts facts, Situation expected, LivePlaceKind? kind, string? where, bool moves)
        {
            var decision = LiveSituations.Decide(facts);
            Step(name, decision.Situation == expected && decision.Place?.Kind == kind && (where is null || decision.Place?.Text.Contains(where) == true) &&
                decision.Moves == moves,
                new { situation = decision.Title, now = decision.Place?.Text, moves = decision.Moves, order = decision.Order.Select(p => p.Text), why = decision.Why });
        }
        Decides("normal: Thinking on this PC's card, no game", gaming, Situation.Normal, null, null, false);
        Decides("a game on a PC used for games: live Thinking moves to a host that answers", gaming with { Game = "ELDEN RING" },
            Situation.Gaming, LivePlaceKind.Host, "gpu-box (qwen3:8b)", true);
        Decides("a game, the host is away: the hosted backup", gaming with { Game = "ELDEN RING", Away = ["gpu-box"] },
            Situation.Gaming, LivePlaceKind.Hosted, "NVIDIA Build", true);
        Decides("a game, the host is away, online services Never: this PC's own model stays", gaming with
        {
            Game = "ELDEN RING", Away = ["gpu-box"], BackupAllowed = false
        }, Situation.Gaming, LivePlaceKind.ThisPc, "on the processor when the game needs", false);
        Decides("a game on a PC not used for games: nothing moves", gaming with { Game = "ELDEN RING", PlaysGames = false },
            Situation.Normal, null, null, false);
        Decides("the game ended: live Thinking comes back", gaming with { Game = null }, Situation.Normal, null, null, false);
        Decides("Thinking on a host, a game here: nothing moves", host with { Game = "ELDEN RING", PlaysGames = true }, Situation.Normal, null, null, false);
        Decides("the host is away: another host", host with { Away = ["miku"] }, Situation.HostAway, LivePlaceKind.Host, "desk-host (gemma4:e4b)", true);
        Decides("the host is away, no other host: the hosted backup", host with { Away = ["miku"], Hosts = [] },
            Situation.HostAway, LivePlaceKind.Hosted, "NVIDIA Build", true);
        Decides("the host is away, no other host, online services Never: this PC's own model", host with
        {
            Away = ["miku"], Hosts = [], BackupAllowed = false
        }, Situation.HostAway, LivePlaceKind.ThisPc, "this PC's gemma4:e2b", true);
        Decides("the host is away and nothing else can think: Martlet waits for it", host with
        {
            Away = ["miku"], Hosts = [], Backup = null, LocalModel = null
        }, Situation.HostAway, null, null, false);
        Decides("the host answers again: live Thinking comes back", host, Situation.Normal, null, null, false);

        Step("the card is short of memory only below 1 GB free", LiveSituations.ShortOfMemory(12L << 30, (12L << 30) - (512L << 20)) &&
            !LiveSituations.ShortOfMemory(12L << 30, 9L << 30) && !LiveSituations.ShortOfMemory(0, 0),
            new { twelveGbWithHalfGbFree = true, twelveGbWithThreeGbFree = false });

        Step("the routes a situation moves live Thinking to are valid and the same each time", Routes(out var routes), routes);
        Step("the game watch finds a game, keeps it through a switch to another window and ends it after the hold", Watch(out var watch), watch);
        Step("Recommended setup's three plans on a FIXTURE network", Plans(out var plans), plans);
        return new { ok, scene = "FIXTURE facts, apps and computers on a simulated clock (no real game, host or model)", rules = Rules(), steps };
    }

    private static bool Routes(out object detail)
    {
        try
        {
            var local = SituationRoutes.Local("http://127.0.0.1:11434/v1", "gemma4:e2b");
            var fallback = new ThinkingFallbackSettings
            {
                Origin = ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, ModelId = ChatCompletionsEndpointCatalog.NvidiaBuildDefaultModelId,
                CredentialId = Guid.Parse("6f2c7d1e-55a0-4b8e-9a51-1f0e3c2d4b6a"), ConfigurationRevision = Guid.Parse("0b6e5f2a-8c34-4d1e-a7f9-3c5d2e1b0a98")
            };
            var hosted = SituationRoutes.Hosted(fallback);
            var named = SelfHostSetup.Gateway(SetupRouteType.GatewayOllama);
            var snapshot = new HostRoute(named.RouteId, named.Path, named.ContractId, "1.0", "fixture-destination",
                "fixture-worker", "1.0.0", "qwen3-8b", "fixture-revision", new string('a', 64), "sha256:" + new string('b', 64), 5_700_000, 65_536, 65_536,
                65_536, 4_094, 4_194_304, TimeSpan.FromMinutes(10), "request_abort").Snapshot(SetupRouteType.GatewayOllama);
            var endpoint = new GatewayEndpointSettings
            {
                SchemaVersion = 1, Origin = "https://192.168.1.20:8443", HostId = "gpu-box", SpkiFingerprint = "sha256:" + new string('c', 64),
                DeviceRole = SelfHostSetup.GatewayRole
            };
            var onHost = SituationRoutes.Host(endpoint, Guid.Parse("5a1d2c3b-4e5f-4a6b-8c7d-9e0f1a2b3c4d"), "desk-1", snapshot);
            foreach (var route in new[] { local, hosted, onHost }) route.Validate();
            var same = SituationRoutes.Local("http://127.0.0.1:11434/v1", "gemma4:e2b") == local &&
                SituationRoutes.Hosted(fallback) == hosted;
            var scope = CredentialBinding.For(Guid.Parse("11111111-2222-4333-8444-555555555555"), hosted, fallback.CredentialId!.Value) ==
                fallback.Binding(Guid.Parse("11111111-2222-4333-8444-555555555555"), fallback.CredentialId.Value);
            detail = new
            {
                local = new { type = local.RouteType.ToString(), origin = local.Origin, model = local.ModelId, key = local.CredentialId is not null },
                hosted = new { type = hosted.RouteType.ToString(), origin = hosted.Origin, model = hosted.ModelId, sameKeyScopeAsIfThinkingFails = scope },
                host = new { type = onHost.RouteType.ToString(), hostId = onHost.Gateway?.HostId, model = onHost.ModelId, consented = onHost.Consent == onHost.Selection() },
                sameEachTime = same
            };
            return same && scope && onHost.Consent == onHost.Selection() && hosted.Consent == hosted.Selection();
        }
        catch (Exception error) when (error is ContractException or ArgumentException or FormatException)
        {
            detail = new { error = error.Message };
            return false;
        }
    }

    private static bool Watch(out object detail)
    {
        var clock = new PcAudioCheck.SimulatedClock();
        var source = new FixtureApps();
        var changes = new List<string>();
        using var watch = new GameWatch(() => source, clock, manual: true);
        watch.Changed += game => changes.Add(game ?? "(ended)");
        watch.On = true;
        var seen = new List<object>();
        void Look(string scene)
        {
            watch.Look();
            seen.Add(new { scene, game = watch.Game });
        }
        source.Front = "browser";
        Look("a YouTube video in the browser");
        var none = watch.Game is null;
        source.Front = "game";
        Look("ELDEN RING full screen from a Steam library");
        var found = watch.Game == "ELDEN RING";
        source.Front = "browser";
        clock.Now += TimeSpan.FromSeconds(60).Ticks;
        Look("60 s later the browser is in front, the game makes no sound");
        var held = watch.Game == "ELDEN RING";
        clock.Now += TimeSpan.FromSeconds(31).Ticks;
        Look("91 s after the game was last seen");
        var ended = watch.Game is null;
        detail = new { looks = seen, changes, holdSeconds = LiveSituations.GameHold.TotalSeconds };
        return none && found && held && ended && changes.SequenceEqual(["ELDEN RING", "(ended)"]);
    }

    private static bool Plans(out object detail)
    {
        // desk, a companion PC used for games, thinks with Gemma 4 E2B on its own card; gpu-box runs qwen3:8b and the voice.
        var deskPc = new NetworkMachine(new("desk", "DESK") { Gpus = [new("RTX 4070", GpuVendor.Nvidia, 12)], KeepGpuForGames = true, IsPrimary = true },
            NetworkMachineKind.Companion) { HasHostService = true };
        var box = new NetworkMachine(new("gpu-box", "gpu-box") { Gpus = [new("RTX 4090", GpuVendor.Nvidia, 24)] }, NetworkMachineKind.Host)
            { HasHostService = true };
        var request = new NetworkSetupRequest([deskPc, box]);
        var target = new NetworkSetup(
            [new("desk", NetworkMachineKind.Companion, []), new("gpu-box", NetworkMachineKind.Host, [new("ollama", "qwen3:8b", 0), new("chatterbox", null, 0)])],
            [
                new(ClusterJobs.Thinking, null, OptionId: "gemma4:e2b"),
                new(ClusterJobs.Speaking, "gpu-box"),
                new(ClusterJobs.Listening, null, OptionId: "parakeet-tdt-0.6b-v3-cpu")
            ]);
        var plans = SituationPlans.For(request, new NetworkRecommendation(target, target, []), id => id switch
        {
            "desk" => "DESK",
            null => "your companion PCs",
            _ => id
        }, backup: "NVIDIA Build");
        var gaming = plans.FirstOrDefault(p => p.Situation == Situation.Gaming)?.Lines ?? [];
        var away = plans.FirstOrDefault(p => p.Situation == Situation.HostAway)?.Lines ?? [];
        var thinking = gaming.Any(l => l.Contains("Thinking moves to gpu-box (qwen3:8b), then NVIDIA Build, then DESK's own model", StringComparison.Ordinal));
        var voice = away.Any(l => l.Contains("If gpu-box doesn't answer: Speaking waits for it", StringComparison.Ordinal));
        // The fixture network Recommended setup plans in recommended_setup_status, its companion PCs used for games.
        var build = RecommendedSetupInputs.Request(RecommendedSetupInputs.Fixture(DateTimeOffset.UtcNow));
        var recommendation = NetworkRecommender.Recommend(build.Request, FootprintCatalog.Default);
        var gamers = build.Request with
        {
            Machines = [.. build.Request.Machines.Select(m => m.Kind == NetworkMachineKind.Companion
                ? m with { Specs = m.Specs with { KeepGpuForGames = true } } : m)]
        };
        var fixture = SituationPlans.For(gamers, recommendation, id => id is null ? "your companion PCs" : build.Names.GetValueOrDefault(id) ?? id,
            backup: "NVIDIA Build");
        detail = new
        {
            plans = plans.Select(p => new { situation = p.Situation.ToString(), title = p.Title, lines = p.Lines }),
            fixtureNetwork = fixture.Select(p => new { situation = p.Situation.ToString(), title = p.Title, lines = p.Lines })
        };
        return plans.Count == 3 && thinking && voice && fixture.Count == 3 && fixture.All(p => p.Lines.Count > 0);
    }

    // A browser playing a YouTube video, or ELDEN RING from a Steam library in front and full screen (silent once in the background).
    private sealed class FixtureApps : IPcActivitySource
    {
        internal string Front { get; set; } = "browser";

        public IReadOnlyList<PcAppLevel> Levels() => [new("chrome", 0.2f), new("eldenring", Front == "game" ? 0.3f : 0f)];

        public IReadOnlyList<PcAppFacts> Facts(IReadOnlyCollection<string> apps) =>
        [
            new("chrome", Titles: ["Lofi beats - YouTube - Google Chrome"], Foreground: Front == "browser"),
            new("eldenring", @"D:\SteamLibrary\steamapps\common\ELDEN RING\Game\eldenring.exe", ["ELDEN RING\u2122"],
                Foreground: Front == "game", FullScreen: Front == "game", Gpu: Front == "game" ? 90 : 5)
        ];

        public void Dispose() { }
    }
}
