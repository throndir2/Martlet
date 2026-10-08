using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

// The audio model (docs/SENSE_MODELS.md, Recordings: the audio model). With an audio model of its own (Companion › Listening ›
// Audio model), recordings never go to Thinking: the straight path is off and Thinking gets the transcript. While that model
// takes recordings (the audio path is Described), it hears each thing the user says beside speech-to-text and puts what the
// words miss into words (VoiceNote). A reply takes them only when they are ready as its request is built; words that come later
// go to the context board for the next request. It judges what this PC plays first, too (SenseSoundJudge). With the defaults
// (the audio model is the text model) nothing here runs and no request changes.
internal sealed partial class LiveConversationController
{
    private readonly object lateVoiceGate = new();
    // Words that came after the request they were for, not yet carried by a request (the newest VoiceNotes.MaximumLate).
    private readonly List<VoiceNote> lateVoice = [];
    private long lateVoiceVersion;
    private int lateVoiceWatched;

    /// <summary>Whether the Thinking model may get the user's recording with <paramref name="configured"/>: no audio model of its
    /// own takes recordings (the default, where the audio model is the text model).</summary>
    internal bool ThinkingTakesVoice(LiveConversationConfiguration? configured) => SenseRoute(SenseKind.Audio, configured).Model is null;

    /// <summary>Whether the audio model of its own puts the user's voice into words now (the audio path is Described).</summary>
    internal bool DescribesVoice(LiveConversationConfiguration? configured) => SenseRoute(SenseKind.Audio, configured).Described;

    /// <summary>Whether a recording of the user's voice stays on this PC with <paramref name="configured"/> (null: the current
    /// configuration): where the audio model of its own runs when there is one, else where Thinking runs. Never chosen, Let ...
    /// hear my voice (Companion › Listening) is on only while it does.</summary>
    internal bool RecordingStaysOnThisPc(LiveConversationConfiguration? configured = null)
    {
        configured ??= Configuration;
        if (configured is null) return false;
        return SenseRoute(SenseKind.Audio, configured).Model is { } own ? VoiceNotes.StaysOnThisPc(own) : configured.RecordingStaysOnThisPc();
    }

    // The audio model's words about one utterance the microphone heard (always listening or push-to-talk), started beside
    // speech-to-text: only while the audio path is Described and Let ... hear my voice lets the recording go to the audio model,
    // and never for what this PC plays. On a model that shares the conversation's computer and graphics card the job waits for
    // the reply's request (VoiceNote.Release). Null when nothing starts.
    private VoiceNote? StartVoiceNote(LiveConversationOperation utterance, BoundedWaveAudio recording)
    {
        if (utterance.Listening is not { Pc: false } options) return null;
        var configured = utterance.Authorization.Configuration;
        var route = SenseRoute(SenseKind.Audio, configured);
        if (route is not { Described: true, Model: { } model } || !options.HearsWith(VoiceNotes.StaysOnThisPc(model))) return null;
        // Emptied in Companion › Prompts: the audio model gets no recordings of the user's voice.
        if (PromptSettings.Fill(configured.Prompts, PromptCatalog.VoiceDescription) is not { } instructions) return null;
        string before;
        Guid conversation;
        lock (gate)
        {
            before = VoiceNotes.Context(context.Snapshot(), configured.CharacterName);
            conversation = conversationId;
        }
        var shared = SenseSharesConversation(SenseKind.Audio);
        var note = new VoiceNote(clock, utterance.SpeechEndedAt != 0 ? utterance.SpeechEndedAt : clock.GetTimestamp(), shared, conversation,
            route.Name);
        var job = VoiceNotes.Job(instructions, before, recording, shared);
        Task.Run(() => DescribeVoiceAsync(note, job)).Forget();
        return note;
    }

