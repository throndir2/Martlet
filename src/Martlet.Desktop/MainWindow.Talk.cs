using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Audio;
using Martlet.Core.Settings;
using Martlet.Home;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>The Companion choices the talk window follows: how you talk (Listening), whether replies are spoken (Voice) and
/// what Martlet may look at (Vision). They save to talk-preferences.json and an open talk window follows them at once; the
/// talk window itself shows the conversation, and listening and watching each start and stop from their own buttons there, on
/// Home and in the notification-area menu.</summary>
public partial class MainWindow
{
    private TalkPreferences? talk;
    // A camera address exactly as typed (a password included), for this app session only; the saved copy has no password.
    private string? visionAddress;
    private IReadOnlyList<CameraDevice>? visionCameras;
    private IReadOnlyList<(HomeCamera Camera, Uri Snapshot)>? homeCameras;
    private bool findingCameras, findingHomeCameras;

    private static readonly string[] PauseChoices = ["Short (0.5 s)", "Normal (0.8 s)", "Long (1.2 s)"];
    private static readonly string[] ChattinessChoices = ["Quiet", "Normal", "Chatty", "Martlet decides"];
    // Companion › Listening › Word check, in the order shown.
    private static readonly string[] WordCheckChoices = ["Relaxed", "Normal (recommended)", "Sensitive"];
    private static readonly ListeningSensitivity[] WordCheckOrder =
        [ListeningSensitivity.Relaxed, ListeningSensitivity.Normal, ListeningSensitivity.Sensitive];
    internal const string WordCheckAbout = "Martlet ignores sounds that aren't words (mm, hmm, uh, a cough, laughter) and the words " +
        "speech-to-text often makes up from noise (\"Thank you.\"), so they never get a reply. Relaxed ignores more: Martlet needs " +
        "clearer, longer speech to answer you or to stop. Sensitive also takes single short words. A short answer such as \"yes\", " +
        "\"no\" or \"stop\" works with all three, especially right after Martlet asks something, and anything with Martlet's name " +
        "counts. What Martlet ignored shows faded in the talk window.";

    private TalkPreferences Talk => talk ??= TalkPreferences.Load(store?.DataDirectory);

    private void SaveTalk(TalkPreferences next, bool render = false)
    {
        var spoke = Talk.SpeakReplies;
        talk = next;
        if (!next.Save(store?.DataDirectory))
            ActionText.Text = "Couldn't save your talk choices. They apply until Martlet closes.";
        avatar.Gaze.Decides = next.DecideGaze;
        // An open talk window follows the change right away.
        openConversation?.UsePreferences(next, visionAddress);
        if (spoke != next.SpeakReplies) FollowVoice(next.SpeakReplies);
        RenderListening();
        if (render) RenderTab();
    }

    // ---------- Listening: how you talk ----------

