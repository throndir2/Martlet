using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;
using Martlet.Mcp.Client;

namespace Martlet.Desktop;

/// <summary>The Companion page's Tools tab: Martlet's own terminal (off by default), the MCP servers Martlet may call while you
/// talk, whether each is on and runs without asking, its tools, how confirmations work and a log of recent tool use since
/// Martlet started.</summary>
public partial class MainWindow
{
    private string? toolsNotice;

    private void RenderToolsTab(Panel page)
    {
        var service = mcpTools;
        service.EnsureLoaded();
        var statuses = service.Started ? service.Hub.Status.ToDictionary(s => s.Name, StringComparer.Ordinal) : new Dictionary<string, McpServerStatus>(StringComparer.Ordinal);

        page.Children.Add(TerminalCard(service));

        var servers = new List<UIElement> { Heading("MCP servers") };
        servers.Add(Note("Connect tool servers so Martlet can use files, browsers, calendars and more while you talk. Servers run on this PC or at an address you set.",
            new Thickness(0, 0, 0, 8)));
        if (toolsNotice is { } notice) servers.Add(ToolsAlert(notice));
        if (service.ConfigurationError is { } error)
            servers.Add(ToolsAlert($"Server settings have a problem, so no servers will run: {error}"));
        else if (service.Servers.Count == 0)
            servers.Add(Note("No servers yet. Browse the MCP directory to install one, or add them in mcp.json.", new Thickness(0, 0, 0, 4)));
        // Every server Martlet runs: mcp.json's first, then ones other features manage (Smart home's, for example).
        foreach (var server in service.Servers)
            servers.Add(ServerBlock(service, server, statuses.GetValueOrDefault(server.Name)));
        foreach (var conflict in service.ManagedConflicts)
            servers.Add(ToolsAlert($"{conflict.ManagedBy} wants to add a server named \"{conflict.Name}\", but mcp.json already has one. " +
                "Rename or remove that entry in mcp.json to use it."));
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
            Note("Before a tool runs, Martlet shows what it wants to do. You can allow once, always allow or deny. Terminal commands " +
                "offer only allow once or deny, unless you let them run without asking.",
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

    // ---------- Terminal ----------

    /// <summary>Companion › Tools › Terminal: whether replies may run commands on this PC (off by default), in which shell and
    /// folder, how long one may run and whether each asks first (on by default; turning that off asks once to be sure). It saves
    /// on each change, for this PC only.</summary>
    private Border TerminalCard(McpToolService service)
    {
        var saved = service.Terminal;
        var children = new List<UIElement>
        {
            Heading("Terminal"),
            Note("Let Martlet run commands on this PC when you ask: check on something, start a program or run your scripts, " +
                "and hear back what happened. It's off until you turn it on.", new Thickness(0, 0, 0, 8))
        };
        var on = new CheckBox { Content = "Let Martlet run terminal commands", IsChecked = saved.Enabled, Margin = new Thickness(0, 0, 0, 4) };
        AutomationProperties.SetAutomationId(on, "ToolsTerminalOn");
        on.Checked += (_, _) => { if (!service.Terminal.Enabled) SaveTerminal(service, service.Terminal with { Enabled = true }); };
        on.Unchecked += (_, _) => { if (service.Terminal.Enabled) SaveTerminal(service, service.Terminal with { Enabled = false }); };
        children.Add(on);
        var (state, problem) = TerminalStatus(service, saved);
        var status = problem ? Warning(state) : Note(state, new Thickness(0, 0, 0, 6));
        AutomationProperties.SetAutomationId(status, "ToolsTerminalStatus");
        children.Add(status);

        var shells = Enum.GetValues<TerminalShell>();
        var shell = new ComboBox
        {
            Width = 260, ItemsSource = shells.Select(s => TerminalRunner.Name(s) + (TerminalRunner.Find(s) is null ? " (not installed)" : "")).ToArray(),
            SelectedIndex = Array.IndexOf(shells, saved.Shell)
        };
        AutomationProperties.SetName(shell, "Shell");
        AutomationProperties.SetAutomationId(shell, "ToolsTerminalShell");
        shell.SelectionChanged += (_, _) =>
        {
            if (shell.SelectedIndex >= 0 && shells[shell.SelectedIndex] != service.Terminal.Shell)
                SaveTerminal(service, service.Terminal with { Shell = shells[shell.SelectedIndex] });
        };
        children.Add(TerminalRow("Shell", shell));

        var folder = new StackPanel();
        folder.Children.Add(new TextBlock
        {
            Text = saved.CustomFolder ? saved.Folder : $"Your home folder ({TerminalSettings.HomeFolder})",
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
        });
        var folderButtons = Row(PageButton("Choose folder...", () => ChooseTerminalFolder(service), link: true, id: "ToolsTerminalFolder"),
            saved.CustomFolder ? PageButton("Use my home folder", () => SaveTerminal(service, service.Terminal with { Folder = "" }),
                link: true, id: "ToolsTerminalHome") : null);
        folderButtons.Margin = new Thickness(0, 2, 0, 0);
        folder.Children.Add(folderButtons);
        children.Add(TerminalRow("Starts in", folder));

        var limits = TerminalSettings.TimeLimits;
        var limit = new ComboBox
        {
            Width = 260, ItemsSource = limits.Select(seconds => seconds < 60 ? $"{seconds} seconds" : "1 minute").ToArray(),
            SelectedIndex = limits.ToList().IndexOf(saved.TimeLimitSeconds)
        };
        AutomationProperties.SetName(limit, "Time limit for one command");
        AutomationProperties.SetAutomationId(limit, "ToolsTerminalTimeLimit");
        limit.SelectionChanged += (_, _) =>
        {
            if (limit.SelectedIndex >= 0 && limits[limit.SelectedIndex] != service.Terminal.TimeLimitSeconds)
                SaveTerminal(service, service.Terminal with { TimeLimitSeconds = limits[limit.SelectedIndex] });
        };
        children.Add(TerminalRow("Time limit", limit));

        var ask = new CheckBox { Content = "Ask before every command", IsChecked = saved.AskFirst, Margin = new Thickness(0, 12, 0, 4) };
        AutomationProperties.SetAutomationId(ask, "ToolsTerminalAskFirst");
        ask.Checked += (_, _) => { if (!service.Terminal.AskFirst) SaveTerminal(service, service.Terminal with { AskFirst = true }); };
        // Asked once the click is handled, so the check box (and a UI Automation toggle) isn't held up by the question.
        ask.Unchecked += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (closing || !service.Terminal.AskFirst) return;
            if (ConfirmationDialog.Confirm(this, "Run terminal commands without asking?\n\nYour Thinking model chooses each command, " +
                    "and anything it reads (a web page, a file, a tool's output) can try to steer it. A command can do anything you " +
                    "can: delete or change files, change settings, install programs or send your data anywhere. Martlet still never " +
                    "runs one as administrator, and every command is listed under Recent tool use.",
                    "Martlet - Terminal", yes: "Run without asking", no: "Keep asking", questionId: "ToolsTerminalNoAskQuestion"))
                SaveTerminal(service, service.Terminal with { AskFirst = false });
            else RenderTab();
        });
        children.Add(ask);
        children.Add(Note("The talk window shows each command with Allow once and Deny; no answer within 60 seconds means Deny.",
            new Thickness(0, 0, 0, 8)));
        children.Add(Note("Commands run hidden, one at a time, as you and never as administrator. Martlet closes their input, so " +
            "a command that waits for typing ends at once, and one that runs past the time limit is stopped. Each command and what " +
            "it prints go to your Thinking model. This setting stays on this PC; your other computers keep their own.",
            new Thickness(0, 0, 0, 0)));
        return Card([.. children]);
    }

