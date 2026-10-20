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
}
