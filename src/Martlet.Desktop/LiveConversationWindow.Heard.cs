using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Diagnostics;
using Martlet.Providers;

namespace Martlet.Desktop;

// What always listening heard, talking over Martlet (barge-in) and the lines left out.
public partial class LiveConversationWindow
{
    // Why what the PC plays can't be heard right now (Martlet keeps trying).
    private static string PcProblem(ErrorCode code) => code switch
    {
        ErrorCode.AudioAccessDenied => "Windows is blocking Martlet from hearing this PC's sound.",
        ErrorCode.AudioDeviceUnavailable or ErrorCode.AudioDeviceLost => "This PC has no speakers or headphones to hear right now.",
        ErrorCode.AudioFormatUnsupported => "Martlet can't read the sound format of this PC's speakers.",
        _ => "Martlet can't hear what this PC plays right now."
    };

    private void Heard(HeardSpeech speech)
    {
        var status = speech.Status;
        if (speech.SpeakerCheck is { } check)
            listenNote = check.Verdict != SpeakerVerdict.User && speech.Voiceprint is { } print ? VoiceIdentity.Describe(check, print.Threshold) : null;
        if (status.AudioFailure is { } audio && status.Code.StartsWith("mic.", StringComparison.Ordinal))
        {
            micProblem = MicProblem(audio);
            return;
        }
        micProblem = null;
        // What isn't words (mm, a cough, "Thank you." made up from noise) shows only as a muted note, so you can see what was
        // let go and tune Word check.
        if (speech.Ignored is { } ignored)
        {
            ShowIgnored(ignored.Describe(speech.Text));
            return;
        }
        if (speech.Interrupt is { } stop) heardInterrupt = (stop, speech.SpeechStartedAt);
        var label = speech.Voices?.Speaker is { Voice: { } voice } speaker ? $"{voice.DisplayName}{(speaker.Mine ? " (you)" : "")} (spoken)" : "You (spoken)";
        var yours = Yours(speech);
        // Straight to Thinking: the recording goes on its own and its words fill the bubble once speech-to-text has them.
        if (speech.Words is { } words && speech.Recording is not null)
        {
            var waiting = Add(ChatRole.User, StraightPlaceholder, label);
            lastHeard = clock.GetTimestamp();
            activityAt = lastHeard;
            reportHeld = false;
            heardQueue.Add(new("", null, speech.Voices, waiting, speech.Recording, At: lastHeard, Timeline: speech.Timeline, Words: words,
                Yours: yours));
            awaitingWords.Add((words, waiting, yours));
            notice = null;
            pacer?.NoteConversation();
            return;
        }
        if (speech.Text?.Trim() is not { Length: > 0 } text)
        {
            notice = ListenOutcome(status, controller.Configuration) ?? notice;
            return;
        }
        // The microphone heard this PC's speakers play what Martlet also heard from the PC (a video, show, game or music), in a
        // voice it doesn't know as yours: that isn't you, and the PC's own line already carries the words.
        if (!yours && !CallOn &&
            PcEcho.Speakers(text, speech.SpeechStartedAt, speech.SpeechEndedAt, PlayedLately(), controller.Clock.TimestampFrequency) is { } speakers)
        {
            // Words from the speakers never stop Martlet.
            if (speech.Interrupt is not null) heardInterrupt = null;
            speech.Voice?.Cancel();
            LeftOutSpeakers(null, text, speakers);
            return;
        }
        var bubble = Add(ChatRole.User, text, label);
        lastHeard = clock.GetTimestamp();
        activityAt = lastHeard;
        reportHeld = false;
        heardQueue.Add(new(text, speech.Confidence, speech.Voices, bubble, HearsVoice.On ? speech.Recording : null, At: lastHeard,
            Timeline: speech.Timeline, Yours: yours, SpokeStart: speech.SpeechStartedAt, SpokeEnd: speech.SpeechEndedAt, Voice: speech.Voice));
        saidLately.Add((text, lastHeard, yours));
        LeaveOutYourVoice();
        notice = null;
        pacer?.NoteConversation();
    }
    // Why the microphone can't be opened right now, in words for always listening (which keeps trying it).
    private static string MicProblem(ErrorCode code) => code switch
    {
        ErrorCode.AudioAccessDenied =>
            "Windows is blocking the microphone; allow desktop apps to use it in Windows Settings.",
        ErrorCode.AudioDeviceBusy => "Another app is using the microphone.",
        ErrorCode.AudioFormatUnsupported => "This microphone isn't supported, so choose another in Companion › Listening.",
        _ => "Martlet can't open the microphone; check that it is connected and enabled."
    };

