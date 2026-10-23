using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Prompts (<see cref="PromptsPage"/>) reads and starts in the app. MainWindow gives it to the page
/// (MainWindow.PromptsPage.cs) and keeps the save, which writes the newest saved settings (rule 4 of Pages\README.md).</summary>
internal interface IPromptsPageServices
{
    /// <summary>The saved prompt edits, or null when every prompt uses Martlet's built-in text.</summary>
    PromptSettings? Saved { get; }

    /// <summary>Gives the window the page's auto-save, so a change still waiting is saved when another page opens or Martlet closes.</summary>
    void UseAutoSave(AutoSave autoSave);

    /// <summary>Tells the window that the page has edits that are not saved yet.</summary>
    void MarkEdited();

    /// <summary>The saved skills (Companion › Skills), or null when every skill is built in.</summary>
    SkillSettings? Skills { get; }

    /// <summary>The active persona's name and text, for what every reply sends.</summary>
    (string Name, string Text) Persona { get; }

    /// <summary>What the last reply to you sent besides the conversation, or null before the first reply.</summary>
    ReplyPromptRecord? LastReply { get; }

    /// <summary>Opens Companion › Skills.</summary>
    void OpenSkills();

    /// <summary>Saves the prompts as typed (<paramref name="texts"/>: each prompt's ID and text) into the newest saved settings and calls
    /// <paramref name="saved"/> with what was saved. False: try again shortly (another window saved the settings meanwhile).</summary>
    Task<bool> SaveAsync(IReadOnlyDictionary<string, string> texts, Action<PromptSettings?> saved);
}
