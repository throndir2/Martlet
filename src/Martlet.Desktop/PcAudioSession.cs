using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

internal sealed partial class LiveConversationController
{
    /// <summary>Hearing what this PC plays (Companion › Listening › Hear what this PC plays): the PC's capture devices, the slot
    /// and speech-to-text credentials its always-listening loop runs on (<see cref="ListeningSession"/> runs the loop), the
    /// sound digest of what it plays, and what makes sound on this PC, which keeps the context board's "activity" note. It is
    /// nested in the controller, so it reads the conversation's state through <c>owner</c>; the controller's members that other
    /// code calls forward to it.</summary>
    internal sealed class PcAudioSession
    {
        private readonly LiveConversationController owner;
        // Hearing what this PC plays runs on a slot of its own, with its own speech-to-text credentials (null devices: Martlet
        // can't hear the PC here).
        internal readonly PcAudioCaptureFactory? pcAudio;
        internal readonly SetupOperationRunner pcSlot = new();
        internal readonly OpenAiTranscriptionAdapter pcTranscription;
        internal LiveListener? pcListener;
        internal LiveConversationOperation? pcTranscribing;
        // Companion › Listening › Describe PC sounds: the sound digest of what this PC plays (null when Martlet can't hear the PC).
        private readonly PcSoundDigest? soundDigest;
        // What makes sound on this PC (which app, what kind), while Martlet hears it: labels each line it hears and keeps the
        // context board's "activity" note (null when Martlet can't hear the PC).
        private readonly PcActivityMonitor? pcActivity;
        private string? activityNote;
        private long activityPostedAt;

        internal PcAudioSession(LiveConversationController owner, PcAudioCaptureFactory? pcAudio, PcActivityMonitor? pcActivity,
            Func<IProviderCredentialSource, TimeProvider, OpenAiTranscriptionAdapter>? transcriptionFactory)
        {
            this.owner = owner;
            this.pcAudio = pcAudio;
            if (pcAudio?.Sound is { } sound)
                // The audio model of its own judges first while it takes recordings, then a Thinking pool member that hears.
                soundDigest = new PcSoundDigest(sound, () => pcAudio.WithoutMartlet != true && owner.Speaking is not null,
                    owner.SoundJudge, owner.Board, owner.dataDirectory, held: owner.SoundJudgeHeld);
            // What makes sound on this PC is followed only while Martlet hears the PC; its clock must be this controller's, which
            // times each utterance.
            if (pcAudio is not null && pcActivity is not null)
            {
                this.pcActivity = pcActivity;
                pcActivity.Updated += PostActivity;
            }
            var pcCredentials = new ConversationCredentialSource(() => Volatile.Read(ref pcTranscribing)?.Authorization);
            pcTranscription = transcriptionFactory?.Invoke(pcCredentials, owner.clock) ??
                OpenAiTranscriptionAdapter.Create(pcCredentials, owner.clock);
        }

        /// <summary>Martlet can hear what this PC plays (Companion › Listening › Hear what this PC plays).</summary>
        internal bool CanHearPc => pcAudio is not null;
        /// <summary>The sound digest of what this PC plays (Companion › Listening › Describe PC sounds), or null when Martlet can't
        /// hear the PC here.</summary>
        internal PcSoundDigest? SoundDigest => soundDigest;
        /// <summary>What makes sound on this PC while Martlet hears it (a YouTube video in Chrome, a game, a voice chat in Discord):
        /// the talk window turns it on with the PC listener; null when Martlet can't hear the PC here.</summary>
        internal PcActivityMonitor? PcActivity => pcActivity;

        /// <summary>How long the context board's note on what the user is doing stays fresh (the monitor posts it again sooner).</summary>
        internal static TimeSpan ActivityNoteAge => TimeSpan.FromSeconds(30);

        // The monitor's newest look (on its own thread): the context board's "activity" note, posted when it changed or every 10
        // seconds so it stays fresh, and cleared when the monitor stops or nothing is known. The desktop log says when it changed.
        private void PostActivity(PcActivityState state)
        {
            var note = state.Note;
            var now = owner.clock.GetTimestamp();
            if (note is null)
            {
                owner.Board.Clear(ContextBoard.Activity);
                activityNote = null;
                return;
            }
            if (note == activityNote && owner.clock.GetElapsedTime(activityPostedAt, now) < TimeSpan.FromSeconds(10)) return;
            owner.Board.Post(ContextBoard.Activity, note, owner.clock.GetLocalNow(), ActivityNoteAge);
            if (note != activityNote)
                ErrorLog.Info($"What you're doing on this PC, as Martlet guesses it: {string.Join("; ", state.Activities.Select(entry =>
                    $"{entry.Source.Kind} ({(entry.Source.Kind == PcActivityKind.Game ? "a game" : entry.Source.App)}" +
                    $"{(entry.FullScreen ? ", full screen" : "")}{(entry.Audible ? "" : ", quiet")})"))}.");
            activityNote = note;
            activityPostedAt = now;
        }

        /// <summary>Whether hearing what this PC plays leaves Martlet's own voice out (null until it first listened); without it,
        /// that listening holds off while Martlet speaks.</summary>
        internal bool? PcWithoutMartlet => pcAudio?.WithoutMartlet;
        /// <summary>The one output hearing what this PC plays heard last time (another output was in use, or Windows can't leave
        /// Martlet out), or null.</summary>
        internal string? PcOutput => pcAudio?.Output;

        /// <summary>Stops the sound digest and the activity monitor when the conversation closes.</summary>
        internal void DisposeWatchers()
        {
            soundDigest?.Dispose();
            pcActivity?.Dispose();
        }
    }
}
