using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using Martlet.Avatar.Hosting;
using Martlet.Core.Settings;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Touch › Touch temperament: how the active persona's character acts (never what it says) when each part of it
/// is touched, in one table with a line per category and per part that reacts differently; which temperament the persona uses (its
/// own, the built-in reactions or a custom one); and Re-decide from personality.</summary>
internal sealed partial class TouchPage
{
    private const string NotDecided = "(not decided: follow your mouse)";
    // The intimate category's choice when the temperament doesn't cover it: each intimate part reacts as its body group's line.
    private const string AsBodyAround = "(as the body)";
    // Who decided the temperament, while the card shows (the window shows the service's news on it).
    private TextBlock? decision;

    /// <summary>Shows the temperament service's news on the card's decision line.</summary>
    internal void ShowDecision()
    {
        if (decision is not null) ShowStatusLine(decision, services.Temperaments.Status);
    }

    /// <summary>Touch temperament: how the active persona's character acts when each part of it is touched.</summary>
    private Border TemperamentCard()
    {
        var persona = services.ActivePersona;
        var stack = new List<UIElement>
        {
            Heading("Touch temperament"),
            HelpTip.Explain("How the character reacts when you touch each part of it, and where its eyes usually go. When you save a " +
                "personality, your Thinking model decides this from it in the background. Your own changes stay until you re-decide. " +
                "Make a custom temperament to use the same one for several personas.", new Thickness(0, 0, 0, 12), "CharacterTemperament", "touch temperament")
        };
        var temperament = services.Temperaments.For(persona?.Id);
        // Who decided it, or what the persona uses instead, with the whole temperament in words as its tooltip (and help text, for
        // UI Automation).
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(status, "TouchTemperamentStatus");
        void ShowStatus()
        {
            if (persona is null)
            {
                status.Text = "No persona is in use.";
                return;
            }
            var shown = services.Temperaments.For(persona.Id);
            status.Inlines.Clear();
            status.Inlines.Add(new Run("For "));
            status.Inlines.Add(new Run(persona.Name) { FontWeight = FontWeights.SemiBold });
            status.Inlines.Add(new Run(": " + TouchPageModel.TemperamentStatus(services.Temperaments.Saved, persona.Id)));
            var summary = CharacterTouchTemperaments.Summary(shown) +
                (shown is null ? "" : $"; repeated touches escalate after {shown.Escalation.After}.");
            status.ToolTip = summary;
            AutomationProperties.SetHelpText(status, summary);
        }
        ShowStatus();
        stack.Add(status);
        decision = Note("", new Thickness(0, 2, 0, 0));
        AutomationProperties.SetAutomationId(decision, "TouchTemperamentDecision");
        AutomationProperties.SetLiveSetting(decision, AutomationLiveSetting.Polite);
        ShowStatusLine(decision, services.Temperaments.Status);
        stack.Add(decision);
        var saveState = Note("", new Thickness(0, 10, 0, 0));
        AutomationProperties.SetAutomationId(saveState, "TouchTemperamentSaveState");
        AutomationProperties.SetLiveSetting(saveState, AutomationLiveSetting.Polite);
        ShowStatusLine(saveState, null);

        var busy = services.Deciding || services.Temperaments.Busy;
        var canDecide = !busy && persona is { Text.Length: > 0 } && (services.CanDetect || CharacterTemperamentService.Fixture);
        var decide = PageButton("Re-decide from personality", services.Decide, id: "TouchTemperamentDecide");
        decide.IsEnabled = canDecide;
        const string decideHelp = "Sends the persona's personality text to your Thinking model, which decides how the character reacts to " +
            "touch (actions only, never words, and never while Martlet replies).";
        AutomationProperties.SetHelpText(decide, decideHelp);
        decide.ToolTip = decideHelp;
        ToolTipService.SetShowOnDisabled(decide, true);
        void ShowActions()
        {
            var own = persona is null ? null : services.Temperaments.Own(persona.Id);
            var label = busy ? "Deciding..." : own is null ? "Decide from personality" : "Re-decide from personality";
            decide.Content = label;
            AutomationProperties.SetName(decide, label);
            // Nothing decided yet, and nothing else chosen: deciding is the next step.
            if (canDecide && own is null && persona is not null && !services.Temperaments.Saved.Uses.ContainsKey(persona.Id))
                decide.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
            else decide.ClearValue(FrameworkElement.StyleProperty);
        }
        ShowActions();
        var actions = Row(decide);
        actions.Margin = new Thickness(0, 10, 0, 0);
        if (persona is null)
        {
            stack.Add(actions);
            stack.Add(saveState);
            return Card([.. stack]);
        }

        // Edits in the table save after a short pause; changing what the persona uses saves them first.
        AutoSave? autoSave = null;
        async Task SavePendingAsync()
        {
            if (autoSave is { Pending: true } waiting) await waiting.SaveNowAsync();
        }
        // What a Uses, Create, Rename or Delete did, or why not.
        var useState = Note("", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(useState, "TouchTemperamentUseState");
        AutomationProperties.SetLiveSetting(useState, AutomationLiveSetting.Polite);
        ShowStatusLine(useState, services.UseNote);
        services.UseNote = null;
        void AfterChange(string? why, string done)
        {
            services.MarkEdited(false);
            if (why is not null)
            {
                ShowStatusLine(useState, "Not saved: " + why);
                useState.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                return;
            }
            services.UseNote = done;
            if (!services.Closing && services.TouchOpen) services.Redraw();
        }
        // A line of the Uses rows: a label in the table's name column, then its controls.
        DockPanel UseLine(string text, Control target, params FrameworkElement[] controls)
        {
            var label = new Label
            {
                Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, Target = target, Padding = new Thickness(0),
                Width = TemperamentTable.Part, Margin = new Thickness(0, 3, TemperamentTable.Gap, 3), VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(label, Dock.Left);
            var panel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            foreach (var control in controls)
            {
                control.Margin = new Thickness(0, 3, TemperamentTable.Gap, 3);
                panel.Children.Add(control);
            }
            var line = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            line.Children.Add(label);
            line.Children.Add(panel);
            return line;
        }

        // What the persona uses: its own temperament, the built-in reactions or one of the custom temperaments.
        var saved = services.Temperaments.Saved;
        var custom = saved.CustomOf(persona.Id);
        var builtIn = saved.UsesBuiltIn(persona.Id);
        var customs = saved.Custom.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var choices = new[] { CharacterTouchTemperaments.OwnLabel, CharacterTouchTemperaments.BuiltInLabel }.Concat(customs.Select(c => c.Name)).ToArray();
        var use = Compact(new ComboBox
        {
            ItemsSource = choices, ItemTemplate = TemperamentRow.OneLine, Width = 300, HorizontalAlignment = HorizontalAlignment.Left,
            SelectedIndex = builtIn ? 1 : custom is null ? 0 : 2 + Array.FindIndex(customs, c => c.Id == custom.Id)
        });
        AutomationProperties.SetName(use, $"The touch temperament {persona.Name} uses");
        AutomationProperties.SetAutomationId(use, "TouchTemperamentUse");
        use.ToolTip = $"{CharacterTouchTemperaments.OwnLabel}: {persona.Name}'s own, from your Thinking model; " +
            $"{CharacterTouchTemperaments.BuiltInLabel}: each part's built-in reaction; or one of your custom temperaments.";
        use.SelectionChanged += async (_, _) =>
        {
            var index = use.SelectedIndex;
            if (index < 0) return;
            await SavePendingAsync();
            var why = await services.Temperaments.ChooseAsync(persona.Id,
                index switch { 0 => null, 1 => CharacterTouchTemperaments.BuiltIn, _ => customs[index - 2].Id.ToString() }, services.Lifetime);
            AfterChange(why, $"{persona.Name} uses \"{choices[index]}\" now.");
        };
        stack.Add(UseLine("Uses", use, use));

        // The custom temperament the persona uses: its name, Rename, Delete and who else uses it.
        if (custom is not null)
        {
            var name = Compact(new TextBox { Text = custom.Name, Width = 240, MaxLength = CharacterTouchTemperaments.MaximumNameLength });
            AutomationProperties.SetName(name, "The custom temperament's name");
            AutomationProperties.SetAutomationId(name, "TouchTemperamentName");
            InputHint.SetText(name, "Shy and playful");
            name.TextChanged += (_, _) => services.MarkEdited(true);
            var rename = Compact(PageButton("Rename", async () =>
            {
                await SavePendingAsync();
                var why = await services.Temperaments.RenameCustomAsync(custom.Id, name.Text, services.Lifetime);
                AfterChange(why, $"Renamed \"{custom.Name}\" to \"{name.Text.Trim()}\".");
            }, id: "TouchTemperamentRename"));
            var users = saved.UsedBy(custom.Id);
            var delete = Compact(PageButton("Delete", async () =>
            {
                autoSave?.Cancel();
                var why = await services.Temperaments.DeleteCustomAsync(custom.Id, services.Lifetime);
                AfterChange(why, $"Deleted the custom temperament \"{custom.Name}\". {TouchPageModel.PersonaNames(services.Personas, users)} " +
                    (users.Count == 1 ? "uses its" : "use their") + " own temperament again.");
            }, id: "TouchTemperamentDelete"));
            delete.ToolTip = "Deletes this custom temperament, and each persona that used it uses its own temperament again.";
            AutomationProperties.SetHelpText(delete, (string)delete.ToolTip);
            stack.Add(UseLine("Custom temperament", name, name, rename, delete));
            var usedBy = Note($"Used by {TouchPageModel.PersonaNames(services.Personas, users)}, so a change below changes it for each of them.", new Thickness(0, 2, 0, 0));
            AutomationProperties.SetAutomationId(usedBy, "TouchTemperamentCustomUsers");
            stack.Add(usedBy);
        }
        stack.Add(actions);

        // A new custom temperament starts as a copy of what the persona uses now, and the persona then uses it.
        var newName = Compact(new TextBox { Width = 240, MaxLength = CharacterTouchTemperaments.MaximumNameLength });
        AutomationProperties.SetName(newName, "Name for a new custom temperament");
        AutomationProperties.SetAutomationId(newName, "TouchTemperamentNewName");
        InputHint.SetText(newName, "Shy and playful");
        newName.TextChanged += (_, _) => services.MarkEdited(true);
        var copied = builtIn ? "the built-in reactions" : custom is not null ? $"\"{custom.Name}\"" : $"{persona.Name}'s own temperament";
        var create = Compact(PageButton("Create", async () =>
        {
            await SavePendingAsync();
            var made = newName.Text.Trim();
            var why = await services.Temperaments.CreateCustomAsync(persona.Id, made, services.Lifetime);
            AfterChange(why, $"Made the custom temperament \"{made}\", a copy of {copied}, and {persona.Name} uses it now.");
        }, id: "TouchTemperamentNew"));
        AutomationProperties.SetName(create, "Create a custom temperament");
        create.ToolTip = $"Makes a custom temperament with this name, a copy of what {persona.Name} uses now, that {persona.Name} then " +
            "uses and other personas can choose too.";
        AutomationProperties.SetHelpText(create, (string)create.ToolTip);
        stack.Add(UseLine("New custom temperament", newName, newName, create));
        stack.Add(useState);
        if (builtIn)
        {
            stack.Add(HelpTip.Explain("Every part plays its own built-in reaction, and the eyes follow your mouse. To change how it reacts, choose " +
                "another temperament under Uses, or make a custom one: it starts with the built-in reactions.", new Thickness(0, 12, 0, 0), "TemperamentParts", "reactions"));
            stack.Add(saveState);
            return Card([.. stack]);
        }
        // One table: where the eyes usually go, then a line per category and per part with its own reaction, in aligned columns.
        var table = new TemperamentTable();

        var rows = new List<TemperamentRow>();
        // The reaction columns' headings show once a line has a feeling of its own; until then those columns are empty.
        var reactionHeads = new List<UIElement>();
        void ShowReactionHeads()
        {
            var any = rows.Any(r => r.On);
            foreach (var head in reactionHeads) head.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }
        var after = Compact(new TextBox
        {
            Text = (temperament?.Escalation.After ?? new TouchEscalation().After).ToString(CultureInfo.CurrentCulture),
            Width = 60, MaxLength = 2, TextAlignment = TextAlignment.Center
        });
        AutomationProperties.SetName(after, "Touches in a row before the reaction escalates");
        AutomationProperties.SetAutomationId(after, "TouchTemperamentAfter");
        InputHint.SetText(after, "3");
        // Where the character's eyes usually go (Where the character looks › As the personality decides uses it).
        var gazeChoices = new[] { NotDecided }.Concat(CharacterGaze.Modes.Select(m => m.Label)).ToArray();
        var gaze = Compact(new ComboBox
        {
            ItemsSource = gazeChoices, ItemTemplate = TemperamentRow.OneLine, Width = 300, HorizontalAlignment = HorizontalAlignment.Left,
            SelectedIndex = temperament?.Gaze is { } decidedGaze ? 1 + CharacterGaze.Modes.ToList().FindIndex(m => m.Mode == decidedGaze) : 0
        });
        AutomationProperties.SetName(gaze, "Where the character's eyes usually go");
        AutomationProperties.SetAutomationId(gaze, "TouchTemperamentGaze");
        autoSave = new AutoSave(async () =>
        {
            if (services.ActivePersonaId != persona.Id) return true;
            var now = services.Temperaments.Saved;
            // What the persona uses changed after this card was drawn: these lines belong to the temperament it used then.
            if (now.UsesBuiltIn(persona.Id) || now.CustomOf(persona.Id)?.Id != custom?.Id) return true;
            var current = now.For(persona.Id);
            var escalation = current?.Escalation ?? new TouchEscalation();
            var eyes = gaze.SelectedIndex > 0 ? CharacterGaze.Modes[gaze.SelectedIndex - 1].Mode : (GazeMode?)null;
            var groups = rows.Where(r => r.IsGroup && !r.Removed).Select(r => (r.Id, Entry: r.Read())).Where(r => r.Entry is not null)
                .ToDictionary(r => r.Id, r => r.Entry!);
            var zones = rows.Where(r => !r.IsGroup && !r.Removed).Select(r => (r.Id, Entry: r.Read())).Where(r => r.Entry is not null)
                .ToDictionary(r => r.Id, r => r.Entry!);
            var escalated = escalation with
            {
                After = int.TryParse(after.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var n)
                    ? Math.Clamp(n, CharacterTouchTemperaments.MinimumAfter, CharacterTouchTemperaments.MaximumAfter) : escalation.After
            };
            // A custom temperament changes for every persona that uses it; otherwise the persona's own becomes the owner's.
            var why = now.CustomOf(persona.Id) is { } editing
                ? await services.Temperaments.SaveCustomAsync(editing with { Gaze = eyes, Groups = groups, Zones = zones, Escalation = escalated }, services.Lifetime)
                : await services.Temperaments.SaveAsync(new CharacterTouchTemperament
                {
                    PersonaId = persona.Id, Source = CharacterTouchTemperament.ByOwner,
                    PersonalityDigest = current?.PersonalityDigest ?? CharacterTouchTemperaments.Digest(persona.Text), DecidedAt = current?.DecidedAt,
                    Gaze = eyes, Groups = groups, Zones = zones, Escalation = escalated
                }, services.Lifetime);
            ShowStatusLine(saveState, why is null ? "All changes saved." : "Not saved: " + why);
            saveState.SetResourceReference(TextBlock.ForegroundProperty, why is null ? "MutedBrush" : "WarningBrush");
            if (why is null)
            {
                services.MarkEdited(false);
                ShowStatus();
                ShowActions();
            }
            return true;
        });
        void Edited()
        {
            services.MarkEdited(true);
            ShowReactionHeads();
            ShowStatusLine(saveState, "Saving...");
            autoSave?.Changed();
        }        after.TextChanged += (_, _) => Edited();
        gaze.SelectionChanged += (_, _) => Edited();

        gaze.Margin = new Thickness(0, 3, 0, 3);
        table.Line(new Label { Content = "Eyes usually", Target = gaze, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center }, gaze);
        string[] heads = ["Part", "Feels", "Plays", "Then", "Lingers (s)", "Looks at mouse (s)"];
        string?[] headHelp =
        [
            null, "How the character feels about a touch there: hates, dislikes, neutral, likes, loves or craves. (built-in) leaves a " +
                "category's parts to their built-in reactions; (as the body) lets intimate parts react as the category of the body around them.",
            "What plays first. (default) plays what the feeling usually plays; (nothing) plays nothing at all.",
            "A second reaction, after the first.",
            $"How many seconds the first reaction stays on (0: it plays once), up to {CharacterTouchTemperaments.MaximumLinger:0}.",
            $"How many seconds the eyes then look at your mouse pointer, as if to see who did it (0: they don't), up to {CharacterTouchTemperaments.MaximumLook:0}."
        ];
        var headCells = heads.Select((text, column) =>
        {
            var head = new TextBlock
            {
                Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Bottom, ToolTip = headHelp[column]
            };
            head.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            if (column >= 2) reactionHeads.Add(head);
            return (FrameworkElement)head;
        }).ToArray();
        table.Add(headCells[0], headCells[1..]).Margin = new Thickness(0, 14, 0, 1);
        var rule = new Border { Height = 1, Opacity = 0.8, Margin = new Thickness(0, 0, 0, 3) };
        rule.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
        table.View.Children.Add(rule);

        // A line per category, each with the parts it covers under it: every zone kind is in exactly one.
        foreach (var (_, id, label) in CharacterTouchTemperaments.GroupIds)
        {
            var intimate = id == CharacterTouchTemperaments.IntimateId;
            rows.Add(new TemperamentRow(table, id, label, true, temperament?.Groups.GetValueOrDefault(id), Edited, intimate ? AsBodyAround : null,
                intimate ? $"{AsBodyAround} lets each intimate part react as the category of the body around it: the lips and ears as Head " +
                    "and face; the neck, chest, breasts and waist as Shoulders and torso; the hips, groin, buttocks and inner thighs as Legs " +
                    "and feet." : null));
            var parts = Note("Parts: " + string.Join(", ", CharacterTouchTemperaments.KindsIn(id).Select(k => k.Label.ToLowerInvariant())) + ".",
                new Thickness(0, 0, 0, 6));
            parts.FontSize = 12;
            AutomationProperties.SetAutomationId(parts, $"TouchTemperamentParts-{id}");
            table.View.Children.Add(parts);
        }
        table.View.Children.Add(new TextBlock
        {
            Text = "Parts that react differently from their category", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 16, 0, 2)
        });
        foreach (var (id, entry) in (temperament?.Zones ?? new Dictionary<string, TouchTemperamentEntry>()).OrderBy(z => z.Key, StringComparer.Ordinal))
            rows.Add(new TemperamentRow(table, id, CharacterTouchZones.Kind(id)?.Label ?? id, false, entry, Edited));
        ShowReactionHeads();
        stack.Add(table.View);

        var missing = CharacterTouchZones.Kinds.Where(k => temperament?.Zones.ContainsKey(k.Id) != true).ToArray();
        if (missing.Length > 0)
        {
            var kinds = Compact(new ComboBox { ItemsSource = missing.Select(k => k.Label).ToArray(), SelectedIndex = 0, MinWidth = 180 });
            AutomationProperties.SetName(kinds, "Part to give its own reaction");
            AutomationProperties.SetAutomationId(kinds, "TouchTemperamentAddKind");
            var add = Compact(PageButton("Add part", () =>
            {
                var kind = missing[Math.Max(0, kinds.SelectedIndex)];
                if (rows.Any(r => r.Id == kind.Id && !r.Removed)) return;
                rows.Add(new TemperamentRow(table, kind.Id, kind.Label, false,
                    CharacterTouchTemperaments.Entry(services.Temperaments.For(persona.Id), kind.Id) ?? new TouchTemperamentEntry(), Edited));
                Edited();
            }, id: "TouchTemperamentAdd"));
            AutomationProperties.SetHelpText(add, "Gives the chosen part its own line, which wins over its category.");
            var addPanel = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            addPanel.Children.Add(kinds);
            add.Margin = new Thickness(8, 0, 0, 0);
            addPanel.Children.Add(add);
            stack.Add(addPanel);
        }

        var escalationRow = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        escalationRow.Children.Add(new Label
        {
            Content = "Repeated touches escalate after", Target = after, Padding = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
        });
        escalationRow.Children.Add(after);
        escalationRow.Children.Add(new TextBlock { Text = "touches in a row.", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        stack.Add(escalationRow);
        var steps = temperament?.Escalation ?? new TouchEscalation();
        stack.Add(Note($"Touches are in a row when each comes within {CharacterTouchTemperaments.RepeatWindowSeconds:0} seconds of the one " +
            $"before; a disliked part then plays {TemperamentRow.Words(steps.Disliked)} first, and a loved part {TemperamentRow.Words(steps.Loved)}.",
            new Thickness(0, 4, 0, 0)));
        stack.Add(HelpTip.Explain("Under Touch zones each zone plays its own reaction list. A new zone's list starts from these reactions " +
            "(Defaults there fills it again); how the persona feels, the linger, the look and the escalation apply to every zone. " +
            "Intimate parts react only with Include intimate zones on.",
            new Thickness(0, 10, 0, 0), "TouchTemperamentZones", "the temperament and touch zones", wiki: "The-Character#touch-temperament"));
        stack.Add(saveState);
        var card = Card([.. stack]);
        card.Unloaded += (_, _) => { if (autoSave is { Pending: true } waiting) waiting.SaveNowAsync().Forget(); };
        return card;
    }
}
