using Martlet.Perception;

namespace Martlet.Perception.Tests;

public sealed class AdversarialTests
{
    [Fact]
    public async Task Oversized_vlm_output_is_rejected_without_truncation()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(
            PerceptionRole.VisualQuestionAnswering);
        var transport = MutatingTransport.Create(worker, response =>
        {
            var observation = response.WorkerResponse.Observation!;
            return response with
            {
                WorkerResponse = response.WorkerResponse with
                {
                    Observation = observation with
                    {
                        Vlm = observation.Vlm! with
                        {
                            Answer = new string('x', 4097)
                        }
                    }
                }
            };
        });
        var adapter = Adapter(transport, worker);
        var request = PerceptionTestData.Intent(
            PerceptionRole.VisualQuestionAnswering, worker: worker);

        var result = await adapter.ExecuteAsync(
            request, PerceptionTestData.Authorize(request));

        Assert.Equal(PerceptionWorkerFailure.InvalidData, result.Failure);
        Assert.Null(result.Observation);
        Assert.True(result.OutputDiscarded);
    }

    [Fact]
    public async Task Invalid_confidence_is_unknown_output_not_invented_truth()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(
            PerceptionRole.VisualQuestionAnswering);
        var transport = MutatingTransport.Create(worker, response =>
        {
            var observation = response.WorkerResponse.Observation!;
            return response with
            {
                WorkerResponse = response.WorkerResponse with
                {
                    Observation = observation with
                    {
                        Vlm = observation.Vlm! with
                        {
                            Confidence = new()
                            {
                                Kind = PerceptionConfidenceKind.Calibrated,
                                Value = double.NaN
                            }
                        }
                    }
                }
            };
        });
        var adapter = Adapter(transport, worker);
        var request = PerceptionTestData.Intent(
            PerceptionRole.VisualQuestionAnswering, worker: worker);

        var result = await adapter.ExecuteAsync(
            request, PerceptionTestData.Authorize(request));

        Assert.Equal(PerceptionWorkerFailure.UnknownOutput, result.Failure);
        Assert.Null(result.Observation);
    }

    [Fact]
    public async Task Nonsequential_ocr_regions_are_rejected()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(
            PerceptionRole.Ocr);
        var transport = MutatingTransport.Create(worker, response =>
        {
            var observation = response.WorkerResponse.Observation!;
            var original = observation.Ocr!.Regions[0];
            return response with
            {
                WorkerResponse = response.WorkerResponse with
                {
                    Observation = observation with
                    {
                        Ocr = observation.Ocr with
                        {
                            Regions = [original with { Index = 4 }]
                        }
                    }
                }
            };
        });
        var adapter = Adapter(transport, worker);
        var request = PerceptionTestData.Intent(
            PerceptionRole.Ocr, worker: worker);

        var result = await adapter.ExecuteAsync(
            request, PerceptionTestData.Authorize(request));

        Assert.Equal(PerceptionWorkerFailure.UnknownOutput, result.Failure);
        Assert.Null(result.Observation);
    }

    [Fact]
    public async Task Accepted_output_is_copied_from_mutable_worker_collection()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(
            PerceptionRole.Ocr);
        List<PerceptionTextRegion>? supplied = null;
        var transport = MutatingTransport.Create(worker, response =>
        {
            var observation = response.WorkerResponse.Observation!;
            supplied = [observation.Ocr!.Regions[0]];
            return response with
            {
                WorkerResponse = response.WorkerResponse with
                {
                    Observation = observation with
                    {
                        Ocr = observation.Ocr with { Regions = supplied }
                    }
                }
            };
        });
        var adapter = Adapter(transport, worker);
        var request = PerceptionTestData.Intent(
            PerceptionRole.Ocr, worker: worker);

        var result = await adapter.ExecuteAsync(
            request, PerceptionTestData.Authorize(request));
        supplied!.Clear();

        Assert.Equal(PerceptionJobOutcome.Completed, result.Outcome);
        Assert.Single(result.Observation!.Ocr!.Regions);
    }

    [Fact]
    public async Task Stronger_cancel_ack_than_identity_is_rejected()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(
            PerceptionRole.Ocr);
        var inner = new DeterministicPerceptionWorkerTransport(
            PerceptionTestData.Binding(),
            worker,
            new()
            {
                BlockUntilReleased = true,
                IgnoreCancellation = true
            },
            PerceptionTestData.Clock);
        var transport = new MutatingTransport(
            inner,
            response => response,
            request => new()
            {
                Ids = request.Ids,
                ActionId = request.ActionId,
                Epoch = request.Epoch,
                LocalDiscardAcknowledged = true,
                ComputeCancellation =
                    PerceptionCancellationCapability.CooperativeComputeCancel,
                WorkerMayContinue = false
            });
        var adapter = Adapter(transport, worker);
        var request = PerceptionTestData.Intent(
            PerceptionRole.Ocr, worker: worker);
        using var stop = new CancellationTokenSource();
        var running = adapter.ExecuteAsync(
            request,
            PerceptionTestData.Authorize(request),
            stop.Token).AsTask();
        await PerceptionTestData.WaitForRequestsAsync(inner, 1);

        await stop.CancelAsync();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
        inner.Release();

        Assert.Equal(PerceptionJobOutcome.Canceled, result.Outcome);
        Assert.Null(result.ComputeCancellation);
        Assert.True(result.WorkerMayContinue);
    }

    [Fact]
    public async Task Noncooperative_terminal_cancel_cannot_claim_compute_stopped()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(
            PerceptionRole.Ocr);
        var transport = MutatingTransport.Create(worker, response => response with
        {
            WorkerResponse = response.WorkerResponse with
            {
                Outcome = PerceptionWorkerOutcome.Canceled,
                Observation = null,
                Error = null,
                ComputeCancellation = PerceptionCancellationCapability.DiscardOnly,
                WorkerMayContinue = false
            }
        });
        var adapter = Adapter(transport, worker);
        var request = PerceptionTestData.Intent(
            PerceptionRole.Ocr, worker: worker);

        var result = await adapter.ExecuteAsync(
            request, PerceptionTestData.Authorize(request));

        Assert.Equal(PerceptionWorkerFailure.ProtocolViolation, result.Failure);
        Assert.True(result.WorkerMayContinue);
        Assert.Null(result.Observation);
    }

    [Fact]
    public async Task Malformed_nested_output_conservatively_quarantines_compute()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(
            PerceptionRole.Ocr);
        var transport = MutatingTransport.Create(worker, response =>
            response with
            {
                WorkerResponse = response.WorkerResponse with
                {
                    Observation = response.WorkerResponse.Observation! with
                    {
                        Provenance = null!
                    }
                }
            });
        var adapter = Adapter(transport, worker);
        var request = PerceptionTestData.Intent(
            PerceptionRole.Ocr, worker: worker);

        var result = await adapter.ExecuteAsync(
            request, PerceptionTestData.Authorize(request));

        Assert.Equal(PerceptionWorkerFailure.ProtocolViolation, result.Failure);
        Assert.True(result.WorkerMayContinue);
    }

    [Fact]
    public void Cooperative_fixture_rejects_ignore_cancellation_lie()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(
            PerceptionRole.Ocr,
            cancellation: PerceptionCancellationCapability.CooperativeComputeCancel);

        PerceptionTestData.Failure(
            PerceptionWorkerFailure.InvalidData,
            () => new DeterministicPerceptionWorkerTransport(
                PerceptionTestData.Binding(),
                worker,
                new() { IgnoreCancellation = true },
                PerceptionTestData.Clock));
    }

    private static PerceptionGatewayClientAdapter Adapter(
        IAuthenticatedPerceptionWorkerTransport transport,
        PerceptionWorkerIdentity worker) => new(
            transport,
            PerceptionTestData.Destination,
            PerceptionTestData.Host,
            worker,
            PerceptionTestData.Clock);

    private sealed class MutatingTransport(
        DeterministicPerceptionWorkerTransport inner,
        Func<PerceptionGatewayResponse, PerceptionGatewayResponse> mutate,
        Func<PerceptionCancelRequest, PerceptionCancelResponse>? cancel = null)
        : IAuthenticatedPerceptionWorkerTransport
    {
        public PerceptionGatewayBinding Binding => inner.Binding;

        internal static MutatingTransport Create(
            PerceptionWorkerIdentity worker,
            Func<PerceptionGatewayResponse, PerceptionGatewayResponse> mutate) =>
            new(
                new DeterministicPerceptionWorkerTransport(
                    PerceptionTestData.Binding(),
                    worker,
                    clock: PerceptionTestData.Clock),
                mutate);

        public async ValueTask<PerceptionGatewayResponse> ExecuteAsync(
            PerceptionWorkerRequest request,
            CancellationToken cancellationToken) =>
            mutate(await inner.ExecuteAsync(request, cancellationToken));

        public ValueTask<PerceptionCancelResponse> CancelAsync(
            PerceptionCancelRequest request,
            CancellationToken cancellationToken) =>
            cancel is null
                ? inner.CancelAsync(request, cancellationToken)
                : ValueTask.FromResult(cancel(request));
    }
}
