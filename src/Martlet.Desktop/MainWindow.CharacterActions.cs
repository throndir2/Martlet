using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Companion › Character › Emotes and motions: every emote (expression) and motion of the character this PC shows,
/// plus Martlet's own nod and shake, with the tag replies write for each ({blush}), when to use it, the voice cue that also
/// sets it off ([laugh] with Chatterbox Turbo, (laughs) with Dia) and whether it is used. The Thinking model names them once
/// for each new model (Name them with Thinking asks again); edits save as you type, per model, on this PC.</summary>
public partial class MainWindow
{
    private readonly CharacterActionService characterActions;
    private bool loadingCharacterActions, namingCharacterActions;
    private TextBlock? characterActionsLast;

    /// <summary>The reply instructions and tags for the showing character's emotes and motions (null while it is hidden), with
    /// the lingering emotes it shows now for the newest message's notes.</summary>
    private CharacterActionPrompt? CharacterActionPromptFor(SpeechEngine? engine, PromptSettings? prompts)
    {
        if (!avatar.IsShowing) return null;
        var actions = characterActions.For(avatar.InspectedProfile?.ModelPath) is { } catalog
            ? catalog.Prompt(engine, prompts, avatar.Held.Current, DateTimeOffset.Now) : null;
        // Where the character looks, while it may change that: the look tags, and a note while its own choice holds the eyes.
        var gaze = avatar.Gaze.Prompt(prompts);
        return CharacterGaze.Join(actions, gaze, gaze?.Looking);
    }

    /// <summary>The context board of the live conversation: background producers (a screen digest, the sounds this PC plays,
    /// touches on the character) post their newest short note here with <see cref="Martlet.Conversation.ContextBoard.Post"/>, and every reply and
    /// look takes the fresh ones without waiting. The character's lingering emotes are its <see cref="Martlet.Conversation.ContextBoard.Character"/>
    /// note, which the conversation posts as it builds each request.</summary>
    internal readonly Martlet.Conversation.ContextBoard contextBoard = new();

    private void WireCharacterActions()
    {
        avatar.UseActions(characterActions.For);
        avatar.ActionPlayed += () => Dispatcher.InvokeAsync(() =>
        {
            if (characterActionsLast is not null) characterActionsLast.Text = avatar.LastAction ?? "";
            if (characterActionsHeld is not null) characterActionsHeld.Text = HeldText();
            foreach (var (id, button) in characterActionTries)
            {
                var label = avatar.Held.Holds(id) ? "Turn off" : "Try";
                button.Content = label;
                AutomationProperties.SetName(button, label);
            }
            RefreshComboTries();
        });
        characterActions.Changed += () =>
        {
            // An emote the owner turned off or made brief stops lingering.
            avatar.ReconcileHeldAsync(lifetime.Token).Forget();
            Dispatcher.InvokeAsync(() =>
            {
                if (closing || openTab != CompanionTab.Character || CompanionContent.IsKeyboardFocusWithin) return;
                // Another model's emotes replace the rows at once; the same model's re-render waits until nothing is being edited.
                var model = characterActions.Current?.Inventory.ModelId;
                if (!tabEdited || model != renderedActionsModel) RenderTab();
            });
        };
    }

    private TextBlock? characterActionsHeld;
    private readonly List<(string Id, Button Button)> characterActionTries = [];

    /// <summary>The lingering emotes the character shows now, in words (Companion › Character › Emotes and motions).</summary>
    private string HeldText()
    {
        var held = avatar.Held.Current;
        return held.Count == 0 ? "No lingering emotes are on." : "On now: " + string.Join(", ", held.Select(h =>
            $"{h.Source.Name} ({CharacterActions.Age(DateTimeOffset.Now - h.Since)})")) + ". Clear emotes on the character's menu turns them off.";
    }

    /// <summary>Clear emotes (the character's right-click menu or Companion › Character): turns off every lingering emote.</summary>
    private async Task ClearCharacterEmotesAsync()
    {
        try { await avatar.ClearHeldAsync("Clear emotes", lifetime.Token); }
        catch (OperationCanceledException) { }
    }

    private string? renderedActionsModel;

    /// <summary>Keeps the loaded emotes and motions on the model this PC shows (or would show), and names a showing model's
    /// with the Thinking model once (when Thinking is set up). Runs from the character timer and when Companion › Character
    /// renders.</summary>
    private void FollowCharacterActions()
    {
        if (closing || loadingCharacterActions) return;
        var profile = avatar.IsShowing ? avatar.InspectedProfile : homeAvatar;
        var modelPath = profile?.ModelPath ?? BundledLive2D.Prefix + BundledLive2D.DefaultCharacter;
        if (!string.Equals(characterActions.Path, modelPath, StringComparison.OrdinalIgnoreCase))
        {
            loadingCharacterActions = true;
            LoadAsync().Forget();
            return;
        }
        if (avatar.IsShowing && conversation is not null && !namingCharacterActions && characterActions.Current is not null &&
            (homeSettings?.Setup?.Routes.Any(r => r.Role == SetupRole.Llm) == true || conversation.Helpers.PoolHas(HelperCapability.Text)) &&
            characterActions.ClaimAutomaticNaming())
            NameCharacterActionsAsync().Forget();

        async Task LoadAsync()
        {
            try { await characterActions.LoadAsync(profile, force: false, lifetime.Token); }
            catch (OperationCanceledException) { }
            finally { loadingCharacterActions = false; }
        }
    }

