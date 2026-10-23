using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Skills (<see cref="SkillsPage"/>) reads and starts in the app. MainWindow gives it to the page
/// (MainWindow.SkillsPage.cs) and keeps the save, which writes the newest saved settings (rule 4 of Pages\README.md).</summary>
internal interface ISkillsPageServices
{
    /// <summary>The saved skills, or null when every skill is built in and there is none of the user's own.</summary>
    SkillSettings? Saved { get; }

    /// <summary>Gives the window the page's auto-save, so a change still waiting is saved when another page opens or Martlet closes.</summary>
    void UseAutoSave(AutoSave autoSave);

    /// <summary>Tells the window that the page has edits that are not saved yet.</summary>
    void MarkEdited();

    /// <summary>Saves the skills as typed (every skill, built in or the user's own, in order) into the newest saved settings and
    /// calls <paramref name="saved"/> with what was saved. False: try again shortly (another window saved the settings meanwhile).</summary>
    Task<bool> SaveAsync(IReadOnlyList<Skill> skills, Action<SkillSettings?> saved);

    /// <summary>Draws the page again (after a skill was added or deleted and saved).</summary>
    void Refresh();

    /// <summary>Opens Find skills (<see cref="SkillDirectoryWindow"/>) over the window and waits until it closes. <paramref name="add"/>
    /// saves a skill found there; <paramref name="current"/> gives every skill the user has now.</summary>
    void ShowSkillDirectory(Func<Skill, Task<string?>> add, Func<IReadOnlyList<Skill>> current);
}
