using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

/// <summary>Companion › Background thinking (an optional extra), the UI only. Martlet can think in the background while you keep
/// talking, and the page answers two questions. What it does: three switches (think longer about hard questions, look things up
/// on the web, help when a reply is slow). Where it runs: every machine Martlet knows (a graphics card of a paired computer, a
/// paired computer, Ollama on this PC, a cloud provider or server), each with a box and a model; these machines are the Thinking
/// pool. Add a cloud provider is at the end of that list. Everything else (each machine's slots and rules, Runs on, Busy pool,
/// how hard it thinks...) is under one Advanced section, closed, with automatic defaults.
/// <para>The members are kept in thinking-pool.json; its cloud providers and servers (with their keys) travel to your other
/// computers with the shared settings (<see cref="SharedThinkingPool"/>). The conversation keeps its own Thinking model, which is
/// never a member, so its replies start as fast as they can (<see cref="DeepThinkingPlan"/>).</para>
/// <para>Files: this one (the top, What it does and Advanced), ThinkingPoolPage.Machines.cs (Where it runs),
/// ThinkingPoolPage.MemberRow.cs (a machine's row and its Advanced settings), ThinkingPoolPage.AddMachine.cs (Add a cloud
/// provider and the Advanced add forms) and ThinkingPoolPage.BusyPool.cs (Runs on, Backup and Busy pool under Advanced). What
/// each line says and what each change saves is in <see cref="ThinkingPoolPageModel"/>; what the page needs from the app is
/// <see cref="IThinkingPoolPageServices"/>. MainWindow keeps one page and draws it again (<see cref="Render"/>) each time it
/// opens or changes.</para></summary>
internal sealed partial class ThinkingPoolPage(IThinkingPoolPageServices services)
{
    // The last check of whether a model fits beside Thinking's in Ollama on this PC (Advanced › Another model in Ollama on this PC).
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

