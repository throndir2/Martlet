using Martlet.Core.Cluster;
using Martlet.Core.Planning;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

// Situations (docs/RECOMMENDATION_DESIGN.md, stage 2): While gaming and Host away move live Thinking, and come back.
public sealed class LiveSituationsTests
{
    private static readonly LiveHost GpuBox = new("gpu-box", "gpu-box", "qwen3-8b");
    private static readonly LiveHost DeskHost = new("desk-host", "desk-host", "gemma4-e4b");

    private static SituationFacts Gaming => new()
    {
        Home = LiveHome.ThisPc, HomeModel = "gemma4:e2b", PlaysGames = true, Hosts = [GpuBox], Backup = "NVIDIA Build",
        BackupModel = "nvidia/model", LocalModel = "gemma4:e2b"
    };

    private static SituationFacts OnHost => new()
    {
        Home = LiveHome.Host, HomeHost = "miku", HomeName = "MIKU", HomeModel = "qwen3-14b", Hosts = [DeskHost], Backup = "NVIDIA Build",
        BackupModel = "nvidia/model", LocalModel = "gemma4:e2b"
    };

    [Fact]
    public void A_game_moves_live_thinking_to_a_host_then_the_backup_then_keeps_this_pc()
    {
        Assert.Equal(Situation.Normal, LiveSituations.Decide(Gaming).Situation);

        var host = LiveSituations.Decide(Gaming with { Game = "ELDEN RING" });
        Assert.Equal((Situation.Gaming, LivePlaceKind.Host, "gpu-box", true), (host.Situation, host.Place!.Kind, host.Place.HostId, host.Moves));
        Assert.Equal([LivePlaceKind.Host, LivePlaceKind.Hosted, LivePlaceKind.ThisPc], host.Order.Select(p => p.Kind));
        Assert.Contains("comes back when the game ends", host.Why);

        var backup = LiveSituations.Decide(Gaming with { Game = "ELDEN RING", Away = ["gpu-box"] });
        Assert.Equal((LivePlaceKind.Hosted, "NVIDIA Build"), (backup.Place!.Kind, backup.Place.Name));

        var never = LiveSituations.Decide(Gaming with { Game = "ELDEN RING", Away = ["gpu-box"], BackupAllowed = false });
        Assert.Equal((LivePlaceKind.ThisPc, true, false), (never.Place!.Kind, never.Place.CardKept, never.Moves));
        Assert.Contains("Online services are set to Never", never.Why);
    }

    [Fact]
    public void Nothing_moves_on_a_pc_not_used_for_games_or_when_thinking_runs_elsewhere()
    {
        Assert.Equal(Situation.Normal, LiveSituations.Decide(Gaming with { Game = "ELDEN RING", PlaysGames = false }).Situation);
        Assert.Equal(Situation.Normal, LiveSituations.Decide(OnHost with { Game = "ELDEN RING", PlaysGames = true }).Situation);
        Assert.Equal(Situation.Normal, LiveSituations.Decide(new SituationFacts { Home = LiveHome.Online, Game = "x", PlaysGames = true }).Situation);
    }

    [Fact]
    public void A_host_away_uses_another_host_the_backup_then_this_pc_and_comes_back()
    {
        var other = LiveSituations.Decide(OnHost with { Away = ["miku"] });
        Assert.Equal((Situation.HostAway, LivePlaceKind.Host, "desk-host"), (other.Situation, other.Place!.Kind, other.Place.HostId));
        Assert.Contains("until MIKU answers again", other.Why);

        Assert.Equal(LivePlaceKind.Hosted, LiveSituations.Decide(OnHost with { Away = ["miku", "desk-host"] }).Place!.Kind);

        var local = LiveSituations.Decide(OnHost with { Away = ["miku"], Hosts = [], BackupAllowed = false });
        Assert.Equal((LivePlaceKind.ThisPc, "gemma4:e2b", true), (local.Place!.Kind, local.Place.Model, local.Moves));

        var nothing = LiveSituations.Decide(OnHost with { Away = ["miku"], Hosts = [], Backup = null, LocalModel = null });
        Assert.Equal((Situation.HostAway, false), (nothing.Situation, nothing.Moves));
        Assert.Null(nothing.Place);

        Assert.Equal(Situation.Normal, LiveSituations.Decide(OnHost).Situation);
    }

    [Fact]
    public void The_card_is_short_of_memory_below_one_gigabyte_free()
    {
        Assert.True(LiveSituations.ShortOfMemory(12L << 30, (12L << 30) - (512L << 20)));
        Assert.False(LiveSituations.ShortOfMemory(12L << 30, 9L << 30));
        Assert.False(LiveSituations.ShortOfMemory(0, 0));
    }

