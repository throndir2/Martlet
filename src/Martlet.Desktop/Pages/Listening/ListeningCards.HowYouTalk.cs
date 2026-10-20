using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Companion › Listening › How you talk.</summary>
internal sealed partial class ListeningCards
{
    private static readonly string[] PauseChoices = ["Short (0.5 s)", "Normal (0.8 s)", "Long (1.2 s)"];
    // Companion › Listening › Word check, in the order shown.
    private static readonly string[] WordCheckChoices = ["Relaxed", "Normal (recommended)", "Sensitive"];
    private static readonly ListeningSensitivity[] WordCheckOrder =
        [ListeningSensitivity.Relaxed, ListeningSensitivity.Normal, ListeningSensitivity.Sensitive];
    // Companion › Listening › When you talk over Martlet, in the order shown.
    private static readonly string[] BargeInStyleChoices = ["Pause and decide (recommended)", "Stop at once"];
    private static readonly Martlet.Conversation.BargeInBehavior[] BargeInStyleOrder =
        [Martlet.Conversation.BargeInBehavior.PauseAndDecide, Martlet.Conversation.BargeInBehavior.StopAtOnce];

    private TalkPreferences Talk => services.Talk;

    private void SaveTalk(TalkPreferences next, bool render = false) => services.SaveTalk(next, render);

    /// <summary>Always listening (the default; started from the talk window's Start listening) or push-to-talk, the
    /// voice-activity tuning and Voice ID.</summary>
    internal Border TalkMode()
    {
        var prefs = Talk;
        var always = Choice("TalkMode", "Always listening (recommended)",
            "Press Start listening on Home and Martlet answers when you pause, until you press Stop listening.",
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

            children.Add(MainWindow.Labeled("Sensitivity", scale));
            children.Add(MainWindow.Labeled("Reply after", pause));
            children.Add(HelpTip.Explain("Martlet listens again after each reply, with or without the talk window open. Stop listening " +
                "ends it, and locking Windows pauses it.", new Thickness(0, 8, 0, 0), "TalkListensAgain", "always listening",
                wiki: "Talking-to-Martlet#ways-to-talk"));

            var judgeTurns = new CheckBox { Content = "Judge when I finish talking (recommended)", IsChecked = prefs.JudgeTurns, Margin = new Thickness(0, 16, 0, 4) };
            AutomationProperties.SetAutomationId(judgeTurns, "TalkJudgeTurns");
            judgeTurns.Checked += (_, _) => { if (!Talk.JudgeTurns) SaveTalk(Talk with { JudgeTurns = true }, render: true); };
            judgeTurns.Unchecked += (_, _) => { if (Talk.JudgeTurns) SaveTalk(Talk with { JudgeTurns = false }, render: true); };
            children.Add(judgeTurns);
            var judgeStatus = Note(MainWindow.TurnJudgeText(prefs.JudgeTurns, services.TurnJudgeStatus), new Thickness(0, 0, 0, 4));
            AutomationProperties.SetAutomationId(judgeStatus, "TalkJudgeTurnsStatus");
            services.KeepTurnJudgeLine(judgeStatus);
            children.Add(judgeStatus);
            children.Add(HelpTip.Explain("A small model on this PC (Smart Turn) hears how you end each sentence, so Martlet answers " +
                "sooner when you finished and waits longer when you trail off. Your voice never leaves this PC for this.",
                new Thickness(0, 0, 0, 0), "SmartTurn", "Smart Turn", wiki: "Talking-to-Martlet#how-martlet-takes-turns"));

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
            var earlyStatus = Note(MainWindow.EarlyRepliesText(prefs, services.ConversationConfiguration, services.EarlyReplyRecords), new Thickness(0, 0, 0, 4));
            AutomationProperties.SetAutomationId(earlyStatus, "TalkEarlyRepliesStatus");
            services.KeepEarlyRepliesLine(earlyStatus);
            children.Add(earlyStatus);
            var earlyAbout = HelpTip.Explain(MainWindow.EarlyRepliesAbout, new Thickness(0, 0, 0, 0), "TalkEarlyReplies", "starting replies early",
                wiki: "Talking-to-Martlet#how-martlet-takes-turns");
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
            var wordCheckRow = MainWindow.Labeled("Word check", wordCheck);
            wordCheckRow.Margin = new Thickness(0, 12, 0, 0);
            children.Add(wordCheckRow);
            var wordCheckAbout = HelpTip.Explain(MainWindow.WordCheckAbout, new Thickness(0, 4, 0, 0), "TalkWordCheck", "word check",
                wiki: "Talking-to-Martlet#how-martlet-takes-turns");
            AutomationProperties.SetAutomationId(wordCheckAbout, "TalkWordCheckAbout");
            children.Add(wordCheckAbout);

            var bargeIn = new CheckBox { Content = "Let me interrupt Martlet by talking", IsChecked = prefs.BargeIn, Margin = new Thickness(0, 16, 0, 4) };
            AutomationProperties.SetAutomationId(bargeIn, "TalkBargeIn");
            bargeIn.Checked += (_, _) => SaveTalk(Talk with { BargeIn = true });
            bargeIn.Unchecked += (_, _) => SaveTalk(Talk with { BargeIn = false });
            children.Add(bargeIn);
            var bargeInAbout = HelpTip.Explain("On, talking over Martlet with real words stops its reply and gets an answer. Off (the " +
                "default), Martlet answers what you said once it finishes, and Stop (or Esc) interrupts it.", new Thickness(0, 0, 0, 0),
                "TalkOptional", "this setting", wiki: "Talking-to-Martlet#talking-over-martlet");
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
            var bargeInStyleRow = MainWindow.Labeled("When you talk over Martlet", bargeInStyle);
            bargeInStyleRow.Margin = new Thickness(0, 8, 0, 0);
            children.Add(bargeInStyleRow);
            var bargeInStyleAbout = HelpTip.Explain(MainWindow.BargeInStyleAbout, new Thickness(0, 4, 0, 0), "TalkBargeInBehavior", "talking over Martlet",
                wiki: "Talking-to-Martlet#talking-over-martlet");
            AutomationProperties.SetAutomationId(bargeInStyleAbout, "TalkBargeInBehaviorAbout");
            children.Add(bargeInStyleAbout);
        }

        var voiceId = new CheckBox { Content = "Only answer my voice", IsChecked = prefs.VoiceId, Margin = new Thickness(0, 16, 0, 4) };
        AutomationProperties.SetAutomationId(voiceId, "TalkVoiceId");
        voiceId.Checked += (_, _) => SaveTalk(Talk with { VoiceId = true }, render: true);
        voiceId.Unchecked += (_, _) => SaveTalk(Talk with { VoiceId = false }, render: true);
        children.Add(voiceId);
        var enrolled = services.VoiceIdEnrolled;
        var status = services.VoiceIdLoadError ?? (enrolled is not null
            ? $"Your voice is enrolled ({enrolled.CreatedAt.LocalDateTime:d}), so Martlet checks it on this PC and ignores other voices."
            : prefs.VoiceId ? "Set up Voice ID to use this."
            : "Optional: set up Voice ID so Martlet ignores other voices.");
        children.Add(prefs.VoiceId && enrolled is null ? Warning(status) : Note(status, new Thickness(0, 0, 0, 0)));
        children.Add(Row(PageButton(enrolled is null ? "Set up Voice ID" : "Set up Voice ID again", services.SetUpVoiceId,
            primary: prefs.VoiceId && enrolled is null, id: "SetupVoiceId")));
        return Card([.. children]);
    }
}