        var thinkStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 0, 0, 6) };
        page.Children.Add(WhatItDoesCard(model, thinkStatus));
        page.Children.Add(WhereItRunsCard(model));
        page.Children.Add(AdvancedCard(model, thinkStatus));
    }

    /// <summary>A heading inside the Advanced section.</summary>
    private static TextBlock SubHeading(string text) =>
        new() { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 4), TextWrapping = TextWrapping.Wrap };

    // ---------- What it does ----------

    /// <summary>What background thinking does: think longer about hard questions (ThinkLongerOn, saved with the reply settings, so
    /// your computers share it), look things up on the web (WebResearchOn, under it) and help when a reply is slow
    /// (ThinkingPoolBackup, saved in thinking-pool.json). Each saves at once.</summary>
    private static Border WhatItDoesCard(ThinkingPoolPageModel model, TextBlock thinkStatus)
    {
        var choices = model.ThinkLongerChoices;
        // Checked/Unchecked rather than Click, so UI Automation's toggle (MCP ui_toggle) changes the choice too.
        CheckBox Switch(string id, string label, string name, bool on, string tip, double left = 0)
        {
            var box = new CheckBox
            {
                IsChecked = on, Margin = new Thickness(left, 8, 0, 2), ToolTip = tip,
                Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, FontSize = 15 }
            };
            AutomationProperties.SetAutomationId(box, id);
            AutomationProperties.SetName(box, name);
            return box;
        }
        TextBlock Status(string id, (string Text, bool Problem) line, double left)
        {
            var status = Note(line.Text, new Thickness(left, 0, 0, 4));
            if (line.Problem) status.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            AutomationProperties.SetAutomationId(status, id);
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            return status;
        }

        const string thinkLabel = "Think longer about hard questions";
        var think = Switch("ThinkLongerOn", thinkLabel, "Thinking longer: " + thinkLabel, model.On,
            "Song lyrics, a story or a plan, tricky math or code: Martlet says it'll think it over, works on it while you keep talking, " +
            "then brings it up. Off: Martlet answers everything right away and never thinks in the background or researches.");
        think.Checked += (_, _) => model.TurnDeepThinkingAsync(true, "Thinking longer is on.").Forget();
        think.Unchecked += (_, _) => model.TurnDeepThinkingAsync(false, "Thinking longer is off.").Forget();
        var (thinkText, thinkProblem) = model.ThinkLongerStatus(choices);
        thinkStatus.Text = thinkText;
        thinkStatus.SetResourceReference(TextBlock.ForegroundProperty, thinkProblem ? "WarningBrush" : "MutedBrush");
        AutomationProperties.SetAutomationId(thinkStatus, "ThinkLongerStatus");
        AutomationProperties.SetLiveSetting(thinkStatus, AutomationLiveSetting.Polite);

        var web = Switch("WebResearchOn", "Look things up on the web", "Let Martlet search the web to look things up", choices.WebResearchOn,
            "Martlet checks a fact it doesn't know with a quick look-up, and researches what you ask it to, then offers a report.", left: 24);
        web.IsEnabled = choices.On;
        web.Checked += (_, _) => model.SetWebResearchAsync(true).Forget();
        web.Unchecked += (_, _) => model.SetWebResearchAsync(false).Forget();
        var budget = WebResearch.Budget;
        var disclosure = HelpTip.Explain("What leaves this PC: your search words go to DuckDuckGo, and each page Martlet reads sees this PC's " +
            "internet address. What the pages say goes to the machine that works on it, with the recent conversation. Martlet only " +
            "connects to public websites (never your own network). It answers what it knows at once. For one fact it doesn't know for " +
            $"sure (a price, a date, how to get something in a game) it does a quick look-up of up to {WebResearch.QuickBudget.Pages} pages. " +
            $"When you ask it to research something, it researches the way a careful person would: up to {budget.Searches} searches and " +
            $"{budget.Pages} pages ({WebResearch.DeepBudget.Pages} when you ask for an in-depth look), taking notes as it reads, with no " +
            "time limit, one thing at a time. It tells you as soon as it finds what you wanted, then checks the report's sources after " +
            "the report is ready. Cancel in the talk window stops it, and a paid provider may charge for its thinking. The report and " +
            "its sources are kept in Creations; Martlet offers to show it in your browser.",
            new Thickness(48, 0, 0, 4), "WebResearchSent", "what leaves this PC", wiki: "Thinking#what-leaves-this-pc");
        AutomationProperties.SetAutomationId(disclosure, "WebResearchDisclosure");

        const string backupLabel = "Help when a reply is slow to start";
        var backup = Switch("ThinkingPoolBackup", backupLabel, "Backup for slow replies: " + backupLabel, model.Pool.BackupThinking,
            "When the conversation's model has no words after a short wait, a free machine below answers too, and the first to start " +
            "gives the reply. A paid cloud provider is asked only when you choose it under Advanced.");
        void Backup(bool on)
        {
            var next = ThinkingPoolPageModel.WithBackup(model.Pool, on);
            model.SavePoolAsync(p => ThinkingPoolPageModel.WithBackup(p, on), ThinkingPoolPageModel.BackupDone(next)).Forget();
        }
        backup.Checked += (_, _) => Backup(true);
        backup.Unchecked += (_, _) => Backup(false);

        return Card(Heading("What it does"),
            HelpTip.Explain("Martlet can think in the background while you keep talking. Your conversation keeps its own Thinking model, " +
                "so replies never wait for this.", new Thickness(0, 0, 0, 4), "BackgroundThinking", "background thinking",
                wiki: "Thinking#thinking-longer"),
            think, thinkStatus,
            web, Status("WebResearchStatus", model.WebResearchStatus(), 48), disclosure,
            backup, Status("ThinkingPoolBackupStatus", (model.BackupStatus, false), 24),
            Note("Martlet also uses these machines for small jobs by itself: screen and sound summaries, remembering, check-ins and " +
                "the judges that tell when you're done talking. You don't need to set anything for them.", new Thickness(0, 10, 0, 0)));
    }

    // ---------- Advanced ----------

    /// <summary>Advanced, closed by default: everything Martlet sets by itself. Thinking longer's How hard and Share it, each
    /// machine's slots and rules, the conversation model fallback, Runs on, Backup's wait, Busy pool and more ways to add a machine.</summary>
    private Border AdvancedCard(ThinkingPoolPageModel model, TextBlock thinkStatus)
    {
        var items = new List<UIElement>
        {
            Note("Martlet sets all of these by itself. Change them only when you want something different.", new Thickness(0, 4, 0, 0)),
            SubHeading("Thinking longer")
        };
        items.AddRange(ThinkLongerChoices(model, thinkStatus));
        items.AddRange(MachineSettings(model));
        items.AddRange(RunsOnSection(model));
        items.AddRange(BackupSection(model));
        items.AddRange(PrioritySection(model));
        items.Add(SubHeading("More ways to add a machine"));
        items.Add(Fold.Create("Another model in Ollama on this PC", "ThinkingPoolAddLocal", false, [.. DeepLocalPanel(model)]));
        items.Add(Fold.Create("Another of your computers", "ThinkingPoolAddComputer", false, [.. DeepComputersPanel(model)]));
        return Card(Fold.Create("Advanced", "ThinkingPoolAdvanced", false, [.. items]));
    }

    /// <summary>How hard Martlet thinks something over and when it shares the result (a think has no time or hourly limit). They
    /// save a moment after each change, with the reply settings; <paramref name="status"/> (ThinkLongerStatus, under the switch)
    /// follows.</summary>
    private IEnumerable<UIElement> ThinkLongerChoices(ThinkingPoolPageModel model, TextBlock status)
    {
        var saved = model.SavedThinkLonger;
        var current = saved ?? new ThinkLongerSettings();
        var changed = (Action)(() => { });
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
        foreach (var control in new Control[] { effort, when }) control.IsEnabled = model.On;
        void Show(ThinkLongerSettings? settings)
        {
            var (text, problem) = model.ThinkLongerStatus(settings ?? new());
            status.Text = text;
            status.SetResourceReference(TextBlock.ForegroundProperty, problem ? "WarningBrush" : "MutedBrush");
            foreach (var control in new Control[] { effort, when }) control.IsEnabled = settings?.On ?? true;
        }
        // There is no Save button: a change saves a moment later, into the newest saved reply settings (whether it is on stays
        // as saved: ThinkLongerOn turns it on or off).
        var autoSave = new AutoSave(() => model.SaveThinkLongerChoicesAsync(
            () => (ThinkingPoolPageModel.ThinkEfforts[Math.Max(0, effort.SelectedIndex)], ThinkingPoolPageModel.ThinkDeliveries[Math.Max(0, when.SelectedIndex)]),
            Show));
        services.UseAutoSave(autoSave);
        changed = () => { services.MarkEdited(); autoSave.Changed(); };
        return [
            TerminalRow("How hard", effort), TerminalRow("Share it", when),
            HelpTip.Explain("Each think runs on a free slot of a machine that takes long jobs, with no time limit. It thinks with Thinking " +
                "steps on, whatever replies use, and no limit on how many; when the pool has two or more slots, one that takes quick jobs " +
                "stays free. Stop (Esc) doesn't end it: its Cancel in the talk window or closing the conversation do. A paid " +
                "provider may charge for its thinking.",
                new Thickness(0, 8, 0, 0), "ThinkLongerHow", "how a think runs", wiki: "Thinking#thinking-longer")];
    }
}
