using Martlet.Perception;

namespace Martlet.Perception.Tests;

public sealed class SchedulerTests
{
    [Fact]
    public async Task Newer_frame_supersedes_old_job_and_only_latest_completes()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(
            PerceptionRole.Ocr,
            cancellation: PerceptionCancellationCapability.CooperativeComputeCancel);
        var (adapter, transport) = PerceptionTestData.Adapter(worker, new()
        {
            BlockUntilReleased = true
        });
        await using var scheduler = Scheduler(
            [adapter],
            cpu: 1,
            gpu: 0,
            concurrency: 1);
        var first = PerceptionTestData.Intent(
            PerceptionRole.Ocr, epoch: 1, worker: worker);
        var firstRun = scheduler.ScheduleAsync(
            first, PerceptionTestData.Authorize(first));
        await PerceptionTestData.WaitForRequestsAsync(transport, 1);

        var second = PerceptionTestData.Intent(
            PerceptionRole.Ocr, epoch: 2, worker: worker);
        var secondRun = scheduler.ScheduleAsync(
            second, PerceptionTestData.Authorize(second));
        await PerceptionTestData.WaitForRequestsAsync(transport, 2);
        transport.Release();

        var firstResult = await firstRun.WaitAsync(TimeSpan.FromSeconds(5));
        var secondResult = await secondRun.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PerceptionWorkerFailure.Superseded, firstResult.Failure);
        Assert.Null(firstResult.Observation);
        Assert.True(firstResult.OutputDiscarded);
        Assert.Equal(PerceptionJobOutcome.Completed, secondResult.Outcome);
        Assert.Equal(2, secondResult.Epoch);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task Rapid_three_frame_burst_accepts_only_newest_observation()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(
            PerceptionRole.Ocr,
            cancellation: PerceptionCancellationCapability.CooperativeComputeCancel);
        var (adapter, transport) = PerceptionTestData.Adapter(worker, new()
        {
            BlockUntilReleased = true
        });
        await using var scheduler = Scheduler(
            [adapter], cpu: 1, gpu: 0, concurrency: 1);
        var first = PerceptionTestData.Intent(
            PerceptionRole.Ocr, epoch: 1, worker: worker);
        var firstRun = scheduler.ScheduleAsync(
            first, PerceptionTestData.Authorize(first));
        await PerceptionTestData.WaitForRequestsAsync(transport, 1);
        var second = PerceptionTestData.Intent(
            PerceptionRole.Ocr, epoch: 2, worker: worker);
        var secondRun = scheduler.ScheduleAsync(
            second, PerceptionTestData.Authorize(second));
        var third = PerceptionTestData.Intent(
            PerceptionRole.Ocr, epoch: 3, worker: worker);
        var thirdRun = scheduler.ScheduleAsync(
            third, PerceptionTestData.Authorize(third));

        await PerceptionTestData.WaitForRequestAsync(
            transport, third.ActionId);
        transport.Release();

        Assert.Equal(PerceptionWorkerFailure.Superseded,
            (await firstRun.WaitAsync(TimeSpan.FromSeconds(5))).Failure);
        Assert.Equal(PerceptionWorkerFailure.Superseded,
            (await secondRun.WaitAsync(TimeSpan.FromSeconds(5))).Failure);
        Assert.Equal(PerceptionJobOutcome.Completed,
            (await thirdRun.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
    }

    [Fact]
    public async Task Discard_only_cancellation_quarantines_budget_instead_of_overcommitting()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var (adapter, transport) = PerceptionTestData.Adapter(worker, new()
        {
            BlockUntilReleased = true,
            IgnoreCancellation = true
        });
        await using var scheduler = Scheduler(
            [adapter],
            cpu: 2,
            gpu: 0,
            concurrency: 2);
        var first = PerceptionTestData.Intent(
            PerceptionRole.Ocr, epoch: 1, worker: worker);
        var firstRun = scheduler.ScheduleAsync(
            first, PerceptionTestData.Authorize(first));
        await PerceptionTestData.WaitForRequestsAsync(transport, 1);

        var second = PerceptionTestData.Intent(
            PerceptionRole.Ocr, epoch: 2, worker: worker);
        var secondRun = scheduler.ScheduleAsync(
            second, PerceptionTestData.Authorize(second));
        var secondResult = await secondRun.WaitAsync(TimeSpan.FromSeconds(5));
        var firstResult = await firstRun.WaitAsync(TimeSpan.FromSeconds(5));
        transport.Release();

        Assert.Equal(PerceptionWorkerFailure.Superseded, firstResult.Failure);
        Assert.True(firstResult.WorkerMayContinue);
        Assert.Equal(PerceptionWorkerFailure.ResourceUnavailable, secondResult.Failure);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task Invalid_permission_does_not_cancel_current_or_poison_new_epoch()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(
            PerceptionRole.Ocr,
            cancellation: PerceptionCancellationCapability.CooperativeComputeCancel);
        var (adapter, transport) = PerceptionTestData.Adapter(worker, new()
        {
            BlockUntilReleased = true
        });
        await using var scheduler = Scheduler(
            [adapter], cpu: 1, gpu: 0, concurrency: 1);
        var first = PerceptionTestData.Intent(
            PerceptionRole.Ocr, epoch: 1, worker: worker);
        var firstRun = scheduler.ScheduleAsync(
            first, PerceptionTestData.Authorize(first));
        await PerceptionTestData.WaitForRequestsAsync(transport, 1);
        var second = PerceptionTestData.Intent(
            PerceptionRole.Ocr, epoch: 2, worker: worker);
        var unrelated = PerceptionTestData.Intent(
            PerceptionRole.Ocr, epoch: 99, worker: worker);

        var rejected = await scheduler.ScheduleAsync(
            second, PerceptionTestData.Authorize(unrelated));

        Assert.Equal(PerceptionWorkerFailure.PermissionMismatch, rejected.Failure);
        Assert.False(firstRun.IsCompleted);
        transport.Release();
        Assert.Equal(PerceptionJobOutcome.Completed, (await firstRun).Outcome);
        var accepted = await scheduler.ScheduleAsync(
            second, PerceptionTestData.Authorize(second));
        Assert.Equal(PerceptionJobOutcome.Completed, accepted.Outcome);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task Late_epoch_is_rejected_without_dispatch()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var (adapter, transport) = PerceptionTestData.Adapter(worker);
        await using var scheduler = Scheduler(
            [adapter], cpu: 1, gpu: 0, concurrency: 1);
        var latest = PerceptionTestData.Intent(
            PerceptionRole.Ocr, epoch: 2, worker: worker);
        Assert.Equal(
            PerceptionJobOutcome.Completed,
            (await scheduler.ScheduleAsync(
                latest, PerceptionTestData.Authorize(latest))).Outcome);
        var old = PerceptionTestData.Intent(
            PerceptionRole.Ocr, epoch: 1, worker: worker);

        var result = await scheduler.ScheduleAsync(
            old, PerceptionTestData.Authorize(old));

        Assert.Equal(PerceptionWorkerFailure.LateEpoch, result.Failure);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task Stale_frame_is_excluded_before_worker_dispatch()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var (adapter, transport) = PerceptionTestData.Adapter(worker);
        await using var scheduler = Scheduler(
            [adapter], cpu: 1, gpu: 0, concurrency: 1);
        var request = PerceptionTestData.Intent(
            PerceptionRole.Ocr,
            worker: worker,
            capturedAt: PerceptionTestData.Now.AddSeconds(-6),
            maximumFrameAge: TimeSpan.FromSeconds(5));

        var result = await scheduler.ScheduleAsync(
            request, PerceptionTestData.Authorize(request));

        Assert.Equal(PerceptionWorkerFailure.StaleFrame, result.Failure);
        Assert.Null(result.Observation);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Job_larger_than_total_cpu_or_gpu_budget_is_rejected()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(
            PerceptionRole.VisualQuestionAnswering,
            cpuUnits: 3,
            gpuMemoryMiB: 2048);
        var (adapter, transport) = PerceptionTestData.Adapter(worker);
        await using var scheduler = Scheduler(
            [adapter], cpu: 2, gpu: 1024, concurrency: 1);
        var request = PerceptionTestData.Intent(
            PerceptionRole.VisualQuestionAnswering, worker: worker);

        var result = await scheduler.ScheduleAsync(
            request, PerceptionTestData.Authorize(request));

        Assert.Equal(PerceptionWorkerFailure.ResourceBudgetExceeded, result.Failure);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Busy_budget_rejects_second_role_immediately_without_queue()
    {
        var ocr = DeterministicPerceptionFixtureIdentity.Create(
            PerceptionRole.Ocr,
            cancellation: PerceptionCancellationCapability.CooperativeComputeCancel);
        var vlm = DeterministicPerceptionFixtureIdentity.Create(
            PerceptionRole.VisualQuestionAnswering,
            cancellation: PerceptionCancellationCapability.CooperativeComputeCancel);
        var (ocrAdapter, ocrTransport) = PerceptionTestData.Adapter(ocr, new()
        {
            BlockUntilReleased = true
        });
        var (vlmAdapter, vlmTransport) = PerceptionTestData.Adapter(vlm);
        await using var scheduler = Scheduler(
            [ocrAdapter, vlmAdapter], cpu: 2, gpu: 0, concurrency: 1);
        var ocrRequest = PerceptionTestData.Intent(
            PerceptionRole.Ocr, worker: ocr);
        var ocrRun = scheduler.ScheduleAsync(
            ocrRequest, PerceptionTestData.Authorize(ocrRequest));
        await PerceptionTestData.WaitForRequestsAsync(ocrTransport, 1);
        var vlmRequest = PerceptionTestData.Intent(
            PerceptionRole.VisualQuestionAnswering, worker: vlm);

        var vlmResult = await scheduler.ScheduleAsync(
            vlmRequest, PerceptionTestData.Authorize(vlmRequest))
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(PerceptionWorkerFailure.ResourceUnavailable, vlmResult.Failure);
        Assert.Empty(vlmTransport.Requests);
        ocrTransport.Release();
        Assert.Equal(PerceptionJobOutcome.Completed,
            (await ocrRun.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
    }

    [Fact]
    public async Task Preferred_role_preempts_lower_role_only_with_truthful_compute_cancel()
    {
        var ocr = DeterministicPerceptionFixtureIdentity.Create(
            PerceptionRole.Ocr,
            cancellation: PerceptionCancellationCapability.CooperativeComputeCancel);
        var vlm = DeterministicPerceptionFixtureIdentity.Create(
            PerceptionRole.VisualQuestionAnswering,
            cancellation: PerceptionCancellationCapability.CooperativeComputeCancel);
        var (ocrAdapter, ocrTransport) = PerceptionTestData.Adapter(ocr, new()
        {
            BlockUntilReleased = true
        });
        var (vlmAdapter, vlmTransport) = PerceptionTestData.Adapter(vlm);
        await using var scheduler = Scheduler(
            [ocrAdapter, vlmAdapter],
            cpu: 2,
            gpu: 0,
            concurrency: 1,
            preferredRole: PerceptionRole.VisualQuestionAnswering);
        var ocrRequest = PerceptionTestData.Intent(
            PerceptionRole.Ocr, worker: ocr);
        var ocrRun = scheduler.ScheduleAsync(
            ocrRequest, PerceptionTestData.Authorize(ocrRequest));
        await PerceptionTestData.WaitForRequestsAsync(ocrTransport, 1);
        var vlmRequest = PerceptionTestData.Intent(
            PerceptionRole.VisualQuestionAnswering, worker: vlm);

        var vlmResult = await scheduler.ScheduleAsync(
            vlmRequest, PerceptionTestData.Authorize(vlmRequest))
            .WaitAsync(TimeSpan.FromSeconds(5));
        var ocrResult = await ocrRun.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PerceptionWorkerFailure.Superseded, ocrResult.Failure);
        Assert.False(ocrResult.WorkerMayContinue);
        Assert.Equal(PerceptionJobOutcome.Completed, vlmResult.Outcome);
        Assert.Single(vlmTransport.Requests);
    }

    [Fact]
    public async Task Cpu_sum_is_strict_even_when_concurrency_has_room()
    {
        var ocr = DeterministicPerceptionFixtureIdentity.Create(
            PerceptionRole.Ocr,
            cpuUnits: 2,
            cancellation: PerceptionCancellationCapability.CooperativeComputeCancel);
        var vlm = DeterministicPerceptionFixtureIdentity.Create(
            PerceptionRole.VisualQuestionAnswering,
            cpuUnits: 2);
        var (ocrAdapter, ocrTransport) = PerceptionTestData.Adapter(ocr, new()
        {
            BlockUntilReleased = true
        });
        var (vlmAdapter, vlmTransport) = PerceptionTestData.Adapter(vlm);
        await using var scheduler = Scheduler(
            [ocrAdapter, vlmAdapter], cpu: 3, gpu: 0, concurrency: 2);
        var first = PerceptionTestData.Intent(PerceptionRole.Ocr, worker: ocr);
        var firstRun = scheduler.ScheduleAsync(
            first, PerceptionTestData.Authorize(first));
        await PerceptionTestData.WaitForRequestsAsync(ocrTransport, 1);
        var second = PerceptionTestData.Intent(
            PerceptionRole.VisualQuestionAnswering, worker: vlm);

        var secondResult = await scheduler.ScheduleAsync(
            second, PerceptionTestData.Authorize(second));

        Assert.Equal(PerceptionWorkerFailure.ResourceUnavailable, secondResult.Failure);
        Assert.Empty(vlmTransport.Requests);
        ocrTransport.Release();
        await firstRun;
    }

    [Fact]
    public async Task Host_2_loss_is_a_bounded_optional_result_not_an_exception()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var (adapter, _) = PerceptionTestData.Adapter(worker, new()
        {
            Fault = DeterministicPerceptionFault.HostUnavailable
        });
        await using var scheduler = Scheduler(
            [adapter], cpu: 1, gpu: 0, concurrency: 1);
        var request = PerceptionTestData.Intent(
            PerceptionRole.Ocr, worker: worker);
        var independentVoiceSentinel = Task.FromResult("voice-continues");

        var result = await scheduler.ScheduleAsync(
            request, PerceptionTestData.Authorize(request));

        Assert.Equal("voice-continues", await independentVoiceSentinel);
        Assert.Equal(PerceptionWorkerFailure.HostUnavailable, result.Failure);
        Assert.Null(result.Observation);
        Assert.True(result.WorkerMayContinue);
    }

    [Fact]
    public async Task Future_job_is_rejected_before_epoch_state_changes()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var (adapter, transport) = PerceptionTestData.Adapter(worker);
        await using var scheduler = Scheduler(
            [adapter], cpu: 1, gpu: 0, concurrency: 1);
        var future = PerceptionTestData.Now.AddDays(10);
        var invalid = PerceptionTestData.Intent(
            PerceptionRole.Ocr,
            epoch: 5,
            worker: worker,
            capturedAt: future,
            createdAt: future,
            deadline: future.AddSeconds(10));

        var rejected = await scheduler.ScheduleAsync(
            invalid, PerceptionTestData.Authorize(invalid));
        var valid = PerceptionTestData.Intent(
            PerceptionRole.Ocr, epoch: 5, worker: worker);
        var accepted = await scheduler.ScheduleAsync(
            valid, PerceptionTestData.Authorize(valid));

        Assert.Equal(PerceptionWorkerFailure.ClockSkew, rejected.Failure);
        Assert.Equal(PerceptionJobOutcome.Completed, accepted.Outcome);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task Closing_scheduler_cancels_active_work_and_rejects_new_jobs()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(
            PerceptionRole.Ocr,
            cancellation: PerceptionCancellationCapability.CooperativeComputeCancel);
        var (adapter, transport) = PerceptionTestData.Adapter(worker, new()
        {
            BlockUntilReleased = true
        });
        var scheduler = Scheduler(
            [adapter], cpu: 1, gpu: 0, concurrency: 1);
        var active = PerceptionTestData.Intent(
            PerceptionRole.Ocr, epoch: 1, worker: worker);
        var running = scheduler.ScheduleAsync(
            active, PerceptionTestData.Authorize(active));
        await PerceptionTestData.WaitForRequestsAsync(transport, 1);

        await scheduler.DisposeAsync();
        var canceled = await running;
        var later = PerceptionTestData.Intent(
            PerceptionRole.Ocr, epoch: 2, worker: worker);
        var rejected = await scheduler.ScheduleAsync(
            later, PerceptionTestData.Authorize(later));

        Assert.Equal(PerceptionWorkerFailure.Canceled, canceled.Failure);
        Assert.Equal(PerceptionWorkerFailure.Closed, rejected.Failure);
    }

    private static PerceptionFreshnessScheduler Scheduler(
        IEnumerable<IPerceptionJobExecutor> executors,
        int cpu,
        int gpu,
        int concurrency,
        PerceptionRole? preferredRole = null) => new(
            executors,
            new()
            {
                Budget = new()
                {
                    MaximumCpuUnits = cpu,
                    MaximumGpuMemoryMiB = gpu,
                    MaximumConcurrency = concurrency
                },
                PreferredRole = preferredRole
            },
            PerceptionTestData.Clock);
}
