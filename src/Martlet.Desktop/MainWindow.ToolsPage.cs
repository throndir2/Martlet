using System.Windows;
using System.Windows.Controls;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › Tools (<see cref="ToolsPage"/> in Pages\Tools). The MCP servers and the
/// terminal run in <see cref="McpToolService"/> (<c>mcpTools</c>); the window draws the page again when they change.</summary>
public partial class MainWindow : IToolsPageServices
{
    private ToolsPage? toolsPage;

    /// <summary>The Tools page, made once: it keeps the last notice while the window lives.</summary>
    private ToolsPage ToolsPage => toolsPage ??= new(this);

    McpToolService IToolsPageServices.Tools => mcpTools;
    SetupRoute? IToolsPageServices.ThinkingRoute => homeSettings.ThinkingRoute();
    Window IToolsPageServices.Owner => this;
    bool IToolsPageServices.Closing => closing;
    bool IToolsPageServices.IsOpen => openTab == CompanionTab.Tools;
    Border IToolsPageServices.NowCard(McpToolService service) => ToolsNowCard(service);
    void IToolsPageServices.Say(string text) => ActionText.Text = text;
    void IToolsPageServices.Redraw() => RenderTab();

    // Raised on any thread when MCP servers, approvals or the tool log change.
    private void ToolsChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (!closing && openTab == CompanionTab.Tools && NavCompanion.IsChecked == true) RenderTab();
        if (!closing) RenderHealth();
    });
}
