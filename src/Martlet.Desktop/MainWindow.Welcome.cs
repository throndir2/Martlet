using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Martlet.Avatar.Hosting;
using Martlet.Core.Installation;
using Martlet.Core.Planning;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The welcome wizard (docs/WELCOME_WIZARD.md): start a new Martlet network or join one found on the local network,
/// read this PC's hardware, ask whether free online services are fine, suggest what runs where with each part's share of this
/// PC, get a free NVIDIA Build key when Thinking goes online, then set it all up after one confirmation and land on Home.</summary>
public partial class MainWindow
{
    internal const string NvidiaKeyPage = "https://build.nvidia.com/settings/api-keys";

    /// <summary>One share of this PC: "Graphics memory 31-35%" when the part usually holds less (<paramref name="usual"/>)
    /// than the most it takes; the bar shows the most.</summary>
    private static StackPanel Bar(string label, int percent, bool available, int? usual = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var share = $"{label} {WelcomePlan.Percents(usual ?? percent, percent)}";
        var text = new TextBlock { Text = available ? share : $"{label}: none", FontSize = 12 };
        text.SetResourceReference(StyleProperty, "Muted");
        panel.Children.Add(text);
        var bar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, Value = Math.Min(100, percent), Margin = new Thickness(0, 3, 0, 0) };
        AutomationProperties.SetName(bar, share);
        panel.Children.Add(bar);
        return panel;
    }
}
