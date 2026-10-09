using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Companion › Eyes › Where the eyes are: where the shown model's eyes come from (its own meshes or eye bones, a
/// vision measurement or an estimate), Measure the eyes and Forget the measurement. The measurement is a low-priority vision
/// helper job, never on a reply's path: it runs on its own once per model when the renderer says the eyes are only estimated
/// and a model that can see is set up, and on request. Its hint goes to the renderer after each model load.</summary>
public partial class MainWindow
{
    private readonly CharacterEyeService characterEyes;
    private TextBlock? eyesStatus, eyesProgress;
    private bool measuringEyes;
    private long nextEyesCheck, nextEyesSight;
    private bool eyesCanSee;
    // What the Eyes rows showed when the page was drawn: a new model, measurement or busy state draws them again.
    private (string? Model, CharacterEyeMeasurement? Measurement, bool Busy) renderedEyes;

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
        if (eyesStatus is not null) eyesStatus.Text = EyesStatusText();
        if (eyesProgress is not null)
        {
            eyesProgress.Text = characterEyes.Progress ?? "";
            eyesProgress.Visibility = characterEyes.Progress is null ? Visibility.Collapsed : Visibility.Visible;
        }
        FollowCharacterEyes();
        if (openTab == CompanionTab.Eyes && !CompanionContent.IsKeyboardFocusWithin && !tabEdited &&
            renderedEyes != (characterEyes.ModelId, characterEyes.Current, measuringEyes || characterEyes.Busy)) RenderTab();
    }

    /// <summary>From the character timer, about once a second: keeps the showing character's eye hint on the shown model's
    /// measurement, and measures the eyes on its own when they are only estimated.</summary>
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
        if (avatar.EyesFrom != RendererEyesFrom.Estimate || hint is not null || conversation is null || characterEyes.Busy) return;
        // Whether a model can see changes rarely: read the setup at most every few seconds.
        var now = Environment.TickCount64;
        if (now >= nextEyesSight)
        {
            nextEyesSight = now + 5000;
            eyesCanSee = EyesSight().CanSee;
        }
        if (eyesCanSee && characterEyes.ClaimAutomatic()) MeasureEyesAsync(automatically: true).Forget();
    }

    /// <summary>Whether a model can see the close-up: a Thinking pool member that can see, else the image model of its own while
    /// pictures go to it, else the Thinking model unless it is known to be text-only (or a FIXTURE - NOT AI stand-in answers).</summary>
    private (bool CanSee, string Line, string? Off) EyesSight()
    {
        conversation?.ReadThinkingPoolOnce();
        var seer = conversation?.ThinkingPool.Find(ThinkingJobKind.TouchZones, ThinkingCapability.Text | ThinkingCapability.Vision);
        return TouchZonesSight(homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm), SavedModelAbilities(),
            seer is null ? null : seer.Name + (seer.Model is { Length: > 0 } model ? $" ({model})" : ""), CharacterEyeService.Fixture,
            HelperImageModel());
    }

    /// <summary>The image model of its own that takes pictures now (Companion › Vision, sense-models.json), by name, or null: a
    /// helper job with a picture goes to it when no Thinking pool member can take it (docs/SENSE_MODELS.md).</summary>
    private string? HelperImageModel() =>
        SenseRouting.For(SenseKind.Image, SenseModels.Load(store?.DataDirectory), homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm),
            SavedModelAbilities()) is { Described: true } route ? route.Name : null;

    private string EyesStatusText() => characterEyes.ModelId is null ? "Reading the character..."
        : CharacterEyes.Status(avatar.EyesFrom, characterEyes.Current, avatar.IsShowing, DateTimeOffset.Now);

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
            if (!automatically) ErrorLog.Info("Measure the eyes: the owner asked.");
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

    /// <summary>Companion › Eyes › Where the eyes are: where the eyes come from, how measuring went, Measure the eyes and Forget
    /// the measurement, and the close-up Thinking saw with its boxes.</summary>
    private Border CharacterEyesCard()
    {
        var catalog = characterActions.Current;
        var busy = measuringEyes || characterEyes.Busy;
        renderedEyes = (characterEyes.ModelId, characterEyes.Current, busy);
        var stack = new List<UIElement>
        {
            Heading("Where the eyes are"),
            HelpTip.Explain("Some emotes are drawn over the eyes (heart eyes, star eyes, dizzy swirls). They cover only the iris when Martlet " +
                "knows where the eyes are: from the model's own data when it has it, otherwise measured once by your Thinking model in a " +
                "close-up of the face, drawn off screen in the rest pose (so it works while the character is hidden). When the eyes are " +
                "only estimated and a model that can see is set up, Martlet measures them on its own, in the background and never " +
                "while it replies.", new Thickness(0, 0, 0, 4), "CharacterEyes", "emotes over the eyes")
        };
        eyesStatus = Note(EyesStatusText(), new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(eyesStatus, "CharacterEyesStatus");
        AutomationProperties.SetLiveSetting(eyesStatus, AutomationLiveSetting.Polite);
        stack.Add(eyesStatus);
        eyesProgress = Note(characterEyes.Progress ?? "", new Thickness(0, 0, 0, 4));
        eyesProgress.Visibility = characterEyes.Progress is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetAutomationId(eyesProgress, "CharacterEyesProgress");
        AutomationProperties.SetLiveSetting(eyesProgress, AutomationLiveSetting.Polite);
        stack.Add(eyesProgress);

        var sight = EyesSight();
        var measure = PageButton(busy ? "Measuring..." : "Measure the eyes", () => MeasureEyesAsync(automatically: false).Forget(), id: "CharacterEyesMeasure");
        measure.IsEnabled = !busy && conversation is not null && catalog is not null && sight.CanSee;
        AutomationProperties.SetHelpText(measure, "Shows your Thinking model a close-up of the character's face (never its files) to find each " +
            "iris and eye. Martlet draws the character off screen in its rest pose, so it works while the character is hidden.");
        var forget = PageButton("Forget the measurement", () => ForgetEyesAsync().Forget(), id: "CharacterEyesForget");
        forget.IsEnabled = !busy && characterEyes.Current is not null;
        AutomationProperties.SetHelpText(forget, "Deletes this model's measurement and its pictures. The eyes then use the model's own data or an estimate.");
        stack.Add(Row(measure, forget));
        if (!sight.CanSee)
        {
            var off = Warning("Measure the eyes is off: no model that can see pictures is set up. Choose a vision-capable model in " +
                "Companion › Thinking or Companion › Thinking pool.");
            AutomationProperties.SetAutomationId(off, "CharacterEyesNote");
            stack.Add(off);
        }
        if (characterEyes.BoxesPicture is { } path)
        {
            try
            {
                var bitmap = PictureAt(path, decodeHeight: 0, decodeWidth: 480);
                var picture = new Image { Source = bitmap, Width = 240, Height = 240 * bitmap.PixelHeight / Math.Max(1, bitmap.PixelWidth),
                    Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) };
                AutomationProperties.SetAutomationId(picture, "CharacterEyesPicture");
                AutomationProperties.SetName(picture, "The close-up Thinking saw, with its boxes: 1 and 2 the left iris and eye, 3 and 4 the right ones");
                stack.Add(picture);
            }
            catch (Exception error) when (error is IOException or NotSupportedException or UriFormatException or InvalidOperationException) { }
        }
        return Card([.. stack]);
    }
}
