using Martlet.Avatars;
using Martlet.Core.Cluster;
using Martlet.HostClient;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › Motion (<see cref="MotionPage"/> in Pages\Motion). The work the page shows
/// stays in the window (MainWindow.Motion.cs).</summary>
public partial class MainWindow : IMotionPageServices
{
    private MotionPage? motionPage;

    /// <summary>The Motion page, made once.</summary>
    private MotionPage MotionPage => motionPage ??= new(this, PoolListCard);

    string? IMotionPageServices.DataDirectory => store?.DataDirectory;
    AvatarRenderer? IMotionPageServices.ShownRenderer => (avatar.IsShowing ? avatar.InspectedProfile : homeAvatar)?.Renderer;
    string? IMotionPageServices.Pending => motionPendingHost;
    string? IMotionPageServices.Failure(string key) => motionFailures.GetValueOrDefault(key);

    PairedHost? IMotionPageServices.ThisPcHost() => ThisPcHost();
    PairedHost? IMotionPageServices.FindHost(string? hostId) => FindHost(hostId);
    bool IMotionPageServices.Offers(PairedHost host, string kind) => Offers(host, kind);
    string? IMotionPageServices.CannotHand(string hostId, string roleKind, string job) => CannotHand(hostId, roleKind, job);
    bool? IMotionPageServices.Reachable(string hostId) => hostChecks.GetValueOrDefault(hostId)?.Reachable;
    string? IMotionPageServices.OfferedModel(string hostId) => hostChecks.GetValueOrDefault(hostId)?.Offers?.GetValueOrDefault(HostRoles.Motion);
    bool IMotionPageServices.CanAdd(PairedHost host) => !host.Shared && HostCan(host.HostId, HostRoles.Motion) is not { Allowed: false };
    string? IMotionPageServices.ThisPcCannotMove() => ThisPcCannotMove();

    void IMotionPageServices.EnsurePool() => EnsureMotionPool();
    void IMotionPageServices.OpenEmotes() => OpenCompanion(CompanionTab.Emotes);
    void IMotionPageServices.SetUp(PairedHost? host, string key) => SetUpMotionAsync(host, key).Forget();
    void IMotionPageServices.ShowSettings(string memberKey) => poolExpanded.Add(PoolAreas.Motion.Id + " " + memberKey);
}
