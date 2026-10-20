using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Conversation;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Logging;
using Martlet.Providers;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

/// <summary>More of MainWindow.SenseModels.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    private async Task LookForSenseServersAsync()
    {
        await LookForLocalServersAsync(quiet: true);
        if (!closing && openTab is CompanionTab.Vision or CompanionTab.Hearing && !tabEdited) RenderTab();
    }
}
