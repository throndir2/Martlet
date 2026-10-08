using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Martlet.Avatar.Hosting;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Companion › Character › Touch temperament: how the active persona's character acts (never what it says) when each
/// part of it is touched: how much it likes it (hates to craves) and which emotes, gestures and face symbols play, in one table
/// with a line per category (each names the parts it covers; intimate parts have their own) and per part that reacts
/// differently. The Thinking model decides the persona's own temperament from the personality in the background after the
/// personality is saved (Re-decide from personality asks again); the owner's edits here win and stay until they re-decide. The
/// owner can make named custom temperaments and choose, per persona, its own, the built-in reactions or a custom one; editing a
/// custom one changes it for each persona that uses it. A zone's own pick under Touch zones still wins over the temperament.</summary>
public partial class MainWindow
{
    private const string NotDecided = "(not decided: follow your mouse)";
    // The intimate category's choice when the temperament doesn't cover it: each intimate part reacts as its body group's line.
    private const string AsBodyAround = "(as the body)";
    private readonly CharacterTemperamentService characterTemperaments;
    private TextBlock? temperamentDecision;
    private bool decidingTemperament;
    // What the last Uses, Create, Rename or Delete did (or why not), for the next card's line under them.
    private string? temperamentUseNote;

    private void WireCharacterTemperament()
    {
        characterTemperaments.Changed += () => Dispatcher.InvokeAsync(() =>
        {
            if (temperamentDecision is not null) ShowStatusLine(temperamentDecision, characterTemperaments.Status);
            if (closing || openTab != CompanionTab.Character || CompanionContent.IsKeyboardFocusWithin || tabEdited) return;
            if (!characterTemperaments.Busy && !decidingTemperament) RenderTab();
        });
        // A decided (or edited, or shared) temperament may change the character's usual gaze.
        characterTemperaments.Changed += avatar.Gaze.Refresh;
        WireCharacterGaze();
    }

    private Task<(string? Answer, string? Failure)> AskThinkingForTemperamentAsync(string purpose, string instructions, string text, CancellationToken token) =>
        conversation is { } live ? live.AskHelperAsync(HelperJobKind.Temperament, purpose, instructions, text, null, token)
            : Task.FromResult<(string?, string?)>((null, "Thinking isn't set up yet"));

    /// <summary>After Personality closes: each persona whose personality changed meaningfully (or is new) has its touch
    /// temperament decided again in the background, once Martlet isn't replying.</summary>
    private void PersonalitiesSaved(IReadOnlyDictionary<Guid, string>? before)
    {
        if (closing || homeSettings?.Companion is not { } companion) return;
        foreach (var persona in companion.Personas)
        {
            if (before is not null && before.TryGetValue(persona.Id, out var old) &&
                CharacterTouchTemperaments.Digest(old) == CharacterTouchTemperaments.Digest(persona.Text)) continue;
            characterTemperaments.PersonalitySaved(persona, () => conversation?.Replying == true || openConversation?.HearingYou == true,
                AskThinkingForTemperamentAsync, lifetime.Token);
        }
    }

    private async Task DecideTemperamentAsync()
    {
        if (decidingTemperament || homeSettings?.Companion?.ActivePersona is not { } persona) return;
        decidingTemperament = true;
        try { await characterTemperaments.DecideAsync(persona, AskThinkingForTemperamentAsync, lifetime.Token, use: true); }
        finally
        {
            decidingTemperament = false;
            tabEdited = false;
            if (!closing && openTab == CompanionTab.Character) RenderTab();
        }
    }

    /// <summary>Who decided the persona's own temperament, or what it uses instead (after "For Mira: ").</summary>
    private static string TemperamentStatusText(TouchTemperamentSet saved, Guid personaId)
    {
        if (saved.UsesBuiltIn(personaId)) return "built-in reactions, as you chose.";
        if (saved.CustomOf(personaId) is { } custom) return $"your custom temperament \"{custom.Name}\".";
        var own = saved.Own(personaId);
        return own?.Source switch
        {
            CharacterTouchTemperament.ByOwner => "your own choices.",
            CharacterTouchTemperament.ByFixture => "FIXTURE - NOT AI: read from " + CharacterTemperamentService.FixtureVariable + ".",
            CharacterTouchTemperament.ByThinking => "decided by the Thinking model from the personality" +
                (own.DecidedAt is { } at ? $" on {at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}." : "."),
            _ => "built-in reactions (not decided yet)."
        };
    }

