using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Emotes and motions, its main card: a row for each emote, motion and gesture (its switch, tag, voice cue,
/// Stays on, When to use and Try), with Name them with Thinking, Use the model's own names and Clear emotes, Make a motion
/// (EmotesPage.Motions.cs) and the combos after the rows (EmotesPage.Combos.cs). Edits save as you type, with the card's own
/// autosave.</summary>
internal sealed partial class EmotesPage
{
    private Border CharacterActionsCard()
    {
        services.FollowActions();
        var stack = new List<UIElement>
        {
            Heading("Emotes and motions"),
            HelpTip.Explain("Martlet's replies can make the character show its emotes and play its motions, and nod or shake its head. Link one " +
                "to a voice cue and it plays whenever the voice makes that sound or tone (Chatterbox Turbo's [laugh], Dia's (laughs)); " +
                "the others are offered to the Thinking model as tags such as {blush}, and it is asked to use them freely and " +
                "vary them. Each tag's When to use is the hint the Thinking model reads with it; leave it empty for " +
                "Martlet's own hint, shown in grey. Tags are always English (a-z), so every " +
                "Thinking model can write them; names stay as the model's creator wrote them, in any language. A tag written " +
                "another way still plays ([blush] or *blushes* for {blush}), and the talk window notes under each reply what it " +
                "set off. Changes save as you type, for this model.",
                new Thickness(0, 0, 0, 8), "CharacterActions", "emotes and motions")
        };
        var characterActions = services.Actions;
        var catalog = characterActions.Current;
        RenderedModel = catalog?.Inventory.ModelId;
        var status = Note(EmotesPageModel.Status(characterActions.Problem, catalog), new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(status, "CharacterActionsStatus");
        stack.Add(status);
        if (characterActions.Naming is { } naming)
        {
            var named = Note(naming, new Thickness(0, 0, 0, 4));
            AutomationProperties.SetAutomationId(named, "CharacterActionsNaming");
            AutomationProperties.SetLiveSetting(named, AutomationLiveSetting.Polite);
            stack.Add(named);
        }
        if (catalog is null) return Card([.. stack]);

        var offered = Note(EmotesPageModel.Offered(catalog, services.SpeakingEngine), new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(offered, "CharacterActionsOffered");
        stack.Add(offered);
        characterActionsLast = Note(services.LastAction ?? (services.AvatarShowing ? "Nothing played yet." : "Show the character to try them."),
            new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(characterActionsLast, "CharacterActionsLast");
        AutomationProperties.SetLiveSetting(characterActionsLast, AutomationLiveSetting.Polite);
        stack.Add(characterActionsLast);
        characterActionsHeld = Note(HeldText(), new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(characterActionsHeld, "CharacterActionsHeld");
        AutomationProperties.SetLiveSetting(characterActionsHeld, AutomationLiveSetting.Polite);
        stack.Add(characterActionsHeld);
        var saveState = Note("", new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(saveState, "CharacterActionsSaveState");
        AutomationProperties.SetLiveSetting(saveState, AutomationLiveSetting.Polite);
        stack.Add(saveState);

        var name = PageButton(characterActions.Busy ? "Naming..." : "Name them with Thinking", services.NameActions,
            id: "CharacterActionsDetect");
        name.IsEnabled = services.CanName && !characterActions.Busy && CharacterActions.Nameable(catalog.Inventory).Count > 0;
        AutomationProperties.SetHelpText(name, "Sends the model's emote and motion names and what they change (never its files) to your Thinking model.");
        var reset = PageButton("Use the model's own names", () => ResetCharacterActionsAsync().Forget(), id: "CharacterActionsReset");
        var clear = PageButton("Clear emotes", () => services.ClearEmotesAsync().Forget(), id: "CharacterActionsClear");
        AutomationProperties.SetHelpText(clear, "Turns off every lingering emote the character shows now.");
        stack.Add(Row(name, reset, clear));
        // Make a motion, for a VRM while motions are set up (EmotesPage.Motions.cs).
        AddMakeMotion(stack, catalog);

        var showing = services.AvatarShowing && services.ShownCatalog is not null;
        var rows = new List<(CharacterActionSource Source, CheckBox On, TextBox Tag, ComboBox Cue, TextBox Use, CheckBox Stays)>();
        var comboRows = new List<ComboRow>();
        characterActionTries.Clear();
        // The emotes as their rows show them now: what is saved, and what the combos' parts name.
        IReadOnlyList<CharacterAction> CurrentActions() => [.. rows.Select(r => new CharacterAction
        {
            Id = r.Source.Id, Enabled = r.On.IsChecked == true,
            Tag = r.Tag.Text.Trim().Trim('{', '}').Trim().ToLowerInvariant() is { Length: > 0 } tag ? tag : null,
            Use = r.Use.Text.Trim() is { Length: > 0 } use ? use : null,
            Cue = r.Cue.SelectedItem as string is { } cue && cue != EmotesPageModel.NoCue ? cue : null,
            Mode = r.Stays.IsChecked == true ? CharacterActions.Lingering : CharacterActions.Brief
        })];
        var autoSave = new AutoSave(async () =>
        {
            var current = characterActions.Current;
            if (current is null || !ReferenceEquals(current.Inventory, catalog.Inventory)) return true;
            var actions = CurrentActions();
            var combos = ReadCombos(comboRows, actions, out var comboProblem);
            var why = comboProblem ?? await characterActions.SaveAsync(current.Settings with { Actions = actions, Combos = combos }, services.Lifetime);
            saveState.Text = why is null ? "All changes saved." : "Not saved: " + why;
            saveState.SetResourceReference(TextBlock.ForegroundProperty, why is null ? "MutedBrush" : "WarningBrush");
            if (why is null)
            {
                offered.Text = EmotesPageModel.Offered(characterActions.Current ?? catalog, services.SpeakingEngine);
                if (characterCombosStatus is not null) characterCombosStatus.Text = EmotesPageModel.CombosStatus(characterActions.Current ?? catalog);
            }
            return true;
        });
        services.UseAutoSave(autoSave);
        void Edited()
        {
            services.MarkEdited();
            saveState.Text = "Saving...";
            autoSave.Changed();
            // A combo's line follows its parts' rows (turned off, stays on, a new tag).
            foreach (var combo in comboRows) combo.Refresh();
        }
        var cues = new[] { EmotesPageModel.NoCue }.Concat(VoiceTags.Cues).ToArray();
        // The rows, then the combos; on a page just opened they join a batch at a time.
        var list = new StackPanel();
        stack.Add(list);
        var index = 0;
        foreach (var (source, action) in catalog.Entries)
        {
            var n = index++;
            var kind = EmotesPageModel.Kind(source);
            var on = RowSwitch(action.Enabled);
            AutomationProperties.SetName(on, $"Use {source.Name}");
            AutomationProperties.SetAutomationId(on, $"CharacterActionOn-{n}");
            var title = new TextBlock
            {
                Text = $"{source.Name}  \u00b7  {kind}", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetAutomationId(title, $"CharacterActionName-{n}");
            var detail = new TextBlock { Text = source.Detail, FontSize = 13, Margin = new Thickness(RowIndent, 1, 8, 0) };
            detail.SetResourceReference(FrameworkElement.StyleProperty, "Muted");
            var tag = Compact(new TextBox { Text = action.Tag ?? "", Width = 120, MaxLength = CharacterActionCatalog.MaximumTagLength });
            AutomationProperties.SetName(tag, $"Tag for {source.Name}");
            AutomationProperties.SetAutomationId(tag, $"CharacterActionTag-{n}");
            InputHint.SetText(tag, CharacterActions.EnglishTag(source, CharacterActions.Number(catalog.Inventory, source)));
            var cue = Compact(new ComboBox { ItemsSource = cues, SelectedItem = action.Cue ?? EmotesPageModel.NoCue, Width = 120 });
            AutomationProperties.SetName(cue, $"Voice cue for {source.Name}");
            AutomationProperties.SetAutomationId(cue, $"CharacterActionCue-{n}");
            var use = Compact(new TextBox { Text = action.Use ?? "", MinWidth = 150, MaxLength = CharacterActionCatalog.MaximumUseLength });
            AutomationProperties.SetName(use, $"When to use {source.Name}");
            AutomationProperties.SetAutomationId(use, $"CharacterActionUse-{n}");
            // Replies get Martlet's own hint while the box is empty; it shows in grey until the owner writes one.
            var builtIn = CharacterActions.Describe(source);
            var help = $"What the Thinking model reads next to this tag, so it knows when to use it. Empty: \"{builtIn}\".";
            AutomationProperties.SetHelpText(use, help);
            use.ToolTip = help;
            var useBox = WithHint(use, builtIn, $"CharacterActionHint-{n}");
            var stays = new CheckBox
            {
                Content = "Stays on", IsChecked = CharacterActions.Lingers(source, action), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 18, 6)
            };
            AutomationProperties.SetName(stays, $"{source.Name} stays on until turned off");
            AutomationProperties.SetAutomationId(stays, $"CharacterActionMode-{n}");
            AutomationProperties.SetHelpText(stays, "On: a reply's tag turns it on and it stays until the reply writes the tag with a slash, " +
                "such as {/glasses}. Off: it shows for a moment.");
            var tryIt = Compact(PageButton(services.Held.Holds(source.Id) ? "Turn off" : "Try", () => TryCharacterActionAsync(source).Forget(),
                id: $"CharacterActionTry-{n}"));
            tryIt.IsEnabled = showing;
            characterActionTries.Add((source.Id, tryIt));
            on.Checked += (_, _) => Edited();
            on.Unchecked += (_, _) => Edited();
            stays.Checked += (_, _) => Edited();
            stays.Unchecked += (_, _) => Edited();
            tag.TextChanged += (_, _) => Edited();
            use.TextChanged += (_, _) => Edited();
            cue.SelectionChanged += (_, _) => Edited();
            rows.Add((source, on, tag, cue, use, stays));

            // Its name with what it changes under it, then its fields: When to use takes the rest of the line, or a line of its own.
            var named = new DockPanel();
            named.Children.Add(on);
            named.Children.Add(title);
            var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            heading.Children.Add(named);
            if (source.Detail.Length > 0) heading.Children.Add(detail);
            var header = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
            DockPanel.SetDock(tryIt, Dock.Right);
            tryIt.Margin = new Thickness(8, 0, 0, 0);
            header.Children.Add(tryIt);
            header.Children.Add(heading);
            var fields = new FillWrapPanel { Margin = new Thickness(RowIndent, 6, 0, 0), FillMinimum = 300 };
            fields.Children.Add(RowGroup(RowLabel("Tag  {", tag, 0, 4), tag, RowLabel("}", tag, 4, 0)));
            fields.Children.Add(RowGroup(RowLabel("Voice cue", cue), cue));
            fields.Children.Add(stays);
            var when = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            when.Children.Add(RowLabel("When to use", use));
            when.Children.Add(useBox);
            fields.Children.Add(when);
            var rowView = new StackPanel();
            rowView.Children.Add(header);
            rowView.Children.Add(fields);
            addRow(list, rowView);
        }
        // The owner's combos of these emotes, after them (EmotesPage.Combos.cs).
        addRow(list, CharacterCombosSection(catalog, comboRows, CurrentActions, Edited, showing));
        return Card([.. stack]);
    }
}
