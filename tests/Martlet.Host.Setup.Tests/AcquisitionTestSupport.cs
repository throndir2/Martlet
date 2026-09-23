using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Martlet.HostArtifacts;
using Martlet.Core.Installation;
using Martlet.Core.Contracts;
using System.Collections.Immutable;

namespace Martlet.Host.Setup.Tests;

internal sealed class RecordingDownloadHandler : HttpMessageHandler
{
    public int Calls { get; private set; }
    public List<RecordedDownloadRequest> Requests { get; } = [];
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (_, _) => throw new InvalidOperationException("Configure an inert response.");

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Calls++;
        Requests.Add(new(
            request.RequestUri?.AbsoluteUri,
            request.Headers.Range?.ToString(),
            request.Headers.IfRange?.ToString(),
            request.Headers.Authorization?.ToString(),
            request.Headers.AcceptEncoding.Select(value => value.ToString()).ToArray(),
            request.Version,
            request.VersionPolicy));
        return await Respond(request, cancellationToken);
    }
}

internal sealed class BlockingReadStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();
    public override void SetLength(long value) =>
        throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}

internal sealed record RecordedDownloadRequest(
    string? Uri,
    string? Range,
    string? IfRange,
    string? Authorization,
    string[] AcceptEncoding,
    Version Version,
    HttpVersionPolicy VersionPolicy);

internal sealed class RecordingAcquisitionProgress : IArtifactAcquisitionProgressSink
{
    public List<ArtifactAcquisitionProgress> Values { get; } = [];

    public ValueTask ReportAsync(
        ArtifactAcquisitionProgress progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Values.Add(progress);
        return ValueTask.CompletedTask;
    }
}

internal sealed class CancelingAcquisitionProgress(
    CancellationTokenSource source,
    long cancelAtBytes) : IArtifactAcquisitionProgressSink
{
    public List<ArtifactAcquisitionProgress> Values { get; } = [];

    public ValueTask ReportAsync(
        ArtifactAcquisitionProgress progress,
        CancellationToken cancellationToken)
    {
        Values.Add(progress);
        if (progress.Phase == ArtifactAcquisitionProgressPhase.Downloading &&
            progress.PersistedBytes >= cancelAtBytes)
            source.Cancel();
        return ValueTask.CompletedTask;
    }
}

internal sealed class ThrowingAcquisitionProgress : IArtifactAcquisitionProgressSink
{
    public ValueTask ReportAsync(
        ArtifactAcquisitionProgress progress,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("PRIVATE-PROGRESS-CANARY");
}

internal sealed class RecordingFreeSpaceProbe(long availableBytes) : IArtifactFreeSpaceProbe
{
    public List<string> Paths { get; } = [];

    public long GetAvailableBytes(string absoluteDirectory)
    {
        Paths.Add(absoluteDirectory);
        return availableBytes;
    }
}

internal sealed class FaultingAcquisitionStorage(
    IArtifactAcquisitionMutationStorage inner) : IArtifactAcquisitionMutationStorage
{
    public ArtifactAcquisitionFailure? OpenFailure { get; set; }
    public ArtifactAcquisitionFailure? WriteFailure { get; set; }
    public long WriteFailureAtBytes { get; set; } = long.MaxValue;
    public ArtifactAcquisitionFailure? FinalizeFailure { get; set; }
    public int? JournalFailureAttempt { get; set; }
    public long? AvailableBytesOverride { get; set; }
    public Action? AfterPartialHash { get; set; }
    public int JournalWrites { get; private set; }
    public string RootPath => inner.RootPath;
    public ValueTask<IAsyncDisposable> AcquireLeaseAsync(ArtifactAcquisitionPaths paths, Guid ownerId,
        CancellationToken cancellationToken) => inner.AcquireLeaseAsync(paths, ownerId, cancellationToken);
    public ValueTask ValidateLeaseAsync(ArtifactAcquisitionPaths paths, CancellationToken cancellationToken) =>
        inner.ValidateLeaseAsync(paths, cancellationToken);
    public ValueTask<ArtifactAcquisitionFileSnapshot> QuarantineOwnedFinalAsync(
        ArtifactAcquisitionPaths paths, string expectedIdentity, long expectedBytes, string expectedHash,
        CancellationToken cancellationToken) =>
        inner.QuarantineOwnedFinalAsync(paths, expectedIdentity, expectedBytes, expectedHash, cancellationToken);

    public ArtifactAcquisitionPaths GetPaths(
        ArtifactAcquisitionCandidate candidate) =>
        inner.GetPaths(candidate);

    public async ValueTask<ArtifactAcquisitionStorageSnapshot> InspectAsync(
        ArtifactAcquisitionPaths paths,
        CancellationToken cancellationToken)
    {
        var value = await inner.InspectAsync(paths, cancellationToken);
        return AvailableBytesOverride is { } available
            ? new(
                available,
                value.PartialExists,
                value.PartialBytes,
                value.FinalExists,
                value.FinalBytes,
                value.JournalVersion,
                value.JournalContent,
                value.JournalPendingExists, value.PartialIdentity, value.FinalIdentity,
                value.QuarantineExists, value.QuarantineBytes, value.QuarantineIdentity, value.LeaseExists)
            : value;
    }

    public ValueTask<SetupFileSnapshot> WriteJournalAsync(
        ArtifactAcquisitionPaths paths,
        string? expectedVersion,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        JournalWrites++;
        if (JournalFailureAttempt == JournalWrites)
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.JournalIoFailure);
        return inner.WriteJournalAsync(
            paths,
            expectedVersion,
            content,
            cancellationToken);
    }

