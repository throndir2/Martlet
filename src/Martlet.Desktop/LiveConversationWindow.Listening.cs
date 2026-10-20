using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using Martlet.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Reading;
using Martlet.Core.Settings;
using Martlet.Diagnostics;
using Martlet.Providers;

namespace Martlet.Desktop;

// Always listening: starting, stopping and collecting what the microphone and this PC play.
public partial class LiveConversationWindow
{
    // ---------- always listening ----------

    internal static TimeSpan ListenRetry => TimeSpan.FromSeconds(2);
    /// <summary>How much longer Martlet waits for the rest when what you said sounds unfinished ("so, um", "and", a trailing comma).</summary>
    internal static TimeSpan UnfinishedPause => TimeSpan.FromMilliseconds(1500);
    // How often one set of things you said may be restarted because you kept talking (a TV in the background must not loop it).
    private const int MaximumRestarts = 3;

    // Keeps one listener running while always listening is on; it listens on while Martlet speaks when it can tell Martlet's voice
    // from yours, and otherwise holds off by itself until Martlet is done. With Hear what this PC
    // plays on, a second one hears the PC beside it.
    private void KeepListening()
    {
        KeepHearingPc();
        if (!listening || !Available || listener is not null || clock.GetTimestamp() < listenRetryAt) return;
        try
        {
            listener = controller.Listen(Listening(true));
            // A reply started early waits for what is heard: take it as soon as it is posted, not at the next tick.
            listener.Posted += () => { if (controller.EarlyReply is not null) Dispatcher.InvokeAsync(Pump); };
            listenProblem = null;
        }
        catch (LiveActionException error) when (error.Code is "conversation.ownership_busy" or "conversation.controls_blocked")
        {
            listenRetryAt = After(ListenRetry);
        }
        catch (LiveActionException error) { CantListen(ListenFailure(error.Code)); }
        catch (VoiceIdentityException error) { CantListen(error.Message); }
    }

    private bool HearingPc => listening && (preferences.HearPc || CallOn) && controller.CanHearPc;

    // Companion › Listening › Describe PC sounds: the sound digest runs only while this window hears what the PC plays because
    // Hear what this PC plays is on (not for a Discord call alone) and Windows isn't locked.
    private void FollowSoundDigest()
    {
        if (controller.SoundDigest is not { } digest) return;
        var on = !locked && preferences.HearPc && preferences.DescribePcSounds && pcListener is { Running: true };
        if (digest.On != on) digest.On = on;
    }

    // What makes sound on this PC (PcActivityMonitor) is followed only while Martlet hears the PC and Windows isn't locked: each
    // line heard then says where it came from, and replies get a note on what you seem to be doing.
    private void FollowPcActivity()
    {
        if (controller.PcActivity is not { } activity) return;
        var on = !locked && !closed && pcListener is { Running: true };
        if (activity.On != on) activity.On = on;
    }

    private void KeepHearingPc()
    {
        if (!HearingPc || !Available || pcListener is not null || clock.GetTimestamp() < pcRetryAt) return;
        try { pcListener = controller.Listen(ListeningOptions.PcAudio with { WordCheck = preferences.WordCheck }); }
        catch (LiveActionException error)
        {
            if (error.Code is not ("conversation.ownership_busy" or "conversation.controls_blocked")) pcProblem = ListenFailure(error.Code);
            pcRetryAt = After(ListenRetry);
        }
    }

    private string ListenFailure(string code) =>
        code is "conversation.configuration_unsupported" or "conversation.setup_required" ? ListeningProblem() : Remedy(code);

    // Listening can't run right now: it says why and keeps trying, so it carries on by itself once that is fixed.
    private void CantListen(string problem)
    {
        if (problem != listenProblem) notice = problem;
        listenProblem = problem;
        listenRetryAt = After(ListenRetry);
    }

    private void StopListening(bool keepHeard)
    {
        if (listener is { } live) controller.StopListening(live);
        listener = null;
        micProblem = listenProblem = null;
        listenRetryAt = 0;
        StopPcListening(keepHeard);
        if (keepHeard) return;
        saidLately.Clear();
        pcEchoes = speakersLeftOut = 0;
        foreach (var entry in heardQueue) entry.Bubble.AddNote("Not answered.");
        heardQueue.Clear();
    }

