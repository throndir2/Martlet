using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;
using Martlet.Presentation;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

/// <summary>One machine in Companion › Background thinking: its row in Where it runs and its settings under Advanced.</summary>
internal sealed partial class ThinkingPoolPage
{
    /// <summary>A machine in the pool, in Where it runs: its box (<c>ThinkingPoolOn-&lt;n&gt;</c>; unticked, it keeps its settings
    /// and takes no jobs, and a paired computer's card then never joins again by itself), its model (<see cref="ModelChoice"/>),
    /// Remove for a cloud provider or server, and what may slow it or cost money.</summary>
    private StackPanel MachineRow(ThinkingPoolPageModel model, ThinkingPoolPageModel.MemberLine line)
    {
        var (member, i, key, name, on) = (line.Member, line.Index, line.Key, line.Name, line.On);
        // The model shows in its list beside the name, so the name doesn't repeat it.
        var shown = member.ModelId is { Length: > 0 } id && name.EndsWith($" ({id})", StringComparison.Ordinal) ? name[..^(id.Length + 3)] : name;
        var onBox = new CheckBox { Content = RowName(shown), IsChecked = on,
            ToolTip = "Untick to stop using this machine for background thinking; it keeps its settings." };
        AutomationProperties.SetAutomationId(onBox, $"ThinkingPoolOn-{i}");
        AutomationProperties.SetName(onBox, $"{name}: On");
        // Checked/Unchecked rather than Click, so UI Automation's toggle (MCP ui_toggle) changes the choice too.
        onBox.Checked += (_, _) => model.SavePoolAsync(p => p.TurnOn(key), $"{name} is on: it takes jobs again.").Forget();
        onBox.Unchecked += (_, _) => model.SavePoolAsync(p => p.TurnOff(key),
            $"{name} is off: it takes no jobs and keeps its settings until you tick it again.").Forget();

        UIElement choice = (UIElement?)ModelChoice(model, line) ?? Note($"Its Ollama's model ({member.ModelId})", new Thickness(0));
        Button? remove = null;
        if (line.HostId is null)
        {
            remove = PageButton("Remove", () => model.RemovePoolMemberAsync(key, name).Forget(), id: $"ThinkingPoolRemove-{i}");
            AutomationProperties.SetName(remove, $"Remove {name} from the Thinking pool");
        }

        var badgeLine = Note(string.Join("  \u00b7  ", line.Badges), new Thickness(0));
        badgeLine.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        badgeLine.Visibility = line.Badges.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // What each badge means, in one sentence.
        var badgeTip = string.Join("; ", line.Badges.Select(badge => badge switch
        {
            "Waits while you talk" => "waits while you talk: it shares the conversation's computer, so it starts no new pool work while you talk",
            "Costs money" => "costs money: a paid cloud provider",
            "Offline" => "offline: its computer doesn't answer now",
            "Limiting requests" => "limiting requests: its provider asked Martlet to slow down, so its jobs wait instead of failing",
            _ => badge
        }));
        badgeLine.ToolTip = badgeTip.Length == 0 ? null : char.ToUpperInvariant(badgeTip[0]) + badgeTip[1..] + ".";
        AutomationProperties.SetName(badgeLine, $"{name}: {badgeLine.Text}");
        AutomationProperties.SetAutomationId(badgeLine, $"ThinkingPoolBadges-{i}");
        TextBlock? cantRun = null;
        if (line.CantRun is { } why)
        {
            cantRun = Note(why, new Thickness(0));
            cantRun.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            AutomationProperties.SetAutomationId(cantRun, $"ThinkingPoolCantRun-{i}");
        }
        // One graphics card for each Thinking model: a pool model on the card of this computer's Thinking shares it.
        TextBlock? shared = null;
        if (line.SharedCard is { } text)
        {
            shared = HelpTip.Explain(text + " " + DeepThinkingFit.SharedCardMore, new Thickness(0),
                "SharedCard-" + line.HostId + line.Suffix, "sharing a graphics card", wiki: "Thinking#add-a-machine");
            shared.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            AutomationProperties.SetName(shared, $"{line.HostId}: {text}");
            AutomationProperties.SetAutomationId(shared, "DeepThinkingShare-" + line.HostId + line.Suffix);
        }
        var row = ListRow(onBox, choice, remove, badgeLine, cantRun, shared);
        row.Opacity = on ? 1 : 0.8;
        return row;
    }

