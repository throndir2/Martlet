using System.Text.Json.Nodes;
using System.Windows.Controls;
using Martlet.Core.Cluster;
using Martlet.Core.Pictures;
using Martlet.Core.Settings;
using Martlet.HostClient;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › Picture making (<see cref="PicturesPage"/> in Pages\Pictures). The work the
/// page shows stays in the window (MainWindow.Pictures.cs).</summary>
public partial class MainWindow : IPicturesPageServices
{
    private PicturesPage? picturesPage;

    /// <summary>The Picture making page, made once: it keeps the cloud provider chosen in its form while the window lives.</summary>
    private PicturesPage PicturesPage => picturesPage ??= new(this, PoolListCard, () => picturesTestImage);

    string? IPicturesPageServices.DataDirectory => store?.DataDirectory;
    AppSettings? IPicturesPageServices.Settings => homeSettings;
    string? IPicturesPageServices.Pending => picturesPending;
    string? IPicturesPageServices.Failure => picturesFailure;
    string? IPicturesPageServices.FailedOn => picturesFailedOn;
    string? IPicturesPageServices.CheckResult => picturesCheck;
    bool IPicturesPageServices.Checking => picturesChecking;
    bool IPicturesPageServices.Testing => picturesTesting;
    string? IPicturesPageServices.CloudState
    {
        get => picturesCloudState;
        set => picturesCloudState = value;
    }
    string? IPicturesPageServices.ComfyState(string memberKey) => picturesComfyState.GetValueOrDefault(memberKey);
    JsonObject? IPicturesPageServices.ComfyStatus(string memberKey) => picturesComfyStatus.GetValueOrDefault(memberKey);

    PairedHost? IPicturesPageServices.ThisPcHost() => ThisPcHost();
    PairedHost? IPicturesPageServices.FindHost(string? hostId) => FindHost(hostId);
    bool IPicturesPageServices.Offers(PairedHost host, string kind) => Offers(host, kind);
    string? IPicturesPageServices.CannotHand(string hostId, string roleKind, string job) => CannotHand(hostId, roleKind, job);
    bool? IPicturesPageServices.Reachable(string hostId) => hostChecks.GetValueOrDefault(hostId)?.Reachable;
    bool IPicturesPageServices.Checked(string hostId) => hostChecks.ContainsKey(hostId);
    bool IPicturesPageServices.CanAdd(PairedHost host) => !host.Shared && HostCan(host.HostId, HostRoles.Pictures) is not { Allowed: false };
    string? IPicturesPageServices.ThisPcCannotDraw() => ThisPcCannotDraw();

    void IPicturesPageServices.Check(bool test) => CheckPicturesAsync(test).Forget();
    void IPicturesPageServices.SetUp(PairedHost? host) => SetUpPicturesAsync(host).Forget();
    void IPicturesPageServices.ConnectComfy(PoolMember member) => ConnectComfyAsync(member).Forget();
    void IPicturesPageServices.LoadWorkflow(PoolMember member) => LoadPicturesWorkflow(member);
    void IPicturesPageServices.SaveWorkflow(PoolMember member, PictureWorkflow workflow, string checkpoint) => SavePictureWorkflow(member, workflow, checkpoint);
    void IPicturesPageServices.SaveKey(PoolMember member, PasswordBox key) => SavePictureKeyAsync(member, key).Forget();
    void IPicturesPageServices.AddCloud(string provider, string model, PasswordBox key, bool consent) =>
        AddPictureCloudAsync(provider, model, key, consent).Forget();
    Task IPicturesPageServices.ListSaved(PoolList before, PoolList after) => PicturesListSavedAsync(before, after);
    void IPicturesPageServices.MarkEdited() => tabEdited = true;
    void IPicturesPageServices.RedrawLater() => Dispatcher.InvokeAsync(() => { if (!closing && openTab == CompanionTab.Pictures) RenderTab(); });
}
