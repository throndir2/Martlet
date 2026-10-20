using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using Martlet.Avatar.Hosting;
using Martlet.Core.Settings;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Touch › Changes the character made: what the character changed itself about how it reacts to touches, with
/// Undo for each and Undo all, then the newest that ended.</summary>
internal sealed partial class TouchPage
{
    private const int EndedShown = 5;

    /// <summary>Changes the character made: the active persona's changes in effect, each with Undo, then the newest that ended.</summary>
    private Border ReactionChangesCard()
    {
        var persona = services.ActivePersona;
        var stack = new List<UIElement>
        {
            Heading("Changes the character made"),
            HelpTip.Explain("The How I react check-in lets the character change how it reacts to your touches for a while, for example when " +
                "it gets angry with you or warms up to you. Its changes never edit your zones or temperament, and each one ends on its " +
                "own. Undo a change to end it now. Turn the check-in off on Companion › Check-ins.", new Thickness(0, 0, 0, 10), "CharacterReactionChanges", "reaction changes")
        };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(status, "ReactionChangesStatus");
        stack.Add(status);
        var state = Note("", new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(state, "ReactionChangesState");
        AutomationProperties.SetLiveSetting(state, AutomationLiveSetting.Polite);
        ShowStatusLine(state, services.ReactionNote);
        services.ReactionNote = null;
        if (persona is null)
        {
            status.Text = "No persona is in use.";
            stack.Add(state);
            return Card([.. stack]);
        }
        var now = DateTimeOffset.UtcNow;
        var zones = services.Zones.Current;
        var catalog = services.Catalog;
        var mine = services.ReactionChanges.Saved.Where(c => c.PersonaId == persona.Id).ToArray();
        var active = mine.Where(c => c.ActiveAt(now)).OrderBy(c => c.At).ToArray();
        status.Text = active.Length switch
        {
            0 => $"{persona.Name} has no changes of its own in effect: touches play what you chose.",
            1 => $"{persona.Name} has 1 change of its own in effect.",
            _ => $"{persona.Name} has {active.Length} changes of its own in effect."
        };

        async Task EndAsync(string? id, string done)
        {
            var why = await services.ReactionChanges.EndAsync(id, persona.Id, CharacterReactionChange.ByOwner, services.Lifetime);
            services.MarkEdited(false);
            if (why is not null)
            {
                ShowStatusLine(state, "Not undone: " + why);
                state.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                return;
            }
            services.ReactionNote = done;
            if (!services.Closing && services.TouchOpen) services.Redraw();
        }

        var n = 0;
        foreach (var change in active)
        {
            n++;
            var what = CharacterReactionChanges.Describe(change, zones, catalog);
            var line = new TextBlock
            {
                Text = $"{what}, {CharacterReactionChanges.Until(change, now)}. Made {TouchPageModel.Made(change, now)}.",
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
                var line = Note($"{CharacterReactionChanges.Describe(change, zones, catalog)}. {TouchPageModel.Ended(change)}.", new Thickness(0, 4, 0, 0));
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
}