    /// <summary>A machine's settings under Advanced › Each machine: what it reads and writes, its slots and whether it can run
    /// now (<c>ThinkingPoolMember-&lt;n&gt;</c>); its rules (Quick jobs, Long jobs, Backup for slow replies, and pictures for a
    /// machine outside your computers), its smarts and its slots. A paired computer's slots are that computer's role settings:
    /// choosing another number sets them there, and each check of that computer takes the role's slot count again.</summary>
    private StackPanel MemberSettings(ThinkingPoolPageModel model, ThinkingPoolPageModel.MemberLine line)
    {
        var pool = model.Pool;
        var (member, i, key, name) = (line.Member, line.Index, line.Key, line.Name);
        var (hostId, card, host, check, paid, slots) = (line.HostId, line.Card, line.Host, line.Check, line.Paid, line.Slots);
        var text = new StackPanel { Margin = new Thickness(0, 0, 0, 12), Opacity = line.On ? 1 : 0.8 };
        text.Children.Add(new TextBlock { Text = name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var detail = Note(line.Detail, new Thickness(0, 2, 0, 0));
        AutomationProperties.SetName(detail, $"{name}: {line.Detail}");
        AutomationProperties.SetAutomationId(detail, $"ThinkingPoolMember-{i}");
        text.Children.Add(detail);

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
            "The judges (are you done talking, did you interrupt) and the screen and sound summaries, which need a fast answer.",
            take => model.SavePoolAsync(p => p.WithJobs(key, quick: take), take ? $"{name} takes quick jobs again."
                : $"{name} no longer takes quick jobs: the judges and the summaries go to other machines.").Forget()));
        rules.Children.Add(Rule("ThinkingPoolLong", "Long jobs", pool.TakesLongJobs(key),
            "Thinking longer, research, a song's lyrics, touch zones, remembering, naming and check-ins, which can run for minutes.",
            take => model.SavePoolAsync(p => p.WithJobs(key, @long: take), take ? $"{name} takes long jobs again."
                : $"{name} no longer takes long jobs: thinking longer, research and the other long jobs go to other machines.").Forget()));
        rules.Children.Add(Rule("ThinkingPoolAnswers", "Backup for slow replies" + (paid ? " (each request may cost money)" : ""), pool.Answers(key),
            "With Help when a reply is slow on, a reply that is slow to start also asks this machine, and the first to start gives the reply.",
            allow => model.SavePoolAsync(p => p.WithAnswers(key, allow), allow
                ? $"{name} may answer for the conversation when a reply is slow to start."
                : $"{name} no longer answers for the conversation.").Forget()));
        // Only an external member asks first: this PC and paired computers always may receive pictures and recordings.
        if (ThinkingPoolSettings.IsExternal(member))
            rules.Children.Add(Rule("ThinkingPoolMedia", "May receive pictures and recordings", pool.MayReceiveMedia(member),
                "This machine is outside this PC and your paired computers, so it gets screenshots and recordings only when this is ticked.",
                allow => model.SavePoolAsync(p => p.WithMedia(key, allow), allow
                    ? $"{name} may now receive pictures and recordings."
                    : $"{name} no longer receives pictures and recordings: jobs with them go to other machines or use their simple rules.").Forget()));
        text.Children.Add(rules);

        // How smart its model is, for Runs on: Martlet's guess from the model name, or the owner's choice.
        var guess = ThinkingSmartsGuess.From(member.ModelId);
        var smarts = new ComboBox
        {
            Width = 190, HorizontalAlignment = HorizontalAlignment.Left,
            ItemsSource = new[] { $"Guessed: {guess}", "Fast", "Standard", "Smart" },
            SelectedIndex = pool.Smarts.TryGetValue(key, out var chosenSmarts) ? 1 + (int)chosenSmarts : 0,
            ToolTip = "How smart this machine's model is, for Runs on; Martlet guesses it from the model name."
        };
        AutomationProperties.SetAutomationId(smarts, $"ThinkingPoolSmarts-{i}");
        AutomationProperties.SetName(smarts, $"{name}: smarts");
        smarts.SelectionChanged += (_, _) =>
        {
            ThinkingSmarts? level = smarts.SelectedIndex <= 0 ? null : (ThinkingSmarts)(smarts.SelectedIndex - 1);
            model.SavePoolAsync(p => p.WithSmarts(key, level), level is { } set ? $"{name} is {set} now."
                : $"{name} uses Martlet's guess again: {guess}.").Forget();
        };
        var settings = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        settings.Children.Add(TerminalRow("Smarts", smarts));

        if (member.OnHostRole && hostId is not null)
        {
            // Its Thinking pool role's slots: the role's settings on that computer (card 2's role for card 2...), set there.
            var role = SelfHostSetup.DeepThinkingRoleKind(card);
            var choices = Enumerable.Range(1, Math.Max(SelfHostSetup.DeepThinkingMaximumSlots, slots)).ToArray();
            var choice = new ComboBox
            {
                Width = 110, ItemsSource = choices.Select(n => $"{n} slot{(n == 1 ? "" : "s")}").ToArray(), SelectedIndex = slots - 1,
                Margin = new Thickness(12, 0, 0, 0),
                IsEnabled = host is not null && check is { Reachable: true } && check.Offers?.ContainsKey(role) == true && services.ChangesRolesOn(host),
                ToolTip = $"How many jobs this graphics card runs at once; another number is set on {hostId} in a run window."
            };
            AutomationProperties.SetAutomationId(choice, $"ThinkingPoolSlots-{i}");
            AutomationProperties.SetName(choice, $"Slots on {name}");
            choice.SelectionChanged += (_, _) =>
            {
                var chosen = choice.SelectedIndex + 1;
                if (chosen < 1 || chosen == slots || host is null) return;
                model.ChangeDeepRoleAsync(host, card, DeepThinkingFit.Tag(member.ModelId ?? ""), chosen).Forget();
            };
            settings.Children.Add(choice);
        }
        else if (hostId is null)
        {
            var choice = new ComboBox { Width = 110, ItemsSource = Enumerable.Range(1, DeepThinkingSettings.MaxPlaces).Select(n => $"{n} slot{(n == 1 ? "" : "s")}").ToArray(),
                SelectedIndex = slots - 1, Margin = new Thickness(12, 0, 0, 0), ToolTip = "How many jobs this machine runs at once." };
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
            settings.Children.Add(choice);
        }
        text.Children.Add(settings);
        return text;
    }