    [Fact]
    public void The_moved_route_replaces_the_saved_thinking_route_only_while_it_is_the_one_decided_for()
    {
        var settings = ChatCompletionsSetup.SelectRoute(SetupSettings.Begin(null), "http://127.0.0.1:11434/v1", "gemma4:e2b");
        var saved = settings.Setup!.Routes.Single(r => r.Role == SetupRole.Llm);
        var fallback = new ThinkingFallbackSettings
        {
            Origin = ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, ModelId = ChatCompletionsEndpointCatalog.NvidiaBuildDefaultModelId,
            CredentialId = Guid.NewGuid(), ConfigurationRevision = Guid.NewGuid()
        };
        var hosted = SituationRoutes.Hosted(fallback);
        Assert.Equal(hosted, SituationRoutes.Hosted(fallback));
        Assert.Equal(hosted.Selection(), hosted.Consent);
        var profile = Guid.NewGuid();
        Assert.Equal(fallback.Binding(profile, fallback.CredentialId!.Value), CredentialBinding.For(profile, hosted, fallback.CredentialId.Value));

        var moved = SituationRoutes.Apply(settings, new("k", saved.ConfigurationRevision, hosted, "NVIDIA Build"));
        Assert.Equal(hosted, moved.Setup!.Routes.Single(r => r.Role == SetupRole.Llm));
        moved.Validate();

        Assert.Same(settings, SituationRoutes.Apply(settings, new("k", Guid.NewGuid(), hosted, "NVIDIA Build")));
        Assert.Same(settings, SituationRoutes.Apply(settings, null));

        // A hosted route made from If Thinking fails does nothing after the owner changed it (a new key, another endpoint).
        var withBackup = settings with { ThinkingFallback = fallback };
        var bound = new SituationOverride("k", saved.ConfigurationRevision, hosted, "NVIDIA Build") { Fallback = fallback };
        Assert.Equal(hosted, SituationRoutes.Apply(withBackup, bound).Setup!.Routes.Single(r => r.Role == SetupRole.Llm));
        var newKey = withBackup with { ThinkingFallback = fallback with { CredentialId = Guid.NewGuid() } };
        Assert.Same(newKey, SituationRoutes.Apply(newKey, bound));
    }

    [Fact]
    public void A_host_route_is_enabled_with_the_pairing_and_consented()
    {
        var endpoint = new GatewayEndpointSettings
        {
            SchemaVersion = 1, Origin = "https://192.168.1.20:9443", HostId = "gpu-box", SpkiFingerprint = "sha256:" + new string('a', 64),
            DeviceRole = SelfHostSetup.GatewayRole
        };
        var named = SelfHostSetup.Gateway(SetupRouteType.GatewayOllama);
        var snapshot = new GatewayRouteSnapshot
        {
            SchemaVersion = 1, RouteType = SetupRouteType.GatewayOllama, RegistryId = SelfHostSetup.RegistryId,
            RegistryVersion = SelfHostSetup.RegistryVersion, RouteId = named.RouteId, Path = named.Path, ContractId = named.ContractId,
            ContractVersion = "1.0", DestinationId = "host", WorkerId = "relay", WorkerPackageRevision = "1.0.0", AdapterVersion = "1.0.0",
            ModelId = "qwen3-8b", ModelRevision = "r1", ModelSha256 = new string('c', 64), ArtifactIdentitySha256 = "sha256:" + new string('d', 64),
            MaximumRequestBytes = 1_400_000, MaximumInputBytes = 960_000, MaximumOutputBytes = 16_384, MaximumEventBytes = 16_384,
            MaximumEvents = 16, MaximumStreamBytes = 262_144, MaximumDurationSeconds = 60, Cancellation = GatewayCancellationMode.RequestAbort,
            ObservedAtUtc = DateTimeOffset.UtcNow, ProbeRevision = Guid.NewGuid()
        };
        var route = SituationRoutes.Host(endpoint, Guid.NewGuid(), "desktop-test", snapshot);
        route.Validate();
        Assert.Equal((SetupRouteType.GatewayOllama, true, "qwen3-8b"), (route.RouteType!.Value, route.Enabled == true, route.ModelId));
        Assert.Equal(route.Selection(), route.Consent);
    }

    [Fact]
    public void Recommended_setup_shows_three_plans_with_the_same_order()
    {
        var desk = new NetworkMachine(new("desk", "DESK") { Gpus = [new("RTX 4070", GpuVendor.Nvidia, 12)], KeepGpuForGames = true },
            NetworkMachineKind.Companion) { HasHostService = true };
        var box = new NetworkMachine(new("gpu-box", "gpu-box") { Gpus = [new("RTX 4090", GpuVendor.Nvidia, 24)] }, NetworkMachineKind.Host)
            { HasHostService = true };
        var target = new NetworkSetup(
            [new("desk", NetworkMachineKind.Companion, []), new("gpu-box", NetworkMachineKind.Host, [new("ollama", "qwen3:8b", 0)])],
            [
                new(ClusterJobs.Thinking, null, OptionId: "gemma4:e2b"),
                new(ClusterJobs.Speaking, "gpu-box") { Pool = ["desk"] },
                new(ClusterJobs.Listening, null, OptionId: "parakeet-tdt-0.6b-v3-cpu")
            ]);
        var plans = SituationPlans.For(new([desk, box]), new(target, target, []), id => id == "desk" ? "DESK" : id ?? "your companion PCs",
            backup: "NVIDIA Build");

        Assert.Equal([Situation.Normal, Situation.Gaming, Situation.HostAway], plans.Select(p => p.Situation));
        Assert.Contains("Thinking: each companion PC itself (Gemma 4 E2B).", plans[0].Lines);
        Assert.Contains(plans[1].Lines, l => l.StartsWith("On DESK: Thinking moves to gpu-box (qwen3:8b), then NVIDIA Build, then DESK's own model", StringComparison.Ordinal));
        Assert.Contains("If gpu-box doesn't answer: Speaking goes to DESK (its list).", plans[2].Lines);

        var never = SituationPlans.For(new([desk, box]), new(target, target, []), id => id ?? "", backup: "NVIDIA Build", backupAllowed: false);
        Assert.DoesNotContain(never[1].Lines, l => l.Contains("then NVIDIA Build", StringComparison.Ordinal));
    }
}
