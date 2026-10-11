using Martlet.Core.Planning;
using Martlet.Core.Settings;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

// Situations (docs/RECOMMENDATION_DESIGN.md, stage 2) on the conversation's path: the moved Thinking route, and the order live
// requests try computers in. The tests pass the situation explicitly, so tests that run beside them never see it.
public sealed class LiveSituationTests
{
    [Fact]
    public async Task A_moved_route_replaces_thinking_for_new_replies_and_a_reply_keeps_its_route()
    {
        await using var fixture = await LiveFixture.Create();
        var loaded = await fixture.Store.LoadAsync();
        var local = ChatCompletionsSetup.SelectRoute(loaded.Settings!, MainWindow.LocalOllamaBaseUrl, "gemma4:e2b");
        loaded = loaded with { Settings = local };
        var saved = local.Setup!.Routes.Single(r => r.Role == SetupRole.Llm);
        var fallback = new ThinkingFallbackSettings
        {
            Origin = ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, ModelId = ChatCompletionsEndpointCatalog.NvidiaBuildDefaultModelId,
            CredentialId = Guid.NewGuid(), ConfigurationRevision = Guid.NewGuid()
        };
        var moved = new SituationOverride("Gaming|Hosted", saved.ConfigurationRevision, SituationRoutes.Hosted(fallback), "NVIDIA Build");

        Assert.True(LiveConversationConfiguration.From(loaded, null, null, null)!.LocalOllama);
        var gaming = LiveConversationConfiguration.From(loaded, null, null, moved)!;
        Assert.Same(moved, gaming.Situation);
        Assert.False(gaming.LocalOllama);
        Assert.Equal(ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, gaming.Route(SetupRole.Llm).Origin);
        Assert.Null(gaming.Unavailable(false, false));
        Assert.Contains("NVIDIA Build", gaming.Disclosure(false));
        Assert.Equal(saved, local.Setup.Routes.Single(r => r.Role == SetupRole.Llm));

        // A reply checks its settings again with the route it started with, so it isn't stopped when the situation changes.
        Assert.Equal(gaming.Routes, LiveConversationConfiguration.From(loaded, null, null, gaming.Situation)!.Routes);

        // A situation decided for a Thinking route the owner has since changed does nothing.
        var stale = LiveConversationConfiguration.From(loaded, null, null, moved with { ForRoute = Guid.NewGuid() })!;
        Assert.Null(stale.Situation);
        Assert.True(stale.LocalOllama);
        fixture.NoEffects();
    }

    [Fact]
    public void Live_requests_pass_over_this_pcs_card_while_gaming_and_computers_that_dont_answer()
    {
        IReadOnlyList<string> stops = ["own-host", "gpu-box", "situation-test-away"];
        Assert.Same(stops, LiveSituation.Order(stops, s => s, "own-host", gaming: false));
        Assert.Equal(["gpu-box", "situation-test-away", "own-host"], LiveSituation.Order(stops, s => s, "own-host", gaming: true));
        // A cloud member right after this PC's own host service never moves ahead of it.
        IReadOnlyList<string> cloud = ["own-host", "cloud", "gpu-box"];
        Assert.Same(cloud, LiveSituation.Order(cloud, s => s == "cloud" ? null : s, "own-host", gaming: true));

        HostPresence.Note("situation-test-away", false);
        try
        {
            IReadOnlyList<string> away = ["situation-test-away", "own-host", "gpu-box"];
            Assert.Equal(["own-host", "gpu-box", "situation-test-away"], LiveSituation.Order(away, s => s, "own-host", gaming: false));
            Assert.Equal(["gpu-box", "own-host", "situation-test-away"], LiveSituation.Order(away, s => s, "own-host", gaming: true));
        }
        finally { HostPresence.Note("situation-test-away", true); }
    }

    [Fact]
    public void The_fixture_reads_its_network_game_and_host_away()
    {
        var fixture = SimulatedSituation.Parse("gaming,game")!;
        Assert.Equal(("gaming", SimulatedSituation.GameName, false), (fixture.Network, fixture.Game, fixture.Away));
        Assert.Equal((null, null, true), (SimulatedSituation.Parse("1,away")!.Network, SimulatedSituation.Parse("1,away")!.Game,
            SimulatedSituation.Parse("1,away")!.Away));
        Assert.Null(SimulatedSituation.Parse(" "));
        var decision = LiveSituations.Decide(SimulatedSituation.Facts("gaming", true) with { Game = SimulatedSituation.GameName });
        Assert.Equal((Situation.Gaming, "gpu-box"), (decision.Situation, decision.Place!.HostId));
        var away = LiveSituations.Decide(SimulatedSituation.Facts("host", true) with { Away = ["gpu-box"] });
        Assert.Equal((Situation.HostAway, "desk-host"), (away.Situation, away.Place!.HostId));
    }
}
