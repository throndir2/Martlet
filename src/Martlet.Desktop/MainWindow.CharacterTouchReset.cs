using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>Companion › Touch › Touch zones › Start over: one Reset button that takes the shown model's touch setup and the active
/// persona's touch temperament back to how a fresh character starts, at the level chosen beside it
/// (<see cref="CharacterTouchReset.Levels"/>), after a confirmation that lists exactly what is lost.</summary>
public partial class MainWindow
{
    // The level chosen in the list (kept while the page is drawn again) and what the last reset did.
    private string touchResetLevel = CharacterTouchReset.Levels[0].Id;
    private string? touchResetState;
    private bool resettingTouch;

    private TouchResetTarget TouchResetTargetNow()
    {
        // A fresh zone's reaction list comes from the model's emotes and the persona's temperament; without the model's emotes it
        // is left to be filled (null) when they are known, never emptied.
        var catalog = characterActions.Current is { } shown && shown.Inventory.ModelId == characterTouchZones.ModelId ? shown : null;
        var temperament = characterTemperaments.For(homeSettings?.Companion?.ActivePersonaId);
        return new(characterTouchZones, characterTemperaments, homeSettings?.Companion?.ActivePersona,
            zone => catalog is null ? new CharacterTouchReaction() : CharacterTouchZones.FreshReaction(zone, catalog, temperament),
            persona => characterTemperaments.PersonalitySaved(persona, () => conversation?.Replying == true || openConversation?.HearingYou == true,
                AskThinkingForTemperamentAsync, lifetime.Token,
                news: $"{persona.Name}'s touch temperament was reset; Martlet decides it again from the personality in a moment."));
    }

    /// <summary>The Start over section at the end of the Touch zones card: the level list (TouchZonesResetLevel), what the level
    /// clears (TouchZonesResetNote), Reset (TouchZonesReset) and what the last reset did (TouchZonesResetState).
    /// <paramref name="edits"/> holds the card's unsaved zone edits, saved before a reset so they can't come back after it.</summary>
    private StackPanel TouchZonesResetSection(string modelId, AutoSave edits)
    {
        var section = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
        section.Children.Add(new TextBlock { Text = "Start over", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        section.Children.Add(Note("Reset this character's touch to how a fresh character starts. Choose how much to reset. Martlet asks " +
            "first and lists what you lose. Only the model shown and the active persona change.", new Thickness(0, 0, 0, 6)));
        var levels = CharacterTouchReset.Levels;
        var chosen = CharacterTouchReset.Level(touchResetLevel);
        var list = Compact(new ComboBox { ItemsSource = levels.Select(l => l.Label).ToArray(), SelectedIndex = IndexOf(chosen), MinWidth = 180 });
        AutomationProperties.SetName(list, "What to reset");
        AutomationProperties.SetAutomationId(list, "TouchZonesResetLevel");
        var reset = Compact(PageButton(resettingTouch ? "Resetting..." : "Reset...", () => ResetTouchAsync(modelId, edits).Forget(), id: "TouchZonesReset"));
        reset.Margin = new Thickness(8, 0, 0, 0);
        reset.IsEnabled = !resettingTouch && !detectingTouchZones && !characterTouchZones.Busy;
        AutomationProperties.SetHelpText(reset, "Asks first, and lists exactly what you lose, before anything is reset.");
        var row = new WrapPanel();
        row.Children.Add(list);
        row.Children.Add(reset);
        section.Children.Add(row);
        var clears = Note(chosen.Clears, new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(clears, "TouchZonesResetNote");
        section.Children.Add(clears);
        list.SelectionChanged += (_, _) =>
        {
            var level = levels[Math.Max(0, list.SelectedIndex)];
            touchResetLevel = level.Id;
            clears.Text = level.Clears;
        };
        var state = Note("", new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(state, "TouchZonesResetState");
        AutomationProperties.SetLiveSetting(state, AutomationLiveSetting.Polite);
        ShowStatusLine(state, touchResetState);
        section.Children.Add(state);
        return section;

        static int IndexOf(TouchResetLevel level)
        {
            for (var i = 0; i < CharacterTouchReset.Levels.Count; i++)
                if (CharacterTouchReset.Levels[i] == level) return i;
            return 0;
        }
    }

    private async Task ResetTouchAsync(string modelId, AutoSave edits)
    {
        if (resettingTouch || closing) return;
        var level = CharacterTouchReset.Level(touchResetLevel);
        if (edits.Pending) await edits.SaveNowAsync();
        if (characterTouchZones.ModelId != modelId) return;
        var target = TouchResetTargetNow();
        var loses = level.Loses(target);
        if (loses.Count == 0)
        {
            ShowTouchResetState($"Nothing to reset under {level.Label}: it is already as a fresh character's.");
            return;
        }
        if (!ConfirmationDialog.Confirm(this, CharacterTouchReset.Question(level, target, loses), "Reset touch", "Reset", "Cancel",
                questionId: "TouchZonesResetQuestion"))
        {
            ShowTouchResetState("Nothing was reset.");
            return;
        }
        resettingTouch = true;
        if (openTab == CompanionTab.Touch) RenderTab();
        // Everything's words come from what its parts clear, so they are read before the reset clears them.
        var after = level.After(target);
        string? why;
        try { why = await level.Run(target, lifetime.Token); }
        catch (OperationCanceledException) { return; }
        finally { resettingTouch = false; }
        ErrorLog.Info(why is null ? $"Touch reset ({level.Id}) for the shown model and the active persona." : $"Touch reset ({level.Id}) failed: {why}");
        touchResetState = why is null
            ? $"Reset {level.Label.ToLowerInvariant()} at {DateTime.Now:t}." + (after is null ? "" : " " + after)
            : "Not reset: " + why;
        // A model whose zones were forgotten gets its first guess again when the page is drawn.
        firstTouchZonesTried.Remove(modelId);
        tabEdited = false;
        if (!closing && openTab == CompanionTab.Touch) RenderTab();
    }

    private void ShowTouchResetState(string text)
    {
        touchResetState = text;
        if (!closing && openTab == CompanionTab.Touch) RenderTab();
    }
}
