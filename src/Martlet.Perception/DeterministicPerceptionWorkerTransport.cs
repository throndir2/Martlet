namespace Martlet.Perception;

public enum DeterministicPerceptionFault
{
    None,
    AuthenticationFailed,
    HostUnavailable,
    DeadlineExceeded,
    RedirectRejected,
    ProtocolViolation,
    NullGatewayResponse,
    NullWorkerResponse,
    WrongDestination,
    WrongHost,
    WrongGatewayRole,
    WrongIdentity,
    WrongEpoch,
    UnknownOutcome,
    UnknownOutput,
    StaleResult,
    MismatchedProvenance,
    ModelNotReady,
    ResourceExhausted,
    WorkerFailure
}

public sealed record DeterministicPerceptionWorkerOptions
{
    public override string ToString() =>
        $"Deterministic perception fixture options {{ Fault = {Fault}, content = omitted }}";

    public DeterministicPerceptionFault Fault { get; init; }
    public TimeSpan Delay { get; init; }
    public bool BlockUntilReleased { get; init; }
    public bool IgnoreCancellation { get; init; }
    public string OcrText { get; init; } = "Synthetic fixture text";
    public string VlmAnswer { get; init; } = "The selected-window fixture shows a synthetic menu.";
    public string VlmUncertainty { get; init; } =
        "Fixture output has no model-accuracy meaning.";

    internal void Validate()
    {
        PerceptionWorkerGuard.Defined(Fault);
        PerceptionWorkerGuard.Require(Delay >= TimeSpan.Zero &&
            Delay <= PerceptionProtocol.MaximumJobDuration);
        PerceptionWorkerGuard.Utf8Text(
            OcrText,
            PerceptionProtocol.MaximumRegionTextCharacters,
            PerceptionProtocol.MaximumRegionTextUtf8Bytes);
        PerceptionWorkerGuard.Utf8Text(VlmAnswer, 4096, 8192);
        PerceptionWorkerGuard.Utf8Text(VlmUncertainty, 512, 1024);
    }
}

public static class DeterministicPerceptionFixtureIdentity
{
    private const string Revision = "1111111111111111111111111111111111111111";

    public static PerceptionWorkerIdentity Create(
        PerceptionRole role,
        int cpuUnits = 1,
        int gpuMemoryMiB = 0,
        PerceptionCancellationCapability cancellation =
            PerceptionCancellationCapability.DiscardOnly)
    {
        PerceptionWorkerGuard.Defined(role);
        PerceptionWorkerGuard.Defined(cancellation);
        var artifacts = new List<PerceptionArtifactIdentity>
        {
            Artifact(PerceptionArtifactRole.RuntimeImage, "fixture-runtime", '1'),
            Artifact(PerceptionArtifactRole.ModelWeights, "fixture-model", '2'),
            Artifact(PerceptionArtifactRole.ModelConfiguration, "fixture-config", '3')
        };
        if (role == PerceptionRole.VisualQuestionAnswering)
        {
            artifacts.Add(Artifact(PerceptionArtifactRole.Tokenizer, "fixture-tokenizer", '4'));
            artifacts.Add(Artifact(PerceptionArtifactRole.Projector, "fixture-projector", '5'));
        }

        var roleId = role == PerceptionRole.Ocr ? "ocr" : "vlm";
        return new(
            $"martlet-perception-{roleId}-fixture",
            PerceptionEvidenceKind.SyntheticFixture,
            role,
            new()
            {
                WorkerBuildId = $"martlet-perception-{roleId}-fixture",
                WorkerBuildRevision = Revision,
                RuntimeId = "managed-fixture",
                RuntimeVersion = "1.0.0-fixture",
                RuntimeRevision = Revision,
                OperatingSystemId = "fixture-os",
                OperatingSystemVersion = "1.0.0-fixture"
            },
            new()
            {
                Role = role,
                AdapterId = $"martlet-{roleId}-fixture-adapter",
                AdapterVersion = "1.0.0-fixture",
                ModelId = $"synthetic-{roleId}-fixture",
                ModelRevision = Revision,
                ModelSha256 = new string(role == PerceptionRole.Ocr ? '6' : '7', 64),
                LicenseId = "fixture-only"
            },
            artifacts,
            new()
            {
                MaximumInputBytes = PerceptionProtocol.MaximumImageBytes,
                MaximumWidth = PerceptionProtocol.MaximumImageWidth,
                MaximumHeight = PerceptionProtocol.MaximumImageHeight,
                MaximumPixels = PerceptionProtocol.MaximumImagePixels,
                MaximumOutputUtf8Bytes = PerceptionProtocol.MaximumOutputUtf8Bytes,
                MaximumConcurrency = 1
            },
            new()
            {
                CpuUnits = cpuUnits,
                GpuMemoryMiB = gpuMemoryMiB
            },
            cancellation);
    }