    /// <summary>A machine's model, chosen in its row (<c>ThinkingPoolModel-&lt;n&gt;</c>): for a graphics card on a paired computer,
    /// the models its Thinking pool role offers (choosing one sets it up there, in a run window); for a model in Ollama on this PC,
    /// the models Ollama has and Martlet recommends; for a cloud provider or server, the models the model catalog lists there, or
    /// any model ID typed (saved when you choose one, press Enter or leave the box). Null for a paired computer's member on its
    /// Ollama, whose model is the computer's conversation model.</summary>
    private ComboBox? ModelChoice(ThinkingPoolPageModel model, ThinkingPoolPageModel.MemberLine line)
    {
        var (member, i, name, host, check) = (line.Member, line.Index, line.Name, line.Host, line.Check);
        var key = member.Key;
        var current = member.ModelId ?? "";
        ComboBox Box(IReadOnlyList<string> items, string selected, string tip, bool editable = false)
        {
            var box = new ComboBox { Width = 240, ItemsSource = items, SelectedItem = selected, IsEditable = editable, ToolTip = tip };
            if (editable) box.Text = selected;
            AutomationProperties.SetAutomationId(box, $"ThinkingPoolModel-{i}");
            AutomationProperties.SetName(box, $"{name}: model");
            return box;
        }
        if (member is { OnHostRole: true, HostId: { } hostId })
        {
            var card = member.Card;
            var role = SelfHostSetup.DeepThinkingRoleKind(card);
            var now = DeepThinkingFit.Tag(current);
            var choices = DeepThinkingFit.Models.Contains(now) ? DeepThinkingFit.Models : [now, .. DeepThinkingFit.Models];
            var box = Box(choices, now, $"The model this graphics card runs; another one is set up on {hostId} in a run window, and the old one works until then.");
            box.IsEnabled = host is not null && check is { Reachable: true } && check.Offers?.ContainsKey(role) == true && services.ChangesRolesOn(host);
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedItem is string chosen && chosen != now && host is not null)
                    model.ChangeDeepRoleAsync(host, card, chosen, null, member.ThinksAtOnce).Forget();
            };
            return box;
        }
        if (member.Place != DeepThinkingPlace.Endpoint) return null;
        if (member.Origin == MainWindow.LocalOllamaBaseUrl)
        {
            var beside = model.LocalBeside;
            string[] choices = [.. new[] { current }.Concat(services.OllamaModels ?? []).Concat(MainWindow.LocalChatModels.Select(m => m.Id))
                .Where(id => id.Length > 0).Distinct(StringComparer.Ordinal)];
            var box = Box(choices, current, "The model in Ollama on this PC that does this machine's jobs (not the conversation's own model).");
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedItem is not string chosen || chosen == current) return;
                if (beside is not null && OllamaSideBySide.Same(chosen, beside))
                {
                    services.ShowStatus($"{chosen} is Thinking's own model, which can't think something over while it answers you; choose another model.");
                    box.SelectedItem = current;
                    return;
                }
                model.SavePoolAsync(p => p.WithModel(key, chosen), $"{name} now uses {chosen}" +
                    (services.OllamaModels is { } known && !LocalOllama.Serves(known, chosen) ? $"; download {chosen} on Thinking to use it." : ".")).Forget();
            };
            return box;
        }
        var typed = Box(ThinkingPoolPageModel.CloudModels(member.Origin ?? "", current), current,
            "The model at this provider: choose one Martlet knows there, or type its model ID and press Enter.", editable: true);
        InputHint.SetText(typed, current.Length > 0 ? current : ChatCompletionsEndpointCatalog.Named(member.Origin)?.DefaultModelId ?? "gemma4:e4b");
        var saved = false;
        void Commit()
        {
            var chosen = typed.Text.Trim();
            if (saved || chosen.Length == 0 || chosen == current) return;
            saved = true;
            model.SavePoolAsync(p => p.WithModel(key, chosen), $"{name} now uses {chosen}.").Forget();
        }
        typed.SelectionChanged += (_, _) => { if (typed.SelectedItem is string chosen && chosen != current) { typed.Text = chosen; Commit(); } };
        typed.LostKeyboardFocus += (_, e) => { if (!typed.IsKeyboardFocusWithin) Commit(); };
        typed.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Commit(); };
        return typed;
    }
}
