using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › Emotes and motions (<see cref="EmotesPage"/> in Pages\Emotes). The work the
/// page shows stays in the window: the character action catalog and following the shown model (MainWindow.CharacterActions.cs),
/// playing emotes on the character, and the kept motions (MainWindow.CharacterMotions.cs).</summary>
public partial class MainWindow : IEmotesPageServices
{
    private EmotesPage? emotesPage;

    /// <summary>The Emotes and motions page, made once.</summary>
    private EmotesPage EmotesPage => emotesPage ??= new(this, AddRow);

    CharacterActionService IEmotesPageServices.Actions => characterActions;
    CharacterActionCatalog? IEmotesPageServices.ShownCatalog => characterActions.For(avatar.InspectedProfile?.ModelPath);
    CharacterMotions? IEmotesPageServices.Motions => characterMotions;
    bool IEmotesPageServices.AvatarShowing => avatar.IsShowing;
    string? IEmotesPageServices.LastAction => avatar.LastAction;
    HeldEmotes IEmotesPageServices.Held => avatar.Held;
    SpeechEngine? IEmotesPageServices.SpeakingEngine => conversation?.Configuration?.SpeakingEngine();
    bool IEmotesPageServices.CanName => conversation is not null;
    CancellationToken IEmotesPageServices.Lifetime => lifetime.Token;
    bool IEmotesPageServices.Open => openTab == CompanionTab.Emotes;
    bool IEmotesPageServices.Typing => CompanionContent.IsKeyboardFocusWithin;

    void IEmotesPageServices.FollowActions() => FollowCharacterActions();
    void IEmotesPageServices.NameActions() => NameCharacterActionsAsync().Forget();
    Task IEmotesPageServices.ClearEmotesAsync() => ClearCharacterEmotesAsync();
    Task<bool> IEmotesPageServices.PlayActionAsync(CharacterActionSource source, string reason, bool hold) =>
        avatar.PlayActionAsync(source, reason, null, lifetime.Token, hold: hold);
    Task<bool> IEmotesPageServices.StopActionAsync(CharacterActionSource source, string reason) =>
        avatar.StopActionAsync(source, reason, lifetime.Token);
    Task<int> IEmotesPageServices.PlayComboAsync(string tag, IReadOnlyList<(CharacterActionSource Source, bool Hold)> parts, string reason) =>
        avatar.PlayComboAsync(tag, parts, reason, null, lifetime.Token);
    Task<int> IEmotesPageServices.StopComboAsync(string tag, IReadOnlyList<CharacterActionSource> parts, string reason) =>
        avatar.StopComboAsync(tag, parts, reason, lifetime.Token);
    void IEmotesPageServices.MarkEdited() => tabEdited = true;
    void IEmotesPageServices.ForgetEdits() => tabEdited = false;
    void IEmotesPageServices.UseAutoSave(AutoSave autoSave) => tabAutoSave = autoSave;
    void IEmotesPageServices.Redraw() => RenderTab();
}
