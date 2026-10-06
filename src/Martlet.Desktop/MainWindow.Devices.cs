using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The Devices page under the map: the selected device with one row per job it does (and the controls that
/// change it right there), what else it can take on and how to manage it; then the settings for all devices.</summary>
public partial class MainWindow
{
    /// <summary>Commands that give a device something new to do; the rest manage the device itself. Installing and
    /// removing host roles sit in their own group under these.</summary>
    private static readonly HashSet<NodeAction> GiveActions =
    [
        NodeAction.UseForLipSync, NodeAction.UseForThinking, NodeAction.UseForListening, NodeAction.UseForSpeaking,
        NodeAction.LipSyncThisPc, NodeAction.HostThisPc
    ];

    private bool deviceFactsExpanded;
    private bool deviceRolesExpanded;

    private void RenderDetail(NetworkNode node)
    {
        renderingBoard = true;
        try { BuildDetail(node); }
        finally { renderingBoard = false; }
    }

    private void BuildDetail(NetworkNode node)
    {
        DetailContent.Children.Clear();
        AutomationProperties.SetName(DetailContent, $"{node.Title}, {node.Subtitle}");
        DetailContent.Children.Add(DetailHeader(node));
        if (node.Kind == NodeKind.Add)
        {
            AddChoices(node);
            return;
        }
        if (node.PairedHostId is { } updating && hostUpdates.Notes.GetValueOrDefault(updating) is { } update)
            DetailContent.Children.Add(Callout(update, "SelectedDeviceUpdate"));
        if (node.SharedGpu is { } shared)
            DetailContent.Children.Add(Callout(shared, "SelectedDeviceSharedGpu"));

        var rows = node.Roles.Where(r => r.Component != DeviceComponent.App).OrderBy(r => ComponentRank(r.Component)).ToList();
        var components = rows.Select(r => r.Component).OfType<string>().ToHashSet(StringComparer.Ordinal);
        // A command whose role is not listed here (it moved since) applies to the whole device instead.
        string? Placed(NodeCommand command) => command.Component is { } component && components.Contains(component) ? component : null;
        var shown = new HashSet<string>(StringComparer.Ordinal);

        if (rows.Count > 0)
        {
            DetailContent.Children.Add(DetailSection("What it does",
                rows.All(r => r.Component is DeviceComponent.Host or DeviceComponent.Member) ? null
                : node.Kind == NodeKind.Missing ? "Not set up yet." : "Change these jobs here."));
            foreach (var role in rows)
            {
                var commands = role.Component is { } key && shown.Add(key)
                    ? node.Commands.Where(c => Placed(c) == key).ToList() : [];
                DetailContent.Children.Add(ComponentRow(node, role, commands));
            }
        }
        AddResourcesSection(node);

        var loose = node.Commands.Where(c => Placed(c) is null && !(c.Action == NodeAction.CheckHost && node.Kind != NodeKind.ThisPc)).ToList();
        var give = loose.Where(c => GiveActions.Contains(c.Action)).ToList();
        var roles = loose.Where(c => c.Action is NodeAction.InstallRole or NodeAction.ChangeRole or NodeAction.RemoveRole).ToList();
        AddCommandSection("Give it more to do", node.Kind == NodeKind.ThisPc ? "Run more of Martlet on this PC." : "Assign a job or add a role.",
            give, roles);
        AddCommandSection("Manage", null, loose.Where(c => !GiveActions.Contains(c.Action) && !roles.Contains(c)).ToList());

        var notes = node.Notes.Distinct(StringComparer.Ordinal)
            .Where(n => node.PairedHostId is null || n != hostUpdates.Notes.GetValueOrDefault(node.PairedHostId)).ToList();
        if (node.Facts.Count == 0)
        {
            if (notes.Count > 0) DetailContent.Children.Add(Notes(notes, new Thickness(0, 16, 0, 0)));
        }
        else
        {
            var facts = new StackPanel { Margin = new Thickness(4, 4, 0, 4) };
            facts.Children.Add(FactsGrid(node.Facts));
            if (notes.Count > 0) facts.Children.Add(Notes(notes, new Thickness(0, 10, 0, 0)));
            var expander = DetailExpander(node.Kind == NodeKind.ThisPc ? "Hardware and details" : "Details", "DeviceFactsSection", facts, deviceFactsExpanded);
            expander.Expanded += (_, _) => deviceFactsExpanded = true;
            expander.Collapsed += (_, _) => deviceFactsExpanded = false;
            DetailContent.Children.Add(expander);
        }

        if (node.PairedHostId is { } pairedId && node.Kind == NodeKind.Host && FindHost(pairedId) is { } paired &&
            PlatformCatalog.ManagesRolesRemotely(PlatformDevice.FromHost(pairedId, HardwareStore?.Find(pairedId))))
            DetailContent.Children.Add(DetailExpander("Connection method", "DeviceReachSection", ReachEditor(paired), !paired.CanLaunch));
    }

    // ---------- header ----------

    private FrameworkElement DetailHeader(NetworkNode node)
    {
        var header = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (node.Kind != NodeKind.Add)
        {
            var pill = new StackPanel { Orientation = Orientation.Horizontal };
            pill.Children.Add(Dot(node.Health, 9, new Thickness(0, 0, 8, 0)));
            var health = new TextBlock { Text = node.HealthText, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap };
            AutomationProperties.SetAutomationId(health, "SelectedDeviceHealth");
            pill.Children.Add(health);
            if (node.HealthCommand is { } fix)
            {
                // A status one click fixes ("Update available") is a button that runs the fix.
                var arrow = Glyph("\uE76C", 11, new Thickness(8, 1, 0, 0));
                arrow.VerticalAlignment = VerticalAlignment.Center;
                pill.Children.Add(arrow);
                var button = new Button
                {
                    Content = pill, Padding = new Thickness(12, 5, 12, 5), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center,
                    Cursor = System.Windows.Input.Cursors.Hand, ToolTip = fix.Label
                };
                AutomationProperties.SetAutomationId(button, "SelectedDeviceHealthAction");
                AutomationProperties.SetName(button, $"{node.HealthText}: {fix.Label}");
                var action = fix.Action;
                var argument = fix.Argument;
                button.Click += (_, _) => RunNodeAction(action, argument);
                right.Children.Add(button);
            }
            else
            {
                var chip = new Border { Child = pill, Padding = new Thickness(12, 5, 14, 5), Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
                chip.SetResourceReference(StyleProperty, "Chip");
                right.Children.Add(chip);
            }
        }
        // A remote device's connection check sits next to its status; this PC's host service checks from its own row.
        if (node.Kind != NodeKind.ThisPc && node.Commands.FirstOrDefault(c => c.Action == NodeAction.CheckHost) is { } check)
        {
            var button = CommandButton(check);
            button.Margin = new Thickness(12, 0, 0, 0);
            right.Children.Add(button);
        }
        DockPanel.SetDock(right, Dock.Right);
        header.Children.Add(right);

        var bubble = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(28), Margin = new Thickness(0, 0, 16, 0) };
        bubble.SetResourceReference(Border.BackgroundProperty, node.Kind == NodeKind.ThisPc ? "AccentBrush" : "SoftBrush");
        var glyph = Glyph(node.Glyph, 24, default);
        glyph.SetResourceReference(TextBlock.ForegroundProperty, node.Kind == NodeKind.ThisPc ? "OnAccentBrush" : "AccentBrush");
        bubble.Child = glyph;
        FrameworkElement icon = bubble;
        if (node.Kind == NodeKind.Add && node.Commands.FirstOrDefault(c => c.Primary) is { } start)
        {
            // Add a computer's + is a button that adds one, like the first choice under it.
            var button = new Button { Tag = node.Glyph, Margin = bubble.Margin, ToolTip = start.Label };
            button.SetResourceReference(StyleProperty, "BubbleButton");
            AutomationProperties.SetAutomationId(button, "SelectedDeviceAdd");
            AutomationProperties.SetName(button, start.Label);
            var action = start.Action;
            var argument = start.Argument;
            button.Click += (_, _) => RunNodeAction(action, argument);
            icon = button;
        }
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(icon);

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var title = new TextBlock { Text = node.Title, FontSize = 22, FontWeight = FontWeights.SemiBold };
        AutomationProperties.SetAutomationId(title, "SelectedDevice");
        names.Children.Add(title);
        var subtitle = new TextBlock
        {
            Text = node.Roles.Any(r => r.Component == DeviceComponent.App) ? $"{node.Subtitle} \u00b7 Your companion for conversations, microphone and speakers" : node.Subtitle
        };
        subtitle.SetResourceReference(StyleProperty, "Muted");
        names.Children.Add(subtitle);
        header.Children.Add(names);
        return header;
    }

    // ---------- one row per job ----------

    private static int ComponentRank(string? component) => component switch
    {
        DeviceComponent.Member => -1,
        "job:Llm" => 0,
        "job:Stt" => 1,
        "job:Tts" => 2,
        DeviceComponent.LipSync => 3,
        DeviceComponent.Character => 4,
        DeviceComponent.Audio => 5,
        _ when component?.StartsWith("role:", StringComparison.Ordinal) == true => 6,
        DeviceComponent.HostService => 7,
        DeviceComponent.Host => 8,
        _ => 9
    };

    private static string ComponentGlyph(string? component) => component switch
    {
        "job:Llm" or "role:" + HostRoles.Ollama or "role:" + HostRoles.DeepThinking => "\uE82F",
        "job:Stt" or "role:" + HostRoles.Stt => "\uE720",
        "job:Tts" => "\uE767",
        DeviceComponent.LipSync or "role:" + HostRoles.Audio2Face => "\uE76E",
        DeviceComponent.Character => "\uE77B",
        DeviceComponent.Audio => "\uE7F6",
        DeviceComponent.Member => NetworkMap.ThisPcGlyph,
        _ => NetworkMap.ComputerGlyph
    };

    private FrameworkElement ComponentRow(NetworkNode node, HostedRole role, IReadOnlyList<NodeCommand> commands)
    {
        var job = DeviceComponent.JobRole(role.Component) is { } setupRole ? HostJob.For(setupRole) : null;
        var lipSync = role.Component == DeviceComponent.LipSync;
        var clusterJob = job?.Job ?? (lipSync ? ClusterJobs.LipSync : null);
        var companion = Role == DeviceRole.Companion;
        var standby = role.Component?.StartsWith("role:", StringComparison.Ordinal) == true;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var bubble = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        bubble.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        var glyph = Glyph(ComponentGlyph(role.Component), 18, default);
        glyph.SetResourceReference(TextBlock.ForegroundProperty, standby ? "MutedBrush" : "AccentBrush");
        bubble.Child = glyph;
        grid.Children.Add(bubble);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var title = new TextBlock { Text = role.Name, FontSize = 15, FontWeight = FontWeights.SemiBold };
        AutomationProperties.SetAutomationId(title, "DeviceComponent-" + (role.Component ?? role.Chip).Replace(':', '-'));
        text.Children.Add(title);
        var detail = new TextBlock
        {
            // This PC's own Audio2Face service says whether it is running once Martlet has checked it (a host PC's lip-sync row
            // is the one its host service does for your companion PCs).
            Text = lipSync && companion && node.Kind == NodeKind.ThisPc && NetworkMap.LipSync(homeAvatar) == LipSyncHandler.ThisPc && ownLipSyncAnswers is not null
                ? OwnLipSyncState() : role.Detail,
            Margin = new Thickness(0, 2, 0, 0)
        };
        AutomationProperties.SetAutomationId(detail, "DeviceComponentDetail-" + (role.Component ?? role.Chip).Replace(':', '-'));
        detail.SetResourceReference(StyleProperty, "Muted");
        text.Children.Add(detail);
        if (clusterJob is not null && coverage.FirstOrDefault(c => c.Job == clusterJob && c.IsProblem) is { } problem)
        {
            var warning = new TextBlock
            {
                Text = $"{(problem.State == CoverageState.Limited ? "Reduced" : "Not working")}: {problem.Problem}", FontSize = 13, Margin = new Thickness(0, 6, 0, 0)
            };
            warning.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            AutomationProperties.SetAutomationId(warning, "RoleProblem-" + JobCoverageRules.Title(clusterJob));
            text.Children.Add(warning);
        }

        var buttons = new WrapPanel();
        if (companion && node.Kind != NodeKind.Add && (job is not null || lipSync))
        {
            var setUp = node.Kind == NodeKind.Missing;
            var change = new Button
            {
                Content = setUp ? "Set up in Companion"
                    : lipSync && NetworkMap.LipSync(homeAvatar) == LipSyncHandler.Loudness ? "Set up lip-sync here" : "Change in Companion"
            };
            CompactButton(change, setUp);
            AutomationProperties.SetAutomationId(change, "RoleChange-" + (job?.Role.ToString() ?? "LipSync"));
            var tab = job is null ? CompanionTab.LipSync : TabFor(job.Role);
            change.Click += (_, _) => OpenCompanion(tab);
            buttons.Children.Add(change);
        }
        // The row's own Change button opens the job's Companion page, so the map's matching command is not repeated.
        foreach (var command in commands.Where(c => !(c.Action == NodeAction.Companion && (job is not null || lipSync))))
            buttons.Children.Add(CommandButton(command));
        if (buttons.Children.Count > 0) text.Children.Add(buttons);
        if (companion && clusterJob is not null && ClusterControls(clusterJob) is { } cluster)
        {
            cluster.Margin = new Thickness(0, 10, 0, 0);
            text.Children.Add(cluster);
        }

        // Who does the job: switch it to any computer from here (once a host is paired).
        if (companion && node.Kind != NodeKind.Add && (job is not null || lipSync) && NetworkMap.Hosts(Inputs()).Count > 0)
        {
            var owner = new StackPanel { Width = 250, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            var label = new TextBlock { Text = "Handled by", FontSize = 12, Margin = new Thickness(2, 0, 0, 4) };
            label.SetResourceReference(StyleProperty, "Muted");
            owner.Children.Add(label);
            owner.Children.Add(job is not null ? JobChoice(job) : LipSyncChoice());
            Grid.SetColumn(owner, 2);
            grid.Children.Add(owner);
        }

        var row = new Border { Child = grid, CornerRadius = new CornerRadius(16), Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 10) };
        row.SetResourceReference(Border.BackgroundProperty, "CanvasBrush");
        if (node.Kind == NodeKind.Missing) row.SetResourceReference(Border.BorderBrushProperty, "WarningBrush");
        row.BorderThickness = new Thickness(node.Kind == NodeKind.Missing ? 1.5 : 0);
        return row;
    }

    // ---------- device-wide commands ----------

    private void AddCommandSection(string heading, string? hint, IReadOnlyList<NodeCommand> commands, IReadOnlyList<NodeCommand>? roles = null)
    {
        if (commands.Count == 0 && roles is not { Count: > 0 }) return;
        DetailContent.Children.Add(DetailSection(heading, hint));
        if (commands.Count > 0)
        {
            var panel = new WrapPanel { Margin = new Thickness(0, -10, 0, 0) };
            foreach (var command in commands.OrderByDescending(c => c.Primary)) panel.Children.Add(CommandButton(command));
            DetailContent.Children.Add(panel);
        }
        if (roles is not { Count: > 0 }) return;
        var buttons = new WrapPanel { Margin = new Thickness(4, -6, 0, 4) };
        foreach (var command in roles.OrderBy(c => c.Action)) buttons.Children.Add(CommandButton(command));
        var expander = DetailExpander("Host roles", "DeviceRolesSection", buttons, deviceRolesExpanded);
        expander.Margin = new Thickness(-6, commands.Count > 0 ? 10 : 0, 0, 0);
        expander.Expanded += (_, _) => deviceRolesExpanded = true;
        expander.Collapsed += (_, _) => deviceRolesExpanded = false;
        DetailContent.Children.Add(expander);
    }

    private Button CommandButton(NodeCommand command)
    {
        var button = new Button { Content = command.Label };
        CompactButton(button, command.Primary);
        // Several install and remove commands can sit on one device: the role keeps their automation IDs apart.
        AutomationProperties.SetAutomationId(button, $"NodeAction-{command.Action}" +
            (command.Argument?.Split('/') is [_, var kind] ? "-" + kind : ""));
        var action = command.Action;
        var argument = command.Argument;
        button.Click += (_, _) => RunNodeAction(action, argument);
        return button;
    }

    private static void CompactButton(Button button, bool primary)
    {
        if (primary) button.SetResourceReference(StyleProperty, "PrimaryButton");
        button.Padding = new Thickness(14, 6, 14, 6);
        button.MinHeight = 34;
        button.Margin = new Thickness(0, 10, 8, 0);
    }

    // ---------- Add a computer ----------

    /// <summary>Add a computer's details are only the ways to add one: each is a whole clickable card that says what it does.</summary>
    private void AddChoices(NetworkNode node)
    {
        DetailContent.Children.Add(DetailSection("Choose how", null));
        foreach (var command in node.Commands) DetailContent.Children.Add(ChoiceCard(command));
    }

    private static string ChoiceGlyph(NodeAction action) => action switch
    {
        NodeAction.AddComputer => NetworkMap.AddGlyph,
        NodeAction.HostThisPc => NetworkMap.ThisPcGlyph,
        _ => NetworkMap.ComputerGlyph
    };

    private Button ChoiceCard(NodeCommand command)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = command.Label, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (command.Detail is { } detail)
        {
            var line = new TextBlock { Text = detail, Margin = new Thickness(0, 2, 0, 0) };
            line.SetResourceReference(StyleProperty, "Muted");
            text.Children.Add(line);
        }
        var content = new DockPanel();
        var chevron = Glyph("\uE76C", 14, new Thickness(16, 0, 0, 0));
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        DockPanel.SetDock(chevron, Dock.Right);
        content.Children.Add(chevron);
        content.Children.Add(text);

        var card = new Button { Content = content, Tag = ChoiceGlyph(command.Action), Margin = new Thickness(0, 0, 0, 10) };
        card.SetResourceReference(StyleProperty, "CardButton");
        if (command.Primary)
        {
            card.SetResourceReference(BorderBrushProperty, "AccentBrush");
            card.BorderThickness = new Thickness(1.5);
        }
        AutomationProperties.SetAutomationId(card, $"NodeAction-{command.Action}");
        AutomationProperties.SetName(card, command.Label);
        if (command.Detail is not null) AutomationProperties.SetHelpText(card, command.Detail);
        var action = command.Action;
        var argument = command.Argument;
        card.Click += (_, _) => RunNodeAction(action, argument);
        return card;
    }

    // ---------- details ----------

    private static TextBlock DetailSection(string heading, string? hint)
    {
        var text = new TextBlock { Margin = new Thickness(0, 22, 0, 12) };
        text.Inlines.Add(new System.Windows.Documents.Run(heading));
        text.SetResourceReference(StyleProperty, "Eyebrow");
        text.TextWrapping = TextWrapping.Wrap;
        if (hint is not null)
        {
            var muted = new System.Windows.Documents.Run("   " + hint) { FontWeight = FontWeights.Normal, FontSize = 13 };
            muted.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "MutedBrush");
            text.Inlines.Add(muted);
        }
        return text;
    }

    private static Expander DetailExpander(string header, string automationId, UIElement content, bool expanded)
    {
        var expander = new Expander { Header = header, Content = content, IsExpanded = expanded, Margin = new Thickness(-6, 16, 0, 0) };
        AutomationProperties.SetAutomationId(expander, automationId);
        AutomationProperties.SetName(expander, header);
        return expander;
    }

    private static Grid FactsGrid(IReadOnlyList<NodeFact> facts)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < facts.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = facts[i].Label, Margin = new Thickness(0, 0, 12, 6) };
            label.SetResourceReference(StyleProperty, "Muted");
            var value = new TextBlock { Text = facts[i].Value, Margin = new Thickness(0, 0, 0, 6) };
            if (facts[i].AutomationId is { } id) AutomationProperties.SetAutomationId(value, id);
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }
        return grid;
    }

    private static StackPanel Notes(IEnumerable<string> notes, Thickness margin)
    {
        var panel = new StackPanel { Margin = margin };
        foreach (var note in notes)
        {
            var text = new TextBlock { Text = "\u2022 " + note, Margin = new Thickness(0, 0, 0, 4) };
            text.SetResourceReference(StyleProperty, "Muted");
            panel.Children.Add(text);
        }
        return panel;
    }

    private static Border Callout(string text, string? automationId = null)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (automationId is not null) AutomationProperties.SetAutomationId(block, automationId);
        var callout = new Border { Child = block, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 16, 0, 0), BorderThickness = new Thickness(1.5) };
        callout.SetResourceReference(Border.BorderBrushProperty, "WarningBrush");
        callout.SetResourceReference(Border.BackgroundProperty, "CanvasBrush");
        return callout;
    }

    /// <summary>How this desktop reaches a host to install or remove its roles and update it: through Martlet on that computer
    /// (its paired connection; the default), SSH (Docker or native Linux) or this PC's Docker Desktop.</summary>
    private StackPanel ReachEditor(PairedHost host)
    {
        var panel = new StackPanel { Margin = new Thickness(4, 4, 0, 4), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        var now = new TextBlock { Text = $"Reached via: {host.Reach}.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetAutomationId(now, "HostReachNow");
        panel.Children.Add(now);
        var method = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(method, "Connection method");
        AutomationProperties.SetAutomationId(method, "HostReachMethod");
        foreach (var (value, text) in new[]
        {
            (HostSetupMethod.Agent, "Through Martlet on that computer (paired connection)"),
            (HostSetupMethod.SshDocker, "SSH to a Docker host"), (HostSetupMethod.SshNative, "SSH to an Ubuntu host"),
            (HostSetupMethod.ThisPcDocker, "This PC with Docker Desktop")
        })
        {
            var item = new ComboBoxItem { Content = text, Tag = value };
            method.Items.Add(item);
            if (value == host.Method || value == HostSetupMethod.Agent && host.Method == HostSetupMethod.OnHost) method.SelectedItem = item;
        }
        var hint = new TextBlock
        {
            Text = "Through Martlet on that computer: commands go over the pinned, signed connection you paired, and Martlet there " +
                "runs them (it needs to be running there; until then they wait). Use SSH for a Linux computer without Martlet.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        };
        hint.SetResourceReference(StyleProperty, "Muted");
        AutomationProperties.SetAutomationId(hint, "HostReachHint");
        var label = new TextBlock { Text = "SSH target, for example me@192.168.1.20", Margin = new Thickness(0, 0, 0, 4) };
        label.SetResourceReference(StyleProperty, "Muted");
        var ssh = new TextBox { Text = host.SshTarget ?? "", Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(ssh, "SSH target (user@computer)");
        AutomationProperties.SetAutomationId(ssh, "HostReachSsh");
        void Toggle()
        {
            var usesSsh = method.SelectedItem is ComboBoxItem { Tag: HostSetupMethod.SshDocker or HostSetupMethod.SshNative };
            ssh.IsEnabled = label.IsEnabled = usesSsh;
            if (usesSsh && ssh.Text.Length == 0) ssh.Text = host.Address;
        }
        Toggle();
        // No Save button: choosing a method saves at once; the SSH target saves when you leave the field or press Enter (or
        // after a pause in typing). The map isn't rebuilt under your typing.
        var reachSave = new AutoSave(async () =>
        {
            if (method.SelectedItem is not ComboBoxItem { Tag: HostSetupMethod chosen } || closing || store is null) return true;
            var target = ssh.Text.Trim();
            if (chosen == host.Method && (chosen is not (HostSetupMethod.SshDocker or HostSetupMethod.SshNative) || target == (host.SshTarget ?? "")))
                return true;
            ChangeTurns.Turn? turn = null;
            try
            {
                turn = await ChangeTurnAsync();
                var updated = await Pairings().SetReachAsync(host.HostId, chosen, target, Version, lifetime.Token);
                homeHosts = HostRegistry.Upsert(homeHosts, updated);
                host = updated;
                now.Text = $"Reached via: {updated.Reach}.";
                ActionText.Text = $"Saved how Martlet reaches {updated.HostId}.";
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or
                JsonException or ArgumentException)
            {
                ActionText.Text = $"Not saved yet: {error.Message}";
            }
            finally { turn?.Dispose(); }
            return true;
        }, TimeSpan.FromSeconds(1.5));
        method.SelectionChanged += (_, _) =>
        {
            Toggle();
            reachSave.SaveNowAsync().Forget();
        };
        ssh.TextChanged += (_, _) => reachSave.Changed();
        ssh.LostKeyboardFocus += (_, _) => { if (reachSave.Pending) reachSave.SaveNowAsync().Forget(); };
        ssh.KeyDown += (_, args) => { if (args.Key == System.Windows.Input.Key.Enter) reachSave.SaveNowAsync().Forget(); };
        panel.Children.Add(method);
        panel.Children.Add(hint);
        panel.Children.Add(label);
        panel.Children.Add(ssh);
        return panel;
    }

    // ---------- settings for all devices ----------

    private void RenderDeviceSettings(IReadOnlyList<NetworkNode> nodes)
    {
        var companion = Role == DeviceRole.Companion;
        RolesCard.Visibility = companion ? Visibility.Visible : Visibility.Collapsed;
        if (!companion)
        {
            RolesCoverage.Children.Clear();
            return;
        }
        EvaluateCoverage();
        string? NodeFor(string job)
        {
            var component = job == ClusterJobs.LipSync ? DeviceComponent.LipSync
                : HostJob.All.FirstOrDefault(j => j.Job == job) is { } hostJob ? DeviceComponent.Job(hostJob.Role) : null;
            return nodes.FirstOrDefault(n => n.Roles.Any(r => r.Component == component))?.Id;
        }
        ShowCoverage(RolesCoverage, NodeFor);
        RenderUnassignedJobs();
    }

    /// <summary>Jobs no device does yet (no route chosen), so they still have a place to be set up.</summary>
    private void RenderUnassignedJobs()
    {
        UnassignedJobs.Children.Clear();
        var routes = homeSettings?.Setup?.Routes ?? [];
        var missing = HostJob.All.Where(job => routes.All(r => r.Role != job.Role)).ToList();
        if (missing.Count == 0) return;
        var heading = new TextBlock { Text = "Not set up yet", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 4) };
        UnassignedJobs.Children.Add(heading);
        foreach (var job in missing)
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var setUp = new Button { Content = "Set up in Companion", VerticalAlignment = VerticalAlignment.Center };
            CompactButton(setUp, job.Role == SetupRole.Llm);
            setUp.Margin = new Thickness(12, 0, 0, 0);
            AutomationProperties.SetAutomationId(setUp, "RoleSetup-" + job.Role);
            var tab = TabFor(job.Role);
            setUp.Click += (_, _) => OpenCompanion(tab);
            DockPanel.SetDock(setUp, Dock.Right);
            row.Children.Add(setUp);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = NetworkMap.RoleName(job.Role) });
            var effect = new TextBlock { Text = JobCoverageRules.Effect(job.Job), FontSize = 13 };
            effect.SetResourceReference(StyleProperty, "Muted");
            text.Children.Add(effect);
            row.Children.Add(text);
            UnassignedJobs.Children.Add(row);
        }
    }
}
