using Martlet.Avatar.Hosting;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Touch (<see cref="TouchPage"/>) reads and starts in the app, beyond its Touch zones card's
/// (<see cref="ITouchZonesPageServices"/>). MainWindow gives it to the page (MainWindow.TouchPage.cs) and keeps the work the page
/// shows: deciding the temperament with the Thinking model (MainWindow.CharacterTemperament.cs) and the services that save the
/// temperaments and the character's changes. It has no WPF types.</summary>
internal interface ITouchPageServices : ITouchZonesPageServices
{
    /// <summary>The persona in use, and its ID as saved (null before the settings are loaded).</summary>
    PersonaProfile? ActivePersona { get; }

    Guid? ActivePersonaId { get; }

    /// <summary>Every persona, in Personality's order.</summary>
    IReadOnlyList<PersonaProfile> Personas { get; }

    /// <summary>The personas' touch temperaments and the custom temperaments.</summary>
    CharacterTemperamentService Temperaments { get; }

    /// <summary>What the character changed itself about how it reacts to touches.</summary>
    CharacterReactionChangeService ReactionChanges { get; }

    /// <summary>Whether the character shows on the desktop.</summary>
    bool AvatarShowing { get; }

    /// <summary>Whether the window is closing: the page then draws nothing again.</summary>
    bool Closing { get; }

    /// <summary>Follows the model Companion shows (its emotes and motions), before the page draws.</summary>
    void FollowActions();

    /// <summary>Whether Re-decide from personality runs now.</summary>
    bool Deciding { get; }

    /// <summary>Asks the Thinking model to decide the active persona's temperament from its personality.</summary>
    void Decide();

    /// <summary>What the last Uses, Create, Rename or Delete did (or why not), for the next card's line.</summary>
    string? UseNote { get; set; }

    /// <summary>What the last Undo did (or why not), for the next card's line.</summary>
    string? ReactionNote { get; set; }
}
