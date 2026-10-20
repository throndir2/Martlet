using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Platforms;

namespace Martlet.Desktop.Tests;

/// <summary>Companion › Lip sync's page model (Pages\LipSync), without WPF.</summary>
public sealed class LipSyncPageModelTests
{
    [Fact]
    public void NowSaysWhatMovesTheMouthNext()
    {
        var services = new FakeServices();
        Assert.StartsWith("Voice loudness:", new LipSyncPageModel(services, null).NowText, StringComparison.Ordinal);

        var list = new PoolList { Area = PoolAreas.LipSync.Id, Members = [PoolMember.ThisPc(), PoolMember.Computer("studio")] };
        services.Answers = false;
        Assert.Equal("Your own Audio2Face service at 127.0.0.1:52000, which isn't answering now, or the next of 1 more when it doesn't " +
            "answer; when none can, the mouth follows the voice's loudness.", new LipSyncPageModel(services, list).NowText);

        services.LipSync = AvatarLipSync.Loudness;
        Assert.StartsWith("Voice loudness:", new LipSyncPageModel(services, list).NowText, StringComparison.Ordinal);
        services.LipSync = AvatarLipSync.Audio2Face;
        Assert.StartsWith("Your own Audio2Face service on this PC (127.0.0.1:52000)", new LipSyncPageModel(services, list).NowText, StringComparison.Ordinal);
    }

    [Fact]
    public void MemberLinesSayWhereAudio2FaceStands()
    {
        var services = new FakeServices { Answers = true };
        var model = new LipSyncPageModel(services, null);
        Assert.Equal("your own Audio2Face service at 127.0.0.1:52000; answering", model.MemberStatus(PoolMember.ThisPc()));
        Assert.Equal("not paired on this PC, so this PC skips it", model.MemberStatus(PoolMember.Computer("studio")));
        Assert.Equal("Audio2Face can't run there: no graphics card", model.ComputerText("studio"));
        services.Cannot = null;
        Assert.StartsWith("Audio2Face needs ", model.ComputerText("studio"), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyALoopbackAddressWithAPortIsSaved()
    {
        Assert.Equal(new Uri("http://127.0.0.1:52001/"), LipSyncPageModel.ParseEndpoint(" http://127.0.0.1:52001/ "));
        Assert.Null(LipSyncPageModel.ParseEndpoint("http://example.com:52000/"));
        Assert.Null(LipSyncPageModel.ParseEndpoint("not an address"));
    }

    [Fact]
    public void ThisPcCardSaysWhatItsGraphicsCardMeans()
    {
        var model = new LipSyncPageModel(new FakeServices(), null);
        Assert.False(model.ThisPcFits);
        Assert.StartsWith("No dedicated graphics card was found on this PC", model.ThisPcAbout, StringComparison.Ordinal);
        Assert.Contains("Docker Desktop isn't installed yet", model.ThisPcSetUpText, StringComparison.Ordinal);
    }

    private sealed class FakeServices : ILipSyncPageServices
    {
        internal AvatarLipSync? LipSync = AvatarLipSync.Auto;
        internal bool? Answers;
        internal string? Cannot = "no graphics card";

        public string? DataDirectory => null;
        public AvatarLipSync? CharacterLipSync => LipSync;
        public bool? OwnServiceAnswers => Answers;
        public Uri OwnServiceEndpoint(PoolMember? member = null) => CharacterEndpoint;
        public Uri CharacterEndpoint { get; } = new("http://127.0.0.1:52000/");
        public JobCoverage? Problem => null;
        public MachineInfo Machine => MachineInfo.Unknown;
        public PairedHost? ThisPcHost() => null;
        public PairedHost? FindHost(string? hostId) => null;
        public HostCheck? CheckOf(string hostId) => null;
        public bool? Reachable(string hostId) => null;
        public bool? RunsAudio2Face(string hostId) => null;
        public string? CannotHand(string hostId, string roleKind, string job) => Cannot;
        public bool ChangesRolesOn(PairedHost host) => false;
        public PoolList? EnsurePool() => null;
        public Task FollowListAsync(PoolList after) => Task.CompletedTask;
        public void ShowStatus(string text) { }
        public void CheckOwnService() { }
        public void LaunchOnHost(PairedHost host, HostAction action) { }
        public void CheckHost(string hostId) { }
        public void SetUpThisPc() { }
        public void UseHost(string hostId) { }
    }
}