using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Eyes (<see cref="EyesPage"/> and <see cref="EyesPageModel"/>) reads and starts in the app.
/// MainWindow gives it to the page (MainWindow.EyesPage.cs) and keeps the work the page shows: where the character looks
/// (<see cref="CharacterGazeService"/> and the character's Eyes menu, MainWindow.CharacterGaze.cs) and measuring the face, which also
/// runs on its own for a new model (MainWindow.CharacterEyes.cs). It has no WPF types.</summary>
internal interface IEyesPageServices
{
    // ---------- where the character looks ----------

    /// <summary>How you talk with Martlet now (talk-preferences.json): the usual gaze and whether replies may change it.</summary>
    TalkPreferences Talk { get; }

    /// <summary>Saves <paramref name="next"/> as how you talk, gives it to the character and draws the page again.</summary>
    void SaveTalk(TalkPreferences next);

    /// <summary>The gaze the active persona's touch temperament decided, or null.</summary>
    GazeMode? PersonalityGaze { get; }

    /// <summary>Where the character looks now, in words.</summary>
    string Looking { get; }

    /// <summary>What the last reply did with the eyes, or null.</summary>
    string? LastLook { get; }

    // ---------- where the face is ----------

    /// <summary>Whether the character shows on the desktop.</summary>
    bool Showing { get; }

    /// <summary>Where the showing model's eyes come from (<see cref="RendererEyesFrom"/>), or null.</summary>
    string? EyesFrom { get; }

    /// <summary>The face measurement of the model the page follows, its progress and its pictures.</summary>
    CharacterEyeService Eyes { get; }

    /// <summary>Whether Measure the face runs now.</summary>
    bool Measuring { get; }

    /// <summary>Whether a conversation is there to ask the Thinking model.</summary>
    bool CanAsk { get; }

    /// <summary>The emotes and motions of the model Companion shows, or null while they are read.</summary>
    CharacterActionCatalog? Catalog { get; }

    /// <summary>Whether a model can see the close-up, the line that says which, and why not.</summary>
    (bool CanSee, string Line, string? Off) Sight();

    /// <summary>Follows the model Companion shows (its emotes and motions), before the page draws.</summary>
    void FollowActions();

    /// <summary>Measures the face now, as the owner asked.</summary>
    void Measure();

    /// <summary>Deletes this model's measurement and its pictures.</summary>
    void Forget();
}
