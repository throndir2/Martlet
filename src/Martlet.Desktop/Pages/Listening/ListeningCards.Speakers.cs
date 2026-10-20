using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Martlet.Desktop;

/// <summary>Companion › Listening › Speakers and echo, and Watch along.</summary>
internal sealed partial class ListeningCards
{
    /// <summary>Companion › Listening › Reduce echo from my speakers (on by default): while the microphone listens, Martlet also
    /// reads what this PC plays and removes it from the microphone, so on speakers it doesn't hear its own voice, a video or music
    /// as you talking, and always listening can go on while Martlet speaks. The status line says how the last listen went.</summary>
    internal Border Echo()
    {
        var reduce = new CheckBox { Content = "Reduce echo from my speakers", IsChecked = Talk.ReduceEcho, Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetAutomationId(reduce, "TalkReduceEcho");
        reduce.Checked += (_, _) => { if (!Talk.ReduceEcho) SaveTalk(Talk with { ReduceEcho = true }, render: true); };
        reduce.Unchecked += (_, _) => { if (Talk.ReduceEcho) SaveTalk(Talk with { ReduceEcho = false }, render: true); };
        var (text, problem) = MainWindow.EchoStatus(Talk.ReduceEcho, services.EchoReport);
        var status = problem ? Warning(text) : Note(text, new Thickness(0, 0, 0, 6));
        AutomationProperties.SetAutomationId(status, "TalkReduceEchoStatus");
        return Card(Heading("Speakers and echo"), reduce, status,
            HelpTip.Explain("Martlet removes what this PC plays from the microphone, so it works without headphones and keeps listening " +
                "while it speaks. That sound stays on this PC and is never saved or sent.", new Thickness(0, 0, 0, 0), "EchoCancel",
                "hearing this PC", wiki: "Talking-to-Martlet#speakers-and-echo"));
    }

    /// <summary>Companion › Listening › Hear what this PC plays (off by default): while always listening runs, Martlet also hears
    /// what the PC plays (a video, a stream, a call, a game), beside the microphone, so it can watch along. Ticking it is the
    /// consent; the text says what is heard, how it is marked and that it never reaches memory or voice recognition.</summary>
    internal Border PcAudio()
    {
        var hear = new CheckBox { Content = "Hear what this PC plays", IsChecked = Talk.HearPc, Margin = new Thickness(0, 0, 0, 6),
            IsEnabled = Talk.HearPc || services.CanHearPc != false };
        AutomationProperties.SetAutomationId(hear, "TalkHearPc");
        hear.Checked += (_, _) => { if (!Talk.HearPc) SaveTalk(Talk with { HearPc = true }, render: true); };
        hear.Unchecked += (_, _) => { if (Talk.HearPc) SaveTalk(Talk with { HearPc = false }, render: true); };
        // Which outputs are in use now (their sessions only, no sound), until a conversation has heard the PC itself.
        var outputs = Talk.HearPc ? Martlet.Audio.Windows.WasapiPcAudioSourceFactory.Outputs() : null;
        var (text, problem) = MainWindow.PcAudioStatus(Talk, services.CanHearPc != false,
            services.PcWithoutMartlet ?? (outputs is { Elsewhere: not null } ? false : null),
            services.PcOutput ?? outputs?.Output, outputs?.Elsewhere);
        var status = problem ? Warning(text) : Note(text, new Thickness(0, 0, 0, 6));
        AutomationProperties.SetAutomationId(status, "TalkHearPcStatus");
        var describe = new CheckBox { Content = "Describe PC sounds", IsChecked = Talk.DescribePcSounds, Margin = new Thickness(0, 0, 0, 6),
            IsEnabled = Talk.HearPc };
        AutomationProperties.SetAutomationId(describe, "TalkDescribePcSounds");
        describe.Checked += (_, _) => { if (!Talk.DescribePcSounds) SaveTalk(Talk with { DescribePcSounds = true }, render: true); };
        describe.Unchecked += (_, _) => { if (Talk.DescribePcSounds) SaveTalk(Talk with { DescribePcSounds = false }, render: true); };
        var digest = services.SoundDigest;
        var describeStatus = Note(MainWindow.SoundDigestStatus(Talk, services.CanHearPc != false, digest,
            digest is null ? services.SavedSoundJudge() : (digest.Judge, digest.JudgeKind),
            services.Clock), new Thickness(0, 0, 0, 6));
        AutomationProperties.SetAutomationId(describeStatus, "TalkDescribePcSoundsStatus");
        return Card([Heading("Watch along"), hear, status,
            HelpTip.Explain("While always listening runs, Martlet also hears the sound your PC plays (videos, streams, calls, games), " +
                "as if it were watching with you. Listening transcribes it like your microphone, and it goes to Thinking marked as the " +
                "PC's, never remembered or saved.", new Thickness(0, 0, 0, 8), "TalkHearPcAbout", "hearing what this PC plays",
                wiki: "Vision#hearing-what-your-pc-plays"),
            describe, describeStatus,
            HelpTip.Explain("About every 10 seconds while something plays, Martlet describes the rest of the sound (music, game " +
                "sounds, laughter) in one short line for its next reply. The line is never saved, and a reply never waits for it.",
                new Thickness(0, 0, 0, 8), "SoundDigest", "sound descriptions", wiki: "Vision#hearing-what-your-pc-plays"),
            Note("How chatty Martlet is about it (the same choice as Vision's How often it comments):", new Thickness(0, 0, 0, 4)),
            .. ChattinessPicker.Create("TalkPcChattiness", () => Talk.ScreenChattiness, services.DecidedChattiness,
                choice => SaveTalk(Talk with { ScreenChattiness = choice }, render: true))]);
    }
}
