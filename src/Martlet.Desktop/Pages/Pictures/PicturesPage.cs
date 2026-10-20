using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Martlet.Core.Cluster;
using Martlet.Core.Pictures;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Presentation;
using Martlet.Providers.Pictures;

namespace Martlet.Desktop;

/// <summary>Companion › Picture making (docs/PICTURES.md, an optional extra), the UI only: Now (where it draws, with Check and Draw
/// a test picture), then the Picture making list (<see cref="PoolAreas.PictureMaking"/>, shown with the shared list control,
/// which MainWindow builds: <paramref name="poolList"/>), each place's settings (the Picture making role's state and Set up, a
/// ComfyUI place's workflow, a cloud place's key) and the Add a cloud provider form. What each line says is in
/// <see cref="PicturesPageModel"/>; what the page needs from the app is <see cref="IPicturesPageServices"/>.
/// <paramref name="testImage"/> is the last test picture (it isn't kept). MainWindow keeps one page for the window's life: it
/// keeps the cloud provider chosen in the form. The automation IDs are listed on MainWindow.Pictures.cs.</summary>
internal sealed partial class PicturesPage(IPicturesPageServices services, Func<PoolListOptions, UIElement> poolList, Func<ImageSource?> testImage)
{
    private string cloudProvider = PictureMakingPoolMembers.OpenRouter;

