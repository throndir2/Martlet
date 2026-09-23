using System.Collections.Immutable;
using System.Security.Cryptography;
using Martlet.Host.Inventory;
using Martlet.HostArtifacts;

namespace Martlet.Host.Setup.Tests;

internal sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;
    public override DateTimeOffset GetUtcNow() => UtcNow;
}

internal sealed class FakeSetupFileSystem : ISetupFileSystem
{
    private byte[]? content;
    private string? version;

    public string JournalPath { get; }
    public int ReadCount { get; private set; }
    public int WriteAttempts { get; private set; }
    public int SuccessfulWrites { get; private set; }
    public int? FailOnWriteAttempt { get; set; }
    public List<string> TouchedPaths { get; } = [];
    public byte[]? Content => content?.ToArray();
    public string? Version => version;

    public FakeSetupFileSystem(string journalPath = @"C:\inert-h05a\setup-journal-v1.json") =>
        JournalPath = journalPath;

    public ValueTask<SetupFileSnapshot?> ReadAsync(int maximumBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        if (content is null) return ValueTask.FromResult<SetupFileSnapshot?>(null);
        if (content.Length > maximumBytes) throw new SetupException(SetupFailure.JournalTooLarge);
        return ValueTask.FromResult<SetupFileSnapshot?>(new SetupFileSnapshot(content, version!));
    }

    public ValueTask<SetupFileSnapshot> WriteAtomicAsync(
        string? expectedVersion,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteAttempts++;
        TouchedPaths.Add(JournalPath);
        if (FailOnWriteAttempt == WriteAttempts)
            throw new SetupException(SetupFailure.JournalIoFailure);
        if (!string.Equals(version, expectedVersion, StringComparison.Ordinal))
            throw new SetupException(SetupFailure.JournalConcurrentChange);
        content = bytes.ToArray();
        version = Convert.ToHexStringLower(SHA256.HashData(content));
        SuccessfulWrites++;
        return ValueTask.FromResult(new SetupFileSnapshot(content, version));
    }

    public void SetRaw(ReadOnlySpan<byte> bytes)
    {
        content = bytes.ToArray();
        version = Convert.ToHexStringLower(SHA256.HashData(content));
    }

    public void AppendWhitespace()
    {
        if (content is null) throw new InvalidOperationException();
        SetRaw([.. content, (byte)' ']);
    }
}

internal sealed class CancelingStepExecutor(CancellationTokenSource source) : ISetupStepExecutor
{
    public int Calls { get; private set; }

    public ValueTask<SetupExecutionResult> ExecuteAsync(
        SetupCommand command,
        CancellationToken cancellationToken)
    {
        Calls++;
        source.Cancel();
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("The supplied token should have canceled.");
    }
}

internal sealed class ExpiringStepExecutor(
    FakeStateProbe probe,
    MutableTimeProvider clock,
    DateTimeOffset expiresAtUtc) : ISetupStepExecutor
{
    public List<string> Calls { get; } = [];

    public ValueTask<SetupExecutionResult> ExecuteAsync(
        SetupCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(command.Executable);
        probe.SetSatisfied(Path.GetFileName(command.Executable));
        if (Calls.Count == 1) clock.UtcNow = expiresAtUtc;
        return ValueTask.FromResult(SetupExecutionResult.Completed());
    }
}

internal sealed class RecordingDirectoryCommitter : ISetupDirectoryCommitter
{
    public List<string> Calls { get; } = [];
    public bool Fail { get; set; }

    public void Commit(string absoluteDirectory)
    {
        Calls.Add(absoluteDirectory);
        if (Fail) throw new SetupException(SetupFailure.JournalIoFailure);
    }
}

internal sealed class FakeStateProbe : ISetupStateProbe
{
    private readonly Dictionary<string, SetupStepObservation> observations = new(StringComparer.Ordinal);
    public List<string> Calls { get; } = [];

    public ValueTask<SetupStepObservation> ObserveAsync(SetupStep step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(step.Id);
        return ValueTask.FromResult(observations.TryGetValue(step.Id, out var observation)
            ? observation
            : Pending(step.Id));
    }

    public void SetPending(string stepId) => observations[stepId] = Pending(stepId);
    public void SetSatisfied(string stepId, string revision = "1") =>
        observations[stepId] = new(SetupObservationState.Satisfied, "expected-state",
            FingerprintBuilder.Create("external", stepId, revision));
    public void SetConflict(string stepId, string revision = "conflict") =>
        observations[stepId] = new(SetupObservationState.Conflict, "conflicting-state",
            FingerprintBuilder.Create("external", stepId, revision));
    public void SetUnknown(string stepId) =>
        observations[stepId] = new(SetupObservationState.Unknown, "unavailable");

