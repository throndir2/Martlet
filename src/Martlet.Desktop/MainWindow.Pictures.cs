using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Contracts;
using Martlet.Core.Pictures;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Providers.Pictures;

namespace Martlet.Desktop;

/// <summary>
/// Companion › Pictures (docs/PICTURES.md): where Martlet draws when you ask (draw_picture), this PC's own choice
/// (pictures.json): off, Martlet's <c>pictures</c> host role (ComfyUI with Z-Image Turbo, set up on this PC or another of your
/// computers with the same martlet-host flow as every role, reached through its gateway), a ComfyUI you run at an address (any
/// computer; Z-Image Turbo, one of its checkpoints or your own workflow exported with Export (API)), OpenRouter or NVIDIA Build
/// (paid; Pictures' own key or Thinking's for the same provider). Check and Draw a test picture try the saved choice. Automation
/// IDs: <c>PicturesNow</c>, <c>PicturesPlace-&lt;place&gt;</c>, <c>PicturesHost-&lt;host&gt;</c>, <c>PicturesHostState</c>,
/// <c>PicturesSetUp</c>, <c>PicturesUseHost</c>, <c>PicturesComfyAddress</c>, <c>PicturesComfyConnect</c>,
/// <c>PicturesComfyState</c>, <c>PicturesWorkflow</c>, <c>PicturesCheckpoint</c>, <c>PicturesLoadWorkflow</c>,
/// <c>PicturesUseComfy</c>, <c>PicturesModel</c>, <c>PicturesKey</c>, <c>PicturesKeyStatus</c>, <c>PicturesConsent</c>,
/// <c>PicturesUseCloud</c>, <c>PicturesTurnOff</c>, <c>PicturesCheck</c>, <c>PicturesTest</c>, <c>PicturesTestState</c> and
/// <c>PicturesTestImage</c>.
/// </summary>
public partial class MainWindow
{
    internal const string PicturesThisPc = "this-pc";
    internal const string PicturesTestPrompt =
        "A small songbird with a bright red breast perched on a mossy branch at sunrise, soft watercolour, warm light";

    internal static readonly IReadOnlyList<string> PicturesFeatures =
        ["NVIDIA GPU 8 GB+, shared", "Docker", "Z-Image Turbo, Apache-2.0", "A few seconds per picture", "Frees the card when idle"];

    private const string PicturesTerms =
        "It builds a container with ComfyUI (GPL-3.0) and PyTorch, then downloads about 20 GB of pinned Z-Image Turbo files from " +
        "huggingface.co/Comfy-Org/z_image_turbo (Apache-2.0): the diffusion model, its Qwen3 4B text encoder and the VAE. The " +
        "descriptions Martlet writes go to that computer. Don't use it for anything illegal or harmful or to depict real people " +
        "without their consent, and say pictures are AI-generated when you share them.";

    private PicturePlace? picturesShown;
    private string? picturesHost;
    private string? picturesPending;
    private string? picturesFailure;
    private string? picturesCheck;
    private bool picturesChecking, picturesTesting;
    private System.Windows.Media.ImageSource? picturesTestImage;
    private string? picturesComfyState;
    private JsonObject? picturesComfyStatus;
    private string? picturesComfyFor;

