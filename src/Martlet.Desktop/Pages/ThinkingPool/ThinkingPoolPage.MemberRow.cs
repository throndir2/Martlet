using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

/// <summary>One machine's row in Companion › Thinking pool's machine list.</summary>
internal sealed partial class ThinkingPoolPage
{
    /// <summary>One machine in the list: whether it is on (its On box; off keeps its settings and takes no jobs), what it reads and
    /// writes, its slots and whether it can run now; what may slow it or cost money (Waits while you talk, Costs money, Offline);
    /// its model (<see cref="ModelChoice"/>), its rules (Quick jobs, Long jobs, Backup for slow replies) and smarts; and its slots
    /// and Remove (an endpoint). A paired computer's Thinking pool model and slots are that computer's role settings: choosing
    /// another sets them there, and each check of that computer takes the role's slot count again.</summary>
    private DockPanel MemberRow(ThinkingPoolPageModel model, ThinkingPoolPageModel.MemberLine line)
    {
        var pool = model.Pool;
        var (member, i, key, name, on) = (line.Member, line.Index, line.Key, line.Name, line.On);
        var (hostId, card, host, check, paid, slots) = (line.HostId, line.Card, line.Host, line.Check, line.Paid, line.Slots);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Opacity = on ? 1 : 0.75 };
        text.Children.Add(new TextBlock { Text = name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var detail = Note(line.Detail, new Thickness(0, 2, 0, 0));
        AutomationProperties.SetName(detail, $"{name}: {line.Detail}");
        AutomationProperties.SetAutomationId(detail, $"ThinkingPoolMember-{i}");
        text.Children.Add(detail);
        var badgeLine = Note(string.Join("  \u00b7  ", line.Badges), new Thickness(0, 2, 0, 0));
        badgeLine.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        badgeLine.Visibility = line.Badges.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        badgeLine.ToolTip = "Waits while you talk: it shares the conversation's computer, so it starts no new pool work while you talk. " +
            "Costs money: a paid cloud provider. Offline: its computer doesn't answer now. Limiting requests: its provider asked " +
            "Martlet to slow down, so it gets fewer jobs for a while and its jobs wait instead of failing.";
        AutomationProperties.SetName(badgeLine, $"{name}: {badgeLine.Text}");
        AutomationProperties.SetAutomationId(badgeLine, $"ThinkingPoolBadges-{i}");
        text.Children.Add(badgeLine);
        // One graphics card for each Thinking model: a pool model on the card of this computer's Thinking shares it.
        if (line.SharedCard is { } shared)
        {
            var warning = Note(shared, new Thickness(0, 2, 0, 0));
            warning.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            AutomationProperties.SetName(warning, $"{hostId}: {shared}");
            AutomationProperties.SetAutomationId(warning, "DeepThinkingShare-" + hostId + line.Suffix);
            text.Children.Add(warning);
        }

        // Checked/Unchecked rather than Click, so UI Automation's toggle (MCP ui_toggle) changes the choice too.
        CheckBox Rule(string id, string label, bool value, string tip, Action<bool> save)
        {
            var box = new CheckBox { IsChecked = value, Content = label, ToolTip = tip, Margin = new Thickness(0, 6, 18, 0) };
            AutomationProperties.SetAutomationId(box, $"{id}-{i}");
            AutomationProperties.SetName(box, $"{name}: {label}");
            box.Checked += (_, _) => save(true);
            box.Unchecked += (_, _) => save(false);
            return box;
        }
        var rules = new WrapPanel();
        rules.Children.Add(Rule("ThinkingPoolQuick", "Quick jobs", pool.TakesQuickJobs(key),
            "The judges (are you done talking, did you interrupt) and the screen and sound summaries. They are short and need a fast answer.",
            take => model.SavePoolAsync(p => p.WithJobs(key, quick: take), take ? $"{name} takes quick jobs again."
                : $"{name} no longer takes quick jobs: the judges and the summaries go to other machines.").Forget()));
        rules.Children.Add(Rule("ThinkingPoolLong", "Long jobs", pool.TakesLongJobs(key),
            "Thinking longer, research, a song's lyrics, touch zones, remembering, naming and check-ins. They can run for minutes.",
            take => model.SavePoolAsync(p => p.WithJobs(key, @long: take), take ? $"{name} takes long jobs again."
                : $"{name} no longer takes long jobs: thinking longer, research and the other long jobs go to other machines.").Forget()));
        rules.Children.Add(Rule("ThinkingPoolAnswers", "Backup for slow replies" + (paid ? " (each request may cost money)" : ""), pool.Answers(key),
            "With Backup for slow replies on (below), a reply that is slow to start sends the same request here too, and the first " +
                "to start gives the reply. Choose a machine with the same model as the conversation, or a similar one.",
            allow => model.SavePoolAsync(p => p.WithAnswers(key, allow), allow
                ? $"{name} may answer for the conversation when a reply is slow to start."
                : $"{name} no longer answers for the conversation.").Forget()));
        // Only an external member asks first: this PC and paired computers always may receive pictures and recordings.
        if (ThinkingPoolSettings.IsExternal(member))
            rules.Children.Add(Rule("ThinkingPoolMedia", "May receive pictures and recordings", pool.MayReceiveMedia(member),
                "Check-ins, screen and sound summaries and other pool jobs can include a screenshot or a recording. This machine is " +
                    "outside this PC and your paired computers, so it gets them only when this is ticked. Text-only jobs go to it either way.",
                allow => model.SavePoolAsync(p => p.WithMedia(key, allow), allow
                    ? $"{name} may now receive pictures and recordings."
                    : $"{name} no longer receives pictures and recordings: jobs with them go to other machines or use their simple rules.").Forget()));
        text.Children.Add(rules);
        // How smart its model is, for Runs on (below): Martlet's guess from the model name, or the owner's choice.
        var guess = ThinkingSmartsGuess.From(member.ModelId);
        var smarts = new ComboBox
        {
            Width = 190, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            ItemsSource = new[] { $"Guessed: {guess}", "Fast", "Standard", "Smart" },
            SelectedIndex = pool.Smarts.TryGetValue(key, out var chosenSmarts) ? 1 + (int)chosenSmarts : 0,
            ToolTip = "How smart this machine's model is. Martlet guesses it from the model name: small models (about 4B or less) are " +
                "Fast, large ones (about 26B or more, and big cloud models) are Smart. Runs on (below) uses it: Prefer smart and " +
                "Smart only send jobs to the smartest machines."
        };
        AutomationProperties.SetAutomationId(smarts, $"ThinkingPoolSmarts-{i}");
        AutomationProperties.SetName(smarts, $"{name}: smarts");
        smarts.SelectionChanged += (_, _) =>
        {
            ThinkingSmarts? level = smarts.SelectedIndex <= 0 ? null : (ThinkingSmarts)(smarts.SelectedIndex - 1);
            model.SavePoolAsync(p => p.WithSmarts(key, level), level is { } set ? $"{name} is {set} now."
                : $"{name} uses Martlet's guess again: {guess}.").Forget();
        };
        text.Children.Add(TerminalRow("Smarts", smarts));
        if (ModelChoice(model, line) is { } modelRow) text.Children.Insert(text.Children.Count - 1, modelRow);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        // On: the list is the pool. Unticking keeps the machine and its settings in the list but gives it no jobs (a paired
        // computer's card then never joins again by itself); ticking it uses it again.
        const string onLabel = "On";
        var onBox = new CheckBox
        {
            Content = onLabel, IsChecked = on, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
            ToolTip = "Untick to stop using this machine for background work; it keeps its settings. Tick it to use it again. " +
                "With no machine on, the Thinking pool is off."
        };
        AutomationProperties.SetAutomationId(onBox, $"ThinkingPoolOn-{i}");
        AutomationProperties.SetName(onBox, $"{name}: {onLabel}");
        // Checked/Unchecked rather than Click, so UI Automation's toggle (MCP ui_toggle) changes the choice too.
        onBox.Checked += (_, _) => model.SavePoolAsync(p => p.TurnOn(key), $"{name} is on: it takes jobs again.").Forget();
        onBox.Unchecked += (_, _) => model.SavePoolAsync(p => p.TurnOff(key),
            $"{name} is off: it takes no jobs and keeps its settings. Tick On to use it again.").Forget();
        buttons.Children.Add(onBox);
        if (member.OnHostRole && hostId is not null)
        {
            // Its Thinking pool role's slots: the role's settings on that computer (card 2's role for card 2...), set there.
            var role = SelfHostSetup.DeepThinkingRoleKind(card);
            var choices = Enumerable.Range(1, Math.Max(SelfHostSetup.DeepThinkingMaximumSlots, slots)).ToArray();
            var choice = new ComboBox
            {
                Width = 110, ItemsSource = choices.Select(n => $"{n} slot{(n == 1 ? "" : "s")}").ToArray(), SelectedIndex = slots - 1,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
                IsEnabled = host is not null && check is { Reachable: true } && check.Offers?.ContainsKey(role) == true && services.ChangesRolesOn(host),
                ToolTip = $"How many jobs this graphics card runs at once. Choosing another number sets it on {hostId}: a run window " +
                    "shows the change. Each slot beyond the first needs room on the card for another job's context."
            };
            AutomationProperties.SetAutomationId(choice, $"ThinkingPoolSlots-{i}");
            AutomationProperties.SetName(choice, $"Slots on {name}");
            choice.SelectionChanged += (_, _) =>
            {
                var chosen = choice.SelectedIndex + 1;
                if (chosen < 1 || chosen == slots || host is null) return;
                model.ChangeDeepRoleAsync(host, card, DeepThinkingFit.Tag(member.ModelId ?? ""), chosen).Forget();
            };
            buttons.Children.Add(choice);
        }
        else if (hostId is null)
        {
            var choice = new ComboBox { Width = 110, ItemsSource = Enumerable.Range(1, DeepThinkingSettings.MaxPlaces).Select(n => $"{n} slot{(n == 1 ? "" : "s")}").ToArray(),
                SelectedIndex = slots - 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetAutomationId(choice, $"ThinkingPoolSlots-{i}");
            AutomationProperties.SetName(choice, $"Slots on {name}");
            choice.SelectionChanged += (_, _) =>
            {
                var chosen = choice.SelectedIndex + 1;
                model.SavePoolAsync(p => p with
                {
                    Members = [.. p.Members.Select(m => m.Key == key ? m with { Slots = chosen } : m)],
                    OffMembers = [.. p.OffMembers.Select(m => m.Key == key ? m with { Slots = chosen } : m)]
                }, $"{name} now takes {chosen} job{(chosen == 1 ? "" : "s")} at once.").Forget();
            };
            buttons.Children.Add(choice);
            var remove = PageButton("Remove", () => model.RemovePoolMemberAsync(key, name).Forget(), id: $"ThinkingPoolRemove-{i}");
            AutomationProperties.SetName(remove, $"Remove {name} from the Thinking pool");
            buttons.Children.Add(remove);
        }
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(text);
        return row;
    }

    /// <summary>A machine's model, chosen in its row (<c>ThinkingPoolModel-&lt;n&gt;</c>): for a graphics card on a paired computer,
    /// the models its Thinking pool role offers (choosing one sets it up there, in a run window); for a model in Ollama on this PC,
    /// the models Ollama has and Martlet recommends; for a cloud provider or server, the model ID typed (saved when you press Enter
    /// or leave the box). Null for a paired computer's member on its Ollama, whose model is the computer's conversation model.</summary>
    private DockPanel? ModelChoice(ThinkingPoolPageModel model, ThinkingPoolPageModel.MemberLine line)
    {
        var (member, i, name, host, check) = (line.Member, line.Index, line.Name, line.Host, line.Check);
        var key = member.Key;
        var current = member.ModelId ?? "";
        if (member is { OnHostRole: true, HostId: { } hostId })
        {
            var card = member.Card;
            var role = SelfHostSetup.DeepThinkingRoleKind(card);
            var now = DeepThinkingFit.Tag(current);
            var choices = DeepThinkingFit.Models.Contains(now) ? DeepThinkingFit.Models : [now, .. DeepThinkingFit.Models];
            var box = new ComboBox
            {
                Width = 190, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = choices,
                SelectedItem = now,
                IsEnabled = host is not null && check is { Reachable: true } && check.Offers?.ContainsKey(role) == true && services.ChangesRolesOn(host),
                ToolTip = $"The model this graphics card runs. Choosing another one sets it up on {hostId}: a run window shows the " +
                    "download, and the old model keeps working until the new one is ready."
            };
            AutomationProperties.SetAutomationId(box, $"ThinkingPoolModel-{i}");
            AutomationProperties.SetName(box, $"{name}: model");
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedItem is string chosen && chosen != now && host is not null)
                    model.ChangeDeepRoleAsync(host, card, chosen, null, member.ThinksAtOnce).Forget();
            };
            return TerminalRow("Model", box);
        }
        if (member.Place != DeepThinkingPlace.Endpoint) return null;
        if (member.Origin == MainWindow.LocalOllamaBaseUrl)
        {
            var beside = model.LocalBeside;
            string[] choices = [.. new[] { current }.Concat(services.OllamaModels ?? []).Concat(MainWindow.LocalChatModels.Select(m => m.Id))
                .Where(id => id.Length > 0).Distinct(StringComparer.Ordinal)];
            var box = new ComboBox
            {
                Width = 190, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = choices,
                SelectedItem = current,
                ToolTip = "The model in Ollama on this PC that does this machine's jobs. It can't be the conversation's own model, which " +
                    "can't think something over while it answers you."
            };
            AutomationProperties.SetAutomationId(box, $"ThinkingPoolModel-{i}");
            AutomationProperties.SetName(box, $"{name}: model");
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedItem is not string chosen || chosen == current) return;
                if (beside is not null && OllamaSideBySide.Same(chosen, beside))
                {
                    services.ShowStatus($"{chosen} is Thinking's own model, which can't think something over while it answers you. Choose another model.");
                    box.SelectedItem = current;
                    return;
                }
                model.SavePoolAsync(p => p.WithModel(key, chosen), $"{name} now uses {chosen}." +
                    (services.OllamaModels is { } known && !LocalOllama.Serves(known, chosen) ? $" Download {chosen} on Thinking to use it." : "")).Forget();
            };
            return TerminalRow("Model", box);
        }
        var typed = new TextBox { Width = 260, MaxLength = 128, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            Text = current, ToolTip = "The model ID at this provider. Press Enter or leave the box to save it." };
        AutomationProperties.SetAutomationId(typed, $"ThinkingPoolModel-{i}");
        AutomationProperties.SetName(typed, $"{name}: model");
        var saved = false;
        void Commit()
        {
            var chosen = typed.Text.Trim();
            if (saved || chosen.Length == 0 || chosen == current) return;
            saved = true;
            model.SavePoolAsync(p => p.WithModel(key, chosen), $"{name} now uses {chosen}.").Forget();
        }
        typed.LostKeyboardFocus += (_, _) => Commit();
        typed.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Commit(); };
        return TerminalRow("Model", typed);
    }
}
