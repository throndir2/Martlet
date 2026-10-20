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

// What a finished turn leaves in the window.
public partial class LiveConversationWindow
{
    // Once per finished turn: the last of its text and what to tell you. Listening carries on whatever happened.
    private void Finished(LiveConversationOperation done)
    {
        Observe(done);
        activityAt = clock.GetTimestamp();
        var status = done.Status;
        var code = status.Code;
        if (done.Look && !done.Commentary) NoteTakenLook(done, code);
        var continued = code == "conversation.continued";
        // What went straight to Thinking wasn't words (a cough, mm): its reply was dropped before it played, so it leaves only the
        // faded "Ignored ..." note, never a reply or a latency line.
        var notWords = code == LiveConversationController.NotWordsCode;
        // Voice latency for every reply, in the desktop log (logs_tail and MCP's latency_report read it): how long from when you
        // stopped talking (or sent your message) to the first audio, step by step (ReplyLatency). Martlet bringing up its
        // background work on its own isn't a wait of yours, so it has no line.
        if (!done.OnItsOwn && !notWords && done.Turn?.Snapshot is { } finishedReply &&
            ReplyLatency.Describe(done.LatencyTimeline, done.ReplyStartedAt, done.LatencyTimeline?.Clock ?? clock, finishedReply,
                done.Authorization.Configuration.LatencyModels(done.Spoken || done.Authorization.Microphone,
                    done.LatencyTimeline?.StandIn ?? done.StandIn),
                interrupted: ReferenceEquals(yielded, done) && code == "conversation.interrupted",
                passed: done.Passed, restarted: continued, floor: controller.LiveFloorNote) is { } latency)
        {
            ErrorLog.Info(latency);
            KeepFirstWord(done.Authorization.Configuration, finishedReply);
        }
        // What the reply's tags did (character emotes and the voice's sounds and tones), with any other spelling Martlet took for
        // a tag ([nod] for {nod}); tag names only, never the words.
        var acted = done.Turn?.Acted ?? [];
        if (!notWords && acted.Count > 0) ErrorLog.Info($"Reply acted: {ReplyTag.Describe(acted)}.");
        if (ReferenceEquals(shown, done) && reply is not null)
        {
            // A reply restarted because you kept talking is replaced by the next one, unless you already heard some of it; one to
            // something that wasn't words is dropped.
            if ((continued || notWords) && done.Turn?.Snapshot.MayHavePlayed != true)
            {
                Messages.Remove(reply);
                if (ReferenceEquals(lastReply, reply)) lastReply = null;
                reply = null;
            }
            else
            {
                // Every request the reply sent (tool rounds, retries), now that it ended.
                KeepContext(reply, done);
                // How the reply was acted out: its tone, the voice's sounds and the character's emotes.
                if (ReplyTag.Note(acted) is { } how) reply.AddNote(how);
                var refusal = done.Turn?.Content.Refusal?.Trim();
                if (!string.IsNullOrEmpty(refusal) && reply.Text != refusal) reply.AddNote($"{CharacterName} declined: {refusal}");
                else if (code == TouchCutInCode) reply.AddNote("Stopped for your touch.");
                else if (done.Turn?.Snapshot.State is not (ConversationState.Completed or ConversationState.Refused)) reply.AddNote("Cut short.");
                else if (done.Turn?.Snapshot is { SpeechFailed: true } voiceless)
                    reply.AddNote(voiceless.MayHavePlayed ? "The voice stopped partway, so only the beginning was spoken."
                        : "The voice failed, so this wasn't spoken.");
                else if (done.Turn?.Snapshot is { VoiceMuted: true, MayHavePlayed: true })
                    reply.AddNote("Muted partway, so only the beginning was spoken.");
                else if (done.Turn?.Snapshot.SpeechLimitReached == true) reply.AddNote("Only the beginning was spoken.");
            }
        }
        // Whether Thinking got the recording of what you said with the transcript (Companion › Listening). Notes go on your own
        // words, never on what the PC played.
        var asked = done.Spoken ? answering?.LastOrDefault(entry => !entry.Pc)?.Bubble
            : ReferenceEquals(shown, done) ? heard ?? (typed is { } sent && ReferenceEquals(sent.Operation, done) ? sent.Bubble : null) : null;
        if (!continued && done.VoiceSent && asked is { } said)
        {
            if (done.Turn?.Snapshot.AudioRejected == true)
                said.AddNote(done.Straight ? "Thinking couldn't take your recording, so it got your words once they were transcribed."
                    : "Thinking couldn't take your recording, so it got the transcript.");
            else if (done.Turn?.Snapshot.State == ConversationState.Completed)
                said.AddNote(done.Straight ? "Thinking heard your voice straight away." : "Thinking heard your voice.");
        }
        // Whether the audio model's words about how you sounded went with the reply (times only, never the words).
        else if (!continued && done.VoiceNotes is { Count: > 0 } && asked is { } described && done.Turn?.Snapshot.State == ConversationState.Completed)
            described.AddNote(VoiceNoteLine(done));
        // Whether the reply saw the picture of what vision watches that went with your message.
        if (!continued)
        {
            var rejected = done.ScreenSent && done.Turn?.Snapshot.ImageRejected == true;
            var seenIt = done.ScreenSent && !rejected && done.Turn?.Snapshot.State == ConversationState.Completed;
            if (rejected) asked?.AddNote("Thinking couldn't take the picture of your screen, so it got your words only.");
            else if (seenIt && done.Seen is { } seen) asked?.AddNote($"{CharacterName} saw {seen.Source.Label}.");
            // With an image model of its own: whether the reply took its description of the picture, or it wasn't ready yet.
            else if (done is { ImagePath: SensePath.Described, Seen: { } pictured } && done.Turn?.Snapshot.State == ConversationState.Completed)
                asked?.AddNote(done.ScreenDescribed ? $"{CharacterName} saw {pictured.Source.Label} through the image model's description."
                    : $"The image model's description of {pictured.Source.Label} wasn't ready, so {CharacterName} answered without it.");
            if (rejected && watching && controller.Configuration is { } selected && selected.Vision() != VisionSupport.Supported)
                StopWatching(Lead("The Thinking model rejected the picture", selected.VisionAdvice()));
        }
        if (typed is { } finished && ReferenceEquals(finished.Operation, done)) typed = null;
        // A reply that used an app's guide: the speech bubble offers to read more about it for a little while.
        if (!continued && !notWords && done.GuideKey is { } used && done.Turn?.Snapshot.State == ConversationState.Completed) OfferReadMore(used);
        if (notWords && done.Spoken && answering is { } dropped)
            foreach (var entry in dropped.Where(entry => !entry.Pc))
            {
                Messages.Remove(entry.Bubble);
                awaitingWords.RemoveAll(waiting => ReferenceEquals(waiting.Bubble, entry.Bubble));
                ShowIgnored(entry.Words?.Ignored?.Describe(entry.Words.Text) ?? "Ignored a sound (no speech).");
            }
        if (done.Spoken && !continued)
        {
            if (done.Passed) answering?.LastOrDefault(entry => !entry.Pc)?.Bubble.AddNote($"{CharacterName} stayed quiet.");
            answering = null;
            restarts = 0;
        }
        notice = Outcome(done) ?? (code is "runtime.Completed" or "listen.passed" ? null : notice);
        FinishRemote(done);
        // The setup was changed elsewhere (Companion is usable while this window is open): load it before the next turn.
        if (code == "conversation.configuration_changed") reloadReason ??= "Your setup changed.";
    }

