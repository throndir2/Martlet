using System.Collections;
using Martlet.Perception;

namespace Martlet.Perception.Tests;

public sealed class ReuseRegressionTests
{
    [Fact]
    public void Oversized_image_is_rejected_before_allocating_a_copy()
    {
        var bytes = new byte[PerceptionProtocol.MaximumImageBytes + 1];
        var before = GC.GetAllocatedBytesForCurrentThread();
        PerceptionTestData.Failure(PerceptionWorkerFailure.LimitExceeded,
            () => SelectedWindowFrameContent.FromInlinePng(bytes));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1_000_000);
    }

    [Fact]
    public void Artifact_enumeration_stops_at_the_first_excess_item()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        IEnumerable<PerceptionArtifactIdentity> TooMany()
        {
            for (var i = 0; i < 17; i++)
                yield return worker.Artifacts[0];
            throw new InvalidOperationException("Enumeration exceeded the fixed bound.");
        }
        PerceptionTestData.Failure(PerceptionWorkerFailure.InvalidData,
            () => new PerceptionWorkerIdentity(worker.WorkerId, worker.Evidence,
                worker.Role, worker.Runtime, worker.Model, TooMany(),
                worker.Limits, worker.Resources, worker.Cancellation));
    }

    [Fact]
    public void Content_bearing_records_redact_default_diagnostics()
    {
        const string secret = "PRIVATE-CONTENT-CANARY";
        object[] records =
        [
            new PerceptionTextRegion
            {
                Index = 0, Text = secret,
                Bounds = new() { Left = 0, Top = 0, Right = 1, Bottom = 1 },
                Confidence = PerceptionConfidence.Unavailable()
            },
            new PerceptionOcrOutput { Regions = [], DetectedLanguage = secret },
            new PerceptionVlmOutput
            {
                Answer = secret, Uncertainty = secret,
                Confidence = PerceptionConfidence.Unavailable()
            },
            new PerceptionWorkerError
            {
                Code = PerceptionWorkerErrorCode.InternalFailure,
                Summary = secret, RemedyCode = secret
            },
            new DeterministicPerceptionWorkerOptions
            {
                OcrText = secret, VlmAnswer = secret, VlmUncertainty = secret
            }
        ];
        Assert.All(records, record =>
            Assert.DoesNotContain(secret, record.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exactly_expired_frame_is_not_dispatched()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var (adapter, transport) = PerceptionTestData.Adapter(worker);
        var request = PerceptionTestData.Intent(PerceptionRole.Ocr, worker: worker,
            capturedAt: PerceptionTestData.Now.AddSeconds(-5));
        var result = await adapter.ExecuteAsync(request, PerceptionTestData.Authorize(request));
        Assert.Equal(PerceptionWorkerFailure.StaleFrame, result.Failure);
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_during_output_validation_cannot_publish(bool scheduled)
    {
        using var stop = new CancellationTokenSource();
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var transport = new ResponseTransport(worker, response =>
        {
            var observation = response.WorkerResponse.Observation!;
            return response with
            {
                WorkerResponse = response.WorkerResponse with
                {
                    Observation = observation with
                    {
                        Ocr = observation.Ocr! with
                        {
                            Regions = new InterceptedRegions(observation.Ocr!.Regions,
                                () => stop.Cancel())
                        }
                    }
                }
            };
        });
        var adapter = Adapter(transport, worker);
        await using var scheduler = Scheduler(adapter);
        var request = PerceptionTestData.Intent(PerceptionRole.Ocr, worker: worker);
        var result = scheduled
            ? await scheduler.ScheduleAsync(request, PerceptionTestData.Authorize(request), stop.Token)
            : await adapter.ExecuteAsync(request, PerceptionTestData.Authorize(request), stop.Token);
        Assert.Equal(PerceptionWorkerFailure.Canceled, result.Failure);
        Assert.Null(result.Observation);
    }

    [Fact]
    public async Task Utc_rollback_does_not_refund_remaining_frame_age()
    {
        var clock = new AdvancingClock();
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var transport = new ResponseTransport(worker, response =>
        {
            clock.Elapsed = TimeSpan.FromSeconds(2);
            return response;
        });
        var request = PerceptionTestData.Intent(PerceptionRole.Ocr, worker: worker,
            capturedAt: PerceptionTestData.Now.AddSeconds(-4));
        var result = await Adapter(transport, worker, clock).ExecuteAsync(
            request, PerceptionTestData.Authorize(request));
        Assert.Equal(PerceptionWorkerFailure.StaleResult, result.Failure);
        Assert.Null(result.Observation);
    }

    [Fact]
    public async Task Expiry_during_output_validation_cannot_publish()
    {
        var clock = new AdvancingClock();
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var transport = new ResponseTransport(worker, response =>
        {
            var observation = response.WorkerResponse.Observation!;
            return response with
            {
                WorkerResponse = response.WorkerResponse with
                {
                    Observation = observation with
                    {
                        Ocr = observation.Ocr! with
                        {
                            Regions = new InterceptedRegions(observation.Ocr!.Regions,
                                () => clock.Elapsed = TimeSpan.FromSeconds(6))
                        }
                    }
                }
            };
        });
        var request = PerceptionTestData.Intent(PerceptionRole.Ocr, worker: worker);
        var result = await Adapter(transport, worker, clock).ExecuteAsync(
            request, PerceptionTestData.Authorize(request));
        Assert.Equal(PerceptionWorkerFailure.StaleResult, result.Failure);
        Assert.Null(result.Observation);
    }

    [Fact]
    public async Task Mutable_ocr_count_cannot_publish_unvalidated_array_entries()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var transport = new ResponseTransport(worker, response =>
        {
            var observation = response.WorkerResponse.Observation!;
            return response with
            {
                WorkerResponse = response.WorkerResponse with
                {
                    Observation = observation with
                    {
                        Ocr = observation.Ocr! with { Regions = new ChangingCountRegions() }
                    }
                }
            };
        });
        var request = PerceptionTestData.Intent(PerceptionRole.Ocr, worker: worker);
        var result = await Adapter(transport, worker).ExecuteAsync(
            request, PerceptionTestData.Authorize(request));
        Assert.NotEqual(PerceptionJobOutcome.Completed, result.Outcome);
        Assert.Null(result.Observation);
    }

    [Fact]
    public async Task Newest_pending_job_keeps_the_running_predecessor_barrier()
    {
        var executor = new ControlledExecutor();
        await using var scheduler = Scheduler(executor);
        var first = PerceptionTestData.Intent(PerceptionRole.Ocr, worker: executor.Worker);
        var run1 = scheduler.ScheduleAsync(first, PerceptionTestData.Authorize(first));
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = PerceptionTestData.Intent(PerceptionRole.Ocr, 2, executor.Worker);
        var run2 = scheduler.ScheduleAsync(second, PerceptionTestData.Authorize(second));
        var third = PerceptionTestData.Intent(PerceptionRole.Ocr, 3, executor.Worker);
        var run3 = scheduler.ScheduleAsync(third, PerceptionTestData.Authorize(third));
        await run2.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(run3.IsCompleted);
        }
        finally
        {
            executor.Release.TrySetResult();
        }
        Assert.Equal(PerceptionWorkerFailure.Superseded, (await run1).Failure);
        Assert.Equal(PerceptionJobOutcome.Completed, (await run3).Outcome);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Blocked_execution_or_cancellation_cannot_hold_the_scheduler_caller(
        bool blockSynchronously, bool faultCancellation)
    {
        using var executor = new BlockingExecutor(blockSynchronously, faultCancellation);
        await using var scheduler = Scheduler(executor);
        var request = PerceptionTestData.Intent(PerceptionRole.Ocr, worker: executor.Worker);
        using var stop = new CancellationTokenSource();
        var returned = new TaskCompletionSource<Task<PerceptionJobResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = Task.Run(() => returned.TrySetResult(
            scheduler.ScheduleAsync(request, PerceptionTestData.Authorize(request), stop.Token)));
        try
        {
            await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var running = await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() => stop.Cancel()).WaitAsync(TimeSpan.FromSeconds(5));
            var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(PerceptionJobOutcome.Completed, result.Outcome);
            Assert.Null(result.Observation);
            Assert.True(result.WorkerMayContinue);
            var later = PerceptionTestData.Intent(PerceptionRole.Ocr, 2, executor.Worker);
            var rejected = await scheduler.ScheduleAsync(later, PerceptionTestData.Authorize(later));
            Assert.Equal(PerceptionWorkerFailure.ResourceUnavailable, rejected.Failure);
        }
        finally
        {
            executor.Release.Set();
            await invocation.WaitAsync(TimeSpan.FromSeconds(5));
            await executor.Retired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adapter_cancellation_is_bounded_even_when_transport_callbacks_block(
        bool blockCancelCall)
    {
        using var transport = new BlockingTransport(blockCancelCall);
        var adapter = Adapter(transport, transport.Worker);
        var request = PerceptionTestData.Intent(PerceptionRole.Ocr, worker: transport.Worker);
        using var stop = new CancellationTokenSource();
        var running = adapter.ExecuteAsync(request,
            PerceptionTestData.Authorize(request), stop.Token).AsTask();
        try
        {
            await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() => stop.Cancel()).WaitAsync(TimeSpan.FromSeconds(5));
            var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(PerceptionWorkerFailure.Canceled, result.Failure);
            Assert.True(result.WorkerMayContinue);
            Assert.Null(result.Observation);
        }
        finally
        {
            transport.Release.Set();
            await transport.Retired.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await transport.CancelRetired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Cancellation_cannot_claim_retirement_while_dispatch_preflight_is_blocked()
    {
        using var transport = new BindingGateTransport();
        using var stop = new CancellationTokenSource();
        var adapter = Adapter(transport, transport.Worker);
        var request = PerceptionTestData.Intent(PerceptionRole.Ocr, worker: transport.Worker);
        var running = Task.Run(async () => await adapter.ExecuteAsync(request,
            PerceptionTestData.Authorize(request), stop.Token));
        try
        {
            await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop.Cancel();
            var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(PerceptionWorkerFailure.Canceled, result.Failure);
            Assert.True(result.WorkerMayContinue);
        }
        finally { transport.Release.Set(); }
    }

    [Fact]
    public async Task Blocked_output_snapshot_does_not_escape_adapter_cancellation_boundary()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var transport = new ResponseTransport(worker, response =>
        {
            var observation = response.WorkerResponse.Observation!;
            return response with
            {
                WorkerResponse = response.WorkerResponse with
                {
                    Observation = observation with
                    {
                        Ocr = observation.Ocr! with
                        {
                            Regions = new InterceptedRegions(observation.Ocr!.Regions, () =>
                            {
                                entered.TrySetResult();
                                release.Wait();
                                retired.TrySetResult();
                            })
                        }
                    }
                }
            };
        });
        var request = PerceptionTestData.Intent(PerceptionRole.Ocr, worker: worker);
        var running = Task.Run(async () => await Adapter(transport, worker).ExecuteAsync(
            request, PerceptionTestData.Authorize(request), stop.Token));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop.Cancel();
            var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(PerceptionWorkerFailure.Canceled, result.Failure);
            Assert.True(result.WorkerMayContinue);
            Assert.Null(result.Observation);
        }
        finally
        {
            release.Set();
            await retired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Deadline_before_executor_launch_does_not_quarantine_unused_capacity()
    {
        var clock = new DispatchExpiryClock();
        var executor = new CountingExecutor();
        await using var scheduler = new PerceptionFreshnessScheduler([executor], new()
        {
            Budget = new() { MaximumCpuUnits = 1, MaximumGpuMemoryMiB = 0, MaximumConcurrency = 1 }
        }, clock);
        var request = PerceptionTestData.Intent(PerceptionRole.Ocr, worker: executor.Worker);
        var expired = await scheduler.ScheduleAsync(request, PerceptionTestData.Authorize(request));
        Assert.Equal(0, executor.Calls);
        Assert.Equal(PerceptionWorkerFailure.DeadlineExceeded, expired.Failure);
        Assert.False(expired.WorkerMayContinue);
        var now = clock.GetUtcNow();
        var later = PerceptionTestData.Intent(PerceptionRole.Ocr, 2, executor.Worker,
            createdAt: now, capturedAt: now, deadline: now.AddSeconds(10));
        var result = await scheduler.ScheduleAsync(later, PerceptionTestData.Authorize(later));
        Assert.Equal(PerceptionWorkerFailure.ModelNotReady, result.Failure);
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task Cancellation_after_retirement_cannot_start_new_callbacks_before_release()
    {
        using var executor = new RetirementGateExecutor();
        await using var scheduler = Scheduler(executor);
        using var stop = new CancellationTokenSource();
        var request = PerceptionTestData.Intent(PerceptionRole.Ocr, worker: executor.Worker);
        var running = scheduler.ScheduleAsync(request, PerceptionTestData.Authorize(request), stop.Token);
        try
        {
            await executor.ReleaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() => stop.Cancel()).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(executor.WorkerToken.IsCancellationRequested);
            executor.AllowRelease.Set();
            var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(PerceptionWorkerFailure.Canceled, result.Failure);
            Assert.False(result.WorkerMayContinue);
            Assert.Equal(0, Volatile.Read(ref executor.CallbackCalls));
            var next = PerceptionTestData.Intent(PerceptionRole.Ocr, 2, executor.Worker);
            var nextResult = await scheduler.ScheduleAsync(next, PerceptionTestData.Authorize(next));
            Assert.Equal(PerceptionWorkerFailure.ModelNotReady, nextResult.Failure);
        }
        finally
        {
            executor.AllowRelease.Set();
            executor.AllowCallback.Set();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static PerceptionGatewayClientAdapter Adapter(
        IAuthenticatedPerceptionWorkerTransport transport, PerceptionWorkerIdentity worker,
        TimeProvider? clock = null) => new(transport, PerceptionTestData.Destination,
            PerceptionTestData.Host, worker, clock ?? PerceptionTestData.Clock);

    private static PerceptionFreshnessScheduler Scheduler(IPerceptionJobExecutor executor) =>
        new([executor], new()
        {
            Budget = new() { MaximumCpuUnits = 1, MaximumGpuMemoryMiB = 0, MaximumConcurrency = 1 }
        }, PerceptionTestData.Clock);

    private sealed class AdvancingClock : TimeProvider
    {
        internal TimeSpan Elapsed { get; set; }
        public override DateTimeOffset GetUtcNow() => PerceptionTestData.Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
    }

    private sealed class ResponseTransport(
        PerceptionWorkerIdentity worker,
        Func<PerceptionGatewayResponse, PerceptionGatewayResponse> transform)
        : IAuthenticatedPerceptionWorkerTransport
    {
        private readonly DeterministicPerceptionWorkerTransport inner =
            new(PerceptionTestData.Binding(), worker, clock: PerceptionTestData.Clock);
        public PerceptionGatewayBinding Binding => inner.Binding;
        public async ValueTask<PerceptionGatewayResponse> ExecuteAsync(
            PerceptionWorkerRequest request, CancellationToken cancellationToken) =>
            transform(await inner.ExecuteAsync(request, cancellationToken));
        public ValueTask<PerceptionCancelResponse> CancelAsync(
            PerceptionCancelRequest request, CancellationToken cancellationToken) =>
            inner.CancelAsync(request, cancellationToken);
    }

    private sealed class InterceptedRegions(
        IReadOnlyList<PerceptionTextRegion> regions, Action onRead)
        : IReadOnlyList<PerceptionTextRegion>
    {
        public int Count => regions.Count;
        public PerceptionTextRegion this[int index]
        {
            get { onRead(); return regions[index]; }
        }
        public IEnumerator<PerceptionTextRegion> GetEnumerator() => regions.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ChangingCountRegions : IReadOnlyList<PerceptionTextRegion>
    {
        private int reads;
        public int Count => ++reads <= 2 ? 1 : 0;
        public PerceptionTextRegion this[int index] =>
            throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<PerceptionTextRegion> GetEnumerator() => throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ControlledExecutor : IPerceptionJobExecutor
    {
        public PerceptionRole Role => PerceptionRole.Ocr;
        public PerceptionWorkerIdentity Worker { get; } =
            DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr,
                cancellation: PerceptionCancellationCapability.CooperativeComputeCancel);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<PerceptionJobResult> ExecuteAsync(
            PerceptionJobIntent request, PerceptionVisionAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            if (request.Epoch == 1)
            {
                Entered.TrySetResult();
                await Release.Task;
            }
            var (adapter, _) = PerceptionTestData.Adapter(Worker);
            return await adapter.ExecuteAsync(request, authorization, cancellationToken);
        }
    }

    private sealed class BlockingExecutor(bool blockSynchronously, bool faultCancellation)
        : IPerceptionJobExecutor, IDisposable
    {
        public PerceptionRole Role => PerceptionRole.Ocr;
        public PerceptionWorkerIdentity Worker { get; } =
            DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        internal ManualResetEventSlim Release { get; } = new();
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Retired { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<PerceptionJobResult> ExecuteAsync(
            PerceptionJobIntent request, PerceptionVisionAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            if (blockSynchronously)
            {
                Entered.TrySetResult();
                Release.Wait();
                Retired.TrySetResult();
                throw new InvalidOperationException("Synthetic blocked execution retired.");
            }
            return new(WaitAsync(cancellationToken));
        }

        private async Task<PerceptionJobResult> WaitAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var registration = cancellationToken.Register(() =>
                {
                    if (faultCancellation)
                        throw new InvalidOperationException("Synthetic cancellation fault.");
                    Release.Wait();
                });
                Entered.TrySetResult();
                await Task.Run(() => Release.Wait());
                throw new InvalidOperationException("Synthetic execution retired.");
            }
            finally
            {
                Retired.TrySetResult();
            }
        }

        public void Dispose() => Release.Dispose();
    }

    private sealed class BlockingTransport(bool blockCancelCall)
        : IAuthenticatedPerceptionWorkerTransport, IDisposable
    {
        public PerceptionGatewayBinding Binding { get; } = PerceptionTestData.Binding();
        internal PerceptionWorkerIdentity Worker { get; } =
            DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr,
                cancellation: PerceptionCancellationCapability.CooperativeComputeCancel);
        internal ManualResetEventSlim Release { get; } = new();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Retired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancelRetired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<PerceptionGatewayResponse> ExecuteAsync(
            PerceptionWorkerRequest request, CancellationToken cancellationToken)
        {
            try
            {
                using var registration = cancellationToken.Register(() => Release.Wait());
                Entered.TrySetResult();
                await Task.Run(() => Release.Wait());
                throw new PerceptionTransportException(PerceptionTransportFailure.HostUnavailable);
            }
            finally { Retired.TrySetResult(); }
        }

        public ValueTask<PerceptionCancelResponse> CancelAsync(
            PerceptionCancelRequest request, CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() => Release.Wait());
            try
            {
                if (blockCancelCall)
                    Release.Wait();
                return ValueTask.FromResult(new PerceptionCancelResponse
                {
                    Ids = request.Ids, ActionId = request.ActionId, Epoch = request.Epoch,
                    LocalDiscardAcknowledged = true,
                    ComputeCancellation = PerceptionCancellationCapability.CooperativeComputeCancel,
                    WorkerMayContinue = false
                });
            }
            finally { CancelRetired.TrySetResult(); }
        }

        public void Dispose() => Release.Dispose();
    }

    private sealed class BindingGateTransport : IAuthenticatedPerceptionWorkerTransport, IDisposable
    {
        private int reads;
        internal PerceptionWorkerIdentity Worker { get; } =
            DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr,
                cancellation: PerceptionCancellationCapability.CooperativeComputeCancel);
        internal ManualResetEventSlim Release { get; } = new();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PerceptionGatewayBinding Binding
        {
            get
            {
                if (Interlocked.Increment(ref reads) == 2)
                {
                    Entered.TrySetResult();
                    Release.Wait();
                }
                return PerceptionTestData.Binding();
            }
        }
        public ValueTask<PerceptionGatewayResponse> ExecuteAsync(
            PerceptionWorkerRequest request, CancellationToken cancellationToken) =>
            throw new PerceptionTransportException(PerceptionTransportFailure.HostUnavailable);
        public ValueTask<PerceptionCancelResponse> CancelAsync(
            PerceptionCancelRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PerceptionCancelResponse
            {
                Ids = request.Ids, ActionId = request.ActionId, Epoch = request.Epoch,
                LocalDiscardAcknowledged = true,
                ComputeCancellation = PerceptionCancellationCapability.CooperativeComputeCancel,
                WorkerMayContinue = false
            });
        public void Dispose() => Release.Dispose();
    }

    private sealed class DispatchExpiryClock : TimeProvider
    {
        private int reads;
        public override DateTimeOffset GetUtcNow() =>
            PerceptionTestData.Now.AddSeconds(Interlocked.Increment(ref reads) >= 4 ? 20 : 0);
        public override long GetTimestamp() => 0;
    }

    private sealed class CountingExecutor : IPerceptionJobExecutor
    {
        internal int Calls;
        public PerceptionRole Role => PerceptionRole.Ocr;
        public PerceptionWorkerIdentity Worker { get; } =
            DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        public ValueTask<PerceptionJobResult> ExecuteAsync(
            PerceptionJobIntent request, PerceptionVisionAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return ValueTask.FromResult(new PerceptionJobResult
            {
                Ids = request.Ids, ActionId = request.ActionId, Epoch = request.Epoch, Role = Role,
                Outcome = PerceptionJobOutcome.Failed, Failure = PerceptionWorkerFailure.ModelNotReady,
                WorkerMayContinue = false, OutputDiscarded = true
            });
        }
    }

    private sealed class RetirementGateExecutor : IPerceptionJobExecutor, IDisposable
    {
        private readonly PerceptionWorkerIdentity worker =
            DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        private int releaseArmed;
        private CancellationTokenRegistration registration;
        internal int CallbackCalls;
        internal CancellationToken WorkerToken { get; private set; }
        internal ManualResetEventSlim AllowRelease { get; } = new();
        internal ManualResetEventSlim AllowCallback { get; } = new();
        internal TaskCompletionSource ReleaseEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PerceptionRole Role => PerceptionRole.Ocr;
        public PerceptionWorkerIdentity Worker
        {
            get
            {
                if (Interlocked.Exchange(ref releaseArmed, 0) == 1)
                {
                    ReleaseEntered.TrySetResult();
                    AllowRelease.Wait();
                }
                return worker;
            }
        }
        public ValueTask<PerceptionJobResult> ExecuteAsync(
            PerceptionJobIntent request, PerceptionVisionAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            if (request.Epoch == 1)
            {
                WorkerToken = cancellationToken;
                registration = cancellationToken.Register(() =>
                {
                    Interlocked.Increment(ref CallbackCalls);
                    AllowCallback.Wait();
                });
                Interlocked.Exchange(ref releaseArmed, 1);
            }
            return ValueTask.FromResult(new PerceptionJobResult
            {
                Ids = request.Ids, ActionId = request.ActionId, Epoch = request.Epoch, Role = Role,
                Outcome = PerceptionJobOutcome.Failed, Failure = PerceptionWorkerFailure.ModelNotReady,
                WorkerMayContinue = false, OutputDiscarded = true
            });
        }
        public void Dispose()
        {
            registration.Dispose();
            AllowRelease.Dispose();
            AllowCallback.Dispose();
        }
    }
}