    /// <summary>The personas' names in Personality's order ("Mira and Aki"); a persona that isn't on this PC counts as another
    /// persona.</summary>
    private string PersonaNames(IReadOnlyList<Guid> ids)
    {
        var personas = homeSettings?.Companion?.Personas ?? [];
        var names = personas.Where(p => ids.Contains(p.Id)).Select(p => p.Name).ToList();
        var others = ids.Count(id => personas.All(p => p.Id != id));
        if (others > 0) names.Add(others == 1 ? "another persona" : $"{others} other personas");
        return names.Count switch { 0 => "no persona", 1 => names[0], _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1] };
    }

    private Border CharacterTemperamentCard()
    {
        var persona = homeSettings?.Companion?.ActivePersona;
        var stack = new List<UIElement>
        {
            Heading("Touch temperament"),
            Note("How the character reacts when you touch each part of it, and where its eyes usually go. When you save a " +
                "personality, your Thinking model decides this from it in the background. Your own changes stay until you re-decide. " +
                "Make a custom temperament to use the same one for several personas.", new Thickness(0, 0, 0, 12))
        };
        var temperament = characterTemperaments.For(persona?.Id);
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
            var shown = characterTemperaments.For(persona.Id);
            status.Inlines.Clear();
            status.Inlines.Add(new Run("For "));
            status.Inlines.Add(new Run(persona.Name) { FontWeight = FontWeights.SemiBold });
            status.Inlines.Add(new Run(": " + TemperamentStatusText(characterTemperaments.Saved, persona.Id)));
            var summary = CharacterTouchTemperaments.Summary(shown) +
                (shown is null ? "" : $"; repeated touches escalate after {shown.Escalation.After}.");
            status.ToolTip = summary;
            AutomationProperties.SetHelpText(status, summary);
        }
        ShowStatus();
        stack.Add(status);
        temperamentDecision = Note("", new Thickness(0, 2, 0, 0));
        AutomationProperties.SetAutomationId(temperamentDecision, "TouchTemperamentDecision");
        AutomationProperties.SetLiveSetting(temperamentDecision, AutomationLiveSetting.Polite);
        ShowStatusLine(temperamentDecision, characterTemperaments.Status);
        stack.Add(temperamentDecision);
        var saveState = Note("", new Thickness(0, 10, 0, 0));
        AutomationProperties.SetAutomationId(saveState, "TouchTemperamentSaveState");
        AutomationProperties.SetLiveSetting(saveState, AutomationLiveSetting.Polite);
        ShowStatusLine(saveState, null);

        var busy = decidingTemperament || characterTemperaments.Busy;
        var canDecide = !busy && persona is { Text.Length: > 0 } && (conversation is not null || CharacterTemperamentService.Fixture);
        var decide = PageButton("Re-decide from personality", () => DecideTemperamentAsync().Forget(), id: "TouchTemperamentDecide");
        decide.IsEnabled = canDecide;
        const string decideHelp = "Sends the persona's personality text to your Thinking model, which decides how the character reacts to " +
            "touch: actions only, never words. It never asks while Martlet replies. The persona then uses what it decides.";
        AutomationProperties.SetHelpText(decide, decideHelp);
        decide.ToolTip = decideHelp;
        ToolTipService.SetShowOnDisabled(decide, true);
        void ShowActions()
        {
            var own = persona is null ? null : characterTemperaments.Own(persona.Id);
            var label = busy ? "Deciding..." : own is null ? "Decide from personality" : "Re-decide from personality";
            decide.Content = label;
            AutomationProperties.SetName(decide, label);
            // Nothing decided yet, and nothing else chosen: deciding is the next step.
            if (canDecide && own is null && persona is not null && !characterTemperaments.Saved.Uses.ContainsKey(persona.Id))
                decide.SetResourceReference(StyleProperty, "PrimaryButton");
            else decide.ClearValue(StyleProperty);
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
        ShowStatusLine(useState, temperamentUseNote);
        temperamentUseNote = null;
        void AfterChange(string? why, string done)
        {
            tabEdited = false;
            if (why is not null)
            {
                ShowStatusLine(useState, "Not saved: " + why);
                useState.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                return;
            }
            temperamentUseNote = done;
            if (!closing && openTab == CompanionTab.Character) RenderTab();
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
        var saved = characterTemperaments.Saved;
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
        use.ToolTip = $"{CharacterTouchTemperaments.OwnLabel}: {persona.Name}'s own, which your Thinking model decides. " +
            $"{CharacterTouchTemperaments.BuiltInLabel}: each part's built-in reaction. Or one of your custom temperaments.";
        use.SelectionChanged += async (_, _) =>
        {
            var index = use.SelectedIndex;
            if (index < 0) return;
            await SavePendingAsync();
            var why = await characterTemperaments.ChooseAsync(persona.Id,
                index switch { 0 => null, 1 => CharacterTouchTemperaments.BuiltIn, _ => customs[index - 2].Id.ToString() }, lifetime.Token);
            AfterChange(why, $"{persona.Name} uses \"{choices[index]}\" now.");
        };
        stack.Add(UseLine("Uses", use, use));

        // The custom temperament the persona uses: its name, Rename, Delete and who else uses it.
        if (custom is not null)
        {
            var name = Compact(new TextBox { Text = custom.Name, Width = 240, MaxLength = CharacterTouchTemperaments.MaximumNameLength });
            AutomationProperties.SetName(name, "The custom temperament's name");
            AutomationProperties.SetAutomationId(name, "TouchTemperamentName");
            name.TextChanged += (_, _) => tabEdited = true;
            var rename = Compact(PageButton("Rename", async () =>
            {
                await SavePendingAsync();
                var why = await characterTemperaments.RenameCustomAsync(custom.Id, name.Text, lifetime.Token);
                AfterChange(why, $"Renamed \"{custom.Name}\" to \"{name.Text.Trim()}\".");
            }, id: "TouchTemperamentRename"));
            var users = saved.UsedBy(custom.Id);
            var delete = Compact(PageButton("Delete", async () =>
            {
                autoSave?.Cancel();
                var why = await characterTemperaments.DeleteCustomAsync(custom.Id, lifetime.Token);
                AfterChange(why, $"Deleted the custom temperament \"{custom.Name}\". {PersonaNames(users)} " +
                    (users.Count == 1 ? "uses its" : "use their") + " own temperament again.");
            }, id: "TouchTemperamentDelete"));
            delete.ToolTip = "Deletes this custom temperament. Each persona that used it uses its own temperament again.";
            AutomationProperties.SetHelpText(delete, (string)delete.ToolTip);
            stack.Add(UseLine("Custom temperament", name, name, rename, delete));
            var usedBy = Note($"Used by {PersonaNames(users)}. Changing it below changes it for every persona that uses it.", new Thickness(0, 2, 0, 0));
            AutomationProperties.SetAutomationId(usedBy, "TouchTemperamentCustomUsers");
            stack.Add(usedBy);
        }
        stack.Add(actions);

        // A new custom temperament starts as a copy of what the persona uses now, and the persona then uses it.
        var newName = Compact(new TextBox { Width = 240, MaxLength = CharacterTouchTemperaments.MaximumNameLength });
        AutomationProperties.SetName(newName, "Name for a new custom temperament");
        AutomationProperties.SetAutomationId(newName, "TouchTemperamentNewName");
        newName.TextChanged += (_, _) => tabEdited = true;
        var copied = builtIn ? "the built-in reactions" : custom is not null ? $"\"{custom.Name}\"" : $"{persona.Name}'s own temperament";
        var create = Compact(PageButton("Create", async () =>
        {
            await SavePendingAsync();
            var made = newName.Text.Trim();
            var why = await characterTemperaments.CreateCustomAsync(persona.Id, made, lifetime.Token);
            AfterChange(why, $"Made the custom temperament \"{made}\", a copy of {copied}. {persona.Name} uses it now.");
        }, id: "TouchTemperamentNew"));
        AutomationProperties.SetName(create, "Create a custom temperament");
        create.ToolTip = $"Makes a custom temperament with this name: a copy of what {persona.Name} uses now. {persona.Name} then uses it, " +
            "and you can choose it for other personas too.";
        AutomationProperties.SetHelpText(create, (string)create.ToolTip);
        stack.Add(UseLine("New custom temperament", newName, newName, create));
        stack.Add(useState);
        if (builtIn)
        {
            stack.Add(Note("Every part plays its own built-in reaction, and the eyes follow your mouse. To change how it reacts, choose " +
                "another temperament under Uses, or make a custom one: it starts with the built-in reactions.", new Thickness(0, 12, 0, 0)));
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
            if (homeSettings?.Companion?.ActivePersonaId != persona.Id) return true;
            var now = characterTemperaments.Saved;
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
                ? await characterTemperaments.SaveCustomAsync(editing with { Gaze = eyes, Groups = groups, Zones = zones, Escalation = escalated }, lifetime.Token)
                : await characterTemperaments.SaveAsync(new CharacterTouchTemperament
                {
                    PersonaId = persona.Id, Source = CharacterTouchTemperament.ByOwner,
                    PersonalityDigest = current?.PersonalityDigest ?? CharacterTouchTemperaments.Digest(persona.Text), DecidedAt = current?.DecidedAt,
                    Gaze = eyes, Groups = groups, Zones = zones, Escalation = escalated
                }, lifetime.Token);
            ShowStatusLine(saveState, why is null ? "All changes saved." : "Not saved: " + why);
            saveState.SetResourceReference(TextBlock.ForegroundProperty, why is null ? "MutedBrush" : "WarningBrush");
            if (why is null)
            {
                tabEdited = false;
                ShowStatus();
                ShowActions();
            }
            return true;
        });
        void Edited()
        {
            tabEdited = true;
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
                    CharacterTouchTemperaments.Entry(characterTemperaments.For(persona.Id), kind.Id) ?? new TouchTemperamentEntry(), Edited));
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
            $"before. A disliked part then plays {TemperamentRow.Words(steps.Disliked)} first, and a loved part {TemperamentRow.Words(steps.Loved)}.",
            new Thickness(0, 4, 0, 0)));
        stack.Add(Note("A zone's own pick under Touch zones still wins. Intimate parts react only with Include intimate zones on.",
            new Thickness(0, 10, 0, 0)));
        stack.Add(saveState);
        var card = Card([.. stack]);
        card.Unloaded += (_, _) => { if (autoSave is { Pending: true } waiting) waiting.SaveNowAsync().Forget(); };
        return card;
    }

    /// <summary>Shows a status line only while it has something to say.</summary>
    private static void ShowStatusLine(TextBlock line, string? text)
    {
        line.Text = text ?? "";
        line.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The temperament's table: rows of a part's name and five cells (feels, plays, then, lingers, looks) whose columns
    /// share one width. The three choices share what the card's width leaves, each up to a most; on a narrow window each row's
    /// cells wrap under each other, all at the same places, to the right of the names, instead of being cut off.</summary>
    internal sealed class TemperamentTable
    {
        internal const double Part = 130, Gap = 8;
        private const double Number = 60;
        private static readonly (double Weight, double Least, double Most)[] Choices = [(1.1, 96, 170), (1.2, 104, 200), (1.2, 104, 200)];
        private readonly List<FrameworkElement>[] columns = [[], [], [], [], []];
        private readonly double[] widths = [96, 104, 104, Number, Number];
        internal StackPanel View { get; } = new() { Margin = new Thickness(0, 12, 0, 0) };

        internal TemperamentTable() => View.SizeChanged += (_, e) => { if (e.WidthChanged) Fit(e.NewSize.Width); };

        /// <summary>Adds a row: the part's name, then a cell per column.</summary>
        internal DockPanel Add(FrameworkElement part, params FrameworkElement[] cells)
        {
            var rest = new WrapPanel();
            for (var column = 0; column < cells.Length; column++)
            {
                cells[column].Margin = new Thickness(0, 3, Gap, 3);
                cells[column].Width = widths[column];
                columns[column].Add(cells[column]);
                rest.Children.Add(cells[column]);
            }
            return Line(part, rest);
        }

        /// <summary>Adds a row of the part's name and something outside the columns.</summary>
        internal DockPanel Line(FrameworkElement part, FrameworkElement content)
        {
            part.Width = Part;
            part.Margin = new Thickness(0, 3, Gap, 3);
            DockPanel.SetDock(part, Dock.Left);
            var row = new DockPanel();
            row.Children.Add(part);
            row.Children.Add(content);
            View.Children.Add(row);
            return row;
        }

        private void Fit(double width)
        {
            // Two pixels to spare, so rounding never pushes a row's last cell onto a line of its own.
            var left = width - Part - 2 * Number - (columns.Length + 1) * Gap - 2;
            var weight = Choices.Sum(c => c.Weight);
            for (var i = 0; i < Choices.Length; i++)
            {
                widths[i] = Math.Clamp(left * Choices[i].Weight / weight, Choices[i].Least, Choices[i].Most);
                foreach (var cell in columns[i]) cell.Width = widths[i];
            }
        }
    }
    /// <summary>One group's or part's line of the temperament table: how the character feels about a touch there, up to two
    /// reactions (or none at all), how long the first lingers and how long the eyes then look at your mouse, and a part's
    /// Remove. Controls that don't apply now are hidden: all but the feeling of a group left to its built-in reactions, a
    /// second reaction after the feeling's default, and the linger after no reaction.</summary>
    private sealed class TemperamentRow
    {
        private const string BuiltIn = "(built-in)", Default = "(default)", Nothing = "(nothing)";
        private readonly ComboBox attitude, first, second;
        private readonly TextBox linger, look;
        private readonly DockPanel view;
        internal string Id { get; }
        internal bool IsGroup { get; }
        internal bool Removed { get; private set; }
        /// <summary>Whether the line has a feeling of its own (not a group left to its built-in reactions, nor removed).</summary>
        internal bool On => !Removed && (!IsGroup || attitude.SelectedIndex > 0);

        /// <summary>A choice shown on one line, cut short with an ellipsis when its box is narrow.</summary>
        internal static readonly DataTemplate OneLine = OneLineTemplate();

        /// <summary>A line; a category's first feeling choice leaves it uncovered, and <paramref name="unset"/> names what that does (the
        /// built-in reactions unless said otherwise), with <paramref name="unsetHelp"/> as its tooltip.</summary>
        internal TemperamentRow(TemperamentTable table, string id, string label, bool isGroup, TouchTemperamentEntry? entry, Action edited,
            string? unset = null, string? unsetHelp = null)
        {
            Id = id;
            IsGroup = isGroup;
            var words = CharacterTouchTemperaments.Vocabulary;
            var shown = words.Select(Display).ToArray();
            var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            FrameworkElement part = name;
            if (!isGroup)
            {
                var glyph = new TextBlock { Text = "\uE711", FontSize = 11 };
                glyph.SetResourceReference(StyleProperty, "Icon");
                var remove = PageButton("Remove", () =>
                {
                    Removed = true;
                    view!.Visibility = Visibility.Collapsed;
                    edited();
                }, id: $"TouchTemperamentRemove-{id}");
                remove.Content = glyph;
                remove.MinWidth = remove.MinHeight = 0;
                remove.Width = remove.Height = 28;
                remove.Padding = new Thickness(0);
                remove.Margin = new Thickness(6, 0, 4, 0);
                remove.VerticalAlignment = VerticalAlignment.Center;
                // Quiet until pointed at: the button's hover ring shows it.
                remove.Background = remove.BorderBrush = Brushes.Transparent;
                remove.SetResourceReference(ForegroundProperty, "MutedBrush");
                remove.ToolTip = $"Remove {label}'s own line, so it reacts like its category";
                AutomationProperties.SetName(remove, $"Remove {label}");
                var dock = new DockPanel();
                DockPanel.SetDock(remove, Dock.Right);
                dock.Children.Add(remove);
                dock.Children.Add(name);
                part = dock;
            }
            attitude = Combo((isGroup ? new[] { unset ?? BuiltIn } : []).Concat(CharacterTouchTemperaments.AttitudeWords).ToArray());
            if (unsetHelp is not null)
            {
                attitude.ToolTip = unsetHelp;
                AutomationProperties.SetHelpText(attitude, unsetHelp);
            }
            var offset = isGroup ? 1 : 0;
            attitude.SelectedIndex = entry is null ? 0 : entry.Attitude - CharacterTouchTemperaments.MinimumAttitude + offset;
            AutomationProperties.SetName(attitude, $"How the character feels about a touch on: {label}");
            AutomationProperties.SetAutomationId(attitude, $"TouchTemperamentAttitude-{id}");
            // First choices: the attitude's own reactions, no reaction at all, then each reaction.
            first = Combo(new[] { Default, Nothing }.Concat(shown).ToArray());
            first.SelectedIndex = entry?.Reactions switch
            {
                { Count: 0 } => 1,
                { Count: > 0 } r when IndexOf(words, r[0]) is var i and >= 0 => i + 2,
                _ => 0
            };
            AutomationProperties.SetName(first, $"First reaction to a touch on: {label}");
            AutomationProperties.SetAutomationId(first, $"TouchTemperamentReaction-{id}");
            second = Combo(new[] { Nothing }.Concat(shown).ToArray());
            second.SelectedIndex = entry?.Reactions is { Count: > 1 } r2 ? Math.Max(0, IndexOf(words, r2[1]) + 1) : 0;
            AutomationProperties.SetName(second, $"Second reaction to a touch on: {label}");
            AutomationProperties.SetAutomationId(second, $"TouchTemperamentReaction2-{id}");
            linger = SecondsBox(entry?.LingerSeconds ?? 0);
            AutomationProperties.SetName(linger, $"Seconds the first reaction to {label} stays on");
            AutomationProperties.SetAutomationId(linger, $"TouchTemperamentLinger-{id}");
            look = SecondsBox(entry?.LookSeconds ?? 0);
            AutomationProperties.SetName(look, $"Seconds the character looks at your mouse after a touch on {label}");
            AutomationProperties.SetAutomationId(look, $"TouchTemperamentLook-{id}");
            void Apply()
            {
                var on = !isGroup || attitude.SelectedIndex > 0;
                // A line without a feeling of its own ends after it; otherwise a gap keeps the cells after it in their columns.
                var off = on ? Visibility.Hidden : Visibility.Collapsed;
                first.Visibility = look.Visibility = on ? Visibility.Visible : off;
                linger.Visibility = on && first.SelectedIndex != 1 ? Visibility.Visible : off;
                second.Visibility = on && first.SelectedIndex > 1 ? Visibility.Visible : off;
                // What (default) plays for the feeling chosen now.
                var feeling = attitude.SelectedIndex - offset + CharacterTouchTemperaments.MinimumAttitude;
                var help = $"(default) plays what {CharacterTouchTemperaments.AttitudeWord(feeling)} usually plays: " +
                    Words(CharacterTouchTemperaments.DefaultReactions(feeling)) + ". (nothing) plays nothing at all.";
                first.ToolTip = help;
                AutomationProperties.SetHelpText(first, help);
            }
            Apply();
            attitude.SelectionChanged += (_, _) => { Apply(); edited(); };
            first.SelectionChanged += (_, _) => { Apply(); edited(); };
            second.SelectionChanged += (_, _) => edited();
            linger.TextChanged += (_, _) => edited();
            look.TextChanged += (_, _) => edited();
            view = table.Add(part, attitude, first, second, linger, look);
        }
        /// <summary>Reactions in words: "anger and look away", "hearts, blush and lean in".</summary>
        internal static string Words(IReadOnlyList<string> words) => words.Count switch
        {
            0 => "nothing",
            1 => Display(words[0]),
            _ => string.Join(", ", words.Take(words.Count - 1).Select(Display)) + " and " + Display(words[^1])
        };

        private static string Display(string word) => word.Replace('_', ' ');

        private static ComboBox Combo(IReadOnlyList<string> items) => Compact(new ComboBox { ItemsSource = items, ItemTemplate = OneLine });

        private static TextBox SecondsBox(double value) => Compact(new TextBox
        {
            Text = value.ToString("0.#", CultureInfo.CurrentCulture), MaxLength = 4, TextAlignment = TextAlignment.Center
        });

        private static DataTemplate OneLineTemplate()
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding());
            text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
            text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            var template = new DataTemplate { VisualTree = text };
            template.Seal();
            return template;
        }

        private static int IndexOf(IReadOnlyList<string> words, string word)
        {
            for (var i = 0; i < words.Count; i++) if (words[i] == word) return i;
            return -1;
        }

        /// <summary>The line as an entry, or null for a group left to its built-in reactions.</summary>
        internal TouchTemperamentEntry? Read()
        {
            var offset = IsGroup ? 1 : 0;
            if (attitude.SelectedIndex < offset) return null;
            var words = CharacterTouchTemperaments.Vocabulary;
            IReadOnlyList<string>? reactions = first.SelectedIndex switch
            {
                <= 0 => null,
                1 => [],
                _ => new[] { words[first.SelectedIndex - 2] }.Concat(second.SelectedIndex > 0 ? [words[second.SelectedIndex - 1]] : Array.Empty<string>())
                    .Distinct(StringComparer.Ordinal).ToArray()
            };
            static double Seconds(TextBox box, double most) =>
                double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var seconds) && double.IsFinite(seconds)
                    ? Math.Clamp(seconds, 0, most) : 0;
            return new()
            {
                Attitude = attitude.SelectedIndex - offset + CharacterTouchTemperaments.MinimumAttitude, Reactions = reactions,
                LingerSeconds = Seconds(linger, CharacterTouchTemperaments.MaximumLinger),
                LookSeconds = Seconds(look, CharacterTouchTemperaments.MaximumLook)
            };
        }
    }
}
