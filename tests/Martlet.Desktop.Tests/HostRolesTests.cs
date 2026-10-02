using Martlet.Avatar.Hosting;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class HostRolesTests
{
    private static AvatarRemoteHost Remote(string id, string ip) => new()
    {
        Origin = $"https://{ip}:9443", HostId = id, SpkiFingerprint = "sha256:" + new string('a', 64),
        DeviceId = "desktop-test", CredentialId = new string('B', 22)
    };

    [Fact]
    public void Registry_keeps_every_paired_host_and_lists_an_older_lip_sync_pairing()
    {
        using var scope = new AvatarHostingTests.Scope();
        Assert.Empty(HostRegistry.Load(scope.DirectoryPath));
        var a = new PairedHost { Pairing = Remote("gpu-a", "192.168.1.20"), Method = HostSetupMethod.SshDocker, SshTarget = "me@gpu-a" };
        var b = new PairedHost { Pairing = Remote("gpu-b", "192.168.1.30"), Method = HostSetupMethod.SshNative, SshTarget = "me@gpu-b" };
        HostRegistry.Save(scope.DirectoryPath, HostRegistry.Upsert(HostRegistry.Upsert([], a), b));
        var loaded = HostRegistry.Load(scope.DirectoryPath, Remote("legacy", "192.168.1.40"), thisPcAddress: "192.168.1.40");
        Assert.Equal(new[] { "gpu-a", "gpu-b", "legacy" }, loaded.Select(h => h.HostId));
        Assert.Equal(a, loaded[0]);
        Assert.Equal(HostSetupMethod.ThisPcDocker, loaded[2].Method);
        Assert.True(loaded[1].CanLaunch);
        Assert.Contains("martlet-host --yes add audio2face", HostSetupCommands.RemoteShell(loaded[1].Target("0.2.0"), HostSetupCommands.Engine(HostAction.Add(HostRoles.Audio2Face)), false), StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(scope.DirectoryPath, HostRegistry.FileName), "{not json");
        Assert.Throws<InvalidDataException>(() => HostRegistry.Load(scope.DirectoryPath));
    }

    [Fact]
    public void Registry_treats_a_this_pc_pairing_on_another_address_as_that_other_computer()
    {
        using var scope = new AvatarHostingTests.Scope();
        var here = new PairedHost { Pairing = Remote("here-host", "192.168.1.10"), Method = HostSetupMethod.ThisPcDocker };
        var pasted = new PairedHost { Pairing = Remote("gaming-host", "192.168.1.50"), Method = HostSetupMethod.ThisPcDocker };
        HostRegistry.Save(scope.DirectoryPath, [here, pasted]);
        var loaded = HostRegistry.Load(scope.DirectoryPath, thisPcAddress: "192.168.1.10");
        Assert.Equal(HostSetupMethod.ThisPcDocker, loaded[0].Method);
        Assert.Equal(HostSetupMethod.OnHost, loaded[1].Method);
        Assert.False(loaded[1].CanLaunch);
        Assert.All(HostRegistry.Load(scope.DirectoryPath), h => Assert.Equal(HostSetupMethod.ThisPcDocker, h.Method));
    }

    [Fact]
    public void Map_shows_who_handles_lip_sync_and_offers_to_hand_it_to_other_hosts()
    {
        using var scope = new AvatarHostingTests.Scope();
        var hosts = new[]
        {
            new PairedHost { Pairing = Remote("gpu-a", "192.168.1.20"), Method = HostSetupMethod.SshDocker, SshTarget = "me@gpu-a" },
            new PairedHost { Pairing = Remote("gpu-b", "192.168.1.30") }
        };
        var checks = new Dictionary<string, HostCheck>
        {
            ["gpu-a"] = new(true, "Reachable. Runs Audio2Face (model claire).",
                new Dictionary<string, string> { ["audio2face"] = "claire", ["ollama"] = "llama3.2-3b", ["stt"] = "small" }),
            ["gpu-b"] = new(true, "Reachable. Not running Audio2Face.", new Dictionary<string, string>())
        };
        var avatar = scope.Profile() with { RemoteHost = hosts[1].Pairing };
        var nodes = NetworkMap.Build(new(MachineInfo.Unknown, DeviceRole.Companion, null, avatar, false, checks, Hosts: hosts));
        var a = nodes.Single(n => n.Id == "host:gpu-a");
        var b = nodes.Single(n => n.Id == "host:gpu-b");
        var pc = nodes.Single(n => n.Id == "this-pc");
        Assert.Contains(a.Roles, r => r.Chip == "Lip-sync" && r.Detail.Contains("standing by", StringComparison.Ordinal));
        Assert.Contains(a.Commands, c => c.Action == NodeAction.UseForLipSync && c.Argument == "gpu-a" && c.Primary);
        Assert.Contains(a.Commands, c => c.Action == NodeAction.RemoveRole && c.Argument == "gpu-a/audio2face");
        Assert.Contains(a.Roles, r => r.Chip == "Thinks" && r.Detail.Contains("llama3.2-3b", StringComparison.Ordinal));
        Assert.Contains(a.Commands, c => c.Action == NodeAction.UseForThinking && c.Argument == "gpu-a" && c.Primary);
        Assert.Contains(a.Roles, r => r.Chip == "Listens" && r.Detail.Contains("small", StringComparison.Ordinal));
        Assert.Contains(a.Commands, c => c.Action == NodeAction.UseForListening && c.Argument == "gpu-a" && c.Primary);
        Assert.Contains(b.Commands, c => c.Action == NodeAction.InstallRole && c.Argument == "gpu-b/stt");
        Assert.Contains(b.Commands, c => c.Action == NodeAction.InstallRole && c.Argument == "gpu-b/ollama");
        Assert.Contains(b.Roles, r => r.Chip == "Lip-sync" && r.Detail.StartsWith("In charge", StringComparison.Ordinal));
        Assert.DoesNotContain(b.Commands, c => c.Action == NodeAction.UseForLipSync);
        Assert.Contains(b.Commands, c => c.Action == NodeAction.InstallRole && c.Argument == "gpu-b/audio2face");
        Assert.Contains(b.Facts, f => f.Label == "Reached via" && f.Value.StartsWith("Not set", StringComparison.Ordinal));
        Assert.Contains(pc.Commands, c => c.Action == NodeAction.LipSyncThisPc);
        Assert.Equal(LipSyncHandler.Host, NetworkMap.LipSync(avatar));
        Assert.Equal(LipSyncHandler.Loudness, NetworkMap.LipSync(avatar with { LipSync = AvatarLipSync.Loudness }));
        Assert.Equal(LipSyncHandler.ThisPc, NetworkMap.LipSync(avatar with { RemoteHost = null }));
    }
}
