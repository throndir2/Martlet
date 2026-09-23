using Martlet.Perception;

namespace Martlet.Perception.Tests;

public sealed class GatewayAdapterTests
{
    [Theory]
    [InlineData(PerceptionRole.Ocr)]
    [InlineData(PerceptionRole.VisualQuestionAnswering)]
    public async Task Adapter_accepts_only_exact_authenticated_role_output(
        PerceptionRole role)
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(role);
        var (adapter, transport) = PerceptionTestData.Adapter(worker);
        var request = PerceptionTestData.Intent(role, worker: worker);

        var result = await adapter.ExecuteAsync(
            request, PerceptionTestData.Authorize(request));

        Assert.Equal(PerceptionJobOutcome.Completed, result.Outcome);
        Assert.Null(result.Failure);
        Assert.NotNull(result.Observation);
        Assert.Equal(role, result.Observation.Role);
        Assert.Equal(request.Frame.FrameId,
            result.Observation.Provenance.FrameId);
        Assert.Equal(request.Frame.Content.Sha256,
            result.Observation.Provenance.FrameSha256);
        Assert.Equal(worker.Model.ModelRevision,
            result.Observation.Provenance.ModelRevision);
        Assert.Equal(PerceptionTestData.Destination,
            result.Observation.Provenance.DestinationId);
        Assert.Equal(PerceptionTestData.Host,
            result.Observation.Provenance.HostId);
        Assert.Single(transport.Requests);
        if (role == PerceptionRole.Ocr)
        {
            Assert.NotNull(result.Observation.Ocr);
            Assert.Null(result.Observation.Vlm);
            Assert.Single(result.Observation.Ocr.Regions);
        }
        else
        {
            Assert.Null(result.Observation.Ocr);
            Assert.NotNull(result.Observation.Vlm);
            Assert.NotEmpty(result.Observation.Vlm.Answer);
            Assert.NotEmpty(result.Observation.Vlm.Uncertainty);
        }
    }

    [Fact]
    public async Task Authorization_is_exact_expiring_and_one_use()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var (adapter, transport) = PerceptionTestData.Adapter(worker);
        var first = PerceptionTestData.Intent(PerceptionRole.Ocr, epoch: 1, worker: worker);
        var second = PerceptionTestData.Intent(PerceptionRole.Ocr, epoch: 2, worker: worker);
        var authorization = PerceptionTestData.Authorize(first);

        var mismatch = await adapter.ExecuteAsync(second, authorization);
        Assert.Equal(PerceptionWorkerFailure.PermissionMismatch, mismatch.Failure);

        var success = await adapter.ExecuteAsync(first, authorization);
        Assert.Equal(PerceptionJobOutcome.Completed, success.Outcome);

        var consumed = await adapter.ExecuteAsync(first, authorization);
        Assert.Equal(PerceptionWorkerFailure.PermissionConsumed, consumed.Failure);

        var expiredRequest = PerceptionTestData.Intent(
            PerceptionRole.Ocr, epoch: 3, worker: worker);
        var expired = await adapter.ExecuteAsync(
            expiredRequest,
            PerceptionTestData.Authorize(
                expiredRequest, PerceptionTestData.Now));
        Assert.Equal(PerceptionWorkerFailure.PermissionExpired, expired.Failure);
        Assert.Single(transport.Requests);
    }

    public static IEnumerable<object[]> TransportFailures()
    {
        yield return
        [
            DeterministicPerceptionFault.AuthenticationFailed,
            PerceptionWorkerFailure.AuthenticationFailed
        ];
        yield return
        [
            DeterministicPerceptionFault.HostUnavailable,
            PerceptionWorkerFailure.HostUnavailable
        ];
        yield return
        [
            DeterministicPerceptionFault.RedirectRejected,
            PerceptionWorkerFailure.RedirectRejected
        ];
    }

    [Theory]
    [MemberData(nameof(TransportFailures))]
    public async Task Auth_host_loss_and_redirects_fail_without_fallback(
        DeterministicPerceptionFault fault,
        PerceptionWorkerFailure expected)
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var (adapter, transport) = PerceptionTestData.Adapter(worker,
            new() { Fault = fault });
        var request = PerceptionTestData.Intent(PerceptionRole.Ocr, worker: worker);

        var result = await adapter.ExecuteAsync(
            request, PerceptionTestData.Authorize(request));

        Assert.Equal(expected, result.Failure);
        Assert.Null(result.Observation);
        Assert.True(result.OutputDiscarded);
        Assert.Single(transport.Requests);
    }

    public static IEnumerable<object[]> InvalidResponses()
    {
        yield return
        [
            DeterministicPerceptionFault.NullGatewayResponse,
            PerceptionWorkerFailure.ProtocolViolation
        ];
        yield return
        [
            DeterministicPerceptionFault.NullWorkerResponse,
            PerceptionWorkerFailure.ProtocolViolation
        ];
        yield return
        [
            DeterministicPerceptionFault.WrongDestination,
            PerceptionWorkerFailure.DestinationMismatch
        ];
        yield return
        [
            DeterministicPerceptionFault.WrongHost,
            PerceptionWorkerFailure.DestinationMismatch
        ];
        yield return
        [
            DeterministicPerceptionFault.WrongGatewayRole,
            PerceptionWorkerFailure.ProtocolViolation
        ];
        yield return
        [
            DeterministicPerceptionFault.WrongIdentity,
            PerceptionWorkerFailure.IdentityMismatch
        ];
        yield return
        [
            DeterministicPerceptionFault.WrongEpoch,
            PerceptionWorkerFailure.LateEpoch
        ];
        yield return
        [
            DeterministicPerceptionFault.UnknownOutcome,
            PerceptionWorkerFailure.UnknownOutput
        ];
        yield return
        [
            DeterministicPerceptionFault.UnknownOutput,
            PerceptionWorkerFailure.UnknownOutput
        ];
        yield return
        [
            DeterministicPerceptionFault.MismatchedProvenance,
            PerceptionWorkerFailure.ProtocolViolation
        ];
    }

    [Theory]
    [MemberData(nameof(InvalidResponses))]
    public async Task Malformed_or_unknown_worker_output_fails_closed(
        DeterministicPerceptionFault fault,
        PerceptionWorkerFailure expected)
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(
            PerceptionRole.VisualQuestionAnswering);
        var (adapter, _) = PerceptionTestData.Adapter(worker,
            new() { Fault = fault });
        var request = PerceptionTestData.Intent(
            PerceptionRole.VisualQuestionAnswering, worker: worker);

        var result = await adapter.ExecuteAsync(
            request, PerceptionTestData.Authorize(request));

        Assert.Equal(expected, result.Failure);
        Assert.Null(result.Observation);
        Assert.True(result.OutputDiscarded);
    }

    [Fact]
    public async Task Valid_but_expired_worker_observation_is_excluded()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var (adapter, _) = PerceptionTestData.Adapter(worker,
            new() { Fault = DeterministicPerceptionFault.StaleResult });
        var request = PerceptionTestData.Intent(
            PerceptionRole.Ocr,
            worker: worker,
            capturedAt: PerceptionTestData.Now.AddSeconds(-2),
            maximumFrameAge: TimeSpan.FromSeconds(5));

        var result = await adapter.ExecuteAsync(
            request, PerceptionTestData.Authorize(request));

        Assert.Equal(PerceptionWorkerFailure.StaleResult, result.Failure);
        Assert.Null(result.Observation);
    }

    public static IEnumerable<object[]> WorkerFailures()
    {
        yield return
        [
            DeterministicPerceptionFault.ModelNotReady,
            PerceptionWorkerFailure.ModelNotReady
        ];
        yield return
        [
            DeterministicPerceptionFault.ResourceExhausted,
            PerceptionWorkerFailure.ResourceExhausted
        ];
        yield return
        [
            DeterministicPerceptionFault.WorkerFailure,
            PerceptionWorkerFailure.WorkerFailed
        ];
    }

    [Theory]
    [MemberData(nameof(WorkerFailures))]
    public async Task Worker_errors_remain_typed_and_never_become_fixture_success(
        DeterministicPerceptionFault fault,
        PerceptionWorkerFailure expected)
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var (adapter, _) = PerceptionTestData.Adapter(worker,
            new() { Fault = fault });
        var request = PerceptionTestData.Intent(PerceptionRole.Ocr, worker: worker);

        var result = await adapter.ExecuteAsync(
            request, PerceptionTestData.Authorize(request));

        Assert.Equal(PerceptionJobOutcome.Failed, result.Outcome);
        Assert.Equal(expected, result.Failure);
        Assert.NotNull(result.WorkerError);
        Assert.Null(result.Observation);
    }

    [Fact]
    public async Task Cancellation_discards_late_output_and_reports_compute_truth()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var (adapter, transport) = PerceptionTestData.Adapter(worker, new()
        {
            BlockUntilReleased = true,
            IgnoreCancellation = true
        });
        var request = PerceptionTestData.Intent(PerceptionRole.Ocr, worker: worker);
        using var stop = new CancellationTokenSource();
        var running = adapter.ExecuteAsync(
            request,
            PerceptionTestData.Authorize(request),
            stop.Token).AsTask();
        await PerceptionTestData.WaitForRequestsAsync(transport, 1);

        await stop.CancelAsync();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
        transport.Release();

        Assert.Equal(PerceptionJobOutcome.Canceled, result.Outcome);
        Assert.Equal(PerceptionWorkerFailure.Canceled, result.Failure);
        Assert.Null(result.Observation);
        Assert.True(result.OutputDiscarded);
        Assert.Equal(PerceptionCancellationCapability.DiscardOnly,
            result.ComputeCancellation);
        Assert.True(result.WorkerMayContinue);
        Assert.Single(transport.Cancellations);
    }

    [Fact]
    public async Task Deadline_discards_worker_that_ignores_cancellation()
    {
        var now = DateTimeOffset.UtcNow;
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var transport = new DeterministicPerceptionWorkerTransport(
            PerceptionTestData.Binding(),
            worker,
            new()
            {
                BlockUntilReleased = true,
                IgnoreCancellation = true
            },
            TimeProvider.System);
        var adapter = new PerceptionGatewayClientAdapter(
            transport,
            PerceptionTestData.Destination,
            PerceptionTestData.Host,
            worker,
            TimeProvider.System);
        var request = PerceptionTestData.Intent(
            PerceptionRole.Ocr,
            worker: worker,
            capturedAt: now,
            createdAt: now,
            deadline: now.AddMilliseconds(100));

        var result = await adapter.ExecuteAsync(
            request, PerceptionTestData.Authorize(request))
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        transport.Release();

        Assert.Equal(PerceptionJobOutcome.DeadlineExceeded, result.Outcome);
        Assert.Equal(PerceptionWorkerFailure.DeadlineExceeded, result.Failure);
        Assert.True(result.WorkerMayContinue);
        Assert.Null(result.Observation);
        Assert.Single(transport.Cancellations);
    }

    [Fact]
    public async Task Expired_ephemeral_reference_never_reaches_worker()
    {
        var content = SelectedWindowFrameContent.FromEphemeralGatewayReference(
            "frame-ref-001",
            new string('a', 64),
            100,
            10,
            10,
            PerceptionTestData.Now.AddSeconds(1));
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var laterClock = new FixedTimeProvider(
            PerceptionTestData.Now.AddSeconds(2));
        var (adapter, transport) = PerceptionTestData.Adapter(
            worker, clock: laterClock);
        var request = PerceptionTestData.Intent(
            PerceptionRole.Ocr,
            worker: worker,
            content: content);

        var result = await adapter.ExecuteAsync(
            request, PerceptionTestData.Authorize(request));

        Assert.Equal(PerceptionWorkerFailure.InvalidFrameReference, result.Failure);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Far_future_timestamps_are_rejected_before_timer_or_transport()
    {
        var future = PerceptionTestData.Now.AddDays(100);
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var (adapter, transport) = PerceptionTestData.Adapter(worker);
        var request = PerceptionTestData.Intent(
            PerceptionRole.Ocr,
            worker: worker,
            capturedAt: future,
            createdAt: future,
            deadline: future.AddSeconds(10));

        var result = await adapter.ExecuteAsync(
            request, PerceptionTestData.Authorize(request));

        Assert.Equal(PerceptionWorkerFailure.ClockSkew, result.Failure);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Remaining_deadline_cannot_exceed_maximum_from_actual_clock()
    {
        var created = PerceptionTestData.Now.AddSeconds(2);
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var (adapter, transport) = PerceptionTestData.Adapter(worker);
        var request = PerceptionTestData.Intent(
            PerceptionRole.Ocr,
            worker: worker,
            capturedAt: created,
            createdAt: created,
            deadline: created.Add(PerceptionProtocol.MaximumJobDuration));

        var result = await adapter.ExecuteAsync(
            request, PerceptionTestData.Authorize(request));

        Assert.Equal(PerceptionWorkerFailure.DeadlineExceeded, result.Failure);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Transport_deadline_attempts_bounded_cancel_and_quarantines_by_default()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var (adapter, transport) = PerceptionTestData.Adapter(worker, new()
        {
            Fault = DeterministicPerceptionFault.DeadlineExceeded
        });
        var request = PerceptionTestData.Intent(
            PerceptionRole.Ocr, worker: worker);

        var result = await adapter.ExecuteAsync(
            request, PerceptionTestData.Authorize(request));

        Assert.Equal(PerceptionWorkerFailure.DeadlineExceeded, result.Failure);
        Assert.True(result.WorkerMayContinue);
        Assert.Equal(PerceptionCancellationCapability.DiscardOnly,
            result.ComputeCancellation);
        Assert.Single(transport.Cancellations);
    }
}
