using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Mcp.Client;

namespace Martlet.Desktop;

/// <summary>The Companion page's Tools tab: the MCP servers Martlet may call while you talk, whether each is on and runs
/// without asking, its tools, how confirmations work and a log of recent tool use since Martlet started.</summary>
public partial class MainWindow
{
    private string? toolsNotice;

    private void RenderToolsTab(Panel page)
    {
        var service = mcpTools;
        var configuration = service.Configuration;
        var statuses = service.Started ? service.Hub.Status.ToDictionary(s => s.Name, StringComparer.Ordinal) : new Dictionary<string, McpServerStatus>(StringComparer.Ordinal);

        var servers = new List<UIElement> { Heading("MCP servers") };
        servers.Add(Note("Connect tool servers so Martlet can use files, browsers, calendars and more while you talk. Servers run on this PC or at an address you set.",
            new Thickness(0, 0, 0, 8)));
        if (toolsNotice is { } notice) servers.Add(ToolsAlert(notice));
        if (service.ConfigurationError is { } error)
            servers.Add(ToolsAlert($"Server settings have a problem, so no servers will run: {error}"));
        else if (service.Servers.Count == 0)
            servers.Add(Note("No servers yet. Add them in mcp.json, or insert the Documents example.", new Thickness(0, 0, 0, 4)));
        // Every server Martlet runs: mcp.json's first, then ones other features manage (Smart home's, for example).
        foreach (var server in service.Servers)
            servers.Add(ServerBlock(service, server, statuses.GetValueOrDefault(server.Name)));
        foreach (var conflict in service.ManagedConflicts)
            servers.Add(ToolsAlert($"{conflict.ManagedBy} wants to add a server named \"{conflict.Name}\", but mcp.json already has one. " +
                "Rename or remove that entry in mcp.json to use it."));
        var anyOn = service.HasEnabledServers;
        servers.Add(Row(
            PageButton("Edit servers", OpenMcpEditor, primary: configuration.Servers.Count == 0, id: "ToolsEditConfig"),
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
            Note("Before a tool runs, Martlet shows what it wants to do. You can allow once, always allow or deny.",
                new Thickness(0, 0, 0, 6)),
            Note("Only skip confirmations for servers you trust. Tool input and results are sent to your Thinking model.",
                new Thickness(0, 0, 0, 6)),
            Note("Tools are used only for replies to what you say or type, not for screen, camera or memory work.",
                new Thickness(0, 0, 0, 0))));

        var recent = service.Log;
        var log = new List<UIElement> { Heading("Recent tool use") };
        if (recent.Count == 0)
            log.Add(Note("Nothing yet. Tool calls are listed here until you close Martlet; they are never saved.", new Thickness(0, 0, 0, 4)));
        else
        {
            foreach (var entry in recent)
                log.Add(Note($"{entry.At.LocalDateTime:t}  {entry.Server} › {entry.Tool}: {entry.Outcome}" +
                    (entry.Arguments.Length > 0 ? $"  {entry.Arguments}" : ""), new Thickness(0, 0, 0, 4)));
            log.Add(Row(PageButton("Clear", service.ClearLog, link: true, id: "ToolsClearLog")));
        }
        page.Children.Add(Card([.. log]));
    }

