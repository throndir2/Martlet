using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Streaming;
using Martlet.Providers;

namespace Martlet.Conversation;

// Generation: the reply's Thinking requests, one per tool round, their timings and the tools they call.
public sealed partial class ConversationTurn
{
    private enum RoundEnd { Completed, Refused, Failed, Invalid }

    private sealed record RoundResult(RoundEnd End, string Text, IReadOnlyList<TextToolCall> Calls,
        ProviderFailureCode? Failure = null, SequenceIssue? Issue = null, string? Refusal = null, TimeSpan? RetryAfter = null,
        string? PromptText = null);

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
            // Why the next request is sent, for the Thinking trace.
            var why = "first";
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
                    if (fallback) await HeldAsync().ConfigureAwait(false);
                    result = await RequestAsync(imageDropped ? sent.WithoutImage() : sent,
                        attempt == 0 ? TextIds : NewIds(), segmenter, fallback, reasoningDropped, hedged: attempt == 0, why: why).ConfigureAwait(false);
                }
                // The selected destination failed without answering (no reply in time or a broken stream): ask the fallback.
                catch (Exception error) when (error is ConversationException { Failure: ConversationFailure.DeadlineExceeded or
                    ConversationFailure.InvalidStream or ConversationFailure.DependencyFailed } or ContractException &&
                    !fallback && FallBack(before, error is ConversationException failed ? failed.Failure.ToString() : nameof(ConversationFailure.InvalidStream)))
                {
                    fallback = true;
                    why = $"the Thinking fallback, after {fellBackAfter}";
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
                        why = "again without the recording, which the model refused";
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
                        why = "again without the picture, which the model refused";
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
                        why = "again without tools, which the model refused";
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
                        why = "again without the Thinking steps choice, which the model refused";
                        continue;
                    }
                    // Nothing of this answer arrived yet, so the fallback can give it instead.
                    if (!fallback && result.Text.Length == 0 &&
                        FallBack(before, result.Failure?.ToString() ?? result.Issue?.ToString() ?? result.End.ToString()))
                    {
                        fallback = true;
                        why = $"the Thinking fallback, after {fellBackAfter}";
                        continue;
                    }
                    Fail(result.End == RoundEnd.Failed ? ConversationFailure.ProviderFailed : ConversationFailure.InvalidStream,
                        result.Failure, result.Issue, ProviderRole.Llm, result.RetryAfter);
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
                    var results = await CallToolsAsync(tools, result.Calls, rounds, input, result.Text).ConfigureAwait(false);
                    rounds.Add(new(result.Text, result.Calls, results));
                    input = request.Input.WithToolRounds(rounds, callsAllowed: rounds.Count < request.Limits.MaxToolRounds);
                    why = $"after tool round {rounds.Count}";
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

    // One request of the reply, with its line in the Thinking trace once it ended (why: what led to it).
    private async Task<RoundResult> RequestAsync(BoundedTextInput input, CorrelationIds ids, SpeechSegmenter? segmenter,
        bool fallback = false, bool withoutReasoning = false, bool hedged = false, string why = "first")
    {
        var traced = TraceRequest(why);
        if (traced is null) return await RequestCoreAsync(input, ids, segmenter, fallback, withoutReasoning, hedged, why).ConfigureAwait(false);
        RoundResult result;
        try { result = await RequestCoreAsync(input, ids, segmenter, fallback, withoutReasoning, hedged, why).ConfigureAwait(false); }
        catch (Exception error)
        {
            TraceRequestEnded(traced, null, error);
            throw;
        }
        TraceRequestEnded(traced, result, null);
        return result;
    }

    private async Task<RoundResult> RequestCoreAsync(BoundedTextInput input, CorrelationIds ids, SpeechSegmenter? segmenter,
        bool fallback, bool withoutReasoning, bool hedged, string why)
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
        lock (Sync)
        {
            textRequestAfter ??= requestedAfter;
            TraceAuthorized(requestedAfter);
            // What goes to the model now, as it goes (a reference, so the reply pays nothing for it).
            if (sentRequests.Count < ConversationSentRequest.MaximumKept)
                sentRequests.Add(new(input, model, fallback, withoutReasoning, why, Clock.GetUtcNow()));
        }
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
                TraceStream(run.Stream, requestedAfter);
                SetState(ConversationState.Generating);
            }
            if (backup is not null) run = await RaceAsync(run, backup, input).ConfigureAwait(false);
            var said = new StringBuilder();
            var guard = request.StopAtPromptText ? new PromptTextGuard() : null;
            // A piece of the reply's words: shown, captioned and handed to the voice.
            async Task TakeAsync(string piece)
            {
                if (piece.Length == 0) return;
                lock (Sync)
                {
                    CheckActive();
                    // The chat and the saved conversation show words only; the voice gets its tags from the segmenter.
                    if (shown.Push(piece) is { Length: > 0 } visible)
                    {
                        text.Append(visible);
                        Emit(ConversationEventKind.Text, visible);
                    }
                }
                said.Append(piece);
                Caption(captions => captions.Push(piece));
                if (segmenter is not null)
                {
                    TraceStaging(true);
                    try { await StageAsync(segmenter.Push(piece), window).ConfigureAwait(false); }
                    finally { TraceStaging(false); }
                }
            }
            // What the race already read from the stream that answers.
            var step = run.Step;
            while (guard?.Cut is null)
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
                        return new(RoundEnd.Failed, said.ToString(), [], run.Stream.Result?.Failure?.Code, update.Snapshot.Issue,
                            RetryAfter: run.Stream.Result?.Failure?.RetryAfter);
                }
                else if (step == RunStep.Ended) break;
                else if (step == RunStep.Failed)
                    return new(RoundEnd.Failed, said.ToString(), [], run.Stream.Result?.Failure?.Code, run.Validator.Snapshot.Issue,
                        RetryAfter: run.Stream.Result?.Failure?.RetryAfter);
                step = RunStep.None;
                while (run.Validator.TryReadText(out var chunk))
                {
                    Check(window);
                    lock (Sync)
                    {
                        CheckActive();
                        firstTextAfter ??= Clock.GetElapsedTime(startedAt);
                        TraceWords(chunk!.Text.Length);
                    }
                    await TakeAsync(guard is null ? chunk!.Text : guard.Push(chunk!.Text)).ConfigureAwait(false);
                    if (guard?.Cut is not null) break;
                }
            }
            Check(window);
            // The model began to write prompt text: the reply is what came before it, and the rest of the stream is let go
            // (disposing the run stops it), so nothing more is read, shown or spoken.
            if (guard?.Cut is { } marker)
            {
                lock (Sync)
                {
                    textWindow = null;
                    promptTextCut = marker;
                }
                return new(RoundEnd.Completed, said.ToString(), [], PromptText: marker);
            }
            if (guard is not null) await TakeAsync(guard.Finish()).ConfigureAwait(false);
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

    // Tool calls run one at a time; each result is cut to its share of what is left of the reply's tool budget: the tool exchange
    // bytes and the request's input tokens (three bytes a token, as the estimate counts), less the calls, what the model wrote
    // before them (said) and room for their reservations. A cut result tells the model so, and the Thinking trace notes it. A
    // reply started early calls no tool until it is released: a tool may act on the world.
    private async Task<IReadOnlyList<TextToolResult>> CallToolsAsync(IConversationToolHost tools, IReadOnlyList<TextToolCall> calls,
        IReadOnlyList<TextToolRound> earlier, BoundedTextInput input, string said)
    {
        await HeldAsync().ConfigureAwait(false);
        var tokenRoom = (long)request.TextLimits.MaxInputTokens - input.InputTokenReservation - 64L * calls.Count - 64;
        var available = (int)Math.Min(BoundedTextInput.RemainingToolExchangeBytes(earlier), Math.Max(0, tokenRoom * 3)) -
            calls.Sum(c => c.Utf8Bytes) - System.Text.Encoding.UTF8.GetByteCount(said) - 4096;
        var share = Math.Max(256, available / calls.Count);
        var results = new List<TextToolResult>(calls.Count);
        // Each call, how long it took and whether its result was cut, for the Thinking trace.
        var began = Clock.GetElapsedTime(startedAt);
        var took = trace is null ? null : new List<(string Name, TimeSpan Took, bool Failed, bool Cut)>(calls.Count);
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
            TraceWaiting($"tool {call.Name}");
            var called = Clock.GetTimestamp();
            ConversationToolResult outcome;
            try { outcome = await tools.CallAsync(call, stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
            // A tool host boundary can fail arbitrarily; the model hears that the tool failed, never the exception text.
            catch (Exception) { outcome = new("The tool failed on this PC.", true); }
            finally { TraceWaiting(null); }
            lock (Sync) CheckActive();
            var bounded = TextToolResult.Bound((outcome.IsError ? "Error: " : "") + outcome.Output, share);
            took?.Add((call.Name, Clock.GetElapsedTime(called), outcome.IsError, TextToolResult.WasCut(bounded)));
            results.Add(new(call.CallId, bounded));
        }
        lock (Sync)
        {
            activeTool = null;
            Emit(ConversationEventKind.State);
        }
        if (took is not null) TraceTools(earlier.Count + 1, took, began);
        return results;
    }
}
