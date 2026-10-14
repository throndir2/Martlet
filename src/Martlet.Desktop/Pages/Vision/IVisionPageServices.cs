using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Home;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Vision (<see cref="VisionPage"/> and <see cref="VisionPageModel"/>) reads and changes in the
/// app. MainWindow gives it to the page when it makes the page (MainWindow.VisionPage.cs). It has no WPF types, so the page model
/// can use it too. The image models' list and its Now lines are shared with Companion › Hearing and built on the shared pool
/// list; MainWindow gives them to the page as two builders of their own (see <see cref="VisionPage"/>).</summary>
internal interface IVisionPageServices
{
    // ---------- what the page reads ----------

    /// <summary>How you talk with Martlet now (talk-preferences.json): whether vision is on, what it looks at, the comments, the
    /// glances and the screen summary.</summary>
    TalkPreferences Talk { get; }

    /// <summary>The saved settings (the Thinking route), or null before they are loaded.</summary>
    AppSettings? Settings { get; }

    /// <summary>Martlet's data folder (thinking-pool.json is in it), or null before Martlet is set up.</summary>
    string? DataDirectory { get; }

    /// <summary>What each model reads and writes, as saved.</summary>
    ModelAbilities SavedModelAbilities();

    /// <summary>Where a picture with a reply goes (<see cref="SenseRouting"/>): to Thinking, to an image model, or nowhere.</summary>
    SenseRoute ImageRoute(SetupRoute? thinking, ModelAbilities abilities);

    /// <summary>Which model takes <paramref name="job"/>, as the conversation decides it (<see cref="SenseJobRouting"/>).</summary>
    SenseDecision SenseDecision(SenseJobKind job);

    /// <summary>The camera address typed this session, with its password if it has one (never saved), or null.</summary>
    string? TypedAddress { get; set; }

    /// <summary>The comments level Martlet picked itself, while a conversation runs.</summary>
    Chattiness? DecidedChattiness { get; }

    /// <summary>The character's usual gaze in words, such as "The character follows your mouse."</summary>
    string UsualGaze { get; }

    /// <summary>What the character's gaze does now, while Martlet decides it.</summary>
    string GazeNow { get; }

    /// <summary>Whether Home Assistant is connected, so its cameras can be used.</summary>
    bool SmartHomeConnected { get; }

    // ---------- what the page does ----------

    /// <summary>Whether the window is closing: the page then starts nothing new.</summary>
    bool Closing { get; }

    /// <summary>Cancelled when the window closes.</summary>
    CancellationToken Lifetime { get; }

    /// <summary>Shows <paramref name="text"/> in the window's status line.</summary>
    void ShowStatus(string text);

    /// <summary>Draws the open Companion page again.</summary>
    void Redraw();

    /// <summary>Draws the page again when the window isn't closing and the Vision page is still open.</summary>
    void RedrawIfOpen();

    /// <summary>Saves <paramref name="next"/> as how you talk, gives it to an open conversation and, with
    /// <paramref name="render"/>, draws the page again.</summary>
    void SaveTalk(TalkPreferences next, bool render = false);

    /// <summary>Home Assistant's cameras and their snapshot addresses.</summary>
    Task<IReadOnlyList<(HomeCamera Camera, Uri Snapshot)>> HomeCamerasAsync(CancellationToken token);
}
