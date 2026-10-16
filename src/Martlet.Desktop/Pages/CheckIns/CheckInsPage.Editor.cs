using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>The controls every check-in card shares on Companion › Check-ins, built-in or your own.</summary>
internal sealed partial class CheckInsPage
{
    /// <summary>The controls every check-in card shares, built-in or your own: Its answer, then what it gets to know, when it
    /// waits, what it takes with each run, a script and what its model must handle. A switch or a choice saves at once, and
    /// typing saves after a short pause.</summary>
    private (ComboBox Outcome, StackPanel Choices, Func<CheckInsPageModel.CheckInEdit> Read) Editor(string id, string name, CheckInsPageModel.CheckInEdit start,
        AutoSave autoSave)
    {
        void SaveNow() => autoSave.SaveNowAsync().Forget();
        var outcome = Compact(new ComboBox
        {
            ItemsSource = CheckInsPageModel.OutcomeChoices.Select(c => c.Label).ToArray(),
            SelectedIndex = Math.Max(0, Array.FindIndex(CheckInsPageModel.OutcomeChoices, c => c.Outcome == start.Outcome)), Width = 250,
            ToolTip = CheckInsPageModel.OutcomeHelp
        });
        AutomationProperties.SetAutomationId(outcome, "CheckInOutcome-" + id);
        AutomationProperties.SetName(outcome, name + ": what happens with its answer");
        AutomationProperties.SetHelpText(outcome, CheckInsPageModel.OutcomeHelp);
        outcome.SelectionChanged += (_, _) => SaveNow();
        CheckBox Choice(string label, bool on, string help, string automationId)
        {
            var box = new CheckBox { Content = label, IsChecked = on, Margin = new Thickness(0, 0, 16, 6), ToolTip = help };
            AutomationProperties.SetAutomationId(box, automationId);
            AutomationProperties.SetHelpText(box, help);
            box.Checked += (_, _) => SaveNow();
            box.Unchecked += (_, _) => SaveNow();
            return box;
        }
        var facts = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var factBoxes = new List<(CheckInFacts Fact, CheckBox Box)>();
        foreach (var (fact, label, help) in CheckInsPageModel.FactChoices)
        {
            var box = Choice(label, start.Facts.HasFlag(fact), help, $"CheckInFact-{id}-{fact}");
            factBoxes.Add((fact, box));
            facts.Children.Add(box);
        }
        var conditions = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var conditionBoxes = new List<(CheckInConditions Condition, CheckBox Box)>();
        foreach (var (condition, label, help) in CheckInsPageModel.ConditionChoices)
        {
            var box = Choice(label, start.Conditions.HasFlag(condition), help, $"CheckInWhen-{id}-{condition}");
            conditionBoxes.Add((condition, box));
            conditions.Children.Add(box);
        }
        // The tool sets it may call; a set it chose that this Martlet doesn't know is dropped on the next save.
        var toolSets = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var toolBoxes = new List<(string Set, CheckBox Box)>();
        foreach (var set in CheckInToolSets.All)
        {
            var box = Choice(set.Name, start.ToolSets.Contains(set.Id), $"{set.Does} Its tools: {string.Join(", ", set.Tools.Select(t => t.Name))}.",
                $"CheckInTools-{id}-{set.Id}");
            toolBoxes.Add((set.Id, box));
            toolSets.Children.Add(box);
        }
        var triggers = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var triggerBoxes = new List<(CheckInTriggers Trigger, CheckBox Box)>();
        foreach (var (trigger, label, help) in CheckInsPageModel.TriggerChoices)
        {
            var box = Choice(label, start.Triggers.HasFlag(trigger), help, $"CheckInTrigger-{id}-{trigger}");
            triggerBoxes.Add((trigger, box));
            triggers.Children.Add(box);
        }
        // Only between these hours, and at most this many runs an hour.
        var hours = Enumerable.Range(0, 24).Select(CheckIns.Clock).ToArray();
        var fromHour = Compact(new ComboBox { ItemsSource = hours, SelectedIndex = start.FromHour, Width = 90 });
        AutomationProperties.SetAutomationId(fromHour, "CheckInFrom-" + id);
        AutomationProperties.SetName(fromHour, name + ": runs from");
        var untilHour = Compact(new ComboBox { ItemsSource = hours, SelectedIndex = start.UntilHour, Width = 90 });
        AutomationProperties.SetAutomationId(untilHour, "CheckInUntil-" + id);
        AutomationProperties.SetName(untilHour, name + ": runs until");
        const string hoursHelp = "With Only between these hours ticked, it runs from the first hour up to the second, past " +
            "midnight when the second comes first. The two must differ.";
        fromHour.ToolTip = untilHour.ToolTip = hoursHelp;
        AutomationProperties.SetHelpText(fromHour, hoursHelp);
        AutomationProperties.SetHelpText(untilHour, hoursHelp);
        var mostPerHour = Compact(new ComboBox
        {
            ItemsSource = CheckIns.MostPerHourChoices.Select(n => n == 0 ? "No limit" : n == 1 ? "Once an hour" : $"{n} times an hour").ToArray(),
            SelectedIndex = Math.Max(0, CheckIns.MostPerHourChoices.ToList().IndexOf(start.MostPerHour)), Width = 150
        });
        AutomationProperties.SetAutomationId(mostPerHour, "CheckInMostPerHour-" + id);
        AutomationProperties.SetName(mostPerHour, name + ": at most");
        const string mostHelp = "A guard: however often its conditions are met, it runs at most this many times in an hour.";
        mostPerHour.ToolTip = mostHelp;
        AutomationProperties.SetHelpText(mostPerHour, mostHelp);
        fromHour.SelectionChanged += (_, _) => SaveNow();
        untilHour.SelectionChanged += (_, _) => SaveNow();
        mostPerHour.SelectionChanged += (_, _) => SaveNow();
        var limits = new WrapPanel { Margin = new Thickness(0, 0, 0, 0) };
        limits.Children.Add(RowGroup(RowLabel("From", fromHour), fromHour));
        limits.Children.Add(RowGroup(RowLabel("Until", untilHour), untilHour));
        limits.Children.Add(RowGroup(RowLabel("At most", mostPerHour), mostPerHour));
        // The model it needs: text always; pictures and recordings by choice, and always for a screenshot or a recording.
        var ownVision = start.Needs.HasFlag(ThinkingCapability.Vision);
        var ownAudio = start.Needs.HasFlag(ThinkingCapability.Audio);
        var text = new CheckBox { Content = "Text", IsChecked = true, IsEnabled = false, Margin = new Thickness(0, 0, 16, 6) };
        AutomationProperties.SetAutomationId(text, $"CheckInNeeds-{id}-Text");
        var vision = new CheckBox { Content = "Sees pictures", Margin = new Thickness(0, 0, 16, 6) };
        AutomationProperties.SetAutomationId(vision, $"CheckInNeeds-{id}-Vision");
        AutomationProperties.SetHelpText(vision, "Only Thinking pool members whose model sees pictures take this check-in.");
        var audio = new CheckBox { Content = "Hears recordings", Margin = new Thickness(0, 0, 16, 6) };
        AutomationProperties.SetAutomationId(audio, $"CheckInNeeds-{id}-Audio");
        AutomationProperties.SetHelpText(audio, "Only Thinking pool members whose model hears recordings take this check-in.");
        var needs = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        needs.Children.Add(text);
        needs.Children.Add(vision);
        needs.Children.Add(audio);
        var screenshot = new CheckBox
        {
            Content = "A screenshot of the screen in front", IsChecked = start.Screenshot, Margin = new Thickness(0, 0, 16, 6),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetAutomationId(screenshot, "CheckInScreenshot-" + id);
        AutomationProperties.SetHelpText(screenshot, "Each run takes a screenshot of the screen you work on. It doesn't run while a private window is in front.");
        var recording = Compact(new ComboBox
        {
            ItemsSource = new[] { "No recording", "The last seconds of the microphone", "The last seconds of what this PC plays" },
            SelectedIndex = (int)start.Recording, Width = 280
        });
        AutomationProperties.SetAutomationId(recording, "CheckInRecording-" + id);
        AutomationProperties.SetName(recording, "Recording with each run");
        var seconds = Compact(new ComboBox
        {
            ItemsSource = CheckIns.RecordingChoices.Select(s => $"{s} seconds").ToArray(),
            SelectedIndex = Math.Max(0, CheckIns.RecordingChoices.ToList().IndexOf(start.RecordingSeconds)), Width = 120
        });
        AutomationProperties.SetAutomationId(seconds, "CheckInSeconds-" + id);
        AutomationProperties.SetName(seconds, "How many seconds it records");
        seconds.ToolTip = CheckInsPageModel.LengthHelp;
        AutomationProperties.SetHelpText(seconds, CheckInsPageModel.LengthHelp);
        var script = new TextBox
        {
            Text = start.Script, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap,
            MaxLength = CheckIns.MaximumScriptCharacters, MinHeight = 40, MaxHeight = 160, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 4, 0, 0)
        };
        AutomationProperties.SetAutomationId(script, "CheckInScript-" + id);
        AutomationProperties.SetName(script, "A script it runs first");
        AutomationProperties.SetHelpText(script, "Windows PowerShell runs it hidden in your home folder before each run, " +
            $"for at most {CheckIns.ScriptTimeout.TotalSeconds:0} seconds. What it prints goes to the check-in.");
        InputHint.SetText(script, "PowerShell, for example: Get-Process | Select-Object -First 5 Name");
        void ShowNeeds()
        {
            // Disable before checking, so a forced tick isn't taken as the owner's choice.
            vision.IsEnabled = screenshot.IsChecked != true;
            vision.IsChecked = !vision.IsEnabled || ownVision;
            audio.IsEnabled = recording.SelectedIndex <= 0;
            audio.IsChecked = !audio.IsEnabled || ownAudio;
            seconds.IsEnabled = recording.SelectedIndex > 0;
        }
        ShowNeeds();
        script.TextChanged += (_, _) =>
        {
            services.MarkEdited();
            autoSave.Changed();
        };
        void Chose(CheckBox box, bool on)
        {
            if (!box.IsEnabled) return;
            if (box == vision) ownVision = on;
            else ownAudio = on;
            SaveNow();
        }
        vision.Checked += (_, _) => Chose(vision, true);
        vision.Unchecked += (_, _) => Chose(vision, false);
        audio.Checked += (_, _) => Chose(audio, true);
        audio.Unchecked += (_, _) => Chose(audio, false);
        void Gathers()
        {
            ShowNeeds();
            SaveNow();
        }
        screenshot.Checked += (_, _) => Gathers();
        screenshot.Unchecked += (_, _) => Gathers();
        recording.SelectionChanged += (_, _) => Gathers();
        seconds.SelectionChanged += (_, _) => SaveNow();

        var view = new StackPanel();
        view.Children.Add(HelpTip.After(new TextBlock { Text = "It gets to know", Margin = new Thickness(0, 8, 0, 0) }, "what it gets to know",
            "It always gets the day and time. Each fact you tick adds a few lines of text to its question; a fact Martlet doesn't " +
            "have now (for example the screen, while Martlet doesn't watch it) says so instead. Point at each one to see what it gets.",
            "CheckInFacts-" + id));
        view.Children.Add(facts);
        view.Children.Add(HelpTip.After(new TextBlock { Text = "It waits until", Margin = new Thickness(0, 4, 0, 0) }, "when it waits",
            "Every check-in also waits for its time, while you talk and while nobody uses this PC. Check now skips the waits for " +
            "time and something new, but not the others. Point at each one to see when.", "CheckInWhen-" + id));
        view.Children.Add(conditions);
        view.Children.Add(HelpTip.After(new TextBlock { Text = "It starts when (instead of every few minutes)", Margin = new Thickness(0, 4, 0, 0) },
            "what starts it", CheckInsPageModel.TriggersHelp + " Point at each one to see when.", "CheckInTriggers-" + id));
        view.Children.Add(triggers);
        view.Children.Add(limits);
        view.Children.Add(new TextBlock { Text = "With each run it takes", Margin = new Thickness(0, 4, 0, 0) });
        var inputs = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        inputs.Children.Add(RowGroup(screenshot));
        inputs.Children.Add(RowGroup(recording));
        inputs.Children.Add(RowGroup(RowLabel("Length", seconds), seconds));
        view.Children.Add(inputs);
        view.Children.Add(HelpTip.After(new Label
        {
            Content = "A _script it runs first (optional)", Target = script, Padding = new Thickness(0), Margin = new Thickness(0, 6, 0, 0)
        }, "the script", "A Windows PowerShell script; the check-in reads what it prints. For example, the busiest programs: " +
            "Get-Process | Sort-Object CPU -Descending | Select-Object -First 10 Name, CPU. It runs hidden in your home folder, " +
            $"as you, and stops after {CheckIns.ScriptTimeout.TotalSeconds:0} seconds.", "CheckInScript-" + id));
        view.Children.Add(script);
        view.Children.Add(HelpTip.After(new TextBlock { Text = "It may use these tools", Margin = new Thickness(0, 8, 0, 0) }, "its tools",
            $"The model can call these tools while it answers, at most {CheckIns.MaximumToolCalls} calls in " +
            $"{CheckIns.MaximumToolRounds} rounds. Only Thinking pool members on an endpoint, whose model calls tools, take a " +
            "check-in with tools. With Its tools act, the tools are what it does. Point at each one to see what it does.",
            "CheckInTools-" + id));
        view.Children.Add(toolSets);
        view.Children.Add(HelpTip.After(new TextBlock { Text = "The model must handle", Margin = new Thickness(0, 8, 0, 0) }, "what the model must handle",
            "Only Thinking pool members that can do all of these take this check-in. A screenshot needs a model " +
            "that sees pictures, and a recording needs one that hears recordings. They go only to that member, which can be a " +
            "cloud service. The microphone and what this PC plays are kept in memory, only while a check-in that is on asks for " +
            "them and Martlet hears them. " + CheckInsPageModel.LengthHelp, "CheckInNeeds-" + id));
        view.Children.Add(needs);
        // Where it runs in the Thinking pool: like other check-ins, or its own choice.
        var (runsOnView, readRunsOn) = RunsOnPicker.Create("CheckInRunsOn-" + id, "CheckInRunsOnMember-" + id, name, start.RunsOn, inherit: true,
            ThinkingPoolSettings.Load(services.DataDirectory), SaveNow);
        view.Children.Add(runsOnView);
        return (outcome, view, () => new CheckInsPageModel.CheckInEdit(
            factBoxes.Where(b => b.Box.IsChecked == true).Aggregate(CheckInFacts.None, (all, b) => all | b.Fact),
            conditionBoxes.Where(b => b.Box.IsChecked == true).Aggregate(CheckInConditions.None, (all, b) => all | b.Condition),
            CheckInsPageModel.OutcomeChoices[Math.Max(0, outcome.SelectedIndex)].Outcome,
            ThinkingCapability.Text | (ownVision ? ThinkingCapability.Vision : ThinkingCapability.None) |
                (ownAudio ? ThinkingCapability.Audio : ThinkingCapability.None),
            screenshot.IsChecked == true, (CheckInRecording)Math.Max(0, recording.SelectedIndex),
            CheckIns.RecordingChoices[Math.Max(0, seconds.SelectedIndex)], script.Text.Replace("\r\n", "\n", StringComparison.Ordinal),
            [.. toolBoxes.Where(b => b.Box.IsChecked == true).Select(b => b.Set)])
        {
            Triggers = triggerBoxes.Where(b => b.Box.IsChecked == true).Aggregate(CheckInTriggers.None, (all, b) => all | b.Trigger),
            FromHour = Math.Max(0, fromHour.SelectedIndex), UntilHour = Math.Max(0, untilHour.SelectedIndex),
            MostPerHour = CheckIns.MostPerHourChoices[Math.Max(0, mostPerHour.SelectedIndex)],
            RunsOn = readRunsOn()
        });
    }
}
