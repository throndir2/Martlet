using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Martlet.Desktop;

/// <summary>The talk cards of Companion › Voice, the UI only: In conversations (Speak Martlet's replies aloud and the voice
/// volume) and Quick sounds while Martlet thinks. What the cards need from the app is <see cref="ITalkCardsServices"/>.</summary>
internal sealed class TalkCards(ITalkCardsServices services)
{
    private static readonly string[] QuickSoundDelayNames = ["After 0.5 s", "After 0.7 s (recommended)", "After 1 s", "After 1.5 s"];

    private TalkPreferences Talk => services.Talk;

    private void SaveTalk(TalkPreferences next, bool render = false) => services.SaveTalk(next, render);

    /// <summary>Speak replies (Mute voice on the character's menu is the same choice) and the voice volume.</summary>
    internal Border SpeakReplies()
    {
        var speak = new CheckBox { Content = "Speak Martlet's replies aloud", IsChecked = Talk.SpeakReplies };
        services.KeepSpeakRepliesCheck(speak);
        AutomationProperties.SetAutomationId(speak, "SpeakReplies");
        speak.Checked += (_, _) => { if (!Talk.SpeakReplies) SaveTalk(Talk with { SpeakReplies = true }); };
        speak.Unchecked += (_, _) => { if (Talk.SpeakReplies) SaveTalk(Talk with { SpeakReplies = false }); };

        var volume = new Slider { Minimum = 0, Maximum = 100, Value = Math.Round(Talk.VoiceVolume * 100), SmallChange = 5,
            LargeChange = 10, TickFrequency = 5, IsSnapToTickEnabled = true, Width = 240, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(volume, "Voice volume: how loud Martlet speaks and sings");
        AutomationProperties.SetAutomationId(volume, "VoiceVolume");
        var level = Note(MainWindow.VoiceVolumeLabel(Talk.VoiceVolume), new Thickness(8, 0, 0, 0));
        level.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(level, "VoiceVolumeLevel");
        volume.ValueChanged += (_, e) =>
        {
            var next = e.NewValue / 100;
            level.Text = MainWindow.VoiceVolumeLabel(next);
            if (Math.Abs(next - Talk.VoiceVolume) > 0.001) SaveTalk(Talk with { VoiceVolume = next });
        };
        var scale = new StackPanel { Orientation = Orientation.Horizontal };
        scale.Children.Add(volume);
        scale.Children.Add(level);
        var volumeRow = MainWindow.Labeled("Voice volume", scale);
        volumeRow.Margin = new Thickness(0, 12, 0, 0);

        return Card(Heading("In conversations"), speak,
            HelpTip.Explain("Martlet speaks each reply and shows it in the talk window. Off, replies show only as text, and Mute voice " +
                "on the character's right-click menu does the same.", new Thickness(0, 6, 0, 0), "TalkSpeaks", "spoken replies",
                wiki: "Voices#volume-and-muting"),
            volumeRow,
            Note("How loud Martlet speaks and sings on this PC, without changing Windows' own volume.", new Thickness(0, 4, 0, 0)));
    }

    /// <summary>Quick sounds while Martlet thinks.</summary>
    internal Border QuickSounds()
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
        var status = Note("", new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(status, "VoiceQuickSoundsStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var make = PageButton("Make quick sounds now", services.MakeQuickSounds, link: true, id: "VoiceQuickSoundsMake");
        services.KeepQuickSoundsLine(status, make);
        var delayRow = MainWindow.Labeled("Play one", delay);
        delayRow.Margin = new Thickness(0, 10, 0, 0);
        return Card(Heading("Quick sounds while Martlet thinks"), on,
            HelpTip.Explain("When a reply is slow to start, Martlet first says a quick \"Mm,\" or \"Hmm...\" in its own voice. With a " +
                "paid cloud voice, Martlet makes the sounds only when you press Make quick sounds now.", new Thickness(0, 6, 0, 0),
                "Fillers", "quick sounds", wiki: "Talking-to-Martlet#how-martlet-answers"),
            delayRow, status, Row(make));
    }
}
