using System.Windows;
using System.Windows.Controls;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Tools (<see cref="ToolsPage"/> and <see cref="ToolsPageModel"/>) reads and starts in the app.
/// MainWindow gives it to the page when it makes the page (MainWindow.ToolsPage.cs). MainWindow keeps the work the page shows
/// (rule 4 of Pages\README.md): the MCP servers and the terminal run in <see cref="McpToolService"/>, and the window draws the page
/// again when they change (MainWindow.ToolsPage.cs).</summary>
internal interface IToolsPageServices
{
    /// <summary>The MCP servers, the terminal settings and the tool log.</summary>
    McpToolService Tools { get; }

    /// <summary>The Thinking route (it must be able to use tools), or null when Thinking isn't set up.</summary>
    SetupRoute? ThinkingRoute { get; }

    /// <summary>The window that owns the page's dialogs.</summary>
    Window Owner { get; }

    /// <summary>Whether the window is closing.</summary>
    bool Closing { get; }

    /// <summary>Whether Companion › Tools is the page open now.</summary>
    bool IsOpen { get; }

    /// <summary>The page's Now card (shared with the other pages' Now cards in MainWindow.PageNow.cs).</summary>
    Border NowCard(McpToolService service);

    /// <summary>Shows <paramref name="text"/> in the window's action line.</summary>
    void Say(string text);

    /// <summary>Draws the open Companion page again.</summary>
    void Redraw();
}
