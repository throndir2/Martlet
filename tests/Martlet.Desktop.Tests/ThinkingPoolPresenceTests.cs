using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Xunit;

namespace Martlet.Desktop.Tests;

// The Thinking pool follows which computers answer (HostPresence), while the tools a reply carries stay the same.
public sealed class ThinkingPoolPresenceTests : IDisposable
{
    public ThinkingPoolPresenceTests() => HostPresence.Reset();

    public void Dispose() => HostPresence.Reset();

    private static DeepThinkingSettings Role(string host, int slots) => new()
    {
        Place = DeepThinkingPlace.Host, ModelId = "qwen3:8b", HostId = host, HostOrigin = $"https://{host}.local:9443",
        HostSpkiFingerprint = "sha256:" + new string('0', 64), HostDeviceId = "desk-pc", HostCredentialId = Guid.NewGuid(),
        HostRouteId = SelfHostSetup.DeepThinkingRouteId, Slots = slots
    };

    [Fact]
    public async Task A_member_going_offline_leaves_the_pools_slots_but_the_reply_tools_stay_byte_identical()
    {
        await using var fixture = await LiveFixture.Create();
        var controller = fixture.Controller;
        controller.PoolSettings = new ThinkingPoolSettings { Members = [Role("diva", 2), Role("ripley", 1)] };
        var configured = controller.Configuration!;
        string Tools() => JsonSerializer.Serialize(controller.ReplyThinkTools(configured));
        var tools = Tools();
        var research = controller.OffersResearch(configured);
        Assert.Contains("Up to 2 at once", tools, StringComparison.Ordinal);
        Assert.Equal((3, 3), (controller.ThinkingPool.Status().Slots, controller.ThinkingPool.Status().ConfiguredSlots));

        HostPresence.Note("diva", false);
        Assert.Equal(tools, Tools());
        Assert.Equal(research, controller.OffersResearch(configured));
        var status = controller.ThinkingPool.Status();
        Assert.Equal((1, 3), (status.Slots, status.ConfiguredSlots));
        Assert.False(status.Members.Single(m => m.Id == "host:diva").Online);
        Assert.Equal("host:ripley", controller.ThinkingPool.Find(ThinkingJobKind.Memory)?.Id);
        var file = JsonNode.Parse(controller.PoolStatusJson())!;
        Assert.Equal((1, 3), (file["slots"]!.GetValue<int>(), file["configuredSlots"]!.GetValue<int>()));
        var diva = file["members"]!.AsArray().Single(m => m!["id"]!.GetValue<string>() == "host:diva")!;
        Assert.False(diva["online"]!.GetValue<bool>());
        Assert.NotNull(diva["offlineSince"]);
        Assert.Contains(file["warnings"]!.AsArray(), w => w!.GetValue<string>().Contains("diva is offline", StringComparison.Ordinal));

        // Every computer offline: the tools still don't change, no pool job can run, and the conversation model (a cloud
        // provider here, which answers several requests at once) stands in for thinking longer and research.
        HostPresence.Note("ripley", false);
        Assert.Equal(tools, Tools());
        Assert.False(controller.ThinkingPool.CanRun(ThinkingJobKind.Memory));
        file = JsonNode.Parse(controller.PoolStatusJson())!;
        Assert.Equal(0, file["slots"]!.GetValue<int>());
        Assert.True(file["conversationModelStandsIn"]!.GetValue<bool>());

        HostPresence.Note("diva", true);
        HostPresence.Note("ripley", true);
        Assert.Equal(tools, Tools());
        Assert.Equal(3, controller.ThinkingPool.Status().Slots);
        Assert.False(JsonNode.Parse(controller.PoolStatusJson())!["conversationModelStandsIn"]!.GetValue<bool>());
    }
}
