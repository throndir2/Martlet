using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Installation;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Home's health, drawn: the hero, the attention list and the Health tiles.</summary>
public partial class MainWindow
{
    /// <summary>Rebuilds Home's attention list, Health tiles and hero from <see cref="BuildHealth"/>; skips the rebuild when
    /// nothing changed unless <paramref name="force"/>.</summary>
    private void RenderHealth(bool force = false)
    {
        if (closing || Role != DeviceRole.Companion) return;
        FollowLocalServices();
        var (issues, tiles) = BuildHealth();
        RenderGetStarted(issues);
        var signature = string.Join("\n", issues.Select(i => $"{i.Id}|{i.Level}|{i.Title}|{i.Detail}|{string.Join(",", i.Fixes.Select(f => f.Label))}")) +
            "\n--\n" + string.Join("\n", tiles.Select(t => $"{t.Id}|{t.Health}|{t.Status}"));
        if (!force && signature == healthSignature) return;
        var first = healthSignature is null;
        healthSignature = signature;
        RenderStage(issues);
        RenderIssues(issues, first);
        RenderTiles(tiles, first);
    }

    private void RenderStage(IReadOnlyList<HealthIssue> issues)
    {
        var top = issues.FirstOrDefault(i => i.Level == HealthLevel.Problem);
        var warnings = issues.Count(i => i.Level == HealthLevel.Warning);
        var llm = homeSettings.ThinkingRoute();
        AdvisorButton.Visibility = issues.Any(i => i.Id == "thinking-setup") ? Visibility.Visible : Visibility.Collapsed;
        ConnectComputersButton.Visibility = issues.Any(i => i.Id == "thinking-setup" && i.Fixes.Any(f => f.Id == "network"))
            ? Visibility.Visible : Visibility.Collapsed;
        if (top is not null)
        {
            StageTitle.Text = top.Headline ?? top.Title;
            StageText.Text = top.Headline is null || top.Headline == top.Title || top.Id == "thinking-setup" ? top.Detail : $"{top.Title}. {top.Detail}";
            // The hero offers the first fix that can be pressed now (not one greyed out while a run already does it).
            var fix = top.Fixes.FirstOrDefault(f => f.Enabled);
            stageFix = fix?.Run;
            PrimaryStageButton.Content = fix?.Label ?? "";
            PrimaryStageButton.Visibility = fix is null ? Visibility.Collapsed : Visibility.Visible;
            stageReady = false;
            RenderListening();
            return;
        }
        stageFix = null;
        PrimaryStageButton.Visibility = Visibility.Collapsed;
        stageReady = true;
        RenderListening();
        StageTitle.Text = warnings == 0 ? "Ready when you are" : $"Ready, with {warnings} thing{(warnings == 1 ? "" : "s")} to look at";
        StageText.Text = warnings == 0
            ? "Martlet is ready: start listening to talk by voice, or start talking to chat by voice or text."
            : "Martlet can reply; review the items below when you have time.";
    }

