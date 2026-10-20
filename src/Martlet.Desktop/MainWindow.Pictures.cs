using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.HostClient;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Pictures;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Presentation;
using Martlet.Providers.Pictures;

namespace Martlet.Desktop;

/// <summary>
/// Companion › Picture making (docs/PICTURES.md, an optional extra): where Martlet draws when you ask (draw_picture). In the standard
/// order: Now (where it draws, with Check and Draw a test picture, which try the list), then the Picture making list
/// (<see cref="PoolAreas.PictureMaking"/>, this PC's own pools-local.json, shown with the shared list control
/// <see cref="PoolListCard"/>): the places a picture tries in order, each with its own settings. A place is Martlet's
/// <c>pictures</c> host role on this PC or another of your computers (ComfyUI with Z-Image Turbo, set up from its settings with
/// the same martlet-host flow as every role), a ComfyUI you run at an address, or OpenRouter or NVIDIA Build (paid; added with
/// the owner's agreement; its own key or Thinking's for the same provider). A ComfyUI place's settings hold its workflow
/// (Z-Image Turbo, a checkpoint or your own Export (API) file); a cloud place's hold its key. With nothing on, pictures are
/// off. Automation IDs: <c>PicturesNow</c>, <c>PicturesTestState</c>, <c>PicturesTestImage</c>, <c>PicturesCheck</c>,
/// <c>PicturesTest</c>, the list's <c>Pool-pictures-...</c>; in a place's settings (i: its place in the list)
/// <c>PicturesMemberState-i</c>, <c>PicturesSetUp-i</c>, <c>PicturesWorkflow-i</c>, <c>PicturesCheckpoint-i</c>,
/// <c>PicturesLoadWorkflow-i</c>, <c>PicturesComfyConnect-i</c>, <c>PicturesComfyState-i</c>, <c>PicturesSaveSettings-i</c>,
/// <c>PicturesKey-i</c>, <c>PicturesKeyStatus-i</c> and <c>PicturesSaveKey-i</c>; and the cloud provider form's
/// <c>PicturesCloudProvider</c>, <c>PicturesModel</c>, <c>PicturesKey</c>, <c>PicturesKeyStatus</c>, <c>PicturesConsent</c>,
/// <c>PicturesAddCloud</c> and <c>PicturesCloudState</c> (what the last Add did, or why it didn't).
/// </summary>
public partial class MainWindow
{
    internal const string PicturesThisPc = "this-pc";
    internal const string PicturesTestPrompt =
        "A small songbird with a bright red breast perched on a mossy branch at sunrise, soft watercolour, warm light";

    // What setting the role up downloads and agrees to, in one sentence; the wiki's
    // Pictures-and-Creations#set-up-the-picture-making-role has every file and licence.
    private const string PicturesTerms =
        "It needs an NVIDIA graphics card with 8 GB+ and downloads about 20 GB (ComfyUI, GPL-3.0, and Z-Image Turbo, Apache-2.0), " +
        "and you may not use it for anything illegal or harmful or to depict real people without their consent.";

    private string? picturesPending;
    private string? picturesFailure;
    private string? picturesFailedOn;
    private string? picturesCheck;
    private bool picturesChecking, picturesTesting;
    private System.Windows.Media.ImageSource? picturesTestImage;
    // What Connect found on each ComfyUI place, by its member key.
    private readonly Dictionary<string, string> picturesComfyState = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> picturesComfyStatus = new(StringComparer.Ordinal);
    // What the last Add of a cloud provider did, or why it didn't (never a key).
    private string? picturesCloudState;

    /// <summary>Saves the Picture making list after a change made in a place's settings, and draws the page again after the click.</summary>
    private void SavePictureList(PoolList next, string done)
    {
        if (store is null) return;
        if (!PictureClient.SaveList(store.DataDirectory, next))
        {
            ActionText.Text = "Couldn't save the Picture making list on this PC: check access to Martlet's data folder.";
            return;
        }
        picturesCheck = null;
        picturesTestImage = null;
        ErrorLog.Info("Pictures: " + done);
        ActionText.Text = done;
        Dispatcher.InvokeAsync(() => { if (!closing && openTab == CompanionTab.Pictures) RenderTab(); });
    }