    /// <summary>Always listening (the default; started from the talk window's Start listening) or push-to-talk, the
    /// voice-activity tuning and Voice ID.</summary>
    private Border TalkModeCard()
    {
        var prefs = Talk;
        var always = Choice("TalkMode", "Always listening (recommended)",
            "Press Start listening on Home and Martlet listens, replying when you pause, until you press Stop listening. The talk window is optional.",
            prefs.HandsFree, "TalkModeAlways");
        var push = Choice("TalkMode", "Push-to-talk",
            "Hold the talk button, or Space, when you want Martlet to listen.", !prefs.HandsFree, "TalkModePushToTalk");
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
            children.Add(Note("Martlet listens again after each reply, with or without the talk window open. Stop listening ends it; locking Windows pauses it.",
                new Thickness(0, 8, 0, 0)));

            var wordCheck = new ComboBox { Width = 240, ItemsSource = WordCheckChoices, SelectedIndex = Array.IndexOf(WordCheckOrder, prefs.WordCheck) };
            AutomationProperties.SetName(wordCheck, "Word check: how readily Martlet takes what it hears as words");
            AutomationProperties.SetAutomationId(wordCheck, "TalkWordCheck");
            wordCheck.SelectionChanged += (_, _) =>
            {
                if (wordCheck.SelectedIndex >= 0 && WordCheckOrder[wordCheck.SelectedIndex] != Talk.WordCheck)
                    SaveTalk(Talk with { WordCheck = WordCheckOrder[wordCheck.SelectedIndex] });
            };
            var wordCheckRow = Labeled("Word check", wordCheck);
            wordCheckRow.Margin = new Thickness(0, 12, 0, 0);
            children.Add(wordCheckRow);
            var wordCheckAbout = Note(WordCheckAbout, new Thickness(0, 4, 0, 0));
            AutomationProperties.SetAutomationId(wordCheckAbout, "TalkWordCheckAbout");
            children.Add(wordCheckAbout);

            var bargeIn = new CheckBox { Content = "Let me interrupt Martlet by talking", IsChecked = prefs.BargeIn, Margin = new Thickness(0, 16, 0, 4) };
            AutomationProperties.SetAutomationId(bargeIn, "TalkBargeIn");
            bargeIn.Checked += (_, _) => SaveTalk(Talk with { BargeIn = true });
            bargeIn.Unchecked += (_, _) => SaveTalk(Talk with { BargeIn = false });
            children.Add(bargeIn);
            var bargeInAbout = Note("Optional, off by default: Martlet doesn't listen while it speaks, and Stop (or Esc) in the talk window " +
                "interrupts it. Turn this on and Martlet keeps listening while it speaks; talking over it with real words stops the reply " +
                "and answers what you say: a word like \"stop\" or \"wait\" (or Martlet's name) right away, otherwise a few words. A hum, a " +
                "cough, laughter, a quick \"yeah\" or \"mm-hmm\" and what this PC plays never stop it. With Parakeet on this PC as Listening, " +
                "Martlet checks your words while you talk; otherwise once you pause. " +
                "With Reduce echo from my speakers on, this works through speakers too. If Martlet still stops itself, use headphones " +
                "or turn this off.", new Thickness(0, 0, 0, 0));
            AutomationProperties.SetAutomationId(bargeInAbout, "TalkBargeInAbout");
            children.Add(bargeInAbout);
        }

