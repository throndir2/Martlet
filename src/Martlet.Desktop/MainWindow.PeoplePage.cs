using System.Windows;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › People (<see cref="PeoplePage"/> in Pages\People). Keeping the voice list the
/// same on your computers stays in the window (MainWindow.People.cs), and so do voice recognition's place and list
/// (MainWindow.VoiceRecognition.cs) and the Now card (MainWindow.PageNow.cs).</summary>
public partial class MainWindow : IPeoplePageServices
{
    LocalVoices IPeoplePageServices.Voices => localVoices;
    bool IPeoplePageServices.SyncBusy => voiceSyncBusy;
    string IPeoplePageServices.SyncStatus => voiceSyncStatus;
    string? IPeoplePageServices.AccountName(Guid account) => VoiceAccountName(account);
    bool IPeoplePageServices.Closing => closing;

    UIElement IPeoplePageServices.NowCard() => PeopleNowCard();
    UIElement IPeoplePageServices.RecognitionWhere() => VoiceRecognitionWhere();
    UIElement IPeoplePageServices.RecognitionPoolCard() => VoiceRecognitionPoolCard();

    void IPeoplePageServices.SyncNow() => SyncVoicesAsync().Forget();
    void IPeoplePageServices.QueueSettingsSync() => QueueSettingsSync();
    void IPeoplePageServices.PlayClip(VoiceClip clip) => PlayClip(clip);
    void IPeoplePageServices.OpenMemory(string voiceId) => OpenMemoryAsync(voiceId).Forget();
    bool IPeoplePageServices.Confirm(string question, string yes) => ConfirmationDialog.Confirm(this, question, yes);
    void IPeoplePageServices.Say(string text) => ActionText.Text = text;
    void IPeoplePageServices.Redraw() => RenderTab();
    void IPeoplePageServices.RedrawLater() => Dispatcher.BeginInvoke(() => { if (!closing && openTab == CompanionTab.People) RenderTab(); });
}
