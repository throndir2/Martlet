using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Creations;
using Martlet.Discord.Calls;

namespace Martlet.Desktop;

/// <summary>Companion › Discord › Martlet in your Discord calls (companion mode on the owner's own Discord account, under
/// Martlet's own bot). Every control has an automation ID; the status lines are MCP SafeValues.</summary>
public partial class MainWindow
{
    private readonly DiscordCallService discordCalls;
    private bool callOutputsListed;

    private static readonly string[] CallCaptureChoices = ["The Discord app only (recommended)", "Everything this PC plays except Martlet"];
    private static readonly string[] CallCameraChoices = ["Green", "Blue", "Magenta", "Black"];

    private Border DiscordCallsCard()
    {
        var saved = discordCalls.Preferences;
        if (!callOutputsListed)
        {
            callOutputsListed = true;
            RefreshCallOutputsAsync().Forget();
        }
        var on = new CheckBox { Content = "Martlet joins my Discord calls", IsChecked = saved.On, Margin = new Thickness(0, 0, 0, 6),
            IsEnabled = saved.On || conversation?.CanHearPc != false };
        AutomationProperties.SetAutomationId(on, "DiscordCallOn");
        on.Checked += (_, _) => SaveCall(prefs => prefs with { On = true });
        on.Unchecked += (_, _) => SaveCall(prefs => prefs with { On = false });

        var status = Note(discordCalls.Status(openConversation is { ListeningStarted: true }), new Thickness(0, 0, 0, 8));
        AutomationProperties.SetAutomationId(status, "DiscordCallStatus");

        var capture = new ComboBox { Width = 320, ItemsSource = CallCaptureChoices, SelectedIndex = (int)saved.Capture,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 6) };
        AutomationProperties.SetName(capture, "What Martlet hears of the call");
        AutomationProperties.SetAutomationId(capture, "DiscordCallCapture");
        capture.SelectionChanged += (_, _) =>
        {
            if (capture.SelectedIndex >= 0 && capture.SelectedIndex != (int)discordCalls.Preferences.Capture)
                SaveCall(prefs => prefs with { Capture = (DiscordCallCapture)capture.SelectedIndex });
        };

        var see = new CheckBox { Content = "See who is talking in the Discord window (on this PC only)", IsChecked = saved.SeeSpeakers,
            Margin = new Thickness(0, 4, 0, 4) };
        AutomationProperties.SetAutomationId(see, "DiscordCallSeeSpeakers");
        see.Checked += (_, _) => SaveCall(prefs => prefs with { SeeSpeakers = true });
        see.Unchecked += (_, _) => SaveCall(prefs => prefs with { SeeSpeakers = false });
        var owner = new TextBox { Text = saved.OwnerName ?? "", Width = 240, HorizontalAlignment = HorizontalAlignment.Left,
            MaxLength = DiscordCallPreferences.MaximumNameLength, Margin = new Thickness(0, 2, 0, 4) };
        AutomationProperties.SetName(owner, "Your Discord display name");
        AutomationProperties.SetAutomationId(owner, "DiscordCallOwnerName");
        owner.LostFocus += (_, _) =>
        {
            var name = owner.Text.Trim();
            if (name != (discordCalls.Preferences.OwnerName ?? "")) SaveCall(prefs => prefs with { OwnerName = name });
        };
        var attribution = Note($"Who is talking: {discordCalls.Attribution.Source}; {discordCalls.Attribution.Attributed} " +
            $"{(discordCalls.Attribution.Attributed == 1 ? "person" : "people")} named so far.", new Thickness(0, 0, 0, 8));
        AutomationProperties.SetAutomationId(attribution, "DiscordCallAttribution");