        var voiceId = new CheckBox { Content = "Only answer my voice", IsChecked = prefs.VoiceId, Margin = new Thickness(0, 16, 0, 4) };
        AutomationProperties.SetAutomationId(voiceId, "TalkVoiceId");
        voiceId.Checked += (_, _) => SaveTalk(Talk with { VoiceId = true }, render: true);
        voiceId.Unchecked += (_, _) => SaveTalk(Talk with { VoiceId = false }, render: true);
        children.Add(voiceId);
        var enrolled = voiceIdentity.Current;
        var status = voiceIdentity.LoadError ?? (enrolled is not null
            ? $"Your voice is enrolled ({enrolled.CreatedAt.LocalDateTime:d}). Martlet checks it on this PC and ignores other voices."
            : prefs.VoiceId ? "Set up Voice ID to use this."
            : "Optional. Set up Voice ID so Martlet ignores other voices.");
        children.Add(prefs.VoiceId && enrolled is null ? Warning(status) : Note(status, new Thickness(0, 0, 0, 0)));
        children.Add(Row(PageButton(enrolled is null ? "Set up Voice ID" : "Set up Voice ID again", SetUpVoiceId,
            primary: prefs.VoiceId && enrolled is null, id: "SetupVoiceId")));
        return Card([.. children]);
    }

    private void SetUpVoiceId()
    {
        if (closing) return;
        if (setupOperations.IsRunning) { ActionText.Text = "Martlet is busy with another task. Try again when it finishes."; return; }
        if (voiceIdentity.DataDirectory is null) { ActionText.Text = "Voice ID needs Martlet's data folder."; return; }
        new VoiceIdWindow(voiceIdentity, setupOperations, audioSessionEvents, homeSettings?.Audio?.Input) { Owner = this }.ShowDialog();
        RenderTab();
    }

    // ---------- Listening: echo from the speakers ----------

    /// <summary>Companion › Listening › Reduce echo from my speakers (on by default): while the microphone listens, Martlet also
    /// reads what this PC plays and removes it from the microphone, so on speakers it doesn't hear its own voice, a video or music
    /// as you talking. The status line says how the last listen went.</summary>
    private Border EchoCard()
    {
        var reduce = new CheckBox { Content = "Reduce echo from my speakers", IsChecked = Talk.ReduceEcho, Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetAutomationId(reduce, "TalkReduceEcho");
        reduce.Checked += (_, _) => { if (!Talk.ReduceEcho) SaveTalk(Talk with { ReduceEcho = true }, render: true); };
        reduce.Unchecked += (_, _) => { if (Talk.ReduceEcho) SaveTalk(Talk with { ReduceEcho = false }, render: true); };
        var (text, problem) = EchoStatus(Talk.ReduceEcho, conversation?.EchoReport);
        var status = problem ? Warning(text) : Note(text, new Thickness(0, 0, 0, 6));
        AutomationProperties.SetAutomationId(status, "TalkReduceEchoStatus");
        return Card(Heading("Speakers and echo"), reduce, status,
            Note("While the microphone listens, Martlet also hears what this PC plays (its own voice, videos, music) and removes " +
                "that from the microphone first, so it works without headphones. That sound is only used to cancel the echo, on this " +
                "PC; it is never saved or sent.", new Thickness(0, 0, 0, 0)));
    }

    internal static (string Text, bool Problem) EchoStatus(bool on, EchoReductionReport? report) => !on
        ? ("Off. Through speakers, Martlet may hear its own voice and whatever this PC plays. Use headphones, or turn this on.", false)
        : report switch
        {
            { State: EchoReductionState.Active, SpeakerFrames: > 0 } =>
                ("On. The last time Martlet listened, it removed what your speakers played from the microphone.", false),
            { State: EchoReductionState.Active } => ("On. The last time Martlet listened, your speakers were quiet.", false),
            { State: EchoReductionState.NoSpeakerAudio or EchoReductionState.Unavailable, Problem: { } why } => (why, true),
            _ => ("On. Martlet removes what this PC plays from the microphone whenever it listens.", false)
        };

    // ---------- Listening: hear what this PC plays ----------

    /// <summary>Companion › Listening › Hear what this PC plays (off by default): while always listening runs, Martlet also hears
    /// what the PC plays (a video, a stream, a call, a game), beside the microphone, so it can watch along. Ticking it is the
    /// consent; the text says what is heard, how it is marked and that it never reaches memory or voice recognition.</summary>
    private Border PcAudioCard()
    {
        var hear = new CheckBox { Content = "Hear what this PC plays", IsChecked = Talk.HearPc, Margin = new Thickness(0, 0, 0, 6),
            IsEnabled = Talk.HearPc || conversation?.CanHearPc != false };
        AutomationProperties.SetAutomationId(hear, "TalkHearPc");
        hear.Checked += (_, _) => { if (!Talk.HearPc) SaveTalk(Talk with { HearPc = true }, render: true); };
        hear.Unchecked += (_, _) => { if (Talk.HearPc) SaveTalk(Talk with { HearPc = false }, render: true); };
        // Which outputs are in use now (their sessions only, no sound), until a conversation has heard the PC itself.
        var outputs = Talk.HearPc ? Martlet.Audio.Windows.WasapiPcAudioSourceFactory.Outputs() : null;
        var (text, problem) = PcAudioStatus(Talk, conversation?.CanHearPc != false,
            conversation?.PcWithoutMartlet ?? (outputs is { Elsewhere: not null } ? false : null),
            conversation?.PcOutput ?? outputs?.Output, outputs?.Elsewhere);
        var status = problem ? Warning(text) : Note(text, new Thickness(0, 0, 0, 6));
        AutomationProperties.SetAutomationId(status, "TalkHearPcStatus");
        return Card([Heading("Watch along"), hear, status,
            Note("While always listening runs, Martlet also hears the sound your PC plays (videos, streams, calls, games), as if it " +
                "were watching with you. Your Listening choice transcribes it like your microphone (a cloud provider gets that sound " +
                "when Listening uses one), and it goes to Thinking marked as the PC's, never " +
                "as you. It is never remembered, never used for Voice ID or to learn voices, never sent to Home Assistant or tools, " +
                "and never saved. Martlet's own voice is left out where Windows allows it. While another output is in use (a voice " +
                "changer's or microphone app's virtual cable carries your own voice there), Martlet hears only the output you hear " +
                "and pauses while it speaks. On its own, what plays goes to Thinking " +
                "about every 20 seconds (45 when Martlet is quiet, 12 when it is chatty), and Thinking mostly stays quiet.",
                new Thickness(0, 0, 0, 8)),
            Note("How chatty Martlet is about it (the same choice as Vision's How often it comments):", new Thickness(0, 0, 0, 4)),
            .. ChattinessPicker("TalkPcChattiness")]);
    }

    internal static (string Text, bool Problem) PcAudioStatus(TalkPreferences prefs, bool available, bool? withoutMartlet,
        string? output = null, string? elsewhere = null) =>
        !prefs.HearPc ? ("Off. Martlet hears only your microphone.", false)
        : !available ? ("Martlet can't hear what this PC plays here.", true)
        : !prefs.HandsFree ? ("On, but it works only with Always listening. Push-to-talk hears only your microphone.", true)
        : !prefs.ReduceEcho ? ("On. Through speakers, keep Reduce echo from my speakers on (or use headphones), or the microphone also " +
            "hears the PC and takes it for you.", true)
        : withoutMartlet == false ? ((elsewhere is null ? "On. This Windows can't leave Martlet's own voice out, so " :
            $"On. {elsewhere} is in use too (a virtual cable there can carry your own voice), so ") +
            $"Martlet hears only what plays on {output ?? "your speakers"} and stops hearing it while it speaks.", false)
        : ("On. While Martlet listens it also hears what this PC plays, without its own voice.", false);

    // ---------- Listening: let Thinking hear your voice ----------

    /// <summary>Companion › Listening: whether a Thinking model that hears also gets the recording of what you said with the
    /// transcript. Off by default; ticking it is the consent, and the text under it says what is sent and where.</summary>
    private Border HearVoiceCard()
    {
        var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var abilities = SavedModelAbilities();
        var hears = LiveConversationConfiguration.Hearing(thinking, abilities) == HearingSupport.Supported;
        var hear = new CheckBox { Content = "Let Thinking hear my voice", IsChecked = Talk.HearVoice, Margin = new Thickness(0, 0, 0, 6),
            IsEnabled = Talk.HearVoice || hears };
        AutomationProperties.SetAutomationId(hear, "TalkHearVoice");
        hear.Checked += (_, _) => { if (!Talk.HearVoice) SaveTalk(Talk with { HearVoice = true }, render: true); };
        hear.Unchecked += (_, _) => { if (Talk.HearVoice) SaveTalk(Talk with { HearVoice = false }, render: true); };
        var advice = LiveConversationConfiguration.HearingAdvice(thinking, abilities);
        var status = hears || !Talk.HearVoice ? Note(advice, new Thickness(0, 0, 0, 6)) : Warning(advice);
        AutomationProperties.SetAutomationId(status, "TalkHearVoiceStatus");
        return Card([Heading("Hear how you say it"), hear, status, .. HearingTestControls(thinking),
            Note(LiveConversationConfiguration.HearingDisclosure(thinking), new Thickness(0, 0, 0, 0))]);
    }

    // ---------- Voice: speak replies (Mute voice on the character's menu is the same choice) ----------

    private CheckBox? speakRepliesCheck;

    private Border SpeakRepliesCard()
    {
        var speak = speakRepliesCheck = new CheckBox { Content = "Speak Martlet's replies aloud", IsChecked = Talk.SpeakReplies };
        AutomationProperties.SetAutomationId(speak, "SpeakReplies");
        speak.Checked += (_, _) => { if (!Talk.SpeakReplies) SaveTalk(Talk with { SpeakReplies = true }); };
        speak.Unchecked += (_, _) => { if (Talk.SpeakReplies) SaveTalk(Talk with { SpeakReplies = false }); };
        return Card(Heading("In conversations"), speak,
            Note("Martlet speaks each reply and shows it in the talk window. Turn this off for text-only replies: they show in the " +
                "talk window and the character's speech bubble. Mute voice and Unmute voice on the character's right-click menu " +
                "change this too.", new Thickness(0, 6, 0, 0)));
    }

    /// <summary>Mute voice or Unmute voice on the character's right-click menu: the same choice as Speak Martlet's replies aloud
    /// (Companion › Voice), so it is saved and shared with your other computers like it.</summary>
    private void SetVoiceMuted(bool muted)
    {
        if (Talk.SpeakReplies == !muted) TellCharacterVoiceAsync(muted).Forget();
        else SaveTalk(Talk with { SpeakReplies = !muted });
        if (!closing)
            ActionText.Text = muted
                ? "Martlet's voice is muted: replies show as text. Right-click the character and choose Unmute voice to hear them again."
                : "Martlet's voice is on again: replies are spoken aloud.";
    }

    /// <summary>Speak Martlet's replies aloud changed (here, on the character's menu or from another computer): muting
    /// silences a reply Martlet is saying now (the open talk window does that), and the character's menu and Companion ›
    /// Voice follow.</summary>
    private void FollowVoice(bool speak)
    {
        ErrorLog.Info(speak ? "Martlet's voice is unmuted: replies are spoken aloud." : "Martlet's voice is muted: replies show as text only.");
        if (speakRepliesCheck is { } check && check.IsChecked != speak) check.IsChecked = speak;
        TellCharacterVoiceAsync(!speak).Forget();
    }

    private async Task TellCharacterVoiceAsync(bool muted)
    {
        try { await avatar.SetVoiceMutedAsync(muted, lifetime.Token); }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or TimeoutException or
            OperationCanceledException or ObjectDisposedException or System.Text.Json.JsonException)
        {
            if (!closing) ErrorLog.Warn("The character's menu couldn't be told whether Martlet's voice is muted.", error);
        }
    }

    // ---------- Vision ----------

    private WatchSource VisionSource(TalkPreferences prefs) => (WatchKind)Math.Clamp(prefs.ScreenScope, 0, 3) switch
    {
        WatchKind.Camera => new(WatchKind.Camera, prefs.CameraId, prefs.CameraName),
        WatchKind.Url when WatchSource.Normalize(visionAddress ?? prefs.VideoAddress) is { Length: > 0 } address =>
            new(WatchKind.Url, address, WatchSource.SafeName(address)),
        var kind => new(kind)
    };

    /// <summary>Companion › Vision: whether Martlet may look at your screen or a camera once you press Start watching, what it
    /// looks at and how chatty it is. Turning it on is the consent; the text above the button says exactly what is captured and
    /// where it is sent.</summary>
    private void RenderVisionPage(Panel page)
    {
        var prefs = Talk;
        var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var abilities = SavedModelAbilities();
        var canSee = thinking is not null && LiveConversationConfiguration.Vision(thinking, abilities) != VisionSupport.Unsupported;
        var source = VisionSource(prefs);
        var chosen = source.IsScreen || source.Id.Length > 0;
        var chattiness = ChattinessTags.Choice(prefs.ScreenChattiness);

        var now = new List<UIElement>
        {
            Heading("Now"),
            new TextBlock
            {
                Text = prefs.Watch
                    ? $"On. Martlet looks at {source.Label} occasionally. Comments: {CommentsLabel(chattiness)}."
                    : "Off. Martlet doesn't look at your screen or cameras.",
                FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6)
            }
        };
        if (prefs.Watch && !chosen) now.Add(Warning(source.Kind == WatchKind.Camera ? "Choose a camera below." : "Enter the camera address below."));
        var advice = LiveConversationConfiguration.VisionAdvice(thinking, abilities);
        var adviceText = canSee ? Note(advice, new Thickness(0, 0, 0, 0)) : Warning(advice);
        AutomationProperties.SetAutomationId(adviceText, "VisionStatus");
        now.Add(adviceText);
        page.Children.Add(Card([.. now]));

        var looks = new List<UIElement> { Heading("What Martlet looks at") };
        foreach (var (kind, title, detail) in new[]
        {
            (WatchKind.ActiveWindow, "My active window", "The window you're using. Martlet skips its own windows, password managers and private browsers."),
            (WatchKind.ActiveScreen, "My whole screen", "Every monitor, with the taskbar and pop-up notifications. Martlet greys out its own windows, " +
                "password managers and private browsers, and looks right away when a notification pops up or a taskbar button flashes."),
            (WatchKind.Camera, "A camera", "A webcam, a capture card or your phone connected as a webcam."),
            (WatchKind.Url, "A phone or network camera address", "A snapshot or video stream address on your network.")
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
            looks.Add(Note(cameras.Count == 0 ? "Find cameras to choose one. Nothing opens until Martlet looks."
                : "The camera opens only while Martlet looks.", new Thickness(28, 0, 0, 0)));
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
            looks.Add(Note("Example: http://192.168.1.20:8080/shot.jpg. Passwords are used for this session only and aren't saved.",
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

        page.Children.Add(Card([Heading("How often it comments"),
            Note("Most looks end silently. Martlet won't interrupt while you're talking. This also sets how often Martlet reacts " +
                "to what this PC plays (Listening › Watch along).", new Thickness(0, 0, 0, 8)),
            .. ChattinessPicker("VisionChattiness")]));
        page.Children.Add(GazeCard(prefs));

        toggle = PageButton(prefs.Watch ? "Turn vision off" : "Turn vision on", () =>
        {
            var on = !Talk.Watch;
            SaveTalk(Talk with { Watch = on }, render: true);
            ActionText.Text = on ? "Vision is on. Press Start watching on Home or in the talk window when you want Martlet to look." : "Vision is off.";
        }, primary: !prefs.Watch, id: "VisionToggle");
        toggle.IsEnabled = prefs.Watch || canSee && chosen;
        var disclosure = Note(LiveConversationConfiguration.ScreenDisclosure(thinking, chattiness, source), new Thickness(0, 0, 0, 8));
        AutomationProperties.SetAutomationId(disclosure, "VisionDisclosure");
        page.Children.Add(Card(Heading(prefs.Watch ? "Vision is on" : "Let Martlet see"), disclosure, Row(toggle)));
    }

    /// <summary>How the comments line reads: the level, or Martlet decides with the level it picked while a conversation runs.</summary>
    private string CommentsLabel(ChattinessChoice choice) =>
        choice != ChattinessChoice.MartletDecides ? choice.ToString()
        : conversation is { } live ? $"Martlet decides ({ChattinessTags.Name(live.DecidedChattiness)} right now)" : "Martlet decides";

    // Martlet switched how chatty it is: Vision and Listening say the level it picked.
    private void FollowChattiness()
    {
        if (!closing && openTab is CompanionTab.Listening or CompanionTab.Vision && !tabEdited &&
            ChattinessTags.Choice(Talk.ScreenChattiness) == ChattinessChoice.MartletDecides)
            RenderTab();
    }

    internal const string ChattinessAbout = "Martlet decides: Martlet picks Quiet, Normal or Chatty itself and switches as it goes, " +
        "from what's happening and what you say. Ask it to hush, or to tell you what it thinks, and it follows. It starts at Normal.";

    /// <summary>Companion's chattiness choice (Vision's How often it comments, and the same choice under Listening › Watch
    /// along): Quiet, Normal, Chatty or Martlet decides, then what Martlet decides means and, while a conversation runs, the
    /// level it picked.</summary>
    private UIElement[] ChattinessPicker(string id)
    {
        var choice = ChattinessTags.Choice(Talk.ScreenChattiness);
        var chatty = new ComboBox { Width = 280, ItemsSource = ChattinessChoices, SelectedIndex = (int)choice, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(chatty, "How often Martlet comments");
        AutomationProperties.SetAutomationId(chatty, id);
        chatty.SelectionChanged += (_, _) =>
        {
            if (chatty.SelectedIndex >= 0 && chatty.SelectedIndex != Talk.ScreenChattiness)
                SaveTalk(Talk with { ScreenChattiness = chatty.SelectedIndex }, render: true);
        };
        var status = Note(ChattinessStatus(choice, conversation?.DecidedChattiness), new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(status, id + "Status");
        return [chatty, status];
    }

    internal static string ChattinessStatus(ChattinessChoice choice, Chattiness? decided) => choice switch
    {
        ChattinessChoice.MartletDecides => ChattinessAbout +
            (decided is { } level ? $" Right now it is {ChattinessTags.Name(level)}." : ""),
        ChattinessChoice.Quiet => "Quiet: Martlet speaks up only when something is clearly remarkable.",
        ChattinessChoice.Chatty => "Chatty: Martlet reacts more often, but still stays quiet when nothing is new.",
        _ => "Normal: Martlet says something when it's worth saying."
    };

    /// <summary>Companion › Vision › Where the character looks: at your mouse (the default), or Martlet decides with each new
    /// screenshot of your screen whether to look at your mouse or at something on the screen.</summary>
    private Border GazeCard(TalkPreferences prefs)
    {
        var mouse = Choice("VisionGaze", "At your mouse", "The character's head and eyes follow your mouse pointer.",
            !prefs.DecideGaze, "VisionGaze-Mouse");
        var decide = Choice("VisionGaze", "Martlet decides",
            "With each new screenshot of your screen, Martlet looks at your mouse or at something interesting on the screen: " +
            "something that just popped up or moved, or what it is about to remark on.",
            prefs.DecideGaze, "VisionGaze-Martlet");
        mouse.Checked += (_, _) => { if (Talk.DecideGaze) SaveTalk(Talk with { DecideGaze = false }, render: true); };
        decide.Checked += (_, _) => { if (!Talk.DecideGaze) SaveTalk(Talk with { DecideGaze = true }, render: true); };
        var status = Note(GazeStatus(prefs), new Thickness(0, 2, 0, 0));
        AutomationProperties.SetAutomationId(status, "VisionGazeStatus");
        return Card(Heading("Where the character looks"), mouse, decide, status,
            Note("It decides while vision watches your active window or whole screen and the character shows. Screenshots are " +
                "compared on this PC; the Thinking model only chooses during the looks Martlet already takes, so nothing extra " +
                "is sent.", new Thickness(0, 6, 0, 0)));
    }

    private string GazeStatus(TalkPreferences prefs)
    {
        if (!prefs.DecideGaze) return "The character follows your mouse.";
        if (!prefs.Watch) return "Vision is off, so the character follows your mouse. Turn vision on below.";
        if (VisionSource(prefs) is { IsScreen: false }) return "Martlet decides only while it watches your screen; with a camera the character follows your mouse.";
        return avatar.Gaze.Status;
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
                ? "Windows doesn't see any cameras. Connect a camera or enter a camera address."
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
                : $"Home Assistant has {homeCameras.Count} camera{(homeCameras.Count == 1 ? "" : "s")}. Pick one above.";
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
