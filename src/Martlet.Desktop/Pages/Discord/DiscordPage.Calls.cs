using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Creations;
using Martlet.Discord.Calls;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Discord › Martlet in your Discord calls (companion mode on the owner's own Discord account, under Martlet's
/// own bot): what Martlet hears of the call, its voice in the call and the character as a webcam (the camera view, its background
/// and framing). Every control has an automation ID; the status lines are MCP SafeValues.</summary>
internal sealed partial class DiscordPage
{
    private static readonly string[] CallCaptureChoices = ["The Discord app only (recommended)", "Everything this PC plays except Martlet"];
    private static readonly string[] CallCameraChoices = ["Green", "Blue", "Magenta", "Black"];

    /// <summary>The saved camera picture's small preview, decoded again only when the file changes.</summary>
    private (DateTime Written, System.Windows.Media.ImageSource? Image)? callPicturePreview;

    private Border CallsCard()
    {
        var calls = services.Calls;
        var saved = calls.Preferences;
        services.ListCallOutputsOnce();
        var on = new CheckBox { Content = "Martlet joins my Discord calls", IsChecked = saved.On, Margin = new Thickness(0, 0, 0, 6),
            IsEnabled = saved.On || services.CanHearPc };
        AutomationProperties.SetAutomationId(on, "DiscordCallOn");
        on.Checked += (_, _) => services.SaveCall(prefs => prefs with { On = true });
        on.Unchecked += (_, _) => services.SaveCall(prefs => prefs with { On = false });

        var status = Note(calls.Status(services.Listening), new Thickness(0, 0, 0, 8));
        AutomationProperties.SetAutomationId(status, "DiscordCallStatus");

        var capture = new ComboBox { Width = 320, ItemsSource = CallCaptureChoices, SelectedIndex = (int)saved.Capture,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 6) };
        AutomationProperties.SetName(capture, "What Martlet hears of the call");
        AutomationProperties.SetAutomationId(capture, "DiscordCallCapture");
        capture.SelectionChanged += (_, _) =>
        {
            if (capture.SelectedIndex >= 0 && capture.SelectedIndex != (int)calls.Preferences.Capture)
                services.SaveCall(prefs => prefs with { Capture = (DiscordCallCapture)capture.SelectedIndex });
        };

        var see = new CheckBox { Content = "See who is talking in the Discord window (on this PC only)", IsChecked = saved.SeeSpeakers,
            Margin = new Thickness(0, 4, 0, 4) };
        AutomationProperties.SetAutomationId(see, "DiscordCallSeeSpeakers");
        see.Checked += (_, _) => services.SaveCall(prefs => prefs with { SeeSpeakers = true });
        see.Unchecked += (_, _) => services.SaveCall(prefs => prefs with { SeeSpeakers = false });
        var owner = new TextBox { Text = saved.OwnerName ?? "", Width = 240, HorizontalAlignment = HorizontalAlignment.Left,
            MaxLength = DiscordCallPreferences.MaximumNameLength, Margin = new Thickness(0, 2, 0, 4) };
        AutomationProperties.SetName(owner, "Your Discord display name");
        AutomationProperties.SetAutomationId(owner, "DiscordCallOwnerName");
        InputHint.SetText(owner, "Alex");
        owner.LostFocus += (_, _) =>
        {
            var name = owner.Text.Trim();
            if (name != (calls.Preferences.OwnerName ?? "")) services.SaveCall(prefs => prefs with { OwnerName = name });
        };
        var attribution = Note(DiscordPageModel.AttributionText(calls), new Thickness(0, 0, 0, 8));
        AutomationProperties.SetAutomationId(attribution, "DiscordCallAttribution");

        var outputs = calls.Outputs ?? [];
        var items = new List<string> { "Martlet's usual output" };
        items.AddRange(outputs.Select(output => output.Name + (DiscordCallOutputs.LooksVirtual(output.Name) ? " (virtual cable)" : "")));
        var plan = calls.Plan;
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
            services.SaveCall(prefs => index == 0 ? prefs with { OutputId = null, OutputName = null }
                : prefs with { OutputId = outputs[index - 1].Id, OutputName = outputs[index - 1].Name });
        };
        var outputStatus = plan.Present ? Note("Martlet's voice goes to " + plan.Summary, new Thickness(0, 0, 0, 4)) : Warning(plan.Summary);
        AutomationProperties.SetAutomationId(outputStatus, "DiscordCallOutputStatus");
        var also = new CheckBox { Content = "Also play Martlet's voice on my usual output", IsChecked = saved.AlsoSpeakers,
            Margin = new Thickness(0, 2, 0, 4) };
        AutomationProperties.SetAutomationId(also, "DiscordCallAlsoSpeakers");
        also.Checked += (_, _) => services.SaveCall(prefs => prefs with { AlsoSpeakers = true });
        also.Unchecked += (_, _) => services.SaveCall(prefs => prefs with { AlsoSpeakers = false });
        var bargeIn = new CheckBox { Content = "Stop talking when someone in the call talks over Martlet", IsChecked = saved.BargeIn,
            Margin = new Thickness(0, 2, 0, 8) };
        AutomationProperties.SetAutomationId(bargeIn, "DiscordCallBargeIn");
        bargeIn.Checked += (_, _) => services.SaveCall(prefs => prefs with { BargeIn = true });
        bargeIn.Unchecked += (_, _) => services.SaveCall(prefs => prefs with { BargeIn = false });

        var background = new ComboBox { Width = 160, ItemsSource = calls.HasPicture ? [.. CallCameraChoices, "Picture"] : CallCameraChoices,
            SelectedIndex = (int)saved.CameraBackground, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 4) };
        AutomationProperties.SetName(background, "Camera view background");
        AutomationProperties.SetAutomationId(background, "DiscordCallCameraBackground");
        background.SelectionChanged += (_, _) =>
        {
            if (background.SelectedIndex >= 0 && background.SelectedIndex != (int)calls.Preferences.CameraBackground)
            {
                services.CallPictureState = null;
                services.SaveCall(prefs => prefs with { CameraBackground = (DiscordCameraBackground)background.SelectedIndex });
                services.RefreshCallCamera();
            }
        };
        var camera = PageButton(calls.CameraOpen ? "Close camera view" : "Open camera view", services.ToggleCallCamera, id: "DiscordCallCamera");
        var cameraStatus = Note(DiscordPageModel.CameraText(calls.CameraOpen, services.CharacterShowing, saved), new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(cameraStatus, "DiscordCallCameraStatus");
        var framing = HelpTip.Explain($"Framing: {saved.CameraFraming}. In the camera window, drag the character to move it, use the mouse " +
            "wheel to zoom it in or out and the arrow keys to nudge it; Home resets it and Shift+drag moves the window.",
            new Thickness(0, 4, 0, 2), "DiscordCameraFraming", "framing the camera", wiki: "Discord#her-camera");
        AutomationProperties.SetAutomationId(framing, "DiscordCallCameraFraming");
        var framingOpen = calls.CameraOpen && services.CharacterShowing;
        Button Frame(string label, string action, string id)
        {
            var button = PageButton(label, () => services.FrameCallCamera(action), id: id);
            button.IsEnabled = framingOpen;
            return button;
        }
        var framingButtons = Row(Frame("Bigger", "in", "DiscordCallCameraZoomIn"), Frame("Smaller", "out", "DiscordCallCameraZoomOut"),
            Frame("Left", "left", "DiscordCallCameraLeft"), Frame("Right", "right", "DiscordCallCameraRight"),
            Frame("Up", "up", "DiscordCallCameraUp"), Frame("Down", "down", "DiscordCallCameraDown"),
            Frame("Reset framing", "reset", "DiscordCallCameraReset"));

        var check = PageButton("Check this PC", services.CheckCall, link: true, id: "DiscordCallCheck");
        var doctor = Note(services.CallDoctor ?? "Check this PC to see whether Windows can hear the Discord app alone and your output is connected.",
            new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(doctor, "DiscordCallDoctor");

        return Card([Heading("Martlet in your Discord calls"), on, status,
            HelpTip.Explain("Join a DM, group DM or server call on your own Discord account as usual; Martlet takes part through this PC. " +
                "Martlet never controls Discord (no clicks, typing or account access) and works while always listening runs. " +
                "What it hears of the call goes to Thinking marked as the call's, never as you, never into memory or voice recognition.",
                new Thickness(0, 0, 0, 8), "DiscordCalls", "Discord calls", wiki: "Discord#martlet-in-your-discord-calls"),
            Note("Hear:", new Thickness(0, 0, 0, 0)), capture,
            see,
            Note("Your Discord display name (so your own tile lighting up is never taken for someone else):", new Thickness(0, 2, 0, 0)),
            owner, attribution,
            Note("Martlet's voice in the call:", new Thickness(0, 0, 0, 0)), output, outputStatus, also, bargeIn,
            HelpTip.Explain("To speak into the call, install a virtual audio cable (for example VB-Audio Virtual Cable), choose its " +
                "\"CABLE Input\" here and choose \"CABLE Output\" as your input device in Discord. " +
                "Discord's input device is in User Settings › Voice & Video. Your own voice must reach the cable too: speak through " +
                "Martlet's microphone listening, or mix your microphone into the cable with Voicemeeter or Windows' Listen to this device.",
                new Thickness(0, 0, 0, 8), "DiscordCallVoice", "Martlet's voice in the call", wiki: "Discord#her-voice-in-calls"),
            Heading("Webcam: the character"),
            HelpTip.Explain("Open the camera view, capture its \"Martlet camera\" window in OBS and choose \"OBS Virtual Camera\" as your " +
                "camera in Discord. For a color background, add a Chroma Key filter in OBS (a picture needs none). Martlet installs no camera driver.",
                new Thickness(0, 0, 0, 4), "DiscordCallWebcam", "the webcam", wiki: "Discord#her-camera"),
            Note("Background:", new Thickness(0, 0, 0, 0)), background, CallPicturePanel(saved), Row(camera), cameraStatus, framing, framingButtons,
            Row(check), doctor]);
    }

    /// <summary>The camera picture: a file, a picture from Creations or one drawn from an instruction where Companion › Picture making
    /// draws, with a small preview of the saved one.</summary>
    private StackPanel CallPicturePanel(DiscordCallPreferences saved)
    {
        var calls = services.Calls;
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        var dataDirectory = services.DataDirectory;
        panel.Children.Add(Note("Or put the character on a picture:", new Thickness(0, 0, 0, 2)));

        if (calls.HasPicture && dataDirectory is not null)
        {
            var path = DiscordCallPreferences.PicturePath(dataDirectory);
            DateTime written;
            try { written = System.IO.File.GetLastWriteTimeUtc(path); }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { written = DateTime.MinValue; }
            if (callPicturePreview?.Written != written)
            {
                System.Windows.Media.ImageSource? image = null;
                try { image = PictureView.Decode(System.IO.File.ReadAllBytes(path), 320); }
                catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { }
                callPicturePreview = (written, image);
            }
            var preview = new Image { Source = callPicturePreview?.Image, MaxHeight = 90, MaxWidth = 160, Stretch = System.Windows.Media.Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 4) };
            AutomationProperties.SetAutomationId(preview, "DiscordCallCameraPicture");
            AutomationProperties.SetName(preview, "Camera picture");
            panel.Children.Add(preview);
        }

        var file = PageButton("Choose a picture file...", services.ChooseCallPicture, id: "DiscordCallCameraFile");

        var pictures = services.CreationsFolder is not { } kept ? [] : CreationStore.View(kept).Live
            .Where(c => c.Kind == Martlet.Conversation.PictureCreations.KindName && CreationStore.IsComplete(kept, c)).ToArray();
        ComboBox? creations = null;
        if (pictures.Length > 0)
        {
            creations = new ComboBox { Width = 260, ItemsSource = new[] { "A picture from Creations..." }.Concat(pictures.Select(c => c.Title ?? c.Key)).ToArray(),
                SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(creations, "Use a picture from Creations");
            AutomationProperties.SetAutomationId(creations, "DiscordCallCameraCreation");
            creations.SelectionChanged += (_, _) =>
            {
                if (creations.SelectedIndex > 0) services.UseCreationPicture(pictures[creations.SelectedIndex - 1]);
            };
        }
        panel.Children.Add(PictureRow(file, creations));

        var canDraw = dataDirectory is not null && PictureClient.IsSetUp(dataDirectory);
        if (canDraw)
        {
            var drawing = services.CallPictureDrawing;
            var prompt = new TextBox { Width = 320, MaxLength = Martlet.Core.Pictures.PictureRequest.MaximumPromptCharacters, VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = !drawing, Text = services.CallPicturePrompt };
            prompt.TextChanged += (_, _) => services.CallPicturePrompt = prompt.Text;
            AutomationProperties.SetName(prompt, "What to draw for the camera background");
            AutomationProperties.SetAutomationId(prompt, "DiscordCallCameraPrompt");
            InputHint.SetText(prompt, "A cozy room with warm evening light");
            var draw = PageButton(drawing ? "Drawing..." : "Draw it", () => services.DrawCallPicture(prompt.Text), id: "DiscordCallCameraDraw");
            draw.IsEnabled = !drawing;
            prompt.KeyDown += (_, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Enter) return;
                e.Handled = true;
                services.DrawCallPicture(prompt.Text);
            };
            panel.Children.Add(Note("Or have Martlet draw one (it's kept in Creations too):", new Thickness(0, 6, 0, 2)));
            panel.Children.Add(PictureRow(prompt, draw));
        }

        var state = Note(services.CallPictureState ?? DiscordPageModel.PictureText(saved, calls.HasPicture, canDraw), new Thickness(0, 2, 0, 0));
        AutomationProperties.SetAutomationId(state, "DiscordCallCameraPictureStatus");
        panel.Children.Add(state);
        return panel;

        static WrapPanel PictureRow(params FrameworkElement?[] items)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var item in items.OfType<FrameworkElement>())
            {
                item.Margin = new Thickness(0, 0, 10, 6);
                row.Children.Add(item);
            }
            return row;
        }
    }
}