    private void RenderPicturesTab(Panel page)
    {
        var saved = store is null ? new PicturesSettings() : PicturesSettings.Load(store.DataDirectory);
        var place = picturesShown ?? saved.Place;

        var now = new TextBlock { Text = PicturesNow(saved), FontSize = 15, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(now, "PicturesNow");
        var check = Note(picturesChecking ? picturesTesting ? "Drawing a test picture…" : "Checking…"
            : picturesCheck ?? (saved.On ? "Press Check to see whether it can draw now." : ""), new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(check, "PicturesTestState");
        var image = new Image { Source = picturesTestImage, MaxHeight = 280, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0), Visibility = picturesTestImage is null ? Visibility.Collapsed : Visibility.Visible };
        AutomationProperties.SetAutomationId(image, "PicturesTestImage");
        AutomationProperties.SetName(image, "Test picture");
        page.Children.Add(Card(Heading("Now"), now,
            Note("Ask Martlet to draw, paint or sketch something. It draws in the background while you keep talking, shows the picture " +
                "in the talk window and keeps it in Creations on all your computers.", new Thickness(0, 4, 0, 0)), check, image,
            Row(saved.On || PictureClient.Fixture ? PageButton("Check", () => CheckPicturesAsync(test: false).Forget(), id: "PicturesCheck") : null,
                saved.On || PictureClient.Fixture ? PageButton("Draw a test picture", () => CheckPicturesAsync(test: true).Forget(), id: "PicturesTest") : null)));

        var where = new StackPanel();
        where.Children.Add(Heading("Where it draws"));
        where.Children.Add(Note("Pictures are drawn where you choose. This choice stays on this PC; the pictures are shared with all your computers.",
            new Thickness(0, 0, 0, 10)));
        foreach (var (value, label, detail) in new (PicturePlace, string, string)[]
        {
            (PicturePlace.Off, "Off", "Martlet doesn't offer to draw."),
            (PicturePlace.Host, "Martlet's Pictures role (recommended)", "ComfyUI with Z-Image Turbo on this PC or another of your computers with an NVIDIA graphics card. Private and free."),
            (PicturePlace.ComfyUi, "My own ComfyUI", "A ComfyUI you already run, on this PC or another machine, at its address. Use its models or your own workflow."),
            (PicturePlace.OpenRouter, "OpenRouter", "Many image models in the cloud. Each picture costs money."),
            (PicturePlace.NvidiaBuild, "NVIDIA Build", "FLUX models in NVIDIA's cloud with your NVIDIA API key.")
        })
        {
            var option = Choice("PicturesPlace", label + (value == saved.Place ? "  \u00b7  in use" : ""), detail, value == place, "PicturesPlace-" + value);
            option.Checked += (_, _) =>
            {
                picturesShown = value;
                RenderTab();
            };
            where.Children.Add(option);
        }
        page.Children.Add(Card(where));

        page.Children.Add(place switch
        {
            PicturePlace.Host => PicturesHostCard(saved),
            PicturePlace.ComfyUi => PicturesComfyCard(saved),
            PicturePlace.OpenRouter or PicturePlace.NvidiaBuild => PicturesCloudCard(saved, place),
            _ => Card(Heading("Off"), Note("Martlet won't offer to draw pictures. Pictures it drew before stay in Creations.", new Thickness(0, 0, 0, 0)),
                Row(saved.Place == PicturePlace.Off ? null
                    : PageButton("Turn pictures off", () => SavePictures(new PicturesSettings(), "Pictures are off."), primary: true, id: "PicturesTurnOff")))
        });
    }

    private static string PicturesNow(PicturesSettings saved) => PictureClient.Fixture
        ? "FIXTURE - NOT AI: pictures come from the test picture maker (MARTLET_PICTURES_FIXTURE)."
        : saved.On ? $"Martlet draws on {saved.Describe()}." : "Off. Martlet doesn't offer to draw pictures.";

    /// <summary>Saves this PC's choice; the next reply offers draw_picture (or stops offering it).</summary>
    private bool SavePictures(PicturesSettings next, string done)
    {
        if (store is null) return false;
        try
        {
            if (!next.Save(store.DataDirectory)) throw new InvalidOperationException("Couldn't save Pictures. Check access to Martlet's data folder.");
        }
        catch (Exception error) when (error is ContractException or InvalidOperationException)
        {
            ActionText.Text = error.Message;
            return false;
        }
        picturesShown = null;
        picturesCheck = null;
        picturesTestImage = null;
        ErrorLog.Info($"Pictures: now {next.Describe()}.");
        ActionText.Text = done;
        if (!closing && openTab == CompanionTab.Pictures) RenderTab();
        return true;
    }

    // ---------- Martlet's Pictures role ----------

    private Border PicturesHostCard(PicturesSettings saved)
    {
        var stack = new List<UIElement>
        {
            Heading("Martlet's Pictures role"),
            Note("ComfyUI with Z-Image Turbo (Apache-2.0), set up by Martlet in Docker like its other roles. It shares the graphics card " +
                "with the voice and listening, uses it only while drawing and frees it a few minutes after the last picture.",
                new Thickness(0, 0, 0, 4))
        };
        var thisPc = ThisPcHost();
        var others = NetworkMap.Hosts(Inputs()).Where(h => h.HostId != thisPc?.HostId).ToArray();
        var draws = others.FirstOrDefault(h => Offers(h, HostRoles.Pictures));
        var shown = picturesHost ?? (saved.Place == PicturePlace.Host && saved.HostId is { } chosen
            ? thisPc?.HostId == chosen ? PicturesThisPc : chosen
            : thisPc is not null && Offers(thisPc, HostRoles.Pictures) ? PicturesThisPc : draws?.HostId ?? PicturesThisPc);
        if (others.Length > 0)
        {
            var pills = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
            foreach (var (id, label) in new[] { (PicturesThisPc, "This PC") }.Concat(others.Select(h => (h.HostId, h.HostId))))
            {
                var pill = new RadioButton { Content = label, GroupName = "PicturesHost", IsChecked = id == shown };
                pill.SetResourceReference(StyleProperty, "FilterPill");
                AutomationProperties.SetName(pill, label);
                AutomationProperties.SetAutomationId(pill, "PicturesHost-" + id);
                pill.Checked += (_, _) =>
                {
                    if (picturesHost == id) return;
                    picturesHost = id;
                    RenderTab();
                };
                pills.Children.Add(pill);
            }
            stack.Add(pills);
        }
        var onThisPc = shown == PicturesThisPc;
        var target = onThisPc ? thisPc : FindHost(shown);
        var where = onThisPc ? "this PC" : shown;
        var ready = target is not null && Offers(target, HostRoles.Pictures);
        var pending = picturesPending == shown;
        var inUse = saved.Place == PicturePlace.Host && target is not null && saved.HostId == target.HostId;
        var cannot = ready ? null : onThisPc ? ThisPcCannotDraw() : CannotHand(shown, HostRoles.Pictures, "pictures");
        var state = ready ? $"Ready on {where}." + (inUse ? " Martlet draws there." : "")
            : pending ? $"Setting up on {where}..."
            : cannot ?? (picturesFailure is { } failed ? $"Setup failed on {where}: {failed}" : $"Not set up on {where} yet.");
        var title = OptionTitle("Pictures", ready ? "ready" : null, 15);
        AutomationProperties.SetAutomationId(title, "PicturesEngine");
        var chips = Chips("pictures", PicturesFeatures, feature => feature switch
        {
            "NVIDIA GPU 8 GB+, shared" => "12 GB or more is faster. It doesn't need a card of its own: it uses the card only while drawing.",
            "Z-Image Turbo, Apache-2.0" => "ComfyUI itself is GPL-3.0.",
            _ => null
        });
        AutomationProperties.SetAutomationId(chips, "PicturesFeatures");
        var stateLine = Note(state, new Thickness(0, 4, 0, 0));
        if (cannot is not null || picturesFailure is not null && !pending && !ready) stateLine.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(stateLine, "PicturesHostState");
        var setUp = PageButton(ready ? "Ready" : pending ? "Setting up..." : "Set up", () => SetUpPicturesAsync(onThisPc ? null : target).Forget(),
            primary: !ready, id: "PicturesSetUp");
        setUp.IsEnabled = !ready && !pending && cannot is null && picturesPending is null;
        if (cannot is not null)
        {
            setUp.ToolTip = cannot;
            ToolTipService.SetShowOnDisabled(setUp, true);
            AutomationProperties.SetHelpText(setUp, cannot);
        }
        var text = new StackPanel { Children = { title, chips, stateLine } };
        setUp.VerticalAlignment = VerticalAlignment.Top;
        setUp.Margin = new Thickness(12, 0, 0, 0);
        var row = new DockPanel();
        DockPanel.SetDock(setUp, Dock.Right);
        row.Children.Add(setUp);
        row.Children.Add(text);
        var option = new Border { Child = row, BorderThickness = new Thickness(ready ? 2 : 1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 8, 0, 0) };
        option.SetResourceReference(Border.BorderBrushProperty, ready ? "AccentBrush" : "BorderBrush");
        stack.Add(option);
        if (ready && !inUse)
            stack.Add(Row(PageButton($"Draw on {where}", () => SavePictures(new PicturesSettings
            {
                Place = PicturePlace.Host, HostId = target!.HostId, Workflow = PictureWorkflow.ZImageTurbo, ChosenAt = DateTimeOffset.Now
            }, $"Martlet now draws on {where}. Ask it to draw you something."), primary: true, id: "PicturesUseHost")));
        return Card([.. stack]);
    }

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
    /// licences and terms, through the same martlet-host add as every role, then has Martlet draw there.</summary>
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
        picturesPending = host?.HostId ?? PicturesThisPc;
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
                else UsePicturesHost(host.HostId, host.HostId);
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
                Dispatcher.Invoke(() => UsePicturesHost(pc.HostId, "this PC"));
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

    private void UsePicturesHost(string hostId, string where) =>
        SavePictures(new PicturesSettings { Place = PicturePlace.Host, HostId = hostId, Workflow = PictureWorkflow.ZImageTurbo, ChosenAt = DateTimeOffset.Now },
            $"Pictures are ready on {where}. Ask Martlet to draw you something.");

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

    // ---------- the owner's ComfyUI ----------

    private Border PicturesComfyCard(PicturesSettings saved)
    {
        var mine = saved.Place == PicturePlace.ComfyUi ? saved : null;
        var address = new TextBox { MaxLength = 512, Width = 420, HorizontalAlignment = HorizontalAlignment.Left,
            Text = picturesComfyFor ?? mine?.Address ?? PicturesSettings.DefaultComfyAddress };
        AutomationProperties.SetName(address, "ComfyUI address");
        AutomationProperties.SetAutomationId(address, "PicturesComfyAddress");
        var state = Note(picturesComfyState ?? "Connect to see the models it has.", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(state, "PicturesComfyState");
        var workflow = new ComboBox { MinHeight = 30, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
        workflow.Items.Add(new ComboBoxItem { Content = "Z-Image Turbo (its three files in ComfyUI's models folders)", Tag = PictureWorkflow.ZImageTurbo });
        workflow.Items.Add(new ComboBoxItem { Content = "A checkpoint (Stable Diffusion 1.5 or XL)", Tag = PictureWorkflow.Checkpoint });
        workflow.Items.Add(new ComboBoxItem { Content = "My own workflow (Export (API) file)", Tag = PictureWorkflow.Custom });
        workflow.SelectedIndex = (int)(mine?.Workflow ?? (Checkpoints().Count > 0 && !HasZImage() ? PictureWorkflow.Checkpoint : PictureWorkflow.ZImageTurbo));
        AutomationProperties.SetName(workflow, "Workflow");
        AutomationProperties.SetAutomationId(workflow, "PicturesWorkflow");
        var checkpoint = new ComboBox { MinHeight = 30, MinWidth = 300, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left, IsEditable = true,
            ItemsSource = Checkpoints(), Text = mine?.Checkpoint ?? Checkpoints().FirstOrDefault() ?? "" };
        AutomationProperties.SetName(checkpoint, "Checkpoint");
        AutomationProperties.SetAutomationId(checkpoint, "PicturesCheckpoint");
        var checkpointRow = Labeled("Checkpoint", checkpoint);
        var custom = store is null ? null : PicturesSettings.LoadWorkflow(store.DataDirectory);
        var customNote = Note(custom is null
            ? "In ComfyUI, open your workflow and use Workflow › Export (API), then load that file. Put {{prompt}} where the description " +
              "goes (and {{negative}}, {{seed}}, {{width}}, {{height}} if you like); otherwise Martlet fills the sampler's prompt itself."
            : $"Your workflow is loaded ({custom.Count} nodes).", new Thickness(28, 6, 0, 0));
        var load = PageButton(custom is null ? "Load workflow file…" : "Load another file…", LoadPicturesWorkflow, link: true, id: "PicturesLoadWorkflow");
        var customPanel = new StackPanel { Children = { customNote, Row(load) } };
        void Show()
        {
            var chosen = (PictureWorkflow)((ComboBoxItem)workflow.SelectedItem).Tag;
            checkpointRow.Visibility = chosen == PictureWorkflow.Checkpoint ? Visibility.Visible : Visibility.Collapsed;
            customPanel.Visibility = chosen == PictureWorkflow.Custom ? Visibility.Visible : Visibility.Collapsed;
        }
        Show();
        workflow.SelectionChanged += (_, _) => { tabEdited = true; Show(); };
        return Card(Heading("My own ComfyUI"),
            Note("Martlet sends ComfyUI the workflow and the description through ComfyUI's own API and fetches the picture. Start ComfyUI " +
                "with --listen so other computers can reach it (it has no password, so only on your own network).", new Thickness(0, 0, 0, 8)),
            new Label { Content = "_Address", Target = address, Padding = new Thickness(0, 0, 0, 4) }, address, state,
            Row(PageButton("Connect", () => ConnectComfyAsync(address.Text).Forget(), id: "PicturesComfyConnect")),
            Labeled("Workflow", workflow), checkpointRow, customPanel,
            Row(PageButton("Draw with this ComfyUI", () =>
            {
                try
                {
                    var chosen = (PictureWorkflow)((ComboBoxItem)workflow.SelectedItem).Tag;
                    if (chosen == PictureWorkflow.Custom && (store is null || PicturesSettings.LoadWorkflow(store.DataDirectory) is null))
                        throw new ContractException(ErrorCode.InvalidContract, "Load your workflow file first.");
                    var name = checkpoint.Text.Trim();
                    if (chosen == PictureWorkflow.Checkpoint && name.Length == 0)
                        throw new ContractException(ErrorCode.InvalidContract, "Choose a checkpoint first (Connect lists them).");
                    var url = PicturesSettings.ComfyAddress(address.Text);
                    SavePictures(new PicturesSettings
                    {
                        Place = PicturePlace.ComfyUi, Address = url, Workflow = chosen,
                        Checkpoint = chosen == PictureWorkflow.Checkpoint ? name : null, ChosenAt = DateTimeOffset.Now
                    }, $"Martlet now draws with ComfyUI at {url}. Ask it to draw you something.");
                }
                catch (ContractException error) { ActionText.Text = error.Message; }
            }, primary: true, id: "PicturesUseComfy")));

        IReadOnlyList<string> Checkpoints() =>
            picturesComfyStatus?["models"]?["checkpoints"] is JsonArray files ? [.. files.Select(f => f?.ToString()).OfType<string>()] : [];
        bool HasZImage() => picturesComfyStatus?["models"]?["diffusion_models"] is JsonArray files &&
            files.Any(f => f?.ToString() == ComfyWorkflows.ZImageModel);
    }

    /// <summary>Connects to the ComfyUI at <paramref name="text"/> and shows its version and what it can draw with.</summary>
    private async Task ConnectComfyAsync(string text)
    {
        string url;
        try { url = PicturesSettings.ComfyAddress(text); }
        catch (ContractException error) { ActionText.Text = error.Message; return; }
        picturesComfyFor = url;
        picturesComfyState = $"Connecting to {url}…";
        RenderTab();
        try
        {
            using var api = new ComfyHttpApi(url);
            var status = await api.StatusAsync(lifetime.Token);
            picturesComfyStatus = status;
            var checkpoints = status["models"]?["checkpoints"] is JsonArray c ? c.Count : 0;
            var zImage = new ComfyPictureMaker(api, PictureWorkflow.ZImageTurbo).Missing(status) is null;
            picturesComfyState = $"Connected: ComfyUI {status["comfyui_version"]?.ToString() ?? "(version unknown)"} with " +
                $"{checkpoints} checkpoint{(checkpoints == 1 ? "" : "s")}; Z-Image Turbo {(zImage ? "is there" : "isn't there")}.";
            ErrorLog.Info($"Pictures: connected to ComfyUI at {url} ({checkpoints} checkpoints, Z-Image Turbo {(zImage ? "there" : "missing")}).");
        }
        catch (OperationCanceledException) { return; }
        catch (PictureException error)
        {
            picturesComfyStatus = null;
            picturesComfyState = error.Message + " Is ComfyUI running, started with --listen, and is the address right?";
        }
        if (!closing && openTab == CompanionTab.Pictures) RenderTab();
    }

    private void LoadPicturesWorkflow()
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
            if (!PicturesSettings.SaveWorkflow(store.DataDirectory, workflow)) throw new IOException("Couldn't save the workflow in Martlet's data folder.");
            ActionText.Text = $"Loaded your workflow ({workflow.Count} nodes). Press Draw with this ComfyUI to use it.";
            ErrorLog.Info($"Pictures: loaded a ComfyUI workflow with {workflow.Count} nodes.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ContractException or PictureException)
        {
            ActionText.Text = error.Message;
        }
        if (!closing && openTab == CompanionTab.Pictures) RenderTab();
    }

    // ---------- cloud providers ----------

    private Border PicturesCloudCard(PicturesSettings saved, PicturePlace place)
    {
        var name = place == PicturePlace.OpenRouter ? "OpenRouter" : "NVIDIA Build";
        var mine = saved.Place == place ? saved : null;
        var draft = new PicturesSettings { Place = place };
        var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var model = new TextBox { MaxLength = 128, Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Text = mine?.Model ?? draft.Model };
        AutomationProperties.SetName(model, "Picture model ID");
        AutomationProperties.SetAutomationId(model, "PicturesModel");
        var key = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(key, $"{name} API key");
        AutomationProperties.SetAutomationId(key, "PicturesKey");
        var keyStatus = Note(mine?.CredentialId is not null ? $"Its {name} key is saved. Leave this empty to keep it, or paste a new key."
            : draft.UsesThinkingKey(thinking) ? $"Leave this empty to use Thinking's {name} key, or paste another key."
            : $"Paste your {name} API key. Martlet saves it in Windows Credential Manager.", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(keyStatus, "PicturesKeyStatus");
        var consentText = new TextBlock { TextWrapping = TextWrapping.Wrap,
            Text = $"I choose {name} for pictures. The description of each picture goes there, and each picture may cost money." };
        var consent = new CheckBox { Content = consentText, Margin = new Thickness(0, 12, 0, 8), IsChecked = mine is not null };
        AutomationProperties.SetAutomationId(consent, "PicturesConsent");
        model.TextChanged += (_, _) => { if (model.IsKeyboardFocusWithin) { tabEdited = true; consent.IsChecked = false; } };
        key.PasswordChanged += (_, _) => tabEdited = true;
        return Card(Heading(name),
            Note(place == PicturePlace.OpenRouter
                ? "Any OpenRouter model that draws (openrouter.ai/models, output: image). Martlet asks for one picture at about 1K in the shape it chose."
                : "NVIDIA's FLUX models (build.nvidia.com), with the same nvapi- key as NVIDIA Build's chat models.", new Thickness(0, 0, 0, 8)),
            new Label { Content = "_Model ID", Target = model, Padding = new Thickness(0, 0, 0, 4) }, model,
            new Label { Content = "API _key", Target = key, Padding = new Thickness(0, 8, 0, 4) }, key, keyStatus, consent,
            Row(PageButton($"Draw with {name}", () => SavePicturesCloudAsync(place, model.Text.Trim(), key, consent.IsChecked == true, mine, thinking).Forget(),
                primary: true, id: "PicturesUseCloud")));
    }

    private async Task SavePicturesCloudAsync(PicturePlace place, string model, PasswordBox keyBox, bool consent, PicturesSettings? saved, SetupRoute? thinking)
    {
        if (store is null || closing) return;
        var name = place == PicturePlace.OpenRouter ? "OpenRouter" : "NVIDIA Build";
        if (!consent)
        {
            ActionText.Text = $"Tick the box to confirm {name} for pictures, then press Draw with {name}.";
            return;
        }
        SecretLease? key = null;
        var profile = homeSettings?.Profile.Id ?? Guid.Empty;
        var vault = new WindowsCredentialStore();
        CredentialBinding? written = null;
        try
        {
            Martlet.Core.Settings.ChatCompletionsSetup.ModelId(model);
            using (var entered = keyBox.SecurePassword)
                if (entered.Length > 0) key = TakeKey(keyBox);
            var next = new PicturesSettings { Place = place, ModelId = model, CredentialId = saved?.CredentialId, ChosenAt = DateTimeOffset.Now };
            if (key is null && next.CredentialId is null && !next.UsesThinkingKey(thinking))
                throw new ContractException(ErrorCode.InvalidContract, $"Paste your {name} API key first.");
            if (key is not null)
            {
                if (profile == Guid.Empty) throw new InvalidOperationException("Set up Martlet first, then add the key.");
                next = next with { CredentialId = Guid.NewGuid() };
                var binding = next.Binding(profile, next.CredentialId!.Value);
                var lease = key;
                var error = await Task.Run(() => vault.Write(binding, lease), lifetime.Token);
                if (error != CredentialError.None) throw new InvalidOperationException(CredentialMessages.Describe(error));
                written = binding;
            }
            var old = PicturesSettings.Load(store.DataDirectory);
            if (!SavePictures(next, $"Martlet now draws with {name} ({model})." + (key is null ? "" : " Its API key is saved in Windows Credential Manager.")))
                return;
            written = null;
            if (old.CredentialId is { } oldKey && oldKey != next.CredentialId && old.Origin is not null && profile != Guid.Empty)
            {
                var oldBinding = old.Binding(profile, oldKey);
                await Task.Run(() => vault.Delete(oldBinding), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is ContractException or InvalidOperationException)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            if (written is { } orphan) vault.Delete(orphan);
            key?.Dispose();
        }
    }

    // ---------- Check and Draw a test picture ----------

    /// <summary>Checks the saved choice can draw now; with <paramref name="test"/>, draws one test picture there and shows it here
    /// (it isn't kept). A cloud provider asks first, since a picture costs money.</summary>
    private async Task CheckPicturesAsync(bool test)
    {
        if (store is null || picturesChecking || closing) return;
        var saved = PictureClient.Settings(store.DataDirectory);
        if (test && saved.Cloud && !PictureClient.Fixture &&
            !ConfirmationDialog.Confirm(this, $"Draw a test picture with {saved.Describe()}? It may cost money.", "Draw a test picture", "Draw", "Cancel"))
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
            var availability = await maker.GetAvailabilityAsync(lifetime.Token);
            if (!availability.Available) picturesCheck = availability.Reason;
            else if (!test) picturesCheck = $"Ready: {maker.Where} can draw now.";
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
