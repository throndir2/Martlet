using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Home;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › Vision (<see cref="VisionPage"/> in Pages\Vision), and the two parts of the
/// page that Companion › Hearing shares: the image models' list (<see cref="SensePoolCard"/>, MainWindow.SensePools.cs) and its
/// Now lines (<see cref="SenseNowLines"/>, MainWindow.SenseModels.cs).</summary>
public partial class MainWindow : IVisionPageServices
{
    private VisionPage? visionPage;

    /// <summary>Companion › Vision. One page for the window's life: it keeps the cameras it found.</summary>
    private VisionPage VisionPage => visionPage ??= new(this, () => SensePoolCard(SenseKind.Image), () => SenseNowLines(SenseKind.Image));

    TalkPreferences IVisionPageServices.Talk => Talk;
    AppSettings? IVisionPageServices.Settings => homeSettings;
    string? IVisionPageServices.DataDirectory => store?.DataDirectory;
    ModelAbilities IVisionPageServices.SavedModelAbilities() => SavedModelAbilities();
    SenseRoute IVisionPageServices.ImageRoute(SetupRoute? thinking, ModelAbilities abilities) => SavedImageRoute(thinking, abilities);
    SenseDecision IVisionPageServices.SenseDecision(SenseJobKind job) => SavedSenseDecision(job);
    string? IVisionPageServices.TypedAddress { get => visionAddress; set => visionAddress = value; }
    Chattiness? IVisionPageServices.DecidedChattiness => conversation?.DecidedChattiness;
    string IVisionPageServices.UsualGaze => $"The character {CharacterGazeService.Describe(avatar.Gaze.Settings.Mode)}.";
    string IVisionPageServices.GazeNow => avatar.Gaze.Status;
    bool IVisionPageServices.SmartHomeConnected => smartHome.Connected;

    bool IVisionPageServices.Closing => closing;
    CancellationToken IVisionPageServices.Lifetime => lifetime.Token;
    void IVisionPageServices.ShowStatus(string text) => ActionText.Text = text;
    void IVisionPageServices.Redraw() => RenderTab();
    void IVisionPageServices.RedrawIfOpen()
    {
        if (!closing && openTab == CompanionTab.Vision) RenderTab();
    }
    void IVisionPageServices.SaveTalk(TalkPreferences next, bool render) => SaveTalk(next, render);
    Task<IReadOnlyList<(HomeCamera Camera, Uri Snapshot)>> IVisionPageServices.HomeCamerasAsync(CancellationToken token) =>
        smartHome.CamerasAsync(token);
}