    private static PerceptionArtifactIdentity Artifact(
        PerceptionArtifactRole role,
        string id,
        char digest) => new()
        {
            Role = role,
            ArtifactId = id,
            Revision = Revision,
            Sha256 = new string(digest, 64),
            Bytes = 1,
            LicenseId = "fixture-only"
        };
}

public sealed class DeterministicPerceptionWorkerTransport :
    IAuthenticatedPerceptionWorkerTransport
{
    private readonly object gate = new();
    private readonly PerceptionWorkerIdentity identity;
    private readonly DeterministicPerceptionWorkerOptions options;
    private readonly TimeProvider clock;
    private readonly TaskCompletionSource release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<PerceptionWorkerRequest> requests = [];
    private readonly List<PerceptionCancelRequest> cancellations = [];

    public DeterministicPerceptionWorkerTransport(
        PerceptionGatewayBinding binding,
        PerceptionWorkerIdentity identity,
        DeterministicPerceptionWorkerOptions? options = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(identity);
        binding.Validate();
        PerceptionWorkerGuard.Require(identity.Evidence ==
            PerceptionEvidenceKind.SyntheticFixture);
        this.options = options ?? new();
        this.options.Validate();
        PerceptionWorkerGuard.Require(
            !this.options.IgnoreCancellation ||
            identity.Cancellation !=
                PerceptionCancellationCapability.CooperativeComputeCancel);
        Binding = binding;
        this.identity = identity;
        this.clock = clock ?? TimeProvider.System;
    }

    public PerceptionGatewayBinding Binding { get; }

    public IReadOnlyList<PerceptionWorkerRequest> Requests
    {
        get
        {
            lock (gate)
                return Array.AsReadOnly(requests.ToArray());
        }
    }

    public IReadOnlyList<PerceptionCancelRequest> Cancellations
    {
        get
        {
            lock (gate)
                return Array.AsReadOnly(cancellations.ToArray());
        }
    }

    public void Release() => release.TrySetResult();

    public async ValueTask<PerceptionGatewayResponse> ExecuteAsync(
        PerceptionWorkerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        lock (gate)
            requests.Add(request);

        ThrowTransportFault();
        var waitToken = options.IgnoreCancellation
            ? CancellationToken.None
            : cancellationToken;
        if (options.BlockUntilReleased)
            await release.Task.WaitAsync(waitToken).ConfigureAwait(false);
        if (options.Delay > TimeSpan.Zero)
            await Task.Delay(options.Delay, waitToken).ConfigureAwait(false);

        var workerResponse = CreateResponse(request);
        if (options.Fault == DeterministicPerceptionFault.NullGatewayResponse)
            return null!;
        if (options.Fault == DeterministicPerceptionFault.NullWorkerResponse)
            workerResponse = null!;

        return new()
        {
            DestinationId = options.Fault == DeterministicPerceptionFault.WrongDestination
                ? "other-destination"
                : Binding.DestinationId,
            HostId = options.Fault == DeterministicPerceptionFault.WrongHost
                ? "other-host"
                : Binding.HostId,
            AuthenticatedRole = options.Fault ==
                DeterministicPerceptionFault.WrongGatewayRole
                    ? (PerceptionGatewayRole)999
                    : Binding.Role,
            WorkerResponse = workerResponse
        };
    }

    public ValueTask<PerceptionCancelResponse> CancelAsync(
        PerceptionCancelRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
            cancellations.Add(request);
        return ValueTask.FromResult(new PerceptionCancelResponse
        {
            Ids = request.Ids,
            ActionId = request.ActionId,
            Epoch = request.Epoch,
            LocalDiscardAcknowledged = true,
            ComputeCancellation = identity.Cancellation,
            WorkerMayContinue = identity.Cancellation !=
                PerceptionCancellationCapability.CooperativeComputeCancel
        });
    }

    private void ThrowTransportFault()
    {
        var failure = options.Fault switch
        {
            DeterministicPerceptionFault.AuthenticationFailed =>
                PerceptionTransportFailure.AuthenticationFailed,
            DeterministicPerceptionFault.HostUnavailable =>
                PerceptionTransportFailure.HostUnavailable,
            DeterministicPerceptionFault.DeadlineExceeded =>
                PerceptionTransportFailure.DeadlineExceeded,
            DeterministicPerceptionFault.RedirectRejected =>
                PerceptionTransportFailure.RedirectRejected,
            DeterministicPerceptionFault.ProtocolViolation =>
                PerceptionTransportFailure.ProtocolViolation,
            _ => (PerceptionTransportFailure?)null
        };
        if (failure is not null)
            throw new PerceptionTransportException(failure.Value);
    }

    private PerceptionWorkerResponse CreateResponse(PerceptionWorkerRequest request)
    {
        if (options.Fault is
            DeterministicPerceptionFault.ModelNotReady or
            DeterministicPerceptionFault.ResourceExhausted or
            DeterministicPerceptionFault.WorkerFailure)
            return FailedResponse(request);

        var processedAt = clock.GetUtcNow();
        if (processedAt < request.Frame.CapturedAtUtc)
            processedAt = request.Frame.CapturedAtUtc;
        if (options.Fault == DeterministicPerceptionFault.StaleResult)
            processedAt = request.Frame.CapturedAtUtc;
        var freshExpiry = request.Frame.CapturedAtUtc + request.MaximumFrameAge;
        if (freshExpiry > request.DeadlineUtc)
            freshExpiry = request.DeadlineUtc;
        var expiresAt = options.Fault == DeterministicPerceptionFault.StaleResult
            ? processedAt.AddMilliseconds(1)
            : freshExpiry;
        var provenance = new PerceptionObservationProvenance
        {
            FrameId = options.Fault ==
                DeterministicPerceptionFault.MismatchedProvenance
                    ? Guid.NewGuid()
                    : request.Frame.FrameId,
            CaptureEpoch = request.Frame.CaptureEpoch,
            SelectionId = request.Frame.Source.SelectionId,
            SourceRevision = request.Frame.Source.SourceRevision,
            CapturePermissionRevision =
                request.Frame.Source.CapturePermissionRevision,
            FrameSha256 = request.Frame.Content.Sha256,
            CapturedAtUtc = request.Frame.CapturedAtUtc,
            ProcessedAtUtc = processedAt,
            DestinationId = request.DestinationId,
            HostId = Binding.HostId,
            WorkerId = identity.WorkerId,
            Evidence = identity.Evidence,
            Role = request.Task.Role,
            ModelId = identity.Model.ModelId,
            ModelRevision = identity.Model.ModelRevision,
            ModelSha256 = identity.Model.ModelSha256
        };
        var observation = CreateObservation(request, provenance, expiresAt);
        var worker = options.Fault == DeterministicPerceptionFault.WrongIdentity
            ? DeterministicPerceptionFixtureIdentity.Create(
                request.Task.Role,
                cpuUnits: identity.Resources.CpuUnits == 64
                    ? 63
                    : identity.Resources.CpuUnits + 1,
                gpuMemoryMiB: identity.Resources.GpuMemoryMiB,
                cancellation: identity.Cancellation)
            : identity;
        return new()
        {
            Ids = request.Ids,
            ActionId = request.ActionId,
            Epoch = options.Fault == DeterministicPerceptionFault.WrongEpoch
                ? request.Epoch - 1
                : request.Epoch,
            Worker = worker,
            Outcome = options.Fault == DeterministicPerceptionFault.UnknownOutcome
                ? (PerceptionWorkerOutcome)999
                : PerceptionWorkerOutcome.Completed,
            Observation = observation,
            WorkerMayContinue = false
        };
    }

    private PerceptionObservation CreateObservation(
        PerceptionWorkerRequest request,
        PerceptionObservationProvenance provenance,
        DateTimeOffset expiresAt)
    {
        if (options.Fault == DeterministicPerceptionFault.UnknownOutput)
        {
            return new()
            {
                Role = request.Task.Role,
                Provenance = provenance,
                ExpiresAtUtc = expiresAt
            };
        }

        if (request.Task.Role == PerceptionRole.Ocr)
        {
            return new()
            {
                Role = request.Task.Role,
                Provenance = provenance,
                ExpiresAtUtc = expiresAt,
                Ocr = new()
                {
                    Regions =
                    [
                        new PerceptionTextRegion
                        {
                            Index = 0,
                            Text = options.OcrText,
                            Bounds = new()
                            {
                                Left = 500,
                                Top = 500,
                                Right = 9500,
                                Bottom = 2000
                            },
                            Confidence = PerceptionConfidence.Uncalibrated(0.75)
                        }
                    ],
                    DetectedLanguage = "en"
                }
            };
        }

        return new()
        {
            Role = request.Task.Role,
            Provenance = provenance,
            ExpiresAtUtc = expiresAt,
            Vlm = new()
            {
                Answer = options.VlmAnswer,
                Uncertainty = options.VlmUncertainty,
                Confidence = PerceptionConfidence.Uncalibrated(0.6)
            }
        };
    }

    private PerceptionWorkerResponse FailedResponse(PerceptionWorkerRequest request)
    {
        var code = options.Fault switch
        {
            DeterministicPerceptionFault.ModelNotReady =>
                PerceptionWorkerErrorCode.ModelNotReady,
            DeterministicPerceptionFault.ResourceExhausted =>
                PerceptionWorkerErrorCode.ResourceExhausted,
            _ => PerceptionWorkerErrorCode.InternalFailure
        };
        return new()
        {
            Ids = request.Ids,
            ActionId = request.ActionId,
            Epoch = request.Epoch,
            Worker = identity,
            Outcome = PerceptionWorkerOutcome.Failed,
            Error = new()
            {
                Code = code,
                Summary = "The deterministic fixture emitted the configured failure.",
                RemedyCode = "perception.fixture.failure"
            },
            WorkerMayContinue = false
        };
    }
}
