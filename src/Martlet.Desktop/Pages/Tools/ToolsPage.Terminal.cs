using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Mcp.Client;

namespace Martlet.Desktop;

internal sealed partial class ToolsPage
{
    // ---------- Terminal ----------

    /// <summary>Companion › Tools › Terminal: whether replies may run commands on this PC (off by default), in which shell and
    /// folder, how long one may run and whether each asks first (on by default; turning that off asks once to be sure). It saves
    /// on each change, for this PC only.</summary>
    private Border TerminalCard(McpToolService service, ToolsPageModel model)
    {
        var saved = model.Terminal;
        var children = new List<UIElement>
        {
            Heading("Terminal"),
            HelpTip.Explain("Let Martlet run commands on this PC when you ask: check on something, start a program or run your " +
                "scripts, and hear back what happened.", new Thickness(0, 0, 0, 8), "Terminal", "the terminal", wiki: "Tools-and-MCP#terminal")
        };
        children.AddRange(MainWindow.OnOffChoices("ToolsTerminalOn", "ToolsTerminalOff", "Let Martlet run terminal commands",
            "Martlet runs the commands you ask for on this PC, as you and never as administrator.",
            "Martlet can't run commands on this PC.", saved.Enabled, null,
            on => { if (service.Terminal.Enabled != on) SaveTerminal(service, service.Terminal with { Enabled = on }); }));
        var (state, problem) = model.TerminalState;
        var status = problem ? Warning(state) : Note(state, new Thickness(0, 0, 0, 6));
        AutomationProperties.SetAutomationId(status, "ToolsTerminalStatus");
        children.Add(status);

        var shells = Enum.GetValues<TerminalShell>();
        var shell = new ComboBox
        {
            Width = 260, ItemsSource = shells.Select(ToolsPageModel.ShellName).ToArray(),
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
            Text = ToolsPageModel.Folder(saved),
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
            Width = 260, ItemsSource = limits.Select(ToolsPageModel.TimeLimit).ToArray(),
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
        ask.Unchecked += (_, _) => ask.Dispatcher.BeginInvoke(() =>
        {
            if (services.Closing || !service.Terminal.AskFirst) return;
            if (ConfirmationDialog.Confirm(services.Owner, "Run terminal commands without asking, although a command can do anything you can " +
                    "(delete files, install programs or send your data anywhere)?\n\nYour Thinking model chooses each command, and " +
                    "anything it reads (a web page, a file, a tool's output) can try to steer it.",
                    "Martlet - Terminal", yes: "Run without asking", no: "Keep asking", questionId: "ToolsTerminalNoAskQuestion"))
                SaveTerminal(service, service.Terminal with { AskFirst = false });
            else services.Redraw();
        });
        children.Add(ask);
        children.Add(Note("The talk window shows each command with Allow once and Deny; no answer within 60 seconds means Deny.",
            new Thickness(0, 0, 0, 8)));
        children.Add(HelpTip.Explain("Commands run hidden, one at a time, as you and never as administrator. A command that waits " +
            "for typing ends at once, one that runs past the time limit is stopped, and each command and what it prints go to your " +
            "Thinking model.", new Thickness(0, 0, 0, 0), "Commands", "commands", wiki: "Tools-and-MCP#terminal"));
        return Card([.. children]);
    }

    private void ChooseTerminalFolder(McpToolService service)
    {
        if (services.Closing) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where terminal commands start", Multiselect = false, InitialDirectory = service.Terminal.StartFolder
        };
        if (dialog.ShowDialog(services.Owner) == true) SaveTerminal(service, service.Terminal with { Folder = dialog.FolderName });
    }

    private void SaveTerminal(McpToolService service, TerminalSettings next)
    {
        var before = service.Terminal;
        if (!service.SetTerminal(next))
        {
            toolsNotice = "Couldn't save the terminal settings: check access to Martlet's data folder.";
            services.Redraw();
            return;
        }
        toolsNotice = null;
        services.Say(ToolsPageModel.Saved(before, next));
        services.Redraw();
    }
}
