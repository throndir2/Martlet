using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Replies (<see cref="RepliesPage"/> and <see cref="RepliesPageModel"/>) reads and starts in the
/// app. MainWindow gives it to the page (MainWindow.RepliesPage.cs). MainWindow keeps the saving of the reply settings
/// (<c>SaveRepliesAsync</c>, which Thinking pool uses too) and the context check (MainWindow.ContextCheck.cs), as rule 4 of
/// Pages\README.md says.</summary>
internal interface IRepliesPageServices
{
    // ---------- what the page reads ----------

    /// <summary>The saved settings (the Thinking route and the reply settings), or null before they are loaded.</summary>
    AppSettings? Settings { get; }

    /// <summary>Where <paramref name="route"/> runs, in words ("this PC", a host's name, a provider).</summary>
    string PlaceName(SetupRoute route);

    /// <summary>The context size line of <paramref name="route"/> (RepliesContextStatus): what the model takes and what is set.</summary>
    string ContextStatus(SetupRoute route, string place);

    // ---------- what the page starts ----------

    /// <summary>Opens another Companion page.</summary>
    void OpenCompanion(CompanionTab tab);

    /// <summary>Gives the window the page's auto-save, so leaving the page saves what waits.</summary>
    void UseAutoSave(AutoSave autoSave);

    /// <summary>Gives the window what draws the context size line again (after a context check or a save).</summary>
    void ShowContextStatusWith(Action show);

    /// <summary>Draws the context size line again, when a page gave the window one.</summary>
    void RefreshContextStatus();

    /// <summary>Check model limit: asks the Thinking model's server for its context window.</summary>
    void CheckContext();

    /// <summary>Saves <paramref name="generation"/> (keeping Thinking longer as saved) into the newest saved settings, then calls
    /// <paramref name="saved"/>. False: another window saved the settings meanwhile, so try again shortly.</summary>
    Task<bool> Save(GenerationSettings generation, Action<GenerationSettings?> saved);

    /// <summary>Tells the window that the page has edits that are not saved yet.</summary>
    void MarkEdited();

    /// <summary>Shows <paramref name="text"/> on the window's status line.</summary>
    void Say(string text);
}
