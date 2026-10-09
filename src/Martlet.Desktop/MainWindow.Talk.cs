using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Audio;
using Martlet.Conversation;
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
    // Companion › Listening's end-of-turn judge status line while the page shows it (it follows each decision).
    private TextBlock? turnJudgeStatus;

    /// <summary>Companion › Listening's end-of-turn judge status (TalkJudgeTurnsStatus): whether it is on, which judge runs and
    /// whether it can, and how the newest turns were decided (no words).</summary>
    internal static string TurnJudgeText(bool on,
        (string? Judge, bool Available, string? Problem, TimeSpan? LoadTime, Martlet.Conversation.EndOfTurnDecision[] Decisions)? status)
    {
        if (!on) return "Off. The pause above alone decides when you finished talking.";
        if (status is not { } s || !s.Available)
            return $"On, but the judge can't run here ({status?.Problem ?? "no judge"}). The pause above decides until it can.";
        var text = s.Problem is not null
            ? $"On. Smart Turn can't run here ({s.Problem}), so {s.Judge} judges instead."
            : $"On. {s.Judge} on this PC" + (s.LoadTime is { } load ? $" (loaded in {load.TotalMilliseconds:0} ms)." : ".");
        if (s.Decisions.Length == 0) return text + " No turns judged yet.";
        var complete = s.Decisions.Count(d => d.Outcome == Martlet.Conversation.EndOfTurnDecision.Complete);
        var unfinished = s.Decisions.Count(d => d.Outcome is Martlet.Conversation.EndOfTurnDecision.Incomplete or Martlet.Conversation.EndOfTurnDecision.WentOn);
        var plain = s.Decisions.Length - complete - unfinished;
        var times = s.Decisions.Where(d => d.JudgeTime is not null).Select(d => d.JudgeTime!.Value.TotalMilliseconds).Order().ToArray();
        text += $" Last {s.Decisions.Length} pause{(s.Decisions.Length == 1 ? "" : "s")}: {complete} finished, {unfinished} unfinished, {plain} left to the pause";
        if (times.Length > 0) text += $"; judge median {times[times.Length / 2]:0} ms";
        var last = s.Decisions[^1];
        return text + $". Last: {last.Outcome} after {last.Silence.TotalMilliseconds:0} ms of silence.";
    }

    private void ShowTurnJudge()
    {
        if (turnJudgeStatus is { } line && conversation is not null) line.Text = TurnJudgeText(Talk.JudgeTurns, conversation.TurnJudgeStatus);
    }

    // Companion › Listening's Start replies early status line while the page shows it (it follows each reply started early).
    private TextBlock? earlyRepliesStatus;

    internal const string EarlyRepliesAbout = "Martlet starts working on its reply in the short pause after you speak, before it's " +
        "sure you finished, and keeps it to itself until you have. When you stop, the reply is already on its way, so Martlet " +
        "answers sooner; when you keep talking, it drops that start and tries again at your next pause. Nothing is shown, said " +
        "or done before your turn ends. It needs Parakeet on this PC as Listening. With a cloud Thinking model, a dropped start " +
        "can still cost a little, so cloud models need the second box. Prepare the voice early too makes the first spoken words " +
        "ready before you finish as well (a paid cloud voice only with the second box).";

    /// <summary>Companion › Listening's Start replies early status (TalkEarlyRepliesStatus): whether it is on, whether replies
    /// can start early with this setup (or why not) and what became of the newest ones (never words or audio).</summary>
    internal static string EarlyRepliesText(TalkPreferences prefs, LiveConversationConfiguration? configured,
        IReadOnlyList<Martlet.Conversation.EarlyReplyRecord> records)
    {
        if (!prefs.EarlyReplies) return "Off. Martlet starts each reply once you finished talking.";
        if (configured is not null && (!configured.LocalStt() || configured.SttHostTarget() is not null))
            return "On, but replies start early only with Parakeet on this PC as Listening.";
        var (thinking, _, why) = LiveConversationController.EarlyAllowed(prefs.EarlyReplyOptions, configured);
        if (configured is not null && !thinking) return $"On, but not with this setup: {why}.";
        var text = configured is null ? "On." : $"On. {char.ToUpperInvariant(why[0])}{why[1..]}.";
        if (records.Count == 0) return text + " No replies started early yet.";
        var taken = records.Count(r => r.Outcome == Martlet.Conversation.EarlyReplyRecord.Promoted);
        var talkedOn = records.Count(r => r.Outcome == Martlet.Conversation.EarlyReplyRecord.Cancelled);
        var other = records.Count - taken - talkedOn;
        text += $" Last {records.Count}: {taken} taken as the reply, {talkedOn} let go because you went on talking" +
            (other > 0 ? $", {other} let go for another reason" : "") + ".";
        var last = records[^1];
        return text + $" Last: {last.Outcome} after {last.Waited.TotalMilliseconds:0} ms.";
    }

    private void ShowEarlyReplies()
    {
        if (earlyRepliesStatus is { } line && conversation is not null)
            line.Text = EarlyRepliesText(Talk, conversation.Configuration, conversation.EarlyReplies);
    }

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

    // Companion › Listening › When you talk over Martlet, in the order shown.
    private static readonly string[] BargeInStyleChoices = ["Pause and decide (recommended)", "Stop at once"];
    private static readonly Martlet.Conversation.BargeInBehavior[] BargeInStyleOrder =
        [Martlet.Conversation.BargeInBehavior.PauseAndDecide, Martlet.Conversation.BargeInBehavior.StopAtOnce];
    internal const string BargeInStyleAbout = "Pause and decide: a clear word like \"stop\" or \"wait\", or Martlet's name, still stops it " +
        "at once. Other words pause Martlet at once, and it decides in a moment: words for Martlet stop the reply and get an answer; " +
        "a quick \"yeah\", agreeing, laughing along, talking to someone else or a TV leaves the reply playing on from where it paused, " +
        "with nothing lost. Keep talking and it stops. Stop at once: real words stop the reply right away.";

    private TalkPreferences Talk => talk ??= TalkPreferences.Load(store?.DataDirectory);

    private void SaveTalk(TalkPreferences next, bool render = false)
    {
        var prior = Talk;
        var spoke = prior.SpeakReplies;
        talk = next;
        if (!next.Save(store?.DataDirectory))
            ActionText.Text = "Couldn't save your talk choices. They apply until Martlet closes.";
        avatar.Gaze.Decides = next.DecideGaze;
        avatar.Gaze.Configure(next.GazeUsual, next.GazeFree);
        if (conversation is not null)
        {
            conversation.VoiceVolume = next.VoiceVolume;
            conversation.QuickSounds = next.QuickSoundOptions;
        }
        // An open talk window follows the change right away.
        openConversation?.UsePreferences(next, visionAddress);
        if (spoke != next.SpeakReplies) FollowVoice(next.SpeakReplies);
        // The character profile in use keeps its own eyes and touch choice on this PC.
        if (prior.GazeUsual != next.GazeUsual || prior.GazeFree != next.GazeFree || prior.TouchInterrupts != next.TouchInterrupts)
            RememberProfileHere();
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

            var judgeTurns = new CheckBox { Content = "Judge when I finish talking (recommended)", IsChecked = prefs.JudgeTurns, Margin = new Thickness(0, 16, 0, 4) };
            AutomationProperties.SetAutomationId(judgeTurns, "TalkJudgeTurns");
            judgeTurns.Checked += (_, _) => { if (!Talk.JudgeTurns) SaveTalk(Talk with { JudgeTurns = true }, render: true); };
            judgeTurns.Unchecked += (_, _) => { if (Talk.JudgeTurns) SaveTalk(Talk with { JudgeTurns = false }, render: true); };
            children.Add(judgeTurns);
            var judgeStatus = Note(TurnJudgeText(prefs.JudgeTurns, conversation?.TurnJudgeStatus), new Thickness(0, 0, 0, 4));
            AutomationProperties.SetAutomationId(judgeStatus, "TalkJudgeTurnsStatus");
            turnJudgeStatus = judgeStatus;
            children.Add(judgeStatus);
            children.Add(HelpTip.Explain("On, a small model on this PC (Smart Turn) listens to how you end each sentence after a short pause. " +
                "When you clearly finished, Martlet answers sooner than the pause above; when you trail off mid-thought, it waits " +
                "longer (up to twice that pause) so it cuts you off less. If it ever answers too soon, just keep talking. Off, the " +
                "pause above alone decides. Your voice never leaves this PC for this.", new Thickness(0, 0, 0, 0), "SmartTurn", "Smart Turn"));

            var earlyReplies = new CheckBox { Content = "Start replies early (recommended)", IsChecked = prefs.EarlyReplies, Margin = new Thickness(0, 16, 0, 4) };
            AutomationProperties.SetAutomationId(earlyReplies, "TalkEarlyReplies");
            earlyReplies.Checked += (_, _) => { if (!Talk.EarlyReplies) SaveTalk(Talk with { EarlyReplies = true }, render: true); };
            earlyReplies.Unchecked += (_, _) => { if (Talk.EarlyReplies) SaveTalk(Talk with { EarlyReplies = false }, render: true); };
            children.Add(earlyReplies);
            if (prefs.EarlyReplies)
            {
                var earlyCloud = new CheckBox { Content = "Also for cloud models (may add a small cost)", IsChecked = prefs.EarlyRepliesCloud,
                    Margin = new Thickness(24, 2, 0, 2) };
                AutomationProperties.SetAutomationId(earlyCloud, "TalkEarlyRepliesCloud");
                earlyCloud.Checked += (_, _) => { if (!Talk.EarlyRepliesCloud) SaveTalk(Talk with { EarlyRepliesCloud = true }, render: true); };
                earlyCloud.Unchecked += (_, _) => { if (Talk.EarlyRepliesCloud) SaveTalk(Talk with { EarlyRepliesCloud = false }, render: true); };
                children.Add(earlyCloud);
                var earlyVoice = new CheckBox { Content = "Prepare the voice early too", IsChecked = prefs.EarlyVoice, Margin = new Thickness(24, 2, 0, 4) };
                AutomationProperties.SetAutomationId(earlyVoice, "TalkEarlyVoice");
                earlyVoice.Checked += (_, _) => { if (!Talk.EarlyVoice) SaveTalk(Talk with { EarlyVoice = true }, render: true); };
                earlyVoice.Unchecked += (_, _) => { if (Talk.EarlyVoice) SaveTalk(Talk with { EarlyVoice = false }, render: true); };
                children.Add(earlyVoice);
            }
            var earlyStatus = Note(EarlyRepliesText(prefs, conversation?.Configuration, conversation?.EarlyReplies ?? []), new Thickness(0, 0, 0, 4));
            AutomationProperties.SetAutomationId(earlyStatus, "TalkEarlyRepliesStatus");
            earlyRepliesStatus = earlyStatus;
            children.Add(earlyStatus);
            var earlyAbout = HelpTip.Explain(EarlyRepliesAbout, new Thickness(0, 0, 0, 0), "TalkEarlyReplies", "starting replies early");
            AutomationProperties.SetAutomationId(earlyAbout, "TalkEarlyRepliesAbout");
            children.Add(earlyAbout);

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
            var wordCheckAbout = HelpTip.Explain(WordCheckAbout, new Thickness(0, 4, 0, 0), "TalkWordCheck", "word check");
            AutomationProperties.SetAutomationId(wordCheckAbout, "TalkWordCheckAbout");
            children.Add(wordCheckAbout);

            var bargeIn = new CheckBox { Content = "Let me interrupt Martlet by talking", IsChecked = prefs.BargeIn, Margin = new Thickness(0, 16, 0, 4) };
            AutomationProperties.SetAutomationId(bargeIn, "TalkBargeIn");
            bargeIn.Checked += (_, _) => SaveTalk(Talk with { BargeIn = true });
            bargeIn.Unchecked += (_, _) => SaveTalk(Talk with { BargeIn = false });
            children.Add(bargeIn);
            var bargeInAbout = HelpTip.Explain("Optional, off by default. Either way Martlet keeps listening while it speaks (with Reduce echo from " +
                "my speakers on) and answers what you said once it finishes; Stop (or Esc) in the talk window interrupts it. Turn this " +
                "on and talking over it with real words stops the reply " +
                "and answers what you say: a word like \"stop\" or \"wait\" (or Martlet's name) right away, otherwise a few words. A hum, a " +
                "cough, laughter, a quick \"yeah\" or \"mm-hmm\" and what this PC plays never stop it. With Parakeet on this PC as Listening, " +
                "Martlet checks your words while you talk; otherwise once you pause. " +
                "With Reduce echo from my speakers on, this works through speakers too. If Martlet still stops itself, use headphones " +
                "or turn this off.", new Thickness(0, 0, 0, 0), "TalkOptional", "this setting");
            AutomationProperties.SetAutomationId(bargeInAbout, "TalkBargeInAbout");
            children.Add(bargeInAbout);
            var bargeInStyle = new ComboBox { Width = 240, ItemsSource = BargeInStyleChoices,
                SelectedIndex = Math.Max(0, Array.IndexOf(BargeInStyleOrder, prefs.BargeInStyle)) };
            AutomationProperties.SetName(bargeInStyle, "When you talk over Martlet");
            AutomationProperties.SetAutomationId(bargeInStyle, "TalkBargeInBehavior");
            bargeInStyle.SelectionChanged += (_, _) =>
            {
                if (bargeInStyle.SelectedIndex >= 0 && BargeInStyleOrder[bargeInStyle.SelectedIndex] != Talk.BargeInStyle)
                    SaveTalk(Talk with { BargeInStyle = BargeInStyleOrder[bargeInStyle.SelectedIndex] });
            };
            var bargeInStyleRow = Labeled("When you talk over Martlet", bargeInStyle);
            bargeInStyleRow.Margin = new Thickness(0, 8, 0, 0);
            children.Add(bargeInStyleRow);
            var bargeInStyleAbout = HelpTip.Explain(BargeInStyleAbout, new Thickness(0, 4, 0, 0), "TalkBargeInBehavior", "talking over Martlet");
            AutomationProperties.SetAutomationId(bargeInStyleAbout, "TalkBargeInBehaviorAbout");
            children.Add(bargeInStyleAbout);
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
    /// as you talking, and always listening can go on while Martlet speaks. The status line says how the last listen went.</summary>
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
            HelpTip.Explain("While the microphone listens, Martlet also hears what this PC plays (its own voice, videos, music) and removes " +
                "that from the microphone first, so it works without headphones and keeps listening to you while it speaks. Without " +
                "it (or when it can't run), always listening pauses while Martlet speaks so it doesn't hear itself. That sound is " +
                "only used to cancel the echo, on this PC; it is never saved or sent.", new Thickness(0, 0, 0, 0), "EchoCancel", "hearing this PC"));
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
        var describe = new CheckBox { Content = "Describe PC sounds", IsChecked = Talk.DescribePcSounds, Margin = new Thickness(0, 0, 0, 6),
            IsEnabled = Talk.HearPc };
        AutomationProperties.SetAutomationId(describe, "TalkDescribePcSounds");
        describe.Checked += (_, _) => { if (!Talk.DescribePcSounds) SaveTalk(Talk with { DescribePcSounds = true }, render: true); };
        describe.Unchecked += (_, _) => { if (Talk.DescribePcSounds) SaveTalk(Talk with { DescribePcSounds = false }, render: true); };
        var digest = conversation?.SoundDigest?.Status;
        var tagger = Martlet.Sherpa.SoundTagger.Included();
        var describeStatus = Note(SoundDigestStatus(Talk, conversation?.CanHearPc != false, digest,
            digest is null ? (tagger ? CpuSoundJudge.Label : null, tagger ? Martlet.Audio.SoundJudgeKind.Cpu : null) : (digest.Judge, digest.JudgeKind),
            conversation?.Clock ?? TimeProvider.System), new Thickness(0, 0, 0, 6));
        AutomationProperties.SetAutomationId(describeStatus, "TalkDescribePcSoundsStatus");
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
            describe, describeStatus,
            HelpTip.Explain("Words aren't everything: about every 10 seconds while something plays, Martlet describes the rest of the sound " +
                "(music and its mood, game or video sounds, laughter, applause, alarms) in one short line for its next reply. The " +
                "audio model gets a short clip first when it is a model of its own (Listening › Audio model); otherwise a Thinking pool " +
                "model that can hear does; without either, a small sound tagger on this PC's processor names " +
                "what it hears. The last few seconds of sound stay in memory only; the line is never saved or remembered, and a " +
                "reply never waits for it.", new Thickness(0, 0, 0, 8), "SoundDigest", "sound descriptions"),
            Note("How chatty Martlet is about it (the same choice as Vision's How often it comments):", new Thickness(0, 0, 0, 4)),
            .. ChattinessPicker("TalkPcChattiness")]);
    }

    /// <summary>Companion › Listening › Describe PC sounds' status: off or why it can't run, else which judge describes the
    /// sound and the last line with its age (in memory only, never saved).</summary>
    internal static string SoundDigestStatus(TalkPreferences prefs, bool available, Martlet.Audio.SoundDigestStatus? status,
        (string? Name, Martlet.Audio.SoundJudgeKind? Kind) judge, TimeProvider clock) =>
        !prefs.HearPc ? "Works while Hear what this PC plays is on."
        : !prefs.DescribePcSounds ? "Off. Only the words this PC plays reach Thinking."
        : !available ? "Martlet can't hear what this PC plays here."
        : $"On. {PcSoundDigest.JudgeText(judge.Name, judge.Kind)} " +
            (status?.On == true ? "Describing what plays now. " : "Runs while Martlet hears this PC. ") +
            PcSoundDigest.LastText(status?.Last, clock);

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

    // ---------- Listening: let Thinking (or the audio model) hear your voice ----------

    /// <summary>Companion › Hearing › Hear how you say it: whether a Thinking model that hears also gets the recording of what you
    /// said, or, with an audio model of its own (Companion › Hearing, docs/SENSE_MODELS.md), whether that model hears it and
    /// describes how you sound for Thinking. Your own choice always wins; never chosen, it is on only while the recording stays on
    /// this PC where it goes (Ollama on this PC, not a cloud model), and anywhere else ticking it is the consent. The text under it
    /// says which applies, what is sent and where; the Now card above says whether the model hears (TalkHearVoiceStatus).</summary>
    private Border HearVoiceCard()
    {
        var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var abilities = SavedModelAbilities();
        var audio = Martlet.Conversation.SenseRouting.For(SenseKind.Audio, SenseModels.Load(store?.DataDirectory), thinking, abilities);
        // An audio model of its own takes recordings in Thinking's place: Thinking never gets one then.
        var own = audio.Model;
        var hears = own is not null ? audio.Described : LiveConversationConfiguration.Hearing(thinking, abilities) == HearingSupport.Supported;
        var on = (own is not null ? Talk.HearVoiceFor(VoiceNotes.StaysOnThisPc(own)) : Talk.HearVoiceFor(thinking)).On;
        var hear = new CheckBox { Content = own is not null ? "Let the audio model hear my voice" : "Let Thinking hear my voice",
            IsChecked = on, Margin = new Thickness(0, 0, 0, 6), IsEnabled = on || hears };
        AutomationProperties.SetAutomationId(hear, "TalkHearVoice");
        hear.Checked += (_, _) => { if (Talk.HearVoice != true) SaveTalk(Talk with { HearVoice = true }, render: true); };
        hear.Unchecked += (_, _) => { if (Talk.HearVoice != false) SaveTalk(Talk with { HearVoice = false }, render: true); };
        var choice = Note(LiveConversationConfiguration.HearVoiceChoice(Talk.HearVoice, thinking, audio), new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(choice, "TalkHearVoiceChoice");
        // The straight path and Test hearing are about Thinking hearing you, which an audio model of its own replaces.
        return Card([Heading("Hear how you say it"), hear, choice, .. hears && on && own is null ? VoicePathControls() : [],
            .. own is null ? HearingTestControls(thinking) : [],
            Note(LiveConversationConfiguration.HearingDisclosure(thinking, audio), new Thickness(0, 6, 0, 0))]);
    }

    /// <summary>Companion › Listening › When Thinking can hear you, shown while Thinking hears your voice: your voice straight to
    /// Thinking (the default; speech-to-text runs beside the reply for the talk window, history and memory) or the transcript
    /// first, then both.</summary>
    private UIElement[] VoicePathControls()
    {
        var label = new TextBlock { Text = "When Thinking can hear you", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 6) };
        var straight = Choice("TalkVoicePath", "Send my voice straight to Thinking (fastest)",
            "The reply starts the moment you stop talking. Your words are still transcribed in the background for the talk window, " +
            "the conversation history and memory.", !Talk.TranscribeFirst, "TalkVoicePathStraight");
        var first = Choice("TalkVoicePath", "Transcribe first, then send both",
            "Martlet waits for speech-to-text, then sends Thinking your recording with the transcript.", Talk.TranscribeFirst,
            "TalkVoicePathTranscribeFirst");
        straight.Checked += (_, _) => { if (Talk.TranscribeFirst) SaveTalk(Talk with { TranscribeFirst = false }, render: true); };
        first.Checked += (_, _) => { if (!Talk.TranscribeFirst) SaveTalk(Talk with { TranscribeFirst = true }, render: true); };
        var status = Note(VoicePathStatus(Talk), new Thickness(0, 0, 0, 6));
        AutomationProperties.SetAutomationId(status, "TalkVoicePathStatus");
        return [label, straight, first, status];
    }

    internal static string VoicePathStatus(TalkPreferences prefs) => prefs.TranscribeFirst
        ? "Transcribe first: each reply waits for speech-to-text, then Thinking gets your recording and the transcript."
        : "Straight to Thinking: Thinking gets your recording alone and answers right away. What you said appears in the talk " +
          "window once it is transcribed. A message with what this PC plays, one said over Martlet while it speaks, or one for " +
          "Home Assistant's Assist is transcribed first.";

    // ---------- Voice: speak replies (Mute voice on the character's menu is the same choice) ----------

    private CheckBox? speakRepliesCheck;

    private Border SpeakRepliesCard()
    {
        var speak = speakRepliesCheck = new CheckBox { Content = "Speak Martlet's replies aloud", IsChecked = Talk.SpeakReplies };
        AutomationProperties.SetAutomationId(speak, "SpeakReplies");
        speak.Checked += (_, _) => { if (!Talk.SpeakReplies) SaveTalk(Talk with { SpeakReplies = true }); };
        speak.Unchecked += (_, _) => { if (Talk.SpeakReplies) SaveTalk(Talk with { SpeakReplies = false }); };

        var volume = new Slider { Minimum = 0, Maximum = 100, Value = Math.Round(Talk.VoiceVolume * 100), SmallChange = 5,
            LargeChange = 10, TickFrequency = 5, IsSnapToTickEnabled = true, Width = 240, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(volume, "Voice volume: how loud Martlet speaks and sings");
        AutomationProperties.SetAutomationId(volume, "VoiceVolume");
        var level = Note(VoiceVolumeLabel(Talk.VoiceVolume), new Thickness(8, 0, 0, 0));
        level.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(level, "VoiceVolumeLevel");
        volume.ValueChanged += (_, e) =>
        {
            var next = e.NewValue / 100;
            level.Text = VoiceVolumeLabel(next);
            if (Math.Abs(next - Talk.VoiceVolume) > 0.001) SaveTalk(Talk with { VoiceVolume = next });
        };
        var scale = new StackPanel { Orientation = Orientation.Horizontal };
        scale.Children.Add(volume);
        scale.Children.Add(level);
        var volumeRow = Labeled("Voice volume", scale);
        volumeRow.Margin = new Thickness(0, 12, 0, 0);

        return Card(Heading("In conversations"), speak,
            HelpTip.Explain("Martlet speaks each reply and shows it in the talk window. Turn this off for text-only replies: they show in the " +
                "talk window and the character's speech bubble. Mute voice and Unmute voice on the character's right-click menu " +
                "change this too.", new Thickness(0, 6, 0, 0), "TalkSpeaks", "spoken replies"),
            volumeRow,
            Note("How loud Martlet speaks and sings, on this PC. A reply or song playing now follows at once. Windows' own volume " +
                "and other apps aren't changed.", new Thickness(0, 4, 0, 0)));
    }

    /// <summary>Companion › Voice › Voice volume's level, as a percentage ("80%").</summary>
    internal static string VoiceVolumeLabel(double volume) => $"{Math.Round(Martlet.Audio.PcmGain.Clamp(volume) * 100):0}%";

    // ---------- Voice: quick sounds while Martlet thinks ----------

    private TextBlock? quickSoundsStatus;
    private Button? quickSoundsMake;
    private static readonly string[] QuickSoundDelayNames = ["After 0.5 s", "After 0.7 s (recommended)", "After 1 s", "After 1.5 s"];

    private Border QuickSoundsCard()
    {
        var prefs = Talk;
        var on = new CheckBox { Content = "Play a quick sound while Martlet thinks", IsChecked = prefs.QuickSounds };
        AutomationProperties.SetAutomationId(on, "VoiceQuickSounds");
        on.Checked += (_, _) => { if (!Talk.QuickSounds) SaveTalk(Talk with { QuickSounds = true }, render: true); };
        on.Unchecked += (_, _) => { if (Talk.QuickSounds) SaveTalk(Talk with { QuickSounds = false }, render: true); };
        var delay = new ComboBox
        {
            Width = 240, ItemsSource = QuickSoundDelayNames,
            SelectedIndex = Math.Max(0, Martlet.Conversation.QuickSoundOptions.DelayChoices.ToList().IndexOf(prefs.QuickSoundDelayMs))
        };
        AutomationProperties.SetName(delay, "How long a reply may stay silent before a quick sound plays");
        AutomationProperties.SetAutomationId(delay, "VoiceQuickSoundsDelay");
        delay.SelectionChanged += (_, _) =>
        {
            if (delay.SelectedIndex >= 0 && Martlet.Conversation.QuickSoundOptions.DelayChoices[delay.SelectedIndex] is var ms && ms != Talk.QuickSoundDelayMs)
                SaveTalk(Talk with { QuickSoundDelayMs = ms });
        };
        var status = quickSoundsStatus = Note("", new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(status, "VoiceQuickSoundsStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        quickSoundsMake = PageButton("Make quick sounds now", () =>
        {
            if (conversation?.MakeQuickSounds() != true) ActionText.Text = "Quick sounds need a voice: choose one above first.";
        }, link: true, id: "VoiceQuickSoundsMake");
        ShowQuickSounds();
        var delayRow = Labeled("Play one", delay);
        delayRow.Margin = new Thickness(0, 10, 0, 0);
        return Card(Heading("Quick sounds while Martlet thinks"), on,
            HelpTip.Explain("When a reply is slow to start, Martlet first says a quick \"Mm,\" or \"Hmm...\" in its own voice, and the reply " +
                "follows it. It never plays when the reply is quick, never twice in one reply and at most once every 20 seconds. " +
                "The sounds are made once with your voice and kept on this PC; with a paid cloud voice, Martlet makes them only " +
                "when you press Make quick sounds now (one short request for each).", new Thickness(0, 6, 0, 0), "Fillers", "quick sounds"),
            delayRow, status, Row(quickSoundsMake));
    }

    // Companion › Voice's quick sounds line and its Make button, as the conversation says they stand.
    private void ShowQuickSounds()
    {
        if (quickSoundsStatus is not { } line) return;
        line.Text = QuickSoundsText(Talk.QuickSounds, conversation?.QuickSoundStatus, Talk.QuickSoundDelayMs);
        if (quickSoundsMake is { } make)
            make.Visibility = Talk.QuickSounds && conversation?.QuickSoundStatus.State is not (null or QuickSoundState.Making or QuickSoundState.NoVoice)
                ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Companion › Voice › Quick sounds while Martlet thinks, in words (VoiceQuickSoundsStatus).</summary>
    internal static string QuickSoundsText(bool on, (QuickSoundState State, string? Voice, int Clips, string? Problem)? status, int delayMs)
    {
        if (!on) return "Off.";
        var after = (delayMs / 1000.0).ToString("0.0#", System.Globalization.CultureInfo.CurrentCulture);
        return status?.State switch
        {
            QuickSoundState.Ready => $"On: {status.Value.Clips} quick sounds in {status.Value.Voice}. One plays when a reply has no audio " +
                $"of its own {after} s after Martlet starts answering (sooner when Thinking thinks first)." +
                (status.Value.Problem is { } kept ? $" Note: {kept}." : ""),
            QuickSoundState.Making => $"On. Making the quick sounds with {status.Value.Voice}...",
            QuickSoundState.NeedsClick => $"On, but {status.Value.Voice} is a paid cloud voice: press Make quick sounds now to make them " +
                "once (one short request for each). Until then none play.",
            QuickSoundState.Failed => $"On, but the quick sounds couldn't be made with {status.Value.Voice} ({status.Value.Problem}). " +
                "Press Make quick sounds now to try again.",
            _ => "On, but Martlet has no voice to make them with yet: set up Martlet's voice first."
        };
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
        catch (Exception error) when (error is OperationCanceledException || RendererFailures.Is(error, lifetime.Token))
        {
            if (!closing && RendererFailures.Is(error, lifetime.Token))
                RendererFailures.Log("The character's menu couldn't be told whether Martlet's voice is muted", error);
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

    /// <summary>Companion › Vision (an optional extra, in the standard order): Now (whether vision is on, what Martlet looks at,
    /// whether it can see and the image model's lines), then the main choice (Off, or the image model: Thinking's own model, the
    /// audio model, Ollama on this PC, a cloud provider or server, or one of your computers), then what it looks at, how chatty it
    /// is, its glances and screen summary, and what it sends. Vision is on by default, at your whole screen. Pressing Start
    /// watching is the consent; What Martlet sends says exactly what is captured and where it is sent.</summary>
    private void RenderVisionPage(Panel page)
    {
        var prefs = Talk;
        var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var abilities = SavedModelAbilities();
        // Martlet can see when its pictures go to Thinking or to an image model of its own (docs/SENSE_MODELS.md).
        var image = SavedImageRoute(thinking, abilities);
        var canSee = thinking is not null && image.Path != SensePath.None;
        var source = VisionSource(prefs);
        var chosen = source.IsScreen || source.Id.Length > 0;
        var chattiness = ChattinessTags.Choice(prefs.ScreenChattiness);

        var nowText = new TextBlock
        {
            Text = prefs.Watch
                ? $"On. Martlet looks at {source.Label} occasionally. Comments: {CommentsLabel(chattiness)}."
                : $"Off. {OptionalExtras.OffMeans(CompanionTab.Vision)}.",
            FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6)
        };
        AutomationProperties.SetAutomationId(nowText, "VisionNow");
        var now = new List<UIElement> { Heading("Now"), nowText };
        if (prefs.Watch && !chosen) now.Add(Warning(source.Kind == WatchKind.Camera ? "Choose a camera below." : "Enter the camera address below."));
        var advice = LiveConversationConfiguration.VisionAdvice(thinking, abilities, image);
        var adviceText = canSee ? Note(advice, new Thickness(0, 0, 0, 6)) : Warning(advice);
        AutomationProperties.SetAutomationId(adviceText, "VisionStatus");
        now.Add(adviceText);
        now.AddRange(SenseNowLines(SenseKind.Image));
        page.Children.Add(Card([.. now]));

        // The main choice: Off, or the image model. Its buttons turn vision off or on (VisionToggle).
        Button? turnOn = null;
        page.Children.Add(SenseChoiceCard(SenseKind.Image, prefs.Watch,
            () => PageButton("Turn vision off", () =>
            {
                SaveTalk(Talk with { Watch = false }, render: true);
                ActionText.Text = "Vision is off.";
            }, primary: true, id: "VisionToggle"),
            () =>
            {
                turnOn = PageButton("Turn vision on", () =>
                {
                    SaveTalk(Talk with { Watch = true }, render: true);
                    ActionText.Text = "Vision is on. Press Start watching on Home or in the talk window when you want Martlet to look.";
                }, primary: true, id: "VisionToggle");
                turnOn.IsEnabled = canSee && chosen;
                if (!turnOn.IsEnabled)
                {
                    var why = !canSee ? advice : source.Kind == WatchKind.Camera ? "Choose a camera below first." : "Enter the camera address below first.";
                    turnOn.ToolTip = why;
                    ToolTipService.SetShowOnDisabled(turnOn, true);
                    AutomationProperties.SetHelpText(turnOn, why);
                }
                return turnOn;
            },
            "Vision is off. Turn it on to use this model."));

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
                if (turnOn is not null && !Talk.Watch) turnOn.IsEnabled = canSee && typed.Length > 0;
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
        page.Children.Add(ScreenSummaryCard(prefs, image.Path == SensePath.Described));

        // What Martlet captures and where it goes, for what it looks at and how chatty it is (Turn vision on is above).
        var disclosure = Note(LiveConversationConfiguration.ScreenDisclosure(thinking, chattiness, source, image), new Thickness(0, 0, 0, 0));
        AutomationProperties.SetAutomationId(disclosure, "VisionDisclosure");
        page.Children.Add(Card(Heading("What Martlet sends"), disclosure));
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

    /// <summary>Companion › Vision › Glances at your screen: the character keeps its usual gaze (the default; Companion ›
    /// Character › Where the character looks), or Martlet decides with each new screenshot of your screen whether to glance at
    /// something on it.</summary>
    private Border GazeCard(TalkPreferences prefs)
    {
        var mouse = Choice("VisionGaze", "Keep its usual gaze",
            "The character's eyes do what Companion › Eyes › Where the character looks says: by default, they follow your mouse.",
            !prefs.DecideGaze, "VisionGaze-Mouse");
        var decide = Choice("VisionGaze", "Martlet decides",
            "With each new screenshot of your screen, the character keeps its usual gaze or glances at something interesting on " +
            "the screen: something that just popped up or moved, or what it is about to remark on.",
            prefs.DecideGaze, "VisionGaze-Martlet");
        mouse.Checked += (_, _) => { if (Talk.DecideGaze) SaveTalk(Talk with { DecideGaze = false }, render: true); };
        decide.Checked += (_, _) => { if (!Talk.DecideGaze) SaveTalk(Talk with { DecideGaze = true }, render: true); };
        var status = Note(GazeStatus(prefs), new Thickness(0, 2, 0, 0));
        AutomationProperties.SetAutomationId(status, "VisionGazeStatus");
        return Card(Heading("Glances at your screen"), mouse, decide, status,
            Note("It decides while vision watches your active window or whole screen and the character shows. Screenshots are " +
                "compared on this PC; the Thinking model only chooses during the looks Martlet already takes, so nothing extra " +
                "is sent.", new Thickness(0, 6, 0, 0)));
    }

    /// <summary>Companion › Vision › Screen summary over time (on by default): while Martlet watches, an image model of its own
    /// (<paramref name="byImageModel"/>, the saved choice) or a Thinking model that sees in the Thinking pool sums up in the
    /// background what changed on the screen, and the next reply gets it as a note.</summary>
    private Border ScreenSummaryCard(TalkPreferences prefs, bool byImageModel)
    {
        var canSee = byImageModel || conversation?.ScreenDigestThinker.CanSee == true;
        var box = new CheckBox { Content = "Screen summary over time", IsChecked = prefs.ScreenSummary, Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetAutomationId(box, "VisionScreenSummary");
        box.Checked += (_, _) => { if (!Talk.ScreenSummary) SaveTalk(Talk with { ScreenSummary = true }, render: true); };
        box.Unchecked += (_, _) => { if (Talk.ScreenSummary) SaveTalk(Talk with { ScreenSummary = false }, render: true); };
        var text = ScreenSummaryStatus(prefs.ScreenSummary, prefs.Watch, canSee, byImageModel);
        var status = prefs.ScreenSummary && prefs.Watch && !canSee ? Warning(text) : Note(text, new Thickness(0, 0, 0, 6));
        AutomationProperties.SetAutomationId(status, "VisionScreenSummaryStatus");
        return Card(Heading("Screen summary over time"), box, status,
            Note("Each reply gets only the newest picture, so it starts to speak as fast as before. With this on, Martlet also " +
                "keeps the last few pictures that changed (about 25 seconds, in memory only, never saved) and, every 15 seconds " +
                "while they change or when you start to talk, sends a small sheet of them with the text read on them to your " +
                "image model (when you chose one) or a Thinking model that sees in the Thinking pool, in the background. It is " +
                "never the model your conversation uses. The one or two lines it " +
                "answers (\"They switched from VS Code to a boss fight\") go with your next message as a note. A reply never " +
                "waits for it. Each summary is one more request with a picture, which may cost more.", new Thickness(0, 6, 0, 0)));
    }

    internal static string ScreenSummaryStatus(bool on, bool vision, bool canSee, bool byImageModel = false) =>
        !on ? "Off. Replies know only the newest picture."
        : !vision ? "On, but vision is off. Turn vision on above."
        : !canSee ? "Off for now: the Thinking pool has no other model that sees. Add one that sees in Companion › Thinking pool."
        : byImageModel ? "On. While Martlet watches, your image model sums up what changed for your next message."
        : "On. While Martlet watches, a Thinking model that sees sums up what changed for your next message.";

    private string GazeStatus(TalkPreferences prefs)
    {
        var usual = $"The character {CharacterGazeService.Describe(avatar.Gaze.Settings.Mode)}.";
        if (!prefs.DecideGaze) return usual;
        if (!prefs.Watch) return "Vision is off, so the character keeps its usual gaze. Turn vision on above. " + usual;
        if (VisionSource(prefs) is { IsScreen: false }) return "Martlet decides only while it watches your screen; with a camera the character keeps its usual gaze. " + usual;
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
