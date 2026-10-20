using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.HostClient;
using Martlet.Avatar.Hosting;
using Martlet.Core.Contracts;
using Martlet.Core.Network;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;

namespace Martlet.Desktop;

/// <summary>More of MainWindow.Network.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    private static FrameworkElement NetworkRowText(string automationId, string title, string detail)
    {
        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var heading = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        // The heading carries the whole row for UI Automation (and MCP snapshots): who it is and what it means.
        AutomationProperties.SetAutomationId(heading, automationId);
        AutomationProperties.SetName(heading, $"{title}: {detail}");
        text.Children.Add(heading);
        var line = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
        line.SetResourceReference(StyleProperty, "Muted");
        text.Children.Add(line);
        return text;
    }

    private static Border NetworkRowCard(FrameworkElement row, bool warning)
    {
        var border = new Border { Child = row, CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 10, 0, 0) };
        border.SetResourceReference(Border.BackgroundProperty, "CanvasBrush");
        if (warning)
        {
            border.BorderThickness = new Thickness(1);
            border.SetResourceReference(Border.BorderBrushProperty, "WarningBrush");
        }
        return border;
    }
}
