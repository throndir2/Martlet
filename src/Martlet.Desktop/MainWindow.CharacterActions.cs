using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The character's emotes and motions, behind Companion › Emotes and motions (Pages\Emotes): the catalog of the model
/// this PC shows (<see cref="CharacterActionService"/>) with the reply instructions and tags it gives each request, following the
/// shown model, naming its emotes with the Thinking model once for each new model, and Clear emotes. The page draws them and edits
/// them; Touch offers them as each zone's reactions.</summary>
public partial class MainWindow
{
    private readonly CharacterActionService characterActions;
    private bool loadingCharacterActions, namingCharacterActions;

    /// <summary>The reply instructions and tags for the showing character's emotes and motions (null while it is hidden), with
    /// the lingering emotes it shows now for the newest message's notes. A VRM's kept motions are offered as they were when the
    /// conversation began (<see cref="OfferedMotions"/>).</summary>
    private CharacterActionPrompt? CharacterActionPromptFor(SpeechEngine? engine, PromptSettings? prompts)
    {
        if (!avatar.IsShowing) return null;
        var actions = characterActions.For(avatar.InspectedProfile?.ModelPath) is { } catalog
            ? OfferedMotions(catalog).Prompt(engine, prompts, avatar.Held.Current, DateTimeOffset.Now) : null;
        // Where the character looks, while it may change that: the look tags, and a note while its own choice holds the eyes.
        var gaze = avatar.Gaze.Prompt(prompts);
        return CharacterGaze.Join(actions, gaze, gaze?.Looking);
    }

    /// <summary>The context board of the live conversation: background producers (a screen digest, the sounds this PC plays,
    /// touches on the character) post their newest short note here with <see cref="Martlet.Conversation.ContextBoard.Post"/>, and every reply and
    /// look takes the fresh ones without waiting. The character's lingering emotes are its <see cref="Martlet.Conversation.ContextBoard.Character"/>
    /// note, which the conversation posts as it builds each request.</summary>
    internal readonly Martlet.Conversation.ContextBoard contextBoard = new();

    private void WireCharacterActions()
    {
        avatar.UseActions(characterActions.For);
        avatar.ActionPlayed += () => Dispatcher.InvokeAsync(() => emotesPage?.ShowPlayed());
        characterActions.Changed += () =>
        {
            // An emote the owner turned off or made brief stops lingering.
            avatar.ReconcileHeldAsync(lifetime.Token).Forget();
            Dispatcher.InvokeAsync(() =>
            {
                // Emotes and motions shows the catalog's rows, and Touch offers them as each zone's reactions. (Eyes follows the
                // model through its own service.)
                if (closing || openTab is not (CompanionTab.Emotes or CompanionTab.Touch) || CompanionContent.IsKeyboardFocusWithin) return;
                // Another model's emotes replace the rows at once; the same model's re-render waits until nothing is being edited.
                var model = characterActions.Current?.Inventory.ModelId;
                if (!tabEdited || model != (openTab == CompanionTab.Emotes ? emotesPage?.RenderedModel : renderedZonesModel)) RenderTab();
            });
        };
    }

    /// <summary>Clear emotes (the character's right-click menu or Companion › Emotes and motions): turns off every lingering emote.</summary>
    private async Task ClearCharacterEmotesAsync()
    {
        try { await avatar.ClearHeldAsync("Clear emotes", lifetime.Token); }
        catch (OperationCanceledException) { }
    }

    /// <summary>Keeps the loaded emotes and motions on the model this PC shows (or would show), and names a showing model's
    /// with the Thinking model once (when Thinking is set up). Runs from the character timer and when Companion › Emotes and
    /// motions, Eyes or Touch renders.</summary>
    private void FollowCharacterActions()
    {
        if (closing || loadingCharacterActions) return;
        FollowCharacterMotions();
        var profile = avatar.IsShowing ? avatar.InspectedProfile : homeAvatar;
        var modelPath = profile?.ModelPath ?? BundledLive2D.Prefix + BundledLive2D.DefaultCharacter;
        if (!string.Equals(characterActions.Path, modelPath, StringComparison.OrdinalIgnoreCase))
        {
            loadingCharacterActions = true;
            LoadAsync().Forget();
            return;
        }
        if (avatar.IsShowing && conversation is not null && !namingCharacterActions && characterActions.Current is not null &&
            (homeSettings.ThinkingRoute() is not null || conversation.Helpers.PoolHas(HelperCapability.Text)) &&
            characterActions.ClaimAutomaticNaming())
            NameCharacterActionsAsync().Forget();

        async Task LoadAsync()
        {
            try { await characterActions.LoadAsync(profile, force: false, lifetime.Token); }
            catch (OperationCanceledException) { }
            finally { loadingCharacterActions = false; }
        }
    }

    private async Task NameCharacterActionsAsync()
    {
        if (conversation is null || namingCharacterActions) return;
        namingCharacterActions = true;
        try
        {
            var talk = conversation;
            await characterActions.NameAsync((purpose, instructions, text, token) =>
                talk.AskHelperAsync(HelperJobKind.ActionNaming, purpose, instructions, text, null, token), homeSettings?.Prompts, lifetime.Token);
        }
        finally { namingCharacterActions = false; }
    }
}
