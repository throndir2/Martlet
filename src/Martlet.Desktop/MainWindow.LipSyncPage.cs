using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Platforms;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › Lip sync (<see cref="LipSyncPage"/> in Pages\LipSync). The work the page shows
/// stays in the window: the list and the older choice in step (MainWindow.LipSyncPool.cs), this PC's own Audio2Face check
/// (MainWindow.Coverage.cs) and setting Audio2Face up (MainWindow.WhoDoesWhat.cs).</summary>
public partial class MainWindow : ILipSyncPageServices
{
    private LipSyncPage? lipSyncPage;

    /// <summary>Companion › Lip sync, made once.</summary>
    private LipSyncPage LipSyncPage => lipSyncPage ??= new(this, PoolListCard, SavePoolList);

    string? ILipSyncPageServices.DataDirectory => store?.DataDirectory;
    AvatarLipSync? ILipSyncPageServices.CharacterLipSync => homeAvatar?.LipSync;
    bool? ILipSyncPageServices.OwnServiceAnswers => ownLipSyncAnswers;
    Uri ILipSyncPageServices.OwnServiceEndpoint(PoolMember? member) => LipSyncThisPcEndpoint(member);
    Uri ILipSyncPageServices.CharacterEndpoint => OwnLipSyncEndpoint();
    JobCoverage? ILipSyncPageServices.Problem => coverage.FirstOrDefault(c => c.Job == ClusterJobs.LipSync && c.IsProblem);
    MachineInfo ILipSyncPageServices.Machine => machine;

    PairedHost? ILipSyncPageServices.ThisPcHost() => ThisPcHost();
    PairedHost? ILipSyncPageServices.FindHost(string? hostId) => FindHost(hostId);
    HostCheck? ILipSyncPageServices.CheckOf(string hostId) => hostChecks.GetValueOrDefault(hostId);
    bool? ILipSyncPageServices.Reachable(string hostId) => LipSyncHostReachable(hostId);
    bool? ILipSyncPageServices.RunsAudio2Face(string hostId) =>
        HostServes(hostId, ClusterJobs.LipSync, HostRoles.Get(HostRoles.Audio2Face).RouteId);
    string? ILipSyncPageServices.CannotHand(string hostId, string roleKind, string job) => CannotHand(hostId, roleKind, job);
    bool ILipSyncPageServices.ChangesRolesOn(PairedHost host) => ChangesRolesOn(host);

    PoolList? ILipSyncPageServices.EnsurePool() => EnsureLipSyncPool();
    Task ILipSyncPageServices.FollowListAsync(PoolList after) => FollowLipSyncListAsync(after);
    void ILipSyncPageServices.ShowStatus(string text) => ActionText.Text = text;
    void ILipSyncPageServices.CheckOwnService()
    {
        ownLipSyncAnswers = null;
        CheckOwnLipSyncAsync().Forget();
    }
    void ILipSyncPageServices.LaunchOnHost(PairedHost host, HostAction action) => LaunchOnHost(host, action);
    void ILipSyncPageServices.CheckHost(string hostId) => RunNodeAction(NodeAction.CheckHost, hostId);
    void ILipSyncPageServices.SetUpThisPc() => SetUpThisPcHostAsync(AssignLipSyncAsync).Forget();
    void ILipSyncPageServices.UseHost(string hostId) => AssignLipSyncAsync("host:" + hostId).Forget();
}