    private static DockPanel TerminalRow(string label, UIElement control)
    {
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
        var name = new TextBlock { Text = label, Width = 90, VerticalAlignment = control is ComboBox ? VerticalAlignment.Center : VerticalAlignment.Top };
        DockPanel.SetDock(name, Dock.Left);
        row.Children.Add(name);
        if (control is ComboBox box) box.HorizontalAlignment = HorizontalAlignment.Left;
        row.Children.Add(control);
        return row;
    }

    /// <summary>The terminal's state in words, and whether it is a problem that keeps it from working.</summary>
    private (string Text, bool Problem) TerminalStatus(McpToolService service, TerminalSettings saved)
    {
        if (!saved.Enabled) return ("Off. Martlet can't run commands on this PC.", false);
        var shell = TerminalRunner.Name(saved.Shell);
        if (TerminalRunner.Find(saved.Shell) is null) return ($"On, but {shell} isn't installed on this PC. Choose another shell.", true);
        if (!System.IO.Directory.Exists(saved.StartFolder)) return ("On, but the start folder doesn't exist any more. Choose another.", true);
        var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        if (route is null) return ("On. Set up Thinking so Martlet can use it.", true);
        if (route.RouteType is not (SetupRouteType.OpenAi or SetupRouteType.ChatCompletions))
            return ("On, but your Thinking model can't use tools here. Use OpenAI or a Chat Completions endpoint (such as Ollama on " +
                "this PC) in Companion › Thinking.", true);
        if (service.IsUnsupported(McpToolService.ModelKey($"{route.RouteType}", route.Origin, route.ModelId)))
            return ($"On, but {route.ModelId} turned down tools, so Martlet stops offering them to it for a week. Choose a model " +
                "that can use tools.", true);
        return ($"On. {shell}, {(saved.AskFirst ? "asks before every command" : "runs commands without asking")}, stops a command " +
            $"after {(saved.TimeLimitSeconds < 60 ? $"{saved.TimeLimitSeconds} seconds" : "1 minute")}.", false);
    }