    // Runs one utterance's job on the audio model's lane. A job stopped for a reply on the same computer (Preempted) goes once
    // more: the lane holds it until that reply's voice is made.
    private async Task DescribeVoiceAsync(VoiceNote note, SenseJob job)
    {
        var token = note.Token;
        SenseJobResult? result = null;
        string? problem = null;
        try
        {
            if (note.Shared)
                await Task.WhenAny(note.MayStart, Task.Delay(VoiceNotes.LongestWait, clock, token)).ConfigureAwait(false);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var patience = note.Shared ? new CancellationTokenSource(VoiceNotes.SharedPatience, clock) : new CancellationTokenSource();
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token, patience.Token);
                try { result = await RunSenseAsync(SenseKind.Audio, job, limit.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    problem = "the conversation kept the audio model's computer busy";
                    break;
                }
                if (result.Outcome != SenseJobOutcome.Preempted) break;
            }
        }
        catch (OperationCanceledException) { problem = "it was no longer needed"; }
        catch (ContractException) { problem = "the recording didn't fit the audio model's job"; }
        var words = result is { Succeeded: true } ? VoiceNotes.Clean(result.Text) : null;
        if (note.Finish(result?.Outcome, words, result?.Problem ?? problem, result?.Took ?? TimeSpan.Zero)) PostLateVoice(note);
        if (note.IsLate) ErrorLog.Info(LateVoiceLine(note));
        VoiceDescribed?.Invoke(note);
    }

    /// <summary>Raised (off the dispatcher) once the audio model's job for an utterance ended: its words are ready, or there are
    /// none (never the words in any log).</summary>
    internal event Action<VoiceNote>? VoiceDescribed;

    // The audio model's words that are ready as a reply's request is built (under the gate): they go with this request only, as a
    // note after the user's words, and a short line stays after the message in the conversation.
    private static (string? Note, string? Kept, VoiceNote[] Ready) VoiceNow(LiveConversationOperation operation, PromptSettings? prompts)
    {
        if (operation.VoiceNotes is not { Count: > 0 } notes) return (null, null, []);
        var ready = notes.Where(note => note.TryPeek(out _, out _)).ToArray();
        if (ready.Length == 0 || VoiceNotes.Note(prompts, ready, late: false) is not { } note) return (null, null, []);
        return (note, VoiceNotes.Kept(ready, late: false), ready);
    }

    // The reply's request was sent: the words it carried are taken, the others go to the context board once they come, and a job
    // on a shared model may start now (the lane holds it while the reply makes its voice). The desktop log says which.
    private void SentVoiceNotes(LiveConversationOperation operation, VoiceNote[] carried)
    {
        if (operation.VoiceNotes is not { Count: > 0 } notes) return;
        var taken = 0;
        foreach (var note in notes)
        {
            if (carried.Contains(note) && note.TryTake()) taken++;
            else if (note.MarkLate()) PostLateVoice(note);
            note.Release();
        }
        operation.VoiceNotesTaken = taken;
        ErrorLog.Info(VoicePathLine(notes, taken));
    }

    // The reply ended. One that never sent its request because Martlet doesn't answer it (Declined): its words go to the context
    // board for the next request once they come. Any other (stopped, or asked again with more words) keeps them for the next ask.
    private void EndVoiceNotes(LiveConversationOperation operation)
    {
        if (operation.VoiceNotes is not { Count: > 0 } notes) return;
        foreach (var note in notes)
        {
            if (operation.Declined && note.MarkLate()) PostLateVoice(note);
            note.Release();
        }
    }

    private static void CancelVoiceNotes(LiveConversationOperation operation)
    {
        foreach (var note in operation.VoiceNotes ?? []) note.Cancel();
    }

    // Words that came after the request they were for: one consume-once note on the context board for the next request (with
    // the other late ones no request carried yet), dropped once the conversation was cleared.
    private void PostLateVoice(VoiceNote note)
    {
        var current = ConversationId;
        var prompts = Configuration?.Prompts;
        lock (lateVoiceGate)
        {
            lateVoice.RemoveAll(late => late.Conversation != current);
            if (note.Conversation != current) return;
            lateVoice.Add(note);
            while (lateVoice.Count > VoiceNotes.MaximumLate) lateVoice.RemoveAt(0);
            if (VoiceNotes.Note(prompts, lateVoice, late: true) is not { } text) return;
            if (Interlocked.Exchange(ref lateVoiceWatched, 1) == 0) Board.Sent += LateVoiceSent;
            lateVoiceVersion = Board.Post(VoiceNotes.BoardSource, text, clock.GetLocalNow(), VoiceNotes.LateAge, consume: true,
                kept: VoiceNotes.Kept(lateVoice, late: true))?.Version ?? 0;
        }
    }

    // A request carried the late words: the next late words start a new note.
    private void LateVoiceSent(ContextBoardSnapshot sent)
    {
        lock (lateVoiceGate)
            if (sent.Notes.Any(note => note.Source == VoiceNotes.BoardSource && note.Version == lateVoiceVersion)) lateVoice.Clear();
    }

    // The desktop log's line for a reply's message that the audio model heard (times only, never the words).
    private static string VoicePathLine(IReadOnlyList<VoiceNote> notes, int taken)
    {
        var start = $"Voice path: described by the audio model ({notes[0].Model}); Thinking got the transcript, never the recording; ";
        if (notes.FirstOrDefault(note => note.WasTaken) is { } took)
            return start + $"the reply took its words, ready {Ms(took.ReadyAfter ?? TimeSpan.Zero)} ms after you stopped (the audio model took " +
                $"{Ms(took.Took)} ms)" + (taken < notes.Count ? $"; {notes.Count - taken} more go with the next request." : ".");
        if (notes.All(note => note.Shared && !note.IsReady))
            return start + "it shares the conversation's computer, so it describes your voice once the reply's voice is made, for the next request.";
        if (notes.All(note => note.IsReady))
            return start + (notes.All(note => note.Outcome == SenseJobOutcome.Succeeded)
                ? "it found nothing that stands out."
                : $"it didn't describe it ({notes[0].Outcome?.ToString() ?? "not run"}{(notes[0].Problem is { } problem ? ": " + problem : "")}).");
        return start + "its words weren't ready when the request was built; they go with the next request.";
    }

    // The desktop log's line for words that came late (times only, never the words).
    private static string LateVoiceLine(VoiceNote note) => note.Summary is not null
        ? $"Voice description: ready {Ms(note.ReadyAfter ?? TimeSpan.Zero)} ms after you stopped (the audio model took {Ms(note.Took)} ms); " +
          "it went to the context board for the next request."
        : note.Outcome == SenseJobOutcome.Succeeded
            ? $"Voice description: the audio model found nothing that stands out ({Ms(note.ReadyAfter ?? TimeSpan.Zero)} ms after you stopped)."
            : $"Voice description: none ({note.Outcome?.ToString() ?? "not run"}{(note.Problem is { } problem ? ": " + problem : "")}).";

    /// <summary>The sound digest's judge now: the audio model while it takes recordings (the audio path is Described), else a
    /// Thinking pool member that hears, else none (the digest uses the CPU sound tagger).</summary>
    internal ISoundJudge? SoundJudge() => (ISoundJudge?)SenseSoundJudge.For(this) ?? PoolSoundJudge.For(ThinkingPool);

    /// <summary>Whether the sound digest skips its turn: on the audio model, while it shares the conversation's computer and the
    /// live floor isn't idle (or a reply makes its voice); in the Thinking pool, while the live floor holds its members that hear.</summary>
    internal bool SoundJudgeHeld() => DescribesVoice(Configuration)
        ? SenseSharesConversation(SenseKind.Audio) && (floor.Level != LiveFloorLevel.Idle || SenseHeld(SenseKind.Audio))
        : PoolSoundJudge.Held(ThinkingPool);
}

