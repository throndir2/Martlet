using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>What the check-in runner knows about one check-in now, for its status line.</summary>
/// <param name="Running">It runs now (on its pace or because a trigger fired).</param>
/// <param name="TriggeredRunning">It runs now because a trigger fired.</param>
/// <param name="Wait">Why it waits, or null.</param>
/// <param name="Fired">A trigger that fired and waits for it, or null.</param>
/// <param name="Last">Its last run since Martlet started, or null.</param>
internal sealed record CheckInRunState(bool Running, bool TriggeredRunning, string? Wait, CheckInTrigger? Fired, CheckInRun? Last,
    int Runs, int Acted);

/// <summary>Everything Companion › Check-ins (<see cref="CheckInsPage"/> and <see cref="CheckInsPageModel"/>) reads and changes in
/// the app. The check-in runner stays in MainWindow (MainWindow.CheckIns.cs); MainWindow gives the page these services when it
/// makes the page (MainWindow.CheckInsPage.cs). It has no WPF types, so the page model can use it too.</summary>
internal interface ICheckInsPageServices
{
    // ---------- what the page reads ----------

    /// <summary>check-ins.json as the runner uses it now.</summary>
    CheckInSettings Choices { get; }

    /// <summary>The saved prompts (a built-in check-in's prompt and a copy's task come from them), or null.</summary>
    PromptSettings? Prompts { get; }

    /// <summary>Martlet's data folder (thinking-pool.json is in it), or null before Martlet is set up.</summary>
    string? DataDirectory { get; }

    /// <summary>Whether this PC is a companion PC: check-ins run only there.</summary>
    bool IsCompanion { get; }

    /// <summary>Whether Martlet can talk on this PC (it has a live conversation).</summary>
    bool CanTalk { get; }

    /// <summary>The live conversation's Thinking pool, or null when Martlet can't talk on this PC.</summary>
    DesktopThinkingPool? Pool { get; }

    /// <summary>Whether a test fixture (<see cref="MainWindow.CheckInFixtureVariable"/>) answers the check-ins.</summary>
    bool UsesFixture { get; }

    /// <summary>What the runner knows about the check-in <paramref name="id"/> now.</summary>
    CheckInRunState RunState(string id);

    /// <summary>The last check-in that ran since Martlet started, or null.</summary>
    (string Name, CheckInRun Run)? LastRun { get; }

    // ---------- what the page does ----------

    /// <summary>Whether the window is closing: the page then starts nothing new.</summary>
    bool Closing { get; }

    /// <summary>Shows <paramref name="text"/> in the window's status line.</summary>
    void ShowStatus(string text);

    /// <summary>Draws the open Companion page again.</summary>
    void Redraw();

    /// <summary>Marks the open page as edited, so a background change doesn't draw it again under the owner's typing.</summary>
    void MarkEdited();

    /// <summary>Makes <paramref name="autoSave"/> the open page's auto-save, saved at once when another page opens.</summary>
    void UseAutoSave(AutoSave autoSave);

    /// <summary>The open page's auto-save, or null.</summary>
    AutoSave? OpenAutoSave { get; }

    /// <summary>Makes the live conversation read thinking-pool.json once, so the Now line knows the pool's members.</summary>
    void ReadThinkingPoolOnce();

    /// <summary>Opens Companion › Background thinking.</summary>
    void OpenThinkingPool();

    /// <summary>Notes why each check-in waits now, unless one runs; nothing runs.</summary>
    void LookNow();

    /// <summary>Check now: runs the check-in <paramref name="id"/> at once and says what came of it.</summary>
    Task CheckNowAsync(string id);

    /// <summary>Saves check-ins.json and uses it at once; says <paramref name="said"/>, or why it can't save. True when saved.</summary>
    bool Save(CheckInSettings next, string? said);

    /// <summary>Forgets the runs, waits and triggers of a removed check-in, so a new one with its ID starts with none.</summary>
    void Forget(string id);

    /// <summary>Saves the built-in check-in prompts in <paramref name="edits"/> (by prompt ID) into the newest saved settings;
    /// <paramref name="saved"/> runs once they are saved. False: try again shortly (another window saved the settings meanwhile).</summary>
    Task<bool> SavePromptsAsync(IReadOnlyDictionary<string, string> edits, Action saved);
}