    private void ChooseTerminalFolder(McpToolService service)
    {
        if (closing) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where terminal commands start", Multiselect = false, InitialDirectory = service.Terminal.StartFolder
        };
        if (dialog.ShowDialog(this) == true) SaveTerminal(service, service.Terminal with { Folder = dialog.FolderName });
    }

    private void SaveTerminal(McpToolService service, TerminalSettings next)
    {
        var before = service.Terminal;
        if (!service.SetTerminal(next))
        {
            toolsNotice = "Couldn't save the terminal settings. Check access to Martlet's data folder.";
            RenderTab();
            return;
        }
        toolsNotice = null;
        ActionText.Text = next.Enabled != before.Enabled
            ? next.Enabled
                ? next.AskFirst ? "The terminal is on. Martlet asks before each command." : "The terminal is on. Commands run without asking."
                : "The terminal is off."
            : next.AskFirst != before.AskFirst
                ? next.AskFirst ? "Martlet asks before every terminal command." : "Terminal commands run without asking."
                : "Terminal settings saved.";
        RenderTab();
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
        if (server.Registry is { } registry)
            block.Children.Add(Note($"From the MCP directory: {registry}{(server.RegistryVersion is { } version ? $" {version}" : "")}" +
                (server.Secrets.Count > 0 ? $"; {server.Secrets.Count} secret value{(server.Secrets.Count == 1 ? " is" : "s are")} kept in " +
                    "Windows Credential Manager." : "."), new Thickness(0, 2, 0, 0)));
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
        if (!ConfirmationDialog.Confirm(this, $"Remove the MCP server \"{server}\" from mcp.json? Secrets Martlet kept for it are " +
                "forgotten too.", "Martlet - MCP servers"))
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
        RenderTab();
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

    private void OpenMcpDirectory()
    {
        if (closing) return;
        new McpDirectoryWindow(mcpTools) { Owner = this }.ShowDialog();
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
