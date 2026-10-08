using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Installation;
using Martlet.Core.Network;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>Hosts friends share with this PC (their engines only) and the Friends overview of your own hosts.</summary>
public sealed class SharedHostsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private const string Spki = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static AvatarRemoteHost Remote(string id, string ip) => new()
    {
        Origin = $"https://{ip}:9443", HostId = id, SpkiFingerprint = Spki, DeviceId = "desktop-ana", CredentialId = new string('C', 22)
    };

    private static PairedHost Own(string id, string ip) => new() { Pairing = Remote(id, ip) };

    private static PairedHost Shared(string id, string ip, IReadOnlyList<string>? outside = null) => new()
    {
        Pairing = Remote(id, ip), Access = HostSignInAccess.Friend, SignedInAs = "ana@example.net (authentik)", OutsideAddresses = outside
    };

    [Fact]
    public void A_shared_pairing_keeps_its_access_and_its_invites_addresses_and_speaks_with_the_recording_itself()
    {
        using var scope = new AvatarHostingTests.Scope();
        HostRegistry.Save(scope.DirectoryPath, [Own("home-host", "192.168.70.10"), Shared("friends-host", "10.20.30.40", ["friend.example.net:9443"])]);
        var loaded = HostRegistry.Load(scope.DirectoryPath);
        Assert.False(loaded[0].Shared);
        Assert.Null(loaded[0].Access);
        Assert.True(loaded[1].Shared);
        Assert.Equal("ana@example.net (authentik)", loaded[1].SignedInAs);
        Assert.Contains("\"access\": \"friend\"", File.ReadAllText(Path.Combine(scope.DirectoryPath, HostRegistry.FileName)), StringComparison.Ordinal);
        // A friend's host never holds this PC's voices: speaking there sends the recording from the first sentence.
        Assert.True(Audio2FaceHostConnection.SendsRecordingTo("friends-host"));

        // Your network's roster never moves a friend's host's addresses, even under the same name.
        using var founder = NetworkKey.Create("home-pc");
        var roster = NetworkRoster.Found(founder, "HOME", Now)
            .AddHost(founder, "friends-host", "friends-host", "https://10.20.30.40:9443", Spki, Now)
            .SetHostAddresses(founder, "friends-host", ["other.example.net:9443"], Now.AddSeconds(1));
        Assert.Same(loaded[1], HostRegistry.WithRosterAddresses(loaded, roster)[1]);
    }

    [Fact]
    public void A_shared_host_is_never_one_of_your_hosts_even_when_it_does_this_PCs_lip_sync()
    {
        using var scope = new AvatarHostingTests.Scope();
        var own = Own("home-host", "192.168.70.10");
        var shared = Shared("friends-host", "10.20.30.40");
        var avatar = scope.Profile() with { RemoteHost = shared.Pairing };
        var checks = new Dictionary<string, HostCheck>();
        var inputs = new NetworkInputs(MachineInfo.Unknown, DeviceRole.Companion, null, avatar, false, checks, Hosts: [own, shared],
            SharedHosts: [shared]);
        Assert.Equal(["home-host"], NetworkMap.Hosts(inputs).Select(h => h.HostId));
        // Without the shared list, an old lip-sync pairing saved only in the avatar profile still counts as one of yours.
        Assert.Equal(["home-host", "friends-host"], NetworkMap.Hosts(inputs with { Hosts = [own], SharedHosts = null }).Select(h => h.HostId));
        var nodes = NetworkMap.Build(inputs);
        // It shows with the lip-sync it does for this PC, marked as a friend's, never with your hosts' commands.
        var face = nodes.Single(n => n.Id == "host:friends-host");
        Assert.Equal("Shared by a friend", face.Subtitle);
        Assert.Contains(face.Roles, r => r.Chip == "Lip-sync" && r.Detail == "Handles lip-sync for this PC.");
        Assert.DoesNotContain(face.Commands, c => c.Action is NodeAction.ForgetHost or NodeAction.ManageHost or NodeAction.InstallRole);
        Assert.Contains(nodes.Single(n => n.Id == "this-pc").Commands, c => c.Action == NodeAction.LipSyncThisPc);
    }

    [Fact]
    public void A_friends_computer_shows_as_one_and_never_as_one_of_your_hosts()
    {
        using var scope = new AvatarHostingTests.Scope();
        var own = Own("diva-host", "192.168.70.10");
        var checks = new Dictionary<string, HostCheck> { ["diva-host"] = new(true, "Reachable.", new Dictionary<string, string>()) };
        // Named like the PC that runs diva-host: still a friend's computer, never that host's device.
        var friend = new MartletComputer("friend-pc", "DIVA", ComputerStanding.Friend, null, true, Through: "diva-host");
        var nodes = NetworkMap.Build(new(MachineInfo.Unknown, DeviceRole.Companion, null, null, false, checks, Hosts: [own], Computers: [friend]));
        var node = nodes.Single(n => n.Id == "pc:friend-pc");
        Assert.Contains(node.Facts, f => f.Label == "Martlet network" && f.Value == "Never: a friend's computer");
        Assert.Contains(node.Roles, r => r.Detail.Contains("signed in to diva-host as a friend", StringComparison.Ordinal));
        Assert.Null(NetworkMap.RoleCommand(friend));
        Assert.DoesNotContain(nodes.Single(n => n.Id == "host:diva-host").Facts, f => f.Value.Contains("friend-pc", StringComparison.Ordinal));
    }

    [Fact]
    public void A_job_on_a_shared_host_is_this_PCs_own_choice_that_the_plan_never_records_or_moves()
    {
        using var scope = new AvatarHostingTests.Scope();
        var shared = Shared("friends-host", "10.20.30.40");
        var avatar = scope.Profile() with { RemoteHost = shared.Pairing };
        var local = ClusterSync.Local(ClusterJobs.LipSync, null, avatar, shared: ["friends-host"]);
        Assert.Equal(new LocalJob("friends-host", false, Shared: true), local);
        Assert.False(ClusterSync.Seeds(local));
        var plan = ClusterPlan.Empty.Assign(ClusterJobs.LipSync, "home-host", false, false, null, "desktop-b", Now);
        Assert.True(ClusterSync.Matches(plan.For(ClusterJobs.LipSync)!, local));
        // The same pairing as one of your own hosts is recorded and followed as before.
        var own = ClusterSync.Local(ClusterJobs.LipSync, null, avatar);
        Assert.True(ClusterSync.Seeds(own));
        Assert.False(ClusterSync.Matches(plan.For(ClusterJobs.LipSync)!, own));
    }

    [Fact]
    public void A_shared_hosts_owner_first_refusal_moves_a_request_on_and_a_live_hold_still_means_wait()
    {
        Assert.Equal(WorkRefusal.Owner, WorkSharingRoster.Classify(new Audio2FaceHostException("job.busy", "busy") { Detail = "owner" }));
        Assert.Equal(WorkRefusal.Preempted, WorkSharingRoster.Classify(new Audio2FaceHostException("job.busy", "busy") { Detail = "live" }));
        Assert.Equal(WorkRefusal.Busy, WorkSharingRoster.Classify(new Audio2FaceHostException("job.busy", "busy")));
        Assert.True(new Audio2FaceHostException("job.busy", "busy") { Detail = "owner" }.OwnerFirst);
        Assert.False(new Audio2FaceHostException("job.preempted", "stopped").OwnerFirst);
        Assert.Contains("its owner's own work", WorkSharingRoster.OwnerFirstText("friends-host", "speaking"), StringComparison.Ordinal);
    }

    private static HostSignInSettings Settings(string hostId, string[] providers, HostSignInAllowed[] allowed, HostSignInEnrolled[] enrolled,
        HostSignInEnrolled[] refused) =>
        new(hostId, "owner", 10, providers.Select(p => new HostSignInProviderSettings(p, "oidc", p, "https://idp.example", "c", null, true)).ToArray(),
            allowed, enrolled, null) { Refused = refused };

    [Fact]
    public void Friends_lists_each_person_with_the_hosts_shared_with_them_and_who_asked_never_your_own_sign_ins()
    {
        var home = Settings("home-host", ["authentik", "discord"],
            [
                new("authentik", "me-1", "me@example.net"),
                new("authentik", "ana-7", "ana@example.net") { Access = HostSignInAccess.Friend }
            ],
            [new("friend-pc", "authentik", "ana-7", "ana@example.net", Now) { Access = HostSignInAccess.Friend }],
            [new("bob-pc", "discord", "42", "bob", Now.AddMinutes(5))]);
        var gpu = Settings("gpu-box", ["authentik"], [], [],
            // Your own laptop trying a host where it isn't allowed yet: one of your sign-ins, not a friend.
            [new("laptop", "authentik", "me-1", "me@example.net", Now.AddMinutes(1))]);
        var friends = FriendsOverview.Build(new Dictionary<string, HostSignInSettings> { ["home-host"] = home, ["gpu-box"] = gpu });
        Assert.Equal(["ana@example.net", "bob"], friends.Select(f => f.Name));
        var ana = friends[0];
        Assert.Equal(["home-host"], ana.SharedOn);
        Assert.Equal(FriendHostState.NotShared, ana.Hosts.Single(h => h.HostId == "gpu-box").State);
        Assert.True(ana.Hosts.Single(h => h.HostId == "gpu-box").ProviderReady);
        Assert.Equal(["friend-pc"], ana.Hosts.SelectMany(h => h.Computers).Select(c => c.DeviceId));
        var bob = friends[1];
        Assert.True(bob.Asking);
        Assert.Equal(FriendHostState.Asked, bob.Hosts.Single(h => h.HostId == "home-host").State);
        Assert.False(bob.Hosts.Single(h => h.HostId == "gpu-box").ProviderReady);
        Assert.Equal("discord-42", bob.Key);
        Assert.Equal("authentik-a_b_c", FriendsOverview.Key("authentik", "a b@c"));
        Assert.StartsWith("You share 1 host with 1 friend; 1 person asked to use a host.", FriendsOverview.Status(friends, 2, 3), StringComparison.Ordinal);
        Assert.EndsWith("1 host didn't answer.", FriendsOverview.Status(friends, 2, 3), StringComparison.Ordinal);
        Assert.StartsWith("You share no host with a friend yet.", FriendsOverview.Status([], 1, 1), StringComparison.Ordinal);
        Assert.StartsWith("1 person asked to use one of your hosts", FriendsOverview.Status([bob], 1, 1), StringComparison.Ordinal);

        using var scope = new AvatarHostingTests.Scope();
        FriendsOverview.SaveSummary(scope.DirectoryPath, new Dictionary<string, HostSignInSettings> { ["home-host"] = home },
            new Dictionary<string, string> { ["gpu-box"] = "didn't answer in time" }, Now);
        using var summary = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(scope.DirectoryPath, FriendsOverview.SummaryFile)));
        var hosts = summary.RootElement.GetProperty("hosts").EnumerateArray().ToArray();
        Assert.Equal(["gpu-box", "home-host"], hosts.Select(h => h.GetProperty("hostId").GetString()));
        Assert.False(hosts[0].GetProperty("read").GetBoolean());
        var friend = hosts[1].GetProperty("friends").EnumerateArray().Single();
        Assert.Equal("ana@example.net", friend.GetProperty("label").GetString());
        Assert.Equal(1, friend.GetProperty("computers").GetInt32());
        Assert.Equal(1, hosts[1].GetProperty("asking").GetInt32());
    }
}
