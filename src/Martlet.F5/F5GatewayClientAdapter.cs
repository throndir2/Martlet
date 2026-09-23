namespace Martlet.F5;

public sealed class F5GatewayClientAdapter
{
    private sealed record ActionContext(
        Guid ActionId,
        F5RequestIds Ids,
        string DestinationId,
        F5WorkerIdentity ExpectedWorker,
        string ReferenceRevision,
        DateTimeOffset DeadlineUtc,
        F5TextChunk[] Chunks);

    private sealed record CancelAttempt(F5CancelResponse? Response, bool Failed);

    private readonly F5ReferencePresetStore presets;
    private readonly IF5WorkerTransport transport;
    private readonly F5WorkerIdentity selectedWorker;
    private readonly string destinationId;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim actionGate = new(1, 1);
    private string? preparedReferenceRevision;

    public F5GatewayClientAdapter(
        F5ReferencePresetStore presets,
        IF5WorkerTransport transport,
        string destinationId,
        F5WorkerIdentity selectedWorker,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(presets);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(selectedWorker);
        F5Guard.Identifier(destinationId, 128);
        this.presets = presets;
        this.transport = transport;
        this.destinationId = destinationId;
        this.selectedWorker = selectedWorker;
        this.clock = clock ?? TimeProvider.System;
    }

    public Task<F5ReferenceApplyReceipt> ApplyReferenceAsync(
        F5ReferenceApplyPreview preview,
        F5ReferenceApplyAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        F5Guard.Require(preview.DestinationId == destinationId,
            F5Failure.AuthorizationMismatch);
        return presets.ApplyAsync(preview, authorization, cancellationToken);
    }

    public Task<F5SynthesisResult> SynthesizeAsync(
        F5ConversationSynthesis request,
        F5ConversationAuthorization authorization,
        IF5PcmFrameSink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(sink);
        var action = new ActionContext(
            request.ActionId,
            request.Ids,
            request.DestinationId,
            request.ExpectedWorker,
            request.ExpectedReferenceRevision,
            request.DeadlineUtc,
            request.Chunks.ToArray());
        return ExecuteAsync(
            action,
            authorization.ExpiresAtUtc,
            ReferenceEquals(authorization.Request, request),
            () => Interlocked.CompareExchange(ref authorization.Used, 1, 0) == 0,
            token => presets.AcquireAppliedAsync(request.ExpectedReferenceRevision, token),
            sink,
            cancellationToken);
    }

    public Task<F5SynthesisResult> PreviewAsync(
        F5PreviewSynthesis request,
        F5PreviewAuthorization authorization,
        IF5PcmFrameSink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(sink);
        var action = new ActionContext(
            request.ActionId,
            request.Ids,
            request.DestinationId,
            request.ExpectedWorker,
            request.ReferenceRevision,
            request.DeadlineUtc,
            request.Chunks.ToArray());
        return ExecuteAsync(
            action,
            authorization.ExpiresAtUtc,
            ReferenceEquals(authorization.Request, request),
            () => Interlocked.CompareExchange(ref authorization.Used, 1, 0) == 0,
            token => presets.AcquireForPreviewAsync(
                request.PresetId, request.ReferenceRevision, token),
            sink,
            cancellationToken);
    }

