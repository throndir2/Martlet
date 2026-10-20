using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;
using Martlet.Presentation;

namespace Martlet.Desktop;

internal sealed partial class EmotesPage
{
    /// <summary>One combo's row: on, its tag, its parts and When to use (with the hint replies get in grey while it is empty), a
    /// line on what it sets off, Try and Remove. The parts stay the emotes the Parts box named when it was drawn or last typed
    /// while its text is unchanged, so renaming an emote's tag never breaks a combo or points it at another emote.</summary>
    private sealed class ComboRow
    {
        private readonly CharacterCombo? saved;
        private readonly CharacterActionCatalog catalog;
        private readonly Func<IReadOnlyList<CharacterAction>> actions;
        private readonly CheckBox on;
        private readonly TextBox tag, parts, use;
        private readonly TextBlock title, state, hint;
        // The Parts box's text and the parts it named then, against the emotes' tags of that moment.
        private (string Text, IReadOnlyList<string> Ids) named;
        internal Button TryButton { get; }
        internal bool Removed { get; private set; }
        internal StackPanel View { get; } = new();

        internal ComboRow(EmotesPage page, CharacterCombo? combo, int n, CharacterActionCatalog catalog,
            Func<IReadOnlyList<CharacterAction>> actions, bool showing, Action edited, Action removed)
        {
            saved = combo;
            this.catalog = catalog;
            this.actions = actions;
            named = (combo is null ? "" : CharacterActions.PartsText(combo.Parts, catalog.Settings.Actions), combo?.Parts ?? []);
            on = RowSwitch(combo?.Enabled ?? true);
            AutomationProperties.SetName(on, $"Use combo {n + 1}");
            AutomationProperties.SetAutomationId(on, $"CharacterComboOn-{n}");
            title = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(title, $"CharacterComboName-{n}");
            state = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(RowIndent, 1, 8, 0) };
            state.SetResourceReference(FrameworkElement.StyleProperty, "Muted");
            AutomationProperties.SetAutomationId(state, $"CharacterComboState-{n}");
            tag = Compact(new TextBox { Text = combo?.Tag ?? "", Width = 120, MaxLength = CharacterActionCatalog.MaximumTagLength });
            AutomationProperties.SetName(tag, $"Tag for combo {n + 1}");
            AutomationProperties.SetAutomationId(tag, $"CharacterComboTag-{n}");
            InputHint.SetText(tag, "flustered");
            parts = Compact(new TextBox { Text = named.Text, MinWidth = 220, MaxLength = 200 });
            AutomationProperties.SetName(parts, $"Parts of combo {n + 1}");
            AutomationProperties.SetAutomationId(parts, $"CharacterComboParts-{n}");
            const string partsHelp = "The tags of 2 to 6 emotes, motions or gestures, with spaces between, such as blush hearts nod.";
            AutomationProperties.SetHelpText(parts, partsHelp);
            parts.ToolTip = partsHelp;
            InputHint.SetText(parts, "blush hearts nod");
            use = Compact(new TextBox { Text = combo?.Use ?? "", MinWidth = 150, MaxLength = CharacterActionCatalog.MaximumUseLength });
            AutomationProperties.SetName(use, $"When to use combo {n + 1}");
            AutomationProperties.SetAutomationId(use, $"CharacterComboUse-{n}");
            const string useHelp = "What the Thinking model reads next to this tag, so it knows when to use it (empty: what it combines).";
            AutomationProperties.SetHelpText(use, useHelp);
            use.ToolTip = useHelp;
            var useBox = WithHint(use, "", $"CharacterComboHint-{n}");
            hint = (TextBlock)useBox.Children[1];
            TryButton = Compact(PageButton("Try", () => page.TryCharacterComboAsync(this).Forget(), id: $"CharacterComboTry-{n}"));
            TryButton.IsEnabled = showing;
            var remove = Compact(PageButton("Remove", () =>
            {
                Removed = true;
                View.Visibility = Visibility.Collapsed;
                removed();
            }, id: $"CharacterComboRemove-{n}"));
            AutomationProperties.SetHelpText(remove, "Removes this combo. Its parts that show now stay on.");

            on.Checked += (_, _) => edited();
            on.Unchecked += (_, _) => edited();
            // The card's Edited brings every combo's line and hint up to date.
            tag.TextChanged += (_, _) => edited();
            parts.TextChanged += (_, _) =>
            {
                // Typed parts name the emotes with those tags now; later renames keep them.
                if (CharacterActions.ParseParts(parts.Text, actions(), out _) is { } ids) named = (parts.Text, ids);
                edited();
            };
            use.TextChanged += (_, _) => edited();
            Refresh();

