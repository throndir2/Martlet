using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Martlet.Desktop;

/// <summary>Companion › Tools, the UI only: Now, Martlet's own terminal (off by default, <c>ToolsPage.Terminal.cs</c>), the MCP
/// servers Martlet may call while you talk, whether each is on and runs without asking, its tools (<c>ToolsPage.Servers.cs</c>),
/// how confirmations work and a log of recent tool use since Martlet started. What each line says is in
/// <see cref="ToolsPageModel"/>; what the page needs from the app is <see cref="IToolsPageServices"/>. MainWindow keeps one page for
/// the window's life: it keeps the last notice (a save that failed, a server removed).</summary>
internal sealed partial class ToolsPage(IToolsPageServices services)
{
    private string? toolsNotice;

    /// <summary>Draws the page's cards into <paramref name="page"/>.</summary>
    internal void Render(Panel page)
    {
        var service = services.Tools;
        service.EnsureLoaded();
        var model = new ToolsPageModel(service, services.ThinkingRoute);
        var statuses = model.Statuses;

        page.Children.Add(services.NowCard(service));
        page.Children.Add(TerminalCard(service, model));

        var servers = new List<UIElement> { Heading("MCP servers") };
        servers.Add(HelpTip.Explain("Connect tool servers so Martlet can use files, browsers, calendars and more while you talk. " +
            "Servers run on this PC or at an address you set, with your permissions, so install only servers you trust.",
            new Thickness(0, 0, 0, 8), "ToolsServers", "MCP servers", wiki: "Tools-and-MCP#mcp-servers"));
        if (toolsNotice is { } notice) servers.Add(ToolsAlert(notice));
        if (service.ConfigurationError is { } error)
            servers.Add(ToolsAlert($"Server settings have a problem, so no servers will run: {error}"));
        else if (service.Servers.Count == 0)
            servers.Add(Note("No servers yet: browse the MCP directory to install one, or add them in mcp.json.", new Thickness(0, 0, 0, 4)));
        // Every server Martlet runs: mcp.json's first, then ones other features manage (Smart home's, for example).
        foreach (var server in service.Servers)
            servers.Add(ServerBlock(service, server, statuses.GetValueOrDefault(server.Name)));
        foreach (var conflict in service.ManagedConflicts)
            servers.Add(ToolsAlert($"{conflict.ManagedBy} wants to add a server named \"{conflict.Name}\", but mcp.json already has one, so rename or " +
                "remove that entry in mcp.json to use it."));
        var anyOn = service.HasEnabledServers;
        servers.Add(Row(
            PageButton("Browse MCP directory", OpenMcpDirectory, primary: true, id: "ToolsBrowseDirectory"),
            PageButton("Edit servers", OpenMcpEditor, id: "ToolsEditConfig"),
            anyOn ? PageButton(service.Started ? "Restart stopped servers" : "Start servers now", () =>
            {
                toolsNotice = null;
                service.EnsureStarted(retryNow: true);
            }, id: "ToolsStart") : null,
            PageButton("Reload mcp.json", () =>
            {
                toolsNotice = null;
                service.Reload();
            }, id: "ToolsReload")));
        page.Children.Add(Card([.. servers]));

        page.Children.Add(Card(Heading("Confirmations"),
            HelpTip.Explain("Before a tool runs, Martlet shows what it wants to do, and you allow it once, always allow it or deny it. " +
                "Terminal commands offer only Allow once or Deny, unless you let them run without asking.",
                new Thickness(0, 0, 0, 6), "ToolsConfirmations", "confirmations", wiki: "Tools-and-MCP#confirmations"),
            Note("Skip confirmations only for servers you trust, because tool input and results go to your Thinking model.",
                new Thickness(0, 0, 0, 6)),
            Note("Tools are used only for replies to what you say or type, not for screen, camera or memory work.",
                new Thickness(0, 0, 0, 0))));

        var recent = service.Log;
        var log = new List<UIElement> { Heading("Recent tool use") };
        if (recent.Count == 0)
            log.Add(Note("Nothing yet: tool calls are listed here until you close Martlet, and they are never saved.", new Thickness(0, 0, 0, 4)));
        else
        {
            foreach (var entry in recent)
                log.Add(Note(ToolsPageModel.LogLine(entry), new Thickness(0, 0, 0, 4)));
            log.Add(Row(PageButton("Clear", service.ClearLog, link: true, id: "ToolsClearLog")));
        }
        page.Children.Add(Card([.. log]));
    }

    private void OpenMcpEditor()
    {
        if (services.Closing) return;
        new McpConfigWindow(services.Tools) { Owner = services.Owner }.ShowDialog();
        toolsNotice = null;
        if (services.IsOpen) services.Redraw();
    }

    private void OpenMcpDirectory()
    {
        if (services.Closing) return;
        new McpDirectoryWindow(services.Tools) { Owner = services.Owner }.ShowDialog();
        toolsNotice = null;
        if (services.IsOpen) services.Redraw();
    }

    private static TextBlock ToolsAlert(string text)
    {
        var status = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        return status;
    }
}