    private async Task<F5SynthesisResult> ExecuteAsync(
        ActionContext action,
        DateTimeOffset authorizationExpiry,
        bool authorizationMatches,
        Func<bool> consumeAuthorization,
        Func<CancellationToken, Task<F5ReferenceUseLease>> acquireReference,
        IF5PcmFrameSink sink,
        CancellationToken callerToken)
    {
        if (callerToken.IsCancellationRequested)
            return Result(action, F5SynthesisOutcome.Canceled, F5Failure.Canceled,
                localDiscarded: true);
        if (!actionGate.Wait(0))
            return Result(action, F5SynthesisOutcome.Failed, F5Failure.Busy);

        try
        {
            var now = CurrentUtc();
            try
            {
                ValidateAction(action, now);
                F5Guard.Require(authorizationMatches, F5Failure.AuthorizationMismatch);
                F5Guard.Require(authorizationExpiry > now &&
                    authorizationExpiry <= action.DeadlineUtc, F5Failure.AuthorizationExpired);
                F5Guard.Require(consumeAuthorization(), F5Failure.AuthorizationConsumed);
            }
            catch (F5Exception error)
            {
                return Result(action, FailureOutcome(error.Failure), error.Failure,
                    localDiscarded: error.Failure is F5Failure.Canceled or F5Failure.DeadlineExceeded);
            }

            var remaining = action.DeadlineUtc - CurrentUtc();
            if (remaining <= TimeSpan.Zero)
                return Result(action, F5SynthesisOutcome.DeadlineExceeded,
                    F5Failure.DeadlineExceeded, localDiscarded: true);
            using var deadline = new CancellationTokenSource(remaining, clock);
            using var actionLifetime = CancellationTokenSource.CreateLinkedTokenSource(
                callerToken, deadline.Token);

            F5ReferenceUseLease reference;
            try
            {
                reference = await acquireReference(actionLifetime.Token);
            }
            catch (F5Exception error)
            {
                return Result(action, FailureOutcome(error.Failure), error.Failure);
            }
            catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
            {
                return Result(action, F5SynthesisOutcome.Canceled, F5Failure.Canceled,
                    localDiscarded: true);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                return Result(action, F5SynthesisOutcome.DeadlineExceeded,
                    F5Failure.DeadlineExceeded, localDiscarded: true);
            }

            using (reference)
            {
                if (reference.DestinationId != action.DestinationId)
                    return Result(action, F5SynthesisOutcome.Failed,
                        F5Failure.AuthorizationMismatch);
                try
                {
                    await PrepareConditioningCacheAsync(
                        reference.ReferenceRevision, actionLifetime.Token);
                }
                catch (F5Exception error)
                {
                    return Result(action, FailureOutcome(error.Failure), error.Failure);
                }
                catch (F5TransportException)
                {
                    return Result(action, F5SynthesisOutcome.Failed,
                        F5Failure.CacheInvalidationFailed);
                }
                catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
                {
                    return Result(action, F5SynthesisOutcome.Canceled, F5Failure.Canceled,
                        localDiscarded: true);
                }
                catch (OperationCanceledException)
                {
                    return Result(action,
                        deadline.IsCancellationRequested
                            ? F5SynthesisOutcome.DeadlineExceeded
                            : F5SynthesisOutcome.Failed,
                        deadline.IsCancellationRequested
                            ? F5Failure.DeadlineExceeded
                            : F5Failure.CacheInvalidationFailed,
                        localDiscarded: deadline.IsCancellationRequested);
                }

                if (deadline.IsCancellationRequested)
                    return Result(action, F5SynthesisOutcome.DeadlineExceeded,
                        F5Failure.DeadlineExceeded, localDiscarded: true);
                if (callerToken.IsCancellationRequested)
                    return Result(action, F5SynthesisOutcome.Canceled,
                        F5Failure.Canceled, localDiscarded: true);
                var workerRequest = new F5SynthesisRequest(
                    action.ActionId,
                    action.Ids,
                    action.DestinationId,
                    action.ExpectedWorker,
                    action.DeadlineUtc,
                    reference.Reference,
                    action.Chunks);
                return await ConsumeStreamAsync(
                    action, workerRequest, sink, callerToken,
                    deadline.Token, actionLifetime.Token);
            }
        }
        finally
        {
            actionGate.Release();
        }
    }