    private static string? ListenOutcome(LiveConversationStatus status, LiveConversationConfiguration? configuration)
    {
        if (status.Quarantined) return Remedy("conversation.cleanup_quarantined");
        if (status.ProviderFailure is { } provider)
            return configuration?.SttHostTarget() is { } host && HostRemedy(provider, host.HostId, ProviderRole.Stt) is { } remedy
                ? remedy : Lead("Couldn't transcribe speech", ProviderRemedy(provider));
        return status.Code is "listen.heard" or "listen.held" or "listen.ignored" or "mic.no_speech" or "stt.NoSpeech" or "speaker.not_user" or
            "speaker.too_short" or "conversation.canceled" or "conversation.revoked" or "conversation.expired" ? null : Remedy(status.Code);
    }

    // You kept talking before Martlet answered (you are talking now, or something new was heard since the reply was asked for):
    // that reply (or remark) stops, and once you pause, everything you said is answered together. A reply that already acted
    // (Home Assistant or a tool) finishes; what you add is answered after it. Once Martlet is speaking, only talking over it
    // with real words stops it (BargeInPolicy): a quick check of what you are saying while you say it (Parakeet on this PC), or
    // what you said once you paused; never a hum, a cough, laughter, a quick "yeah", a queued word or what this PC plays (the PC
    // listener never interrupts anything).
    private void Interrupt()
    {
        CallBargeIn();
        // A reply paused for what you said (Pause and decide) that the judge, or your talking on, decided to stop.
        var heldStop = controller.TakeHeldStop();
        var quick = (listener is { TalkingOver: true } live ? live.TalkOver : null) ?? heldStop;
        var said = heardInterrupt;
        heardInterrupt = null;
        var over = quick is not null || said is not null;
        var talking = over || listener is { Hearing: true } or { Transcribing: > 0 };
        if (!talking && heardQueue.Count == 0) return;
        pacer?.NoteConversation();
        // Barge-in: you talked over a reply Martlet is already saying. It stops at once (the rest of the reply and its queued
        // audio are dropped) and what you say now is answered next, with the reply so far kept in context.
        if (preferences.BargeIn && over && owned is { OwnershipReleased: false } speaking && !ReferenceEquals(yielded, speaking) &&
            Speaking(speaking))
        {
            yielded = speaking;
            controller.Stop(speaking, "conversation.interrupted", keepContext: true);
            LogBargeIn(quick, said);
        }
        if (owned is { OwnershipReleased: false, Spoken: true } reply && answering is { } batch && !ReferenceEquals(yielded, reply) &&
            restarts < MaximumRestarts && Restartable(reply) && !Speaking(reply))
        {
            yielded = reply;
            restarts++;
            answering = null;
            Requeue(batch);
            controller.Stop(reply, "conversation.continued", keepContext: true);
        }
        if (talking && commentary is { OwnershipReleased: false } glance && !ReferenceEquals(yielded, glance) && (over || !Speaking(glance)))
        {
            yielded = glance;
            controller.Stop(glance, "commentary.interrupted", keepContext: true);
        }
        // You come first: Martlet bringing up its background work gives way when you start talking before it says anything (or
        // you talk over it); what it carried goes with what you say instead.
        if (talking && owned is { OwnershipReleased: false, Report: true } report && !ReferenceEquals(yielded, report) &&
            (over || !Speaking(report)))
        {
            yielded = report;
            controller.Stop(report, "report.interrupted", keepContext: true);
        }
        // A short reaction to being touched gives way the same way; its touches go with what you say instead.
        if (talking && owned is { OwnershipReleased: false, Touch: true } touched && !ReferenceEquals(yielded, touched) &&
            (over || !Speaking(touched)))
        {
            yielded = touched;
            controller.Stop(touched, "touch.interrupted", keepContext: true);
        }
    }

    /// <summary>Martlet is saying it (or may already have said some of it).</summary>
    private static bool Speaking(LiveConversationOperation operation) =>
        operation.Turn?.Snapshot is { State: ConversationState.Playing } or { MayHavePlayed: true };

    // What you said, once you paused, that stops Martlet (BargeInPolicy), and when your voice began; taken by the next Interrupt.
    private (BargeInDecision Decision, long StartedAt)? heardInterrupt;
    // The muted "Ignored ..." note the last ignored sounds went into, while nothing else came after it.
    private ChatMessage? ignoredNote;

