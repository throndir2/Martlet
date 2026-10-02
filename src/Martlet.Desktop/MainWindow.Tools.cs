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
        servers.Add(Note("MCP servers give Martlet tools: your files, a browser, a calendar, notes, developer tools and many more. " +
            "Each runs as a program on this PC with your permissions (or is an address you set). Martlet starts them when you open " +
            "a talk window, and while you talk the Thinking model decides when a tool would help.", new Thickness(0, 0, 0, 8)));
        if (toolsNotice is { } notice) servers.Add(ToolsAlert(notice));
        if (service.ConfigurationError is { } error)
            servers.Add(ToolsAlert($"mcp.json has a problem, so no servers run until it's fixed: {error}"));
        else if (configuration.Servers.Count == 0)
            servers.Add(Note("No servers yet. Add them in mcp.json, the same format Claude Desktop, Cursor and VS Code use, so you can paste " +
                "a server's configuration from its instructions. Insert example adds a server for your Documents folder.", new Thickness(0, 0, 0, 4)));
        foreach (var server in configuration.Servers)
            servers.Add(ServerBlock(service, server, statuses.GetValueOrDefault(server.Name)));
        var anyOn = configuration.Servers.Any(s => !s.Disabled);
        servers.Add(Row(
            PageButton("Edit servers (mcp.json)", OpenMcpEditor, primary: configuration.Servers.Count == 0, id: "ToolsEditConfig"),
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
            Note("Before each tool call, the talk window shows the tool, its server and exactly what it will be given, with Allow once, " +
                "Always allow this tool and Deny. Without an answer in 60 seconds the call is declined. Always allow and Run its tools " +
                "without asking are saved in mcp.json (\"autoApprove\"), where you can review or undo them, or here.", new Thickness(0, 0, 0, 6)),
            Note("Only let tools run without asking when you're comfortable with anything they can do: the model, not you, chooses what to " +
                "pass, and text a tool reads (a web page, a file, an email) can try to steer it.", new Thickness(0, 0, 0, 6)),
            Note("Tools are offered only to replies to what you say or type, never to screen or camera looks or to memory. Tool descriptions " +
                "and what tools return go to your Thinking model with your message. A reply may use up to four tool rounds, each one more " +
                "LLM request. Tools aren't available while Thinking runs on a Martlet host; a model that doesn't support tools is asked " +
                "again without them.", new Thickness(0, 0, 0, 0))));

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
            : status is null ? "Not started yet; it starts when you open a talk window or press Start servers now."
            : status.State switch
            {
                McpServerState.Starting => "Starting... (the first start of an npx server downloads it, which can take a minute)",
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
        if (status is { State: McpServerState.Failed, Diagnostics: { } output })
            block.Children.Add(Note("What it printed: " + output, new Thickness(0, 2, 0, 0)));

        var on = new CheckBox { Content = "On", IsChecked = !server.Disabled, Margin = new Thickness(0, 8, 18, 0) };
        AutomationProperties.SetAutomationId(on, "ToolsServerOn-" + server.Name);
        on.Click += (_, _) => EditToolServer(service, server.Name, entry => McpConfiguration.SetDisabled(entry, on.IsChecked != true));
        var trust = new CheckBox
        {
            Content = "Run its tools without asking me first", IsChecked = server.AutoApproveAll, IsEnabled = !server.Disabled,
            Margin = new Thickness(0, 8, 0, 0)
        };
        AutomationProperties.SetAutomationId(trust, "ToolsServerTrust-" + server.Name);
        trust.Click += (_, _) => EditToolServer(service, server.Name, entry => McpConfiguration.SetAutoApproveAll(entry, trust.IsChecked == true));
        block.Children.Add(new WrapPanel { Children = { on, trust } });

        if (status is { State: McpServerState.Ready, Tools.Count: > 0 })
        {
            var tools = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            AutomationProperties.SetName(tools, $"Tools of {server.Name}");
            foreach (var tool in status.Tools)
            {
                var allowed = !server.AutoApproveAll && server.AutoApprove.Contains(tool.Name, StringComparer.Ordinal);
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
            toolsNotice = $"Couldn't change mcp.json: {error.Message}";
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
    });
}
