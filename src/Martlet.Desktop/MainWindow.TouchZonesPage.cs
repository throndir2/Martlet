using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › Touch › Touch zones (<see cref="TouchZonesPage"/> in Pages\TouchZones). The
/// work the page shows stays in the window: what a touch plays, Detect zones and the first guess
/// (MainWindow.CharacterTouchZones.cs), and Start over's reset (MainWindow.CharacterTouchReset.cs).</summary>
public partial class MainWindow : ITouchZonesPageServices
{
    private TouchZonesPage? touchZonesPage;

    /// <summary>The Touch zones page, made once: it keeps the zone map's zoom and which rows show More while the window lives.</summary>
    private TouchZonesPage TouchZonesPage => touchZonesPage ??= new(this, AddRow, PhysicalLastLine, () => VoiceSoundsNote());

    /// <summary>The last stroke's line (CharacterPhysicalLast), which <see cref="ShowPhysicalLast"/> updates.</summary>
    private TextBlock PhysicalLastLine()
    {
        physicalLastText = Note(PhysicalLastText(), new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(physicalLastText, "CharacterPhysicalLast");
        AutomationProperties.SetLiveSetting(physicalLastText, AutomationLiveSetting.Polite);
        return physicalLastText;
    }

    CharacterTouchZoneService ITouchZonesPageServices.Zones => characterTouchZones;
    CharacterActionCatalog? ITouchZonesPageServices.Catalog => characterActions.Current;
    CharacterTouchTemperament? ITouchZonesPageServices.Temperament => characterTemperaments.For(homeSettings?.Companion?.ActivePersonaId);
    AppSettings? ITouchZonesPageServices.Settings => homeSettings;
    ModelAbilities ITouchZonesPageServices.SavedModelAbilities() => SavedModelAbilities();
    SenseDecision ITouchZonesPageServices.SenseDecision(SenseJobKind job) => SavedSenseDecision(job);
    TalkPreferences ITouchZonesPageServices.Talk => Talk;
    bool ITouchZonesPageServices.CharacterShowing => avatar.IsShowing && characterActions.For(avatar.InspectedProfile?.ModelPath) is not null;
    string ITouchZonesPageServices.TouchIdle => TouchZonesIdle();
    IEnumerable<(string Id, string Label)> ITouchZonesPageServices.VoiceSoundItems() => VoiceSoundItems();
    bool ITouchZonesPageServices.Opening => openingTab;
    bool ITouchZonesPageServices.TouchOpen => openTab == CompanionTab.Touch;
    CancellationToken ITouchZonesPageServices.Lifetime => lifetime.Token;

    bool ITouchZonesPageServices.CanDetect => conversation is not null;
    bool ITouchZonesPageServices.Detecting => detectingTouchZones;
    CancellationTokenSource? ITouchZonesPageServices.DetectRun => detectTouchZones;
    void ITouchZonesPageServices.Detect() => DetectTouchZonesAsync().Forget();

    void ITouchZonesPageServices.MarkEdited(bool edited) => tabEdited = edited;
    void ITouchZonesPageServices.Redraw() => RenderTab();
    void ITouchZonesPageServices.SaveTalk(TalkPreferences next) => SaveTalk(next);
    bool ITouchZonesPageServices.ShowZonesOnCharacter
    {
        get => showZonesOnCharacter;
        set => showZonesOnCharacter = value;
    }
    void ITouchZonesPageServices.SendZoneView() => SendTouchZoneView();
    void ITouchZonesPageServices.TryZone(CharacterTouchZone zone, CharacterActionCatalog catalog) => TryTouchZone(zone, catalog);
    void ITouchZonesPageServices.HearSound(string cue) => HearTouchSound(cue);

    string ITouchZonesPageServices.ResetLevel
    {
        get => touchResetLevel;
        set => touchResetLevel = value;
    }
    string? ITouchZonesPageServices.ResetState => touchResetState;
    bool ITouchZonesPageServices.Resetting => resettingTouch;
    void ITouchZonesPageServices.Reset(string modelId, AutoSave edits) => ResetTouchAsync(modelId, edits).Forget();
}