/// <summary>The sound digest's judge on the audio model of its own (Companion › Listening › Audio model) while it takes recordings:
/// a "PC sounds" job on the audio model's lane with the clip as a 16 kHz mono WAV and <see cref="SoundDigest.Prompt"/>. Its answer
/// is one line or "none". A job that its lane doesn't start in time is dropped; a reply never waits for it.</summary>
internal sealed class SenseSoundJudge(Func<SenseKind, SenseJob, CancellationToken, Task<SenseJobResult>> run, string model) : ISoundJudge
{
    internal const string Purpose = "PC sounds";

    /// <summary>A judge on the audio model when the audio path is Described now, else null.</summary>
    internal static SenseSoundJudge? For(LiveConversationController conversation) =>
        conversation.SenseRoute(SenseKind.Audio) is { Described: true } route ? new(conversation.RunSenseAsync, route.Name) : null;

    public string Name => model;
    public SoundJudgeKind Kind => SoundJudgeKind.AudioModel;

    public async Task<string?> DescribeAsync(float[] clip, CancellationToken cancellationToken)
    {
        var wave = SoundDigest.Wave(clip);
        try
        {
            var result = await run(SenseKind.Audio, new SenseJob
            {
                Purpose = Purpose, Key = "pc-sounds", Instructions = "You describe the sound of short audio clips in one line.",
                Text = SoundDigest.Prompt, Audio = BoundedWaveAudio.FromWave(wave), Timeout = TimeSpan.FromSeconds(12), MaxOutputTokens = 96
            }, cancellationToken).ConfigureAwait(false);
            return result.Succeeded ? result.Text : null;
        }
        finally { Array.Clear(wave); }
    }

    public override string ToString() => nameof(SenseSoundJudge);
}