    public async ValueTask<IArtifactPartialWriter> OpenPartialAsync(
        ArtifactAcquisitionPaths paths,
        long offset,
        string? expectedIdentity,
        CancellationToken cancellationToken)
    {
        if (OpenFailure is { } failure)
            throw new ArtifactAcquisitionException(failure);
        var writer = await inner.OpenPartialAsync(
            paths,
            offset,
            expectedIdentity,
            cancellationToken);
        return new FaultingPartialWriter(
            writer,
            () => WriteFailure,
            () => WriteFailureAtBytes);
    }

    public async ValueTask<string> ComputePartialSha256Async(
        ArtifactAcquisitionPaths paths,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        var hash = await inner.ComputePartialSha256Async(
            paths,
            expectedBytes,
            cancellationToken);
        AfterPartialHash?.Invoke();
        return hash;
    }

    public ValueTask<string> ComputeFinalSha256Async(
        ArtifactAcquisitionPaths paths,
        long expectedBytes,
        CancellationToken cancellationToken) =>
        inner.ComputeFinalSha256Async(
            paths,
            expectedBytes,
            cancellationToken);

    public ValueTask FinalizeAsync(
        ArtifactAcquisitionPaths paths,
        string expectedIdentity,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        if (FinalizeFailure is { } failure)
            throw new ArtifactAcquisitionException(failure);
        return inner.FinalizeAsync(
            paths,
            expectedIdentity,
            expectedBytes,
            cancellationToken);
    }

    public ValueTask DeleteOwnedPartialAsync(
        ArtifactAcquisitionPaths paths,
        string expectedIdentity,
        long expectedBytes,
        CancellationToken cancellationToken) =>
        inner.DeleteOwnedPartialAsync(
            paths,
            expectedIdentity,
            expectedBytes,
            cancellationToken);

