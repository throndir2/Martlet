using System.Windows.Controls;
using Martlet.Core.Creations;
using Martlet.Discord;
using Martlet.Discord.Calls;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Discord (<see cref="DiscordPage"/> and <see cref="DiscordPageModel"/>) reads and starts in the app.
/// MainWindow gives it to the page (MainWindow.DiscordPage.cs). The page reads the bot (<see cref="Discord"/>) and the calls
/// (<see cref="Calls"/>) directly, and MainWindow keeps the work the page shows (rule 4 of Pages\README.md): saving and forgetting
/// the token, connecting, saving the setup, calling a friend, the camera view and the camera picture (MainWindow.Discord.cs,
/// MainWindow.Discord.Companion.cs and MainWindow.DiscordCalls.cs). The token box goes to the window as it is
/// (<see cref="PasswordBox"/>), so the token never becomes a string on the way.</summary>
internal interface IDiscordPageServices
{
    // ---------- what the page reads ----------

    /// <summary>Martlet's Discord bot: its setup, its state, the servers, channels and people it sees, and its friends.</summary>
    DiscordService Discord { get; }

    /// <summary>Martlet in your Discord calls: its setup, the outputs, the camera view and the camera picture.</summary>
    DiscordCallService Calls { get; }

    /// <summary>Martlet's data folder and the Creations folder, or null before Martlet is set up.</summary>
    string? DataDirectory { get; }

    string? CreationsFolder { get; }

    /// <summary>The last outcome of saving the token (never the token), or null.</summary>
    string? TokenStatus { get; }

    /// <summary>Whether a token save or a connect runs now.</summary>
    bool Busy { get; }

    /// <summary>The typed Discord user ID that isn't saved yet, kept while the page draws again.</summary>
    string? OwnerDraft { get; set; }

    /// <summary>The last outcome on Friends and calls.</summary>
    string FriendsResult { get; }

    /// <summary>The names of the people Martlet knows by voice (to say who a friend is).</summary>
    IReadOnlyList<string> VoiceNames();

    /// <summary>Whether Martlet can hear this PC (it can join calls), and whether always listening runs now.</summary>
    bool CanHearPc { get; }

    bool Listening { get; }

    /// <summary>Whether the character shows now (the camera view needs it).</summary>
    bool CharacterShowing { get; }

    /// <summary>The last Check this PC result, or null.</summary>
    string? CallDoctor { get; }

    /// <summary>What the last camera picture action did, or null (choosing a background clears it).</summary>
    string? CallPictureState { get; set; }

    /// <summary>What was typed to draw, kept while the page draws again, and whether a drawing runs now.</summary>
    string CallPicturePrompt { get; set; }

    bool CallPictureDrawing { get; }

    // ---------- what the page starts ----------

    void SaveToken(PasswordBox token, Button save);

    void Forget();

    void SetEnabled(bool on);

    /// <summary>Saves a change to the setup and says <paramref name="done"/> in the window's action line.</summary>
    void Save(Func<DiscordPreferences, DiscordPreferences> update, string done);

    void SaveOwner(string text);

    /// <summary>Shows a Friends and calls outcome and draws the page again.</summary>
    void FriendsDone(string message);

    void CallFriend(DiscordPerson person, TextBlock result);

    /// <summary>Takes a new picture of the character for the bot now; <paramref name="done"/> gets the outcome.</summary>
    void UpdatePictureNow(Action<string> done);

    /// <summary>Lists the audio outputs once, the first time the calls card shows.</summary>
    void ListCallOutputsOnce();

    void SaveCall(Func<DiscordCallPreferences, DiscordCallPreferences> update);

    void RefreshCallCamera();

    void ToggleCallCamera();

    void FrameCallCamera(string action);

    void CheckCall();

    void ChooseCallPicture();

    void UseCreationPicture(Creation creation);

    void DrawCallPicture(string instruction);

    void OpenInBrowser(Uri address);
}