            // As an emote's row: the tag and what it sets off, then its fields; When to use takes the rest of the line.
            var titleLine = new DockPanel();
            titleLine.Children.Add(on);
            titleLine.Children.Add(title);
            var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            heading.Children.Add(titleLine);
            heading.Children.Add(state);
            var header = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
            DockPanel.SetDock(remove, Dock.Right);
            DockPanel.SetDock(TryButton, Dock.Right);
            remove.Margin = new Thickness(6, 0, 0, 0);
            TryButton.Margin = new Thickness(8, 0, 0, 0);
            TryButton.VerticalAlignment = remove.VerticalAlignment = VerticalAlignment.Top;
            header.Children.Add(remove);
            header.Children.Add(TryButton);
            header.Children.Add(heading);
            var fields = new FillWrapPanel { Margin = new Thickness(RowIndent, 6, 0, 0), FillMinimum = 300 };
            fields.Children.Add(RowGroup(RowLabel("Tag  {", tag, 0, 4), tag, RowLabel("}", tag, 4, 0)));
            fields.Children.Add(RowGroup(RowLabel("Parts", parts), parts));
            var when = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            when.Children.Add(RowLabel("When to use", use));
            when.Children.Add(useBox);
            fields.Children.Add(when);
            View.Children.Add(header);
            View.Children.Add(fields);
        }

        /// <summary>The combo's tag as the row shows it now (braces left out, in lower case).</summary>
        internal string Tag => tag.Text.Trim().Trim('{', '}').Trim().ToLowerInvariant();

        internal void FocusTag() => tag.Focus();

        // The parts' IDs: those the Parts box named while its text is unchanged, otherwise what its tags name now.
        private IReadOnlyList<string>? PartIds(IReadOnlyList<CharacterAction> now, out string? problem)
        {
            problem = null;
            return parts.Text == named.Text ? named.Ids : CharacterActions.ParseParts(parts.Text, now, out problem);
        }

        // The parts that resolve to an emote of the model, with their settings as the emotes' rows show them now.
        private (CharacterActionSource Source, CharacterAction Action)[] Resolved(IReadOnlyList<CharacterAction> now, IReadOnlyList<string> ids) =>
            [.. ids.Select(id => (Source: catalog.Inventory.Find(id), Action: now.FirstOrDefault(a => a.Id == id)))
                .Where(p => p.Source is not null && p.Action is not null).Select(p => (p.Source!, p.Action!))];

        /// <summary>The parts that are turned on, as the row and the emotes' rows are now, each with whether it lingers.</summary>
        internal IReadOnlyList<(CharacterActionSource Source, bool Lingers)> OnParts()
        {
            var now = actions();
            return PartIds(now, out _) is { } ids
                ? [.. Resolved(now, ids).Where(p => p.Action.Enabled).Select(p => (p.Source, CharacterActions.Lingers(p.Source, p.Action)))]
                : [];
        }

        /// <summary>The combo as the row shows it now: null for a removed row or a new one left empty (nothing to save), or null
        /// with <paramref name="problem"/> when it can't be saved (Problem checks the rest).</summary>
        internal CharacterCombo? Read(IReadOnlyList<CharacterAction> now, out string? problem)
        {
            problem = null;
            if (Removed) return null;
            var text = use.Text.Trim();
            if (saved is null && Tag.Length == 0 && parts.Text.Trim().Length == 0 && text.Length == 0) return null;
            if (Tag.Length == 0)
            {
                problem = "a combo needs a tag, such as flustered.";
                return null;
            }
            return PartIds(now, out problem) is { } ids
                ? new CharacterCombo { Tag = Tag, Parts = ids, Use = text.Length > 0 ? text : null, Enabled = on.IsChecked == true }
                : null;
        }

        /// <summary>Brings the title, the line on what the combo sets off (CharacterComboState-n) and the grey hint
        /// (CharacterComboHint-n, what replies get while When to use is empty) up to date with the row and the emotes' rows.</summary>
        internal void Refresh()
        {
            title.Text = Tag.Length == 0 ? "New combo" : $"{{{Tag}}}  \u00b7  combo";
            var now = actions();
            if (PartIds(now, out var problem) is not { } ids)
            {
                state.Text = char.ToUpperInvariant(problem![0]) + problem[1..];
                return;
            }
            var resolved = Resolved(now, ids);
            var lasting = resolved.Where(p => p.Action.Enabled && CharacterActions.Lingers(p.Source, p.Action)).Select(p => p.Source).ToArray();
            var brief = resolved.Where(p => p.Action.Enabled && !CharacterActions.Lingers(p.Source, p.Action)).Select(p => p.Source).ToArray();
            var skipped = resolved.Where(p => !p.Action.Enabled).Select(p => p.Source).ToArray();
            var said = string.Join("; ", new[]
            {
                lasting.Length > 0 ? $"turns on {Names(lasting)} until {{/{(Tag.Length > 0 ? Tag : "tag")}}}" : null,
                brief.Length > 0 ? $"plays {Names(brief)} once" : null,
                skipped.Length > 0 ? $"skips {Names(skipped)} (turned off)" : null
            }.OfType<string>());
            state.Text = ids.Count == 0 ? "Write the tags of 2 to 6 parts, such as blush hearts nod."
                : said.Length == 0 ? "None of its parts is on."
                : char.ToUpperInvariant(said[0]) + said[1..] + ".";
            hint.Text = CharacterActions.DescribeCombo([.. resolved.Where(p => p.Action.Enabled).Select(p => CharacterActions.PartLabel(p.Source, p.Action))]);
        }

        private static string Names(IReadOnlyList<CharacterActionSource> sources)
        {
            var names = sources.Select(s => $"\"{s.Name}\"").ToArray();
            return names.Length == 1 ? names[0] : string.Join(", ", names[..^1]) + " and " + names[^1];
        }
    }
}