    private async Task PrepareConditioningCacheAsync(
        string referenceRevision,
        CancellationToken cancellationToken)
    {
        if (preparedReferenceRevision == referenceRevision)
            return;
        var revisions = new[] { preparedReferenceRevision, referenceRevision }
            .Where(revision => revision is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        using var timeout = new CancellationTokenSource(F5WorkerProtocol.MaximumCancelDuration, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);
        var request = new F5CacheInvalidationRequest
        {
            RequestId = Guid.NewGuid(),
            DestinationId = destinationId,
            ExpectedWorker = selectedWorker,
            ReferenceRevisions = Array.AsReadOnly(revisions)
        };
        var response = await transport.InvalidateReferenceCacheAsync(request, linked.Token);
        if (response is null || response.Worker is null ||
            response.InvalidatedReferenceRevisions is null)
            throw new F5Exception(F5Failure.CacheInvalidationFailed);
        F5Guard.Require(response.RequestId == request.RequestId &&
            response.Worker.Matches(selectedWorker) &&
            response.InvalidatedReferenceRevisions.Order(StringComparer.Ordinal)
                .SequenceEqual(revisions, StringComparer.Ordinal),
            F5Failure.CacheInvalidationFailed);
        preparedReferenceRevision = referenceRevision;
    }

    private async Task<F5SynthesisResult> ConsumeStreamAsync(
        ActionContext action,
        F5SynthesisRequest request,
        IF5PcmFrameSink sink,
        CancellationToken callerToken,
        CancellationToken deadlineToken,
        CancellationToken actionToken)
    {
        using var localAbort = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            actionToken, localAbort.Token);
        using var monitorStop = new CancellationTokenSource();
        var monitor = MonitorCancellationAsync(request, linked.Token, monitorStop.Token);

        var expectedEventSequence = 0L;
        var expectedFrameSequence = 0L;
        var expectedSampleOffset = 0L;
        var completedChunks = 0;
        var eventCount = 0;
        var deliveredFrames = 0;
        var deliveredSamples = 0L;
        var discardedFrames = 0;
        var outputFailed = false;
        F5WorkerEvent? terminal = null;
        F5Failure? protocolFailure = null;

        try
        {
            await foreach (var item in RequireStream(
                transport.StreamAsync(request, linked.Token)))
            {
                eventCount = checked(eventCount + 1);
                if (eventCount > F5WorkerProtocol.MaximumEvents)
                {
                    protocolFailure = F5Failure.LimitExceeded;
                    localAbort.Cancel();
                    break;
                }

                try
                {
                    ValidateEnvelope(item, action, expectedEventSequence);
                    expectedEventSequence++;
                    if (item.Kind == F5WorkerEventKind.Started)
                    {
                        F5Guard.Require(eventCount == 1, F5Failure.ProtocolViolation);
                        continue;
                    }
                    F5Guard.Require(eventCount > 1, F5Failure.ProtocolViolation);

                    if (item.Kind == F5WorkerEventKind.AudioFrame)
                    {
                        var frame = item.Frame!;
                        F5Guard.Require(frame.Sequence == expectedFrameSequence &&
                            frame.SampleOffset == expectedSampleOffset &&
                            frame.ChunkIndex == completedChunks &&
                            frame.ChunkIndex < action.Chunks.Length,
                            F5Failure.InvalidFrame);
                        expectedFrameSequence++;
                        expectedSampleOffset = checked(expectedSampleOffset + frame.SampleCount);
                        F5Guard.Require(expectedSampleOffset <= F5WorkerProtocol.MaximumSamples,
                            F5Failure.LimitExceeded);
                        if (linked.IsCancellationRequested || outputFailed)
                        {
                            discardedFrames++;
                            continue;
                        }
                        try
                        {
                            await sink.WriteAsync(frame, linked.Token);
                            deliveredFrames++;
                            deliveredSamples = checked(deliveredSamples + frame.SampleCount);
                        }
                        catch (F5PcmSinkException)
                        {
                            outputFailed = true;
                            localAbort.Cancel();
                        }
                        continue;
                    }

                    if (item.Kind == F5WorkerEventKind.ChunkCompleted)
                    {
                        F5Guard.Require(item.ChunkIndex == completedChunks &&
                            item.FinalSampleCount == expectedSampleOffset,
                            F5Failure.ProtocolViolation);
                        completedChunks++;
                        continue;
                    }

                    terminal = item;
                    if (item.Kind is F5WorkerEventKind.Completed or F5WorkerEventKind.Canceled)
                        F5Guard.Require(item.FinalSampleCount == expectedSampleOffset,
                            F5Failure.ProtocolViolation);
                    if (item.Kind == F5WorkerEventKind.Completed)
                        F5Guard.Require(completedChunks == action.Chunks.Length,
                            F5Failure.ProtocolViolation);
                    break;
                }
                catch (F5Exception error)
                {
                    protocolFailure = error.Failure;
                    localAbort.Cancel();
                    break;
                }
            }
            if (terminal is null && protocolFailure is null && !linked.IsCancellationRequested)
            {
                protocolFailure = F5Failure.StreamTruncated;
                localAbort.Cancel();
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (F5TransportException)
        {
            protocolFailure = F5Failure.WorkerFailed;
            localAbort.Cancel();
        }
        catch (F5Exception error)
        {
            protocolFailure = error.Failure;
            localAbort.Cancel();
        }
        finally
        {
            monitorStop.Cancel();
        }

        var cancel = await monitor;
        var localCancellation = callerToken.IsCancellationRequested ||
            deadlineToken.IsCancellationRequested || localAbort.IsCancellationRequested;
        var computeCancellation = cancel?.Response?.ComputeCancellation;
        var workerMayContinue = cancel?.Response?.WorkerMayContinue ?? localCancellation;
        if (cancel?.Failed == true && protocolFailure is null && !outputFailed)
            protocolFailure = F5Failure.WorkerFailed;

        if (callerToken.IsCancellationRequested)
            return Result(action, F5SynthesisOutcome.Canceled, F5Failure.Canceled,
                deliveredSamples, deliveredFrames, discardedFrames, true,
                computeCancellation, workerMayContinue);
        if (deadlineToken.IsCancellationRequested)
            return Result(action, F5SynthesisOutcome.DeadlineExceeded, F5Failure.DeadlineExceeded,
                deliveredSamples, deliveredFrames, discardedFrames, true,
                computeCancellation, workerMayContinue);
        if (outputFailed)
            return Result(action, F5SynthesisOutcome.Failed, F5Failure.OutputFailed,
                deliveredSamples, deliveredFrames, discardedFrames, true,
                computeCancellation, workerMayContinue);
        if (protocolFailure is not null)
            return Result(action, F5SynthesisOutcome.Failed, protocolFailure,
                deliveredSamples, deliveredFrames, discardedFrames, localCancellation,
                computeCancellation, workerMayContinue);
        if (terminal is null)
            return Result(action, F5SynthesisOutcome.Failed, F5Failure.StreamTruncated,
                deliveredSamples, deliveredFrames, discardedFrames);
        if (terminal.Kind == F5WorkerEventKind.Failed)
        {
            var deadlineFailure = terminal.Error!.Code == F5WorkerErrorCode.DeadlineExceeded;
            return Result(action,
                deadlineFailure ? F5SynthesisOutcome.DeadlineExceeded : F5SynthesisOutcome.Failed,
                deadlineFailure ? F5Failure.DeadlineExceeded : F5Failure.WorkerFailed,
                deliveredSamples, deliveredFrames, discardedFrames,
                workerError: terminal.Error);
        }
        if (terminal.Kind == F5WorkerEventKind.Canceled)
            return Result(action, F5SynthesisOutcome.Canceled, F5Failure.Canceled,
                deliveredSamples, deliveredFrames, discardedFrames, true,
                terminal.Cancellation, terminal.Cancellation != F5CancellationCapability.CooperativeComputeCancel);
        return Result(action, F5SynthesisOutcome.Completed, null,
            deliveredSamples, deliveredFrames, discardedFrames);
    }

    private async Task<CancelAttempt?> MonitorCancellationAsync(
        F5SynthesisRequest request,
        CancellationToken actionToken,
        CancellationToken stopToken)
    {
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(actionToken, stopToken);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, combined.Token);
            return null;
        }
        catch (OperationCanceledException) when (combined.IsCancellationRequested)
        {
            if (!actionToken.IsCancellationRequested)
                return null;
        }

        try
        {
            using var timeout = new CancellationTokenSource(
                F5WorkerProtocol.MaximumCancelDuration, clock);
            var response = await transport.CancelAsync(new()
            {
                Ids = request.Ids,
                ActionId = request.ActionId,
                DestinationId = request.DestinationId,
                ReferenceRevision = request.Reference.ReferenceRevision
            }, timeout.Token);
            if (response is null)
                throw new F5Exception(F5Failure.ProtocolViolation);
            F5Guard.Require(response.Ids == request.Ids &&
                response.LocalDiscardAcknowledged &&
                CancellationAtMost(response.ComputeCancellation,
                    request.ExpectedWorker.Cancellation) &&
                response.WorkerMayContinue ==
                    (response.ComputeCancellation !=
                        F5CancellationCapability.CooperativeComputeCancel),
                F5Failure.ProtocolViolation);
            F5Guard.Defined(response.ComputeCancellation);
            return new(response, Failed: false);
        }
        catch (F5TransportException)
        {
            return new(null, Failed: true);
        }
        catch (OperationCanceledException)
        {
            return new(null, Failed: true);
        }
        catch (F5Exception)
        {
            return new(null, Failed: true);
        }
    }

