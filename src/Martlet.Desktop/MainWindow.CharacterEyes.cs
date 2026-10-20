using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Companion › Eyes › Where the face is, the work behind it (the card is in Pages\Eyes\EyesPage.Face.cs): where the shown
/// model's eyes come from, Measure the face and Forget the measurement. The measurement is a low-priority vision helper job,
/// never on a reply's path: it runs on its own once per model when a new model (one without a measurement of its cheeks) shows
/// and a model that can see is set up, and again on request. Its hint goes to the renderer after each model load.</summary>
public partial class MainWindow
{
    private readonly CharacterEyeService characterEyes;
    private bool measuringEyes;
    private long nextEyesCheck, nextEyesSight;
    private bool eyesCanSee;

    private void WireCharacterEyes()
    {
        characterActions.Changed += () => characterEyes.Follow(characterActions.Current?.Inventory.ModelId);
        // After each model load the renderer gets the measurement saved for that model, when Martlet already knows the model.
        avatar.UseEyes(path => characterActions.For(path)?.Inventory.ModelId is { } id ? characterEyes.HintFor(id) : null);
        characterEyes.Changed += () => Dispatcher.InvokeAsync(EyesChanged);
        avatar.EyesChanged += () => Dispatcher.InvokeAsync(EyesChanged);
    }

    private void EyesChanged()
    {
        if (closing) return;
        eyesPage?.ShowEyes();
        FollowCharacterEyes();
        // What the Eyes rows showed when the page was drawn: a new model, measurement or busy state draws them again.
        if (openTab == CompanionTab.Eyes && !CompanionContent.IsKeyboardFocusWithin && !tabEdited &&
            (eyesPage?.RenderedEyes ?? default) != (characterEyes.ModelId, characterEyes.Current, measuringEyes || characterEyes.Busy)) RenderTab();
    }

    /// <summary>From the character timer, about once a second: keeps the showing character's face hint on the shown model's
    /// measurement, and measures the face on its own when the model has no measurement with its cheeks yet (a new model).</summary>
    private void TickCharacterEyes()
    {
        var now = Environment.TickCount64;
        if (now < nextEyesCheck) return;
        nextEyesCheck = now + 1000;
        FollowCharacterEyes();
    }

    private void FollowCharacterEyes()
    {
        if (closing || !avatar.IsShowing || measuringEyes) return;
        var id = characterActions.For(avatar.InspectedProfile?.ModelPath)?.Inventory.ModelId;
        if (id is null || id != characterEyes.ModelId) return;
        var hint = characterEyes.HintFor(id);
        if (!avatar.HasEyes(hint))
        {
            avatar.SendEyesAsync(hint, lifetime.Token).Forget();
            return;
        }
        if (avatar.EyesFrom is null || characterEyes.Current is { HasCheeks: true } || conversation is null || characterEyes.Busy) return;
        // Whether a model can see changes rarely: read the setup at most every few seconds.
        var now = Environment.TickCount64;
        if (now >= nextEyesSight)
        {
            nextEyesSight = now + 5000;
            eyesCanSee = EyesSight().CanSee;
        }
        if (eyesCanSee && characterEyes.ClaimAutomatic()) MeasureEyesAsync(automatically: true).Forget();
    }

    /// <summary>Whether a model can see the close-up, from <see cref="SenseJobRouting"/>'s decision for
    /// <see cref="SenseJobKind.HelperPicture"/>: a Thinking pool member that can see, else the image model of its own while pictures
    /// go to it, else the Thinking model unless it is known to be text-only (or a FIXTURE - NOT AI stand-in answers).</summary>
    private (bool CanSee, string Line, string? Off) EyesSight() =>
        TouchZonesPageModel.SightOf(SavedSenseDecision(SenseJobKind.HelperPicture), homeSettings.ThinkingRoute(),
            SavedModelAbilities(), CharacterEyeService.Fixture);

    private async Task MeasureEyesAsync(bool automatically)
    {
        if (conversation is null || measuringEyes) return;
        measuringEyes = true;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try
        {
            // Drawn off screen by a renderer of its own, in the rest pose: the character needn't show, and the one on the desktop
            // never moves.
            var profile = avatar.IsShowing && avatar.InspectedProfile is { } shown ? shown : (await SavedCharacterAsync()).Shown;
            characterEyes.Follow(characterActions.For(profile.ModelPath)?.Inventory.ModelId);
            if (!automatically) ErrorLog.Info("Measure the face: the owner asked.");
            // The close-up goes to a Thinking pool member that can see, else to the conversation's Thinking model after any reply,
            // at low priority. MARTLET_EYES_FIXTURE answers instead (FIXTURE - NOT AI); see CharacterEyeService.
            var talk = conversation;
            await characterEyes.MeasureAsync(avatar, profile,
                (purpose, instructions, text, image, token) => talk.AskHelperAsync(HelperJobKind.Eyes, purpose, instructions, text, image, token),
                automatically, stop.Token);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or
            Martlet.Core.Contracts.ContractException or System.Text.Json.JsonException or OperationCanceledException)
        {
            if (!closing) characterEyes.Report(error is OperationCanceledException ? "Measuring the eyes was stopped."
                : $"Martlet couldn't read which character to picture: {error.Message}");
        }
        finally
        {
            measuringEyes = false;
            if (!closing)
            {
                FollowCharacterEyes();
                // After the owner's own request the page shows the result at once, as Detect zones does; a measurement Martlet
                // took on its own waits until no one types on the page.
                if (openTab == CompanionTab.Eyes && (!automatically || !CompanionContent.IsKeyboardFocusWithin && !tabEdited)) RenderTab();
            }
        }
    }

    private async Task ForgetEyesAsync()
    {
        await characterEyes.ForgetAsync(lifetime.Token);
        if (closing) return;
        FollowCharacterEyes();
        if (openTab == CompanionTab.Eyes) RenderTab();
    }
}
