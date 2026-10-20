using System.Windows;

namespace Martlet.Desktop;

/// <summary>Everything Companion › People (<see cref="PeoplePage"/> and <see cref="PeoplePageModel"/>) reads and starts in the app.
/// MainWindow gives it to the page (MainWindow.PeoplePage.cs). MainWindow keeps the work the page shows (rule 4 of
/// Pages\README.md): keeping the voice list the same on your computers (MainWindow.People.cs), voice recognition's place and its
/// Thinking pool list (MainWindow.VoiceRecognition.cs), the Now card that Home shares (MainWindow.PageNow.cs), playing a clip and
/// the Memory window. The cards the window makes come to the page as builders, as Vision's do.</summary>
internal interface IPeoplePageServices
{
    // ---------- what the page reads ----------

    /// <summary>The voices Martlet knows on this PC, their names and clips, and the choices about them.</summary>
    LocalVoices Voices { get; }

    /// <summary>Whether the voice list is syncing with your other computers now, and what the last sync did.</summary>
    bool SyncBusy { get; }

    string SyncStatus { get; }

    /// <summary>The name of the account <paramref name="account"/> (yours or someone's in the household), or null.</summary>
    string? AccountName(Guid account);

    /// <summary>Whether the window is closing (nothing more is saved or drawn).</summary>
    bool Closing { get; }

    // ---------- the cards the window makes ----------

    /// <summary>Now (PeopleNow), which Home's page summary shares.</summary>
    UIElement NowCard();

    /// <summary>Where voice recognition runs (PeopleRecognitionWhere), with its Check it now.</summary>
    UIElement RecognitionWhere();

    /// <summary>The Voice recognition list of places (the shared Thinking pool list control).</summary>
    UIElement RecognitionPoolCard();

    // ---------- what the page starts ----------

    /// <summary>Sync now: reads every paired host's copy of the voice list and merges it here.</summary>
    void SyncNow();

    /// <summary>Shares a changed choice (recognition on or off, keeping clips) with your other computers soon.</summary>
    void QueueSettingsSync();

    /// <summary>Plays one clip of a voice.</summary>
    void PlayClip(VoiceClip clip);

    /// <summary>Opens the Memory window at what Martlet remembers about the voice <paramref name="voiceId"/>.</summary>
    void OpenMemory(string voiceId);

    /// <summary>Asks the owner <paramref name="question"/>; true when they press <paramref name="yes"/>.</summary>
    bool Confirm(string question, string yes);

    /// <summary>Shows <paramref name="text"/> on the window's status line.</summary>
    void Say(string text);

    /// <summary>Draws the page again now.</summary>
    void Redraw();

    /// <summary>Draws the page again after the click returns, while it is still open.</summary>
    void RedrawLater();
}