    /// <summary>The list changed in the list control: a cloud place that left the list takes its own key with it.</summary>
    private async Task PicturesListSavedAsync(PoolList before, PoolList after)
    {
        picturesCheck = null;
        picturesTestImage = null;
        if (store is null) return;
        var area = PoolAreas.PictureMaking.Id;
        var keys = PoolKeys.Load(store.DataDirectory);
        var profile = homeSettings?.Profile.Id ?? Guid.Empty;
        var vault = new WindowsCredentialStore();
        var changed = false;
        foreach (var member in before.Members.Where(m => m.Kind == PoolMemberKind.Cloud && after.Find(m.Key) is null))
        {
            if (keys.For(area, member.Key) is not { } id) continue;
            keys = keys.With(area, member.Key, null);
            changed = true;
            if (profile != Guid.Empty && PictureMakingPoolMembers.Place(member, null, null) is { } place)
            {
                var binding = place.Binding(profile, id);
                await Task.Run(() => vault.Delete(binding), CancellationToken.None);
            }
            ErrorLog.Info($"Pictures: removed {PictureMakingPoolMembers.Describe(member)}'s own key with it.");
        }
        if (changed && !keys.Save(store.DataDirectory)) throw new IOException("Couldn't save pool-keys.json.");
    }

    // ---------- each place's state and settings ----------

    private void SavePictureWorkflow(PoolMember member, PictureWorkflow workflow, string checkpoint)
    {
        if (store is null) return;
        if (workflow == PictureWorkflow.Checkpoint && checkpoint.Length is 0 or > 255)
        {
            ActionText.Text = "Choose a checkpoint first (Connect lists them).";
            return;
        }
        var file = member.Setting(PoolSettingKeys.File);
        if (workflow == PictureWorkflow.Custom && (file is null || PicturesSettings.LoadWorkflow(store.DataDirectory, file) is null))
        {
            ActionText.Text = "Load your workflow file first.";
            return;
        }
        var list = PictureClient.List(store.DataDirectory);
        if (list.Find(member.Key) is not { } current) return;
        var next = PictureMakingPoolMembers.WithWorkflow(current, workflow, checkpoint, file);
        SavePictureList(list.With(next), $"{Capitalized(PictureMakingPoolMembers.Describe(next))} now draws with {PictureMakingPoolMembers.WorkflowText(next)}.");
    }

    /// <summary>Asks a ComfyUI place what it has: its version, checkpoints and whether Z-Image Turbo is there. Reads only.</summary>
    private async Task ConnectComfyAsync(PoolMember member)
    {
        if (store is null) return;
        var where = PictureMakingPoolMembers.Describe(member);
        IComfyApi api;
        try
        {
            api = member.Kind == PoolMemberKind.Address ? new ComfyHttpApi(member.Address!)
                : new GatewayComfyApi(store.DataDirectory, member.Kind == PoolMemberKind.ThisPc ? ThisPcHost()?.HostId : member.HostId);
        }
        catch (ContractException error)
        {
            picturesComfyState[member.Key] = error.Message;
            RenderTab();
            return;
        }
        picturesComfyState[member.Key] = $"Connecting to {where}…";
        RenderTab();
        try
        {
            var status = await api.StatusAsync(lifetime.Token);
            picturesComfyStatus[member.Key] = status;
            var checkpoints = status["models"]?["checkpoints"] is JsonArray c ? c.Count : 0;
            var zImage = new ComfyPictureMaker(api, PictureWorkflow.ZImageTurbo).Missing(status) is null;
            picturesComfyState[member.Key] = $"Connected: ComfyUI {status["comfyui_version"]?.ToString() ?? "(version unknown)"} with " +
                $"{checkpoints} checkpoint{(checkpoints == 1 ? "" : "s")}; Z-Image Turbo {(zImage ? "is there" : "isn't there")}.";
            ErrorLog.Info($"Pictures: connected to {where} ({checkpoints} checkpoints, Z-Image Turbo {(zImage ? "there" : "missing")}).");
        }
        catch (OperationCanceledException) { return; }
        catch (PictureException error)
        {
            picturesComfyStatus.Remove(member.Key);
            picturesComfyState[member.Key] = member.Kind == PoolMemberKind.Address
                ? error.Message.TrimEnd('.') + ": check that ComfyUI runs, started with --listen, and that the address is right."
                : error.Message;
        }
        finally { (api as IDisposable)?.Dispose(); }
        if (!closing && openTab == CompanionTab.Pictures) RenderTab();
    }
}
