using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Martlet.HostClient;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Installation;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

// The app shell's status line (see MainWindow.Shell.cs for the shell's shared state).
public partial class MainWindow
{
    private void ActionTextChanged(object? sender, EventArgs e)
    {
        var visible = !string.IsNullOrWhiteSpace(ActionText.Text);
        if (!visible) { StatusBar.Visibility = Visibility.Collapsed; return; }
        LogStatusLine(ActionText.Text);
        StatusBar.Visibility = Visibility.Visible;
        Motion.Enter(StatusBar, dy: 10, milliseconds: 200);
    }

    private void DismissStatus_Click(object sender, RoutedEventArgs e) => ActionText.Text = "";

    /// <summary>This change's turn to save (<see cref="ChangeTurns"/>): at once when no other change is saving, otherwise
    /// after the ones before it, saying so on the status line meanwhile. Dispose it as soon as the save is done; never hold
    /// it while something long (an install, a host run) works.</summary>
    private async Task<ChangeTurns.Turn> ChangeTurnAsync()
    {
        if (changes.TryTake() is { } now) return now;
        ActionText.Text = "Waiting for the change before this one to finish saving...";
        return await changes.TakeAsync(lifetime.Token);
    }
}
