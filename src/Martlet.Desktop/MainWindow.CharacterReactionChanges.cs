using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>Companion › Touch › Changes the character made: what the character changed itself about how it reacts to touches
/// (the How I react check-in, with the Touch reactions tools), what each change does, when it was made, why and until when, with
/// Undo for each and Undo all; then the newest that ended. A touch, a stroke and the touch line use the active persona's changes
/// in effect over the owner's zones and temperament (<see cref="FeltTemperament"/>, <see cref="ChangedZone"/>); they never edit
/// them.</summary>
public partial class MainWindow
{
    private readonly CharacterReactionChangeService characterReactionChanges;
    private const int EndedShown = 5;
    // What the last Undo did (or why not), for the next card's line.
    private string? reactionChangesNote;

    private void WireCharacterReactionChanges()
    {
        characterReactionChanges.Changed += () => Dispatcher.InvokeAsync(() =>
        {
            if (closing || openTab != CompanionTab.Touch || CompanionContent.IsKeyboardFocusWithin || tabEdited) return;
            RenderTab();
        });
    }

    /// <summary>The temperament the active persona uses, with the character's own changes in effect over it: what a touch's
    /// escalation, lingering and looking and the touch line's feeling follow.</summary>
    private CharacterTouchTemperament? FeltTemperament()
    {
        var persona = homeSettings?.Companion?.ActivePersonaId;
        return characterReactionChanges.Temperament(characterTemperaments.For(persona), persona);
    }

    /// <summary>The zone with the active persona's own changes in effect over its reaction list.</summary>
    private CharacterTouchZone ChangedZone(CharacterTouchZone zone, CharacterActionCatalog? catalog)
    {
        var persona = homeSettings?.Companion?.ActivePersonaId;
        return characterReactionChanges.Zone(zone, characterTemperaments.For(persona), catalog, persona);
    }

    private Border CharacterReactionChangesCard()
    {
        var persona = homeSettings?.Companion?.ActivePersona;
        var stack = new List<UIElement>
        {
            Heading("Changes the character made"),
            Note("The How I react check-in lets the character change how it reacts to your touches for a while, for example when " +
                "it gets angry with you or warms up to you. Its changes never edit your zones or temperament, and each one ends on its " +
                "own. Undo a change to end it now. Turn the check-in off on Companion › Check-ins.", new Thickness(0, 0, 0, 10))
        };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(status, "ReactionChangesStatus");
        stack.Add(status);
        var state = Note("", new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(state, "ReactionChangesState");
        AutomationProperties.SetLiveSetting(state, AutomationLiveSetting.Polite);
        ShowStatusLine(state, reactionChangesNote);
        reactionChangesNote = null;
        if (persona is null)
        {
            status.Text = "No persona is in use.";
            stack.Add(state);
            return Card([.. stack]);
        }
        var now = DateTimeOffset.UtcNow;
        var zones = characterTouchZones.Current;
        var catalog = characterActions.Current;
        var mine = characterReactionChanges.Saved.Where(c => c.PersonaId == persona.Id).ToArray();
        var active = mine.Where(c => c.ActiveAt(now)).OrderBy(c => c.At).ToArray();
        status.Text = active.Length switch
        {
            0 => $"{persona.Name} has no changes of its own in effect: touches play what you chose.",
            1 => $"{persona.Name} has 1 change of its own in effect.",
            _ => $"{persona.Name} has {active.Length} changes of its own in effect."
        };

        async Task EndAsync(string? id, string done)
        {
            var why = await characterReactionChanges.EndAsync(id, persona.Id, CharacterReactionChange.ByOwner, lifetime.Token);
            tabEdited = false;
            if (why is not null)
            {
                ShowStatusLine(state, "Not undone: " + why);
                state.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                return;
            }
            reactionChangesNote = done;
            if (!closing && openTab == CompanionTab.Touch) RenderTab();
        }

        var n = 0;
        foreach (var change in active)
        {
            n++;
            var what = CharacterReactionChanges.Describe(change, zones, catalog);
            var line = new TextBlock
            {
                Text = $"{what}, {CharacterReactionChanges.Until(change, now)}. Made {Made(change, now)}.",
                TextWrapping = TextWrapping.Wrap
            };
            AutomationProperties.SetAutomationId(line, $"ReactionChange-{n}");
            // The character's reason comes from the conversation: shown, but not a value Martlet's MCP reads.
            var why = Note("Why: " + change.Why, new Thickness(0, 2, 0, 0));
            AutomationProperties.SetAutomationId(why, $"ReactionChangeWhy-{n}");
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(line);
            text.Children.Add(why);
            var undo = PageButton("Undo", () => EndAsync(change.Id, $"Undid \"{what}\".").Forget(), id: $"ReactionChangeUndo-{n}");
            AutomationProperties.SetName(undo, "Undo " + what);
            undo.Margin = new Thickness(10, 0, 0, 0);
            undo.VerticalAlignment = VerticalAlignment.Center;
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(undo, Dock.Right);
            row.Children.Add(undo);
            row.Children.Add(text);
            stack.Add(row);
        }
        if (active.Length > 1)
            stack.Add(Row(PageButton("Undo all", () => EndAsync(null, $"Undid {active.Length} changes.").Forget(), id: "ReactionChangesUndoAll")));

        var ended = mine.Where(c => !c.ActiveAt(now)).OrderByDescending(c => c.EndedAt ?? c.Until).Take(EndedShown).ToArray();
        if (ended.Length > 0)
        {
            stack.Add(Note("Ended lately:", new Thickness(0, 12, 0, 0)));
            n = 0;
            foreach (var change in ended)
            {
                n++;
                var line = Note($"{CharacterReactionChanges.Describe(change, zones, catalog)}. {Ended(change)}.", new Thickness(0, 4, 0, 0));
                AutomationProperties.SetAutomationId(line, $"ReactionChangeEnded-{n}");
                stack.Add(line);
                var why = Note("Why: " + change.Why, new Thickness(12, 0, 0, 0));
                AutomationProperties.SetAutomationId(why, $"ReactionChangeEndedWhy-{n}");
                stack.Add(why);
            }
        }
        stack.Add(state);
        return Card([.. stack]);
    }

    private static string Made(CharacterReactionChange change, DateTimeOffset now)
    {
        var at = change.At.ToLocalTime();
        var when = at.Date == now.ToLocalTime().Date ? "at " + at.ToString("t", CultureInfo.CurrentCulture) : "on " + at.ToString("g", CultureInfo.CurrentCulture);
        return when + (change.By == "reactions" ? " by How I react" : change.By is { } by ? $" by the check-in {by}" : "");
    }

    private static string Ended(CharacterReactionChange change)
    {
        var at = (change.EndedAt ?? change.Until).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        return change.EndedBy switch
        {
            CharacterReactionChange.ByOwner => $"You undid it on {at}",
            CharacterReactionChange.ByCharacter => $"The character undid it on {at}",
            CharacterReactionChange.ByReplaced => $"A newer change replaced it on {at}",
            CharacterReactionChange.ByReset => $"Reset undid it on {at}",
            _ => $"It ended on {at}"
        };
    }
}