    private void RenderIssues(IReadOnlyList<HealthIssue> issues, bool first)
    {
        HealthIssues.Children.Clear();
        var problems = issues.Count(i => i.Level == HealthLevel.Problem);
        var warnings = issues.Count(i => i.Level == HealthLevel.Warning);
        var notices = issues.Count(i => i.Level == HealthLevel.Notice);
        HealthTitle.Text = problems + warnings == 0 ? "All good" : "Needs attention";
        var parts = new List<string>();
        if (problems > 0) parts.Add($"{problems} problem{(problems == 1 ? "" : "s")} stop{(problems == 1 ? "s" : "")} Martlet replying");
        if (warnings > 0) parts.Add($"{warnings} warning{(warnings == 1 ? "" : "s")}");
        if (notices > 0) parts.Add($"{notices} good to know");
        HealthSummary.Text = problems + warnings == 0
            ? "Nothing needs your attention." + (notices > 0 ? $" {notices} good to know below." : "")
            : string.Join(" · ", parts) + ".";

        var index = 0;
        if (problems + warnings == 0)
        {
            var clear = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 0, 8) };
            var check = Glyph("\uE930", 18, new Thickness(0, 0, 10, 0));
            check.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
            clear.Children.Add(check);
            var text = new TextBlock { Text = "Everything Martlet can check looks good.", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(text, "HealthAllClear");
            clear.Children.Add(text);
            HealthIssues.Children.Add(clear);
            if (first) Motion.Enter(clear);
        }
        var noticeHeading = false;
        foreach (var issue in issues)
        {
            if (issue.Level == HealthLevel.Notice && !noticeHeading)
            {
                noticeHeading = true;
                var heading = new TextBlock { Text = "Good to know", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, problems + warnings == 0 ? 4 : 12, 0, 6) };
                heading.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                HealthIssues.Children.Add(heading);
            }
            var row = IssueRow(issue);
            HealthIssues.Children.Add(row);
            if (first) Motion.Enter(row, delay: index++ * 50);
        }
    }

    private Border IssueRow(HealthIssue issue)
    {
        var (glyph, brush, word) = issue.Level switch
        {
            HealthLevel.Problem => ("\uEA39", "WarningBrush", "Problem"),
            HealthLevel.Warning => ("\uE7BA", "WarningBrush", "Warning"),
            _ => ("\uE946", "AccentBrush", "Good to know")
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = Glyph(glyph, 18, new Thickness(0, 1, 12, 0));
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.SetResourceReference(TextBlock.ForegroundProperty, brush);
        grid.Children.Add(icon);

        var body = new StackPanel();
        var title = new TextBlock { Text = issue.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(title, "HealthIssue-" + issue.Id);
        AutomationProperties.SetName(title, $"{word}: {issue.Title} — {issue.Detail}");
        body.Children.Add(title);
        var detail = new TextBlock { Text = issue.Detail, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        detail.SetResourceReference(StyleProperty, "Muted");
        body.Children.Add(detail);
        if (issue.Fixes.Count > 0)
        {
            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var primary = issue.Level == HealthLevel.Problem;
            foreach (var fix in issue.Fixes)
            {
                var button = new Button { Content = fix.Label, Margin = new Thickness(0, 0, 8, 4), Padding = new Thickness(12, 6, 12, 6), IsEnabled = fix.Enabled };
                if (fix.Id == "dismiss") button.SetResourceReference(StyleProperty, "LinkButton");
                else if (primary) button.SetResourceReference(StyleProperty, "PrimaryButton");
                primary = false;
                AutomationProperties.SetAutomationId(button, $"{(fix.Passive ? "HealthOpen" : "HealthFix")}-{issue.Id}-{fix.Id}");
                AutomationProperties.SetName(button, $"{fix.Label}: {issue.Title}");
                var run = fix.Run;
                button.Click += (_, _) => run();
                actions.Children.Add(button);
            }
            body.Children.Add(actions);
        }
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        var row = new Border
        {
            Child = grid, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 0, 8),
            BorderThickness = new Thickness(issue.Level == HealthLevel.Problem ? 1.5 : issue.Level == HealthLevel.Warning ? 1 : 0)
        };
        row.SetResourceReference(Border.BorderBrushProperty, "WarningBrush");
        row.SetResourceReference(Border.BackgroundProperty, issue.Level == HealthLevel.Notice ? "CanvasBrush" : "SoftBrush");
        AutomationProperties.SetName(row, $"{word}: {issue.Title}");
        return row;
    }

    private void RenderTiles(IReadOnlyList<HealthTile> tiles, bool first)
    {
        HealthChecks.Children.Clear();
        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            status.Children.Add(Dot(tile.Health, 8, new Thickness(0, 0, 6, 0)));
            var text = new TextBlock { Text = tile.Status, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, MaxWidth = 170 };
            text.SetResourceReference(StyleProperty, "Muted");
            status.Children.Add(text);
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = tile.Name, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            content.Children.Add(status);
            var button = new Button { Content = content, Tag = tile.Glyph, Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 10, 10) };
            button.SetResourceReference(StyleProperty, "CardButton");
            var state = tile.Health switch
            {
                NodeHealth.Ready => "OK",
                NodeHealth.Attention => "needs attention",
                _ => "not checked or not set up"
            };
            AutomationProperties.SetAutomationId(button, "HealthCheck-" + tile.Id);
            AutomationProperties.SetName(button, $"{tile.Name}: {state}. {tile.Status}");
            var open = tile.Open;
            button.Click += (_, _) => open();
            HealthChecks.Children.Add(button);
            if (first) Motion.Enter(button, delay: i * 40);
        }
    }
}