    // What the PC played and nobody answered yet is simply let go: it was never said to Martlet.
    private void StopPcListening(bool keepHeard)
    {
        if (pcListener is { } live) controller.StopListening(live);
        pcListener = null;
        pcProblem = null;
        pcRetryAt = 0;
        FollowSoundDigest();
        FollowPcActivity();
        if (keepHeard) return;
        playingQueue.Clear();
        pcHeld.Clear();
        playedLately.Clear();
        pcBubbleWhere = null;
        Calls?.Attribution.Reset();
    }

    private long After(TimeSpan delay) => clock.GetTimestamp() + (long)(delay.TotalSeconds * clock.TimestampFrequency);

    // Takes what always listening made of each utterance: the words go into the history and wait for a reply. Your own words
    // come first, so what the PC played back of them is recognized before it shows.
    private void Collect()
    {
        CollectMic();
        FollowWords();
        CollectPc();
        ReleasePc();
    }

    // The words of what went straight to Thinking, as speech-to-text beside the reply finishes them: they replace the bubble's
    // placeholder (marked when the word check wouldn't count them; Thinking already heard it), and count as what you said lately
    // so your own voice played back by the PC is left out. When speech-to-text couldn't transcribe it, the bubble goes: there is
    // nothing to show (Thinking already heard the recording, and its reply stays).
    private void FollowWords()
    {
        if (awaitingWords.Count == 0) return;
        var now = clock.GetTimestamp();
        foreach (var (words, bubble, yours) in awaitingWords.Where(waiting => waiting.Words.IsReady).ToArray())
        {
            awaitingWords.Remove((words, bubble, yours));
            if (words.Text is { Length: > 0 } text)
            {
                bubble.Text = text;
                if (words.Ignored is { } ignored) bubble.AddNote($"Word check: {ignored.Reason.TrimEnd('.')}, but Thinking heard it anyway.");
                saidLately.Add((text, now, yours));
                LeaveOutYourVoice();
            }
            else Messages.Remove(bubble);
        }
    }

    private void CollectMic()
    {
        if (listener is not { } live) return;
        while (live.TryTake(out var speech)) Heard(speech);
        if (live.MicrophoneWorks) micProblem = null;
        if (live.Running) return;
        // It ended by itself: the setup changed (reloaded) or can't be used right now (said, and tried again). Anything else
        // just starts it again shortly.
        listener = null;
        listenRetryAt = After(ListenRetry);
        switch (live.Ended)
        {
            case "voiceid.not_enrolled" or "conversation.configuration_unsupported" or "conversation.setup_required":
                CantListen(ListenFailure(live.Ended));
                break;
            case "conversation.configuration_changed":
                ReloadWhenIdle("Your setup changed.");
                break;
        }
    }

    private void CollectPc()
    {
        if (pcListener is not { } live) return;
        while (live.TryTake(out var speech)) HeardPc(speech);
        if (live.MicrophoneWorks) pcProblem = null;
        if (live.Running) return;
        pcListener = null;
        pcRetryAt = After(ListenRetry);
        if (live.Ended is "conversation.configuration_unsupported" or "conversation.setup_required") pcProblem = ListenFailure(live.Ended);
    }

    /// <summary>What the PC played, heard and transcribed: marked as the PC's in the history (never as you), joined to the PC's
    /// last line while nothing else came between, and kept to go with the next reply. It never touches Voice ID, voice
    /// recognition or memory. While the microphone is still on your words it waits (ReleasePc), and a line that repeats what
    /// you just said is your own voice played back on this PC: it is left out.</summary>
    private void HeardPc(HeardSpeech speech)
    {
        if (speech.Status.AudioFailure is { } audio && speech.Status.Code.StartsWith("mic.", StringComparison.Ordinal))
        {
            pcProblem = PcProblem(audio);
            return;
        }
        pcProblem = speech.Status.ProviderFailure is not null ? ListenOutcome(speech.Status, controller.Configuration) : null;
        // What the PC played that isn't words (music, a sound) is simply let go.
        if (speech.Ignored is not null || speech.Text?.Trim() is not { Length: > 0 } text) return;
        var now = clock.GetTimestamp();
        var from = PcSources(speech);
        // In order: once one line waits for your words, the next waits behind it.
        if (MicBusy || pcHeld.Count > 0) pcHeld.Add((speech, text, now, from));
        else Played(speech, text, now, whileYouTalked: false, from);
    }