    private async Task NameCharacterActionsAsync()
    {
        if (conversation is null || namingCharacterActions) return;
        namingCharacterActions = true;
        try
        {
            var talk = conversation;
            await characterActions.NameAsync((purpose, instructions, text, token) =>
                talk.AskHelperAsync(HelperJobKind.ActionNaming, purpose, instructions, text, null, token), homeSettings?.Prompts, lifetime.Token);
        }
        finally { namingCharacterActions = false; }
    }

    private Border CharacterActionsCard()
    {
        FollowCharacterActions();
        var stack = new List<UIElement>
        {
            Heading("Emotes and motions"),
            Note("Martlet's replies can make the character show its emotes and play its motions, and nod or shake its head. Link one " +
                "to a voice cue and it plays whenever the voice makes that sound or tone (Chatterbox Turbo's [laugh], Dia's (laughs)); " +
                "the others are offered to the Thinking model as tags such as {blush}, and it is asked to use them freely and " +
                "vary them. Each tag's When to use is the hint the Thinking model reads with it; leave it empty for " +
                "Martlet's own hint, shown in grey. Tags are always English (a-z), so every " +
                "Thinking model can write them; names stay as the model's creator wrote them, in any language. A tag written " +
                "another way still plays ([blush] or *blushes* for {blush}), and the talk window notes under each reply what it " +
                "set off. Changes save as you type, for this model.",
                new Thickness(0, 0, 0, 8))
        };
        var catalog = characterActions.Current;
        renderedActionsModel = catalog?.Inventory.ModelId;
        var counts = catalog is null ? "" : Counts(catalog.Inventory);
        var status = Note(characterActions.Problem is { } problem ? "Martlet couldn't read this model's emotes and motions: " + problem
            : catalog is null ? "Reading the character's emotes and motions..."
            : counts + (catalog.Settings.DetectedBy == CharacterActionSettings.ByThinking && catalog.Settings.DetectedAt is { } at
                ? $" Named by the Thinking model on {at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}."
                : " Named from the model's own files."), new Thickness(0, 0, 0, 4));
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

        var offered = Note(Offered(catalog), new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(offered, "CharacterActionsOffered");
        stack.Add(offered);
        characterActionsLast = Note(avatar.LastAction ?? (avatar.IsShowing ? "Nothing played yet." : "Show the character to try them."),
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

        var name = PageButton(characterActions.Busy ? "Naming..." : "Name them with Thinking", () => NameCharacterActionsAsync().Forget(),
            id: "CharacterActionsDetect");
        name.IsEnabled = conversation is not null && !characterActions.Busy && CharacterActions.Nameable(catalog.Inventory).Count > 0;
        AutomationProperties.SetHelpText(name, "Sends the model's emote and motion names and what they change (never its files) to your Thinking model.");
        var reset = PageButton("Use the model's own names", () => ResetCharacterActionsAsync().Forget(), id: "CharacterActionsReset");
        var clear = PageButton("Clear emotes", () => ClearCharacterEmotesAsync().Forget(), id: "CharacterActionsClear");
        AutomationProperties.SetHelpText(clear, "Turns off every lingering emote the character shows now.");
        stack.Add(Row(name, reset, clear));

        var showing = avatar.IsShowing && characterActions.For(avatar.InspectedProfile?.ModelPath) is not null;
        var rows = new List<(CharacterActionSource Source, CheckBox On, TextBox Tag, ComboBox Cue, TextBox Use, CheckBox Stays)>();
        var comboRows = new List<ComboRow>();
        characterActionTries.Clear();
        // The emotes as their rows show them now: what is saved, and what the combos' parts name.
        IReadOnlyList<CharacterAction> CurrentActions() => [.. rows.Select(r => new CharacterAction
        {
            Id = r.Source.Id, Enabled = r.On.IsChecked == true,
            Tag = r.Tag.Text.Trim().Trim('{', '}').Trim().ToLowerInvariant() is { Length: > 0 } tag ? tag : null,
            Use = r.Use.Text.Trim() is { Length: > 0 } use ? use : null,
            Cue = r.Cue.SelectedItem as string is { } cue && cue != NoCue ? cue : null,
            Mode = r.Stays.IsChecked == true ? CharacterActions.Lingering : CharacterActions.Brief
        })];
        var autoSave = new AutoSave(async () =>
        {
            var current = characterActions.Current;
            if (current is null || !ReferenceEquals(current.Inventory, catalog.Inventory)) return true;
            var actions = CurrentActions();
            var combos = ReadCombos(comboRows, actions, out var comboProblem);
            var why = comboProblem ?? await characterActions.SaveAsync(current.Settings with { Actions = actions, Combos = combos }, lifetime.Token);
            saveState.Text = why is null ? "All changes saved." : "Not saved: " + why;
            saveState.SetResourceReference(TextBlock.ForegroundProperty, why is null ? "MutedBrush" : "WarningBrush");
            if (why is null)
            {
                offered.Text = Offered(characterActions.Current ?? catalog);
                if (characterCombosStatus is not null) characterCombosStatus.Text = CombosStatus(characterActions.Current ?? catalog);
            }
            return true;
        });
        tabAutoSave = autoSave;
        void Edited()
        {
            tabEdited = true;
            saveState.Text = "Saving...";
            autoSave.Changed();
            // A combo's line follows its parts' rows (turned off, stays on, a new tag).
            foreach (var combo in comboRows) combo.Refresh();
        }
        var cues = new[] { NoCue }.Concat(VoiceTags.Cues).ToArray();
        var index = 0;
        foreach (var (source, action) in catalog.Entries)
        {
            var n = index++;
            var kind = source.Kind switch
            {
                CharacterActionKind.Expression => "emote", CharacterActionKind.Motion => "motion", _ => "Martlet gesture"
            };
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
            detail.SetResourceReference(StyleProperty, "Muted");
            var tag = Compact(new TextBox { Text = action.Tag ?? "", Width = 120, MaxLength = CharacterActionCatalog.MaximumTagLength });
            AutomationProperties.SetName(tag, $"Tag for {source.Name}");
            AutomationProperties.SetAutomationId(tag, $"CharacterActionTag-{n}");
            var cue = Compact(new ComboBox { ItemsSource = cues, SelectedItem = action.Cue ?? NoCue, Width = 120 });
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
            var tryIt = Compact(PageButton(avatar.Held.Holds(source.Id) ? "Turn off" : "Try", () => TryCharacterActionAsync(source).Forget(),
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
            stack.Add(header);
            stack.Add(fields);
        }
        // The owner's combos of these emotes, after them (MainWindow.CharacterCombos.cs).
        stack.Add(CharacterCombosSection(catalog, comboRows, CurrentActions, Edited, showing));
        return Card([.. stack]);
    }

    private const string NoCue = "(none)";

    private static string Counts(CharacterActionInventory inventory)
    {
        var emotes = inventory.Sources.Count(s => s.Kind == CharacterActionKind.Expression);
        var motions = inventory.Sources.Count(s => s.Kind == CharacterActionKind.Motion);
        return $"{emotes} emote{(emotes == 1 ? "" : "s")}, {motions} motion{(motions == 1 ? "" : "s")} and Martlet's nod and shake.";
    }

    /// <summary>What replies are offered (with the voice chosen now) and what follows the voice's cues, in words.</summary>
    private string Offered(CharacterActionCatalog catalog)
    {
        var engine = conversation?.Configuration?.SpeakingEngine();
        var tags = catalog.Offered(engine).Select(e => "{" + e.Action.Tag + "}").ToArray();
        var linked = catalog.Entries.Where(e => e.Action is { Enabled: true, Cue: not null }).Select(e =>
            $"{(engine?.Tags.FirstOrDefault(t => t.Cue == e.Action.Cue)?.Text ?? e.Action.Cue)} plays {e.Source.Name}").ToArray();
        return (tags.Length == 0 ? "Replies aren't offered any tags." : $"Replies can use {tags.Length}: {string.Join(" ", tags)}.") +
            (linked.Length == 0 ? "" : engine?.SupportsTags == true
                ? $" Following {engine.Name}'s voice: {string.Join("; ", linked)}."
                : $" Linked to voice cues (with a voice that makes sounds and tones): {string.Join("; ", linked)}.");
    }

    /// <summary>Try: plays an emote or motion once, or turns a lingering one on (Turn off turns it off again), as a reply's
    /// {tag} and {/tag} would.</summary>
    private async Task TryCharacterActionAsync(CharacterActionSource source)
    {
        try
        {
            if (avatar.Held.Holds(source.Id)) await avatar.StopActionAsync(source, "a try", lifetime.Token);
            else
                await avatar.PlayActionAsync(source, "a try", null, lifetime.Token,
                    hold: characterActions.For(avatar.InspectedProfile?.ModelPath)?.Lingers(source) == true);
        }
        catch (Exception error) when (error is OperationCanceledException or System.IO.IOException or InvalidOperationException or
            System.IO.InvalidDataException or TimeoutException)
        {
            if (characterActionsLast is not null) characterActionsLast.Text = $"The character couldn't play \"{source.Name}\" right now.";
        }
    }

    private async Task ResetCharacterActionsAsync()
    {
        var why = await characterActions.ResetAsync(lifetime.Token);
        tabEdited = false;
        if (why is not null && characterActionsLast is not null) characterActionsLast.Text = "Couldn't reset: " + why;
        else if (openTab == CompanionTab.Character) RenderTab();
    }
}