    /// <summary>Draws the page's cards into <paramref name="page"/>.</summary>
    internal void Render(Panel page)
    {
        var model = new PicturesPageModel(services);
        var on = model.On;
        var picture = testImage();

        var now = new TextBlock { Text = model.NowText, FontSize = 15, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(now, "PicturesNow");
        var check = Note(model.CheckText, new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(check, "PicturesTestState");
        var image = new Image { Source = picture, MaxHeight = 280, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0), Visibility = picture is null ? Visibility.Collapsed : Visibility.Visible };
        AutomationProperties.SetAutomationId(image, "PicturesTestImage");
        AutomationProperties.SetName(image, "Test picture");
        page.Children.Add(Card(Heading("Now"), now,
            HelpTip.Explain("Ask Martlet to draw, paint or sketch something. It draws in the background while you keep talking, shows " +
                "the picture in the talk window and keeps it in Creations on all your computers.", new Thickness(0, 4, 0, 0),
                "PicturesAsk", "asking for a picture", wiki: "Pictures-and-Creations#asking-for-a-picture"), check, image,
            Row(on ? PageButton("Check", () => services.Check(false), id: "PicturesCheck") : null,
                on ? PageButton("Draw a test picture", () => services.Check(true), id: "PicturesTest") : null)));

        page.Children.Add(poolList(new PoolListOptions
        {
            Area = PoolAreas.PictureMaking,
            Heading = "Where it draws",
            Intro = "The places that draw pictures, in order. A picture goes to the first one that is free, and open Settings to set " +
                "a place up or choose its workflow. With nothing on, pictures are off; this list stays on this PC, but the pictures " +
                "are shared with all your computers.",
            Wiki = "Pictures-and-Creations#picture-making",
            Status = model.MemberStatus,
            Settings = member => MemberSettings(model, member),
            CanAdd = services.CanAdd,
            NewMember = member => member.Kind == PoolMemberKind.Cloud ? member : member.WithSetting(PoolSettingKeys.Workflow, PictureMakingPoolMembers.ZImageTurbo),
            AddCloud = CloudAddForm,
            Saved = services.ListSaved
        }));
    }

    /// <summary>A place's settings: a cloud provider's key, or the Picture making role's state and Set up, then the workflow.</summary>
    private UIElement? MemberSettings(PicturesPageModel model, PoolMember member)
    {
        if (services.DataDirectory is null) return null;
        var i = model.MemberIndex(member);
        var stack = new StackPanel();
        if (member.Kind == PoolMemberKind.Cloud)
        {
            KeyEditor(model, member, i, stack);
            return stack;
        }
        if (member.Kind is PoolMemberKind.ThisPc or PoolMemberKind.Computer)
        {
            var state = model.RoleState(member);
            var line = Note(MainWindow.Capitalized(state.Text) + ".", new Thickness(0, 0, 0, 4));
            if (state.Cannot is not null) line.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            AutomationProperties.SetAutomationId(line, $"PicturesMemberState-{i}");
            stack.Children.Add(line);
            if (!state.Ready)
            {
                var thisPc = member.Kind == PoolMemberKind.ThisPc;
                var setUp = PageButton(services.Pending == (thisPc ? MainWindow.PicturesThisPc : member.HostId) ? "Setting up..." : "Set up",
                    () => services.SetUp(thisPc ? null : state.Host), primary: true, id: $"PicturesSetUp-{i}");
                AutomationProperties.SetName(setUp, $"Set up Picture making on {(thisPc ? "this PC" : member.HostId)}");
                setUp.IsEnabled = services.Pending is null && state.Cannot is null && (thisPc || state.Host is not null);
                if (state.Cannot is not null)
                {
                    setUp.ToolTip = state.Cannot;
                    ToolTipService.SetShowOnDisabled(setUp, true);
                    AutomationProperties.SetHelpText(setUp, state.Cannot);
                }
                stack.Children.Add(Row(setUp));
                stack.Children.Add(HelpTip.Explain("Set up by Martlet in Docker like its other roles. It shares the graphics card with " +
                    "the voice and listening, uses it only while drawing and frees it a few minutes after the last picture.",
                    new Thickness(0, 4, 0, 8), $"PicturesRole-{i}", "the Picture making role",
                    wiki: "Pictures-and-Creations#set-up-the-picture-making-role"));
            }
        }
        else
            stack.Children.Add(HelpTip.Explain("Start ComfyUI with --listen so Martlet can reach it, but only on your own network, since " +
                "it has no password. Martlet sends it the workflow and the description through ComfyUI's own API and fetches the picture.",
                new Thickness(0, 0, 0, 8), $"PicturesComfy-{i}", "your own ComfyUI", wiki: "Pictures-and-Creations#your-own-comfyui"));
        WorkflowEditor(member, i, stack);
        return stack;
    }

    // A ComfyUI place's workflow: Z-Image Turbo, a checkpoint (Connect lists them) or its own Export (API) file.
    private void WorkflowEditor(PoolMember member, int i, Panel stack)
    {
        var name = PictureMakingPoolMembers.Describe(member);
        var status = services.ComfyStatus(member.Key);
        IReadOnlyList<string> checkpoints = status?["models"]?["checkpoints"] is JsonArray files ? [.. files.Select(f => f?.ToString()).OfType<string>()] : [];
        var workflow = new ComboBox { MinHeight = 30, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
        workflow.Items.Add(new ComboBoxItem { Content = "Z-Image Turbo (its three files in ComfyUI's models folders)", Tag = PictureWorkflow.ZImageTurbo });
        workflow.Items.Add(new ComboBoxItem { Content = "A checkpoint (Stable Diffusion 1.5 or XL)", Tag = PictureWorkflow.Checkpoint });
        workflow.Items.Add(new ComboBoxItem { Content = "My own workflow (Export (API) file)", Tag = PictureWorkflow.Custom });
        workflow.SelectedIndex = (int)(PictureMakingPoolMembers.Workflow(member.Setting(PoolSettingKeys.Workflow)) ?? PictureWorkflow.ZImageTurbo);
        AutomationProperties.SetName(workflow, $"Workflow on {name}");
        AutomationProperties.SetAutomationId(workflow, $"PicturesWorkflow-{i}");
        var checkpoint = new ComboBox { MinHeight = 30, MinWidth = 300, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left, IsEditable = true,
            ItemsSource = checkpoints, Text = member.Setting(PoolSettingKeys.Checkpoint) ?? checkpoints.FirstOrDefault() ?? "" };
        AutomationProperties.SetName(checkpoint, $"Checkpoint on {name}");
        AutomationProperties.SetAutomationId(checkpoint, $"PicturesCheckpoint-{i}");
        var checkpointRow = MainWindow.Labeled("Checkpoint", checkpoint);
        var file = member.Setting(PoolSettingKeys.File);
        var custom = file is null ? null : PicturesSettings.LoadWorkflow(services.DataDirectory!, file);
        var customNote = custom is null
            ? HelpTip.Explain("In ComfyUI, open your workflow and use Workflow › Export (API), then load that file. Put {{prompt}} where " +
                "the description goes (and {{negative}}, {{seed}}, {{width}}, {{height}} if you like), or Martlet fills the sampler's " +
                "prompt itself.", new Thickness(28, 6, 0, 0), $"PicturesWorkflowFile-{i}", "your own workflow",
                wiki: "Pictures-and-Creations#your-own-comfyui")
            : Note($"Its workflow is loaded ({custom.Count} nodes).", new Thickness(28, 6, 0, 0));
        var load = PageButton(custom is null ? "Load workflow file…" : "Load another file…", () => services.LoadWorkflow(member), link: true,
            id: $"PicturesLoadWorkflow-{i}");
        var customPanel = new StackPanel { Children = { customNote, Row(load) } };
        void Show()
        {
            var chosen = (PictureWorkflow)((ComboBoxItem)workflow.SelectedItem).Tag;
            checkpointRow.Visibility = chosen == PictureWorkflow.Checkpoint ? Visibility.Visible : Visibility.Collapsed;
            customPanel.Visibility = chosen == PictureWorkflow.Custom ? Visibility.Visible : Visibility.Collapsed;
        }
        Show();
        workflow.SelectionChanged += (_, _) => { services.MarkEdited(); Show(); };
        var comfyState = Note(services.ComfyState(member.Key) ?? "Connect to see the checkpoints and models it has.", new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(comfyState, $"PicturesComfyState-{i}");
        var connect = PageButton("Connect", () => services.ConnectComfy(member), id: $"PicturesComfyConnect-{i}");
        AutomationProperties.SetName(connect, $"See what {name} has");
        var save = PageButton("Save", () => services.SaveWorkflow(member, (PictureWorkflow)((ComboBoxItem)workflow.SelectedItem).Tag, checkpoint.Text.Trim()),
            primary: true, id: $"PicturesSaveSettings-{i}");
        AutomationProperties.SetName(save, $"Save the workflow of {name}");
        stack.Children.Add(MainWindow.Labeled("Workflow", workflow));
        stack.Children.Add(checkpointRow);
        stack.Children.Add(customPanel);
        stack.Children.Add(comfyState);
        stack.Children.Add(Row(connect, save));
    }
}
