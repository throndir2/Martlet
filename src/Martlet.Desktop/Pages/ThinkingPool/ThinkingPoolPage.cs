using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

/// <summary>Companion › Thinking pool (an optional extra), the UI only: one list of the machines that do Martlet's background
/// work (thinking longer, research, screen and sound summaries, the judges and other helpers). There is no separate on/off
/// choice: the list is the pool. Each row is one machine (one graphics card of a paired computer, a model in Ollama on this PC or
/// an endpoint) with its On box (<c>ThinkingPoolOn-&lt;n&gt;</c>), its model, slots and rules (Quick jobs, Long jobs, Backup for
/// slow replies); a machine that is off keeps its settings and takes no jobs. With no machine on, the pool is off. Below the
/// list: a paired computer's graphics card without a Thinking pool model yet, paired computers that aren't in the pool (why, and
/// In the pool), and Add a machine (another of your computers, a model in Ollama on this PC, a cloud provider or server). Then
/// Runs on, Thinking longer (its own on/off, shared with your computers), Web research, Backup for slow replies and Busy pool.
/// The members are kept in thinking-pool.json; its cloud providers and servers (with their keys) travel to your other computers
/// with the shared settings (<see cref="SharedThinkingPool"/>). The conversation keeps its own Thinking model, which is never a
/// member, so its replies start as fast as they can (<see cref="DeepThinkingPlan"/>).
/// <para>Files: this one (the Now card, Thinking longer and Web research), ThinkingPoolPage.Machines.cs (the machine list),
/// ThinkingPoolPage.MemberRow.cs (one machine's row), ThinkingPoolPage.AddMachine.cs (Add a machine) and
/// ThinkingPoolPage.BusyPool.cs (Runs on, Backup for slow replies and Busy pool). What each line says and what each change
/// saves is in <see cref="ThinkingPoolPageModel"/>; what the page needs from the app is <see cref="IThinkingPoolPageServices"/>.
/// MainWindow keeps one page and draws it again (<see cref="Render"/>) each time it opens or changes.</para></summary>
internal sealed partial class ThinkingPoolPage(IThinkingPoolPageServices services)
{
    // The last check of whether a model fits beside Thinking's in Ollama on this PC (Add a machine › A model in Ollama on this PC).
    private SideBySideFit? localFit;
    private string? localFitModel;

