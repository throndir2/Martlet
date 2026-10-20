using System.Text.Json.Nodes;
using Martlet.Conversation;
using Martlet.Core.Cluster;
using Martlet.Core.Lorebooks;
using Martlet.Core.Motion;
using Martlet.Core.Pictures;
using Martlet.Core.Planning;
using Martlet.Core.Settings;
using Martlet.Core.Sync;

namespace Martlet.Core.Tests;

/// <summary>Two simulated computers keep what #1040, #805 and #955 made shared the same: the check-ins (each script stays where it
/// was written), Chatterbox Original's style, the recommended setups declined, the Picture making, Vision and Hearing lists (moved
/// once from pools-local.json) with their members' API keys and custom workflows, and the Core store sections the desktop shares.</summary>
public sealed class SharedListsTwoComputersTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "martlet-shared-lists-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset start = DateTimeOffset.UtcNow;

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private (string A, string B) Computers(string name)
    {
        var (a, b) = (Path.Combine(root, name, "a"), Path.Combine(root, name, "b"));
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        return (a, b);
    }

    private static Task<SharedSettingsResult> Sync(SharedSettingsNode node, SharedSettingsNode other, DateTimeOffset at) =>
        node.SyncAsync([other.Document], true, at, CancellationToken.None);

    [Fact]
    public async Task Check_ins_travel_but_each_script_stays_where_it_was_written()
    {
        var (a, b) = Computers("check-ins");
        Assert.True(SettingScopes.IsAccountKey(CheckInSettings.SharedKey));
        var nodeA = new SharedSettingsNode(a, "desktop-a", [CheckInSettings.Section(a)]);
        var nodeB = new SharedSettingsNode(b, "desktop-b", [CheckInSettings.Section(b)]);
        await Sync(nodeB, nodeA, start);

        var tea = new CustomCheckIn { Id = "c1", Name = "Tea break", On = true, Task = "Remind me to drink tea.", Script = "Get-Date" };
        Assert.True(new CheckInSettings().With(tea).Save(a));
        var recorded = await Sync(nodeA, nodeB, start.AddSeconds(15));
        Assert.Contains(CheckInSettings.SharedKey, recorded.Recorded);
        Assert.DoesNotContain("Get-Date", nodeA.Document.Find(CheckInSettings.SharedKey)!.Value, StringComparison.Ordinal);
        await Sync(nodeB, nodeA, start.AddSeconds(30));
        var onB = Assert.Single(CheckInSettings.Load(b).Custom);
        Assert.Equal("Tea break", onB.Name);
        Assert.Equal("", onB.Script);

        // B renames it and writes its own script; A takes the name and keeps its own script.
        Assert.True(CheckInSettings.Load(b).With(onB with { Name = "Tea", Script = "Get-Process" }).Save(b));
        await Sync(nodeB, nodeA, start.AddSeconds(45));
        await Sync(nodeA, nodeB, start.AddSeconds(60));
        var onA = Assert.Single(CheckInSettings.Load(a).Custom);
        Assert.Equal("Tea", onA.Name);
        Assert.Equal("Get-Date", onA.Script);
        Assert.Equal("Get-Process", Assert.Single(CheckInSettings.Load(b).Custom).Script);
    }

    [Fact]
    public async Task The_chatterbox_style_and_the_declined_setups_are_one_for_both_computers()
    {
        var (a, b) = Computers("style");
        var nodeA = new SharedSettingsNode(a, "desktop-a", [ChatterboxStyle.Section(a), RecommendedSetupMemory.Section(a)]);
        var nodeB = new SharedSettingsNode(b, "desktop-b", [ChatterboxStyle.Section(b), RecommendedSetupMemory.Section(b)]);
        await Sync(nodeB, nodeA, start);
        new ChatterboxStyle(0.8, 0.4, 1.2, 0.2).Save(a);
        Assert.True(new RecommendedSetupMemory().Decline("setup-1", start).WithOff(PlanComponent.Vision, true).Save(a));
        await Sync(nodeA, nodeB, start.AddSeconds(15));
        await Sync(nodeB, nodeA, start.AddSeconds(30));
        Assert.Equal(new ChatterboxStyle(0.8, 0.4, 1.2, 0.2), ChatterboxStyle.Load(b));
        Assert.True(RecommendedSetupMemory.Load(b).WasDeclined("setup-1"));
        Assert.Null(ChatterboxStyle.Parse("""{"general":{"exaggeration":9,"cfg_weight":0.5},"expressive":{"exaggeration":0.7,"cfg_weight":0.3}}"""));
    }

    [Fact]
    public async Task The_vision_list_moves_from_pools_local_once_and_travels_with_its_key_and_the_picture_workflow()
    {
        var (a, b) = Computers("pools");
        var pcA = new Pc(a, "desktop-a");
        var pcB = new Pc(b, "desktop-b");
        var profileA = await pcA.ProfileAsync();
        var profileB = await pcB.ProfileAsync();

        // Before #805, A kept its Vision list and its Reading list in pools-local.json.
        var server = PoolMember.Cloud("llm-lan", "qwen3-vl", "https://llm.example.com/v1");
        var reading = new PoolList { Area = PoolAreas.Reading.Id, Members = [PoolMember.ThisPc()] };
        Assert.True(new PoolSettings().With(new PoolList { Area = PoolAreas.Vision.Id, Members = [server] }).With(reading).Save(a, shared: false));
        // It reads as shared at once, without a write; the next save of pools.json takes it in and leaves Reading here.
        Assert.Equal([server.Key], PoolSettings.LoadFor(a, PoolAreas.Vision)!.Members.Select(m => m.Key));
        Assert.False(PoolSettings.Saved(a, shared: true));
        Assert.True(PoolSettings.Load(a, shared: false).Has(PoolAreas.Vision.Id));

        // Its key, and a Picture making member with a workflow of its own.
        var credential = Guid.NewGuid();
        using (var lease = new SecretLease("sk-lab-vision-key-0123456789"))
            pcA.Vault.Write(PoolKeyShare.Binding(PoolAreas.Vision, server, profileA, credential)!, lease);
        Assert.True(PoolKeys.Load(a).With(PoolAreas.Vision.Id, server.Key, credential).Save(a));
        Assert.True(PoolSettings.SaveFor(a, PoolAreas.Hearing, new PoolList { Area = PoolAreas.Hearing.Id }));
        Assert.Equal([server.Key], PoolSettings.Load(a).Pool(PoolAreas.Vision.Id).Members.Select(m => m.Key));
        Assert.False(PoolSettings.Load(a, shared: false).Has(PoolAreas.Vision.Id));
        Assert.True(PoolSettings.Load(a, shared: false).Has(PoolAreas.Reading.Id));
        var comfy = PictureMakingPoolMembers.WithWorkflow(PoolMember.Service("http://192.168.1.20:8188"), PictureWorkflow.Custom, null, "pictures-workflow-lab.json");
        Assert.True(PoolSettings.SaveFor(a, PoolAreas.PictureMaking, new PoolList { Area = PoolAreas.PictureMaking.Id, Members = [comfy] }));
        Assert.True(PicturesSettings.SaveWorkflow(a, new JsonObject { ["3"] = new JsonObject { ["class_type"] = "KSampler", ["inputs"] = new JsonObject() } },
            "pictures-workflow-lab.json"));

        await pcB.Node.SyncAsync([], true, start, CancellationToken.None);
        var recorded = await pcA.Node.SyncAsync([pcB.Node.Document], true, start.AddSeconds(15), CancellationToken.None);
        Assert.Contains(PoolKeyShare.SharedKey, recorded.Recorded);
        Assert.DoesNotContain("sk-lab", pcA.Node.Document.Find(PoolKeyShare.SharedKey)!.Value, StringComparison.Ordinal);
        await pcB.Node.SyncAsync([pcA.Node.Document], true, start.AddSeconds(30), CancellationToken.None);
        await pcB.Node.SyncAsync([pcA.Node.Document], true, start.AddSeconds(45), CancellationToken.None);

        Assert.Equal([server.Key], PoolSettings.LoadFor(b, PoolAreas.Vision)!.Members.Select(m => m.Key));
        Assert.Null(PoolSettings.LoadFor(b, PoolAreas.Reading));
        var keyOnB = PoolKeys.Load(b).For(PoolAreas.Vision.Id, server.Key);
        Assert.NotNull(keyOnB);
        Assert.True(pcB.Vault.Has("sk-lab-vision-key-0123456789"));
        Assert.NotNull(pcB.Vault.Read(PoolKeyShare.Binding(PoolAreas.Vision, server, profileB, keyOnB!.Value)!).Secret);
        Assert.NotNull(PicturesSettings.LoadWorkflow(b, "pictures-workflow-lab.json"));
    }

    [Fact]
    public async Task The_core_store_sections_follow_the_later_choice_and_wait_on_a_newer_martlet()
    {
        var (a, b) = Computers("stores");
        var nodeA = new SharedSettingsNode(a, "desktop-a", StoreSections.All(a));
        var nodeB = new SharedSettingsNode(b, "desktop-b", StoreSections.All(b));
        await Sync(nodeB, nodeA, start);
        Assert.True(new MotionSettings { Place = MotionPlace.Off }.Save(a));
        await Sync(nodeA, nodeB, start.AddSeconds(15));
        var taken = await Sync(nodeB, nodeA, start.AddSeconds(30));
        Assert.Contains(taken.Applied, change => change.Key == MotionSettings.SharedKey);
        Assert.Equal(MotionPlace.Off, MotionSettings.Load(b).Place);

        var section = StoreSections.Motion(b);
        var newer = nodeA.Document.Find(MotionSettings.SharedKey)! with { Value = "{\"schema_version\":99}" };
        var result = await section.ApplyAsync(newer, null, CancellationToken.None);
        Assert.False(result.Applied);
        Assert.Equal(MotionPlace.Off, MotionSettings.Load(b).Place);
    }

    private sealed class MemoryVault : ICredentialStore
    {
        private readonly Dictionary<string, string> items = new(StringComparer.Ordinal);
        private static string Target(CredentialBinding b) => $"{b.ProfileId:N}/{b.CredentialId:N}/{b.ScopeDigest()}";

        public CredentialError Write(CredentialBinding binding, SecretLease secret)
        {
            binding.Validate();
            secret.Use(chars => items[Target(binding)] = new string(chars));
            return CredentialError.None;
        }

        public CredentialReadResult Read(CredentialBinding binding) =>
            items.TryGetValue(Target(binding), out var value) ? new(CredentialError.None, new SecretLease(value)) : new(CredentialError.Missing, null);

        public CredentialError Delete(CredentialBinding binding) => items.Remove(Target(binding)) ? CredentialError.None : CredentialError.Missing;

        internal bool Has(string value) => items.ContainsValue(value);
    }

    private sealed class Pc
    {
        internal MemoryVault Vault { get; } = new();
        internal SetupService Setup { get; }
        internal SharedSettingsNode Node { get; }

        internal Pc(string data, string device)
        {
            Setup = new SetupService(new SettingsStore(data), Vault);
            Node = new SharedSettingsNode(data, device, [StoreSections.Pools(data), PoolKeyShare.Section(data, Vault), PictureWorkflowFiles.Section(data)]);
        }

        internal async Task<Guid> ProfileAsync()
        {
            var loaded = await Setup.LoadAsync();
            if (loaded.Settings is { } settings) return settings.Profile.Id;
            var begun = SetupSettings.Begin(null);
            var saved = await Setup.SaveAsync(begun with { Profile = begun.Profile with { Kind = ProfileKind.Api } }, loaded.Revision);
            Assert.True(saved.Save.Saved, saved.Summary);
            return saved.Settings.Profile.Id;
        }
    }
}
