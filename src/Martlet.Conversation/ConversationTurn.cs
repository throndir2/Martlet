using System.Runtime.ExceptionServices;
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
    private MonotonicWindow? textWindow, speechWindow, playWindow;
    private readonly long startedAt;
    private TimeSpan? firstTextAfter, firstAudioAfter;
    private bool synthesizing;
    private SpeechSegmenter? segmentation;
    private PlaybackRun? playback;
    private GeneratedSpeechObservation? speechObservation;
    private PlaybackSnapshot? lastPlayback;
    private PlaybackSnapshot? observedPlayback;
    private ConversationState state = ConversationState.Authorizing;
    private ConversationFailure failure;
    private ProviderFailureCode? providerFailure;
    private ProviderRole? failedProvider;
    private SequenceIssueInfo? sequenceFailure;
    private EvidenceProvenance? textProvenance, speechProvenance;
    private Guid? speechRequest;
    private string? refusal;
    private bool invalidated, userStopped, workFinished, released, quarantined, textComplete, refused, terminal, toolsRejected,
        audioRejected;
    private bool speechLimitReached;
    private int peakQueued, committed, suppressed, reservedBytes, toolCalls;
    private string? activeTool, fellBackAfter;
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

    // One reply may take several requests: each tool round sends the calls and their results back to the model, until it
    // answers in text. Every request is separately authorized; text from every round is shown and spoken in order.
    private async Task GenerateAsync()
    {
        var segmenter = request.Speech is { } voice
            ? new SpeechSegmenter(voice.Limits.MaxInputBytes, request.TextLimits.MaxTextCharacters, request.SilentReply,
                eagerFirstClause: true) : null;
        try
        {
            var input = request.Input;
            var rounds = new List<TextToolRound>();
            bool retried = false, fallback = false, audioDropped = false;
            for (var attempt = 0; ; attempt++)
            {
                int before;
                lock (Sync) before = text.Length;
                RoundResult result;
                try
                {
                    result = await RequestAsync(fallback || audioDropped ? input.WithoutAudio() : input,
                        attempt == 0 ? TextIds : NewIds(), segmenter, fallback).ConfigureAwait(false);
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
                            // Rejected again without the recording: the tools were the problem, not the audio.
                            if (audioDropped) audioRejected = false;
                        }
                        input = request.Input.WithoutTools();
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
                            text.Append('\n');
                            Emit(ConversationEventKind.Text, "\n");
                        }
                        if (segmenter is not null) await StageAsync(segmenter.Push("\n"), whole).ConfigureAwait(false);
                    }
                    var results = await CallToolsAsync(tools, result.Calls, rounds).ConfigureAwait(false);
                    rounds.Add(new(result.Text, result.Calls, results));
                    input = request.Input.WithToolRounds(rounds, callsAllowed: rounds.Count < request.Limits.MaxToolRounds);
                    continue;
                }
                // Calls the model makes when no more are allowed are ignored; it still has to have answered in text.
                if (string.IsNullOrWhiteSpace(result.Text))
                {
                    Fail(ConversationFailure.InvalidStream, null, SequenceIssue.EmptyCompletion);
                    return;
                }
                lock (Sync)
                {
                    CheckActive();
                    textComplete = true;
                }
                if (segmenter is not null) await StageAsync(segmenter.Finish(), whole).ConfigureAwait(false);
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
        bool fallback = false)
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
        var stream = Owner.StreamText(context, request, input, consent, originalCaller, fallback);
        using var validator = new ProviderSequenceValidator(new()
        {
            Ids = ids, Epoch = Epoch, Capabilities = stream.Capabilities
        }, new()
        {
            MaxIngressEvents = request.TextLimits.MaxEvents + 2,
            MaxTextCharacters = request.TextLimits.MaxTextCharacters, MaxQueuedChunks = 1,
            FirstEventTimeout = request.TextLimits.FirstDeltaTimeout, IdleTimeout = request.TextLimits.IdleTimeout,
            TotalTimeout = request.TextLimits.MaxRequestTime, AllowEmptyCompletion = input.Tools.Count > 0
        }, Clock, stop.Token);
        lock (Sync)
        {
            CheckActive();
            segmentation = segmenter;
            textProvenance = stream.Capabilities.Provenance;
            SetState(ConversationState.Generating);
        }
        var said = new StringBuilder();
        await using (var enumeration = stream.GetAsyncEnumerator(stop.Token))
        {
            while (true)
            {
                Check(window);
                bool moved = await enumeration.MoveNextAsync().ConfigureAwait(false);
                Check(window);
                if (!moved) break;
                var update = validator.Accept(enumeration.Current);
                if (update.Snapshot.Result?.Outcome == TurnOutcome.Failed)
                    return new(RoundEnd.Failed, said.ToString(), [], stream.Result?.Failure?.Code, update.Snapshot.Issue);
                while (validator.TryReadText(out var chunk))
                {
                    Check(window);
                    lock (Sync)
                    {
                        CheckActive();
                        firstTextAfter ??= Clock.GetElapsedTime(startedAt);
                        text.Append(chunk!.Text);
                        Emit(ConversationEventKind.Text, chunk.Text);
                    }
                    said.Append(chunk.Text);
                    if (segmenter is not null) await StageAsync(segmenter.Push(chunk.Text), window).ConfigureAwait(false);
                }
            }
        }
        Check(window);
        var end = validator.EndOfInput().Snapshot;
        lock (Sync) textWindow = null;
        return end.Result?.Outcome switch
        {
            TurnOutcome.Completed => new(RoundEnd.Completed, said.ToString(), stream.Result?.ToolCalls ?? []),
            TurnOutcome.Refused => new(RoundEnd.Refused, said.ToString(), [], Refusal: validator.RefusalText),
            _ => new(RoundEnd.Invalid, said.ToString(), [], stream.Result?.Failure?.Code, end.Issue)
        };
    }

    // Tool calls run one at a time; each result is cut to its share of what is left of the reply's tool budget.
    private async Task<IReadOnlyList<TextToolResult>> CallToolsAsync(IConversationToolHost tools, IReadOnlyList<TextToolCall> calls,
        IReadOnlyList<TextToolRound> earlier)
    {
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
    }

    // Synthesis runs one sentence ahead of playback: while sentence N plays, sentence N+1 is already being synthesized, so
    // there is no synthesis gap between sentences. A synthesis failure is raised only when playback reaches that sentence,
    // so what is already playing finishes first, as it did before.
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
            await foreach (var piece in segments.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                // Once the speech budget is spent (or a sentence failed) the rest of the reply is text only; keep draining so
                // the text still finishes.
                if (speechLimitReached || broken) continue;
                if (!await ready.WaitToWriteAsync(stop.Token).ConfigureAwait(false)) return;
                if (Reserve(piece.Text!, voice) is not { } take) continue;
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
        finally { ready.TryComplete(); }
    }

    private SpeechTake? Reserve(string segment, SpeechOutput voice)
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
            var take = new SpeechTake(segment, ++committed, NewIds());
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
            Check(window);
            var input = new BoundedSpeechInput(take.Text);
            var context = new ProviderRequestContext { Ids = take.Ids, Epoch = Epoch, Deadline = Deadline(window) };
            var budget = new OperationBudget(take.Ids, Epoch, ProviderRole.Tts, 1, input.Utf8Bytes, 0, 0, voice.Limits.MaxSamples);
            var action = new SpeechAuthorizationAction(context, take.Number, input, voice.Selection, voice.Limits, budget);
            var permission = await authorization.AuthorizeSpeechAsync(action, stop.Token).ConfigureAwait(false);
            Check(window);
            if (permission?.Authorization is not { } consent)
                throw new ConversationException(ConversationFailure.AuthorizationUnavailable);
            window = ValidateReservation(permission.Reservation, budget, consent.ExpiresAt, context.Deadline, window);
            lock (Sync) speechWindow = window;
            Check(window);
            context = context with { Deadline = Deadline(window) };
            var stream = Owner.StreamSpeech(context, request, input, consent, originalCaller);
            lock (Sync)
            {
                CheckActive();
                speechProvenance = stream.Capabilities.Provenance;
                if (playback is null) SetState(ConversationState.Synthesizing);
            }
            await using (var enumeration = stream.GetAsyncEnumerator(stop.Token))
            {
                while (true)
                {
                    Check(window);
                    bool moved = await enumeration.MoveNextAsync().ConfigureAwait(false);
                    Check(window);
                    if (!moved) break;
                    if (!take.Frames.Writer.TryWrite(enumeration.Current))
                        throw new ConversationException(ConversationFailure.InvalidStream);
                }
            }
            Check(window);
            if (stream.Result is not { Outcome: SpeechSynthesisOutcome.Completed, FinalSampleCount: { } samples })
            {
                take.ProviderFailed = true;
                take.Failure = stream.Result?.Failure?.Code;
                return false;
            }
            take.FinalSamples = samples;
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
        await foreach (var take in ready.ReadAllAsync(stop.Token).ConfigureAwait(false))
        {
            var window = new MonotonicWindow(Clock, voice.Limits.MaxRequestTime);
            lock (Sync) playWindow = window;
            try
            {
                if (!await PlayAsync(take, voice, window).ConfigureAwait(false)) return;
            }
            finally { lock (Sync) playWindow = null; }
        }
    }

    private async Task<bool> PlayAsync(SpeechTake take, SpeechOutput voice, MonotonicWindow window)
    {
        PlaybackRun? run = null;
        try
        {
            await foreach (var frame in take.Frames.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                Check(window);
                if (run is null)
                {
                    lock (Sync)
                    {
                        CheckActive();
                        run = Owner.StartPlayback(this, take.Ids, voice.Output, Deadline(window), stop.Token);
                        playback = run;
                        speechRequest = take.Ids.RequestId;
                        speechObservation = Owner.GeneratedSpeech?.Begin(run, frame.Format);
                        Owner.SpokenText?.Post(take.Text, run.Completion);
                    }
                }
                await SubmitAsync(run, frame, window).ConfigureAwait(false);
            }
            Check(window);
            take.Error?.Throw();
            if (take.ProviderFailed)
            {
                Fail(ConversationFailure.ProviderFailed, take.Failure, failedRole: ProviderRole.Tts);
                return false;
            }
            if (run is null || take.FinalSamples is not { } samples || !run.CompleteInput(samples))
                throw new ConversationException(ConversationFailure.PlaybackFailed);
            speechObservation?.CompleteInput(samples);
            var terminalPlayback = await run.Completion.WaitAsync(stop.Token).ConfigureAwait(false);
            Check(window);
            if (terminalPlayback.State != PlaybackState.Completed)
                throw new ConversationException(ConversationFailure.PlaybackFailed);
            return true;
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
                    if (!invalidated && !textComplete) SetState(synthesizing ? ConversationState.Synthesizing : ConversationState.Generating);
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
                    if (progress.MayHavePlayed || progress.State == PlaybackState.Playing)
                        firstAudioAfter ??= Clock.GetElapsedTime(startedAt);
                    if (!invalidated && progress.State == PlaybackState.Playing) SetState(ConversationState.Playing);
                    Emit(ConversationEventKind.Playback);
                }
                if (whole.Expired) Fail(ConversationFailure.DeadlineExceeded);
                else if (textWindow?.Expired == true) Fail(textWindow.ExpiryFailure);
                else if (speechWindow?.Expired == true) Fail(speechWindow.ExpiryFailure);
                else if (playWindow?.Expired == true) Fail(playWindow.ExpiryFailure);
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
            Interlocked.Read(ref dropped), currentPlayback ?? lastPlayback, retryOf, earlierSpeech, toolCalls, activeTool, toolsRejected,
            speechLimitReached, failedProvider, fellBackAfter, audioRejected, firstTextAfter, firstAudioAfter);
    }
}
