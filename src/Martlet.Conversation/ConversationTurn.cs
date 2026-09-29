using System.Text;
using System.Threading.Channels;
using Martlet.Audio;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;
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
    private readonly TaskCompletionSource stopSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ConversationSnapshot> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<SpeechPiece> segments = Channel.CreateBounded<SpeechPiece>(new BoundedChannelOptions(2)
    {
        FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = false, AllowSynchronousContinuations = false
    });
    private readonly Channel<ConversationEvent> events;
    private readonly StringBuilder text = new();
    private readonly Guid? retryOf;
    private readonly bool earlierSpeech;
    private Task callbacks = Task.CompletedTask;
    private CancellationToken originalCaller;
    private CancellationTokenRegistration callerRegistration;
    private MonotonicWindow? textWindow, speechWindow;
    private SpeechSegmenter? segmentation;
    private PlaybackRun? playback;
    private GeneratedSpeechObservation? speechObservation;
    private PlaybackSnapshot? lastPlayback;
    private PlaybackSnapshot? observedPlayback;
    private ConversationState state = ConversationState.Authorizing;
    private ConversationFailure failure;
    private ProviderFailureCode? providerFailure;
    private SequenceIssueInfo? sequenceFailure;
    private EvidenceProvenance? textProvenance, speechProvenance;
    private Guid? speechRequest;
    private string? refusal;
    private bool invalidated, userStopped, workFinished, released, quarantined, textComplete, refused, terminal;
    private int peakQueued, committed, suppressed, reservedBytes;
    private long reservedSamples, accepted, submitted, consumed, eventSequence, dropped;
    private bool mayHavePlayed;

    public Guid SessionId => Owner.SessionId;
    public Guid TurnId { get; } = Guid.NewGuid();
    public long Epoch { get; }
    public CorrelationIds TextIds { get; }
    public Task<ConversationSnapshot> Completion => completion.Task;
    public Task OwnershipRelease => release.Task;
    public ChannelReader<ConversationEvent> Events => events.Reader;
    public ConversationSnapshot Snapshot { get { lock (Sync) return GetSnapshot(); } }
    public ConversationContent Content { get { lock (Sync) return new(text.ToString(), refusal); } }

    internal ConversationTurn(ConversationRuntime owner, ConversationRequest request,
        IConversationAuthorizationSource authorization, long epoch, Guid? retryOf, bool earlierSpeech)
    {
        Owner = owner;
        this.request = request;
        this.authorization = authorization;
        Epoch = epoch;
        this.retryOf = retryOf;
        this.earlierSpeech = earlierSpeech;
        whole = new(Clock, request.Limits.TurnTimeout);
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
        await Task.WhenAll(GuardStageAsync(GenerateAsync), GuardStageAsync(SpeakAsync)).ConfigureAwait(false);
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

    private void Fail(ConversationFailure reason, ProviderFailureCode? provider = null, SequenceIssue? sequence = null)
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
            sequenceFailure = sequence is { } issue ? new(issue) : null;
            Invalidate();
        }
    }

    private async Task GenerateAsync()
    {
        var window = new MonotonicWindow(Clock, request.TextLimits.MaxRequestTime);
        lock (Sync) textWindow = window;
        try
        {
            Check(window);
            var context = new ProviderRequestContext { Ids = TextIds, Epoch = Epoch, Deadline = Deadline(window) };
            var budget = new OperationBudget(TextIds, Epoch, ProviderRole.Llm, 1, request.Input.Utf8Bytes,
                request.Input.InputTokenReservation, request.TextLimits.MaxOutputTokens, 0);
            var action = new TextAuthorizationAction(context, request.Input, request.Model, request.TextLimits, budget);
            var permission = await authorization.AuthorizeTextAsync(action, stop.Token).ConfigureAwait(false);
            Check(window);
            if (permission?.Authorization is not { } consent)
                throw new ConversationException(ConversationFailure.AuthorizationUnavailable);
            window = ValidateReservation(permission.Reservation, budget, consent.ExpiresAt, context.Deadline, window);
            lock (Sync) textWindow = window;
            Check(window);
            // Clamp to remaining ORIGINAL stage/turn budgets after a potentially slow authorization callback.
            context = context with { Deadline = Deadline(window) };
            var stream = Owner.StreamText(context, request, consent, originalCaller);
            using var validator = new ProviderSequenceValidator(new()
            {
                Ids = TextIds, Epoch = Epoch, Capabilities = stream.Capabilities
            }, new()
            {
                MaxIngressEvents = request.TextLimits.MaxEvents + 2,
                MaxTextCharacters = request.TextLimits.MaxTextCharacters, MaxQueuedChunks = 1,
                FirstEventTimeout = request.TextLimits.FirstDeltaTimeout, IdleTimeout = request.TextLimits.IdleTimeout,
                TotalTimeout = request.TextLimits.MaxRequestTime
            }, Clock, stop.Token);
            var segmenter = request.Speech is { } voice
                ? new SpeechSegmenter(voice.Limits.MaxInputBytes, request.TextLimits.MaxTextCharacters) : null;
            lock (Sync)
            {
                CheckActive();
                segmentation = segmenter;
                textProvenance = stream.Capabilities.Provenance;
                SetState(ConversationState.Generating);
            }
            await using var enumeration = stream.GetAsyncEnumerator(stop.Token);
            while (true)
            {
                Check(window);
                bool moved = await enumeration.MoveNextAsync().ConfigureAwait(false);
                Check(window);
                if (!moved) break;
                var item = enumeration.Current;
                var update = validator.Accept(item);
                if (update.Snapshot.Result?.Outcome == TurnOutcome.Failed)
                {
                    Fail(ConversationFailure.ProviderFailed, stream.Result?.Failure?.Code, update.Snapshot.Issue);
                    return;
                }
                while (validator.TryReadText(out var chunk))
                {
                    Check(window);
                    lock (Sync)
                    {
                        CheckActive();
                        text.Append(chunk!.Text);
                        Emit(ConversationEventKind.Text, chunk.Text);
                    }
                    if (segmenter is not null) await StageAsync(segmenter.Push(chunk!.Text), window).ConfigureAwait(false);
                }
            }
            Check(window);
            var end = validator.EndOfInput().Snapshot;
            if (end.Result?.Outcome == TurnOutcome.Completed)
            {
                lock (Sync)
                {
                    CheckActive();
                    textComplete = true;
                }
                if (segmenter is not null) await StageAsync(segmenter.Finish(), window).ConfigureAwait(false);
            }
            else if (end.Result?.Outcome == TurnOutcome.Refused)
            {
                lock (Sync)
                {
                    CheckActive();
                    refused = true;
                    refusal = validator.RefusalText;
                }
            }
            else
            {
                Fail(ConversationFailure.InvalidStream, stream.Result?.Failure?.Code, end.Issue);
            }
        }
        finally
        {
            lock (Sync)
            {
                segmentation?.Clear();
                segmentation = null;
                textWindow = null;
            }
            segments.Writer.TryComplete();
        }
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
                if (iterator.Current.Text is null)
                {
                    suppressed++;
                    Emit(ConversationEventKind.SpeechSuppressed);
                }
                else
                {
                    if (!segments.Writer.TryWrite(iterator.Current))
                        throw new ConversationException(ConversationFailure.InvalidStream);
                    peakQueued = Math.Max(peakQueued, segments.Reader.Count);
                    Emit(ConversationEventKind.SegmentQueued);
                }
            }
        }
        Check(window);
    }

    private async Task SpeakAsync()
    {
        if (request.Speech is not { } voice) return;
        await foreach (var piece in segments.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
        {
            var window = new MonotonicWindow(Clock, voice.Limits.MaxRequestTime);
            lock (Sync) speechWindow = window;
            try { await SpeakSegmentAsync(piece.Text!, voice, window).ConfigureAwait(false); }
            finally { lock (Sync) speechWindow = null; }
        }
    }

    private async Task SpeakSegmentAsync(string segment, SpeechOutput voice, MonotonicWindow window)
    {
        Check(window);
        var input = new BoundedSpeechInput(segment);
        var ids = NewIds();
        int number;
        lock (Sync)
        {
            CheckActive();
            if (committed >= request.Limits.MaxSpeechSegments ||
                input.Utf8Bytes > request.Limits.MaxSpeechTextBytes - reservedBytes ||
                voice.Limits.MaxSamples > request.Limits.MaxReservedSpeechSamples - reservedSamples)
                throw new ConversationException(ConversationFailure.LimitExceeded);
            number = ++committed;
            reservedBytes += input.Utf8Bytes;
            reservedSamples += voice.Limits.MaxSamples;
            speechRequest = ids.RequestId;
            SetState(ConversationState.Authorizing);
            Emit(ConversationEventKind.SegmentStarted);
        }
        var context = new ProviderRequestContext { Ids = ids, Epoch = Epoch, Deadline = Deadline(window) };
        var budget = new OperationBudget(ids, Epoch, ProviderRole.Tts, 1, input.Utf8Bytes, 0, 0, voice.Limits.MaxSamples);
        var action = new SpeechAuthorizationAction(context, number, input, voice.Selection, voice.Limits, budget);
        var permission = await authorization.AuthorizeSpeechAsync(action, stop.Token).ConfigureAwait(false);
        Check(window);
        if (permission?.Authorization is not { } consent)
            throw new ConversationException(ConversationFailure.AuthorizationUnavailable);
        window = ValidateReservation(permission.Reservation, budget, consent.ExpiresAt, context.Deadline, window);
        lock (Sync) speechWindow = window;
        Check(window);
        context = context with { Deadline = Deadline(window) };
        var stream = Owner.Speech!.Stream(context, voice.Selection, input, voice.Limits, consent, originalCaller);
        PlaybackRun? run = null;
        lock (Sync)
        {
            CheckActive();
            speechProvenance = stream.Capabilities.Provenance;
            SetState(ConversationState.Synthesizing);
        }
        try
        {
            await using var enumeration = stream.GetAsyncEnumerator(stop.Token);
            while (true)
            {
                Check(window);
                bool moved = await enumeration.MoveNextAsync().ConfigureAwait(false);
                Check(window);
                if (!moved) break;
                var frame = enumeration.Current;
                if (run is null)
                {
                    lock (Sync)
                    {
                        CheckActive();
                        run = Owner.StartPlayback(this, ids, voice.Output, Deadline(window), stop.Token);
                        playback = run;
                        speechObservation = Owner.GeneratedSpeech?.Begin(run, frame.Format);
                    }
                }
                await SubmitAsync(run, frame, window).ConfigureAwait(false);
            }
            Check(window);
            if (stream.Result is not { Outcome: SpeechSynthesisOutcome.Completed, FinalSampleCount: { } samples })
            {
                Fail(ConversationFailure.ProviderFailed, stream.Result?.Failure?.Code);
                return;
            }
            if (run is null || !run.CompleteInput(samples))
                throw new ConversationException(ConversationFailure.PlaybackFailed);
            speechObservation?.CompleteInput(samples);
            var terminalPlayback = await run.Completion.WaitAsync(stop.Token).ConfigureAwait(false);
            Check(window);
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
                    Emit(ConversationEventKind.Playback);
                }
                await run.DeviceRelease.ConfigureAwait(false);
                lock (Sync)
                {
                    var final = run.Snapshot;
                    accepted += final.AcceptedSamples;
                    submitted += final.SubmittedSamples;
                    consumed += final.DeviceConsumedSamples;
                    mayHavePlayed |= final.MayHavePlayed;
                    quarantined |= !final.DeviceReleased || final.Error?.Code == ErrorCode.AudioPlaybackFailed;
                    lastPlayback = final;
                    playback = null;
                    speechObservation = null;
                    speechRequest = null;
                    if (!invalidated && !textComplete) SetState(ConversationState.Generating);
                }
            }
        }
    }

    private async Task SubmitAsync(PlaybackRun run, PcmFrame frame, MonotonicWindow window)
    {
        while (true)
        {
            Check(window);
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
            await Task.Delay(TimeSpan.FromMilliseconds(10), Clock, stop.Token).ConfigureAwait(false);
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
            pendingCallbacks = callbacks;
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
                    if (!invalidated && progress.State == PlaybackState.Playing) SetState(ConversationState.Playing);
                    Emit(ConversationEventKind.Playback);
                }
                if (whole.Expired) Fail(ConversationFailure.DeadlineExceeded);
                else if (textWindow?.Expired == true) Fail(textWindow.ExpiryFailure);
                else if (speechWindow?.Expired == true) Fail(speechWindow.ExpiryFailure);
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
            Interlocked.Read(ref dropped), currentPlayback ?? lastPlayback, retryOf, earlierSpeech);
    }
}
