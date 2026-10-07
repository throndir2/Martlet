using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Companion › Character › Touch temperament: how the active persona's character acts (never what it says) when each
/// part of it is touched: how much it likes it (hates to craves) and which emotes, gestures and face symbols play. The
/// Thinking model decides it from the personality in the background after the personality is saved (Re-decide from
/// personality asks again); the owner's edits here win and stay until they re-decide. A zone's own pick under Touch zones
/// still wins over the temperament.</summary>
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
            if (temperamentDecision is not null) temperamentDecision.Text = characterTemperaments.Status ?? "";
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
            Note("How the character acts when you touch it: which emotes, gestures and face symbols play for each part, from " +
                "hating it to craving it, whether it then looks at your mouse, and where its eyes usually go. When you save a " +
                "personality, your Thinking model decides this from it in the background (never while Martlet replies); it " +
                "decides actions only, never words. Change anything below and your choices stay until you re-decide. A zone's own " +
                "pick under Touch zones still wins. Intimate parts react only with Include intimate zones on.", new Thickness(0, 0, 0, 8))
        };
        var temperament = characterTemperaments.For(persona?.Id);
        var status = Note(persona is null ? "No persona is in use." : $"For {persona.Name}: " + (temperament?.Source switch
        {
            CharacterTouchTemperament.ByOwner => "your own choices.",
            CharacterTouchTemperament.ByFixture => "FIXTURE - NOT AI: read from " + CharacterTemperamentService.FixtureVariable + ".",
            CharacterTouchTemperament.ByThinking => "decided by the Thinking model from the personality" +
                (temperament.DecidedAt is { } at ? $" on {at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}." : "."),
            _ => "built-in reactions (not decided yet)."
        }), new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(status, "TouchTemperamentStatus");
        stack.Add(status);
        var summary = Note(CharacterTouchTemperaments.Summary(temperament) +
            (temperament is null ? "" : $"; repeated touches escalate after {temperament.Escalation.After}."), new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(summary, "TouchTemperamentSummary");
        stack.Add(summary);
        temperamentDecision = Note(characterTemperaments.Status ?? "", new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(temperamentDecision, "TouchTemperamentDecision");
        AutomationProperties.SetLiveSetting(temperamentDecision, AutomationLiveSetting.Polite);
        stack.Add(temperamentDecision);
        var saveState = Note("", new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(saveState, "TouchTemperamentSaveState");
        AutomationProperties.SetLiveSetting(saveState, AutomationLiveSetting.Polite);
        stack.Add(saveState);

        var busy = decidingTemperament || characterTemperaments.Busy;
        var decide = PageButton(busy ? "Deciding..." : "Re-decide from personality", () => DecideTemperamentAsync().Forget(), id: "TouchTemperamentDecide");
        decide.IsEnabled = !busy && persona is { Text.Length: > 0 } && (conversation is not null || CharacterTemperamentService.Fixture);
        AutomationProperties.SetHelpText(decide, "Sends the persona's personality text to your Thinking model, which decides how the character reacts to touch.");
        var reset = PageButton("Use built-in reactions", async () =>
        {
            if (persona is null) return;
            var why = await characterTemperaments.ResetAsync(persona.Id, lifetime.Token);
            tabEdited = false;
            if (why is not null) saveState.Text = "Not saved: " + why;
            else if (openTab == CompanionTab.Character) RenderTab();
        }, id: "TouchTemperamentReset");
        reset.IsEnabled = !busy && temperament is not null;
        stack.Add(Row(decide, reset));
        if (persona is null) return Card([.. stack]);

        var rows = new List<TemperamentRow>();
        var after = new TextBox { Text = (temperament?.Escalation.After ?? new TouchEscalation().After).ToString(CultureInfo.CurrentCulture), Width = 60 };
        AutomationProperties.SetName(after, "Touches in a row before the reaction escalates");
        AutomationProperties.SetAutomationId(after, "TouchTemperamentAfter");
        // Where the character's eyes usually go (Where the character looks › As the personality decides uses it).
        var gazeChoices = new[] { NotDecided }.Concat(CharacterGaze.Modes.Select(m => m.Label)).ToArray();
        var gaze = new ComboBox
        {
            ItemsSource = gazeChoices, MinWidth = 260, MinHeight = 26,
            SelectedIndex = temperament?.Gaze is { } decidedGaze ? 1 + CharacterGaze.Modes.ToList().FindIndex(m => m.Mode == decidedGaze) : 0
        };
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
            saveState.Text = why is null ? "All changes saved." : "Not saved: " + why;
            saveState.SetResourceReference(TextBlock.ForegroundProperty, why is null ? "MutedBrush" : "WarningBrush");
            if (why is null)
            {
                tabEdited = false;
                status.Text = $"For {persona.Name}: your own choices.";
                summary.Text = CharacterTouchTemperaments.Summary(characterTemperaments.For(persona.Id)) + $"; repeated touches escalate after {next.Escalation.After}.";
            }
            return true;
        });
        void Edited()
        {
            tabEdited = true;
            saveState.Text = "Saving...";
            autoSave.Changed();
        }
        after.TextChanged += (_, _) => Edited();
        gaze.SelectionChanged += (_, _) => Edited();
        var gazeRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        gazeRow.Children.Add(new Label { Content = "Eyes usually", Target = gaze, Padding = new Thickness(0, 4, 6, 4), Width = 170 });
        gazeRow.Children.Add(gaze);
        stack.Add(gazeRow);

        foreach (var (_, id, label) in CharacterTouchTemperaments.GroupIds)
        {
            var row = new TemperamentRow(id, label, true, temperament?.Groups.GetValueOrDefault(id), Edited);
            rows.Add(row);
            stack.Add(row.View);
        }
        var zonesHeading = Note("Parts that react differently from their group:", new Thickness(0, 10, 0, 0));
        stack.Add(zonesHeading);
        foreach (var (id, entry) in (temperament?.Zones ?? new Dictionary<string, TouchTemperamentEntry>()).OrderBy(z => z.Key, StringComparer.Ordinal))
        {
            var row = new TemperamentRow(id, CharacterTouchZones.Kind(id)?.Label ?? id, false, entry, Edited);
            rows.Add(row);
            stack.Add(row.View);
        }
        var missing = CharacterTouchZones.Kinds.Where(k => temperament?.Zones.ContainsKey(k.Id) != true).ToArray();
        if (missing.Length > 0)
        {
            var kinds = new ComboBox { ItemsSource = missing.Select(k => k.Label).ToArray(), SelectedIndex = 0, MinWidth = 180, MinHeight = 26 };
            AutomationProperties.SetName(kinds, "Part to give its own reaction");
            AutomationProperties.SetAutomationId(kinds, "TouchTemperamentAddKind");
            var addPanel = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var add = PageButton("Add part", () =>
            {
                var kind = missing[Math.Max(0, kinds.SelectedIndex)];
                if (rows.Any(r => r.Id == kind.Id && !r.Removed)) return;
                var row = new TemperamentRow(kind.Id, kind.Label, false,
                    CharacterTouchTemperaments.Entry(characterTemperaments.For(persona.Id), kind.Id) ?? new TouchTemperamentEntry(), Edited);
                rows.Add(row);
                if (addPanel.Parent is Panel panel) panel.Children.Insert(panel.Children.IndexOf(addPanel), row.View);
                Edited();
            }, id: "TouchTemperamentAdd");
            addPanel.Children.Add(kinds);
            add.Margin = new Thickness(8, 0, 0, 0);
            addPanel.Children.Add(add);
            stack.Add(addPanel);
        }
        var escalationRow = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        escalationRow.Children.Add(new Label { Content = "Repeated touches escalate after", Target = after, Padding = new Thickness(0, 4, 6, 4) });
        escalationRow.Children.Add(after);
        escalationRow.Children.Add(new TextBlock
        {
            Text = $"touches in a row (within {CharacterTouchTemperaments.RepeatWindowSeconds:0} seconds): a disliked part then plays " +
                string.Join(" + ", temperament?.Escalation.Disliked ?? new TouchEscalation().Disliked) + ", a loved one " +
                string.Join(" + ", temperament?.Escalation.Loved ?? new TouchEscalation().Loved) + ".",
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), TextWrapping = TextWrapping.Wrap
        });
        stack.Add(escalationRow);
        var card = Card([.. stack]);
        card.Unloaded += (_, _) => { if (autoSave.Pending) autoSave.SaveNowAsync().Forget(); };
        return card;
    }

    /// <summary>One group's or part's line: attitude, up to two reactions (or none at all), how long the first lingers, how long
    /// the eyes then look at your mouse (and Remove for a part).</summary>
    private sealed class TemperamentRow
    {
        private const string BuiltIn = "(built-in reaction)", AttitudeOwn = "(the attitude's own)", NoReaction = "(no reaction)",
            NothingElse = "(nothing else)";
        private readonly ComboBox attitude, first, second;
        private readonly TextBox linger, look;
        internal string Id { get; }
        internal bool IsGroup { get; }
        internal bool Removed { get; private set; }
        internal WrapPanel View { get; } = new() { Margin = new Thickness(0, 6, 0, 0) };

        internal TemperamentRow(string id, string label, bool isGroup, TouchTemperamentEntry? entry, Action edited)
        {
            Id = id;
            IsGroup = isGroup;
            var words = CharacterTouchTemperaments.Vocabulary;
            View.Children.Add(new TextBlock { Text = label, Width = 170, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            attitude = new ComboBox
            {
                ItemsSource = (isGroup ? new[] { BuiltIn } : []).Concat(CharacterTouchTemperaments.AttitudeWords).ToArray(), MinWidth = 140, MinHeight = 26
            };
            var offset = isGroup ? 1 : 0;
            attitude.SelectedIndex = entry is null ? 0 : entry.Attitude - CharacterTouchTemperaments.MinimumAttitude + offset;
            AutomationProperties.SetName(attitude, $"How the character feels about a touch on: {label}");
            AutomationProperties.SetAutomationId(attitude, $"TouchTemperamentAttitude-{id}");
            // First choices: the attitude's own reactions, no reaction at all, then each reaction.
            first = new ComboBox { ItemsSource = new[] { AttitudeOwn, NoReaction }.Concat(words).ToArray(), MinWidth = 150, MinHeight = 26, Margin = new Thickness(6, 0, 0, 0) };
            first.SelectedIndex = entry?.Reactions switch
            {
                { Count: 0 } => 1,
                { Count: > 0 } r when IndexOf(words, r[0]) is var i and >= 0 => i + 2,
                _ => 0
            };
            AutomationProperties.SetName(first, $"First reaction to a touch on: {label}");
            AutomationProperties.SetAutomationId(first, $"TouchTemperamentReaction-{id}");
            second = new ComboBox { ItemsSource = new[] { NothingElse }.Concat(words).ToArray(), MinWidth = 140, MinHeight = 26, Margin = new Thickness(6, 0, 0, 0) };
            second.SelectedIndex = entry?.Reactions is { Count: > 1 } r2 ? Math.Max(0, IndexOf(words, r2[1]) + 1) : 0;
            AutomationProperties.SetName(second, $"Second reaction to a touch on: {label}");
            AutomationProperties.SetAutomationId(second, $"TouchTemperamentReaction2-{id}");
            linger = new TextBox { Text = (entry?.LingerSeconds ?? 0).ToString("0.#", CultureInfo.CurrentCulture), Width = 50, Margin = new Thickness(6, 0, 0, 0) };
            AutomationProperties.SetName(linger, $"Seconds the first reaction to {label} stays on");
            AutomationProperties.SetAutomationId(linger, $"TouchTemperamentLinger-{id}");
            look = new TextBox { Text = (entry?.LookSeconds ?? 0).ToString("0.#", CultureInfo.CurrentCulture), Width = 50, Margin = new Thickness(6, 0, 0, 0) };
            AutomationProperties.SetName(look, $"Seconds the character looks at your mouse after a touch on {label}");
            AutomationProperties.SetAutomationId(look, $"TouchTemperamentLook-{id}");
            void Enable()
            {
                var on = !isGroup || attitude.SelectedIndex > 0;
                first.IsEnabled = look.IsEnabled = on;
                linger.IsEnabled = on && first.SelectedIndex != 1;
                second.IsEnabled = on && first.SelectedIndex > 1;
            }
            Enable();
            attitude.SelectionChanged += (_, _) => { Enable(); edited(); };
            first.SelectionChanged += (_, _) => { Enable(); edited(); };
            second.SelectionChanged += (_, _) => edited();
            linger.TextChanged += (_, _) => edited();
            look.TextChanged += (_, _) => edited();
            View.Children.Add(attitude);
            View.Children.Add(first);
            View.Children.Add(second);
            View.Children.Add(new TextBlock { Text = "lingers (s)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
            View.Children.Add(linger);
            View.Children.Add(new TextBlock { Text = "looks at your mouse (s)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
            View.Children.Add(look);
            if (isGroup) return;
            var remove = PageButton("Remove", () =>
            {
                Removed = true;
                View.Visibility = Visibility.Collapsed;
                edited();
            }, id: $"TouchTemperamentRemove-{id}");
            remove.Margin = new Thickness(8, 0, 0, 0);
            View.Children.Add(remove);
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
