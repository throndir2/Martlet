using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Touch › Touch zones (<see cref="TouchZonesPage"/> and <see cref="TouchZonesPageModel"/>) reads
/// and changes in the app. MainWindow gives it to the page when it makes the page (MainWindow.TouchZonesPage.cs). It has no WPF
/// types, so the page model can use it too. MainWindow keeps the work the page shows: what a touch plays, Detect zones, the first
/// guess, Try and Start over's reset (MainWindow.CharacterTouchZones.cs and MainWindow.CharacterTouchReset.cs).</summary>
internal interface ITouchZonesPageServices
{
    // ---------- what the page reads ----------

    /// <summary>The zones of the model the page follows, their detection and what the last touches did.</summary>
    CharacterTouchZoneService Zones { get; }

    /// <summary>The emotes and motions of the model Companion shows, or null while they are read.</summary>
    CharacterActionCatalog? Catalog { get; }

    /// <summary>The active persona's touch temperament, or null.</summary>
    CharacterTouchTemperament? Temperament { get; }

    /// <summary>The saved settings (the Thinking route), or null before they are loaded.</summary>
    AppSettings? Settings { get; }

    /// <summary>What each model reads and writes, as saved.</summary>
    ModelAbilities SavedModelAbilities();

    /// <summary>Which model takes <paramref name="job"/>, as the conversation decides it (<see cref="SenseJobRouting"/>).</summary>
    SenseDecision SenseDecision(SenseJobKind job);

    /// <summary>How you talk with Martlet now (talk-preferences.json): what a touch does while Martlet talks.</summary>
    TalkPreferences Talk { get; }

    /// <summary>Whether the character shows a model Martlet knows, so Try can play on it.</summary>
    bool CharacterShowing { get; }

    /// <summary>What the last touch line says before a touch: how to try a zone.</summary>
    string TouchIdle { get; }

    /// <summary>The sounds the voice makes alone, as Add a reaction lists them.</summary>
    IEnumerable<(string Id, string Label)> VoiceSoundItems();

    /// <summary>Whether the page is drawn because it was just opened (not drawn again).</summary>
    bool Opening { get; }

    /// <summary>Whether Companion › Touch is the open page.</summary>
    bool TouchOpen { get; }

    /// <summary>Cancelled when the window closes.</summary>
    CancellationToken Lifetime { get; }

    // ---------- Detect zones ----------

    /// <summary>Whether a conversation is there to ask the Thinking model.</summary>
    bool CanDetect { get; }

    /// <summary>Whether Detect zones runs now.</summary>
    bool Detecting { get; }

    /// <summary>The running Detect zones, which Stop cancels, or null.</summary>
    CancellationTokenSource? DetectRun { get; }

    /// <summary>Starts Detect zones.</summary>
    void Detect();

    // ---------- what the page does ----------

    /// <summary>Tells the window that the page has edits that are not saved yet (the window then holds back its updates).</summary>
    void MarkEdited(bool edited);

    /// <summary>Draws the open Companion page again.</summary>
    void Redraw();

    /// <summary>Saves <paramref name="next"/> as how you talk and gives it to an open conversation.</summary>
    void SaveTalk(TalkPreferences next);

    /// <summary>Whether the zones show on the character, for this session.</summary>
    bool ShowZonesOnCharacter { get; set; }

    /// <summary>Gives the showing character its zones as they are now.</summary>
    void SendZoneView();

    /// <summary>Plays <paramref name="zone"/>'s reaction list on the character.</summary>
    void TryZone(CharacterTouchZone zone, CharacterActionCatalog catalog);

    /// <summary>Plays the voice sound <paramref name="cue"/> (made first when needed).</summary>
    void HearSound(string cue);

    // ---------- Start over ----------

    /// <summary>The reset level chosen in the list (kept while the page is drawn again).</summary>
    string ResetLevel { get; set; }

    /// <summary>What the last reset did, or null.</summary>
    string? ResetState { get; }

    /// <summary>Whether a reset runs now.</summary>
    bool Resetting { get; }

    /// <summary>Asks first, then resets the touch of the model with <paramref name="modelId"/> at the chosen level, after it saves
    /// <paramref name="edits"/>.</summary>
    void Reset(string modelId, AutoSave edits);
}