    private void ValidateAction(ActionContext action, DateTimeOffset now)
    {
        F5Guard.Require(action.ActionId != Guid.Empty);
        action.Ids.Validate();
        F5Guard.Require(action.DestinationId == destinationId,
            F5Failure.AuthorizationMismatch);
        F5Guard.Require(action.ExpectedWorker.Matches(selectedWorker),
            F5Failure.IdentityMismatch);
        F5Guard.Sha256(action.ReferenceRevision);
        F5Guard.Utc(action.DeadlineUtc);
        F5Guard.Require(action.DeadlineUtc > now, F5Failure.DeadlineExceeded);
        F5Guard.Require(action.DeadlineUtc <= now + F5WorkerProtocol.MaximumRequestDuration,
            F5Failure.LimitExceeded);
        F5ConversationSynthesis.ValidateChunks(action.Chunks,
            F5WorkerProtocol.MaximumTextChunks, F5WorkerProtocol.MaximumTextUtf8Bytes);
    }

    private static void ValidateEnvelope(
        F5WorkerEvent? item,
        ActionContext action,
        long expectedSequence)
    {
        if (item is null)
            throw new F5Exception(F5Failure.ProtocolViolation);
        item.ValidateShape();
        F5Guard.Require(item.ProtocolVersion == F5ProtocolVersion.Current &&
            item.ContractId == F5WorkerProtocol.ContractId &&
            item.Sequence == expectedSequence &&
            item.Ids == action.Ids &&
            item.Worker.Matches(action.ExpectedWorker) &&
            item.ReferenceRevision == action.ReferenceRevision,
            F5Failure.ProtocolViolation);
        if (item.Kind == F5WorkerEventKind.Canceled)
            F5Guard.Require(CancellationAtMost(
                item.Cancellation!.Value, action.ExpectedWorker.Cancellation),
                F5Failure.ProtocolViolation);
    }

