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
                : node.Kind == NodeKind.Missing ? "Not set up yet." : "Change these jobs here.",
                HelpTip.For("what it does", "Each row is a job or role of this computer, with the buttons that change it.", "DeviceJobs",
                    wiki: "Multiple-Computers#machine-cards")));
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

    private static TextBlock DetailSection(string heading, string? hint, HelpTip? help = null)
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
        if (help is not null)
        {
            help.Margin = new Thickness(6, 0, 0, 0);
            text.Inlines.Add(new System.Windows.Documents.InlineUIContainer(help) { BaselineAlignment = BaselineAlignment.Center });
        }
        return text;
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
}