    /// <summary>Draws the page's cards into <paramref name="page"/>.</summary>
    internal void Render(Panel page)
    {
        var model = new ThinkingPoolPageModel(services);
        var now = new TextBlock { Text = model.NowText, FontSize = 15, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(now, "DeepThinkingNow");
        var why = Note(model.WhyText, new Thickness(0, 4, 0, 0));
        if (model.WhyIsWarning) why.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(why, "DeepThinkingParallel");
        page.Children.Add(Card(Heading("Now"), now, why));

        // The list is the pool: no machine on means the pool is off. Add a machine is at the end of the list.
        page.Children.Add(MachinesCard(model));
        page.Children.Add(RunsOnCard(model));
        page.Children.Add(ThinkLongerCard(model));
        page.Children.Add(WebResearchCard(model));
        page.Children.Add(BackupThinkingCard(model));
        page.Children.Add(PriorityCard(model));
    }

    // ---------- Thinking longer (saved with the reply settings) ----------

    /// <summary>Whether Martlet may think longer at all (ThinkLongerOn, saved at once with the reply settings, so your computers
    /// share it), how hard it thinks something over and when it shares the result (a think has no time or hourly limit). How hard
    /// and when save a moment after each change, with the reply settings.</summary>
    private Border ThinkLongerCard(ThinkingPoolPageModel model)
    {
        var saved = model.SavedThinkLonger;
        var current = saved ?? new ThinkLongerSettings();
        var changed = (Action)(() => { });
        const string onLabel = "Let Martlet think things over in the background";
        var turnOn = new CheckBox
        {
            IsChecked = model.On, Margin = new Thickness(0, 0, 0, 8),
            Content = new TextBlock { Text = onLabel, TextWrapping = TextWrapping.Wrap },
            ToolTip = "Off: Martlet answers everything right away and never thinks in the background or researches. Your computers share this choice."
        };
        AutomationProperties.SetAutomationId(turnOn, "ThinkLongerOn");
        AutomationProperties.SetName(turnOn, "Thinking longer: " + onLabel);
        // Checked/Unchecked rather than Click, so UI Automation's toggle (MCP ui_toggle) changes the choice too.
        turnOn.Checked += (_, _) => model.TurnDeepThinkingAsync(true, "Thinking longer is on.").Forget();
        turnOn.Unchecked += (_, _) => model.TurnDeepThinkingAsync(false, "Thinking longer is off. Martlet answers everything right away.").Forget();
        ComboBox Choice(string id, string name, IEnumerable<string> items, int selected)
        {
            var box = new ComboBox { Width = 260, ItemsSource = items.ToArray(), SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetAutomationId(box, id);
            AutomationProperties.SetName(box, name);
            box.SelectionChanged += (_, _) => changed();
            return box;
        }
        var effort = Choice("ThinkLongerEffort", "How hard it thinks", ["Medium (default)", "High"],
            Array.IndexOf(ThinkingPoolPageModel.ThinkEfforts, current.HowHard));
        var when = Choice("ThinkLongerDelivery", "When Martlet shares the result",
            ["As soon as Martlet is free (default)", "When I talk next"], Array.IndexOf(ThinkingPoolPageModel.ThinkDeliveries, current.When));
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetAutomationId(status, "ThinkLongerStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        void Show(ThinkLongerSettings? settings)
        {
            var (text, problem) = model.ThinkLongerStatus(settings ?? new());
            status.Text = text;
            status.SetResourceReference(TextBlock.ForegroundProperty, problem ? "WarningBrush" : "MutedBrush");
            foreach (var control in new Control[] { effort, when }) control.IsEnabled = settings?.On ?? true;
        }
        Show(saved);
        // There is no Save button: a change saves a moment later, into the newest saved reply settings (whether it is on stays
        // as saved: ThinkLongerOn turns it on or off).
        var autoSave = new AutoSave(() => model.SaveThinkLongerChoicesAsync(
            () => (ThinkingPoolPageModel.ThinkEfforts[Math.Max(0, effort.SelectedIndex)], ThinkingPoolPageModel.ThinkDeliveries[Math.Max(0, when.SelectedIndex)]),
            Show));
        services.UseAutoSave(autoSave);
        changed = () => { services.MarkEdited(); autoSave.Changed(); };
        return Card(Heading("Thinking longer"),
            HelpTip.Explain("Replies answer right away (Thinking steps are off by default). When a task really needs thought, such as writing " +
                "song lyrics, a story or a plan, or tricky math or code, Martlet can say it'll think it over and work on it in the " +
                "background while you keep talking, then bring it up when it's done.", new Thickness(0, 0, 0, 8), "ThinkingSteps", "Thinking steps"),
            turnOn, status,
            TerminalRow("How hard", effort), TerminalRow("Share it", when),
            Note("It thinks with Thinking steps on, whatever replies use; one think runs on each free slot of a machine that takes " +
                "long jobs (when the pool has two or more slots, one that takes quick jobs stays free), with no time limit and no limit " +
                "on how many. Stop (Esc) doesn't end it: its Cancel in the talk window or closing the conversation do. A paid " +
                "provider may charge for its thinking.",
                new Thickness(0, 8, 0, 0)));
    }

    // ---------- Web research (saved with the reply settings) ----------

    /// <summary>Whether Martlet may look things up on the web when asked (on by default; the card says what leaves this PC: the
    /// search words go to DuckDuckGo and the pages' sites see this PC's internet address). It saves at once, with the reply
    /// settings, so your computers share it; Deep thinking off turns it off too.</summary>
    private static Border WebResearchCard(ThinkingPoolPageModel model)
    {
        var settings = model.ThinkLongerChoices;
        var allow = new CheckBox
        {
            IsChecked = settings.WebResearchOn, IsEnabled = settings.On, Margin = new Thickness(0, 8, 0, 6),
            Content = new TextBlock { Text = "Let Martlet search the web and read pages when I ask it to look something up", TextWrapping = TextWrapping.Wrap }
        };
        AutomationProperties.SetAutomationId(allow, "WebResearchOn");
        AutomationProperties.SetName(allow, "Let Martlet search the web when I ask it to look something up");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetAutomationId(status, "WebResearchStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var (text, problem) = model.WebResearchStatus();
        status.Text = text;
        status.SetResourceReference(TextBlock.ForegroundProperty, problem ? "WarningBrush" : "MutedBrush");
        allow.Checked += (_, _) => model.SetWebResearchAsync(true).Forget();
        allow.Unchecked += (_, _) => model.SetWebResearchAsync(false).Forget();
        var budget = WebResearch.Budget;
        var disclosure = Note("What leaves this PC: your search words go to DuckDuckGo, and each page Martlet reads sees this PC's " +
            "internet address. What the pages say goes to the Thinking pool member that works on it, with the recent conversation. Martlet only " +
            $"connects to public websites (never your own network). It researches the way a careful person would: up to {budget.Searches} " +
            $"searches and {budget.Pages} pages each time you ask, taking notes as it reads, with no time limit, one thing at a time. " +
            "Cancel in the talk window stops it, and a paid provider may charge for its thinking. The report and its sources are kept " +
            "in Creations; Martlet offers to show it in your browser.",
            new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(disclosure, "WebResearchDisclosure");
        return Card(Heading("Web research"),
            HelpTip.Explain("Ask Martlet to look something up or research it and it says it'll look into it, searches the web and reads pages " +
                "in the background while you keep talking, then tells you what it found and offers the full report.", new Thickness(0, 0, 0, 4), "Research", "looking things up"),
            status, allow, disclosure);
    }
}
