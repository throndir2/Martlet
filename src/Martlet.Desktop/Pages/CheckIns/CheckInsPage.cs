using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Companion › Check-ins (docs/CONVERSATION.md#check-ins), the UI only: the Now card, a card for each built-in check-in
/// (On, Every, Its answer, its status line, and under More settings its prompt and the shared controls) and Your own check-ins
/// (a row for each, and Add a check-in). Edits save on their own: a switch or a choice at once, typing after a short pause.
/// <para>Files: this one, and CheckInsPage.Editor.cs (the controls every card shares). What each line says and what each change
/// saves is in <see cref="CheckInsPageModel"/>; the check-in runner stays in MainWindow (MainWindow.CheckIns.cs), and what the
/// page needs from the app is <see cref="ICheckInsPageServices"/>. MainWindow keeps one page and draws it again
/// (<see cref="Render"/>) each time it opens or changes; the runner calls <see cref="ShowNow"/> when a check-in's state changes.</para></summary>
internal sealed partial class CheckInsPage(ICheckInsPageServices services)
{
    private readonly CheckInsPageModel model = new(services);
    // Sets the Now card and every status line again; set by each drawing of the page.
    private Action? show;

    /// <summary>Says again what each check-in does now (the runner calls it), when the page has been drawn.</summary>
    internal void ShowNow() => show?.Invoke();

    /// <summary>Draws the page's cards into <paramref name="page"/>.</summary>
    internal void Render(Panel page)
    {
        services.ReadThinkingPoolOnce();
        var now = new TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(now, "CheckInsNow");
        var last = Note("", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(last, "CheckInsLast");
        AutomationProperties.SetLiveSetting(last, AutomationLiveSetting.Polite);
        page.Children.Add(Card(HelpTip.After(Heading("Now"), "check-ins", CheckInsPageModel.About, "CheckInsAbout"), now, last,
            Row(PageButton("Open Thinking pool", services.OpenThinkingPool, link: true, id: "CheckInsOpenPool"))));
        var lines = new List<Action>();
        // Edits to the built-in check-ins' prompts and to your own check-ins save on their own, a moment after typing stops.
        var promptBoxes = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        var promptsTyped = new HashSet<string>(StringComparer.Ordinal);
        var promptStates = new List<Action>();
        var promptSave = new AutoSave(() => SavePromptsAsync(promptBoxes, promptsTyped, () =>
        {
            foreach (var showState in promptStates) showState();
        }));
        var builtIns = new List<(string Id, Func<CheckInChoice> Read)>();
        var rows = new List<Func<CustomCheckIn>>();
        // Synchronous on purpose: Remove saves the other rows first, and a save still running must not bring the removed row back.
        var save = new AutoSave(() =>
        {
            model.SaveEdits(builtIns, rows);
            if (promptsTyped.Count > 0) promptSave.SaveNowAsync().Forget();
            return Task.FromResult(true);
        });
        services.UseAutoSave(save);
        async Task SaveAllAsync()
        {
            await save.SaveNowAsync();
            await promptSave.SaveNowAsync();
        }
        foreach (var checkIn in model.All.Where(c => !c.Custom))
            page.Children.Add(BuiltInCard(checkIn, lines, promptBoxes, promptStates, id =>
            {
                services.MarkEdited();
                promptsTyped.Add(id);
                save.Changed();
            }, builtIns, save, SaveAllAsync));
        page.Children.Add(OwnCheckInsCard(lines, rows, save, SaveAllAsync));
        show = () =>
        {
            now.Text = model.NowText();
            last.Text = model.LastText();
            foreach (var line in lines) line();
        };
        // Each line says why its check-in waits right away, not only after the next look.
        services.LookNow();
        show();
    }

    /// <summary>The Check-ins page's prompt save: writes only the built-in check-in prompts typed on this page into the newest saved
    /// settings, so every other prompt (and an edit from Companion › Prompts) stays as saved. Returns false to be tried again
    /// shortly when another window saved the settings meanwhile. Check-ins use the saved text from their next run.</summary>
    private Task<bool> SavePromptsAsync(IReadOnlyDictionary<string, TextBox> boxes, HashSet<string> typed, Action saved)
    {
        if (typed.Count == 0) return Task.FromResult(true);
        var edits = typed.Where(boxes.ContainsKey).ToDictionary(id => id,
            id => boxes[id].Text.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparer.Ordinal);
        return services.SavePromptsAsync(edits, () =>
        {
            // A prompt typed again while this saved stays to save next.
            foreach (var (id, text) in edits)
                if (boxes.TryGetValue(id, out var box) && box.Text.Replace("\r\n", "\n", StringComparison.Ordinal) == text) typed.Remove(id);
            saved();
        });
    }

    private Border BuiltInCard(CheckIn checkIn, List<Action> lines, Dictionary<string, TextBox> promptBoxes,
        List<Action> promptStates, Action<string> promptTyped, List<(string Id, Func<CheckInChoice> Read)> builtIns, AutoSave autoSave,
        Func<Task> saveAll)
    {
        var id = checkIn.Id;
        var on = new CheckBox { Content = "On", IsChecked = checkIn.On, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(on, "CheckInOn-" + id);
        AutomationProperties.SetName(on, checkIn.Name + " on");
        var every = EveryChoice(checkIn.EveryMinutes);
        AutomationProperties.SetAutomationId(every, "CheckInEvery-" + id);
        AutomationProperties.SetName(every, checkIn.Name + ": how often");
        var status = Note("", new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(status, "CheckInStatus-" + id);
        lines.Add(() => status.Text = model.Line(id));
        var (outcome, choices, read) = Editor(id, checkIn.Name, model.EditOf(checkIn), autoSave);
        // Only what differs from Martlet's own is kept, so a later Martlet can improve the rest.
        builtIns.Add((id, () => CheckInsPageModel.BuiltInChoice(id, on.IsChecked == true, every.SelectedIndex, read())));
        on.Checked += (_, _) => autoSave.SaveNowAsync().Forget();
        on.Unchecked += (_, _) => autoSave.SaveNowAsync().Forget();
        every.SelectionChanged += (_, _) => autoSave.SaveNowAsync().Forget();
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(RowGroup(on));
        row.Children.Add(RowGroup(RowLabel("Every", every), every));
        row.Children.Add(RowGroup(RowLabel("Its answer", outcome), outcome));
        var children = new List<UIElement> { Heading(checkIn.Name), Note(checkIn.Does, new Thickness(0, 0, 0, 0)), row, status };
        var more = new List<UIElement>();
        // Its prompt, like the task of your own check-ins: edited here, saved with Martlet's other prompts (Companion › Prompts
        // lists it too).
        TextBox? promptBox = null;
        string? promptDefault = null;
        if (checkIn.PromptId is { } promptId && PromptCatalog.Find(promptId) is { } prompt)
        {
            var box = new TextBox
            {
                Text = PromptSettings.Text(services.Prompts, promptId), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                MaxLength = PromptSettings.MaximumTextCharacters, MinHeight = 56, MaxHeight = 260,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 0)
            };
            AutomationProperties.SetAutomationId(box, "CheckInPrompt-" + id);
            AutomationProperties.SetName(box, checkIn.Name + ": what it asks");
            AutomationProperties.SetHelpText(box, prompt.Help);
            var state = Note("", new Thickness(0, 2, 0, 0));
            AutomationProperties.SetAutomationId(state, "CheckInPromptState-" + id);
            void ShowState() => state.Text = MainWindow.PromptState(prompt, box.Text, services.Prompts);
            ShowState();
            promptStates.Add(ShowState);
            promptBoxes[promptId] = box;
            box.TextChanged += (_, _) =>
            {
                ShowState();
                promptTyped(promptId);
            };
            promptBox = box;
            promptDefault = prompt.Default;
            more.Add(HelpTip.After(new Label { Content = "_What it asks", Target = box, Padding = new Thickness(0), Margin = new Thickness(0, 6, 0, 0) },
                "placeholders", CheckInsPageModel.PlaceholderHelp + " Empty it to stop this check-in from asking anything.", "CheckInPrompt-" + id));
            more.Add(box);
            more.Add(state);
        }
        more.Add(choices);
        children.Add(Fold.Create("More settings", "CheckInMore-" + id, false, [.. more]));
        var copy = PageButton("Copy as your own", () => CopyAsync(id, saveAll).Forget(), id: "CheckInCopy-" + id);
        AutomationProperties.SetHelpText(copy, $"Makes a check-in of your own with this one's prompt and choices, off until you turn it on.");
        var reset = PageButton("Use built-in settings", () => ResetAsync().Forget(), link: true, id: "CheckInReset-" + id);
        AutomationProperties.SetHelpText(reset, $"Puts Martlet's own prompt, facts, conditions, answer, inputs and tools for {checkIn.Name} " +
            "back. On and Every stay as they are.");
        children.Add(Row(PageButton("Check now", () => SaveThenCheckAsync().Forget(), id: "CheckInRun-" + id), copy, reset));
        return Card([.. children]);

        // A prompt still saving is saved first, so Check now asks what you typed.
        async Task SaveThenCheckAsync()
        {
            await saveAll();
            await services.CheckNowAsync(id);
        }

        async Task ResetAsync()
        {
            if (promptBox is not null && promptDefault is not null) promptBox.Text = promptDefault;
            await saveAll();
            model.UseBuiltIn(checkIn);
        }
    }

    /// <summary>Copy as your own, after the edits still waiting are saved.</summary>
    private async Task CopyAsync(string id, Func<Task> saveAll)
    {
        await saveAll();
        model.Copy(id);
    }

    /// <summary>Your own check-ins: a row for each and Add a check-in. Their edits save on their own (one save for all of them, at
    /// once for a switch or a choice and after a short pause for typing).</summary>
    private Border OwnCheckInsCard(List<Action> lines, List<Func<CustomCheckIn>> rows, AutoSave autoSave, Func<Task> saveAll)
    {
        var stack = new List<UIElement>
        {
            Heading("Your own check-ins"),
            HelpTip.Explain("Write what the Thinking pool should check, such as \"If the user has been at it for hours, suggest a short break\" or " +
                "\"If it's late at night, remind Martlet to talk more softly\". Choose what it gets to know (it always gets the day " +
                "and time), when it waits, how often it runs and what happens with its answer: a reminder in Martlet's next reply, " +
                "Martlet brings it up on its own as soon as it's free, it turns off lingering emotes, it takes the eyes back to " +
                "their usual, or its tools act. A check-in can also take a screenshot, the last seconds of the microphone or of what this PC plays, " +
                "and what a script of yours prints, and you choose the tool sets it may call and what its model must handle. Copy as your own on a built-in " +
                "check-in starts from that one. A new check-in starts off.", new Thickness(0, 0, 0, 4), "CheckInsOwn", "your own check-ins")
        };
        foreach (var custom in model.Choices.Custom) stack.Add(OwnCheckInRow(custom, lines, rows, autoSave, saveAll));
        var add = PageButton("Add a check-in", AddOwnCheckIn, id: "CheckInAdd");
        add.IsEnabled = model.Choices.Custom.Count < CheckIns.MaximumCustom;
        AutomationProperties.SetHelpText(add, $"Adds a check-in of your own, off until you turn it on (at most {CheckIns.MaximumCustom}).");
        stack.Add(Row(add));
        return Card([.. stack]);
    }

    private void AddOwnCheckIn()
    {
        if (services.OpenAutoSave is { Pending: true } pending) pending.SaveNowAsync().Forget();
        model.AddOwn();
    }

    private StackPanel OwnCheckInRow(CustomCheckIn custom, List<Action> lines, List<Func<CustomCheckIn>> rows, AutoSave autoSave,
        Func<Task> saveAll)
    {
        var id = custom.Id;
        var view = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        var name = Compact(new TextBox { Text = custom.Name, MaxLength = CheckIns.MaximumNameCharacters, Width = 220 });
        AutomationProperties.SetAutomationId(name, "CheckInName-" + id);
        AutomationProperties.SetName(name, "Check-in name");
        var on = new CheckBox { Content = "On", IsChecked = custom.On, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(on, "CheckInOn-" + id);
        AutomationProperties.SetName(on, "Check-in on");
        var every = EveryChoice(custom.EveryMinutes);
        AutomationProperties.SetAutomationId(every, "CheckInEvery-" + id);
        AutomationProperties.SetName(every, "How often it runs");
        var (outcome, choices, read) = Editor(id, custom.Name, CheckInsPageModel.EditOf(custom), autoSave);
        var task = new TextBox
        {
            Text = custom.Task, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = CheckIns.MaximumTaskCharacters,
            MinHeight = 56, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 0)
        };
        AutomationProperties.SetAutomationId(task, "CheckInTask-" + id);
        AutomationProperties.SetName(task, "What it checks");
        AutomationProperties.SetHelpText(task, CheckInsPageModel.PlaceholderHelp);
        var status = Note("", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(status, "CheckInStatus-" + id);
        lines.Add(() => status.Text = model.Line(id));
        rows.Add(() => CheckInsPageModel.CustomOf(id, name.Text, on.IsChecked == true, every.SelectedIndex, task.Text, read()));
        void Typed()
        {
            services.MarkEdited();
            autoSave.Changed();
        }
        name.TextChanged += (_, _) => Typed();
        task.TextChanged += (_, _) => Typed();
        on.Checked += (_, _) => autoSave.SaveNowAsync().Forget();
        on.Unchecked += (_, _) => autoSave.SaveNowAsync().Forget();
        every.SelectionChanged += (_, _) => autoSave.SaveNowAsync().Forget();
        var top = new WrapPanel();
        top.Children.Add(RowGroup(name));
        top.Children.Add(RowGroup(on));
        top.Children.Add(RowGroup(RowLabel("Every", every), every));
        top.Children.Add(RowGroup(RowLabel("Its answer", outcome), outcome));
        view.Children.Add(top);
        view.Children.Add(HelpTip.After(new Label { Content = "_What it checks", Target = task, Padding = new Thickness(0), Margin = new Thickness(0, 6, 0, 0) },
            "placeholders", CheckInsPageModel.PlaceholderHelp, "CheckInTask-" + id));
        view.Children.Add(task);
        view.Children.Add(status);
        view.Children.Add(Fold.Create("More settings", "CheckInMore-" + id, false, choices));
        async Task SaveThenCheckAsync()
        {
            await autoSave.SaveNowAsync();
            await services.CheckNowAsync(id);
        }
        view.Children.Add(Row(PageButton("Check now", () => SaveThenCheckAsync().Forget(), id: "CheckInRun-" + id),
            PageButton("Copy as your own", () => CopyAsync(id, saveAll).Forget(), id: "CheckInCopy-" + id),
            PageButton("Remove", () =>
            {
                // The other rows' edits save first; the save is synchronous, so nothing brings the removed row back.
                autoSave.SaveNowAsync().Forget();
                model.Remove(id);
            }, id: "CheckInRemove-" + id)));
        return view;
    }

    private static ComboBox EveryChoice(int minutes)
    {
        var index = 0;
        for (var i = 0; i < CheckIns.EveryChoices.Count; i++)
            if (CheckIns.EveryChoices[i] == minutes) index = i;
        return Compact(new ComboBox { ItemsSource = CheckIns.EveryChoices.Select(CheckInsPageModel.EveryText).ToArray(), SelectedIndex = index, Width = 130 });
    }
}
