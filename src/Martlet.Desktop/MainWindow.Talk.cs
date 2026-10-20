using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Settings;
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
    // Companion › Listening's end-of-turn judge status line while the page shows it (it follows each decision).
    private TextBlock? turnJudgeStatus;

    /// <summary>Companion › Listening's end-of-turn judge status (TalkJudgeTurnsStatus): whether it is on, which judge runs and
    /// whether it can, and how the newest turns were decided (no words).</summary>
    internal static string TurnJudgeText(bool on,
        (string? Judge, bool Available, string? Problem, TimeSpan? LoadTime, Martlet.Conversation.EndOfTurnDecision[] Decisions)? status)
    {
        if (!on) return "Off: the pause above alone decides when you finished talking.";
        if (status is not { } s || !s.Available)
            return $"On, but the judge can't run here ({status?.Problem ?? "no judge"}), so the pause above decides until it can.";
        var text = s.Problem is not null
            ? $"On, but Smart Turn can't run here ({s.Problem}), so {s.Judge} judges instead"
            : $"On: {s.Judge} on this PC" + (s.LoadTime is { } load ? $" (loaded in {load.TotalMilliseconds:0} ms)" : "");
        if (s.Decisions.Length == 0) return text + ", and no turns are judged yet.";
        var complete = s.Decisions.Count(d => d.Outcome == Martlet.Conversation.EndOfTurnDecision.Complete);
        var unfinished = s.Decisions.Count(d => d.Outcome is Martlet.Conversation.EndOfTurnDecision.Incomplete or Martlet.Conversation.EndOfTurnDecision.WentOn);
        var plain = s.Decisions.Length - complete - unfinished;
        var times = s.Decisions.Where(d => d.JudgeTime is not null).Select(d => d.JudgeTime!.Value.TotalMilliseconds).Order().ToArray();
        text += $"; last {s.Decisions.Length} pause{(s.Decisions.Length == 1 ? "" : "s")}: {complete} finished, {unfinished} unfinished, {plain} left to the pause";
        if (times.Length > 0) text += $", judge median {times[times.Length / 2]:0} ms";
        var last = s.Decisions[^1];
        return text + $"; last: {last.Outcome} after {last.Silence.TotalMilliseconds:0} ms of silence.";
    }

    private void ShowTurnJudge()
    {
        if (turnJudgeStatus is { } line && conversation is not null) line.Text = TurnJudgeText(Talk.JudgeTurns, conversation.TurnJudgeStatus);
    }

    // Companion › Listening's Start replies early status line while the page shows it (it follows each reply started early).
    private TextBlock? earlyRepliesStatus;

    internal const string EarlyRepliesAbout = "Martlet starts its reply in the short pause after you speak and keeps it to itself " +
        "until your turn ends, so it answers sooner. It needs Parakeet on this PC as Listening, and with a cloud model a dropped " +
        "start can cost a little, so cloud models need the second box.";

    /// <summary>Companion › Listening's Start replies early status (TalkEarlyRepliesStatus): whether it is on, whether replies
    /// can start early with this setup (or why not) and what became of the newest ones (never words or audio).</summary>
    internal static string EarlyRepliesText(TalkPreferences prefs, LiveConversationConfiguration? configured,
        IReadOnlyList<Martlet.Conversation.EarlyReplyRecord> records)
    {
        if (!prefs.EarlyReplies) return "Off: Martlet starts each reply once you finished talking.";
        if (configured is not null && (!configured.LocalStt() || configured.SttHostTarget() is not null))
            return "On, but replies start early only with Parakeet on this PC as Listening.";
        var (thinking, _, why) = LiveConversationController.EarlyAllowed(prefs.EarlyReplyOptions, configured);
        if (configured is not null && !thinking) return $"On, but not with this setup: {why}.";
        var text = configured is null ? "On" : $"On: {why}";
        if (records.Count == 0) return text + (configured is null ? ", and" : ";") + " no replies started early yet.";
        var taken = records.Count(r => r.Outcome == Martlet.Conversation.EarlyReplyRecord.Promoted);
        var talkedOn = records.Count(r => r.Outcome == Martlet.Conversation.EarlyReplyRecord.Cancelled);
        var other = records.Count - taken - talkedOn;
        text += $"; last {records.Count}: {taken} taken as the reply, {talkedOn} let go because you went on talking" +
            (other > 0 ? $", {other} let go for another reason" : "");
        var last = records[^1];
        return text + $"; last: {last.Outcome} after {last.Waited.TotalMilliseconds:0} ms.";
    }

    private void ShowEarlyReplies()
    {
        if (earlyRepliesStatus is { } line && conversation is not null)
            line.Text = EarlyRepliesText(Talk, conversation.Configuration, conversation.EarlyReplies);
    }

    private static readonly string[] ChattinessChoices = ["Quiet", "Normal", "Chatty", "Martlet decides"];
    internal const string WordCheckAbout = "Martlet ignores sounds that aren't words (mm, a cough, laughter) and the words " +
        "speech-to-text makes up from noise, so they never get a reply. Relaxed ignores more, Sensitive also takes single short " +
        "words, and what Martlet ignored shows faded in the talk window.";

    internal const string BargeInStyleAbout = "Pause and decide pauses Martlet at once and lets it carry on when your words weren't " +
        "for it; \"stop\", \"wait\" or its name still stop it. Stop at once stops the reply at any real words.";

    private TalkPreferences Talk => talk ??= TalkPreferences.Load(store?.DataDirectory);

    private void SaveTalk(TalkPreferences next, bool render = false)
    {
        var prior = Talk;
        var spoke = prior.SpeakReplies;
        talk = next;
        if (!next.Save(store?.DataDirectory))
            ActionText.Text = "Couldn't save your talk choices, so they apply only until Martlet closes.";
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

    // The Listening cards (How you talk, Speakers and echo, Watch along) are ListeningCards in Pages\Listening, and Voice's talk
    // cards are TalkCards in Pages\Talk; MainWindow gives them their services in MainWindow.ListeningCards.cs.

    private void SetUpVoiceId()
    {
        if (closing) return;
        if (setupOperations.IsRunning) { ActionText.Text = "Martlet is busy with another task; try again when it finishes."; return; }
        if (voiceIdentity.DataDirectory is null) { ActionText.Text = "Voice ID needs Martlet's data folder."; return; }
        new VoiceIdWindow(voiceIdentity, setupOperations, audioSessionEvents, homeSettings?.Audio?.Input) { Owner = this }.ShowDialog();
        RenderTab();
    }

    // ---------- Listening: echo from the speakers ----------

    internal static (string Text, bool Problem) EchoStatus(bool on, EchoReductionReport? report) => !on
        ? ("Off, so through speakers Martlet may hear its own voice and what this PC plays (use headphones, or turn this on).", false)
        : report switch
        {
            { State: EchoReductionState.Active, SpeakerFrames: > 0 } =>
                ("On: the last time Martlet listened, it removed what your speakers played from the microphone.", false),
            { State: EchoReductionState.Active } => ("On: the last time Martlet listened, your speakers were quiet.", false),
            { State: EchoReductionState.NoSpeakerAudio or EchoReductionState.Unavailable, Problem: { } why } => (why, true),
            _ => ("On: Martlet removes what this PC plays from the microphone whenever it listens.", false)
        };

    // ---------- Listening: hear what this PC plays ----------

    /// <summary>The sound judge the saved settings choose before a conversation runs, as <see cref="SenseJobRouting"/> decides
    /// <see cref="SenseJobKind.PcSound"/> (the conversation's <see cref="LiveConversationController.SoundJudge"/> follows the same
    /// decision): the audio model while it takes recordings, else the first Thinking pool model that hears and takes quick jobs, else
    /// the CPU sound tagger when it is in Martlet's folder.</summary>
    private (string? Name, Martlet.Audio.SoundJudgeKind? Kind) SavedSoundJudge() => SoundJudgeOf(SavedSenseDecision(SenseJobKind.PcSound));

    /// <summary>The judge a <see cref="SenseJobKind.PcSound"/> decision names, as the digest's status shows it.</summary>
    internal static (string? Name, Martlet.Audio.SoundJudgeKind? Kind) SoundJudgeOf(SenseDecision decision) => decision.Taker switch
    {
        SenseTaker.SenseModel => (decision.Name, Martlet.Audio.SoundJudgeKind.AudioModel),
        SenseTaker.PoolMember => (decision.Name, Martlet.Audio.SoundJudgeKind.Pool),
        SenseTaker.ThisPc => (CpuSoundJudge.Label, Martlet.Audio.SoundJudgeKind.Cpu),
        _ => (null, null)
    };

    /// <summary>Companion › Listening › Describe PC sounds' status: off or why it can't run, else which judge describes the
    /// sound and the last line with its age (in memory only, never saved).</summary>
    internal static string SoundDigestStatus(TalkPreferences prefs, bool available, Martlet.Audio.SoundDigestStatus? status,
        (string? Name, Martlet.Audio.SoundJudgeKind? Kind) judge, TimeProvider clock) =>
        !prefs.HearPc ? "Works while Hear what this PC plays is on."
        : !prefs.DescribePcSounds ? "Off: only the words this PC plays reach Thinking."
        : !available ? "Martlet can't hear what this PC plays here."
        : $"On. {PcSoundDigest.JudgeText(judge.Name, judge.Kind)} " +
            (status?.On == true ? "Describing what plays now. " : "Runs while Martlet hears this PC. ") +
            PcSoundDigest.LastText(status?.Last, clock);

    internal static (string Text, bool Problem) PcAudioStatus(TalkPreferences prefs, bool available, bool? withoutMartlet,
        string? output = null, string? elsewhere = null) =>
        !prefs.HearPc ? ("Off: Martlet hears only your microphone.", false)
        : !available ? ("Martlet can't hear what this PC plays here.", true)
        : !prefs.HandsFree ? ("On, but it works only with Always listening (push-to-talk hears only your microphone).", true)
        : !prefs.ReduceEcho ? ("On, but through speakers keep Reduce echo from my speakers on (or use headphones), or the microphone also " +
            "hears the PC and takes it for you.", true)
        : withoutMartlet == false ? ((elsewhere is null ? "On, but this Windows can't leave Martlet's own voice out, so " :
            $"On, but {elsewhere} is in use too (a virtual cable there can carry your own voice), so ") +
            $"Martlet hears only what plays on {output ?? "your speakers"} and stops hearing it while it speaks.", false)
        : ("On: while Martlet listens, it also hears what this PC plays, without its own voice.", false);

    // ---------- Listening: let Thinking (or the audio model) hear your voice ----------

    /// <summary>Companion › Hearing › Hear how you say it: whether a Thinking model that hears also gets the recording of what you
    /// said, or, with an audio model of its own (Companion › Hearing, docs/SENSE_MODELS.md), whether that model hears it and
    /// describes how you sound for Thinking. Your own choice always wins; never chosen, it is on only while the recording stays on
    /// this PC where it goes (Ollama on this PC, not a cloud model), and anywhere else ticking it is the consent. The text under it
    /// says which applies, what is sent and where; the Now card above says whether the model hears (TalkHearVoiceStatus).</summary>
    private Border HearVoiceCard()
    {
        var thinking = homeSettings.ThinkingRoute();
        var abilities = SavedModelAbilities();
        var audio = Martlet.Conversation.SenseRouting.For(SenseKind.Audio, SavedSenses(), thinking, abilities);
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
            HelpTip.Explain(LiveConversationConfiguration.HearingDisclosure(thinking, audio), new Thickness(0, 6, 0, 0), "TalkHearVoiceAbout",
                "hearing your voice", wiki: "Talking-to-Martlet#hear-how-you-say-it")]);
    }

    /// <summary>Companion › Listening › When Thinking can hear you, shown while Thinking hears your voice: your voice straight to
    /// Thinking (the default; speech-to-text runs beside the reply for the talk window, history and memory) or the transcript
    /// first, then both.</summary>
    private UIElement[] VoicePathControls()
    {
        var label = new TextBlock { Text = "When Thinking can hear you", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 6) };
        var straight = Choice("TalkVoicePath", "Send my voice straight to Thinking (fastest)",
            "The reply starts the moment you stop talking, and your words are still transcribed for the talk window, history and memory.",
            !Talk.TranscribeFirst, "TalkVoicePathStraight");
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
        : "Straight to Thinking: Thinking gets your recording alone and answers right away, and your words appear once transcribed.";

    // ---------- Voice: speak replies (Mute voice on the character's menu is the same choice) ----------

    private CheckBox? speakRepliesCheck;

    /// <summary>Companion › Voice › Voice volume's level, as a percentage ("80%").</summary>
    internal static string VoiceVolumeLabel(double volume) => $"{Math.Round(Martlet.Audio.PcmGain.Clamp(volume) * 100):0}%";

    // ---------- Voice: quick sounds while Martlet thinks ----------

    private TextBlock? quickSoundsStatus;
    private Button? quickSoundsMake;

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
            QuickSoundState.Ready => $"On: {status.Value.Clips} quick sounds in {status.Value.Voice}, one plays when a reply is silent for {after} s." +
                (status.Value.Problem is { } kept ? $" Note: {kept}." : ""),
            QuickSoundState.Making => $"On: making the quick sounds with {status.Value.Voice}...",
            QuickSoundState.NeedsClick => $"On, but {status.Value.Voice} is a paid cloud voice, so none play until you press Make quick " +
                "sounds now (one short request for each).",
            QuickSoundState.Failed => $"On, but the quick sounds couldn't be made with {status.Value.Voice} ({status.Value.Problem}); " +
                "press Make quick sounds now to try again.",
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
                ? "Martlet's voice is muted, so replies show as text (Unmute voice on the character's right-click menu turns it on)."
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

    // Companion › Vision is VisionPage in Pages\Vision; MainWindow gives it its services in MainWindow.VisionPage.cs.

    /// <summary>What Martlet looks at, with a camera address typed this session before the saved one.</summary>
    private WatchSource VisionSource(TalkPreferences prefs) => VisionPageModel.SourceOf(prefs, visionAddress);

    // Martlet switched how chatty it is: Vision and Listening say the level it picked.
    private void FollowChattiness()
    {
        if (!closing && openTab is CompanionTab.Listening or CompanionTab.Vision && !tabEdited &&
            ChattinessTags.Choice(Talk.ScreenChattiness) == ChattinessChoice.MartletDecides)
            RenderTab();
    }

    // ---------- small builders ----------

    // The radio Choice builder is in Pages\PageKit.cs.

    internal static DockPanel Labeled(string label, UIElement control)
    {
        var row = new DockPanel { Margin = new Thickness(28, 6, 0, 0), LastChildFill = false };
        row.Children.Add(new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(control);
        return row;
    }
}
