using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Pictures;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Providers.Pictures;

namespace Martlet.Desktop;

/// <summary>
/// Companion › Pictures (docs/PICTURES.md, an optional extra): where Martlet draws when you ask (draw_picture). In the standard
/// order: Now (where it draws, with Check and Draw a test picture, which try the list), then the Pictures list
/// (<see cref="PoolAreas.Pictures"/>, this PC's own pools-local.json, shown with the shared list control
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

    private const string PicturesTerms =
        "It builds a container with ComfyUI (GPL-3.0) and PyTorch, then downloads about 20 GB of pinned Z-Image Turbo files from " +
        "huggingface.co/Comfy-Org/z_image_turbo (Apache-2.0): the diffusion model, its Qwen3 4B text encoder and the VAE. The " +
        "descriptions Martlet writes go to that computer. Don't use it for anything illegal or harmful or to depict real people " +
        "without their consent, and say pictures are AI-generated when you share them.";

    private string? picturesPending;
    private string? picturesFailure;
    private string? picturesFailedOn;
    private string? picturesCheck;
    private bool picturesChecking, picturesTesting;
    private System.Windows.Media.ImageSource? picturesTestImage;
    // What Connect found on each ComfyUI place, by its member key.
    private readonly Dictionary<string, string> picturesComfyState = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> picturesComfyStatus = new(StringComparer.Ordinal);
    private string picturesCloudProvider = PicturePoolMembers.OpenRouter;
    // What the last Add of a cloud provider did, or why it didn't (never a key).
    private string? picturesCloudState;

    private void RenderPicturesTab(Panel page)
    {
        var directory = store?.DataDirectory;
        var list = directory is null ? new PoolList { Area = PoolAreas.Pictures.Id } : PictureClient.List(directory);
        IReadOnlyList<PoolMember> order = directory is null ? [] : PictureClient.Order(directory).Members;
        var on = PictureClient.Fixture || order.Count > 0;

        var now = new TextBlock { Text = PicturesNow(list, order), FontSize = 15, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(now, "PicturesNow");
        var check = Note(picturesChecking ? picturesTesting ? "Drawing a test picture…" : "Checking…"
            : picturesCheck ?? (on ? "Press Check to see whether each place can draw now." : ""), new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(check, "PicturesTestState");
        var image = new Image { Source = picturesTestImage, MaxHeight = 280, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0), Visibility = picturesTestImage is null ? Visibility.Collapsed : Visibility.Visible };
        AutomationProperties.SetAutomationId(image, "PicturesTestImage");
        AutomationProperties.SetName(image, "Test picture");
        page.Children.Add(Card(Heading("Now"), now,
            Note("Ask Martlet to draw, paint or sketch something. It draws in the background while you keep talking, shows the picture " +
                "in the talk window and keeps it in Creations on all your computers.", new Thickness(0, 4, 0, 0)), check, image,
            Row(on ? PageButton("Check", () => CheckPicturesAsync(test: false).Forget(), id: "PicturesCheck") : null,
                on ? PageButton("Draw a test picture", () => CheckPicturesAsync(test: true).Forget(), id: "PicturesTest") : null)));

        page.Children.Add(PoolListCard(new PoolListOptions
        {
            Area = PoolAreas.Pictures,
            Heading = "Where it draws",
            Intro = "The places that draw pictures, in order. A picture goes to the first one that is free; when all are busy, it waits " +
                "for the one with the shortest line. Each place keeps its own settings: open Settings to set up the Pictures role there " +
                "or choose its workflow. With nothing on, pictures are off. This list stays on this PC; the pictures are shared with " +
                "all your computers.",
            Status = PictureMemberStatus,
            Settings = PictureMemberSettings,
            CanAdd = host => !host.Shared && HostCan(host.HostId, HostRoles.Pictures) is not { Allowed: false },
            NewMember = member => member.Kind == PoolMemberKind.Cloud ? member : member.WithSetting(PoolSettingKeys.Workflow, PicturePoolMembers.ZImageTurbo),
            AddCloud = PictureCloudAddForm,
            Saved = PicturesListSavedAsync
        }));
    }

    private static string PicturesNow(PoolList list, IReadOnlyList<PoolMember> order)
    {
        if (PictureClient.Fixture) return "FIXTURE - NOT AI: pictures come from the test picture maker (MARTLET_PICTURES_FIXTURE).";
        if (order.Count == 0)
            return list.Members.Count == 0 ? $"Off. {OptionalExtras.OffMeans(CompanionTab.Pictures)}. Add a place below to turn pictures on."
                : "Off: no place in the Pictures list is on and usable on this PC.";
        var first = $"Martlet draws on {PicturePoolMembers.Describe(order[0])}.";
        return order.Count == 1 ? first
            : first + $" When it is busy, {string.Join(" or ", order.Skip(1).Select(PicturePoolMembers.Describe))} draws instead (it tries them in this order).";
    }

    // A place's position in the saved list, for its settings' automation IDs.
    private int PictureMemberIndex(PoolMember member) =>
        store is null ? -1 : PictureClient.List(store.DataDirectory).Members.ToList().FindIndex(m => m.Key == member.Key);

    /// <summary>Saves the Pictures list after a change made in a place's settings, and draws the page again after the click.</summary>
    private void SavePictureList(PoolList next, string done)
    {
        if (store is null) return;
        if (!PictureClient.SaveList(store.DataDirectory, next))
        {
            ActionText.Text = "Couldn't save the Pictures list on this PC. Check access to Martlet's data folder.";
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
        var area = PoolAreas.Pictures.Id;
        var keys = PoolKeys.Load(store.DataDirectory);
        var profile = homeSettings?.Profile.Id ?? Guid.Empty;
        var vault = new WindowsCredentialStore();
        var changed = false;
        foreach (var member in before.Members.Where(m => m.Kind == PoolMemberKind.Cloud && after.Find(m.Key) is null))
        {
            if (keys.For(area, member.Key) is not { } id) continue;
            keys = keys.With(area, member.Key, null);
            changed = true;
            if (profile != Guid.Empty && PicturePoolMembers.Place(member, null, null) is { } place)
            {
                var binding = place.Binding(profile, id);
                await Task.Run(() => vault.Delete(binding), CancellationToken.None);
            }
            ErrorLog.Info($"Pictures: removed {PicturePoolMembers.Describe(member)}'s own key with it.");
        }
        if (changed && !keys.Save(store.DataDirectory)) throw new IOException("Couldn't save pool-keys.json.");
    }

    // ---------- each place's state and settings ----------

    private string? PictureMemberStatus(PoolMember member)
    {
        var parts = new List<string>();
        if (member.Kind is PoolMemberKind.ThisPc or PoolMemberKind.Computer) parts.Add(PictureRoleState(member).Text);
        if (PicturePoolMembers.WorkflowText(member) is { } workflow) parts.Add("draws with " + workflow);
        if (member.Kind == PoolMemberKind.Cloud) parts.Add("each picture may cost money; " + PictureKeyText(member));
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>Where Martlet's Pictures role stands for a this-PC or computer place: its words, whether it is ready, the paired
    /// host and why it can't run there (null when it can, or Martlet can't tell yet).</summary>
    private (string Text, bool Ready, PairedHost? Host, string? Cannot) PictureRoleState(PoolMember member)
    {
        var thisPc = member.Kind == PoolMemberKind.ThisPc;
        var host = thisPc ? ThisPcHost() : FindHost(member.HostId);
        var where = thisPc ? "this PC" : member.HostId!;
        var key = thisPc ? PicturesThisPc : member.HostId;
        if (picturesPending == key) return ($"setting up on {where}...", false, host, null);
        if (!thisPc && host is null) return ($"{where} isn't paired with this PC", false, null, $"{where} isn't paired with this PC.");
        if (host is not null && Offers(host, HostRoles.Pictures)) return ("Pictures role ready", true, host, null);
        var cannot = thisPc ? ThisPcCannotDraw() : CannotHand(member.HostId!, HostRoles.Pictures, "pictures");
        if (cannot is not null) return ("can't draw there: " + cannot.TrimEnd('.'), false, host, cannot);
        if (picturesFailure is { } failed && picturesFailedOn == key) return ("setup failed: " + failed.TrimEnd('.'), false, host, null);
        if (host is null) return ("not set up yet (open Settings to set it up)", false, null, null);
        return (hostChecks.GetValueOrDefault(host.HostId)?.Reachable == false ? "not reachable right now"
            : hostChecks.ContainsKey(host.HostId) ? "Pictures role not set up yet (open Settings to set it up)" : "not checked yet", false, host, null);
    }

    private UIElement? PictureMemberSettings(PoolMember member)
    {
        if (store is null) return null;
        var i = PictureMemberIndex(member);
        var stack = new StackPanel();
        if (member.Kind == PoolMemberKind.Cloud)
        {
            PictureKeyEditor(member, i, stack);
            return stack;
        }
        if (member.Kind is PoolMemberKind.ThisPc or PoolMemberKind.Computer)
        {
            var state = PictureRoleState(member);
            var line = Note(Capitalized(state.Text) + ".", new Thickness(0, 0, 0, 4));
            if (state.Cannot is not null) line.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            AutomationProperties.SetAutomationId(line, $"PicturesMemberState-{i}");
            stack.Children.Add(line);
            if (!state.Ready)
            {
                var thisPc = member.Kind == PoolMemberKind.ThisPc;
                var setUp = PageButton(picturesPending == (thisPc ? PicturesThisPc : member.HostId) ? "Setting up..." : "Set up",
                    () => SetUpPicturesAsync(thisPc ? null : state.Host).Forget(), primary: true, id: $"PicturesSetUp-{i}");
                AutomationProperties.SetName(setUp, $"Set up Pictures on {(thisPc ? "this PC" : member.HostId)}");
                setUp.IsEnabled = picturesPending is null && state.Cannot is null && (thisPc || state.Host is not null);
                if (state.Cannot is not null)
                {
                    setUp.ToolTip = state.Cannot;
                    ToolTipService.SetShowOnDisabled(setUp, true);
                    AutomationProperties.SetHelpText(setUp, state.Cannot);
                }
                stack.Children.Add(Row(setUp));
                stack.Children.Add(Note("Set up by Martlet in Docker like its other roles. It shares the graphics card with the voice and " +
                    "listening, uses it only while drawing and frees it a few minutes after the last picture.", new Thickness(0, 4, 0, 8)));
            }
        }
        else
            stack.Children.Add(Note("Martlet sends ComfyUI the workflow and the description through ComfyUI's own API and fetches the " +
                "picture. Start ComfyUI with --listen so other computers can reach it (it has no password, so only on your own network).",
                new Thickness(0, 0, 0, 8)));
        PictureWorkflowEditor(member, i, stack);
        return stack;
    }

    // A ComfyUI place's workflow: Z-Image Turbo, a checkpoint (Connect lists them) or its own Export (API) file.
    private void PictureWorkflowEditor(PoolMember member, int i, Panel stack)
    {
        var name = PicturePoolMembers.Describe(member);
        var status = picturesComfyStatus.GetValueOrDefault(member.Key);
        IReadOnlyList<string> checkpoints = status?["models"]?["checkpoints"] is JsonArray files ? [.. files.Select(f => f?.ToString()).OfType<string>()] : [];
        var workflow = new ComboBox { MinHeight = 30, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
        workflow.Items.Add(new ComboBoxItem { Content = "Z-Image Turbo (its three files in ComfyUI's models folders)", Tag = PictureWorkflow.ZImageTurbo });
        workflow.Items.Add(new ComboBoxItem { Content = "A checkpoint (Stable Diffusion 1.5 or XL)", Tag = PictureWorkflow.Checkpoint });
        workflow.Items.Add(new ComboBoxItem { Content = "My own workflow (Export (API) file)", Tag = PictureWorkflow.Custom });
        workflow.SelectedIndex = (int)(PicturePoolMembers.Workflow(member.Setting(PoolSettingKeys.Workflow)) ?? PictureWorkflow.ZImageTurbo);
        AutomationProperties.SetName(workflow, $"Workflow on {name}");
        AutomationProperties.SetAutomationId(workflow, $"PicturesWorkflow-{i}");
        var checkpoint = new ComboBox { MinHeight = 30, MinWidth = 300, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left, IsEditable = true,
            ItemsSource = checkpoints, Text = member.Setting(PoolSettingKeys.Checkpoint) ?? checkpoints.FirstOrDefault() ?? "" };
        AutomationProperties.SetName(checkpoint, $"Checkpoint on {name}");
        AutomationProperties.SetAutomationId(checkpoint, $"PicturesCheckpoint-{i}");
        var checkpointRow = Labeled("Checkpoint", checkpoint);
        var file = member.Setting(PoolSettingKeys.File);
        var custom = file is null ? null : PicturesSettings.LoadWorkflow(store!.DataDirectory, file);
        var customNote = Note(custom is null
            ? "In ComfyUI, open your workflow and use Workflow › Export (API), then load that file. Put {{prompt}} where the description " +
              "goes (and {{negative}}, {{seed}}, {{width}}, {{height}} if you like); otherwise Martlet fills the sampler's prompt itself."
            : $"Its workflow is loaded ({custom.Count} nodes).", new Thickness(28, 6, 0, 0));
        var load = PageButton(custom is null ? "Load workflow file…" : "Load another file…", () => LoadPicturesWorkflow(member), link: true,
            id: $"PicturesLoadWorkflow-{i}");
        var customPanel = new StackPanel { Children = { customNote, Row(load) } };
        void Show()
        {
            var chosen = (PictureWorkflow)((ComboBoxItem)workflow.SelectedItem).Tag;
            checkpointRow.Visibility = chosen == PictureWorkflow.Checkpoint ? Visibility.Visible : Visibility.Collapsed;
            customPanel.Visibility = chosen == PictureWorkflow.Custom ? Visibility.Visible : Visibility.Collapsed;
        }
        Show();
        workflow.SelectionChanged += (_, _) => { tabEdited = true; Show(); };
        var comfyState = Note(picturesComfyState.GetValueOrDefault(member.Key) ?? "Connect to see the checkpoints and models it has.", new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(comfyState, $"PicturesComfyState-{i}");
        var connect = PageButton("Connect", () => ConnectComfyAsync(member).Forget(), id: $"PicturesComfyConnect-{i}");
        AutomationProperties.SetName(connect, $"See what {name} has");
        var save = PageButton("Save", () => SavePictureWorkflow(member, (PictureWorkflow)((ComboBoxItem)workflow.SelectedItem).Tag, checkpoint.Text.Trim()),
            primary: true, id: $"PicturesSaveSettings-{i}");
        AutomationProperties.SetName(save, $"Save the workflow of {name}");
        stack.Children.Add(Labeled("Workflow", workflow));
        stack.Children.Add(checkpointRow);
        stack.Children.Add(customPanel);
        stack.Children.Add(comfyState);
        stack.Children.Add(Row(connect, save));
    }

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
        var next = PicturePoolMembers.WithWorkflow(current, workflow, checkpoint, file);
        SavePictureList(list.With(next), $"{Capitalized(PicturePoolMembers.Describe(next))} now draws with {PicturePoolMembers.WorkflowText(next)}.");
    }

    /// <summary>Asks a ComfyUI place what it has: its version, checkpoints and whether Z-Image Turbo is there. Reads only.</summary>
    private async Task ConnectComfyAsync(PoolMember member)
    {
        if (store is null) return;
        var where = PicturePoolMembers.Describe(member);
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
            picturesComfyState[member.Key] = error.Message +
                (member.Kind == PoolMemberKind.Address ? " Is ComfyUI running, started with --listen, and is the address right?" : "");
        }
        finally { (api as IDisposable)?.Dispose(); }
        if (!closing && openTab == CompanionTab.Pictures) RenderTab();
    }

    /// <summary>Loads a ComfyUI Export (API) file as this place's own workflow (its own file in the data folder) and uses it there.</summary>
    private void LoadPicturesWorkflow(PoolMember member)
    {
        if (store is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Load a ComfyUI workflow (Export (API))", Filter = "ComfyUI API workflow (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > PicturesSettings.MaximumWorkflowBytes)
                throw new ContractException(ErrorCode.InvalidContract, "That workflow is too large (256 KB at most).");
            var workflow = JsonNode.Parse(File.ReadAllBytes(dialog.FileName)) as JsonObject ??
                throw new ContractException(ErrorCode.InvalidContract, "That file isn't a ComfyUI workflow.");
            // Fails clearly for a UI-format workflow (nodes/links) or one with nowhere to put the description.
            _ = ComfyWorkflows.Fill(workflow, new PictureRequest { Prompt = "test" }, 1);
            var file = PicturePoolMembers.WorkflowFileFor(member);
            if (!PicturesSettings.SaveWorkflow(store.DataDirectory, workflow, file)) throw new IOException("Couldn't save the workflow in Martlet's data folder.");
            var list = PictureClient.List(store.DataDirectory);
            if (list.Find(member.Key) is not { } current) return;
            var next = PicturePoolMembers.WithWorkflow(current, PictureWorkflow.Custom, null, file);
            SavePictureList(list.With(next), $"Loaded your workflow ({workflow.Count} nodes) for {PicturePoolMembers.Describe(member)}; it draws with it now.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ContractException or PictureException)
        {
            ActionText.Text = error.Message;
        }
    }

    // ---------- cloud providers ----------

    private string PictureKeyText(PoolMember member)
    {
        var name = PicturePoolMembers.ProviderName(member.Provider);
        var place = store is null ? null : PictureClient.CloudPlace(store.DataDirectory, member);
        var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        return place?.CredentialId is not null ? $"its own {name} key is saved"
            : place?.UsesThinkingKey(thinking) == true ? $"it uses Thinking's {name} key"
            : $"no {name} key yet (add one in its settings)";
    }

    // A cloud place's settings: its key (its model is part of the place: another model is another place).
    private void PictureKeyEditor(PoolMember member, int i, Panel stack)
    {
        var name = PicturePoolMembers.ProviderName(member.Provider);
        var key = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(key, $"{name} API key for pictures");
        AutomationProperties.SetAutomationId(key, $"PicturesKey-{i}");
        key.PasswordChanged += (_, _) => tabEdited = true;
        var status = Note(Capitalized(PictureKeyText(member)) + ". Paste a key to save it as its own.", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(status, $"PicturesKeyStatus-{i}");
        var save = PageButton("Save key", () => SavePictureKeyAsync(member, key).Forget(), primary: true, id: $"PicturesSaveKey-{i}");
        AutomationProperties.SetName(save, $"Save the {name} key for pictures");
        stack.Children.Add(Note($"Its model is part of the place. To draw with another {name} model, add {name} again with that model.",
            new Thickness(0, 0, 0, 8)));
        stack.Children.Add(new Label { Content = "API _key", Target = key, Padding = new Thickness(0, 0, 0, 4) });
        stack.Children.Add(key);
        stack.Children.Add(status);
        stack.Children.Add(Row(save));
    }

    /// <summary>Saves a pasted key as a cloud place's own (Windows Credential Manager; its reference in pool-keys.json) and
    /// deletes the key it had before.</summary>
    private async Task SavePictureKeyAsync(PoolMember member, PasswordBox box)
    {
        if (store is null || closing) return;
        var name = PicturePoolMembers.ProviderName(member.Provider);
        var profile = homeSettings?.Profile.Id ?? Guid.Empty;
        if (profile == Guid.Empty) { ActionText.Text = "Set up Martlet first, then add the key."; return; }
        if (PicturePoolMembers.Place(member, null, null) is not { } place) return;
        using (var entered = box.SecurePassword)
            if (entered.Length == 0) { ActionText.Text = $"Paste your {name} API key first."; return; }
        var key = TakeKey(box);
        var vault = new WindowsCredentialStore();
        CredentialBinding? written = null;
        try
        {
            var id = Guid.NewGuid();
            var binding = place.Binding(profile, id);
            var error = await Task.Run(() => vault.Write(binding, key), lifetime.Token);
            if (error != CredentialError.None) throw new InvalidOperationException(CredentialMessages.Describe(error));
            written = binding;
            var keys = PoolKeys.Load(store.DataDirectory);
            var old = keys.For(PoolAreas.Pictures.Id, member.Key);
            if (!keys.With(PoolAreas.Pictures.Id, member.Key, id).Save(store.DataDirectory)) throw new InvalidOperationException("Couldn't save the key's reference on this PC.");
            written = null;
            if (old is { } oldId)
            {
                var oldBinding = place.Binding(profile, oldId);
                await Task.Run(() => vault.Delete(oldBinding), CancellationToken.None);
            }
            ErrorLog.Info($"Pictures: saved an own key for {PicturePoolMembers.Describe(member)}.");
            ActionText.Text = $"Saved the {name} key for pictures in Windows Credential Manager.";
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException error) { ActionText.Text = error.Message; }
        finally
        {
            if (written is { } orphan) vault.Delete(orphan);
            key.Dispose();
            if (!closing && openTab == CompanionTab.Pictures) RenderTab();
        }
    }

    /// <summary>The list's Add a cloud provider form: OpenRouter or NVIDIA Build, its model, a key (or Thinking's for the same
    /// provider) and the owner's agreement to send descriptions there and pay. Add puts it last in the list.</summary>
    private UIElement PictureCloudAddForm()
    {
        var provider = picturesCloudProvider;
        var name = PicturePoolMembers.ProviderName(provider);
        var draft = new PicturesSettings { Place = provider == PicturePoolMembers.NvidiaBuild ? PicturePlace.NvidiaBuild : PicturePlace.OpenRouter };
        var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var choice = new ComboBox { MinHeight = 30, MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
        choice.Items.Add(new ComboBoxItem { Content = "OpenRouter", Tag = PicturePoolMembers.OpenRouter });
        choice.Items.Add(new ComboBoxItem { Content = "NVIDIA Build", Tag = PicturePoolMembers.NvidiaBuild });
        choice.SelectedIndex = provider == PicturePoolMembers.NvidiaBuild ? 1 : 0;
        AutomationProperties.SetName(choice, "Cloud provider for pictures");
        AutomationProperties.SetAutomationId(choice, "PicturesCloudProvider");
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedItem is not ComboBoxItem { Tag: string tag } || tag == picturesCloudProvider) return;
            picturesCloudProvider = tag;
            picturesCloudState = null;
            Dispatcher.InvokeAsync(() => { if (!closing && openTab == CompanionTab.Pictures) RenderTab(); });
        };
        var model = new TextBox { MaxLength = 128, Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Text = draft.Model };
        AutomationProperties.SetName(model, "Picture model ID");
        AutomationProperties.SetAutomationId(model, "PicturesModel");
        var key = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(key, $"{name} API key");
        AutomationProperties.SetAutomationId(key, "PicturesKey");
        var keyStatus = Note(draft.UsesThinkingKey(thinking) ? $"Leave this empty to use Thinking's {name} key, or paste another key."
            : $"Paste your {name} API key. Martlet saves it in Windows Credential Manager.", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(keyStatus, "PicturesKeyStatus");
        var consent = new CheckBox { Margin = new Thickness(0, 12, 0, 8),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap,
                Text = $"I add {name} to the Pictures list. The description of each picture drawn there goes to {name}, and each picture may cost money." } };
        AutomationProperties.SetAutomationId(consent, "PicturesConsent");
        model.TextChanged += (_, _) => { if (model.IsKeyboardFocusWithin) { tabEdited = true; consent.IsChecked = false; } };
        key.PasswordChanged += (_, _) => tabEdited = true;
        var add = PageButton($"Add {name}", () => AddPictureCloudAsync(provider, model.Text.Trim(), key, consent.IsChecked == true).Forget(), id: "PicturesAddCloud");
        var state = Note(picturesCloudState ?? "", new Thickness(0, 4, 0, 0));
        state.Visibility = picturesCloudState is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetAutomationId(state, "PicturesCloudState");
        var stack = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        stack.Children.Add(new TextBlock { Text = "A cloud provider", FontWeight = FontWeights.SemiBold });
        stack.Children.Add(Note(provider == PicturePoolMembers.NvidiaBuild
            ? "NVIDIA's FLUX models (build.nvidia.com), with the same nvapi- key as NVIDIA Build's chat models."
            : "Any OpenRouter model that draws (openrouter.ai/models, output: image). Martlet asks for one picture at about 1K in the shape it chose.",
            new Thickness(0, 2, 0, 8)));
        stack.Children.Add(Labeled("Provider", choice));
        stack.Children.Add(new Label { Content = "_Model ID", Target = model, Padding = new Thickness(0, 8, 0, 4) });
        stack.Children.Add(model);
        stack.Children.Add(new Label { Content = "API _key", Target = key, Padding = new Thickness(0, 8, 0, 4) });
        stack.Children.Add(key);
        stack.Children.Add(keyStatus);
        stack.Children.Add(consent);
        stack.Children.Add(Row(add));
        stack.Children.Add(state);
        return stack;
    }

    private async Task AddPictureCloudAsync(string provider, string model, PasswordBox keyBox, bool consent)
    {
        if (store is null || closing) return;
        var name = PicturePoolMembers.ProviderName(provider);
        if (!consent)
        {
            ActionText.Text = picturesCloudState = $"Tick the box to agree, then press Add {name}.";
            RenderTab();
            return;
        }
        var directory = store.DataDirectory;
        var area = PoolAreas.Pictures.Id;
        var profile = homeSettings?.Profile.Id ?? Guid.Empty;
        var vault = new WindowsCredentialStore();
        SecretLease? key = null;
        CredentialBinding? written = null;
        Guid? id = null;
        try
        {
            ChatCompletionsSetup.ModelId(model);
            var member = PoolMember.Cloud(provider, model).WithConsent(area, DateTimeOffset.UtcNow);
            var list = PictureClient.List(directory);
            if (list.Find(member.Key) is not null)
                throw new ContractException(ErrorCode.InvalidContract, $"{PicturePoolMembers.Describe(member)} is in the Pictures list already.");
            var place = PicturePoolMembers.Place(member, null, null)!;
            var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
            using (var entered = keyBox.SecurePassword)
                if (entered.Length > 0) key = TakeKey(keyBox);
            if (key is null && !place.UsesThinkingKey(thinking)) throw new ContractException(ErrorCode.InvalidContract, $"Paste your {name} API key first.");
            if (key is not null)
            {
                if (profile == Guid.Empty) throw new InvalidOperationException("Set up Martlet first, then add the key.");
                id = Guid.NewGuid();
                var binding = place.Binding(profile, id.Value);
                var lease = key;
                var error = await Task.Run(() => vault.Write(binding, lease), lifetime.Token);
                if (error != CredentialError.None) throw new InvalidOperationException(CredentialMessages.Describe(error));
                written = binding;
                if (!PoolKeys.Load(directory).With(area, member.Key, id).Save(directory))
                    throw new InvalidOperationException("Couldn't save the key's reference on this PC.");
            }
            if (!PictureClient.SaveList(directory, list.With(member)))
            {
                if (id is not null) PoolKeys.Load(directory).With(area, member.Key, null).Save(directory);
                throw new InvalidOperationException("Couldn't save the Pictures list on this PC.");
            }
            written = null;
            picturesCheck = null;
            var done = $"{PicturePoolMembers.Describe(member)} is now last in the Pictures list. Move it up to draw there sooner." +
                (key is null ? "" : " Its API key is saved in Windows Credential Manager.");
            ErrorLog.Info($"Pictures: added {PicturePoolMembers.Describe(member)} to the Pictures list (the owner agreed to pay).");
            ActionText.Text = picturesCloudState = done;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is ContractException or InvalidOperationException)
        {
            ActionText.Text = picturesCloudState = error.Message;
        }
        finally
        {
            if (written is { } orphan) vault.Delete(orphan);
            key?.Dispose();
            if (!closing && openTab == CompanionTab.Pictures) RenderTab();
        }
    }

    // ---------- Martlet's Pictures role ----------

    /// <summary>Why this PC can't run the Pictures role: no NVIDIA graphics card, or one with less than 8 GB; null when it can or
    /// Martlet hasn't read this PC's hardware yet.</summary>
    private string? ThisPcCannotDraw()
    {
        if (ReferenceEquals(machine, MachineInfo.Unknown)) return null;
        if (machine.ArmRefusal("pictures") is { } arm) return arm + " Use another computer, your own ComfyUI or a cloud provider.";
        var nvidia = machine.Gpus.Where(g => g.IsNvidia).OrderByDescending(g => g.MemoryGb ?? 0).FirstOrDefault();
        if (nvidia is null)
            return $"Needs an NVIDIA graphics card; this PC has {(machine.Gpus.Count == 0 ? "none" : string.Join(", ", machine.Gpus.Select(g => g.Describe())))}. " +
                "Use another computer, your own ComfyUI or a cloud provider.";
        return nvidia.MemoryGb is { } gb && gb < 7.5 ? $"Needs an NVIDIA graphics card with 8 GB+; this PC has {nvidia.Describe()}." : null;
    }

    /// <summary>Sets the pictures role up on <paramref name="host"/> (null: this PC) after one confirmation naming its downloads,
    /// licences and terms, through the same martlet-host add as every role; that place in the list then draws.</summary>
    private async Task SetUpPicturesAsync(PairedHost? host)
    {
        if (store is null || setupService is null || closing || picturesPending is not null) return;
        var where = host is null ? "this PC" : host.HostId;
        if (host is null && ThisPcCannotDraw() is { } cannot) { ActionText.Text = $"Pictures can't run on this PC. {cannot}"; return; }
        var thisPc = ThisPcHost();
        if (!ConfirmationDialog.Confirm(this,
                $"Set up Pictures on {where}?\n\n" +
                (host is null && thisPc is null
                    ? machine.DockerInstalled ? "Martlet first sets up its host service in Docker Desktop. "
                        : "Martlet first installs Docker Desktop (it asks for its own terms) and sets up its host service. "
                    : "") +
                "It needs an NVIDIA graphics card with 8 GB+ (12 GB+ is faster) and is a large download; Martlet shows the progress.\n\n" +
                PicturesTerms, "Set up Pictures"))
            return;
        var answers = new Dictionary<string, string>(StringComparer.Ordinal) { ["choice.PICTURES_MODEL"] = "z-image-turbo" };
        picturesFailure = null;
        picturesPending = picturesFailedOn = host?.HostId ?? PicturesThisPc;
        RenderTab();
        try
        {
            if (host is not null)
            {
                var done = await RunHostActionAsync(host, HostRoles.Get(HostRoles.Pictures).Add, answers);
                if (closing) return;
                if (done is null) picturesFailure = $"Setting it up on {host.HostId} stopped. Its run window has details.";
                else if (await WaitForPicturesAsync(host, lifetime.Token) is { } why)
                    picturesFailure = $"Setup finished, but Martlet can't see Pictures on {host.HostId} yet ({why}). Check it in a minute.";
                else UsePicturesHost(host.HostId, host.HostId, thisPc: false);
                return;
            }
            string? stopped = null;
            async Task<string> Continue(HostRunWindow run, PairedHost pc)
            {
                var target = pc.Target(Version);
                await HostLocal.EnsureDockerAsync(run, Martlet.Core.Installation.ContinueSetupKind.Docker);
                target = await HostLocal.EngineForChangeAsync(target, HostRoles.Pictures, run);
                var inputs = await HostLocal.DescribeAsync(target, HostRoles.Pictures, run.Output, run.Token);
                var chosen = HostInputDialog.WithGpu(run, "this PC", "Pictures", inputs, answers) ?? throw new OperationCanceledException();
                run.Status("Installing Pictures on this PC (a large download)...");
                var exit = await HostLocal.EngineAsync(target, ["add", HostRoles.Pictures], SetupProgress(run, "Pictures", why => stopped ??= why),
                    run.Token, answers: chosen);
                if (exit != 0)
                    throw new InvalidOperationException($"Installing Pictures stopped{(stopped is null ? $" (exit {exit})" : ": " + stopped)}. The output has details.");
                run.Status("Checking Pictures on this PC...");
                if (await WaitForPicturesAsync(pc, run.Token) is { } why)
                    throw new InvalidOperationException(stopped = $"Setup finished, but Martlet can't see Pictures yet ({why}). Check this PC in a minute.");
                Dispatcher.Invoke(() => UsePicturesHost(pc.HostId, "this PC", thisPc: true));
                return "Pictures are ready on this PC. Ask Martlet to draw you something.";
            }
            string? status;
            if (thisPc is not null)
                status = await HostRunWindow.RunAsync(this, "Set up Pictures on this PC", run => Continue(run, thisPc)) ??
                    (stopped is null ? "Pictures weren't set up. The run window has details." : $"Pictures weren't set up: {stopped}");
            else
            {
                (_, status) = await HostsWindow.SetUpThisPcAsync(this, new AvatarProfileStore(store.DataDirectory), setupService,
                    text => ActionText.Text = text, lifetime.Token, Continue, "Set up Pictures on this PC");
                await ReadMachineAsync();
            }
            if (!closing && status is not null)
            {
                ActionText.Text = status;
                if (!status.StartsWith("Pictures are ready", StringComparison.Ordinal)) picturesFailure = status;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or
            JsonException or ArgumentException or Audio2FaceHostException || ClusterSync.IsHostFailure(error))
        {
            picturesFailure = error.Message;
            ActionText.Text = error.Message;
        }
        finally
        {
            picturesPending = null;
            if (!closing && openTab is not null) RenderTab();
        }
    }

    /// <summary>The Pictures role is ready on <paramref name="hostId"/>: its place in the list is on (added last when it isn't
    /// there yet).</summary>
    private void UsePicturesHost(string hostId, string where, bool thisPc)
    {
        if (store is null) return;
        var done = $"Pictures are ready on {where}. Ask Martlet to draw you something.";
        var list = PictureClient.List(store.DataDirectory);
        var member = (thisPc ? list.Find(PoolMember.ThisPcKey) : null) ?? list.Find(PoolMember.Computer(hostId).Key);
        if (member is { Off: false })
        {
            ActionText.Text = done;
            ErrorLog.Info($"Pictures: the Pictures role is ready on {where}.");
            return;
        }
        member = member is null
            ? (thisPc ? PoolMember.ThisPc() : PoolMember.Computer(hostId)).WithSetting(PoolSettingKeys.Workflow, PicturePoolMembers.ZImageTurbo)
            : member with { Off = false };
        SavePictureList(list.With(member), done);
    }

    /// <summary>After martlet-host added the role, checks <paramref name="host"/> (about a minute) until its gateway offers the
    /// pictures route and its ComfyUI answers ready. Returns null once it does, else what it saw last.</summary>
    private async Task<string?> WaitForPicturesAsync(PairedHost host, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            var check = await HostControl.CheckAsync(host.Pairing, HardwareStore, token);
            hostChecks[host.HostId] = check;
            var why = check.Text;
            if (check.Routes?.Any(r => r.RouteId == Audio2FaceHostConnection.PictureRouteId) == true && store is not null)
            {
                using var api = new GatewayComfyApi(store.DataDirectory, host.HostId);
                try
                {
                    var status = await api.StatusAsync(token);
                    if (status["state"]?.ToString() == "ready") return null;
                    why = $"its ComfyUI is {status["state"]}";
                }
                catch (PictureException error) { why = error.Message; }
            }
            if (attempt >= 12) return why;
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
    }

    // ---------- Check and Draw a test picture ----------

    /// <summary>Check asks each place in the list whether it can draw now (a cloud provider: only whether a key is there). Draw a
    /// test picture draws one picture where the list sends it and shows it here (it isn't kept); when the list has a cloud
    /// provider, it asks first, since a picture there costs money.</summary>
    private async Task CheckPicturesAsync(bool test)
    {
        if (store is null || picturesChecking || closing) return;
        if (test && PictureClient.FirstPaid(store.DataDirectory) is { } paid && !PictureClient.Fixture &&
            !ConfirmationDialog.Confirm(this, $"Draw a test picture? It may go to {paid}, which may cost money.", "Draw a test picture", "Draw", "Cancel"))
            return;
        var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var maker = PictureClient.For(store.DataDirectory, homeSettings?.Profile.Id ?? Guid.Empty, thinking);
        if (maker is null) return;
        picturesChecking = true;
        picturesTesting = test;
        picturesTestImage = null;
        RenderTab();
        try
        {
            if (!test)
            {
                IReadOnlyList<IPictureMaker> places = maker is PicturePool pool ? [.. pool.Members.Select(m => m.Maker)] : [maker];
                List<string> lines = [];
                foreach (var place in places)
                {
                    var availability = await place.GetAvailabilityAsync(lifetime.Token);
                    lines.Add(availability.Available ? $"Ready: {place.Where} can draw now." : (availability.Reason ?? $"{place.Where} can't draw now.").TrimEnd('.') + ".");
                }
                picturesCheck = string.Join(" ", lines);
            }
            else
            {
                var availability = await maker.GetAvailabilityAsync(lifetime.Token);
                if (!availability.Available) picturesCheck = availability.Reason;
                else
                {
                    var result = await maker.GenerateAsync(new PictureRequest { Prompt = PicturesTestPrompt, Shape = PictureShape.Landscape }, null, lifetime.Token);
                    picturesTestImage = await Task.Run(() => PictureView.Decode(result.Image, 840));
                    picturesCheck = $"Drew a {result.Width}x{result.Height} test picture on {result.Where} in {result.Took.TotalSeconds:0.0} s" +
                        (result.Fixture ? " (FIXTURE - NOT AI)." : ".");
                    ErrorLog.Info($"Pictures: test picture on {result.Where}: {result.Width}x{result.Height} {result.MediaType}, {result.Image.Length / 1024} KiB, " +
                        $"{result.Took.TotalSeconds:0.0} s.");
                    PictureClient.FreeLater(maker);
                }
            }
        }
        catch (OperationCanceledException) { return; }
        catch (PictureException error)
        {
            picturesCheck = error.Message;
            ErrorLog.Info($"Pictures: {(test ? "test picture" : "check")} on {maker.Where} failed ({error.Code}: {error.Message}).");
        }
        finally
        {
            picturesChecking = false;
            (maker as IDisposable)?.Dispose();
            if (!closing && openTab == CompanionTab.Pictures) RenderTab();
        }
    }
}