    // Where a line the PC played came from: the apps that made sound while it was heard (PcActivityMonitor). Nothing in the
    // Discord call mode (it hears Discord alone and names who spoke), or when Martlet can't tell.
    private PcHeardFrom PcSources(HeardSpeech speech) =>
        CallOn || controller.PcActivity is not { On: true } activity ? PcHeardFrom.None
            : activity.Between(speech.SpeechStartedAt, speech.SpeechEndedAt > 0 ? speech.SpeechEndedAt : controller.Clock.GetTimestamp());

    private static PcPlayedLine LineOf(HeardSpeech speech, string text, PcHeardFrom from) =>
        new(text, from, speech.SpeechStartedAt, speech.SpeechEndedAt);

    private void Played(HeardSpeech speech, string text, long at, bool whileYouTalked, PcHeardFrom from)
    {
        var line = LineOf(speech, text, from);
        if (RepeatsYou(text) && !OriginalOfSpeakers(line))
        {
            LeftOutYourVoice();
            return;
        }
        playedLately.Add((line, at));
        // In the call mode each line is named after who said it and shows on its own. Otherwise the line says where it came from
        // when Martlet can tell, and a bubble takes the PC's next lines only while they come from the same place.
        var (said, label) = CallLine(text);
        text = said;
        var where = CallOn ? null : PcActivity.Where(from.Named);
        var bubble = !CallOn && Messages.Count > 0 && Messages[^1] is { IsPcAudio: true } last && pcBubbleWhere == where &&
            last.Text.Length + text.Length < MaximumPcBubble
            ? last : Add(ChatRole.PcAudio, "", where is null ? label : $"{label}: {where}");
        pcBubbleWhere = where;
        bubble.Text = bubble.Text.Length == 0 ? text : bubble.Text + " " + text;
        // A very long stretch goes to Martlet by its end, so it always fits in one message with where it came from.
        var room = MaximumPcLine - (PcActivity.From(from.Named)?.Length + 2 ?? 0);
        var kept = text.Length <= room ? text : "…" + text[^(room - 1)..].TrimStart();
        playingQueue.Add(new(kept, speech.Confidence, null, bubble, Pc: true, At: at, WhileYouTalked: whileYouTalked,
            From: CallOn ? null : from));
        pcHeardAt = at;
    }

    // A line that only videos, shows, games or music played during, and that repeats what the microphone heard lately, is the
    // original: the microphone heard this PC's speakers. The line stays, and the microphone's copies of it (at the same moment,
    // in a voice Martlet doesn't know as yours) that still wait for a reply are let go. When it repeats what a voice Martlet knows
    // as yours said, it is your own voice played back and is left out as before.
    private bool OriginalOfSpeakers(PcPlayedLine line)
    {
        if (CallOn || !line.From.MediaOnly || RepeatsYours(line.Text)) return false;
        PcPlayedLine[] played = [line];
        var frequency = controller.Clock.TimestampFrequency;
        for (var i = heardQueue.Count - 1; i >= 0; i--)
        {
            var entry = heardQueue[i];
            if (entry.Pc || entry.Yours || entry.Words is not null ||
                PcEcho.Speakers(entry.Text, entry.SpokeStart, entry.SpokeEnd, played, frequency) is null) continue;
            heardQueue.RemoveAt(i);
            saidLately.RemoveAll(said => !said.Yours && said.Text == entry.Text);
            LeftOutSpeakers(entry.Bubble, entry.Text, line.From);
        }
        return true;
    }

    // What the microphone heard that this PC's speakers played (PcEcho.Speakers): the bubble goes, a faded note says why and a
    // reply started early for it is let go.
    private void LeftOutSpeakers(ChatMessage? bubble, string text, PcHeardFrom from)
    {
        if (bubble is not null) Messages.Remove(bubble);
        var where = PcActivity.Where(from.Named.Count > 0 ? from.Named : [.. from.Audible.Take(2)]);
        ShowIgnored(new UtteranceDecision(false, UtteranceKind.Words, where is null ? "this PC's speakers" : $"this PC's speakers: {where}", 0)
            .Describe(text));
        controller.LetGoEarly("the microphone heard this PC's speakers");
        if (speakersLeftOut++ == 0)
            ErrorLog.Info("Always listening: the microphone heard this PC's speakers play what Martlet also heard from the PC (a " +
                "video, show, game or music), so Martlet left it out of what you said. Echo reduction or headphones keep it out.");
    }

