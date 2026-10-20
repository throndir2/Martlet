using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Emotes and motions (<see cref="EmotesPage"/>) reads and starts in the app. MainWindow gives it to
/// the page (MainWindow.EmotesPage.cs) and keeps the work that other pages and the conversation use (rule 4 of Pages\README.md): the
/// character action catalog and its saved file (<see cref="CharacterActionService"/>), following the shown model and naming its
/// emotes with the Thinking model (MainWindow.CharacterActions.cs), playing emotes on the character, and the kept motions
/// (MainWindow.CharacterMotions.cs). It has no WPF types.</summary>
internal interface IEmotesPageServices
{
    // ---------- what the page reads ----------

    /// <summary>The emotes and motions of the model this PC shows (or would show), their settings and their saved file.</summary>
    CharacterActionService Actions { get; }

    /// <summary>The emotes and motions of the model the character shows now, or null while it shows none.</summary>
    CharacterActionCatalog? ShownCatalog { get; }

    /// <summary>The kept body motions (Make a motion), or null before Martlet is set up.</summary>
    CharacterMotions? Motions { get; }

    /// <summary>Whether the character shows on the desktop.</summary>
    bool AvatarShowing { get; }

    /// <summary>What the character played last, in words, or null.</summary>
    string? LastAction { get; }

    /// <summary>The lingering emotes the character shows now.</summary>
    HeldEmotes Held { get; }

    /// <summary>The voice chosen now (for which tags follow its cues), or null.</summary>
    SpeechEngine? SpeakingEngine { get; }

    /// <summary>Whether the Thinking model can name the emotes (a conversation is ready).</summary>
    bool CanName { get; }

    /// <summary>Ends when the window closes.</summary>
    CancellationToken Lifetime { get; }

    /// <summary>Whether Companion › Emotes and motions is the open page.</summary>
    bool Open { get; }

    /// <summary>Whether the keyboard is in the open page (the owner is typing).</summary>
    bool Typing { get; }

    // ---------- what the page starts ----------

    /// <summary>Follows the model Companion shows (its emotes and motions), before the page draws.</summary>
    void FollowActions();

    /// <summary>Name them with Thinking: asks the Thinking model to name the model's emotes and motions.</summary>
    void NameActions();

    /// <summary>Clear emotes: turns off every lingering emote.</summary>
    Task ClearEmotesAsync();

    /// <summary>Plays <paramref name="source"/> on the character (turns a lingering one on with <paramref name="hold"/>).</summary>
    Task<bool> PlayActionAsync(CharacterActionSource source, string reason, bool hold);

    /// <summary>Turns a lingering <paramref name="source"/> off.</summary>
    Task<bool> StopActionAsync(CharacterActionSource source, string reason);

    /// <summary>Sets off a combo's <paramref name="parts"/> at once.</summary>
    Task<int> PlayComboAsync(string tag, IReadOnlyList<(CharacterActionSource Source, bool Hold)> parts, string reason);

    /// <summary>Turns a combo's lingering <paramref name="parts"/> off.</summary>
    Task<int> StopComboAsync(string tag, IReadOnlyList<CharacterActionSource> parts, string reason);

    /// <summary>Marks the open page as edited, so a background change doesn't draw it again under the owner's typing.</summary>
    void MarkEdited();

    /// <summary>Marks the open page as not edited (after Use the model's own names).</summary>
    void ForgetEdits();

    /// <summary>Makes <paramref name="autoSave"/> the open page's auto-save, saved at once when another page opens.</summary>
    void UseAutoSave(AutoSave autoSave);

    /// <summary>Draws the open Companion page again.</summary>
    void Redraw();
}
