using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Channels;
using Martlet.Audio;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Streaming;
using Martlet.Providers;

namespace Martlet.Conversation;

public sealed class ConversationTurn
{
    internal ConversationRuntime Owner { get; }
    private object Sync => Owner.Sync;
    private TimeProvider Clock => Owner.Clock;
    private readonly ConversationRequest request;
    private readonly IConversationAuthorizationSource authorization;
    private readonly MonotonicWindow whole;
    private readonly CancellationTokenSource stop = new();
    // Ends only what is said aloud (synthesis and playback); canceled with the turn, or alone when the voice fails.
    private readonly CancellationTokenSource speaking;
    private readonly TaskCompletionSource stopSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ConversationSnapshot> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource synthesized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly VoiceTagStripper shown;
    // The control tags the reply wrote, in order (ConversationRequest.ControlTags); guarded by Sync like the text.
    private readonly List<string> controls = [];
    // The character tags the reply wrote and the sounds and tones its voice performed, in order; guarded by Sync like the text.
    private readonly List<ReplyTag> acted = [];
    private readonly Channel<SpeechPiece> segments = Channel.CreateBounded<SpeechPiece>(new BoundedChannelOptions(2)
    {
        FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = false, AllowSynchronousContinuations = false
    });
    private readonly Channel<ConversationEvent> events;
    private readonly StringBuilder text = new();
    private readonly Guid? retryOf;
    private readonly bool earlierSpeech;
    private Task callbacks = Task.CompletedTask, speechCallbacks = Task.CompletedTask;
    // Started early (ConversationRuntime.StartEarly): completes on Release; null for a turn that started normally. While it
    // waits, what shows or acts (captions and character cues of a reply that isn't spoken) waits in order in heldBack, tools
    // and playback wait for it, and without its voice (holdVoice) synthesis waits too. heldBack is null once released.
    private readonly TaskCompletionSource? hold;
    private readonly bool holdVoice;
    private List<Action>? heldBack;
    private TimeSpan? releasedAfter;
    // A quick sound (PlayQuickSound) playing ahead of the reply's own voice: its playback task, run and lip-sync observation, and
    // when it started. The reply's first piece waits for it to end and never cuts it (ownAudioStarted: none may begin after);
    // at most one per turn, and none once the work ended (quickClosed). Guarded by Sync.
    private Task? quickSound;
    private PlaybackRun? quickRun;
    private GeneratedSpeechObservation? quickObservation;
    private TimeSpan? quickSoundAfter;
    private bool ownAudioStarted, quickClosed;
    // Captions for words the voice couldn't say, shown one after another (ShowUnsaid).
    private Task unsaidCaptions = Task.CompletedTask;
    // A reply that isn't spoken still shows in the captions (speech bubble, subtitles), each sentence once it is written.
    private readonly SpeechSegmenter? captioner;
    private bool captionsEnded;
    private CancellationToken originalCaller;
    private CancellationTokenRegistration callerRegistration;
    private MonotonicWindow? textWindow, speechWindow, playWindow;
    private readonly long startedAt;
    private TimeSpan? firstTextAfter, firstAudioAfter;
    // What the providers reported this reply's requests read (every tool round, retry and fallback), and how much of it came
    // from their prompt cache; null until a request reported it.
    private long? inputTokens, cachedInputTokens;
    private TimeSpan? textRequestAfter, textResponseAfter, firstReasoningAfter, firstSegmentAfter, speechRequestAfter,
        firstSpeechAudioAfter, firstPieceSynthesizedAfter, firstPieceSpeech, playbackStartedAfter;
    // The Thinking stream being read now and when its request was sent: its response headers and hidden reasoning show in the
    // timings as soon as they arrive (SuperviseAsync), before the first words.
    private ITextGenerationStream? openStream;
    private TimeSpan openStreamAfter;
    // What Backup Thinking did for the reply's first request, once decided (RaceAsync).
    private ThinkingBackupResult? backupResult;
    // Finished pieces' waits for the voice's next audio (playback underruns).
    private int voiceWaits;
    private TimeSpan voiceWaited;
    private bool synthesizing;
    private SpeechSegmenter? segmentation;
    private PlaybackRun? playback;
    private GeneratedSpeechObservation? speechObservation;
    private PlaybackSnapshot? lastPlayback;
    private PlaybackSnapshot? observedPlayback;
    private ConversationState state = ConversationState.Authorizing;
    private ConversationFailure failure, speechFailure;
    private ProviderFailureCode? providerFailure;
    private ProviderRole? failedProvider;
    private SequenceIssueInfo? sequenceFailure;
    private EvidenceProvenance? textProvenance, speechProvenance;
    private Guid? speechRequest;
    private string? refusal;
    private bool invalidated, userStopped, workFinished, released, quarantined, textComplete, refused, terminal, toolsRejected,
        audioRejected, imageRejected, reasoningRejected;
    private bool speechLimitReached, voiceMuted;
    private int peakQueued, committed, suppressed, reservedBytes, toolCalls;
    private string? activeTool, fellBackAfter;
    private long reservedSamples, accepted, submitted, consumed, eventSequence, dropped;
    private bool mayHavePlayed;
    // Paused for the user (Pause): the playing sentence's run, and every one started before Resume, is paused; synthesis and
    // the text go on and buffer. Guarded by Sync.
    private bool paused;
    private long pausedAt;
    private int pauses, resumes;
    private TimeSpan pausedTime;
    private string? sentence;
    // What was said aloud so far: each sentence's words (without voice tags) once it started playing. Guarded by Sync.
    private readonly StringBuilder saidAloud = new();
    // How long this reply has played, which its character cues are timed by: it pauses and resumes with the reply, and it
    // stops when the reply is stopped, replaced or fails, so a cue the reply hasn't reached is never acted.
    private readonly CharacterCueClock cueClock;

    public Guid SessionId => Owner.SessionId;
    public Guid TurnId { get; } = Guid.NewGuid();
    public long Epoch { get; }
    public CorrelationIds TextIds { get; }
    public Task<ConversationSnapshot> Completion => completion.Task;
    public Task OwnershipRelease => release.Task;
    /// <summary>Completes once the reply's voice is all made (every sentence synthesized, or the voice stopped), or for a reply
    /// that isn't spoken once its text is written, or when the turn ends: only playback may be left (the live floor's end of a
    /// reply).</summary>
    public Task Synthesized => synthesized.Task;
    public ChannelReader<ConversationEvent> Events => events.Reader;
    public ConversationSnapshot Snapshot { get { lock (Sync) return GetSnapshot(); } }
    public ConversationContent Content { get { lock (Sync) return new(text.ToString(), refusal); } }
    /// <summary>The control tags (<see cref="ConversationRequest.ControlTags"/>) the reply wrote so far, in order, as the request
    /// spelled them; never shown or spoken.</summary>
    public IReadOnlyList<string> Controls { get { lock (Sync) return [.. controls]; } }
    /// <summary>What the reply's tags did so far, in order: each character tag it wrote (<c>{nod}</c>, also when it wrote
    /// <c>[nod]</c> or <c>*nods*</c>) and each sound or tone its voice performed (<c>[laugh]</c>); never shown or spoken as
    /// words.</summary>
    public IReadOnlyList<ReplyTag> Acted { get { lock (Sync) return [.. acted]; } }

    internal ConversationTurn(ConversationRuntime owner, ConversationRequest request,
        IConversationAuthorizationSource authorization, long epoch, Guid? retryOf, bool earlierSpeech, bool early = false,
        bool prepareVoice = true)
    {
        Owner = owner;
        cueClock = new(Clock);
        this.request = request;
        this.authorization = authorization;
        if (early)
        {
            hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            holdVoice = !prepareVoice;
            heldBack = [];
        }
        // The speaking voice's own tags: a spelling it shares with another tag means what it means to the speech segmenter.
        IReadOnlyList<VoiceTag> voiceTags = request.KeptVoiceTags;
        // A reply that isn't spoken has no sentence timing: its character tags act as soon as the words arrive (once released).
        shown = new(request.CharacterTags, request.Speech is null && owner.CharacterCues is { } feed
            ? tag => WhenReleased(() => feed.Post([new(tag, TimeSpan.Zero)], Task.CompletedTask, cueClock)) : null,
            request.ControlTags, controls.Add, voiceTags,
            tag => { if (ReplyTag.Of(tag, voiceTags) is { } act) acted.Add(act); });
        captioner = request.Speech is null && owner.SpokenText is not null
            ? new SpeechSegmenter(BoundedSpeechInput.HardMaxUtf8Bytes, request.TextLimits.MaxTextCharacters, request.SilentReply,
                characterTags: request.CharacterTags, controlTags: request.ControlTags)
            : null;
        Epoch = epoch;
        this.retryOf = retryOf;
        this.earlierSpeech = earlierSpeech;
        whole = new(Clock, request.Limits.TurnTimeout);
        speaking = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        startedAt = Clock.GetTimestamp();
        TextIds = NewIds();
        events = Channel.CreateBounded<ConversationEvent>(new BoundedChannelOptions(request.Limits.EventCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = false, SingleReader = false,
            AllowSynchronousContinuations = false
        }, _ => Interlocked.Increment(ref dropped));
    }

    internal void Begin(CancellationToken callerToken)
    {
        originalCaller = callerToken;
        Emit(ConversationEventKind.State);
        callerRegistration = callerToken.UnsafeRegister(_ => CancelByUser(), null);
        // Callers/authorization implementations cannot synchronously block Start or the UI thread.
        var worker = Task.Run(WorkAsync);
        _ = ReleaseAsync(worker);
        _ = SuperviseAsync();
    }