    private sealed class FaultingPartialWriter(
        IArtifactPartialWriter inner,
        Func<ArtifactAcquisitionFailure?> failure,
        Func<long> failAt) : IArtifactPartialWriter
    {
        public long Position => inner.Position;
        public string Identity => inner.Identity;

        public async ValueTask WriteAsync(
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken)
        {
            if (failure() is { } configured &&
                Position + bytes.Length > failAt())
            {
                var accepted = (int)Math.Max(0, failAt() - Position);
                if (accepted > 0)
                    await inner.WriteAsync(
                        bytes[..accepted],
                        cancellationToken);
                throw new ArtifactAcquisitionException(configured);
            }
            await inner.WriteAsync(bytes, cancellationToken);
        }

        public ValueTask FlushToDiskAsync(
            CancellationToken cancellationToken) =>
            inner.FlushToDiskAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

internal sealed class AcquisitionHarness : IAsyncDisposable
{
    private readonly List<IDisposable> ownedTransports = [];
    public string Root { get; }
    public ArtifactManifest Manifest { get; }
    public ArtifactAcquisitionCandidate Candidate { get; }
    public byte[] Payload { get; }
    public AcquisitionReviewFileSystem SetupFileSystem { get; }
    public SetupPlan SetupPlan { get; }
    public SetupPreview SetupPreview { get; }
    public ArtifactRightsAuthorization Rights { get; }
    public RecordingDirectoryCommitter DirectoryCommitter { get; }
    public LocalArtifactAcquisitionStorage Storage { get; }
    public RecordingDownloadHandler Handler { get; }
    public MutableTimeProvider Clock { get; }

    private AcquisitionHarness(
        string root,
        ArtifactManifest manifest,
        ArtifactAcquisitionCandidate candidate,
        byte[] payload,
        AcquisitionReviewFileSystem setupFileSystem,
        SetupPlan setupPlan,
        SetupPreview setupPreview,
        ArtifactRightsAuthorization rights,
        RecordingDirectoryCommitter directoryCommitter,
        LocalArtifactAcquisitionStorage storage,
        RecordingDownloadHandler handler,
        MutableTimeProvider clock)
    {
        Root = root;
        Manifest = manifest;
        Candidate = candidate;
        Payload = payload;
        SetupFileSystem = setupFileSystem;
        SetupPlan = setupPlan;
        SetupPreview = setupPreview;
        Rights = rights;
        DirectoryCommitter = directoryCommitter;
        Storage = storage;
        Handler = handler;
        Clock = clock;
    }

    public static async Task<AcquisitionHarness> CreateAsync(
        int payloadBytes = 196_613)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "Martlet.Host.Setup.Acquisition.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var payload = Enumerable.Range(0, payloadBytes)
            .Select(index => (byte)(index * 31 % 251))
            .ToArray();
        var manifest = AcquisitionTestData.Manifest(payload);
        var candidate = manifest.DescribeArtifact("ollama-linux-amd64");
        var clock = new MutableTimeProvider(SetupTestData.Now);
        var setupPlan = AcquisitionTestData.BoundPlan(manifest, clock.UtcNow);
        var setupFileSystem = new AcquisitionReviewFileSystem(
            Path.Combine(root, "setup-journal-v2.json"));
        var setupCoordinator = new SetupCoordinator(setupFileSystem, clock);
        var preview = await setupCoordinator.PreviewReviewAsync(setupPlan);
        var approval = preview.Approve(SetupApprovalDecision.Approve, [SetupConsentScope.LocalJournal]);
        Assert.True((await setupCoordinator.RecordReviewAsync(setupPlan, approval)).ReviewRecorded);
        var setupPreview = await setupCoordinator.PreviewReviewAsync(setupPlan);
        Assert.Equal(SetupPreviewState.ReviewRecorded, setupPreview.State);
        Assert.False(setupPlan.ExecutionAuthorized);
        var rights = new ArtifactRightsReview(
            manifest.DescribeAcquisition(["ollama-llm"], "ubuntu-24.04-x64"),
            candidate.ArtifactId,
            "fixture-rights-v1",
            Convert.ToHexStringLower(
                SHA256.HashData("authored synthetic rights evidence"u8)),
            candidate.Licenses.Select(license => new ArtifactReviewedLicense(license.Id,
                "Authored inert test bytes; explicit test-only copying permission",
                Convert.ToHexStringLower(SHA256.HashData("authored terms"u8)))), clock)
            .Authorize(ArtifactRightsDecision.Approve);
        var committer = new RecordingDirectoryCommitter();
        var storage = new LocalArtifactAcquisitionStorage(root, committer);
        var handler = new RecordingDownloadHandler();
        return new(
            root,
            manifest,
            candidate,
            payload,
            setupFileSystem,
            setupPlan,
            setupPreview,
            rights,
            committer,
            storage,
            handler,
            clock);
    }

    public ArtifactAcquisitionCoordinator Coordinator(
        IArtifactAcquisitionProgressSink? progress = null,
        IArtifactAcquisitionMutationStorage? storage = null,
        IArtifactDownloadTransport? transport = null,
        TimeSpan? transferTimeout = null,
        long reserveBytes = 0)
    {
        if (transport is null)
        {
            var owned = new HttpsArtifactDownloadTransport(Handler);
            ownedTransports.Add(owned);
            transport = owned;
        }
        return ArtifactAcquisitionCoordinator.CreateForFixture(
            SetupFileSystem,
            storage ?? Storage,
            transport,
            progress,
            Clock,
            transferTimeout: transferTimeout,
            freeSpaceReserveBytes: reserveBytes);
    }

    public async Task<(ArtifactAcquisitionPreview Preview, ArtifactAcquisitionApproval Approval)>
        ApproveAsync(ArtifactAcquisitionCoordinator coordinator)
    {
        var preview = await coordinator.PreviewAsync(
            Manifest,
            Candidate.ArtifactId,
            SetupPlan,
            SetupPreview,
            Rights);
        var approval = preview.Approve(
            ArtifactAcquisitionDecision.Approve,
            preview.Plan.RequiredConsentScopes);
        Assert.True(approval.IsApproved);
        return (preview, approval);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var transport in ownedTransports)
            transport.Dispose();
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
        return ValueTask.CompletedTask;
    }
}

