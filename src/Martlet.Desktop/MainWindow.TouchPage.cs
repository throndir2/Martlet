using Martlet.Avatar.Hosting;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › Touch (<see cref="TouchPage"/> in Pages\Touch), beyond its Touch zones card's
/// (MainWindow.TouchZonesPage.cs).</summary>
public partial class MainWindow : ITouchPageServices
{
    private TouchPage? touchPage;

    /// <summary>The Touch page, made once.</summary>
    private TouchPage TouchPage => touchPage ??= new(this, CharacterTouchZonesCard, AddRow);

    PersonaProfile? ITouchPageServices.ActivePersona => homeSettings?.Companion?.ActivePersona;
    Guid? ITouchPageServices.ActivePersonaId => homeSettings?.Companion?.ActivePersonaId;
    IReadOnlyList<PersonaProfile> ITouchPageServices.Personas => homeSettings?.Companion?.Personas ?? [];
    CharacterTemperamentService ITouchPageServices.Temperaments => characterTemperaments;
    CharacterReactionChangeService ITouchPageServices.ReactionChanges => characterReactionChanges;
    bool ITouchPageServices.AvatarShowing => avatar.IsShowing;
    bool ITouchPageServices.Closing => closing;
    void ITouchPageServices.FollowActions() => FollowCharacterActions();
    bool ITouchPageServices.Deciding => decidingTemperament;
    void ITouchPageServices.Decide() => DecideTemperamentAsync().Forget();
    string? ITouchPageServices.UseNote
    {
        get => temperamentUseNote;
        set => temperamentUseNote = value;
    }
    string? ITouchPageServices.ReactionNote
    {
        get => reactionChangesNote;
        set => reactionChangesNote = value;
    }
}