    public Task<ConversationSnapshot> StopAsync()
    {
        CancelByUser();
        return Completion;
    }

    /// <summary>The sentence Martlet is saying aloud right now (its words, without voice tags), or null between sentences.</summary>
    public string? Sentence { get { lock (Sync) return sentence; } }

    /// <summary>What Martlet has said aloud of this reply so far: the words of each sentence that started playing (without voice
    /// tags), in order; the last one may have been cut off. Empty for a reply that isn't spoken, or before its first sentence
    /// plays.</summary>
    public string SaidAloud { get { lock (Sync) return saidAloud.ToString(); } }

    /// <summary>Whether what Martlet says aloud is paused (<see cref="Pause"/>).</summary>
    public bool Paused { get { lock (Sync) return paused; } }

    /// <summary>Whether this reply is spoken (it has a voice).</summary>
    public bool Spoken => request.Speech is not null;

    /// <summary>Plays <paramref name="pcm"/> now, ahead of this reply's own voice: a quick sound in Martlet's own voice (24 kHz
    /// mono 16-bit PCM, at most <see cref="QuickSoundAudio.MaximumBytes"/>) while the reply's first audio isn't ready
    /// (<see cref="QuickSoundWatcher"/>). The reply's first piece waits until it has played and then follows it, so neither is
    /// cut. It is never part of the reply's text, captions or history. Returns false, and plays nothing, for a reply that isn't
    /// spoken, is held, paused, finished or stopped, already played a quick sound, or whose own audio started.</summary>
    public bool PlayQuickSound(ReadOnlyMemory<byte> pcm)
    {
        if (pcm.Length is 0 || pcm.Length > QuickSoundAudio.MaximumBytes || pcm.Length % 2 != 0) return false;
        lock (Sync)
        {
            if (request.Speech is not { } voice || quickSound is not null || quickClosed || heldBack is not null || paused ||
                invalidated || workFinished || terminal || speaking.IsCancellationRequested || ownAudioStarted || playback is not null)
                return false;
            quickSoundAfter = Clock.GetElapsedTime(startedAt);
            var token = speaking.Token;
            quickSound = Task.Run(() => PlayQuickSoundAsync(pcm, voice, token));
            Emit(ConversationEventKind.Playback);
            return true;
        }
    }

    /// <summary>Started early (<see cref="ConversationRuntime.StartEarly"/>) and not released yet: its Thinking text streams
    /// (and its first piece may be synthesized), but nothing shows, acts, calls a tool or plays.</summary>
    public bool Held { get { lock (Sync) return heldBack is not null; } }

    /// <summary>Lets a turn started early go on as the reply: what waited to show or act does so now, in order, its first
    /// piece plays at once when it is ready, and tools may run. A reply that isn't spoken may have finished meanwhile; its
    /// captions show now. Returns false for a turn that wasn't held, was released already or was stopped or failed.</summary>
    public bool Release()
    {
        lock (Sync)
        {
            if (heldBack is not { } waiting || invalidated) return false;
            heldBack = null;
            releasedAfter = Clock.GetElapsedTime(startedAt);
            // In order and before anything newer: the feeds take them without blocking or calling back.
            foreach (var action in waiting) action();
            hold!.TrySetResult();
            Emit(ConversationEventKind.State);
            return true;
        }
    }

    // What shows or acts runs now, or once the turn is released when it was started early.
    private void WhenReleased(Action action)
    {
        lock (Sync)
        {
            if (heldBack is { } waiting)
            {
                waiting.Add(action);
                return;
            }
        }
        action();
    }

    // Waits while the turn is held (started early and not released yet); a stopped turn ends the wait.
    private Task WhileHeldAsync() => hold is { Task.IsCompleted: false } held ? held.Task.WaitAsync(stop.Token) : Task.CompletedTask;

    /// <summary>Pauses what Martlet says aloud, at once, without losing anything: the sentence playing stops reading audio
    /// and keeps what is buffered, and a sentence that starts meanwhile starts paused. The Thinking text and the voice's
    /// synthesis go on and buffer, so <see cref="Resume"/> plays on from the same sample with no new synthesis. Returns
    /// false for a reply that isn't spoken, has finished or is paused already.</summary>
    public bool Pause()
    {
        lock (Sync)
        {
            if (request.Speech is null || paused || invalidated || workFinished || terminal || speaking.IsCancellationRequested) return false;
            paused = true;
            pausedAt = Clock.GetTimestamp();
            pauses++;
            playback?.Pause();
            quickRun?.Pause();
            cueClock.Pause();
            return true;
        }
    }

    /// <summary>Plays on from where <see cref="Pause"/> stopped. Returns false when it wasn't paused.</summary>
    public bool Resume()
    {
        lock (Sync)
        {
            if (!paused) return false;
            paused = false;
            pausedTime += Clock.GetElapsedTime(pausedAt);
            resumes++;
            playback?.Resume();
            quickRun?.Resume();
            cueClock.Resume();
            return true;
        }
    }

    private void CancelByUser()
    {
        lock (Sync)
        {
            if (workFinished || terminal) return;
            userStopped = true;
            Invalidate();
        }
    }

    private void Invalidate()
    {
        if (invalidated) return;
        Owner.InvalidateEpoch(this);
        invalidated = true;
        speechObservation?.Stop();
        segmentation?.Clear();
        while (segments.Reader.TryRead(out _)) { }
        segments.Writer.TryComplete();
        // Capture-scoped handle: never use sink-wide Stop, even during delayed teardown.
        _ = playback?.StopAsync();
        quickObservation?.Stop();
        _ = quickRun?.StopAsync();
        cueClock.Stop();
        SetState(userStopped ? ConversationState.Canceled : text.Length > 0 ? ConversationState.Partial : ConversationState.Failed);
        stopSignal.TrySetResult();
        callbacks = stop.CancelAsync();
    }

    internal void CheckActive()
    {
        if (originalCaller.IsCancellationRequested) CancelByUser();
        if (invalidated || Owner.CurrentEpoch != Epoch || stop.IsCancellationRequested)
            throw new OperationCanceledException(stop.Token);
        if (whole.Expired) throw new ConversationException(ConversationFailure.DeadlineExceeded);
    }

    private void Check(MonotonicWindow window)
    {
        lock (Sync)
        {
            CheckActive();
            if (window.Expired) throw new ConversationException(window.ExpiryFailure);
        }
    }

    private DateTimeOffset Deadline(MonotonicWindow window) =>
        window.Deadline < whole.Deadline ? window.Deadline : whole.Deadline;

