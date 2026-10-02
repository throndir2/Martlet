using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;
using Martlet.Home;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>The Companion choices the talk window follows: how you talk (Listening), whether replies are spoken (Voice) and
/// what Martlet may look at (Vision). They save to talk-preferences.json and apply the next time the talk window opens; the
/// talk window itself only shows the conversation and pauses or resumes what is on.</summary>
public partial class MainWindow
{
    private TalkPreferences? talk;
    // A camera address exactly as typed (a password included), for this app session only; the saved copy has no password.
    private string? visionAddress;
    private IReadOnlyList<CameraDevice>? visionCameras;
    private IReadOnlyList<(HomeCamera Camera, Uri Snapshot)>? homeCameras;
    private bool findingCameras, findingHomeCameras;

    private static readonly string[] PauseChoices = ["a short pause (0.5 s)", "a normal pause (0.8 s)", "a long pause (1.2 s)"];
    private static readonly string[] ChattinessChoices = ["Quiet: only the big moments", "Normal", "Chatty"];

    private TalkPreferences Talk => talk ??= TalkPreferences.Load(store?.DataDirectory);

    private void SaveTalk(TalkPreferences next, bool render = false)
    {
        talk = next;
        if (!next.Save(store?.DataDirectory))
            ActionText.Text = "Couldn't save your talk choices to talk-preferences.json; they apply until Martlet closes.";
        if (render) RenderTab();
    }

    // ---------- Listening: how you talk ----------

