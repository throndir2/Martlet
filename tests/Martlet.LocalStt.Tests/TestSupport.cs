using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Martlet.LocalStt;

namespace Martlet.LocalStt.Tests;

internal static class LocalSttTestData
{
    internal static byte[] Wave(int samples = 160)
    {
        var bytes = new byte[CanonicalWaveAudio.HeaderBytes + samples * 2];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), CanonicalWaveAudio.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), CanonicalWaveAudio.SampleRate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), (uint)(samples * 2));
        return bytes;
    }
}

internal sealed class ManualClock : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = [];
    private long ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override DateTimeOffset GetUtcNow() =>
        new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());
    public override long GetTimestamp()
    {
        lock (gate)
            return ticks;
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (gate)
        {
            timers.Add(timer);
            timer.Change(dueTime, period);
        }
        return timer;
    }

    internal void Advance(TimeSpan duration)
    {
        List<ManualTimer> due;
        lock (gate)
        {
            ticks += duration.Ticks;
            due = timers.Where(timer => timer.Due <= ticks).ToList();
            foreach (var timer in due)
                timer.Due = timer.Period == Timeout.InfiniteTimeSpan
                    ? long.MaxValue
                    : ticks + timer.Period.Ticks;
        }
        foreach (var timer in due)
            timer.Fire();
    }

    private sealed class ManualTimer(
        ManualClock clock,
        TimerCallback callback,
        object? state) : ITimer
    {
        internal long Due { get; set; } = long.MaxValue;
        internal TimeSpan Period { get; private set; }
        private bool disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.gate)
            {
                if (disposed)
                    return false;
                Due = dueTime == Timeout.InfiniteTimeSpan
                    ? long.MaxValue
                    : clock.ticks + dueTime.Ticks;
                Period = period;
                return true;
            }
        }

        internal void Fire()
        {
            if (!disposed)
                callback(state);
        }

        public void Dispose()
        {
            lock (clock.gate)
            {
                disposed = true;
                clock.timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class FakePackageVerifier : ILocalSttPackageVerifier
{
    internal PackageVerificationStatus Status { get; set; } = PackageVerificationStatus.Verified;
    internal VerifiedLocalSttPackage Package { get; set; }
    internal int Calls { get; private set; }
    internal Action? BeforeReturn { get; set; }
    internal TaskCompletionSource? Gate { get; set; }

    internal FakePackageVerifier(VerifiedLocalSttPackage package)
    {
        Package = package;
    }

    public async Task<PackageVerificationResult> VerifyForLaunchAsync(CancellationToken cancellationToken)
    {
        Calls++;
        if (Gate is not null)
            await Gate.Task.WaitAsync(cancellationToken);
        BeforeReturn?.Invoke();
        return Status == PackageVerificationStatus.Verified
            ? new PackageVerificationResult(Status, Package)
            : new PackageVerificationResult(Status);
    }
}

internal sealed class FakeWorkspaceFactory : ILocalSttWorkspaceFactory
{
    internal WorkspaceStatus Status { get; set; } = WorkspaceStatus.Ready;
    internal FakeWorkspace Workspace { get; } = new();
    internal int Calls { get; private set; }

    public Task<WorkspaceCreateResult> CreateAsync(
        Guid operationId,
        CanonicalWaveAudio audio,
        CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Status == WorkspaceStatus.Ready
            ? new WorkspaceCreateResult(Status, Workspace)
            : new WorkspaceCreateResult(Status));
    }
}

internal sealed class FakeWorkspace : ILocalSttWorkspace
{
    internal WorkspaceStatus ReadStatus { get; set; } = WorkspaceStatus.Ready;
    internal byte[] Transcript { get; set; } = Encoding.UTF8.GetBytes("fixture transcript");
    internal WorkspaceStatus CleanupStatus { get; set; } = WorkspaceStatus.Ready;
    internal int CleanupCalls { get; private set; }
    internal int ReadCalls { get; private set; }
    internal TaskCompletionSource? CleanupGate { get; set; }

    public string AudioPath => @"C:\fixture-work\input.wav";
    public string TranscriptPrefixPath => @"C:\fixture-work\transcript";
    public string WorkingDirectory => @"C:\fixture-work";

    public Task<WorkspaceReadResult> ReadTranscriptAsync(CancellationToken cancellationToken)
    {
        ReadCalls++;
        return Task.FromResult(ReadStatus == WorkspaceStatus.Ready
            ? new WorkspaceReadResult(ReadStatus, Transcript)
            : new WorkspaceReadResult(ReadStatus));
    }

    public async Task<WorkspaceStatus> CleanupAsync()
    {
        CleanupCalls++;
        if (CleanupGate is not null)
            await CleanupGate.Task;
        return CleanupStatus;
    }
}

internal sealed class FakeProcessRunner : ILocalSttProcessRunner
{
    internal LocalSttProcessStartStatus Status { get; set; } = LocalSttProcessStartStatus.Started;
    internal FakeProcess Process { get; set; } = FakeProcess.Completed();
    internal LocalSttProcessStartRequest? Request { get; private set; }
    internal int Calls { get; private set; }

    public LocalSttProcessStartResult Start(LocalSttProcessStartRequest request)
    {
        Calls++;
        Request = request;
        return Status == LocalSttProcessStartStatus.Started
            ? new(Status, Process)
            : new(Status);
    }
}

internal sealed class FakeProcess : ILocalSttProcess
{
    private readonly TaskCompletionSource<LocalSttProcessCompletion> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Id { get; init; } = 41;
    public Task<LocalSttProcessCompletion> Completion => completion.Task;
    internal bool KillSucceeds { get; set; } = true;
    internal int KillCalls { get; private set; }
    internal int DisposeCalls { get; private set; }

    internal static FakeProcess Completed(
        LocalSttProcessCompletionStatus status = LocalSttProcessCompletionStatus.Exited,
        bool treeExited = true,
        int? exitCode = 0,
        byte[]? stdout = null,
        byte[]? stderr = null)
    {
        var process = new FakeProcess();
        process.Complete(status, treeExited, exitCode, stdout, stderr);
        return process;
    }

    internal void Complete(
        LocalSttProcessCompletionStatus status = LocalSttProcessCompletionStatus.Exited,
        bool treeExited = true,
        int? exitCode = 0,
        byte[]? stdout = null,
        byte[]? stderr = null) =>
        completion.TrySetResult(new(
            status,
            treeExited,
            exitCode,
            stdout ?? [],
            stderr ?? []));

    public ValueTask<bool> KillTreeAsync(TimeSpan timeout)
    {
        KillCalls++;
        return ValueTask.FromResult(KillSucceeds);
    }

    public ValueTask DisposeAsync()
    {
        DisposeCalls++;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeEgressAuditor : ILocalSttEgressAuditor
{
    internal LocalSttEgressBeginStatus Status { get; set; } = LocalSttEgressBeginStatus.Ready;
    internal FakeEgressSession Session { get; } = new();
    internal LocalSttEgressAuditRequest? Request { get; private set; }
    internal int Calls { get; private set; }
    internal TaskCompletionSource? BeginGate { get; set; }
    internal bool IgnoreBeginCancellation { get; set; }
    internal Action? BeforeReturn { get; set; }

    public async ValueTask<LocalSttEgressBeginResult> BeginAsync(
        LocalSttEgressAuditRequest request,
        CancellationToken cancellationToken)
    {
        Calls++;
        Request = request;
        if (BeginGate is not null)
            await BeginGate.Task.WaitAsync(IgnoreBeginCancellation ? CancellationToken.None : cancellationToken);
        Session.OperationId = request.OperationId;
        Session.Policy = request.Policy;
        BeforeReturn?.Invoke();
        return Status == LocalSttEgressBeginStatus.Ready
            ? new LocalSttEgressBeginResult(Status, Session)
            : new LocalSttEgressBeginResult(Status);
    }
}

internal sealed class FakeEgressSession : ILocalSttEgressAuditSession
{
    public bool DenialEstablishedBeforeLaunch { get; set; } = true;
    internal bool BindSucceeds { get; set; } = true;
    internal Guid OperationId { get; set; }
    internal LocalSttNetworkPolicy Policy { get; set; } = LocalSttNetworkPolicy.NoNetwork;
    internal int ProcessId { get; private set; }
    internal int LoopbackAttempts { get; set; }
    internal int NonLoopbackAttempts { get; set; }
    internal bool ObservedUntilTreeExit { get; set; } = true;
    internal bool SystemPolicyMutated { get; set; }
    internal bool RawEndpointDataRetained { get; set; }
    internal int BindCalls { get; private set; }
    internal int CompleteCalls { get; private set; }
    internal int DisposeCalls { get; private set; }
    internal TaskCompletionSource? BindGate { get; set; }
    internal bool BoundToProcessTree { get; private set; }
    internal TaskCompletionSource? DisposeGate { get; set; }
    internal bool FailDisposal { get; set; }
    internal Action? BeforeComplete { get; set; }

    public async ValueTask<bool> BindProcessTreeAsync(int processId, CancellationToken cancellationToken)
    {
        BindCalls++;
        ProcessId = processId;
        if (BindGate is not null)
            await BindGate.Task.WaitAsync(cancellationToken);
        BoundToProcessTree = BindSucceeds;
        return BindSucceeds;
    }

    public ValueTask<LocalSttEgressAuditReport> CompleteAsync(CancellationToken cancellationToken)
    {
        CompleteCalls++;
        BeforeComplete?.Invoke();
        return ValueTask.FromResult(new LocalSttEgressAuditReport(
            OperationId,
            ProcessId,
            Policy,
            DenialEstablishedBeforeLaunch,
            BoundToProcessTree,
            ObservedUntilTreeExit,
            SystemPolicyMutated,
            RawEndpointDataRetained,
            LoopbackAttempts,
            NonLoopbackAttempts));
    }

    public async ValueTask DisposeAsync()
    {
        DisposeCalls++;
        if (DisposeGate is not null)
            await DisposeGate.Task;
        if (FailDisposal)
            throw new IOException("Injected owned audit disposal failure.");
    }
}

internal sealed class AdapterHarness : IAsyncDisposable
{
    internal ManualClock Clock { get; } = new();
    internal CanonicalWaveAudio Audio { get; } =
        CanonicalWaveAudio.FromWave(LocalSttTestData.Wave());
    internal VerifiedLocalSttPackage Package { get; }
    internal FakePackageVerifier Verifier { get; }
    internal FakeWorkspaceFactory Workspaces { get; } = new();
    internal FakeProcessRunner Processes { get; } = new();
    internal FakeEgressAuditor Egress { get; } = new();
    internal LocalSttAdapter Adapter { get; }

    internal AdapterHarness()
    {
        Package = new(
            LocalSttPackageManifest.Current,
            @"C:\fixture-package\runtime\whisper-cli.exe",
            @"C:\fixture-package\models\ggml-base.en.bin",
            @"C:\fixture-package\runtime");
        Verifier = new(Package);
        Adapter = new(
            Verifier,
            Processes,
            Egress,
            Workspaces,
            Clock);
    }

    internal LocalSttRequest Request(TimeSpan? lifetime = null)
    {
        var now = Clock.GetUtcNow();
        return new(Guid.NewGuid(), now.Add(lifetime ?? TimeSpan.FromSeconds(20)));
    }

    internal LocalAudioAuthorization Authorization(
        LocalSttRequest request,
        string? modelId = null,
        string? language = null,
        string? audioSha256 = null,
        bool allow = true,
        bool rightsReviewed = true,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? requestDeadline = null,
        string? packageId = null,
        string? manifestSha256 = null,
        string? modelSha256 = null,
        Guid? operationId = null,
        int? audioBytes = null,
        bool? allowProcess = null,
        bool? allowFile = null,
        bool? deniedEgress = null)
    {
        return new(
            operationId ?? request.OperationId,
            packageId ?? Package.PackageId,
            manifestSha256 ?? Package.ManifestSha256,
            modelId ?? Package.ModelId,
            modelSha256 ?? Package.ModelSha256,
            language ?? Package.Language,
            audioSha256 ?? Audio.Sha256,
            audioBytes ?? Audio.ByteLength,
            expiresAt ?? request.Deadline,
            allow,
            allowFile ?? allow,
            deniedEgress ?? allow,
            rightsReviewed,
            requestDeadline ?? request.Deadline,
            allowProcess ?? allow);
    }

    internal async Task WaitForProcessStartAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Processes.Calls == 0)
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The fixture process was not started.");
            await Task.Yield();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Audio.Dispose();
        await Adapter.DisposeAsync();
    }
}

internal sealed class TrackingStream : MemoryStream
{
    internal bool WasDisposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        WasDisposed = true;
        base.Dispose(disposing);
    }
}

internal sealed class RejectingPathInspector : ILocalPathInspector
{
    internal bool RejectAsReparse { get; set; }
    internal bool RejectAsAccessDenied { get; set; }

    public void AssertSafeExisting(string path, bool directory)
    {
        if (RejectAsAccessDenied)
            throw new UnauthorizedAccessException();
        if (RejectAsReparse)
            throw new LocalPathException();
        new PhysicalLocalPathInspector().AssertSafeExisting(path, directory);
    }
}