    private async Task WorkAsync()
    {
        var writing = GuardStageAsync(GenerateAsync);
        // A reply that isn't spoken has nothing more to make once its text is written.
        if (request.Speech is null) _ = writing.ContinueWith(_ => synthesized.TrySetResult(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        await Task.WhenAll(writing, GuardStageAsync(SpeakAsync)).ConfigureAwait(false);
        synthesized.TrySetResult();
        // A quick sound still playing ends before the turn lets go of its speakers; none may start after this.
        Task? quick;
        lock (Sync)
        {
            quickClosed = true;
            quick = quickSound;
        }
        if (quick is not null) await quick.ConfigureAwait(false);
    }

    private async Task GuardStageAsync(Func<Task> operation)
    {
        try { await operation().ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (ConversationException error) { Fail(error.Failure); }
        catch (ContractException) { Fail(ConversationFailure.InvalidStream); }
        // External credential/authorization/provider boundaries can throw arbitrary exceptions.
        // Surface a failed operation without propagating secret-bearing/native exception messages.
        catch (Exception) { Fail(ConversationFailure.DependencyFailed); }
    }

    private void Fail(ConversationFailure reason, ProviderFailureCode? provider = null, SequenceIssue? sequence = null,
        ProviderRole? failedRole = null)
    {
        lock (Sync)
        {
            if (originalCaller.IsCancellationRequested)
            {
                CancelByUser();
                return;
            }
            if (invalidated || workFinished) return;
            failure = reason;
            providerFailure = provider;
            failedProvider = provider is null ? null : failedRole;
            sequenceFailure = sequence is { } issue ? new(issue) : null;
            Invalidate();
        }
    }

    // A voice that fails or runs out of time ends what is said aloud, never the reply: the rest of the text still streams to
    // completion and is shown, and the turn records why it went quiet. No other voice is asked instead.
    private void StopSpeaking(ConversationFailure reason, ProviderFailureCode? provider = null)
    {
        lock (Sync)
        {
            if (originalCaller.IsCancellationRequested)
            {
                CancelByUser();
                return;
            }
            if (request.Speech is null || invalidated || workFinished || voiceMuted || speechFailure != ConversationFailure.None) return;
            // The whole turn ran out of time before the text finished, which ends the reply too.
            if (whole.Expired && !textComplete)
            {
                Fail(ConversationFailure.DeadlineExceeded);
                return;
            }
            speechFailure = reason;
            if (provider is not null)
            {
                providerFailure = provider;
                failedProvider = ProviderRole.Tts;
            }
            EndSpeaking();
        }
    }

    /// <summary>Mutes the voice for the rest of this reply (the user muted Martlet's voice while it spoke): what is playing
    /// stops now and nothing more is synthesized, while the text still streams to completion and each sentence not said aloud
    /// shows in the captions, as after a voice failure. It is not a failure (<see cref="ConversationSnapshot.VoiceMuted"/>).
    /// A no-op for a reply that isn't spoken, has finished or whose voice already stopped.</summary>
    public void MuteVoice()
    {
        lock (Sync)
        {
            if (request.Speech is null || invalidated || workFinished || voiceMuted || speechFailure != ConversationFailure.None) return;
            voiceMuted = true;
            EndSpeaking();
        }
    }

    private void EndSpeaking()
    {
        speechObservation?.Stop();
        // Capture-scoped handle: never use sink-wide Stop.
        _ = playback?.StopAsync();
        quickObservation?.Stop();
        _ = quickRun?.StopAsync();
        speechCallbacks = speaking.CancelAsync();
        // Muted before the text started: the turn is still authorizing its Thinking request.
        if (!textComplete && textProvenance is not null && state != ConversationState.Generating) SetState(ConversationState.Generating);
        else Emit(ConversationEventKind.State);
    }

    private void CheckSpeaking(MonotonicWindow window)
    {
        Check(window);
        speaking.Token.ThrowIfCancellationRequested();
    }

    private enum RoundEnd { Completed, Refused, Failed, Invalid }

    // Switches the rest of this reply to the Thinking fallback when there is one and nothing of the failed answer was said.
    private bool FallBack(int textBefore, string reason)
    {
        if (request.Fallback is null) return false;
        lock (Sync)
        {
            if (invalidated || stop.IsCancellationRequested || whole.Expired || text.Length != textBefore) return false;
            textWindow = null;
            fellBackAfter = reason;
            Emit(ConversationEventKind.State);
            return true;
        }
    }

    private sealed record RoundResult(RoundEnd End, string Text, IReadOnlyList<TextToolCall> Calls,
        ProviderFailureCode? Failure = null, SequenceIssue? Issue = null, string? Refusal = null);

    // The words of a message sent as the user's recording alone, once speech-to-text running beside the reply has them.
    private string? spokenWords;

    // The input without its recording: with the transcript it already carries, or, for a recording sent alone, with its words
    // (waiting, within the reply's own time, for speech-to-text to finish). Null when those words never came.
    private async Task<BoundedTextInput?> WithoutRecordingAsync(BoundedTextInput input)
    {
        if (input.Audio is null) return input;
        if (request.SpokenWords is not { } words) return input.WithoutAudio();
        if (spokenWords is null)
        {
            try
            {
                var heard = await words(stop.Token).WaitAsync(whole.Remaining > TimeSpan.Zero ? whole.Remaining : TimeSpan.Zero, Clock,
                    stop.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(heard)) return null;
                spokenWords = heard.Trim();
            }
            catch (TimeoutException) { return null; }
            lock (Sync) CheckActive();
        }
        try { return input.WithTranscript(spokenWords); }
        catch (ContractException) { return null; }
    }

    // One reply may take several requests: each tool round sends the calls and their results back to the model, until it
    // answers in text. Every request is separately authorized; text from every round is shown and spoken in order.
    private async Task GenerateAsync()
    {
        var segmenter = request.Speech is { } voice
            ? new SpeechSegmenter(voice.Limits.MaxInputBytes, request.TextLimits.MaxTextCharacters, request.SilentReply,
                tags: request.KeptVoiceTags,
                characterTags: request.CharacterTags, breaks: request.SpeechBreaks, controlTags: request.ControlTags) : null;
        try
        {
            var input = request.Input;
            var rounds = new List<TextToolRound>();
            bool retried = false, fallback = false, audioDropped = false, imageDropped = false, reasoningDropped = false;
            for (var attempt = 0; ; attempt++)
            {
                int before;
                lock (Sync) before = text.Length;
                RoundResult result;
                // Without the recording (a model that refused it, or the fallback): the transcript, or the words that stand in
                // for a recording sent alone once speech-to-text has them.
                var sent = input;
                if (fallback || audioDropped)
                {
                    if (await WithoutRecordingAsync(input).ConfigureAwait(false) is not { } withoutRecording)
                    {
                        Fail(ConversationFailure.ProviderFailed, audioDropped ? ProviderFailureCode.RequestRejected : null, null, ProviderRole.Llm);
                        return;
                    }
                    sent = withoutRecording;
                }
                try
                {
                    // A reply started early asks the Thinking fallback (it may be a paid cloud model) only once it is taken.
                    if (fallback) await WhileHeldAsync().ConfigureAwait(false);
                    result = await RequestAsync(imageDropped ? sent.WithoutImage() : sent,
                        attempt == 0 ? TextIds : NewIds(), segmenter, fallback, reasoningDropped, hedged: attempt == 0).ConfigureAwait(false);
                }
                // The selected destination failed without answering (no reply in time or a broken stream): ask the fallback.
                catch (Exception error) when (error is ConversationException { Failure: ConversationFailure.DeadlineExceeded or
                    ConversationFailure.InvalidStream or ConversationFailure.DependencyFailed } or ContractException &&
                    !fallback && FallBack(before, error is ConversationException failed ? failed.Failure.ToString() : nameof(ConversationFailure.InvalidStream)))
                {
                    fallback = true;
                    continue;
                }
                if (result.End is RoundEnd.Failed or RoundEnd.Invalid)
                {
                    // A model that refuses the attached recording rejects the request before answering; ask again with the
                    // transcript only, and drop the recording for the rest of this reply.
                    if (!audioDropped && !fallback && input.Audio is not null && result.Text.Length == 0 &&
                        result.Failure == ProviderFailureCode.RequestRejected)
                    {
                        audioDropped = true;
                        lock (Sync)
                        {
                            CheckActive();
                            audioRejected = true;
                        }
                        continue;
                    }
                    // A model that can't take the picture of the user's screen sent along with their words rejects the request
                    // before answering; ask again with the words only, and drop the picture for the rest of this reply.
                    if (!imageDropped && request.ImageOptional && input.Image is not null && result.Text.Length == 0 &&
                        result.Failure is ProviderFailureCode.RequestRejected or ProviderFailureCode.ModelUnsupported or
                            ProviderFailureCode.FormatRejected or ProviderFailureCode.InputLimit)
                    {
                        imageDropped = true;
                        lock (Sync)
                        {
                            CheckActive();
                            // Only the selected Thinking model is reported as rejecting the picture.
                            if (!fallback) imageRejected = true;
                        }
                        continue;
                    }
                    // A model without tool support (many local models) rejects the request before answering; ask once more
                    // without tools so the conversation keeps working.
                    if (!retried && rounds.Count == 0 && input.Tools.Count > 0 && result.Text.Length == 0 &&
                        result.Failure == ProviderFailureCode.RequestRejected)
                    {
                        retried = true;
                        lock (Sync)
                        {
                            CheckActive();
                            // Only the selected Thinking model is remembered as rejecting tools.
                            if (!fallback) toolsRejected = true;
                            // Rejected again without the recording or picture: the tools were the problem, not those.
                            if (audioDropped) audioRejected = false;
                            if (imageDropped) imageRejected = false;
                        }
                        input = request.Input.WithoutTools();
                        continue;
                    }
                    // A model that always thinks refuses Thinking steps Off (and a strict server may refuse the control
                    // itself) before answering; ask again with the model's own default for the rest of this reply.
                    if (!reasoningDropped && !fallback && request.Generation?.Reasoning is not null && result.Text.Length == 0 &&
                        result.Failure == ProviderFailureCode.RequestRejected)
                    {
                        reasoningDropped = true;
                        lock (Sync)
                        {
                            CheckActive();
                            reasoningRejected = true;
                            // Rejected again without those: the Thinking steps choice was the problem, not them.
                            if (audioDropped) audioRejected = false;
                            if (imageDropped) imageRejected = false;
                            if (retried) toolsRejected = false;
                        }
                        continue;
                    }
                    // Nothing of this answer arrived yet, so the fallback can give it instead.
                    if (!fallback && result.Text.Length == 0 &&
                        FallBack(before, result.Failure?.ToString() ?? result.Issue?.ToString() ?? result.End.ToString()))
                    {
                        fallback = true;
                        continue;
                    }
                    Fail(result.End == RoundEnd.Failed ? ConversationFailure.ProviderFailed : ConversationFailure.InvalidStream,
                        result.Failure, result.Issue, ProviderRole.Llm);
                    return;
                }
                if (result.End == RoundEnd.Refused)
                {
                    lock (Sync)
                    {
                        CheckActive();
                        refused = true;
                        refusal = result.Refusal;
                    }
                    return;
                }
                if (result.Calls.Count > 0 && input.ToolCallsAllowed && request.Tools is { } tools)
                {
                    if (!string.IsNullOrWhiteSpace(result.Text))
                    {
                        // Say what was written so far while the tools run.
                        lock (Sync)
                        {
                            CheckActive();
                            if (shown.Push("\n") is { Length: > 0 } line)
                            {
                                text.Append(line);
                                Emit(ConversationEventKind.Text, line);
                            }
                        }
                        Caption(captions => captions.Push("\n"));
                        if (segmenter is not null) await StageAsync(segmenter.Push("\n"), whole).ConfigureAwait(false);
                    }
                    var results = await CallToolsAsync(tools, result.Calls, rounds).ConfigureAwait(false);
                    rounds.Add(new(result.Text, result.Calls, results));
                    input = request.Input.WithToolRounds(rounds, callsAllowed: rounds.Count < request.Limits.MaxToolRounds);
                    continue;
                }
                // Calls the model makes when no more are allowed are ignored; it still has to have answered in text, in this
                // round or one before it (a model that said what it was doing before a tool call, such as "let me think about
                // that" before think_longer, may have nothing to add once the call returns).
                bool said;
                lock (Sync) said = text.Length > 0;
                if (string.IsNullOrWhiteSpace(result.Text) && (rounds.Count == 0 || !said))
                {
                    Fail(ConversationFailure.InvalidStream, null, SequenceIssue.EmptyCompletion);
                    return;
                }
                lock (Sync)
                {
                    CheckActive();
                    if (shown.Finish() is { Length: > 0 } rest)
                    {
                        text.Append(rest);
                        Emit(ConversationEventKind.Text, rest);
                    }
                    textComplete = true;
                }
                Caption(captions => captions.Finish());
                // The text is all shown; staging its last sentence for the voice can only end what is said aloud.
                if (segmenter is not null)
                {
                    try { await StageAsync(segmenter.Finish(), whole).ConfigureAwait(false); }
                    catch (ConversationException error) { StopSpeaking(error.Failure); }
                }
                return;
            }
        }
        finally
        {
            lock (Sync)
            {
                segmentation?.Clear();
                segmentation = null;
                textWindow = null;
                activeTool = null;
            }
            segments.Writer.TryComplete();
        }
    }

    private async Task<RoundResult> RequestAsync(BoundedTextInput input, CorrelationIds ids, SpeechSegmenter? segmenter,
        bool fallback = false, bool withoutReasoning = false, bool hedged = false)
    {
        var model = fallback ? request.Fallback!.Model : request.Model;
        var window = new MonotonicWindow(Clock, request.TextLimits.MaxRequestTime);
        lock (Sync) textWindow = window;
        Check(window);
        var context = new ProviderRequestContext { Ids = ids, Epoch = Epoch, Deadline = Deadline(window) };
        var budget = new OperationBudget(ids, Epoch, ProviderRole.Llm, 1, input.Utf8Bytes,
            input.InputTokenReservation, request.TextLimits.MaxOutputTokens, 0);
        var action = new TextAuthorizationAction(context, input, model, request.TextLimits, budget);
        var permission = await authorization.AuthorizeTextAsync(action, stop.Token).ConfigureAwait(false);
        Check(window);
        if (permission?.Authorization is not { } consent)
            throw new ConversationException(ConversationFailure.AuthorizationUnavailable);
        window = ValidateReservation(permission.Reservation, budget, consent.ExpiresAt, context.Deadline, window);
        lock (Sync) textWindow = window;
        Check(window);
        // Clamp to remaining ORIGINAL stage/turn budgets after a potentially slow authorization callback.
        context = context with { Deadline = Deadline(window) };
        var requestedAfter = Clock.GetElapsedTime(startedAt);
        lock (Sync) textRequestAfter ??= requestedAfter;
        // Backup Thinking races the reply's first request only. That request then has a stop of its own, so the stream that
        // loses is stopped alone.
        var backup = hedged && !fallback ? request.Backup : null;
        var own = backup is null ? null : CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        TextRun run;
        try
        {
            var stream = Owner.StreamText(context, request, input, consent, originalCaller, fallback, withoutReasoning);
            run = new(stream, TextValidator(ids, stream, input), own, own?.Token ?? stop.Token, requestedAfter);
        }
        catch
        {
            own?.Dispose();
            throw;
        }
        try
        {
            lock (Sync)
            {
                CheckActive();
                segmentation = segmenter;
                textProvenance = run.Stream.Capabilities.Provenance;
                openStream = run.Stream;
                openStreamAfter = requestedAfter;
                SetState(ConversationState.Generating);
            }
            if (backup is not null) run = await RaceAsync(run, backup, input).ConfigureAwait(false);
            var said = new StringBuilder();
            // What the race already read from the stream that answers.
            var step = run.Step;
            while (true)
            {
                if (step == RunStep.None)
                {
                    Check(window);
                    bool moved = await run.Events.MoveNextAsync().ConfigureAwait(false);
                    Check(window);
                    NoteTextTimings(run.Stream, run.RequestedAfter);
                    if (!moved) break;
                    var update = run.Validator.Accept(run.Events.Current);
                    if (update.Snapshot.Result?.Outcome == TurnOutcome.Failed)
                        return new(RoundEnd.Failed, said.ToString(), [], run.Stream.Result?.Failure?.Code, update.Snapshot.Issue);
                }
                else if (step == RunStep.Ended) break;
                else if (step == RunStep.Failed)
                    return new(RoundEnd.Failed, said.ToString(), [], run.Stream.Result?.Failure?.Code, run.Validator.Snapshot.Issue);
                step = RunStep.None;
                while (run.Validator.TryReadText(out var chunk))
                {
                    Check(window);
                    lock (Sync)
                    {
                        CheckActive();
                        firstTextAfter ??= Clock.GetElapsedTime(startedAt);
                        // The chat and the saved conversation show words only; the voice gets its tags from the segmenter.
                        if (shown.Push(chunk!.Text) is { Length: > 0 } visible)
                        {
                            text.Append(visible);
                            Emit(ConversationEventKind.Text, visible);
                        }
                    }
                    said.Append(chunk.Text);
                    Caption(captions => captions.Push(chunk.Text));
                    if (segmenter is not null) await StageAsync(segmenter.Push(chunk.Text), window).ConfigureAwait(false);
                }
            }
            Check(window);
            var end = run.Validator.EndOfInput().Snapshot;
            lock (Sync)
            {
                textWindow = null;
                if (run.Stream.Result?.Usage is { InputTokens: { } read } usage)
                {
                    inputTokens = (inputTokens ?? 0) + read;
                    if (usage.CachedInputTokens is { } cached) cachedInputTokens = (cachedInputTokens ?? 0) + Math.Min(cached, read);
                }
            }
            return end.Result?.Outcome switch
            {
                TurnOutcome.Completed => new(RoundEnd.Completed, said.ToString(), run.Stream.Result?.ToolCalls ?? []),
                TurnOutcome.Refused => new(RoundEnd.Refused, said.ToString(), [], Refusal: run.Validator.RefusalText),
                _ => new(RoundEnd.Invalid, said.ToString(), [], run.Stream.Result?.Failure?.Code, end.Issue)
            };
        }
        finally
        {
            await run.DisposeAsync().ConfigureAwait(false);
        }
    }

    private ProviderSequenceValidator TextValidator(CorrelationIds ids, ITextGenerationStream stream, BoundedTextInput input) =>
        new(new()
        {
            Ids = ids, Epoch = Epoch, Capabilities = stream.Capabilities
        }, new()
        {
            MaxIngressEvents = request.TextLimits.MaxEvents + 2,
            MaxTextCharacters = request.TextLimits.MaxTextCharacters, MaxQueuedChunks = 1,
            FirstEventTimeout = request.TextLimits.FirstDeltaTimeout, IdleTimeout = request.TextLimits.IdleTimeout,
            TotalTimeout = request.TextLimits.MaxRequestTime, AllowEmptyCompletion = input.Tools.Count > 0
        }, Clock, stop.Token);

    private enum RunStep { None, Words, Ended, Failed }

    // One Thinking stream being read: its checks and its events (with a stop of its own while Backup Thinking races it), when its
    // request started, what the race already read from it, and a backup member's stream and lease.
    private sealed class TextRun(ITextGenerationStream stream, ProviderSequenceValidator validator, CancellationTokenSource? stop,
        CancellationToken token, TimeSpan requestedAfter, ThinkingBackupStream? backup = null) : IAsyncDisposable
    {
        private int disposed;
        internal ITextGenerationStream Stream { get; } = stream;
        internal ProviderSequenceValidator Validator { get; } = validator;
        internal CancellationTokenSource? Stop { get; } = stop;
        internal IAsyncEnumerator<ProviderEvent> Events { get; } = stream.GetAsyncEnumerator(token);
        internal TimeSpan RequestedAfter { get; } = requestedAfter;
        internal ThinkingBackupStream? Backup { get; } = backup;
        internal RunStep Step { get; set; }

        // It answered the race: it has words, or it ended with a whole answer without words (a tool call or a refusal).
        internal bool Answered => Step == RunStep.Words ||
            Step == RunStep.Ended && Validator.Snapshot.Result?.Outcome is TurnOutcome.Completed or TurnOutcome.Refused;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { await Events.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                Validator.Dispose();
                Stop?.Dispose();
                if (Backup?.Lease is { } lease) await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // Backup Thinking's race (IThinkingBackup): the reply's first request reads on alone until its first words. With none Delay
    // after it started, the same request also goes to the backup, and the stream with words first answers. The other is
    // stopped at once and let go off the reply's path, so it never holds up the words. A reply started early asks a paid member
    // only once it is taken. When neither answers (both failed or ended), the reply's own request goes on as if alone: its
    // failure leads to the usual retries and the Thinking fallback.
    private async Task<TextRun> RaceAsync(TextRun primary, IThinkingBackup backup, BoundedTextInput input)
    {
        var delay = backup.Delay;
        var primaryWords = FirstWordsAsync(primary);
        TextRun? second = null;
        Task<RunStep>? secondWords = null;
        CancellationTokenSource? backupStop = null;
        try
        {
            var wait = primary.RequestedAfter + delay - Clock.GetElapsedTime(startedAt);
            if (wait > TimeSpan.Zero)
            {
                using var timer = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                await Task.WhenAny(primaryWords, Task.Delay(wait, Clock, timer.Token)).ConfigureAwait(false);
                await timer.CancelAsync().ConfigureAwait(false);
            }
            stop.Token.ThrowIfCancellationRequested();
            if (primaryWords.IsCompleted)
            {
                primary.Step = await primaryWords.ConfigureAwait(false);
                Decided(backup, new(ThinkingBackupOutcome.NotNeeded, delay));
                return primary;
            }
            backupStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            var ids = NewIds();
            var held = Held;
            var opened = await OpenBackupAsync(backup, input, ids, held, backupStop.Token).ConfigureAwait(false);
            if (opened is null && held && hold is { } taken)
            {
                // Started early: a paid member may answer only once the reply is taken.
                await Task.WhenAny(primaryWords, taken.Task).WaitAsync(stop.Token).ConfigureAwait(false);
                if (!primaryWords.IsCompleted) opened = await OpenBackupAsync(backup, input, ids = NewIds(), false, backupStop.Token).ConfigureAwait(false);
            }
            var askedAfter = Clock.GetElapsedTime(startedAt);
            if (opened is null)
            {
                backupStop.Dispose();
                backupStop = null;
                Decided(backup, new(ThinkingBackupOutcome.NoMember, delay));
                primary.Step = await primaryWords.ConfigureAwait(false);
                return primary;
            }
            try
            {
                second = new(opened.Stream, TextValidator(ids, opened.Stream, input), backupStop, backupStop.Token, askedAfter, opened);
                backupStop = null;
            }
            catch (Exception error) when (error is ContractException or ArgumentException or InvalidOperationException)
            {
                backupStop?.Dispose();
                backupStop = null;
                if (opened.Lease is { } lease) _ = lease.DisposeAsync().AsTask();
                Decided(backup, new(ThinkingBackupOutcome.Failed, delay, opened.Name, askedAfter, Why: "its stream can't be read"));
                primary.Step = await primaryWords.ConfigureAwait(false);
                return primary;
            }
            // The conversation's model had words while the member was chosen: nothing is sent to the member.
            if (primaryWords.IsCompletedSuccessfully)
            {
                primary.Step = primaryWords.Result;
                if (primary.Answered)
                {
                    LetGo(second, null);
                    second = null;
                    Decided(backup, new(ThinkingBackupOutcome.NotNeeded, delay));
                    return primary;
                }
            }
            secondWords = FirstWordsAsync(second);
            TextRun? winner = null;
            Exception? primaryError = null;
            string? backupWhy = null;
            List<Task<RunStep>> pending = [primaryWords, secondWords];
            while (winner is null && pending.Count > 0)
            {
                var done = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(done);
                var run = done == primaryWords ? primary : second;
                try
                {
                    run.Step = await done.ConfigureAwait(false);
                    if (run.Answered) winner = run;
                    else if (run == second) backupWhy = run.Step == RunStep.Failed ? "it failed" : "it ended without an answer";
                }
                catch (Exception error) when (run == primary) { primaryError = error; }
                catch (Exception error) { backupWhy = error is OperationCanceledException ? "it was stopped" : "it failed"; }
                stop.Token.ThrowIfCancellationRequested();
                // A member that failed lets go of its slot and its computer at once, while the conversation's model goes on.
                if (backupWhy is not null && winner is null) LetGo(second, secondWords);
            }
            if (winner is null)
            {
                // Neither answered: the reply's own request goes on as if alone.
                LetGo(second, secondWords);
                second = null;
                Decided(backup, new(ThinkingBackupOutcome.Failed, delay, opened.Name, askedAfter, Why: backupWhy));
                if (primaryError is not null) ExceptionDispatchInfo.Throw(primaryError);
                return primary;
            }
            var firstWords = Clock.GetElapsedTime(startedAt);
            if (winner == second)
            {
                LetGo(primary, primaryWords);
                lock (Sync)
                {
                    textProvenance = second.Stream.Capabilities.Provenance;
                    openStream = second.Stream;
                    openStreamAfter = askedAfter;
                }
                second = null;
                Decided(backup, new(ThinkingBackupOutcome.Won, delay, opened.Name, askedAfter, firstWords));
                return winner;
            }
            LetGo(second, secondWords);
            second = null;
            Decided(backup, new(backupWhy is null ? ThinkingBackupOutcome.Lost : ThinkingBackupOutcome.Failed, delay, opened.Name, askedAfter,
                firstWords, backupWhy));
            return primary;
        }
        catch
        {
            // The turn stopped (or the reply's own request failed with no backup to answer): the backup's stream is let go here,
            // the reply's own by the caller once its read ended.
            backupStop?.Dispose();
            if (second is not null) LetGo(second, secondWords);
            try { primary.Stop?.Cancel(); }
            catch (ObjectDisposedException) { }
            try { await primaryWords.ConfigureAwait(false); }
            catch (Exception) { }
            throw;
        }
    }

    // Reads a stream for the race until it has words to show, ends (a whole answer without words ends it too) or fails.
    private static async Task<RunStep> FirstWordsAsync(TextRun run)
    {
        while (true)
        {
            if (!await run.Events.MoveNextAsync().ConfigureAwait(false)) return RunStep.Ended;
            var snapshot = run.Validator.Accept(run.Events.Current).Snapshot;
            if (snapshot.Result?.Outcome == TurnOutcome.Failed) return RunStep.Failed;
            if (snapshot.QueuedChunks > 0) return RunStep.Words;
        }
    }

    // The backup's stream, or null when no member may take the request or it couldn't be opened.
    private async Task<ThinkingBackupStream?> OpenBackupAsync(IThinkingBackup backup, BoundedTextInput input, CorrelationIds ids,
        bool held, CancellationToken token)
    {
        try { return await backup.OpenAsync(request, input, ids, Epoch, held, token).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested) { return null; }
    }

    // The stream that lost the race (or failed in it) is stopped at once and let go once its read ended, off the reply's path.
    private static void LetGo(TextRun run, Task<RunStep>? reading)
    {
        try { run.Stop?.Cancel(); }
        catch (ObjectDisposedException) { }
        _ = Task.Run(async () =>
        {
            if (reading is not null)
                try { await reading.ConfigureAwait(false); }
                catch (Exception) { }
            try { await run.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { }
        });
    }

    private void Decided(IThinkingBackup backup, ThinkingBackupResult result)
    {
        lock (Sync) backupResult = result;
        try { backup.Ended(result); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    // The first request's response headers and first hidden reasoning, in the turn's own time (the stream counts from its
    // creation, just after textRequestAfter).
    private void NoteTextTimings(ITextGenerationStream stream, TimeSpan requestedAfter)
    {
        var response = stream.ResponseAfter;
        var reasoning = stream.FirstReasoningAfter;
        if (response is null && reasoning is null) return;
        lock (Sync)
        {
            if (textRequestAfter != requestedAfter) return;
            if (response is { } headers) textResponseAfter ??= requestedAfter + headers;
            if (reasoning is { } thinking) firstReasoningAfter ??= requestedAfter + thinking;
        }
    }

    // Tool calls run one at a time; each result is cut to its share of what is left of the reply's tool budget. A reply started
    // early calls no tool until it is released: a tool may act on the world.
    private async Task<IReadOnlyList<TextToolResult>> CallToolsAsync(IConversationToolHost tools, IReadOnlyList<TextToolCall> calls,
        IReadOnlyList<TextToolRound> earlier)
    {
        await WhileHeldAsync().ConfigureAwait(false);
        var available = BoundedTextInput.RemainingToolExchangeBytes(earlier) - calls.Sum(c => c.Utf8Bytes) - 4096;
        var share = Math.Max(256, available / calls.Count);
        var results = new List<TextToolResult>(calls.Count);
        foreach (var call in calls)
        {
            lock (Sync)
            {
                CheckActive();
                textWindow = null;
                activeTool = call.Name;
                toolCalls++;
                Emit(ConversationEventKind.State);
            }
            ConversationToolResult outcome;
            try { outcome = await tools.CallAsync(call, stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
            // A tool host boundary can fail arbitrarily; the model hears that the tool failed, never the exception text.
            catch (Exception) { outcome = new("The tool failed on this PC.", true); }
            lock (Sync) CheckActive();
            results.Add(new(call.CallId, TextToolResult.Bound((outcome.IsError ? "Error: " : "") + outcome.Output, share)));
        }
        lock (Sync)
        {
            activeTool = null;
            Emit(ConversationEventKind.State);
        }
        return results;
    }
    private async Task StageAsync(IEnumerable<SpeechPiece> pieces, MonotonicWindow window)
    {
        using var iterator = pieces.GetEnumerator();
        while (await segments.Writer.WaitToWriteAsync(stop.Token).ConfigureAwait(false))
        {
            Check(window);
            lock (Sync)
            {
                CheckActive();
                // Reserve capacity before advancing segmentation; serialize its buffer with Stop/Clear.
                if (!iterator.MoveNext()) return;
                if (iterator.Current is { Text: null, Cues: null or { Count: 0 } })
                {
                    suppressed++;
                    Emit(ConversationEventKind.SpeechSuppressed);
                }
                else
                {
                    // A piece with no words but character cues still goes in order, so the character acts after what came before.
                    if (!segments.Writer.TryWrite(iterator.Current))
                        throw new ConversationException(ConversationFailure.InvalidStream);
                    firstSegmentAfter ??= Clock.GetElapsedTime(startedAt);
                    peakQueued = Math.Max(peakQueued, segments.Reader.Count);
                    if (iterator.Current.Text is not null) Emit(ConversationEventKind.SegmentQueued);
                }
            }
        }
        Check(window);
    }

    // One sentence ready to play: its audio arrives in Frames while it is synthesized, ahead of its turn to play.
    private sealed class SpeechTake(string text, int number, CorrelationIds ids)
    {
        internal string Text { get; } = text;
        internal int Number { get; } = number;
        internal CorrelationIds Ids { get; } = ids;
        internal Channel<PcmFrame> Frames { get; } = Channel.CreateUnbounded<PcmFrame>(new UnboundedChannelOptions
        {
            SingleWriter = true, SingleReader = true, AllowSynchronousContinuations = false
        });
        internal long? FinalSamples { get; set; }
        internal bool ProviderFailed { get; set; }
        internal ProviderFailureCode? Failure { get; set; }
        internal ExceptionDispatchInfo? Error { get; set; }
        // A sentence written after the voice failed: it is only shown in the captions, in its place after the ones before it.
        internal bool CaptionOnly { get; init; }
        // Character cues with no words of their own (a tag after the reply's last sentence): acted in their place.
        internal bool CuesOnly { get; init; }
        internal IReadOnlyList<SpeechCue>? Cues { get; init; }
        // Its words were posted to the captions (when its playback started).
        internal bool Captioned { get; set; }
    }

    // Synthesis runs one sentence ahead of playback: while sentence N plays, sentence N+1 is already being synthesized, so
    // there is no synthesis gap between sentences. A synthesis failure is raised only when playback reaches that sentence,
    // so what is already playing finishes first, as it did before. A voice failure then stops speaking (StopSpeaking); both
    // stages keep draining until the text ends, so the reply's text is never held up or cut short by its voice.
    private async Task SpeakAsync()
    {
        if (request.Speech is not { } voice) return;
        var ready = Channel.CreateBounded<SpeechTake>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = true, AllowSynchronousContinuations = false
        });
        await Task.WhenAll(GuardStageAsync(() => SynthesizeAllAsync(voice, ready.Writer)),
            GuardStageAsync(() => PlayAllAsync(voice, ready.Reader))).ConfigureAwait(false);
    }

    private async Task SynthesizeAllAsync(SpeechOutput voice, ChannelWriter<SpeechTake> ready)
    {
        var broken = false;
        try
        {
            // Started early without its voice: nothing is synthesized until the turn is released.
            if (holdVoice) await WhileHeldAsync().ConfigureAwait(false);
            await foreach (var piece in segments.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                if (piece.Text is null)
                {
                    if (await ready.WaitToWriteAsync(stop.Token).ConfigureAwait(false))
                        ready.TryWrite(new SpeechTake("", 0, NewIds()) { CuesOnly = true, Cues = piece.Cues });
                    continue;
                }
                // Once the speech budget is spent, a sentence failed or the voice stopped, the rest of the reply is text only;
                // keep draining so the text still finishes. After a voice failure each sentence still reaches the captions.
                if (broken || speaking.IsCancellationRequested)
                {
                    if (await ready.WaitToWriteAsync(stop.Token).ConfigureAwait(false))
                        ready.TryWrite(new SpeechTake(piece.Text, 0, NewIds()) { CaptionOnly = true, Cues = piece.Cues });
                    continue;
                }
                if (speechLimitReached) continue;
                if (!await ready.WaitToWriteAsync(stop.Token).ConfigureAwait(false)) return;
                // The voice stopped (failed or was muted) while this sentence waited for its turn: it goes to the captions too.
                if (speaking.IsCancellationRequested)
                {
                    ready.TryWrite(new SpeechTake(piece.Text, 0, NewIds()) { CaptionOnly = true, Cues = piece.Cues });
                    continue;
                }
                SpeechTake? take;
                try { take = Reserve(piece.Text, voice, piece.Cues); }
                catch (ConversationException error)
                {
                    StopSpeaking(error.Failure);
                    continue;
                }
                catch (ContractException)
                {
                    StopSpeaking(ConversationFailure.InvalidStream);
                    continue;
                }
                if (take is null) continue;
                if (!ready.TryWrite(take)) throw new ConversationException(ConversationFailure.InvalidStream);
                var window = new MonotonicWindow(Clock, voice.Limits.MaxRequestTime);
                lock (Sync)
                {
                    speechWindow = window;
                    synthesizing = true;
                }
                try { broken = !await SynthesizeAsync(take, voice, window).ConfigureAwait(false); }
                catch (Exception) { broken = true; }
                finally
                {
                    lock (Sync)
                    {
                        speechWindow = null;
                        synthesizing = false;
                    }
                }
            }
        }
        finally
        {
            ready.TryComplete();
            // Every sentence is made (or the voice stopped): only playback is left.
            synthesized.TrySetResult();
        }
    }

    private SpeechTake? Reserve(string segment, SpeechOutput voice, IReadOnlyList<SpeechCue>? cues = null)
    {
        var input = new BoundedSpeechInput(segment);
        lock (Sync)
        {
            CheckActive();
            if (committed >= request.Limits.MaxSpeechSegments ||
                input.Utf8Bytes > request.Limits.MaxSpeechTextBytes - reservedBytes ||
                voice.Limits.MaxSamples > request.Limits.MaxReservedSpeechSamples - reservedSamples)
            {
                // The aggregate speech budget ends what is said aloud, never the reply itself.
                speechLimitReached = true;
                suppressed++;
                Emit(ConversationEventKind.SpeechSuppressed);
                return null;
            }
            var take = new SpeechTake(segment, ++committed, NewIds()) { Cues = cues };
            reservedBytes += input.Utf8Bytes;
            reservedSamples += voice.Limits.MaxSamples;
            speechRequest ??= take.Ids.RequestId;
            if (playback is null) SetState(ConversationState.Authorizing);
            Emit(ConversationEventKind.SegmentStarted);
            return take;
        }
    }

    // Authorizes and synthesizes one sentence into its frame buffer. Returns false when the provider failed it.
    private async Task<bool> SynthesizeAsync(SpeechTake take, SpeechOutput voice, MonotonicWindow window)
    {
        try
        {
            CheckSpeaking(window);
            var input = new BoundedSpeechInput(take.Text);
            var context = new ProviderRequestContext { Ids = take.Ids, Epoch = Epoch, Deadline = Deadline(window) };
            var budget = new OperationBudget(take.Ids, Epoch, ProviderRole.Tts, 1, input.Utf8Bytes, 0, 0, voice.Limits.MaxSamples);
            var action = new SpeechAuthorizationAction(context, take.Number, input, voice.Selection, voice.Limits, budget);
            var permission = await authorization.AuthorizeSpeechAsync(action, speaking.Token).ConfigureAwait(false);
            CheckSpeaking(window);
            if (permission?.Authorization is not { } consent)
                throw new ConversationException(ConversationFailure.AuthorizationUnavailable);
            window = ValidateReservation(permission.Reservation, budget, consent.ExpiresAt, context.Deadline, window);
            lock (Sync) speechWindow = window;
            CheckSpeaking(window);
            context = context with { Deadline = Deadline(window) };
            var stream = Owner.StreamSpeech(context, request, input, consent, originalCaller);
            lock (Sync)
            {
                CheckActive();
                speaking.Token.ThrowIfCancellationRequested();
                speechRequestAfter ??= Clock.GetElapsedTime(startedAt);
                speechProvenance = stream.Capabilities.Provenance;
                if (playback is null) SetState(ConversationState.Synthesizing);
            }
            var heardFirst = false;
            var sampleRate = 0;
            await using (var enumeration = stream.GetAsyncEnumerator(speaking.Token))
            {
                while (true)
                {
                    CheckSpeaking(window);
                    bool moved = await enumeration.MoveNextAsync().ConfigureAwait(false);
                    CheckSpeaking(window);
                    if (!moved) break;
                    if (!take.Frames.Writer.TryWrite(enumeration.Current))
                        throw new ConversationException(ConversationFailure.InvalidStream);
                    if (!heardFirst)
                    {
                        heardFirst = true;
                        sampleRate = enumeration.Current.Format.SampleRate;
                        lock (Sync) firstSpeechAudioAfter ??= Clock.GetElapsedTime(startedAt);
                    }
                }
            }
            CheckSpeaking(window);
            if (stream.Result is not { Outcome: SpeechSynthesisOutcome.Completed, FinalSampleCount: { } samples })
            {
                take.ProviderFailed = true;
                take.Failure = stream.Result?.Failure?.Code;
                return false;
            }
            take.FinalSamples = samples;
            if (take.Number == 1 && sampleRate > 0)
                lock (Sync)
                {
                    firstPieceSynthesizedAfter ??= Clock.GetElapsedTime(startedAt);
                    firstPieceSpeech ??= TimeSpan.FromSeconds((double)samples / sampleRate);
                }
            return true;
        }
        catch (Exception error)
        {
            take.Error = ExceptionDispatchInfo.Capture(error);
            throw;
        }
        finally { take.Frames.Writer.TryComplete(); }
    }

    private async Task PlayAllAsync(SpeechOutput voice, ChannelReader<SpeechTake> ready)
    {
        // Started early: nothing plays, acts or shows until the turn is released, and the first piece waits here synthesized
        // (synthesis runs one piece ahead, so only that one is made meanwhile).
        await WhileHeldAsync().ConfigureAwait(false);
        await foreach (var take in ready.ReadAllAsync(stop.Token).ConfigureAwait(false))
        {
            if (take.CuesOnly)
            {
                PostCues(take, null, Task.CompletedTask);
                continue;
            }
            // Once the voice stopped, a sentence already synthesized is dropped unplayed (its words still show in the captions);
            // keep draining so synthesis never waits.
            if (take.CaptionOnly || speaking.IsCancellationRequested)
            {
                ShowUnsaid(take.Text);
                PostCues(take, null, Task.CompletedTask);
                continue;
            }
            var window = new MonotonicWindow(Clock, voice.Limits.MaxRequestTime);
            lock (Sync) playWindow = window;
            try { await PlayAsync(take, voice, window).ConfigureAwait(false); }
            catch (OperationCanceledException) when (speaking.IsCancellationRequested) { }
            catch (ConversationException error) { StopSpeaking(error.Failure); }
            catch (ContractException) { StopSpeaking(ConversationFailure.InvalidStream); }
            // A voice provider or the playback device can fail arbitrarily; only what is said aloud ends.
            catch (Exception) { StopSpeaking(ConversationFailure.DependencyFailed); }
            finally { lock (Sync) playWindow = null; }
            // The voice failed before this sentence started playing: its words still show.
            if (!take.Captioned && speaking.IsCancellationRequested) ShowUnsaid(take.Text);
        }
    }

    // The character acts on a sentence's cues as it starts playing, each one about where it was written: its share of the
    // sentence's words, of the sentence's audio length when that is known (otherwise of a natural reading pace). The cues
    // follow the reply's cue clock, so a pause holds them and a stop drops the ones not yet acted.
    private void PostCues(SpeechTake take, int? sampleRate, Task finished)
    {
        if (Owner.CharacterCues is not { } feed || take.Cues is not { Count: > 0 } cues) return;
        lock (Sync) if (userStopped) return;
        var length = Math.Max(1, take.Text.Length);
        var seconds = sampleRate is > 0 && take.FinalSamples is { } samples ? samples / (double)sampleRate.Value : length / 14.0;
        feed.Post(cues.Select(cue => new CharacterCue(cue.Tag,
            TimeSpan.FromSeconds(Math.Clamp(seconds * cue.Offset / length, 0, 30)))).ToArray(), finished, cueClock);
    }

    // Captions (speech bubble, subtitles) for words the voice couldn't say, one sentence after another for about as long as
    // reading each takes. A newer reply or Stop ends them; the turn never waits for them.
    private void ShowUnsaid(string text)
    {
        if (Owner.SpokenText is not { } feed || VoiceTags.Strip(text).Trim() is not { Length: > 0 } caption) return;
        lock (Sync)
        {
            if (userStopped) return;
            unsaidCaptions = CaptionAfterAsync(unsaidCaptions, feed, caption);
        }
    }

    private async Task CaptionAfterAsync(Task previous, SpokenTextFeed feed, string caption)
    {
        await previous.ConfigureAwait(false);
        lock (Sync)
            if (userStopped || Owner.CurrentEpoch != Epoch) return;
        var shown = Task.Delay(ReadingTime(caption), Clock);
        feed.Post(caption, shown);
        await shown.ConfigureAwait(false);
    }

    // A reply that isn't spoken: each sentence goes to the captions once it is written (or, started early, once it is released),
    // split and kept quiet ([pass]) the way the voice would say it. Captions never fail the reply: one that runs past its
    // limits just stops showing.
    private void Caption(Func<SpeechSegmenter, IEnumerable<SpeechPiece>> next)
    {
        if (captioner is null || captionsEnded) return;
        try
        {
            foreach (var piece in next(captioner))
                if (piece.Text is { } sentence) WhenReleased(() => ShowUnsaid(sentence));
        }
        catch (ConversationException) { captionsEnded = true; }
    }

    internal static TimeSpan ReadingTime(string caption) =>
        TimeSpan.FromSeconds(Math.Clamp(1.5 + caption.Length / 15.0, 2, 20));

    private async Task PlayAsync(SpeechTake take, SpeechOutput voice, MonotonicWindow window)
    {
        PlaybackRun? run = null;
        try
        {
            await foreach (var frame in take.Frames.Reader.ReadAllAsync(speaking.Token).ConfigureAwait(false))
            {
                CheckSpeaking(window);
                if (run is null)
                {
                    // A quick sound playing ahead of the reply ends first: the reply follows it and never cuts it.
                    await AfterQuickSoundAsync().ConfigureAwait(false);
                    lock (Sync)
                    {
                        CheckActive();
                        speaking.Token.ThrowIfCancellationRequested();
                        run = Owner.StartPlayback(this, take.Ids, voice.Output, Deadline(window), speaking.Token);
                        if (paused) run.Pause();
                        playback = run;
                        sentence = VoiceTags.Strip(take.Text).Trim();
                        if (sentence.Length > 0) saidAloud.Append(saidAloud.Length > 0 ? " " : "").Append(sentence);
                        playbackStartedAfter ??= Clock.GetElapsedTime(startedAt);
                        speechRequest = take.Ids.RequestId;
                        speechObservation = Owner.GeneratedSpeech?.Begin(run, frame.Format);
                        // Captions show the words only; the voice still hears its own tags ([laugh]...).
                        if (VoiceTags.Strip(take.Text).Trim() is { Length: > 0 } caption) Owner.SpokenText?.Post(caption, run.Completion);
                        PostCues(take, frame.Format.SampleRate, run.Completion);
                        take.Captioned = true;
                    }
                }
                await SubmitAsync(run, frame, window).ConfigureAwait(false);
            }
            CheckSpeaking(window);
            take.Error?.Throw();
            if (take.ProviderFailed)
            {
                StopSpeaking(ConversationFailure.ProviderFailed, take.Failure);
                return;
            }
            if (run is null || take.FinalSamples is not { } samples || !run.CompleteInput(samples))
                throw new ConversationException(ConversationFailure.PlaybackFailed);
            speechObservation?.CompleteInput(samples);
            var terminalPlayback = await run.Completion.WaitAsync(speaking.Token).ConfigureAwait(false);
            CheckSpeaking(window);
            if (terminalPlayback.State != PlaybackState.Completed)
                throw new ConversationException(ConversationFailure.PlaybackFailed);
        }
        finally
        {
            if (run is not null)
            {
                speechObservation?.Stop();
                // Never concurrently dispose a provider enumerator or release a native device.
                var finished = await run.StopAsync().ConfigureAwait(false);
                lock (Sync)
                {
                    lastPlayback = finished;
                    // A sentence that played entirely between two of the supervisor's looks was still heard, by now at the latest.
                    if (finished.MayHavePlayed) firstAudioAfter ??= Clock.GetElapsedTime(startedAt);
                    Emit(ConversationEventKind.Playback);
                }
                await run.DeviceRelease.ConfigureAwait(false);
                lock (Sync)
                {
                    var final = run.Snapshot;
                    accepted += final.AcceptedSamples;
                    submitted += final.SubmittedSamples;
                    consumed += final.DeviceConsumedSamples;
                    voiceWaits += final.Underruns;
                    voiceWaited += final.UnderrunTime;
                    mayHavePlayed |= final.MayHavePlayed;
                    quarantined |= !final.DeviceReleased || final.Error?.Code == ErrorCode.AudioPlaybackFailed;
                    lastPlayback = final;
                    playback = null;
                    sentence = null;
                    speechObservation = null;
                    speechRequest = null;
                    if (!invalidated && !textComplete)
                        SetState(synthesizing && !speaking.IsCancellationRequested ? ConversationState.Synthesizing : ConversationState.Generating);
                }
            }
        }
    }

    private async Task SubmitAsync(PlaybackRun run, PcmFrame frame, MonotonicWindow window)
    {
        while (true)
        {
            CheckSpeaking(window);
            var snapshot = run.Snapshot;
            if (run.Completion.IsCompleted)
                throw new ConversationException(ConversationFailure.PlaybackFailed);
            var capacity = (long)(frame.Format.SampleRate * Owner.PlaybackOptions.Capacity.TotalSeconds);
            if (snapshot.AcceptedSamples - snapshot.DeviceConsumedSamples + frame.SamplesPerChannel <= capacity &&
                snapshot.QueuedFrames < Owner.PlaybackOptions.MaximumQueuedFrames)
            {
                lock (Sync)
                {
                    CheckActive();
                    // PCM sequence/sample offsets are independent of TTS control event sequence 0/1.
                    var mapped = PlaybackFrameMapping.Map(frame, snapshot.Ids, Epoch, snapshot.Epoch);
                    if (run.Submit(mapped) != FrameAcceptance.Accepted)
                        throw new ConversationException(ConversationFailure.InvalidStream);
                    speechObservation?.Submit(mapped);
                    Emit(ConversationEventKind.Playback);
                }
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(10), Clock, speaking.Token).ConfigureAwait(false);
        }
    }

    // Waits for a quick sound still playing (PlayQuickSound); once the reply's own audio starts, none may begin.
    private async Task AfterQuickSoundAsync()
    {
        while (true)
        {
            Task? playing;
            lock (Sync)
            {
                playing = quickSound is { IsCompleted: false } running ? running : null;
                if (playing is null)
                {
                    ownAudioStarted = true;
                    return;
                }
            }
            await playing.WaitAsync(speaking.Token).ConfigureAwait(false);
        }
    }

    // Plays a quick sound on its own playback run, 20 ms a frame, and lets go of the speakers when it ends. It never fails the
    // reply: a quick sound that can't play (the reply stopped, the speakers failed) just ends, and the reply's own voice goes on.
    private async Task PlayQuickSoundAsync(ReadOnlyMemory<byte> pcm, SpeechOutput voice, CancellationToken token)
    {
        PlaybackRun? run = null;
        var ids = NewIds();
        var format = OpenAiSpeechSynthesisCatalog.PcmFormat;
        var window = new MonotonicWindow(Clock, voice.Limits.MaxRequestTime);
        try
        {
            lock (Sync)
            {
                CheckActive();
                token.ThrowIfCancellationRequested();
                run = Owner.StartPlayback(this, ids, voice.Output, Deadline(window), token);
                if (paused) run.Pause();
                quickRun = run;
                quickObservation = Owner.GeneratedSpeech?.Begin(run, format);
            }
            var frameBytes = format.SampleRate / 50 * format.BlockAlignment;
            long samples = 0, sequence = 0;
            for (var offset = 0; offset < pcm.Length; offset += frameBytes)
            {
                var frame = new PcmFrame(ids, Epoch, sequence++, samples, format, pcm.Span.Slice(offset, Math.Min(frameBytes, pcm.Length - offset)));
                await SubmitQuickAsync(run, frame, window, token).ConfigureAwait(false);
                samples += frame.SamplesPerChannel;
            }
            if (!run.CompleteInput(samples)) return;
            lock (Sync) quickObservation?.CompleteInput(samples);
            await run.Completion.WaitAsync(token).ConfigureAwait(false);
        }
        catch (Exception) { }
        finally
        {
            if (run is not null)
            {
                GeneratedSpeechObservation? observation;
                lock (Sync) observation = quickObservation;
                observation?.Stop();
                // Never release a native device concurrently with its own run.
                await run.StopAsync().ConfigureAwait(false);
                await run.DeviceRelease.ConfigureAwait(false);
                lock (Sync)
                {
                    var final = run.Snapshot;
                    quarantined |= !final.DeviceReleased || final.Error?.Code == ErrorCode.AudioPlaybackFailed;
                    quickRun = null;
                    quickObservation = null;
                    Emit(ConversationEventKind.Playback);
                }
            }
        }
    }

    private async Task SubmitQuickAsync(PlaybackRun run, PcmFrame frame, MonotonicWindow window, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Check(window);
            var snapshot = run.Snapshot;
            if (run.Completion.IsCompleted) throw new ConversationException(ConversationFailure.PlaybackFailed);
            var capacity = (long)(frame.Format.SampleRate * Owner.PlaybackOptions.Capacity.TotalSeconds);
            if (snapshot.AcceptedSamples - snapshot.DeviceConsumedSamples + frame.SamplesPerChannel <= capacity &&
                snapshot.QueuedFrames < Owner.PlaybackOptions.MaximumQueuedFrames)
            {
                lock (Sync)
                {
                    CheckActive();
                    var mapped = PlaybackFrameMapping.Map(frame, snapshot.Ids, Epoch, snapshot.Epoch);
                    if (run.Submit(mapped) != FrameAcceptance.Accepted) throw new ConversationException(ConversationFailure.InvalidStream);
                    quickObservation?.Submit(mapped);
                }
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(10), Clock, token).ConfigureAwait(false);
        }
    }

    private MonotonicWindow ValidateReservation(BudgetReservation? reservation, OperationBudget expected,
        DateTimeOffset authorizationExpiry, DateTimeOffset actionDeadline, MonotonicWindow original)
    {
        if (reservation is null || reservation.Reserved != expected ||
            authorizationExpiry > reservation.ExpiresAt || authorizationExpiry > actionDeadline)
            throw new ConversationException(ConversationFailure.BudgetUnavailable);
        // Expiry is measured from the action's original UTC/timestamp pair, never from the callback's return.
        // Clamp the dispatch deadline without changing the provider's already-scoped one-use authorization.
        return original.Restrict(authorizationExpiry, ConversationFailure.AuthorizationExpired)
            .Restrict(reservation.ExpiresAt, ConversationFailure.BudgetExpired);
    }

    private async Task ReleaseAsync(Task worker)
    {
        await worker.ConfigureAwait(false);
        Task pendingCallbacks;
        lock (Sync)
        {
            if (originalCaller.IsCancellationRequested) CancelByUser();
            workFinished = true;
            pendingCallbacks = Task.WhenAll(callbacks, speechCallbacks);
        }
        try { await pendingCallbacks.ConfigureAwait(false); }
        catch (Exception)
        {
            lock (Sync)
            {
                quarantined = true;
                if (failure == ConversationFailure.None) failure = ConversationFailure.DependencyFailed;
            }
        }
        await callerRegistration.DisposeAsync().ConfigureAwait(false);
        speaking.Dispose();
        stop.Dispose();
        lock (Sync)
        {
            released = true;
            Emit(ConversationEventKind.Released);
            release.TrySetResult();
            if (terminal) events.Writer.TryComplete();
        }
    }

    private async Task SuperviseAsync()
    {
        using var ticks = new CancellationTokenSource();
        while (!release.Task.IsCompleted && !stopSignal.Task.IsCompleted)
        {
            lock (Sync)
            {
                if (originalCaller.IsCancellationRequested) CancelByUser();
                if (playback?.Snapshot is { } progress && progress != observedPlayback)
                {
                    observedPlayback = progress;
                    if (progress.MayHavePlayed || progress.State == PlaybackState.Playing)
                        firstAudioAfter ??= Clock.GetElapsedTime(startedAt);
                    if (!invalidated && progress.State == PlaybackState.Playing) SetState(ConversationState.Playing);
                    Emit(ConversationEventKind.Playback);
                }
                // The first request's response headers and hidden reasoning show as soon as they arrive, not only with the first
                // words, so a quick sound knows at once that the model thinks before it answers.
                if (openStream is { } open && textRequestAfter == openStreamAfter)
                {
                    if (open.ResponseAfter is { } headers) textResponseAfter ??= openStreamAfter + headers;
                    if (open.FirstReasoningAfter is { } thinking) firstReasoningAfter ??= openStreamAfter + thinking;
                }
                // The whole turn ran out of time: after the text finished, only the voice still speaking ends.
                if (whole.Expired)
                {
                    if (textComplete && request.Speech is not null) StopSpeaking(ConversationFailure.DeadlineExceeded);
                    else Fail(ConversationFailure.DeadlineExceeded);
                }
                else if (textWindow?.Expired == true) Fail(textWindow.ExpiryFailure);
                // A sentence that takes too long to synthesize or play ends the voice, not the reply.
                else if (speechWindow?.Expired == true) StopSpeaking(speechWindow.ExpiryFailure);
                else if (playWindow?.Expired == true) StopSpeaking(playWindow.ExpiryFailure);
            }
            if (stopSignal.Task.IsCompleted) break;
            await Task.WhenAny(release.Task, stopSignal.Task,
                Task.Delay(TimeSpan.FromMilliseconds(10), Clock, ticks.Token)).ConfigureAwait(false);
        }
        if (!release.Task.IsCompleted)
            await Task.WhenAny(release.Task, Task.Delay(request.Limits.ShutdownTimeout, Clock, ticks.Token)).ConfigureAwait(false);
        await ticks.CancelAsync().ConfigureAwait(false);
        lock (Sync)
        {
            terminal = true;
            state = userStopped ? ConversationState.Canceled :
                failure != ConversationFailure.None ? text.Length > 0 ? ConversationState.Partial : ConversationState.Failed :
                refused ? ConversationState.Refused : ConversationState.Completed;
            Emit(ConversationEventKind.State);
            completion.TrySetResult(GetSnapshot());
            synthesized.TrySetResult();
            if (released) events.Writer.TryComplete();
        }
    }

    private CorrelationIds NewIds() => new() { SessionId = SessionId, TurnId = TurnId, RequestId = Guid.NewGuid() };
    private void SetState(ConversationState value)
    {
        if (state == value || terminal) return;
        state = value;
        Emit(ConversationEventKind.State);
    }
    private void Emit(ConversationEventKind kind, string? delta = null) =>
        events.Writer.TryWrite(new(++eventSequence, kind, GetSnapshot(), delta));

    private ConversationSnapshot GetSnapshot()
    {
        var currentPlayback = playback?.Snapshot;
        return new(SessionId, TurnId, TextIds.RequestId, Epoch, Owner.CurrentEpoch, state, failure, providerFailure, sequenceFailure,
            text.Length, textComplete, segments.Reader.Count, peakQueued, committed, suppressed, reservedBytes, reservedSamples,
            speechRequest, textProvenance, speechProvenance,
            accepted + (currentPlayback?.AcceptedSamples ?? 0), submitted + (currentPlayback?.SubmittedSamples ?? 0),
            consumed + (currentPlayback?.DeviceConsumedSamples ?? 0), mayHavePlayed || currentPlayback?.MayHavePlayed == true,
            released, quarantined || (currentPlayback is { State: PlaybackState.Failed, DeviceReleased: false }),
            Interlocked.Read(ref dropped), currentPlayback ?? lastPlayback, retryOf, earlierSpeech, toolCalls, activeTool, toolsRejected,
            speechLimitReached, failedProvider, fellBackAfter, audioRejected, firstTextAfter, firstAudioAfter, imageRejected,
            speechFailure, new(textRequestAfter, textResponseAfter, firstReasoningAfter, firstSegmentAfter, speechRequestAfter,
                firstSpeechAudioAfter, firstPieceSynthesizedAfter, firstPieceSpeech, playbackStartedAfter,
                voiceWaits + (currentPlayback?.Underruns ?? 0), voiceWaited + (currentPlayback?.UnderrunTime ?? TimeSpan.Zero),
                pauses, resumes, pausedTime + (paused ? Clock.GetElapsedTime(pausedAt) : TimeSpan.Zero), hold is not null, releasedAfter,
                quickSoundAfter),
            inputTokens, cachedInputTokens,
            reasoningRejected, voiceMuted, backupResult);
    }
}