    /// <summary>Which job's request failed: the reply's own record, or speech-to-text for a failed transcription.</summary>
    private static ProviderRole? FailedJob(LiveConversationOperation done) =>
        done.Turn?.Snapshot is { ProviderFailure: not null } snapshot ? snapshot.FailedProvider
        : done.Status.Code.StartsWith("stt.", StringComparison.Ordinal) ? ProviderRole.Stt : null;

    /// <summary>The note under your words when the audio model of its own heard them: whether the reply took its words about how
    /// you sounded and how soon after you stopped they were ready, or that they go with your next message (never the words).</summary>
    internal static string VoiceNoteLine(LiveConversationOperation done)
    {
        var notes = done.VoiceNotes ?? [];
        if (notes.FirstOrDefault(note => note.WasTaken) is { ReadyAfter: { } ready })
            return $"The audio model described how you sounded ({ready.TotalSeconds:0.0} s after you stopped), and the reply took it.";
        if (notes.Count > 0 && notes.All(note => note.IsReady && note.Summary is null))
            return notes.All(note => note.Outcome == Martlet.Conversation.SenseJobOutcome.Succeeded)
                ? "The audio model heard you; nothing stood out."
                : "The audio model couldn't describe how you sounded this time.";
        return "The audio model describes how you sounded; Martlet gets it with your next message.";
    }

    private static string? Outcome(LiveConversationOperation done)
    {
        var status = done.Status;
        if (done.Authorization.CredentialFailure is { } credential) return CredentialMessages.Describe(credential);
        if (status.AudioFailure is { } audio) return AudioSetupDiagnostics.Remedy(audio) + " You can still type.";
        if ((done.Turn?.Snapshot.ProviderFailure ?? status.ProviderFailure) is { } provider)
            return ProviderRemedy(provider, done.Authorization.Configuration, FailedJob(done));
        if (status.Quarantined) return Remedy("conversation.cleanup_quarantined");
        return status.Code switch
        {
            "runtime.Completed" or "commentary.glance" or "conversation.typing" or "conversation.listening_paused" or
                "commentary.interrupted" or "conversation.interrupted" or "conversation.closed" or "mic.no_speech" or
                "listen.passed" or "conversation.continued" or "report.interrupted" or "touch.interrupted" or TouchCutInCode or
                LiveConversationController.NotWordsCode => null,
            "speaker.not_user" or "speaker.too_short" or "stt.NoSpeech" when done.HandsFree || done.Spoken => null,
            var code when code.StartsWith("policy.", StringComparison.Ordinal) && (done.HandsFree || done.Spoken) => null,
            var code => Remedy(code)
        };
    }
}
