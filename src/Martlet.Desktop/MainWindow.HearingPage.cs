using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › Hearing (<see cref="HearingPage"/> in Pages\Hearing), and the parts of the
/// page that stay in the window: the audio models' list (<see cref="SensePoolCard"/>, MainWindow.SensePools.cs) and its Now lines
/// (<see cref="SenseNowLines"/>, MainWindow.SenseModels.cs), which Companion › Vision shares, and Hear how you say it
/// (<see cref="HearVoiceCard"/>, MainWindow.Talk.cs).</summary>
public partial class MainWindow : IHearingPageServices
{
    private HearingPage? hearingPage;

    /// <summary>Companion › Hearing, made once.</summary>
    private HearingPage HearingPage => hearingPage ??= new(this, () => SensePoolCard(SenseKind.Audio), () => SenseNowLines(SenseKind.Audio),
        HearVoiceCard);

    TalkPreferences IHearingPageServices.Talk => Talk;
    AppSettings? IHearingPageServices.Settings => homeSettings;
    ModelAbilities IHearingPageServices.SavedModelAbilities() => SavedModelAbilities();
    SenseRoute IHearingPageServices.AudioRoute(SetupRoute? thinking, ModelAbilities abilities) =>
        SenseRouting.For(SenseKind.Audio, SavedSenses(), thinking, abilities);

    void IHearingPageServices.ShowStatus(string text) => ActionText.Text = text;
    void IHearingPageServices.SaveTalk(TalkPreferences next, bool render) => SaveTalk(next, render);
}