    private static SetupStepObservation Pending(string stepId) =>
        new(SetupObservationState.Pending, "pending-" + stepId);
}

internal sealed class FakeStepExecutor(FakeStateProbe probe) : ISetupStepExecutor
{
    private readonly Dictionary<string, Queue<SetupExecutionResult>> results = new(StringComparer.Ordinal);
    public List<string> Calls { get; } = [];

    public void Enqueue(string executable, params SetupExecutionResult[] values) =>
        results[executable] = new Queue<SetupExecutionResult>(values);

    public ValueTask<SetupExecutionResult> ExecuteAsync(SetupCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(command.Executable);
        var result = results.TryGetValue(command.Executable, out var queue) && queue.TryDequeue(out var configured)
            ? configured
            : SetupExecutionResult.Completed();
        if (result.Outcome == SetupExecutionOutcome.Completed)
            probe.SetSatisfied(Path.GetFileName(command.Executable));
        return ValueTask.FromResult(result);
    }
}

internal static class SetupTestData
{
    internal static readonly DateTimeOffset Now = new(2026, 9, 22, 7, 0, 0, TimeSpan.Zero);

    internal static HostReport HostReport(
        string fixture = "prerequisites",
        DoctorScope scope = DoctorScope.Prerequisites)
    {
        var name = fixture == "port-in-use" ? "inventory" : fixture;
        var report = HostJson.Deserialize(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "fixtures", name + ".v1.json")));
        if (fixture == "port-in-use")
            report = report with { Probes = report.Probes.Select(p => p.Id == ProbeId.GatewayPort
                ? p with { Code = FindingCode.HOST_PORT_IN_USE, Evidence = new Evidence { Port = new(7443, true) } }
                : p).ToImmutableArray() };
        Assert.Equal(scope, report.Scope);
        report.Validate();
        return report;
    }

    internal static InspectionReport ArtifactReport(string? role = null)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "host-artifacts.v1.json");
        return ArtifactInspector.Inspect(ArtifactManifestReader.Read(File.ReadAllBytes(path)),
            role, "ubuntu-24.04-x64");
    }

    internal static SetupPlan ReviewedPlan(
        DateTimeOffset? createdAt = null,
        string configurationRevision = "config-1",
        string hostRevision = "host-1",
        string artifactRevision = "artifact-1",
        SetupPrivilege secondPrivilege = SetupPrivilege.Administrator)
    {
        var now = createdAt ?? Now;
        var prerequisites = new[]
        {
            new SetupPrerequisite("reviewed-prerequisites", SetupPrerequisiteState.Satisfied, true,
                "Reviewed inert test prerequisites.", "No remedy required.")
        };
        var resources = new[]
        {
            new SetupExpectedResource("step-one-state", SetupExpectedResourceKind.File,
                "/var/lib/martlet/test-one", null, false),
            new SetupExpectedResource("step-two-state", SetupExpectedResourceKind.Service,
                "martlet-test-two.service", null, false)
        };
        var first = new SetupCommand("/test/step-one", ["--exact", "one"], SetupPrivilege.Operator);
        var second = new SetupCommand("/test/step-two", ["--exact", "two"], secondPrivilege);
        var steps = new[]
        {
            new SetupStep("step-one", 1, SetupStepKind.PrepareHostFilesystem,
                SetupStepExecution.OperatorCommand, "Establish exact inert state one.",
                SetupPrivilege.Operator, [SetupConsentScope.HostFilesystem],
                ["reviewed-prerequisites"], ["step-one-state"], first),
            new SetupStep("step-two", 2, SetupStepKind.ConfigureGatewayService,
                SetupStepExecution.OperatorCommand, "Establish exact inert state two.",
                secondPrivilege, [SetupConsentScope.ServiceManagement],
                ["reviewed-prerequisites"], ["step-two-state"], second)
        };
        return new SetupPlan("reviewed-plan-v1", now, now.AddMinutes(10),
            FingerprintBuilder.Create("configuration", configurationRevision),
            FingerprintBuilder.Create("host", hostRevision),
            FingerprintBuilder.Create("artifact", artifactRevision),
            SetupPlanDisposition.Reviewable,
            prerequisites, resources, new SetupDiskBudget(0, 0, 1024, true),
            steps, []);
    }

    internal static async ValueTask<SetupApproval> ApprovalAsync(
        SetupCoordinator coordinator,
        SetupPlan plan,
        SetupPrivilege maximumPrivilege = SetupPrivilege.Administrator)
    {
        var preview = await coordinator.PreviewAsync(plan);
        Assert.True(preview.CanApprove);
        var approval = preview.Approve(SetupApprovalDecision.Approve,
            preview.RequiredConsentScopes, maximumPrivilege);
        Assert.True(approval.IsApproved);
        return approval;
    }
}