    private static IAsyncEnumerable<F5WorkerEvent> RequireStream(
        IAsyncEnumerable<F5WorkerEvent>? stream) =>
        stream ?? throw new F5Exception(F5Failure.ProtocolViolation);

    private static bool CancellationAtMost(
        F5CancellationCapability actual,
        F5CancellationCapability declared) =>
        CancellationRank(actual) <= CancellationRank(declared);

    private static int CancellationRank(F5CancellationCapability capability) => capability switch
    {
        F5CancellationCapability.DiscardOnly => 0,
        F5CancellationCapability.RequestAbort => 1,
        F5CancellationCapability.CooperativeComputeCancel => 2,
        _ => int.MaxValue
    };

    private DateTimeOffset CurrentUtc()
    {
        var now = clock.GetUtcNow();
        F5Guard.Utc(now);
        return now;
    }

    private static F5SynthesisOutcome FailureOutcome(F5Failure failure) => failure switch
    {
        F5Failure.Canceled => F5SynthesisOutcome.Canceled,
        F5Failure.DeadlineExceeded or F5Failure.AuthorizationExpired =>
            F5SynthesisOutcome.DeadlineExceeded,
        _ => F5SynthesisOutcome.Failed
    };

    private static F5SynthesisResult Result(
        ActionContext action,
        F5SynthesisOutcome outcome,
        F5Failure? failure,
        long deliveredSamples = 0,
        int deliveredFrames = 0,
        int discardedFrames = 0,
        bool localDiscarded = false,
        F5CancellationCapability? computeCancellation = null,
        bool workerMayContinue = false,
        F5WorkerError? workerError = null) => new()
        {
            Ids = action.Ids,
            ActionId = action.ActionId,
            Outcome = outcome,
            Failure = failure,
            ReferenceRevision = action.ReferenceRevision,
            DeliveredSamples = deliveredSamples,
            DeliveredFrames = deliveredFrames,
            DiscardedLateFrames = discardedFrames,
            LocalOutputDiscarded = localDiscarded,
            ComputeCancellation = computeCancellation,
            WorkerMayContinue = workerMayContinue,
            WorkerError = workerError
        };
}