internal sealed class AcquisitionReviewFileSystem(string path) : ISetupFileSystem
{
    private readonly LocalSetupFileSystem inner = new(path, new RecordingDirectoryCommitter());
    public string JournalPath => inner.JournalPath;
    public ValueTask<SetupFileSnapshot?> ReadAsync(int maximumBytes, CancellationToken cancellationToken) =>
        inner.ReadAsync(maximumBytes, cancellationToken);
    public ValueTask<SetupFileSnapshot> WriteAtomicAsync(string? expectedVersion,
        ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
        inner.WriteAtomicAsync(expectedVersion, content, cancellationToken);
    internal void AppendWhitespace() => File.AppendAllText(JournalPath, " ");
}

internal static class AcquisitionTestData
{
    internal const string EntityTag = "\"fixture-etag-v1\"";
    internal static readonly DateTimeOffset LastModified =
        SetupTestData.Now.AddDays(-1);

    internal static ArtifactManifest Manifest(byte[] payload)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "host-artifacts.v1.json");
        var document = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        document["provenance"] = "synthetic_fixture";
        var artifact = document["artifacts"]![0]!.AsObject();
        artifact["bytes"] = payload.LongLength;
        artifact["sha256"] =
            Convert.ToHexStringLower(SHA256.HashData(payload));
        return ArtifactManifestReader.Read(
            Encoding.UTF8.GetBytes(document.ToJsonString()));
    }

    internal static SetupPlan BoundPlan(ArtifactManifest manifest, DateTimeOffset now, SetupRole role = SetupRole.Llm)
    {
        var host = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var runtime = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var resource = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var owner = new InstallationOwner(InstallationOwnership.GuidedManaged,
            Guid.Parse("44444444-4444-4444-4444-444444444444"));
        var fact = new InstallationFact(PlanningState.Unknown, "supplied-not-observed");
        var gates = new[] { InstallationPrerequisite.HostQualification, InstallationPrerequisite.RuntimeCompatibility,
            InstallationPrerequisite.ArtifactEligibility, InstallationPrerequisite.LicenseReview,
            InstallationPrerequisite.LocalApproval, InstallationPrerequisite.Pairing }
            .Select(kind => new PrerequisiteFact(kind, fact)).ToImmutableArray();
        var request = new InstallationRequest
        {
            ClientHostId = host,
            Features = role == SetupRole.Llm ? [InstallationFeature.TypedConversation] : [InstallationFeature.SpokenReplies],
            Hosts = [new(host, "Unverified fixture identity", new(100, 100, 100, 100))],
            Resources = [new(resource, host, "shared-runtime", owner, new(null, null, null, null), false, fact, [])],
            Runtimes = [new(runtime, host, "ollama", "candidate", InstallationRuntimeKind.Compose,
                owner, fact, [resource], gates)],
            Destinations = [new(role == SetupRole.Llm ? InstallationRole.Llm : InstallationRole.Tts,
                runtime, role == SetupRole.Llm ? "ollama-llm" : "f5-tts",
                CapabilitySupport.Supported, fact, [new(InstallationPrerequisite.DataConsent, fact)])]
        };
        return new SetupPlanBuilder(new MutableTimeProvider(now)).Build(
            new SetupConfiguration("acquisition-fixture", [role]), request, host, SetupTestData.HostReport(), manifest);
    }

    internal static HttpResponseMessage Response(
        HttpRequestMessage request,
        byte[] payload,
        string? entityTag = EntityTag,
        DateTimeOffset? lastModified = null,
        bool ranges = true,
        bool includeLastModified = true,
        long? declaredLength = null,
        HttpStatusCode? status = null,
        Uri? effectiveUri = null)
    {
        var requestOffset = request.Headers.Range?.Ranges.Single().From ?? 0;
        var responseStatus = status ?? (requestOffset == 0
                ? HttpStatusCode.OK
                : HttpStatusCode.PartialContent);
        var bodyOffset = responseStatus == HttpStatusCode.OK
            ? 0
            : requestOffset;
        var bytes = payload[(int)bodyOffset..];
        var response = new HttpResponseMessage(responseStatus)
        {
            RequestMessage = effectiveUri is null
                ? request
                : new HttpRequestMessage(HttpMethod.Get, effectiveUri),
            Content = new ByteArrayContent(bytes)
        };
        response.Content.Headers.ContentLength =
            declaredLength ?? bytes.LongLength;
        if (includeLastModified)
            response.Content.Headers.LastModified =
                lastModified ?? LastModified;
        if (ranges)
            response.Headers.AcceptRanges.Add("bytes");
        if (entityTag is not null)
            response.Headers.ETag =
                EntityTagHeaderValue.Parse(entityTag);
        if (requestOffset > 0 &&
            responseStatus == HttpStatusCode.PartialContent)
            response.Content.Headers.ContentRange =
                new ContentRangeHeaderValue(
                    requestOffset,
                    payload.LongLength - 1,
                    payload.LongLength);
        return response;
    }
}
