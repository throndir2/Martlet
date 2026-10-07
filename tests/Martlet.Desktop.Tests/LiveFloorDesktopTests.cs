using Martlet.Conversation;
using Martlet.Core.Cluster;
using Martlet.Core.Settings;

namespace Martlet.Desktop.Tests;

/// <summary>The live floor in the desktop's conversation (docs/CONVERSATION.md, Live floor).</summary>
public sealed class LiveFloorDesktopTests
{
    private sealed class RecordingHold : ILiveGpuHold
    {
        public List<(string Host, IReadOnlyList<string> Routes, TimeSpan Ttl)> Holds { get; } = [];
        public List<string> Releases { get; } = [];

        public Task HoldAsync(string hostId, IReadOnlyList<string> routeIds, TimeSpan ttl, CancellationToken token)
        {
            lock (Holds) Holds.Add((hostId, routeIds, ttl));
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(string hostId, CancellationToken token)
        {
            lock (Releases) Releases.Add(hostId);
            return Task.CompletedTask;
        }
    }

    private static async Task Until(Func<bool> condition, LiveFixture? fixture = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            fixture?.Clock.Advance(TimeSpan.FromMilliseconds(20));
            await Task.Delay(1, timeout.Token);
        }
    }

    [Fact]
    public async Task A_typed_reply_holds_the_live_floor_until_its_voice_is_made_and_a_short_grace_ends()
    {
        await using var fixture = await LiveFixture.Create();
        var floor = fixture.Controller.LiveFloor;
        Assert.Equal(LiveFloorLevel.Idle, floor.Level);
        var operation = fixture.Start();
        await fixture.Finish(operation);
        Assert.Equal("runtime.Completed", operation.Status.Code);
        await Until(() => floor.Level == LiveFloorLevel.Idle, fixture);
        var changes = floor.Recent;
        Assert.Contains(changes, c => c.To == LiveFloorLevel.Live && c.Why == "a reply to what you typed started");
        Assert.Equal(LiveFloorLevel.Idle, changes[^1].To);
        Assert.Equal(1, floor.Periods);
        Assert.Null(fixture.Controller.LiveFloorNote);
    }

    [Fact]
    public async Task The_hosts_that_serve_the_live_routes_keep_their_graphics_cards_for_the_turn_and_let_them_go_after()
    {
        await using var fixture = await LiveFixture.Create();
        var loaded = await fixture.Store.LoadAsync();
        var settings = loaded.Settings!;
        var route = settings.Setup!.Routes.Single(item => item.Role == SetupRole.Llm);
        var selfHost = SetupSettings.ConfigureGatewayEndpoint(settings with
        {
            Setup = settings.Setup with { Routes = settings.Setup.Routes.Where(item => item.Role != SetupRole.Llm).ToArray() }
        }, SetupRouteType.GatewayOllama, new()
        {
            SchemaVersion = 1, Origin = "https://192.168.1.20:7443", HostId = "fixture-host",
            SpkiFingerprint = "sha256:" + new string('a', 64), DeviceRole = "voice"
        }, route.ModelId);
        var paired = SetupSettings.ReplaceRoute(selfHost, selfHost.Setup!.Routes.Single(item => item.Role == SetupRole.Llm) with
        {
            CredentialId = Guid.NewGuid(), GatewayDeviceId = "desktop-test"
        });
        paired = SetupSettings.ApplyGatewaySnapshot(paired, SetupRole.Llm, MainWindow.Snapshot(new(
            SelfHostSetup.OllamaRouteId, "/martlet/v1/inference/ollama-chat", "ollama-native-chat-v034-text", "1.0",
            "ollama-host", "ollama-relay", "0.1.0", route.ModelId, "ollama", new string('c', 64), "sha256:" + new string('d', 64),
            98_304, 16_384, 65_536, 65_536, 4_096, 4_194_304, TimeSpan.FromMinutes(15), "request_abort"), SetupRouteType.GatewayOllama));
        paired = SetupSettings.SetRouteEnabled(paired, SetupRole.Llm, true, true);
        // The pairing secret isn't in this fixture's vault, so the settings go straight to the conversation.
        fixture.Controller.Configure(loaded with { Settings = paired });
        var hold = new RecordingHold();
        fixture.Controller.GpuHold = hold;
        var resources = fixture.Controller.LiveFloorRules.Resources;
        Assert.Contains(resources.Items, item => item is { Job: "thinking", Machine: "lan:192.168.1.20", HostId: "fixture-host" });
        // Listening alone holds nothing on the hosts.
        fixture.Controller.LiveFloor.Heard();
        await Task.Delay(100);
        Assert.Empty(hold.Holds);
        fixture.Controller.LiveFloor.Words();
        await Until(() => { lock (hold.Holds) return hold.Holds.Count > 0; });
        var (host, routes, ttl) = hold.Holds[0];
        Assert.Equal("fixture-host", host);
        Assert.Equal([SelfHostSetup.OllamaRouteId], routes);
        Assert.Equal(TimeSpan.FromSeconds(10), ttl);
        // Renewed every 5 seconds while Live.
        await Until(() => { lock (hold.Holds) return hold.Holds.Count > 1; }, fixture);
        fixture.Controller.LiveFloor.Clear();
        await Until(() => { lock (hold.Releases) return hold.Releases.Count > 0; });
        Assert.Equal(["fixture-host"], hold.Releases);
    }
}
