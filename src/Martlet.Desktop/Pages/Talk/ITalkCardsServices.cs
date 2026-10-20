using System.Windows.Controls;

namespace Martlet.Desktop;

/// <summary>Everything the Companion › Voice talk cards (<see cref="TalkCards"/>) read and start in the app. MainWindow gives it
/// to the cards (MainWindow.ListeningCards.cs) and keeps the work they start (rule 4 of Pages\README.md): saving the talk
/// choices and making the quick sounds. The controls that follow the conversation go back to the window, so it can update
/// them while the page shows.</summary>
internal interface ITalkCardsServices
{
    /// <summary>The saved talk choices.</summary>
    TalkPreferences Talk { get; }

    /// <summary>Saves <paramref name="next"/>, and with <paramref name="render"/> draws the page again.</summary>
    void SaveTalk(TalkPreferences next, bool render = false);

    /// <summary>Keeps the Speak Martlet's replies aloud box, so Mute voice on the character's menu ticks it.</summary>
    void KeepSpeakRepliesCheck(CheckBox check);

    /// <summary>Keeps the quick sounds status line and its Make button and shows how the quick sounds stand now.</summary>
    void KeepQuickSoundsLine(TextBlock status, Button make);

    /// <summary>Make quick sounds now.</summary>
    void MakeQuickSounds();
}
