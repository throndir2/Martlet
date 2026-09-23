namespace Martlet.Perception;

public sealed class PerceptionGatewayClientAdapter : IPerceptionJobExecutor
{
    private readonly IAuthenticatedPerceptionWorkerTransport transport;
    private readonly PerceptionGatewayBinding binding;
    private readonly string destinationId;
    private readonly string hostId;
    private readonly PerceptionWorkerIdentity selectedWorker;
    private readonly TimeProvider clock;

    public PerceptionGatewayClientAdapter(
        IAuthenticatedPerceptionWorkerTransport transport,
        string destinationId,
        string hostId,
        PerceptionWorkerIdentity selectedWorker,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(selectedWorker);
        PerceptionWorkerGuard.Identifier(destinationId, 128);
        PerceptionWorkerGuard.Identifier(hostId);
        binding = transport.Binding;
        ArgumentNullException.ThrowIfNull(binding);
        binding.Validate();
        PerceptionWorkerGuard.Require(binding.DestinationId == destinationId,
            PerceptionWorkerFailure.DestinationMismatch);
        PerceptionWorkerGuard.Require(binding.HostId == hostId,
            PerceptionWorkerFailure.DestinationMismatch);
        this.transport = transport;
        this.destinationId = destinationId;
        this.hostId = hostId;
        this.selectedWorker = selectedWorker;
        this.clock = clock ?? TimeProvider.System;
    }

    public PerceptionRole Role => selectedWorker.Role;
    public PerceptionWorkerIdentity Worker => selectedWorker;

    public async ValueTask<PerceptionJobResult> ExecuteAsync(
        PerceptionJobIntent request,
        PerceptionVisionAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authorization);

        if (cancellationToken.IsCancellationRequested)
            return Result(request, PerceptionJobOutcome.Canceled,
                PerceptionWorkerFailure.Canceled, outputDiscarded: true);

        var jobClock = new PerceptionJobClock(clock);
        var now = jobClock.GetUtcNow();
        var preflight = ValidatePreflight(request, authorization, now);
        if (preflight is not null)
            return preflight;

        if (!authorization.TryConsume())
            return Result(request, PerceptionJobOutcome.Failed,
                PerceptionWorkerFailure.PermissionConsumed);

        var remaining = request.DeadlineUtc - jobClock.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
            return Result(request, PerceptionJobOutcome.DeadlineExceeded,
                PerceptionWorkerFailure.DeadlineExceeded, outputDiscarded: true);

