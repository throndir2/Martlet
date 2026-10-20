using Martlet.Avatars;
using Martlet.Core.Cluster;
using Martlet.HostClient;

namespace Martlet.Desktop.Tests;

/// <summary>Companion › Motion's page model (Pages\Motion), without WPF.</summary>
public sealed class MotionPageModelTests
{
    [Fact]
    public void MemberLinesSayWhereMotionStandsAndWhetherItCanBeSetUp()
    {
        var services = new FakeServices();
        var model = new MotionPageModel(services);
        Assert.Contains("only on VRM characters", model.CharacterText, StringComparison.Ordinal);
        Assert.True(model.CharacterWarning);

        var computer = PoolMember.Computer("studio");
        Assert.Equal("not paired with this PC", model.MemberStatus(computer));
        var unpaired = model.Settings(computer);
        Assert.Equal("It isn't paired with this PC.", unpaired.Text);
        Assert.True(unpaired.Warning);
        Assert.False(unpaired.CanSetUp);

        var thisPc = PoolMember.ThisPc();
        Assert.Equal(MainWindow.MotionThisPc, model.SetupKey(thisPc));
        Assert.Equal("doesn't run Motion yet; Settings sets it up", model.MemberStatus(thisPc));
        var here = model.Settings(thisPc);
        Assert.Equal("Motion isn't set up here yet.", here.Text);
        Assert.False(here.Warning);
        Assert.True(here.CanSetUp);
        Assert.Null(here.SetUpOn);

        services.Failures[MainWindow.MotionThisPc] = "Docker isn't running.";
        Assert.Equal("doesn't run Motion yet; Settings sets it up; setup failed: Docker isn't running", model.MemberStatus(thisPc));
        Assert.True(model.Settings(thisPc).Warning);
        services.PendingKey = MainWindow.MotionThisPc;
        Assert.Equal("setting Motion up...", model.MemberStatus(thisPc));
        Assert.False(model.Settings(thisPc).CanSetUp);
    }

    private sealed class FakeServices : IMotionPageServices
    {
        internal readonly Dictionary<string, string> Failures = new(StringComparer.Ordinal);
        internal string? PendingKey;

        public string? DataDirectory => null;
        public AvatarRenderer? ShownRenderer => AvatarRenderer.Live2D;
        public string? Pending => PendingKey;
        public string? Failure(string key) => Failures.GetValueOrDefault(key);
        public PairedHost? ThisPcHost() => null;
        public PairedHost? FindHost(string? hostId) => null;
        public bool Offers(PairedHost host, string kind) => false;
        public string? CannotHand(string hostId, string roleKind, string job) => null;
        public bool? Reachable(string hostId) => null;
        public string? OfferedModel(string hostId) => null;
        public bool CanAdd(PairedHost host) => true;
        public string? ThisPcCannotMove() => null;
        public void EnsurePool() { }
        public void OpenEmotes() { }
        public void SetUp(PairedHost? host, string key) { }
        public void ShowSettings(string memberKey) { }
    }
}
