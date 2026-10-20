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
    internal static Expander DetailExpander(string header, string automationId, UIElement content, bool expanded)
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
}
