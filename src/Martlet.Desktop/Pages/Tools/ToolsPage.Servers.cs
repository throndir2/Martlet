using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Mcp.Client;

namespace Martlet.Desktop;

internal sealed partial class ToolsPage
{
    // ---------- MCP servers ----------

    private UIElement ServerBlock(McpToolService service, McpServerDefinition server, McpServerStatus? status)
    {
        var block = new StackPanel { Margin = new Thickness(0, 12, 0, 4) };
        AutomationProperties.SetName(block, $"MCP server {server.Name}");
        block.Children.Add(new TextBlock { Text = server.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var stateText = Note(ToolsPageModel.ServerState(server, status), new Thickness(0, 2, 0, 0));
        AutomationProperties.SetAutomationId(stateText, "ToolsServerState-" + server.Name);
        block.Children.Add(stateText);
        block.Children.Add(Note((server.Transport == McpTransportKind.Http ? "Address: " : "Runs: ") + server.Describe(), new Thickness(0, 2, 0, 0)));
        if (ToolsPageModel.FromDirectory(server) is { } registry)
            block.Children.Add(Note(registry, new Thickness(0, 2, 0, 0)));
        if (server.Problem is { } problem) block.Children.Add(Note(problem, new Thickness(0, 2, 0, 0)));
        if (server.ManagedBy is { } owner)
            block.Children.Add(Note($"Managed by {owner}, where you change its tool permissions.", new Thickness(0, 2, 0, 0)));
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
                label.SetResourceReference(FrameworkElement.StyleProperty, "Muted");
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
        var actions = new WrapPanel();
        if (!server.Disabled && status is not null)
        {
            var restart = PageButton("Restart", () =>
            {
                toolsNotice = null;
                service.Hub.Restart(server.Name);
            }, link: true, id: "ToolsRestart-" + server.Name);
            restart.Margin = new Thickness(0, 0, 16, 0);
            actions.Children.Add(restart);
        }
        if (editable)
            actions.Children.Add(PageButton("Remove", () => RemoveToolServer(service, server.Name), link: true, id: "ToolsRemove-" + server.Name));
        if (actions.Children.Count > 0) block.Children.Add(actions);
        return block;
    }

    private void RemoveToolServer(McpToolService service, string server)
    {
        if (!ConfirmationDialog.Confirm(services.Owner, $"Remove the MCP server \"{server}\" from mcp.json, with the secrets Martlet kept for it?",
                "Martlet - MCP servers"))
            return;
        try
        {
            service.Remove(server);
            toolsNotice = $"Removed \"{server}\".";
        }
        catch (Exception error) when (error is McpConfigurationException or System.IO.IOException or UnauthorizedAccessException)
        {
            toolsNotice = $"Couldn't change mcp.json: {error.Message}";
        }
        services.Redraw();
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
        services.Redraw();
    }
}
