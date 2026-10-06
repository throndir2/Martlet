using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;
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

        var background = new ComboBox { Width = 160, ItemsSource = discordCalls.HasPicture ? [.. CallCameraChoices, "Picture"] : CallCameraChoices,
            SelectedIndex = (int)saved.CameraBackground, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 4) };
        AutomationProperties.SetName(background, "Camera view background");
        AutomationProperties.SetAutomationId(background, "DiscordCallCameraBackground");
        background.SelectionChanged += (_, _) =>
        {
            if (background.SelectedIndex >= 0 && background.SelectedIndex != (int)discordCalls.Preferences.CameraBackground)
            {
                callPictureState = null;
                SaveCall(prefs => prefs with { CameraBackground = (DiscordCameraBackground)background.SelectedIndex });
                RefreshCallCamera();
            }
        };
        var camera = PageButton(discordCalls.CameraOpen ? "Close camera view" : "Open camera view", () => ToggleCallCameraAsync().Forget(),
            id: "DiscordCallCamera");
        var cameraStatus = Note(!discordCalls.CameraOpen ? "The camera view is closed."
            : avatar.IsShowing
                ? $"The camera view is open: the character in its own 16:9 window titled \"Martlet camera\" on {saved.CameraDescription}."
                : "The camera view is on, but the character isn't showing yet; it opens there as soon as the character shows.",
            new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(cameraStatus, "DiscordCallCameraStatus");
        var framing = Note($"Framing: {saved.CameraFraming}. In the camera window, drag the character to move it, use the mouse " +
            "wheel to zoom it in or out and the arrow keys to nudge it; Home resets it and Shift+drag moves the window. " +
            "OBS captures it as framed.", new Thickness(0, 4, 0, 2));
        AutomationProperties.SetAutomationId(framing, "DiscordCallCameraFraming");
        var framingOpen = discordCalls.CameraOpen && avatar.IsShowing;
        Button Frame(string label, string action, string id)
        {
            var button = PageButton(label, () => FrameCallCameraAsync(action).Forget(), id: id);
            button.IsEnabled = framingOpen;
            return button;
        }
        var framingButtons = Row(Frame("Bigger", "in", "DiscordCallCameraZoomIn"), Frame("Smaller", "out", "DiscordCallCameraZoomOut"),
            Frame("Left", "left", "DiscordCallCameraLeft"), Frame("Right", "right", "DiscordCallCameraRight"),
            Frame("Up", "up", "DiscordCallCameraUp"), Frame("Down", "down", "DiscordCallCameraDown"),
            Frame("Reset framing", "reset", "DiscordCallCameraReset"));

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
            Note("Open the camera view, add it to OBS as a Window Capture of \"Martlet camera\", key out a color background with a " +
                "Chroma Key filter (a picture needs none), then Start Virtual Camera in OBS and pick \"OBS Virtual Camera\" as your " +
                "camera in Discord. Martlet installs no camera driver.", new Thickness(0, 0, 0, 4)),
            Note("Background:", new Thickness(0, 0, 0, 0)), background, CallPicturePanel(saved), Row(camera), cameraStatus, framing, framingButtons,
            Row(check), doctor]);
    }

    private string? callPictureState;
    private string callPicturePrompt = "";
    private bool callPictureDrawing;
    private (DateTime Written, System.Windows.Media.ImageSource? Image)? callPicturePreview;

    /// <summary>The camera picture: a file, a picture from Creations or one drawn from an instruction where Companion › Pictures
    /// draws, with a small preview of the saved one.</summary>
    private StackPanel CallPicturePanel(DiscordCallPreferences saved)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        var dataDirectory = store?.DataDirectory;
        panel.Children.Add(Note("Or put the character on a picture:", new Thickness(0, 0, 0, 2)));

        if (discordCalls.HasPicture && dataDirectory is not null)
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

        var file = PageButton("Choose a picture file...", ChooseCallPicture, id: "DiscordCallCameraFile");

        var pictures = dataDirectory is null ? [] : CreationStore.View(dataDirectory).Live
            .Where(c => c.Kind == Martlet.Conversation.PictureCreations.KindName && CreationStore.IsComplete(dataDirectory, c)).ToArray();
        ComboBox? creations = null;
        if (pictures.Length > 0)
        {
            creations = new ComboBox { Width = 260, ItemsSource = new[] { "A picture from Creations..." }.Concat(pictures.Select(c => c.Title ?? c.Key)).ToArray(),
                SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(creations, "Use a picture from Creations");
            AutomationProperties.SetAutomationId(creations, "DiscordCallCameraCreation");
            creations.SelectionChanged += (_, _) =>
            {
                if (creations.SelectedIndex > 0) UseCreationPictureAsync(pictures[creations.SelectedIndex - 1]).Forget();
            };
        }
        panel.Children.Add(PictureRow(file, creations));

        if (dataDirectory is not null && PictureClient.IsSetUp(dataDirectory))
        {
            var prompt = new TextBox { Width = 320, MaxLength = Martlet.Core.Pictures.PictureRequest.MaximumPromptCharacters, VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = !callPictureDrawing, Text = callPicturePrompt };
            prompt.TextChanged += (_, _) => callPicturePrompt = prompt.Text;
            AutomationProperties.SetName(prompt, "What to draw for the camera background");
            AutomationProperties.SetAutomationId(prompt, "DiscordCallCameraPrompt");
            var draw = PageButton(callPictureDrawing ? "Drawing..." : "Draw it", () => DrawCallPictureAsync(prompt.Text).Forget(), id: "DiscordCallCameraDraw");
            draw.IsEnabled = !callPictureDrawing;
            prompt.KeyDown += (_, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Enter) return;
                e.Handled = true;
                DrawCallPictureAsync(prompt.Text).Forget();
            };
            panel.Children.Add(Note("Or have Martlet draw one (it's kept in Creations too):", new Thickness(0, 6, 0, 2)));
            panel.Children.Add(PictureRow(prompt, draw));
        }

        var state = Note(callPictureState ?? (saved.CameraBackground == DiscordCameraBackground.Picture ? $"The camera shows {saved.CameraDescription}."
            : discordCalls.HasPicture ? "Choose Picture above to use the saved picture again."
            : dataDirectory is not null && PictureClient.IsSetUp(dataDirectory) ? "No picture chosen yet."
            : "No picture chosen yet. Set up Companion › Pictures to have Martlet draw one."), new Thickness(0, 2, 0, 0));
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

    private void ChooseCallPicture()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the camera background", Filter = "Pictures (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp", CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        byte[] bytes;
        try
        {
            if (new System.IO.FileInfo(dialog.FileName).Length > Martlet.Core.Pictures.PictureImages.MaximumBytes)
            {
                UsedCallPicture("That picture is too large (24 MB at most).", null);
                return;
            }
            bytes = System.IO.File.ReadAllBytes(dialog.FileName);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            UsedCallPicture("Couldn't read that picture: " + error.Message, null);
            return;
        }
        UsedCallPicture(discordCalls.UsePicture(bytes, DiscordCameraPictureSource.File), "a picture from a file");
    }

    private async Task UseCreationPictureAsync(Martlet.Core.Creations.Creation creation)
    {
        if (store is null || closing) return;
        try
        {
            var bytes = await CreationStore.Assets(store.DataDirectory, creation).ReadAsync(Martlet.Conversation.PictureCreations.Image, lifetime.Token);
            UsedCallPicture(bytes is null ? "That picture hasn't reached this PC yet." : discordCalls.UsePicture(bytes, DiscordCameraPictureSource.Creation),
                "a picture from Creations");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (CreationStore.IsFailure(error)) { UsedCallPicture("Couldn't read that picture: " + error.Message, null); }
    }

    /// <summary>Draws a 16:9 picture from <paramref name="instruction"/> where Companion › Pictures draws, keeps it in Creations and
    /// puts the camera on it. A cloud provider asks first, since a picture costs money.</summary>
    private async Task DrawCallPictureAsync(string instruction)
    {
        if (store is null || closing || callPictureDrawing) return;
        var text = string.Join(' ', instruction.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0)
        {
            callPictureState = "Say what to draw first, for example \"a cosy library at night, warm lamps\".";
            RenderTab();
            return;
        }
        var dataDirectory = store.DataDirectory;
        var settings = PictureClient.Settings(dataDirectory);
        if (settings.Cloud && !PictureClient.Fixture &&
            !ConfirmationDialog.Confirm(this, $"Draw the camera background with {settings.Describe()}? It may cost money.", "Draw a camera background", "Draw", "Cancel"))
            return;
        var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == Martlet.Core.Settings.SetupRole.Llm);
        var maker = PictureClient.For(dataDirectory, homeSettings?.Profile.Id ?? Guid.Empty, thinking);
        if (maker is null) return;
        callPictureDrawing = true;
        callPictureState = $"Drawing it on {maker.Where}…";
        RenderTab();
        string? problem = null;
        try
        {
            var availability = await maker.GetAvailabilityAsync(lifetime.Token);
            if (!availability.Available) problem = availability.Reason ?? "Pictures aren't available right now.";
            else
            {
                var request = new Martlet.Core.Pictures.PictureRequest { Prompt = text, Shape = Martlet.Core.Pictures.PictureShape.Wide };
                var result = await maker.GenerateAsync(request, null, lifetime.Token);
                PictureClient.FreeLater(maker);
                ErrorLog.Info($"Pictures: camera background on {result.Where}: {result.Width}x{result.Height} {result.MediaType}, " +
                    $"{result.Image.Length / 1024} KiB, {result.Took.TotalSeconds:0.0} s{(result.Fixture ? ", FIXTURE - NOT AI" : "")}.");
                var author = new Martlet.Core.Creations.CreationAuthor { Device = HostSetupCommands.SuggestedDeviceId(), Computer = Environment.MachineName };
                var about = Martlet.Conversation.PictureTools.Label(text);
                try
                {
                    await CreationStore.AddAsync(dataDirectory, Martlet.Conversation.PictureCreations.Draft(result, "Camera background: " + about, about, request, author),
                        Martlet.Core.Creations.CreationRegistry.Shared, DateTimeOffset.UtcNow, lifetime.Token);
                }
                catch (Exception error) when (CreationStore.IsFailure(error)) { ErrorLog.Info($"Pictures: couldn't keep the camera background ({error.Message})."); }
                problem = discordCalls.UsePicture(result.Image, DiscordCameraPictureSource.Drawn);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Martlet.Core.Pictures.PictureException error)
        {
            problem = error.Message;
            ErrorLog.Info($"Pictures: camera background on {maker.Where} failed ({error.Code}: {error.Message}).");
        }
        finally
        {
            callPictureDrawing = false;
            (maker as IDisposable)?.Dispose();
        }
        UsedCallPicture(problem, "a picture Martlet drew");
    }

    private void UsedCallPicture(string? problem, string? what)
    {
        callPictureState = problem ?? $"The camera shows {what}" + (discordCalls.CameraOpen ? "." : " when you open the camera view.");
        if (problem is null)
        {
            ErrorLog.Info($"Discord call: the camera background is now {what}.");
            RefreshCallCamera();
        }
        else ActionText.Text = problem;
        if (!closing && openTab == CompanionTab.Discord) RenderTab();
    }

    private void RefreshCallCamera()
    {
        if (discordCalls.CameraOpen) avatar.SetCameraAsync(discordCalls.CameraView, CancellationToken.None).Forget();
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
            var showing = await avatar.SetCameraAsync(open ? discordCalls.CameraView : null, CancellationToken.None);
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

    /// <summary>Frames the character in the open camera view from the Discord card: "in", "out", "left", "right", "up", "down"
    /// or "reset", then saves the framing.</summary>
    private async Task FrameCallCameraAsync(string action)
    {
        try
        {
            var view = await avatar.ZoomAsync(action, CancellationToken.None);
            if (view is not { Camera: true }) throw new InvalidOperationException("The camera view isn't open.");
            SaveCallFraming(view);
            ActionText.Text = $"Camera framing: {discordCalls.Preferences.CameraFraming}.";
        }
        catch (Exception error) when (error is InvalidOperationException or System.IO.IOException or TimeoutException or
            OperationCanceledException or ObjectDisposedException or System.IO.InvalidDataException or System.Text.Json.JsonException)
        {
            ActionText.Text = "Couldn't frame the character: " + error.Message;
        }
        if (!closing && openTab == CompanionTab.Discord && !tabEdited) RenderTab();
    }

    /// <summary>The character was moved or zoomed in the camera window and has settled: saves the framing, so the camera view
    /// opens framed the same way next time.</summary>
    private async Task RememberCallFramingAsync()
    {
        try
        {
            if (await avatar.ZoomAsync("status", lifetime.Token) is not { Camera: true } view || closing) return;
            SaveCallFraming(view);
            if (openTab == CompanionTab.Discord && !tabEdited) RenderTab();
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or TimeoutException or
            OperationCanceledException or ObjectDisposedException or System.IO.InvalidDataException or System.Text.Json.JsonException)
        {
            if (!closing) ErrorLog.Warn("The camera view's framing couldn't be read to save it.", error);
        }
    }

    private void SaveCallFraming(RendererView view)
    {
        double zoom = view.Zoom, x = view.X ?? 0, y = view.Y ?? 0;
        avatar.RememberCameraFraming(zoom, x, y);
        if (discordCalls.Save(prefs => prefs with { CameraZoom = zoom, CameraX = x, CameraY = y }))
            ErrorLog.Info($"Camera framing saved: {discordCalls.Preferences.CameraFraming}.");
        else ErrorLog.Warn("The camera view's framing couldn't be saved.");
    }

    // ---------- set_camera_background: Martlet changes its own webcam background ----------

    /// <summary>set_camera_background (on the dispatcher): a plain color, or one of Martlet's pictures (a creation's key or ID;
    /// <paramref name="drawn"/> when Martlet just drew it for this) saved as the camera picture, shown at once while the camera
    /// view is open. The words are for the model.</summary>
    private async Task<string> SetCallBackgroundAsync(DiscordCameraBackground? color, string? picture, bool drawn, CancellationToken token)
    {
        string? problem;
        string what, shown;
        if (color is { } chosen)
        {
            problem = discordCalls.Save(prefs => prefs with { CameraBackground = chosen, CameraPicture = null }) ? null : "The background couldn't be saved.";
            what = shown = "plain " + chosen.ToString().ToLowerInvariant();
        }
        else
        {
            if (store is null || Martlet.Conversation.PictureCreations.Find(store.DataDirectory, picture) is not { Removed: false } creation)
                return "There's no picture with that id. Use list_creations to find one, or draw a new one.";
            byte[]? bytes = null;
            try { bytes = await CreationStore.Assets(store.DataDirectory, creation).ReadAsync(Martlet.Conversation.PictureCreations.Image, token); }
            catch (Exception error) when (CreationStore.IsFailure(error)) { ErrorLog.Info($"Discord call: couldn't read a picture ({error.Message})."); }
            if (bytes is null) return "That picture hasn't reached this computer yet. Tell the user you'll try again in a moment.";
            problem = discordCalls.UsePicture(bytes, drawn ? DiscordCameraPictureSource.Drawn : DiscordCameraPictureSource.Creation);
            what = $"the picture \"{creation.Title ?? creation.Key}\"";
            // The card and the log never show a title.
            shown = drawn ? "a picture Martlet drew" : "a picture from Creations";
        }
        UsedCallPicture(problem, shown);
        if (problem is not null) return $"The background didn't change: {problem} Tell the user briefly.";
        return discordCalls.CameraOpen ? $"Your webcam background is now {what}; everyone in the call sees it."
            : $"Your webcam background is now {what}. The camera view is closed right now, so it shows once the owner opens it.";
    }

    /// <summary>set_camera_background's way to the main window.</summary>
    private sealed class CallCameraBridge(MainWindow window) : ICallCamera
    {
        public bool Offered => window.discordCalls.Preferences.On;

        public Task<string> SetBackgroundAsync(DiscordCameraBackground? color, string? picture, bool drawn, CancellationToken token) =>
            window.Dispatcher.InvokeAsync(() => window.SetCallBackgroundAsync(color, picture, drawn, token)).Task.Unwrap();
    }
}