    private UIElement ServerBlock(McpToolService service, McpServerDefinition server, McpServerStatus? status)
    {
        var block = new StackPanel { Margin = new Thickness(0, 12, 0, 4) };
        AutomationProperties.SetName(block, $"MCP server {server.Name}");
        block.Children.Add(new TextBlock { Text = server.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var state = server.Disabled ? "Off."
            : status is null ? "Not started yet. It starts when you open a talk window or press Start servers now."
            : status.State switch
            {
                McpServerState.Starting => "Starting... first start may take a minute.",
                McpServerState.Ready => $"Running{(status.ServerName is { } name && name != server.Name ? $" ({name})" : "")}, " +
                    (status.Tools.Count == 1 ? "1 tool." : $"{status.Tools.Count} tools."),
                McpServerState.Failed => "Stopped: " + status.Error,
                _ => "Off."
            };
        var stateText = Note(state, new Thickness(0, 2, 0, 0));
        AutomationProperties.SetAutomationId(stateText, "ToolsServerState-" + server.Name);
        block.Children.Add(stateText);
        block.Children.Add(Note((server.Transport == McpTransportKind.Http ? "Address: " : "Runs: ") + server.Describe(), new Thickness(0, 2, 0, 0)));
        if (server.Problem is { } problem) block.Children.Add(Note(problem, new Thickness(0, 2, 0, 0)));
        if (server.ManagedBy is { } owner)
            block.Children.Add(Note($"Managed by {owner}. Change its tool permissions there.", new Thickness(0, 2, 0, 0)));
        if (status is { State: McpServerState.Failed, Diagnostics: { } output })
            block.Children.Add(Note("What it printed: " + output, new Thickness(0, 2, 0, 0)));

        var editable = server.ManagedBy is null;
        var on = new CheckBox { Content = "On", IsChecked = !server.Disabled, Margin = new Thickness(0, 8, 18, 0), IsEnabled = editable };
        AutomationProperties.SetAutomationId(on, "ToolsServerOn-" + server.Name);
        on.Click += (_, _) => EditToolServer(service, server.Name, entry => McpConfiguration.SetDisabled(entry, on.IsChecked != true));
        var trust = new CheckBox
        {
            Content = "Run without asking", IsChecked = server.AutoApproveAll, IsEnabled = editable && !server.Disabled,
            Margin = new Thickness(0, 8, 0, 0)
        };
        AutomationProperties.SetAutomationId(trust, "ToolsServerTrust-" + server.Name);
        trust.Click += (_, _) => EditToolServer(service, server.Name, entry => McpConfiguration.SetAutoApproveAll(entry, trust.IsChecked == true));
        if (editable) block.Children.Add(new WrapPanel { Children = { on, trust } });

        if (status is { State: McpServerState.Ready, Tools.Count: > 0 })
        {
            var tools = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            AutomationProperties.SetName(tools, $"Tools of {server.Name}");
            foreach (var tool in status.Tools)
            {
                var allowed = editable && !server.AutoApproveAll && server.AutoApprove.Contains(tool.Name, StringComparer.Ordinal);
                var label = new TextBlock
                {
                    Text = tool.Name + (allowed ? " (always allowed)" : ""), Margin = new Thickness(0, 2, allowed ? 4 : 14, 2),
                    ToolTip = tool.Description, VerticalAlignment = VerticalAlignment.Center
                };
                label.SetResourceReference(StyleProperty, "Muted");
                tools.Children.Add(label);
                if (allowed)
                {
                    var forget = PageButton("Ask again", () => EditToolServer(service, server.Name,
                        entry => McpConfiguration.RemoveAutoApproveEntry(entry, tool.Name)), link: true, id: $"ToolsAskAgain-{server.Name}-{tool.Name}");
                    forget.Margin = new Thickness(0, 0, 14, 0);
                    tools.Children.Add(forget);
                }
            }
            block.Children.Add(tools);
        }
        if (!server.Disabled && status is not null)
        {
            var restart = PageButton("Restart", () =>
            {
                toolsNotice = null;
                service.Hub.Restart(server.Name);
            }, link: true, id: "ToolsRestart-" + server.Name);
            restart.HorizontalAlignment = HorizontalAlignment.Left;
            block.Children.Add(restart);
        }
        return block;
    }

    private void EditToolServer(McpToolService service, string server, Action<System.Text.Json.Nodes.JsonObject> edit)
    {
        try
        {
            service.EditServer(server, edit);
            toolsNotice = null;
        }
        catch (Exception error) when (error is McpConfigurationException or System.IO.IOException or UnauthorizedAccessException)
        {
            toolsNotice = $"Couldn't save server settings: {error.Message}";
        }
        RenderTab();
    }

    private void OpenMcpEditor()
    {
        if (closing) return;
        new McpConfigWindow(mcpTools) { Owner = this }.ShowDialog();
        toolsNotice = null;
        if (openTab == CompanionTab.Tools) RenderTab();
    }

    private static TextBlock ToolsAlert(string text)
    {
        var status = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        return status;
    }

    // Raised on any thread when MCP servers, approvals or the tool log change.
    private void ToolsChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (!closing && openTab == CompanionTab.Tools && NavCompanion.IsChecked == true) RenderTab();
        if (!closing) RenderHealth();
    });
}
