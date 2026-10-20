using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › Eyes (<see cref="EyesPage"/> in Pages\Eyes). The work the page shows stays in
/// the window: where the character looks and its Eyes menu (MainWindow.CharacterGaze.cs), and measuring the face, which also runs
/// on its own for a new model (MainWindow.CharacterEyes.cs).</summary>
public partial class MainWindow : IEyesPageServices
{
    private EyesPage? eyesPage;

    /// <summary>The Eyes page, made once: the window updates the lines of the page last drawn.</summary>
    private EyesPage EyesPage => eyesPage ??= new(this);

    TalkPreferences IEyesPageServices.Talk => Talk;
    void IEyesPageServices.SaveTalk(TalkPreferences next) => SaveTalk(next, render: true);
    GazeMode? IEyesPageServices.PersonalityGaze => characterTemperaments.For(homeSettings?.Companion?.ActivePersonaId)?.Gaze;
    string IEyesPageServices.Looking => avatar.Gaze.Looking;
    string? IEyesPageServices.LastLook => avatar.Gaze.LastLookText;

    bool IEyesPageServices.Showing => avatar.IsShowing;
    string? IEyesPageServices.EyesFrom => avatar.EyesFrom;
    CharacterEyeService IEyesPageServices.Eyes => characterEyes;
    bool IEyesPageServices.Measuring => measuringEyes;
    bool IEyesPageServices.CanAsk => conversation is not null;
    CharacterActionCatalog? IEyesPageServices.Catalog => characterActions.Current;
    (bool CanSee, string Line, string? Off) IEyesPageServices.Sight() => EyesSight();
    void IEyesPageServices.FollowActions() => FollowCharacterActions();
    void IEyesPageServices.Measure() => MeasureEyesAsync(automatically: false).Forget();
    void IEyesPageServices.Forget() => ForgetEyesAsync().Forget();
}
