using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Emotes and motions › Combos: the shown model's combos of its emotes, motions and gestures
/// (Martlet's own, given once, and the owner's). Each ties a tag to 2 to 6 of them: a reply's {tag} sets off every part at once
/// and {/tag} turns its lingering parts off. They save with the model's emote settings (the card's own autosave), so they are the
/// same on all the owner's computers. Each combo's row is <see cref="ComboRow"/> (EmotesPage.ComboRow.cs).</summary>
internal sealed partial class EmotesPage
{
    private TextBlock? characterCombosStatus;
    private IReadOnlyList<ComboRow> characterComboRows = [];

    /// <summary>The Combos section, filling <paramref name="rows"/>: its heading and note, CharacterCombosStatus, a row for each
    /// combo and Add a combo (CharacterCombosAdd only adds an empty row; nothing saves until it has a tag and parts).
    /// <paramref name="actions"/> are the emotes as their rows show them now, which the parts' tags name.</summary>
    private StackPanel CharacterCombosSection(CharacterActionCatalog catalog, List<ComboRow> rows, Func<IReadOnlyList<CharacterAction>> actions,
        Action edited, bool showing)
    {
        characterComboRows = rows;
        var section = new StackPanel();
        var heading = new TextBlock { Text = "Combos", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 22, 0, 4) };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level3);
        section.Children.Add(heading);
        section.Children.Add(HelpTip.Explain("A combo sets off several emotes, motions and gestures at once with a tag of your own. Write the tags " +
            "of 2 to 6 of them, such as blush hearts nod. A reply's {tag} sets off each part that is on: a part that stays on stays " +
            "until the reply writes {/tag}, and the others show a moment. Each model starts with Martlet's own combos, such as " +
            "{lovestruck}, {flustered} and {ahegao} (off until you turn it on). Change, turn off or remove any of them; a removed " +
            "one doesn't come back. Combos are yours, for this model.", new Thickness(0, 0, 0, 4), "CharacterCombos", "combos"));
        characterCombosStatus = Note(EmotesPageModel.CombosStatus(catalog), new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(characterCombosStatus, "CharacterCombosStatus");
        AutomationProperties.SetLiveSetting(characterCombosStatus, AutomationLiveSetting.Polite);
        section.Children.Add(characterCombosStatus);
        var list = new StackPanel();
        section.Children.Add(list);
        Button? add = null;
        void Available() => add!.IsEnabled = rows.Count(r => !r.Removed) < CharacterActions.MaximumCombos;
        ComboRow AddRow(CharacterCombo? combo)
        {
            var row = new ComboRow(this, combo, rows.Count, catalog, actions, showing, edited, () =>
            {
                Available();
                edited();
            });
            rows.Add(row);
            list.Children.Add(row.View);
            return row;
        }
        foreach (var combo in catalog.Combos) AddRow(combo);
        add = PageButton("Add a combo", () =>
        {
            AddRow(null).FocusTag();
            Available();
        }, id: "CharacterCombosAdd");
        AutomationProperties.SetHelpText(add, "Adds an empty combo. It saves once it has a tag and its parts.");
        Available();
        RefreshComboTries();
        section.Children.Add(Row(add));
        return section;
    }

    /// <summary>The combos <paramref name="rows"/> show now, for saving (null when there are none), or null with
    /// <paramref name="problem"/> when one can't be saved. <paramref name="actions"/> are the emotes as they are saved with them.</summary>
    private static IReadOnlyList<CharacterCombo>? ReadCombos(IEnumerable<ComboRow> rows, IReadOnlyList<CharacterAction> actions,
        out string? problem)
    {
        problem = null;
        var combos = new List<CharacterCombo>();
        foreach (var row in rows)
        {
            var combo = row.Read(actions, out problem);
            if (problem is not null) return null;
            if (combo is not null) combos.Add(combo);
        }
        return combos.Count == 0 ? null : combos;
    }

    /// <summary>Each combo's Try reads Turn off while one of its lingering parts shows.</summary>
    private void RefreshComboTries()
    {
        foreach (var row in characterComboRows)
        {
            var label = row.OnParts().Any(p => p.Lingers && services.Held.Holds(p.Source.Id)) ? "Turn off" : "Try";
            row.TryButton.Content = label;
            AutomationProperties.SetName(row.TryButton, label);
        }
    }

    /// <summary>Try on a combo: sets off its parts that are on, as a reply's {tag} would; while one of its lingering parts shows
    /// (the button reads Turn off), turns them off as {/tag} would. It uses the row as it is now, saved or not.</summary>
    private async Task TryCharacterComboAsync(ComboRow row)
    {
        var tag = row.Tag is { Length: > 0 } named ? named : "combo";
        var parts = row.OnParts();
        if (parts.Count == 0)
        {
            if (characterActionsLast is not null)
                characterActionsLast.Text = $"The combo {{{tag}}} has no part to play; write the tags of its parts, such as blush hearts nod.";
            return;
        }
        try
        {
            var lasting = parts.Where(p => p.Lingers).Select(p => p.Source).ToArray();
            if (lasting.Any(s => services.Held.Holds(s.Id))) await services.StopComboAsync(tag, lasting, "a try");
            else await services.PlayComboAsync(tag, parts, "a try");
        }
        catch (Exception error) when (error is OperationCanceledException || RendererFailures.Is(error, services.Lifetime))
        {
            if (characterActionsLast is not null) characterActionsLast.Text = $"The character couldn't play the combo {{{tag}}} right now.";
        }
    }
}