    /// <summary>The desktop log's barge-in line: how long after you started talking over Martlet it stopped (from when your voice
    /// began to the stop), why, and how it knew (never what you said).</summary>
    private void LogBargeIn(TalkOverResult? quick, (BargeInDecision Decision, long StartedAt)? said)
    {
        // A remark Martlet started on its own and dropped: the controller logged why already.
        if (quick?.Dropped == true) return;
        var startedAt = quick?.StartedAt ?? said?.StartedAt ?? 0;
        if (startedAt == 0) return;
        var stopped = controller.Clock.GetElapsedTime(startedAt);
        var judged = quick?.Ruling is { } ruling
            ? $"; paused {quick.Paused?.TotalMilliseconds ?? 0:0} ms first, then {(ruling.Source == BargeInSource.Cue ? "a clear cue" : $"the {ruling.Judge} judge ({ruling.JudgeTime.TotalMilliseconds:0} ms)")} said it was for Martlet"
            : quick?.Paused is { } paused ? $"; paused {paused.TotalMilliseconds:0} ms first" : "";
        ErrorLog.Info(quick is not null
            ? $"Barge-in: Martlet stopped its reply {stopped.TotalMilliseconds:0} ms after you started talking over it " +
              $"({quick.Decision.Reason}; decided {quick.After.TotalMilliseconds:0} ms in, after {quick.Checks} quick " +
              $"check{(quick.Checks == 1 ? "" : "s")} of your words{judged}; word check {preferences.WordCheck}; {preferences.BargeInStyle})."
            : $"Barge-in: Martlet stopped its reply {stopped.TotalMilliseconds:0} ms after you started talking over it " +
              $"({said!.Value.Decision.Reason}; from what you said once you paused; word check {preferences.WordCheck}).");
    }

    /// <summary>The talk window's barge-in line (LiveBargeIn) for the last time the user talked over Martlet, never what was
    /// said: "Talked over at 14:02:11: paused 420 ms, then resumed (not for Martlet: a quick backchannel; rules judge, 0 ms)."</summary>
    internal static string BargeInLine(BargeInRecord record)
    {
        var at = record.At.ToLocalTime().ToString("T", System.Globalization.CultureInfo.CurrentCulture);
        var what = record.Paused is { } paused ? $"paused {paused.TotalMilliseconds:0} ms, then {record.Outcome}" : $"{record.Outcome} at once";
        var verdict = record.Verdict == BargeInVerdict.Interrupt ? "for Martlet" : "not for Martlet";
        var by = record.Source switch
        {
            BargeInSource.Cue => "a clear cue",
            BargeInSource.KeptTalking => "you kept talking",
            BargeInSource.Limit => "the pause reached its limit",
            BargeInSource.Timeout => $"the rules decided in {record.JudgeTime.TotalMilliseconds:0} ms",
            _ => record.Judge == "stop at once" ? "Stop at once" : $"{record.Judge} judge, {record.JudgeTime.TotalMilliseconds:0} ms"
        };
        return $"Talked over at {at}: {what} ({verdict}: {record.Reason}; {by}).";
    }

    /// <summary>The talk window's LiveUnprompted line: how many things Martlet meant to say on its own were dropped and why, and
    /// the newest (its ID and kind, never its text): "Dropped on its own: 2 (1 too old, 1 the conversation moved on, 0 talked
    /// over). Last: checkin-3 (a check-in) at 14:02:11: too old: ...". Empty before the first drop.</summary>
    internal static string UnpromptedLine(LiveConversationController.UnpromptedDrops drops) => drops.Total == 0 ? ""
        : $"Dropped on its own: {drops.Total} ({drops.TooOld} too old, {drops.MovedOn} the conversation moved on, " +
          $"{drops.TalkedOver} talked over). Last: {drops.Last}.";

    // A sound always listening ignored shows as a muted note; ignored sounds in a row share one.
    private void ShowIgnored(string line)
    {
        if (ignoredNote is not null && Messages.Count > 0 && ReferenceEquals(Messages[^1], ignoredNote) && ignoredNote.Text.Length < 300)
            ignoredNote.Text += "  " + line;
        else ignoredNote = Add(ChatRole.Note, line, "");
    }

    private static bool Restartable(LiveConversationOperation reply) =>
        reply.HomeSummary is null && reply.Status.Code != "home.asking" &&
        reply.Turn?.Snapshot is not ({ ToolCalls: > 0 } or { ActiveTool: not null });
}