        var outputs = discordCalls.Outputs ?? [];
        var items = new List<string> { "Martlet's usual output" };
        items.AddRange(outputs.Select(output => output.Name + (DiscordCallOutputs.LooksVirtual(output.Name) ? " (virtual cable)" : "")));
        var plan = discordCalls.Plan;
        var chosen = saved.OutputId is null ? 0 : outputs.ToList().FindIndex(output => output.Id == plan.OutputId) + 1;
        if (saved.OutputId is not null && chosen <= 0)
        {
            items.Add((saved.OutputName ?? "The chosen output") + " (not connected)");
            chosen = items.Count - 1;
        }
        var output = new ComboBox { Width = 320, ItemsSource = items, SelectedIndex = chosen, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, 4) };
        AutomationProperties.SetName(output, "Where Martlet's voice goes in the call");
        AutomationProperties.SetAutomationId(output, "DiscordCallOutput");
        output.SelectionChanged += (_, _) =>
        {
            var index = output.SelectedIndex;
            if (index < 0 || index == chosen || index > outputs.Count) return;
            SaveCall(prefs => index == 0 ? prefs with { OutputId = null, OutputName = null }
                : prefs with { OutputId = outputs[index - 1].Id, OutputName = outputs[index - 1].Name });
        };
        var outputStatus = plan.Present ? Note("Martlet's voice goes to " + plan.Summary, new Thickness(0, 0, 0, 4)) : Warning(plan.Summary);
        AutomationProperties.SetAutomationId(outputStatus, "DiscordCallOutputStatus");
        var also = new CheckBox { Content = "Also play Martlet's voice on my usual output", IsChecked = saved.AlsoSpeakers,
            Margin = new Thickness(0, 2, 0, 4) };
        AutomationProperties.SetAutomationId(also, "DiscordCallAlsoSpeakers");
        also.Checked += (_, _) => SaveCall(prefs => prefs with { AlsoSpeakers = true });
        also.Unchecked += (_, _) => SaveCall(prefs => prefs with { AlsoSpeakers = false });
        var bargeIn = new CheckBox { Content = "Stop talking when someone in the call talks over Martlet", IsChecked = saved.BargeIn,
            Margin = new Thickness(0, 2, 0, 8) };
        AutomationProperties.SetAutomationId(bargeIn, "DiscordCallBargeIn");
        bargeIn.Checked += (_, _) => SaveCall(prefs => prefs with { BargeIn = true });
        bargeIn.Unchecked += (_, _) => SaveCall(prefs => prefs with { BargeIn = false });

        var pictures = CameraPictures();
        var backgrounds = new List<string>(CallCameraChoices);
        backgrounds.AddRange(pictures.Select(picture => "Picture: " + (picture.Title ?? picture.Key)));
        var chosenBackground = (int)saved.CameraBackground;
        if (saved.CameraPicture is { } savedPicture)
        {
            var found = pictures.FindIndex(picture => picture.Key == savedPicture || picture.Id == savedPicture);
            if (found < 0) backgrounds.Add("Picture: (not on this PC)");
            chosenBackground = found < 0 ? backgrounds.Count - 1 : CallCameraChoices.Length + found;
        }
        var background = new ComboBox { Width = 320, ItemsSource = backgrounds, SelectedIndex = chosenBackground,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 4) };
        AutomationProperties.SetName(background, "Camera view background");
        AutomationProperties.SetAutomationId(background, "DiscordCallCameraBackground");
        background.SelectionChanged += (_, _) =>
        {
            var index = background.SelectedIndex;
            if (index < 0 || index == chosenBackground || index >= CallCameraChoices.Length + pictures.Count) return;
            SaveCall(prefs => index < CallCameraChoices.Length
                ? prefs with { CameraBackground = (DiscordCameraBackground)index, CameraPicture = null }
                : prefs with { CameraPicture = pictures[index - CallCameraChoices.Length].Key });
            if (discordCalls.CameraOpen) ApplyCallCameraAsync(CancellationToken.None).Forget();
        };
        var camera = PageButton(discordCalls.CameraOpen ? "Close camera view" : "Open camera view", () => ToggleCallCameraAsync().Forget(),
            id: "DiscordCallCamera");
        var cameraStatus = Note(!discordCalls.CameraOpen ? $"The camera view is closed. Its background: {CameraBackgroundName(saved)}."
            : avatar.IsShowing
                ? $"The camera view is open: the character in its own 16:9 window titled \"Martlet camera\" on {CameraBackgroundName(saved)}." +
                    (callCameraProblem is { } problem ? " " + problem : "")
                : "The camera view is on, but the character isn't showing yet; it opens there as soon as the character shows.",
            new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(cameraStatus, "DiscordCallCameraStatus");

        var check = PageButton("Check this PC", () => CheckCallAsync().Forget(), link: true, id: "DiscordCallCheck");
        var doctor = Note(callDoctor ?? "Check this PC to see whether Windows can hear the Discord app alone and your output is connected.",
            new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(doctor, "DiscordCallDoctor");

        return Card([Heading("Martlet in your Discord calls"), on, status,
            Note("Join a DM, group DM or server call on your own Discord account as usual; Martlet takes part through this PC. " +
                "Martlet never controls Discord (no clicks, typing or account access) and works while always listening runs. " +
                "What it hears of the call is transcribed like Hear what this PC plays and goes to Thinking marked as the call's, " +
                "never as you, never into memory or voice recognition. It answers when someone says its name, and otherwise " +
                "only now and then.", new Thickness(0, 0, 0, 8)),
            Note("Hear:", new Thickness(0, 0, 0, 0)), capture,
            see,
            Note("Your Discord display name (so your own tile lighting up is never taken for someone else):", new Thickness(0, 2, 0, 0)),
            owner, attribution,
            Note("Martlet's voice in the call:", new Thickness(0, 0, 0, 0)), output, outputStatus, also, bargeIn,
            Note("To speak into the call, install a virtual audio cable yourself (for example VB-Audio Virtual Cable), choose its " +
                "\"CABLE Input\" here and choose \"CABLE Output\" as your Input Device in Discord › User Settings › Voice & Video. " +
                "Your own voice then needs to reach the cable too: speak through Martlet's microphone listening, or mix your " +
                "microphone into the cable with Voicemeeter or Windows' Listen to this device.", new Thickness(0, 0, 0, 8)),
            Heading("Webcam: the character"),
            Note("Open the camera view, add it to OBS as a Window Capture of \"Martlet camera\", key out the background with a " +
                "Chroma Key filter (skip it with a picture background), then Start Virtual Camera in OBS and pick \"OBS Virtual " +
                "Camera\" as your camera in Discord. Martlet installs no camera driver. In the call Martlet can change its background " +
                "itself, to a color, one of its pictures or a new picture it draws.", new Thickness(0, 0, 0, 4)),
            Note("Background:", new Thickness(0, 0, 0, 0)), background, Row(camera), cameraStatus,
            Row(check), doctor]);
    }

    private string? callDoctor;

    private void SaveCall(Func<DiscordCallPreferences, DiscordCallPreferences> update)
    {
        if (!discordCalls.Save(update)) ActionText.Text = "Couldn't save this choice.";
        RenderTab();
    }

    private async Task RefreshCallOutputsAsync()
    {
        await discordCalls.RefreshOutputsAsync(CancellationToken.None);
        if (!closing && openTab == CompanionTab.Discord && !tabEdited) RenderTab();
    }

    private async Task CheckCallAsync()
    {
        await discordCalls.RefreshOutputsAsync(CancellationToken.None);
        var saved = discordCalls.Preferences;
        var outputs = discordCalls.Outputs;
        var result = await Task.Run(() => DiscordCallDoctor.Check(saved, outputs));
        callDoctor = result.Describe();
        ErrorLog.Info("Discord call check: " + callDoctor);
        if (!closing && openTab == CompanionTab.Discord) RenderTab();
    }

    private async Task ToggleCallCameraAsync()
    {
        var open = !discordCalls.CameraOpen;
        try
        {
            if (open && !avatar.IsShowing) await ShowSavedCharacterAsync(onlyIfAutoShow: false);
            RendererCamera? view = null;
            if (open) (view, callCameraProblem) = await CallCameraViewAsync(CancellationToken.None);
            var showing = await avatar.SetCameraAsync(view, CancellationToken.None);
            discordCalls.CameraOpen = open;
            ActionText.Text = !open ? "The camera view is closed."
                : showing ? "The camera view is open. Capture the \"Martlet camera\" window in OBS."
                : "The camera view opens as soon as the character shows (Companion › Character).";
        }
        catch (Exception error) when (error is InvalidOperationException or System.IO.IOException or TimeoutException or
            Martlet.Core.Contracts.ContractException or OperationCanceledException)
        {
            ActionText.Text = "Couldn't change the camera view: " + error.Message;
        }
        if (!closing && openTab == CompanionTab.Discord) RenderTab();
    }

    // ---------- the camera view's background ----------

    private string? callCameraProblem;
    private (string Id, string Base64)? cameraPictureCache;

    /// <summary>Martlet's pictures on this PC (newest first, at most 40), the ones the camera view can show.</summary>
    private List<Creation> CameraPictures()
    {
        if (store is null) return [];
        var directory = store.DataDirectory;
        try
        {
            return CreationStore.View(directory).Live
                .Where(c => c.Kind == PictureCreations.KindName && CreationStore.IsComplete(directory, c))
                .OrderByDescending(c => c.CreatedAt).Take(40).ToList();
        }
        catch (Exception error) when (CreationStore.IsFailure(error)) { return []; }
    }

    private string CameraBackgroundName(DiscordCallPreferences saved) =>
        saved.CameraPicture is { } reference && store is not null && PictureCreations.Find(store.DataDirectory, reference) is { Removed: false } picture
            ? $"the picture \"{picture.Title ?? picture.Key}\""
            : saved.CameraBackground.ToString().ToLowerInvariant();

    /// <summary>The camera view as the saved choices make it: the color, and the chosen picture (a JPEG small enough for one
    /// renderer message) when it is on this PC; otherwise what's wrong, and the color shows.</summary>
    private async Task<(RendererCamera View, string? Problem)> CallCameraViewAsync(CancellationToken token)
    {
        var saved = discordCalls.Preferences;
        var view = new RendererCamera(true, saved.CameraColor);
        if (saved.CameraPicture is not { } reference) return (view, null);
        if (store is null || PictureCreations.Find(store.DataDirectory, reference) is not { Removed: false } creation)
            return (view, "The chosen picture isn't in Martlet's creations any more, so the camera view shows its color.");
        var title = creation.Title ?? creation.Key;
        if (cameraPictureCache is { } cached && cached.Id == creation.Id) return (view with { Picture = cached.Base64 }, null);
        try
        {
            var (image, _, problem) = await PictureCreations.LoadAsync(creation, CreationStore.Assets(store.DataDirectory, creation), token);
            if (image is null) return (view, $"\"{title}\" can't show yet: {problem}");
            var jpeg = await Task.Run(() => PictureView.CameraJpeg(image), token);
            if (jpeg is null) return (view, $"\"{title}\" couldn't be made into a background, so the camera view shows its color.");
            var base64 = Convert.ToBase64String(jpeg);
            cameraPictureCache = (creation.Id, base64);
            return (view with { Picture = base64 }, null);
        }
        catch (Exception error) when (CreationStore.IsFailure(error))
        {
            return (view, $"\"{title}\" can't be read right now ({error.Message}).");
        }
    }

    /// <summary>Shows the saved background in the open camera view; returns what went wrong, if anything.</summary>
    private async Task<string?> ApplyCallCameraAsync(CancellationToken token)
    {
        if (!discordCalls.CameraOpen) return null;
        try
        {
            var (view, problem) = await CallCameraViewAsync(token);
            callCameraProblem = problem;
            await avatar.SetCameraAsync(view, token);
        }
        catch (Exception error) when (error is InvalidOperationException or System.IO.IOException or TimeoutException or
            Martlet.Core.Contracts.ContractException or OperationCanceledException)
        {
            callCameraProblem = "Couldn't change the camera view: " + error.Message;
        }
        if (callCameraProblem is not null) ErrorLog.Info("Discord call camera: " + callCameraProblem);
        if (!closing && openTab == CompanionTab.Discord && !tabEdited) RenderTab();
        return callCameraProblem;
    }

    /// <summary>set_camera_background (on the dispatcher): saves a color or one of Martlet's pictures as the camera view's
    /// background and shows it when the view is open. The words are for the model.</summary>
    private async Task<string> SetCallBackgroundAsync(DiscordCameraBackground? color, string? picture, CancellationToken token)
    {
        string name;
        if (color is { } chosen)
        {
            if (!discordCalls.Save(prefs => prefs with { CameraBackground = chosen, CameraPicture = null }))
                return "The background couldn't be saved. Tell the user briefly.";
            name = "plain " + chosen.ToString().ToLowerInvariant();
        }
        else
        {
            if (store is null || PictureCreations.Find(store.DataDirectory, picture) is not { Removed: false } creation)
                return "There's no picture with that id. Use list_creations to find one, or draw a new one.";
            if (!CreationStore.IsComplete(store.DataDirectory, creation))
                return "That picture hasn't reached this computer yet. Tell the user you'll try again in a moment.";
            if (!discordCalls.Save(prefs => prefs with { CameraPicture = creation.Key }))
                return "The background couldn't be saved. Tell the user briefly.";
            name = $"the picture \"{creation.Title ?? creation.Key}\"";
        }
        if (!discordCalls.CameraOpen)
        {
            if (!closing && openTab == CompanionTab.Discord && !tabEdited) RenderTab();
            return $"Your webcam background is now {name}. The camera view is closed right now, so it shows once the owner opens it.";
        }
        var problem = await ApplyCallCameraAsync(token);
        return problem is null ? $"Your webcam background is now {name}; everyone in the call sees it."
            : $"Your webcam background is saved as {name}, but: {problem} Tell the user briefly.";
    }

    /// <summary>set_camera_background's way to the main window.</summary>
    private sealed class CallCameraBridge(MainWindow window) : ICallCamera
    {
        public bool Offered => window.discordCalls.Preferences.On;

        public Task<string> SetBackgroundAsync(DiscordCameraBackground? color, string? picture, CancellationToken token) =>
            window.Dispatcher.InvokeAsync(() => window.SetCallBackgroundAsync(color, picture, token)).Task.Unwrap();
    }
}
