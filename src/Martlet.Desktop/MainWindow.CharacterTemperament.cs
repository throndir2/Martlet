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
/// with a line per group and per part that reacts differently. The Thinking model decides it from the personality in the
/// background after the personality is saved (Re-decide from personality asks again); the owner's edits here win and stay until
/// they re-decide. A zone's own pick under Touch zones still wins over the temperament.</summary>
public partial class MainWindow
{
    private const string NotDecided = "(not decided: follow your mouse)";
    private readonly CharacterTemperamentService characterTemperaments;
    private TextBlock? temperamentDecision;
    private bool decidingTemperament;

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
        try { await characterTemperaments.DecideAsync(persona, AskThinkingForTemperamentAsync, lifetime.Token); }
        finally
        {
            decidingTemperament = false;
            tabEdited = false;
            if (!closing && openTab == CompanionTab.Character) RenderTab();
        }
    }

    private Border CharacterTemperamentCard()
    {
        var persona = homeSettings?.Companion?.ActivePersona;
        var stack = new List<UIElement>
        {
            Heading("Touch temperament"),
            Note("How the character reacts when you touch each part of it, and where its eyes usually go. When you save a " +
                "personality, your Thinking model decides this from it in the background. Your own changes stay until you re-decide.",
                new Thickness(0, 0, 0, 12))
        };
        var temperament = characterTemperaments.For(persona?.Id);
        // Who decided it, with the whole temperament in words as its tooltip (and help text, for UI Automation).
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(status, "TouchTemperamentStatus");
        void ShowStatus(CharacterTouchTemperament? shown)
        {
            if (persona is null)
            {
                status.Text = "No persona is in use.";
                return;
            }
            status.Inlines.Clear();
            status.Inlines.Add(new Run("For "));
            status.Inlines.Add(new Run(persona.Name) { FontWeight = FontWeights.SemiBold });
            status.Inlines.Add(new Run(": " + (shown?.Source switch
            {
                CharacterTouchTemperament.ByOwner => "your own choices.",
                CharacterTouchTemperament.ByFixture => "FIXTURE - NOT AI: read from " + CharacterTemperamentService.FixtureVariable + ".",
                CharacterTouchTemperament.ByThinking => "decided by the Thinking model from the personality" +
                    (shown.DecidedAt is { } at ? $" on {at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}." : "."),
                _ => "built-in reactions (not decided yet)."
            })));
            var summary = CharacterTouchTemperaments.Summary(shown) +
                (shown is null ? "" : $"; repeated touches escalate after {shown.Escalation.After}.");
            status.ToolTip = summary;
            AutomationProperties.SetHelpText(status, summary);
        }
        ShowStatus(temperament);
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
            "touch: actions only, never words. It never asks while Martlet replies.";
        AutomationProperties.SetHelpText(decide, decideHelp);
        decide.ToolTip = decideHelp;
        ToolTipService.SetShowOnDisabled(decide, true);
        var reset = PageButton("Use built-in reactions", async () =>
        {
            if (persona is null) return;
            var why = await characterTemperaments.ResetAsync(persona.Id, lifetime.Token);
            tabEdited = false;
            if (why is not null) ShowStatusLine(saveState, "Not saved: " + why);
            else if (openTab == CompanionTab.Character) RenderTab();
        }, id: "TouchTemperamentReset");
        reset.ToolTip = "Forgets this persona's touch temperament, so every part plays its built-in reaction again.";
        void ShowActions(CharacterTouchTemperament? shown)
        {
            var label = busy ? "Deciding..." : shown is null ? "Decide from personality" : "Re-decide from personality";
            decide.Content = label;
            AutomationProperties.SetName(decide, label);
            // Nothing decided yet: deciding is the next step.
            if (canDecide && shown is null) decide.SetResourceReference(StyleProperty, "PrimaryButton");
            else decide.ClearValue(StyleProperty);
            reset.IsEnabled = !busy && shown is not null;
        }
        ShowActions(temperament);
        var actions = Row(decide, reset);
        actions.Margin = new Thickness(0, 10, 0, 0);
        stack.Add(actions);
        if (persona is null)
        {
            stack.Add(saveState);
            return Card([.. stack]);
        }

        // One table: where the eyes usually go, then a line per group and per part with its own reaction, in aligned columns.
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
        var autoSave = new AutoSave(async () =>
        {
            if (homeSettings?.Companion?.ActivePersonaId != persona.Id) return true;
            var current = characterTemperaments.For(persona.Id);
            var escalation = current?.Escalation ?? new TouchEscalation();
            var next = new CharacterTouchTemperament
            {
                PersonaId = persona.Id, Source = CharacterTouchTemperament.ByOwner,
                PersonalityDigest = current?.PersonalityDigest ?? CharacterTouchTemperaments.Digest(persona.Text), DecidedAt = current?.DecidedAt,
                Gaze = gaze.SelectedIndex > 0 ? CharacterGaze.Modes[gaze.SelectedIndex - 1].Mode : null,
                Groups = rows.Where(r => r.IsGroup && !r.Removed).Select(r => (r.Id, Entry: r.Read())).Where(r => r.Entry is not null)
                    .ToDictionary(r => r.Id, r => r.Entry!),
                Zones = rows.Where(r => !r.IsGroup && !r.Removed).Select(r => (r.Id, Entry: r.Read())).Where(r => r.Entry is not null)
                    .ToDictionary(r => r.Id, r => r.Entry!),
                Escalation = escalation with
                {
                    After = int.TryParse(after.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var n)
                        ? Math.Clamp(n, CharacterTouchTemperaments.MinimumAfter, CharacterTouchTemperaments.MaximumAfter) : escalation.After
                }
            };
            var why = await characterTemperaments.SaveAsync(next, lifetime.Token);
            ShowStatusLine(saveState, why is null ? "All changes saved." : "Not saved: " + why);
            saveState.SetResourceReference(TextBlock.ForegroundProperty, why is null ? "MutedBrush" : "WarningBrush");
            if (why is null)
            {
                tabEdited = false;
                ShowStatus(characterTemperaments.For(persona.Id));
                ShowActions(characterTemperaments.For(persona.Id));
            }
            return true;
        });
        void Edited()
        {
            tabEdited = true;
            ShowReactionHeads();
            ShowStatusLine(saveState, "Saving...");
            autoSave.Changed();
        }
        after.TextChanged += (_, _) => Edited();
        gaze.SelectionChanged += (_, _) => Edited();

        gaze.Margin = new Thickness(0, 3, 0, 3);
        table.Line(new Label { Content = "Eyes usually", Target = gaze, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center }, gaze);
        string[] heads = ["Part", "Feels", "Plays", "Then", "Lingers (s)", "Looks at mouse (s)"];
        string?[] headHelp =
        [
            null, "How the character feels about a touch there: hates, dislikes, neutral, likes, loves or craves. (built-in) leaves a " +
                "group's parts to their built-in reactions.",
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

        foreach (var (_, id, label) in CharacterTouchTemperaments.GroupIds)
            rows.Add(new TemperamentRow(table, id, label, true, temperament?.Groups.GetValueOrDefault(id), Edited));
        table.View.Children.Add(new TextBlock
        {
            Text = "Parts that react differently from their group", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
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
            AutomationProperties.SetHelpText(add, "Gives the chosen part its own line, which wins over its group.");
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
        card.Unloaded += (_, _) => { if (autoSave.Pending) autoSave.SaveNowAsync().Forget(); };
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
        private const double Part = 130, Number = 60, Gap = 8;
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

        internal TemperamentRow(TemperamentTable table, string id, string label, bool isGroup, TouchTemperamentEntry? entry, Action edited)
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
                remove.ToolTip = $"Remove {label}'s own line, so it reacts like its group";
                AutomationProperties.SetName(remove, $"Remove {label}");
                var dock = new DockPanel();
                DockPanel.SetDock(remove, Dock.Right);
                dock.Children.Add(remove);
                dock.Children.Add(name);
                part = dock;
            }
            attitude = Combo((isGroup ? new[] { BuiltIn } : []).Concat(CharacterTouchTemperaments.AttitudeWords).ToArray());
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
