using System.Text;
using System.Threading.Channels;
using Martlet.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Streaming;
using Martlet.Providers;

namespace Martlet.Conversation;

// Turn state: everything a turn knows, guarded by one lock (Sync), with its life cycle (start, hold and release, stop, failure,
// the end and its snapshot). The other parts work on this state: generation (ConversationTurn.Generation.cs), the speech pipeline
// (ConversationTurn.Speech.cs), the fallback policy (ConversationTurn.Fallback.cs) and the Thinking trace (ConversationTurn.Trace.cs).
public sealed partial class ConversationTurn
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
    // Every Thinking request the reply sent, in order (SentRequests); guarded by Sync. References only, so a reply pays nothing.
    private readonly List<ConversationSentRequest> sentRequests = [];
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
    private TimeSpan? providerRetryAfter;
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
        bool prepareVoice = true, string? purpose = null)
    {
        Owner = owner;
        (trace, traceName) = TraceFor(owner, purpose);
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
            tag => { if (ReplyTag.Of(tag, voiceTags) is { } act) acted.Add(act); }, SpeechSegmenter.QuietTag);
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

    /// <summary>The request this reply started from: its first input, model, limits and generation settings. Private content
    /// (<see cref="ConversationRequest.Input"/>): show it on this PC only, never in logs, status files or MCP.</summary>
    public ConversationRequest Request => request;

    /// <summary>Every Thinking request this reply sent so far, in order (<see cref="ConversationSentRequest"/>): the first, each
    /// one after a tool round, and each retry or fallback. Private content, as <see cref="Request"/>.</summary>
    public IReadOnlyList<ConversationSentRequest> SentRequests { get { lock (Sync) return [.. sentRequests]; } }

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

    // WhileHeldAsync, which the Thinking trace says it waits for.
    private async Task HeldAsync()
    {
        var held = WhileHeldAsync();
        if (held.IsCompleted)
        {
            await held.ConfigureAwait(false);
            return;
        }
        TraceWaiting("the talk window to take this reply (it started early)");
        try { await held.ConfigureAwait(false); }
        finally { TraceWaiting(null); }
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
        TraceStart();
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
        ProviderRole? failedRole = null, TimeSpan? retryAfter = null)
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
            providerRetryAfter = provider is null ? null : retryAfter;
            failedProvider = provider is null ? null : failedRole;
            sequenceFailure = sequence is { } issue ? new(issue) : null;
            Invalidate();
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
            string? notice = null;
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
                if (trace is not null) notice = DueNotice();
            }
            if (notice is not null) TraceLine(notice);
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
            var final = GetSnapshot();
            // The trace's end line is written before anyone hears the turn ended (its listener never blocks).
            TraceEnd(final);
            completion.TrySetResult(final);
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
            reasoningRejected, voiceMuted, backupResult) { ProviderRetryAfter = providerRetryAfter };
    }
}
