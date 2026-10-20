using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>More of MainWindow.Devices.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
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
}