    // What the PC played lately, oldest first: lines already shown, then lines waiting for your words.
    private IEnumerable<PcPlayedLine> PlayedLately()
    {
        playedLately.RemoveAll(played => clock.GetElapsedTime(played.At) > EchoWindow);
        return playedLately.Select(played => played.Line).Concat(pcHeld.Select(held => LineOf(held.Speech, held.Text, held.From)));
    }

    // The microphone heard a voice Martlet knows as yours: one linked to the signed-in account, or Voice ID's match.
    private static bool Yours(HeardSpeech speech) =>
        speech.Voices?.Speaker is { Mine: true } || speech.SpeakerCheck is { Verdict: SpeakerVerdict.User };

    /// <summary>How long after the microphone heard you a line the PC plays may still be your own voice played back (both are
    /// transcribed separately, so one can come a few seconds after the other).</summary>
    internal static TimeSpan EchoWindow => TimeSpan.FromSeconds(15);
    /// <summary>How long a line the PC played waits, at most, for the microphone to finish what it is hearing from you.</summary>
    internal static TimeSpan PcHoldWait => TimeSpan.FromSeconds(8);

    // The microphone is still on something that may turn out to be the words the PC just played back: you are talking, or what
    // you said is being transcribed (beside a reply, for what went straight to Thinking) or waits to be taken.
    private bool MicBusy => listener is { Hearing: true } or { Transcribing: > 0 } or { HasResults: true } || awaitingWords.Count > 0;

    // What the PC played while you talked goes on once the microphone has your words (or it waited PcHoldWait): your own voice
    // played back is left out, the rest shows as the PC's in the order it was heard. A line let go while you still talk is
    // checked again when your words come (LeaveOutYourVoice).
    private void ReleasePc()
    {
        if (pcHeld.Count == 0 || MicBusy && clock.GetElapsedTime(pcHeld[0].At) < PcHoldWait) return;
        var busy = MicBusy;
        foreach (var (speech, text, at, from) in pcHeld) Played(speech, text, at, busy, from);
        pcHeld.Clear();
    }

    private bool RepeatsYou(string played)
    {
        saidLately.RemoveAll(said => clock.GetElapsedTime(said.At) > EchoWindow);
        return saidLately.Count > 0 && PcEcho.Repeats(played, saidLately.Select(said => said.Text));
    }

    // The line repeats what a voice Martlet knows as yours said lately (call after RepeatsYou, which drops what is too old).
    private bool RepeatsYours(string played) =>
        saidLately.Any(said => said.Yours) && PcEcho.Repeats(played, saidLately.Where(said => said.Yours).Select(said => said.Text));

    // Your words just came: what the PC played while you talked and that repeats them leaves the queue and the history. A line
    // only videos, shows, games or music played during stays unless it repeats what a voice Martlet knows as yours said
    // (OriginalOfSpeakers).
    private void LeaveOutYourVoice()
    {
        for (var i = playingQueue.Count - 1; i >= 0; i--)
        {
            var entry = playingQueue[i];
            if (!entry.WhileYouTalked || !RepeatsYou(entry.Text) || entry.From is { MediaOnly: true } && !RepeatsYours(entry.Text)) continue;
            playingQueue.RemoveAt(i);
            var bubble = entry.Bubble;
            var at = bubble.Text.LastIndexOf(entry.Text, StringComparison.Ordinal);
            if (at >= 0)
            {
                var rest = string.Join(" ", (bubble.Text[..at] + " " + bubble.Text[(at + entry.Text.Length)..])
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries));
                if (rest.Length == 0) Messages.Remove(bubble);
                else bubble.Text = rest;
            }
            LeftOutYourVoice();
        }
    }

    private void LeftOutYourVoice()
    {
        if (pcEchoes++ == 0)
            ErrorLog.Info("Hear what this PC plays: this PC plays your own voice back (a voice changer's or headset app's " +
                "\"hear myself\", Windows' \"Listen to this device\" or a call), so Martlet leaves your words out of what the PC played.");
    }
}
