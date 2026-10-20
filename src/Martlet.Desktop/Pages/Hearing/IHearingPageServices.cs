using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Hearing (<see cref="HearingPage"/> and <see cref="HearingPageModel"/>) reads and changes in the
/// app. MainWindow gives it to the page when it makes the page (MainWindow.HearingPage.cs). It has no WPF types, so the page model
/// can use it too. The audio models' list and its Now lines are shared with Companion › Vision and built on the shared pool list,
/// and Hear how you say it is part of how you talk (MainWindow.Talk.cs); MainWindow gives them to the page as builders of their own
/// (see <see cref="HearingPage"/>).</summary>
internal interface IHearingPageServices
{
    // ---------- what the page reads ----------

    /// <summary>How you talk with Martlet now (talk-preferences.json): whether you let a model hear your voice.</summary>
    TalkPreferences Talk { get; }

    /// <summary>The saved settings (the Thinking route), or null before they are loaded.</summary>
    AppSettings? Settings { get; }

    /// <summary>What each model reads and writes, as saved.</summary>
    ModelAbilities SavedModelAbilities();

    /// <summary>Where a recording with a reply goes (<see cref="SenseRouting"/>): to Thinking, to an audio model, or nowhere.</summary>
    SenseRoute AudioRoute(SetupRoute? thinking, ModelAbilities abilities);

    // ---------- what the page does ----------

    /// <summary>Shows <paramref name="text"/> in the window's status line.</summary>
    void ShowStatus(string text);

    /// <summary>Saves <paramref name="next"/> as how you talk, gives it to an open conversation and, with
    /// <paramref name="render"/>, draws the page again.</summary>
    void SaveTalk(TalkPreferences next, bool render = false);
}