        using var deadline = new CancellationTokenSource(remaining, clock);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, deadline.Token);
        using var workerLifetime = new CancellationTokenSource();
        var workerCancellationGate = new object();
        var workerCancellationCallbacks = Task.CompletedTask;
        using var cancelWorker = lifetime.Token.Register(() =>
            BeginWorkerCancellation());
        var workerToken = workerLifetime.Token;
        var workerRequest = new PerceptionWorkerRequest(request);
        var dispatched = 0;
        Task<PerceptionJobResult>? execution = null;

        Task BeginWorkerCancellation()
        {
            lock (workerCancellationGate)
            {
                if (!workerLifetime.IsCancellationRequested)
                {
                    workerCancellationCallbacks = workerLifetime.CancelAsync();
                    ObserveFault(workerCancellationCallbacks);
                }
                return workerCancellationCallbacks;
            }
        }

        async ValueTask<CancelAttempt?> StopAsync()
        {
            var callbacks = BeginWorkerCancellation();
            var cancel = await TryCancelAsync(workerRequest).ConfigureAwait(false);
            try
            {
                await Task.WhenAll(callbacks, execution ?? Task.CompletedTask)
                    .WaitAsync(PerceptionProtocol.MaximumCancelDuration, clock)
                    .ConfigureAwait(false);
                return cancel;
            }
            catch (Exception)
            {
                if (callbacks.IsCompletedSuccessfully && (execution?.IsCompleted ?? true))
                    return cancel;
                return cancel is null ? null : cancel with { WorkerMayContinue = true };
            }
        }

        PerceptionJobResult result;
        try
        {
            execution = Task.Run(async () =>
            {
                workerToken.ThrowIfCancellationRequested();
                PerceptionWorkerGuard.Require(transport.Binding == binding,
                    PerceptionWorkerFailure.DestinationMismatch);
                var dispatchFailure = ValidatePreflight(
                    request, authorization, jobClock.GetUtcNow());
                if (dispatchFailure?.Failure is { } failure)
                    throw new PerceptionWorkerException(failure);
                lock (workerCancellationGate)
                {
                    workerToken.ThrowIfCancellationRequested();
                    Interlocked.Exchange(ref dispatched, 1);
                }
                var response = await transport.ExecuteAsync(workerRequest, workerToken)
                    .ConfigureAwait(false);
                workerToken.ThrowIfCancellationRequested();
                return ValidateResponse(request, response, jobClock.GetUtcNow());
            });
            ObserveFault(execution);
            result = await execution.WaitAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (PerceptionTransportException error)
        {
            var failure = MapTransportFailure(error.Failure);
            CancelAttempt? cancel = null;
            if (failure is
                PerceptionWorkerFailure.HostUnavailable or
                PerceptionWorkerFailure.DeadlineExceeded or
                PerceptionWorkerFailure.ProtocolViolation)
            {
                cancel = await StopAsync().ConfigureAwait(false);
            }
            return Result(
                request,
                FailureOutcome(failure),
                failure,
                outputDiscarded: true,
                cancel: cancel,
                workerMayContinue: failure is
                    PerceptionWorkerFailure.HostUnavailable or
                    PerceptionWorkerFailure.DeadlineExceeded or
                    PerceptionWorkerFailure.ProtocolViolation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancel = await StopAsync().ConfigureAwait(false);
            return Result(request, PerceptionJobOutcome.Canceled,
                PerceptionWorkerFailure.Canceled, outputDiscarded: true, cancel: cancel,
                workerMayContinue: true);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            var cancel = await StopAsync().ConfigureAwait(false);
            return Result(request, PerceptionJobOutcome.DeadlineExceeded,
                PerceptionWorkerFailure.DeadlineExceeded, outputDiscarded: true, cancel: cancel,
                workerMayContinue: true);
        }
        catch (PerceptionWorkerException error)
        {
            var cancel = Volatile.Read(ref dispatched) != 0
                ? await StopAsync().ConfigureAwait(false)
                : null;
            return Result(request, FailureOutcome(error.Failure), error.Failure,
                outputDiscarded: true, cancel: cancel,
                workerMayContinue: Volatile.Read(ref dispatched) != 0);
        }
        catch (Exception)
        {
            var cancel = await StopAsync().ConfigureAwait(false);
            return Result(request, PerceptionJobOutcome.Failed,
                PerceptionWorkerFailure.ProtocolViolation, outputDiscarded: true,
                cancel: cancel, workerMayContinue: true);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            var cancel = await StopAsync().ConfigureAwait(false);
            return Result(request, PerceptionJobOutcome.Canceled,
                PerceptionWorkerFailure.Canceled, outputDiscarded: true, cancel: cancel,
                workerMayContinue: true);
        }
        if (deadline.IsCancellationRequested || jobClock.GetUtcNow() >= request.DeadlineUtc)
        {
            var cancel = await StopAsync().ConfigureAwait(false);
            return Result(request, PerceptionJobOutcome.DeadlineExceeded,
                PerceptionWorkerFailure.DeadlineExceeded, outputDiscarded: true, cancel: cancel,
                workerMayContinue: true);
        }

        // Worker-owned output was snapshotted within the bounded execution task.
        var publicationTime = jobClock.GetUtcNow();
        if (result.Outcome == PerceptionJobOutcome.Completed &&
            (publicationTime >= result.Observation!.ExpiresAtUtc ||
             publicationTime - request.Frame.CapturedAtUtc >= request.MaximumFrameAge))
            return Result(request, PerceptionJobOutcome.NotScheduled,
                PerceptionWorkerFailure.StaleResult, outputDiscarded: true);
        return result;
    }

    private PerceptionJobResult? ValidatePreflight(
        PerceptionJobIntent request,
        PerceptionVisionAuthorization authorization,
        DateTimeOffset now)
    {
        if (request.DestinationId != destinationId)
            return Result(request, PerceptionJobOutcome.Failed,
                PerceptionWorkerFailure.DestinationMismatch);
        if (!request.ExpectedWorker.Matches(selectedWorker))
            return Result(request, PerceptionJobOutcome.Failed,
                PerceptionWorkerFailure.IdentityMismatch);
        if (request.Task.Role != selectedWorker.Role)
            return Result(request, PerceptionJobOutcome.Failed,
                PerceptionWorkerFailure.RoleMismatch);
        if (!ReferenceEquals(authorization.Request, request))
            return Result(request, PerceptionJobOutcome.Failed,
                PerceptionWorkerFailure.PermissionMismatch);
        if (authorization.ExpiresAtUtc <= now ||
            authorization.ExpiresAtUtc > request.DeadlineUtc)
            return Result(request, PerceptionJobOutcome.Failed,
                PerceptionWorkerFailure.PermissionExpired);
        if (request.CreatedAtUtc > now + PerceptionProtocol.MaximumClockSkew ||
            request.Frame.CapturedAtUtc > now + PerceptionProtocol.MaximumClockSkew)
            return Result(request, PerceptionJobOutcome.NotScheduled,
                PerceptionWorkerFailure.ClockSkew, outputDiscarded: true);
        if (request.DeadlineUtc <= now)
            return Result(request, PerceptionJobOutcome.DeadlineExceeded,
                PerceptionWorkerFailure.DeadlineExceeded, outputDiscarded: true);
        if (request.DeadlineUtc - now > PerceptionProtocol.MaximumJobDuration)
            return Result(request, PerceptionJobOutcome.DeadlineExceeded,
                PerceptionWorkerFailure.DeadlineExceeded, outputDiscarded: true);
        if (now - request.Frame.CapturedAtUtc >= request.MaximumFrameAge)
            return Result(request, PerceptionJobOutcome.NotScheduled,
                PerceptionWorkerFailure.StaleFrame, outputDiscarded: true);
        if (request.Frame.Content.ReferenceExpiresAtUtc is { } referenceExpiry &&
            referenceExpiry <= now)
            return Result(request, PerceptionJobOutcome.NotScheduled,
                PerceptionWorkerFailure.InvalidFrameReference, outputDiscarded: true);
        return null;
    }

    private PerceptionJobResult ValidateResponse(
        PerceptionJobIntent request,
        PerceptionGatewayResponse response,
        DateTimeOffset now)
    {
        if (response is null)
            throw new PerceptionWorkerException(PerceptionWorkerFailure.ProtocolViolation);
        PerceptionWorkerGuard.Require(response.DestinationId == destinationId &&
            response.HostId == hostId,
            PerceptionWorkerFailure.DestinationMismatch);
        PerceptionWorkerGuard.Require(Enum.IsDefined(response.AuthenticatedRole),
            PerceptionWorkerFailure.ProtocolViolation);
        PerceptionWorkerGuard.Require(response.AuthenticatedRole ==
            PerceptionGatewayRole.Perception,
            PerceptionWorkerFailure.AuthenticationFailed);

        var workerResponse = response.WorkerResponse;
        if (workerResponse is null)
            throw new PerceptionWorkerException(PerceptionWorkerFailure.ProtocolViolation);
        PerceptionWorkerGuard.Require(workerResponse.Ids == request.Ids &&
            workerResponse.ActionId == request.ActionId,
            PerceptionWorkerFailure.ProtocolViolation);
        PerceptionWorkerGuard.Require(workerResponse.Epoch == request.Epoch,
            workerResponse.Epoch < request.Epoch
                ? PerceptionWorkerFailure.LateEpoch
                : PerceptionWorkerFailure.ProtocolViolation);
        PerceptionWorkerGuard.Require(workerResponse.Worker is not null &&
            selectedWorker.Matches(workerResponse.Worker),
            PerceptionWorkerFailure.IdentityMismatch);
        PerceptionWorkerGuard.Require(Enum.IsDefined(workerResponse.Outcome),
            PerceptionWorkerFailure.UnknownOutput);
        if (workerResponse.ComputeCancellation is { } cancellation)
        {
            PerceptionWorkerGuard.Require(Enum.IsDefined(cancellation),
                PerceptionWorkerFailure.ProtocolViolation);
            PerceptionWorkerGuard.Require(CancellationRank(cancellation) <=
                CancellationRank(selectedWorker.Cancellation),
                PerceptionWorkerFailure.ProtocolViolation);
            PerceptionWorkerGuard.Require(
                cancellation == PerceptionCancellationCapability.CooperativeComputeCancel ||
                workerResponse.WorkerMayContinue,
                PerceptionWorkerFailure.ProtocolViolation);
        }

        return workerResponse.Outcome switch
        {
            PerceptionWorkerOutcome.Completed =>
                CompletedResult(request, workerResponse, now),
            PerceptionWorkerOutcome.Canceled =>
                CanceledResult(request, workerResponse),
            PerceptionWorkerOutcome.Failed =>
                FailedResult(request, workerResponse),
            _ => throw new PerceptionWorkerException(PerceptionWorkerFailure.UnknownOutput)
        };
    }

    private PerceptionJobResult CompletedResult(
        PerceptionJobIntent request,
        PerceptionWorkerResponse response,
        DateTimeOffset now)
    {
        PerceptionWorkerGuard.Require(response.Error is null &&
            response.Observation is not null &&
            response.ComputeCancellation is null &&
            !response.WorkerMayContinue,
            PerceptionWorkerFailure.ProtocolViolation);
        var suppliedObservation = response.Observation ??
            throw new PerceptionWorkerException(PerceptionWorkerFailure.ProtocolViolation);
        var observation = ValidateObservation(request, suppliedObservation, now);
        return Result(request, PerceptionJobOutcome.Completed, null,
            observation: observation);
    }

    private PerceptionJobResult CanceledResult(
        PerceptionJobIntent request,
        PerceptionWorkerResponse response)
    {
        PerceptionWorkerGuard.Require(response.Observation is null &&
            response.Error is null &&
            response.ComputeCancellation is not null,
            PerceptionWorkerFailure.ProtocolViolation);
        return Result(
            request,
            PerceptionJobOutcome.Canceled,
            PerceptionWorkerFailure.Canceled,
            outputDiscarded: true,
            computeCancellation: response.ComputeCancellation,
            workerMayContinue: response.WorkerMayContinue);
    }

    private PerceptionJobResult FailedResult(
        PerceptionJobIntent request,
        PerceptionWorkerResponse response)
    {
        PerceptionWorkerGuard.Require(response.Observation is null &&
            response.Error is not null,
            PerceptionWorkerFailure.ProtocolViolation);
        var workerError = response.Error ??
            throw new PerceptionWorkerException(PerceptionWorkerFailure.ProtocolViolation);
        workerError.Validate();
        if (workerError.Code == PerceptionWorkerErrorCode.Canceled)
        {
            PerceptionWorkerGuard.Require(response.ComputeCancellation is not null,
                PerceptionWorkerFailure.ProtocolViolation);
        }
        var failure = workerError.Code switch
        {
            PerceptionWorkerErrorCode.InvalidRequest => PerceptionWorkerFailure.WorkerFailed,
            PerceptionWorkerErrorCode.InvalidImage => PerceptionWorkerFailure.InvalidImage,
            PerceptionWorkerErrorCode.ModelNotReady => PerceptionWorkerFailure.ModelNotReady,
            PerceptionWorkerErrorCode.ResourceExhausted => PerceptionWorkerFailure.ResourceExhausted,
            PerceptionWorkerErrorCode.DeadlineExceeded => PerceptionWorkerFailure.DeadlineExceeded,
            PerceptionWorkerErrorCode.Canceled => PerceptionWorkerFailure.Canceled,
            PerceptionWorkerErrorCode.InternalFailure => PerceptionWorkerFailure.WorkerFailed,
            _ => PerceptionWorkerFailure.UnknownOutput
        };
        return Result(
            request,
            FailureOutcome(failure),
            failure,
            outputDiscarded: true,
            computeCancellation: response.ComputeCancellation,
            workerMayContinue: response.WorkerMayContinue,
            workerError: workerError);
    }

    private PerceptionObservation ValidateObservation(
        PerceptionJobIntent request,
        PerceptionObservation observation,
        DateTimeOffset now)
    {
        PerceptionWorkerGuard.Defined(observation.Role);
        PerceptionWorkerGuard.Require(observation.Role == request.Task.Role,
            PerceptionWorkerFailure.RoleMismatch);
        ArgumentNullException.ThrowIfNull(observation.Provenance);
        var provenance = observation.Provenance;
        PerceptionWorkerGuard.Require(
            provenance.FrameId == request.Frame.FrameId &&
            provenance.CaptureEpoch == request.Epoch &&
            provenance.SelectionId == request.Frame.Source.SelectionId &&
            provenance.SourceRevision == request.Frame.Source.SourceRevision &&
            provenance.CapturePermissionRevision ==
                request.Frame.Source.CapturePermissionRevision &&
            provenance.FrameSha256 == request.Frame.Content.Sha256 &&
            provenance.CapturedAtUtc == request.Frame.CapturedAtUtc &&
            provenance.DestinationId == destinationId &&
            provenance.HostId == hostId &&
            provenance.WorkerId == selectedWorker.WorkerId &&
            provenance.Evidence == selectedWorker.Evidence &&
            provenance.Role == selectedWorker.Role &&
            provenance.ModelId == selectedWorker.Model.ModelId &&
            provenance.ModelRevision == selectedWorker.Model.ModelRevision &&
            provenance.ModelSha256 == selectedWorker.Model.ModelSha256,
            PerceptionWorkerFailure.ProtocolViolation);
        PerceptionWorkerGuard.Utc(provenance.CapturedAtUtc);
        PerceptionWorkerGuard.Utc(provenance.ProcessedAtUtc);
        PerceptionWorkerGuard.Utc(observation.ExpiresAtUtc);
        PerceptionWorkerGuard.Require(provenance.ProcessedAtUtc >= provenance.CapturedAtUtc &&
            provenance.ProcessedAtUtc <= now + TimeSpan.FromSeconds(2) &&
            observation.ExpiresAtUtc > provenance.ProcessedAtUtc &&
            observation.ExpiresAtUtc <=
                request.Frame.CapturedAtUtc + request.MaximumFrameAge &&
            observation.ExpiresAtUtc <= request.DeadlineUtc,
            PerceptionWorkerFailure.ProtocolViolation);
        PerceptionWorkerGuard.Require(observation.ExpiresAtUtc > now,
            PerceptionWorkerFailure.StaleResult);

        PerceptionOcrOutput? ocr = null;
        PerceptionVlmOutput? vlm = null;
        if (request.Task.Role == PerceptionRole.Ocr)
        {
            PerceptionWorkerGuard.Require(observation.Ocr is not null &&
                observation.Vlm is null,
                PerceptionWorkerFailure.UnknownOutput);
            var suppliedOcr = observation.Ocr ??
                throw new PerceptionWorkerException(PerceptionWorkerFailure.UnknownOutput);
            ocr = suppliedOcr.ValidateAndClone(
                selectedWorker.Limits.MaximumOutputUtf8Bytes);
        }
        else
        {
            PerceptionWorkerGuard.Require(observation.Vlm is not null &&
                observation.Ocr is null,
                PerceptionWorkerFailure.UnknownOutput);
            var suppliedVlm = observation.Vlm ??
                throw new PerceptionWorkerException(PerceptionWorkerFailure.UnknownOutput);
            vlm = suppliedVlm.ValidateAndClone(
                selectedWorker.Limits.MaximumOutputUtf8Bytes);
        }

        return observation with
        {
            Provenance = provenance with { },
            Ocr = ocr,
            Vlm = vlm
        };
    }

    private async ValueTask<CancelAttempt?> TryCancelAsync(
        PerceptionWorkerRequest request)
    {
        using var timeout = new CancellationTokenSource(
            PerceptionProtocol.MaximumCancelDuration, clock);
        using var transportCancellation = new CancellationTokenSource();
        using var cancelTransport = timeout.Token.Register(() =>
            ObserveFault(transportCancellation.CancelAsync()));
        var transportToken = transportCancellation.Token;
        try
        {
            var cancellation = Task.Run(async () =>
            {
                transportToken.ThrowIfCancellationRequested();
                return await transport.CancelAsync(new()
                {
                    Ids = request.Ids,
                    ActionId = request.ActionId,
                    Epoch = request.Epoch
                }, transportToken).ConfigureAwait(false);
            });
            ObserveFault(cancellation);
            var response = await cancellation.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (response is null ||
                response.Ids != request.Ids ||
                response.ActionId != request.ActionId ||
                response.Epoch != request.Epoch ||
                !response.LocalDiscardAcknowledged ||
                !Enum.IsDefined(response.ComputeCancellation) ||
                CancellationRank(response.ComputeCancellation) >
                    CancellationRank(selectedWorker.Cancellation))
                return null;
            if (response.ComputeCancellation !=
                    PerceptionCancellationCapability.CooperativeComputeCancel &&
                !response.WorkerMayContinue)
                return null;
            return new(response.ComputeCancellation, response.WorkerMayContinue);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static void ObserveFault(Task task) =>
        _ = task.ContinueWith(completed => _ = completed.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static int CancellationRank(PerceptionCancellationCapability capability) =>
        capability switch
        {
            PerceptionCancellationCapability.DiscardOnly => 0,
            PerceptionCancellationCapability.RequestAbort => 1,
            PerceptionCancellationCapability.CooperativeComputeCancel => 2,
            _ => int.MaxValue
        };

    private static PerceptionWorkerFailure MapTransportFailure(
        PerceptionTransportFailure failure) => failure switch
        {
            PerceptionTransportFailure.AuthenticationFailed =>
                PerceptionWorkerFailure.AuthenticationFailed,
            PerceptionTransportFailure.HostUnavailable =>
                PerceptionWorkerFailure.HostUnavailable,
            PerceptionTransportFailure.DeadlineExceeded =>
                PerceptionWorkerFailure.DeadlineExceeded,
            PerceptionTransportFailure.RedirectRejected =>
                PerceptionWorkerFailure.RedirectRejected,
            PerceptionTransportFailure.ProtocolViolation =>
                PerceptionWorkerFailure.ProtocolViolation,
            _ => PerceptionWorkerFailure.ProtocolViolation
        };

    private static PerceptionJobOutcome FailureOutcome(PerceptionWorkerFailure failure) =>
        failure switch
        {
            PerceptionWorkerFailure.Canceled => PerceptionJobOutcome.Canceled,
            PerceptionWorkerFailure.DeadlineExceeded => PerceptionJobOutcome.DeadlineExceeded,
            PerceptionWorkerFailure.StaleFrame or
            PerceptionWorkerFailure.LateEpoch or
            PerceptionWorkerFailure.Superseded or
            PerceptionWorkerFailure.ResourceBudgetExceeded or
            PerceptionWorkerFailure.ResourceUnavailable =>
                PerceptionJobOutcome.NotScheduled,
            _ => PerceptionJobOutcome.Failed
        };

    private static PerceptionJobResult Result(
        PerceptionJobIntent request,
        PerceptionJobOutcome outcome,
        PerceptionWorkerFailure? failure,
        PerceptionObservation? observation = null,
        bool outputDiscarded = false,
        CancelAttempt? cancel = null,
        PerceptionCancellationCapability? computeCancellation = null,
        bool workerMayContinue = false,
        PerceptionWorkerError? workerError = null) => new()
        {
            Ids = request.Ids,
            ActionId = request.ActionId,
            Epoch = request.Epoch,
            Role = request.Task.Role,
            Outcome = outcome,
            Failure = failure,
            Observation = observation,
            ComputeCancellation = cancel?.Capability ?? computeCancellation,
            WorkerMayContinue = cancel?.WorkerMayContinue ?? workerMayContinue,
            OutputDiscarded = outputDiscarded,
            WorkerError = workerError
        };

    private sealed record CancelAttempt(
        PerceptionCancellationCapability Capability,
        bool WorkerMayContinue);
}