    /// <summary>Always listening while the talk window is open (the default) or push-to-talk, the voice-activity tuning and
    /// Voice ID.</summary>
    private Border TalkModeCard()
    {
        var prefs = Talk;
        var always = Choice("TalkMode", "Always listening (recommended)",
            "While the talk window is open, Martlet hears you whenever you speak and replies when you pause. The mic button there " +
            "pauses it. It uses the microphone above; testing it there is optional.", prefs.HandsFree, "TalkModeAlways");
        var push = Choice("TalkMode", "Push-to-talk",
            "Martlet hears you only while you hold the talk button (or Space on it) in the talk window.", !prefs.HandsFree, "TalkModePushToTalk");
        always.Checked += (_, _) => { if (!Talk.HandsFree) SaveTalk(Talk with { HandsFree = true }, render: true); };
        push.Checked += (_, _) => { if (Talk.HandsFree) SaveTalk(Talk with { HandsFree = false }, render: true); };
        var children = new List<UIElement> { Heading("How you talk"), always, push };

        if (prefs.HandsFree)
        {
            var sensitivity = new Slider { Minimum = 0, Maximum = 1, Value = prefs.Sensitivity, SmallChange = 0.05, LargeChange = 0.1,
                Width = 240, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(sensitivity, "How sensitive listening is");
            AutomationProperties.SetAutomationId(sensitivity, "TalkSensitivity");
            sensitivity.ValueChanged += (_, e) => SaveTalk(Talk with { Sensitivity = e.NewValue });
            var scale = new StackPanel { Orientation = Orientation.Horizontal };
            scale.Children.Add(Note("Low", new Thickness(0, 0, 8, 0)));
            scale.Children.Add(sensitivity);
            scale.Children.Add(Note("High", new Thickness(8, 0, 0, 0)));
            foreach (var label in scale.Children.OfType<TextBlock>()) label.VerticalAlignment = VerticalAlignment.Center;

            var pause = new ComboBox { Width = 240, ItemsSource = PauseChoices, SelectedIndex = Math.Clamp(prefs.PauseIndex, 0, PauseChoices.Length - 1) };
            AutomationProperties.SetName(pause, "How long a pause before Martlet replies");
            AutomationProperties.SetAutomationId(pause, "TalkPauseLength");
            pause.SelectionChanged += (_, _) => { if (pause.SelectedIndex >= 0) SaveTalk(Talk with { PauseIndex = pause.SelectedIndex }); };

            children.Add(Labeled("Sensitivity", scale));
            children.Add(Labeled("Reply after", pause));
            children.Add(Note("It waits for each reply to finish before listening again. Locking Windows, Stop, Esc or closing the talk window " +
                "ends listening.", new Thickness(0, 8, 0, 0)));
        }

        var voiceId = new CheckBox { Content = "Only respond to my voice (Voice ID)", IsChecked = prefs.VoiceId, Margin = new Thickness(0, 16, 0, 4) };
        AutomationProperties.SetAutomationId(voiceId, "TalkVoiceId");
        voiceId.Checked += (_, _) => SaveTalk(Talk with { VoiceId = true }, render: true);
        voiceId.Unchecked += (_, _) => SaveTalk(Talk with { VoiceId = false }, render: true);
        children.Add(voiceId);
        var enrolled = voiceIdentity.Current;
        var status = voiceIdentity.LoadError ?? (enrolled is not null
            ? $"Your voice is enrolled ({enrolled.CreatedAt.LocalDateTime:d}). Martlet checks it on this PC before anything is sent; other voices, TV and games are ignored."
            : prefs.VoiceId ? "Not set up yet: enroll your voice (about 20 seconds), or turn this off. Until then Martlet can't listen."
            : "Optional. Enroll your voice once (about 20 seconds) so Martlet ignores other people, TV and games.");
        children.Add(prefs.VoiceId && enrolled is null ? Warning(status) : Note(status, new Thickness(0, 0, 0, 0)));
        children.Add(Row(PageButton(enrolled is null ? "Set up Voice ID" : "Set up Voice ID again", SetUpVoiceId,
            primary: prefs.VoiceId && enrolled is null, id: "SetupVoiceId")));
        return Card([.. children]);
    }

    private void SetUpVoiceId()
    {
        if (closing) return;
        if (setupOperations.IsRunning) { ActionText.Text = "Martlet is busy with another task. Try again when it finishes."; return; }
        if (voiceIdentity.DataDirectory is null) { ActionText.Text = "Voice ID needs Martlet's data folder, which isn't available."; return; }
        new VoiceIdWindow(voiceIdentity, setupOperations, audioSessionEvents, homeSettings?.Audio?.Input) { Owner = this }.ShowDialog();
        RenderTab();
    }

    // ---------- Voice: speak replies ----------

    private Border SpeakRepliesCard()
    {
        var speak = new CheckBox { Content = "Speak Martlet's replies aloud", IsChecked = Talk.SpeakReplies };
        AutomationProperties.SetAutomationId(speak, "SpeakReplies");
        speak.Checked += (_, _) => SaveTalk(Talk with { SpeakReplies = true });
        speak.Unchecked += (_, _) => SaveTalk(Talk with { SpeakReplies = false });
        return Card(Heading("In conversations"), speak,
            Note("Martlet speaks each reply with the voice above (an AI-generated voice, not a human) and also shows it in the talk window. " +
                "Turn this off for text-only replies.", new Thickness(0, 6, 0, 0)));
    }

    // ---------- Vision ----------

    private WatchSource VisionSource(TalkPreferences prefs) => (WatchKind)Math.Clamp(prefs.ScreenScope, 0, 3) switch
    {
        WatchKind.Camera => new(WatchKind.Camera, prefs.CameraId, prefs.CameraName),
        WatchKind.Url when WatchSource.Normalize(visionAddress ?? prefs.VideoAddress) is { Length: > 0 } address =>
            new(WatchKind.Url, address, WatchSource.SafeName(address)),
        var kind => new(kind)
    };

    /// <summary>Companion › Vision: whether Martlet may look at your screen or a camera while the talk window is open, what it
    /// looks at and how chatty it is. Turning it on is the consent; the text above the button says exactly what is captured and
    /// where it is sent.</summary>
    private void RenderVisionPage(Panel page)
    {
        var prefs = Talk;
        var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var canSee = thinking is not null && LiveConversationConfiguration.Vision(thinking) != VisionSupport.Unsupported;
        var source = VisionSource(prefs);
        var chosen = source.IsScreen || source.Id.Length > 0;
        var chattiness = (Chattiness)Math.Clamp(prefs.ScreenChattiness, 0, 2);

        var now = new List<UIElement>
        {
            Heading("Now"),
            new TextBlock
            {
                Text = prefs.Watch
                    ? $"On. While the talk window is open, Martlet looks at {source.Label} now and then ({chattiness}) and sometimes says something."
                    : "Off. Martlet doesn't look at your screen or cameras.",
                FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6)
            }
        };
        if (prefs.Watch && !chosen) now.Add(Warning(source.Kind == WatchKind.Camera ? "Choose a camera below." : "Enter the camera address below."));
        var advice = LiveConversationConfiguration.VisionAdvice(thinking);
        var adviceText = canSee ? Note(advice, new Thickness(0, 0, 0, 0)) : Warning(advice);
        AutomationProperties.SetAutomationId(adviceText, "VisionStatus");
        now.Add(adviceText);
        page.Children.Add(Card([.. now]));

        var looks = new List<UIElement> { Heading("What Martlet looks at") };
        foreach (var (kind, title, detail) in new[]
        {
            (WatchKind.ActiveWindow, "My active window", "The window you're using, like your game or browser. Martlet's own windows, password managers " +
                "and private browser windows are never captured."),
            (WatchKind.ActiveScreen, "My whole screen", "The monitor your active window is on."),
            (WatchKind.Camera, "A camera", "A webcam, a capture card or your phone connected as a webcam."),
            (WatchKind.Url, "A phone or network camera address", "A snapshot or MJPEG address from a phone camera app on your Wi-Fi, an rtsp:// stream or a video file.")
        })
        {
            var option = Choice("VisionSource", title, detail, source.Kind == kind, "VisionSource-" + kind);
            option.Checked += (_, _) => SaveTalk(Talk with { ScreenScope = (int)kind }, render: true);
            looks.Add(option);
        }
        Button? toggle = null;
        if (source.Kind == WatchKind.Camera)
        {
            var cameras = new List<CameraDevice>();
            if (prefs.CameraId.Length > 0) cameras.Add(new(prefs.CameraId, prefs.CameraName.Length > 0 ? prefs.CameraName : "Saved camera"));
            foreach (var camera in visionCameras ?? [])
            {
                cameras.RemoveAll(c => c.Id == camera.Id);
                cameras.Add(camera);
            }
            var box = new ComboBox { Width = 360, DisplayMemberPath = nameof(CameraDevice.Name), ItemsSource = cameras,
                SelectedItem = cameras.FirstOrDefault(c => c.Id == prefs.CameraId) };
            AutomationProperties.SetName(box, "Which camera Martlet looks through");
            AutomationProperties.SetAutomationId(box, "VisionCamera");
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedItem is CameraDevice picked && picked.Id != Talk.CameraId)
                    SaveTalk(Talk with { CameraId = picked.Id, CameraName = picked.Name }, render: true);
            };
            var find = PageButton(findingCameras ? "Looking..." : "Find cameras", () => FindCamerasAsync().Forget(), id: "VisionFindCameras");
            find.IsEnabled = !findingCameras;
            find.Margin = new Thickness(10, 0, 0, 0);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(28, 0, 0, 6) };
            row.Children.Add(box);
            row.Children.Add(find);
            looks.Add(row);
            looks.Add(Note(cameras.Count == 0 ? "Find cameras lists the cameras Windows offers to apps. No camera is opened until Martlet looks."
                : "The camera opens only while Martlet looks (its light is on exactly then).", new Thickness(28, 0, 0, 0)));
        }
        else if (source.Kind == WatchKind.Url)
        {
            var address = new TextBox { Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Text = visionAddress ?? prefs.VideoAddress,
                Margin = new Thickness(28, 0, 0, 6) };
            AutomationProperties.SetName(address, "Phone or network camera address");
            AutomationProperties.SetAutomationId(address, "VisionAddress");
            address.TextChanged += (_, _) =>
            {
                var typed = address.Text.Trim();
                visionAddress = typed.Length == 0 ? null : typed;
                SaveTalk(Talk with { VideoAddress = WatchSource.WithoutCredentials(WatchSource.Normalize(typed)) });
                if (toggle is not null && !Talk.Watch) toggle.IsEnabled = canSee && typed.Length > 0;
            };
            looks.Add(address);
            looks.Add(Note("For example http://192.168.1.20:8080/shot.jpg. A password in the address is used until Martlet closes and is never saved.",
                new Thickness(28, 0, 0, 0)));
            if (smartHome.Connected)
            {
                // Home Assistant cameras use their snapshot address; Martlet adds the saved Home Assistant token when it looks.
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(28, 8, 0, 0) };
                if (homeCameras is { Count: > 0 } found)
                {
                    var pick = new ComboBox { Width = 300, ItemsSource = found.Select(c => c.Camera.Name).ToArray(),
                        SelectedIndex = found.ToList().FindIndex(c => c.Snapshot.AbsoluteUri == Talk.VideoAddress) };
                    AutomationProperties.SetName(pick, "Home Assistant camera");
                    AutomationProperties.SetAutomationId(pick, "VisionHomeCamera");
                    pick.SelectionChanged += (_, _) =>
                    {
                        if (pick.SelectedIndex < 0) return;
                        visionAddress = null;
                        SaveTalk(Talk with { VideoAddress = found[pick.SelectedIndex].Snapshot.AbsoluteUri },
                            render: true);
                    };
                    row.Children.Add(pick);
                }
                var list = PageButton(findingHomeCameras ? "Looking..." : homeCameras is null ? "Use a Home Assistant camera" : "Look again",
                    () => FindHomeCamerasAsync().Forget(), id: "VisionHomeCameras");
                list.IsEnabled = !findingHomeCameras;
                list.Margin = new Thickness(homeCameras is { Count: > 0 } ? 10 : 0, 0, 0, 0);
                row.Children.Add(list);
                looks.Add(row);
            }
        }
        page.Children.Add(Card([.. looks]));

        var chatty = new ComboBox { Width = 280, ItemsSource = ChattinessChoices, SelectedIndex = (int)chattiness, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(chatty, "How often Martlet comments");
        AutomationProperties.SetAutomationId(chatty, "VisionChattiness");
        chatty.SelectionChanged += (_, _) => { if (chatty.SelectedIndex >= 0) SaveTalk(Talk with { ScreenChattiness = chatty.SelectedIndex }, render: true); };
        page.Children.Add(Card(Heading("How chatty"),
            Note("Like a friend in the room: most looks end in silence, and Martlet never interrupts while you're talking.", new Thickness(0, 0, 0, 8)),
            chatty));

        toggle = PageButton(prefs.Watch ? "Turn vision off" : "Turn vision on", () =>
        {
            var on = !Talk.Watch;
            SaveTalk(Talk with { Watch = on }, render: true);
            ActionText.Text = on ? "Vision is on. Martlet starts looking the next time you open the talk window." : "Vision is off.";
        }, primary: !prefs.Watch, id: "VisionToggle");
        toggle.IsEnabled = prefs.Watch || canSee && chosen;
        page.Children.Add(Card(Heading(prefs.Watch ? "Vision is on" : "Let Martlet see"),
            Note(LiveConversationConfiguration.ScreenDisclosure(thinking, chattiness, source), new Thickness(0, 0, 0, 8)),
            Row(toggle)));
    }

    /// <summary>Lists the cameras Windows offers to desktop apps, on request only; nothing is opened.</summary>
    private async Task FindCamerasAsync()
    {
        if (findingCameras) return;
        findingCameras = true;
        RenderTab();
        try
        {
            var cameras = await Task.Run(() => new VideoInput().Cameras());
            visionCameras = cameras;
            ActionText.Text = cameras.Count == 0
                ? "Windows offers no cameras to apps. Plug in a webcam, connect your phone as a webcam (Phone Link on Windows 11, DroidCam, Camo, iVCam) or use an address."
                : $"Found {cameras.Count} camera{(cameras.Count == 1 ? "" : "s")}.";
            if (cameras.FirstOrDefault(c => c.Id == Talk.CameraId) is { } same) SaveTalk(Talk with { CameraName = same.Name });
            else if (cameras.FirstOrDefault() is { } first) SaveTalk(Talk with { CameraId = first.Id, CameraName = first.Name });
        }
        catch (Exception error) when (error is VideoSourceException or System.Runtime.InteropServices.ExternalException)
        {
            ActionText.Text = error is VideoSourceException ? error.Message : "Windows couldn't list cameras right now. Try again in a moment.";
        }
        finally { findingCameras = false; }
        if (!closing && openTab == CompanionTab.Vision) RenderTab();
    }

    /// <summary>Lists Home Assistant's cameras on request; picking one fills in its snapshot address.</summary>
    private async Task FindHomeCamerasAsync()
    {
        if (findingHomeCameras) return;
        findingHomeCameras = true;
        RenderTab();
        try
        {
            homeCameras = await smartHome.CamerasAsync(lifetime.Token);
            ActionText.Text = homeCameras.Count == 0 ? "Home Assistant has no cameras."
                : $"Home Assistant has {homeCameras.Count} camera{(homeCameras.Count == 1 ? "" : "s")}. Pick one under What Martlet looks at.";
        }
        catch (HomeAssistantException error) { ActionText.Text = error.Message; }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
        {
            if (!closing) ActionText.Text = "Couldn't reach Home Assistant. Check that it is running, then try again.";
        }
        finally { findingHomeCameras = false; }
        if (!closing && openTab == CompanionTab.Vision) RenderTab();
    }

    // ---------- small builders ----------

    private static RadioButton Choice(string group, string title, string detail, bool isChecked, string id)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(Note(detail, new Thickness(0, 2, 0, 0)));
        var option = new RadioButton { Content = text, GroupName = group, IsChecked = isChecked, Margin = new Thickness(0, 0, 0, 10) };
        AutomationProperties.SetName(option, $"{title}: {detail}");
        AutomationProperties.SetAutomationId(option, id);
        return option;
    }

    private static DockPanel Labeled(string label, UIElement control)
    {
        var row = new DockPanel { Margin = new Thickness(28, 6, 0, 0), LastChildFill = false };
        row.Children.Add(new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(control);
        return row;
    }
}
