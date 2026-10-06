using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Diagnostics;

namespace Martlet.Support;

internal sealed record SafeProbe(string Id, Stage Stage, bool Required, ProbeOutcome RecordedOutcome,
    EvidenceProvenance Provenance, EvidenceFreshness Freshness, DateTimeOffset? ObservedAt,
    string Code, string ActionId, string Guidance, ErrorCode? ErrorCode, Guid? TraceId,
    ProbeExecution? Execution, double? DurationMilliseconds, double? AgeMilliseconds,
    double? MaximumAgeMilliseconds, bool OperationStillRunning);

internal sealed record SafeReport(int SchemaVersion, DateTimeOffset CreatedAt, string ApplicationVersion,
    int SharedDoctorExitCode, SettingsLoadState? SettingsState, ProfileKind? ProfileKind,
    SafeProbe[] Probes, ErrorCode? InvocationError, string? InvocationAction);

internal static class ReportProjection
{
    private static readonly string[] ProbeIds =
    [
        "settings.load", "provider.connection", "audio.playback", "application.version", "runtime.version", "platform.architecture",
        "audio.input", "pipeline.vad", "pipeline.stt", "pipeline.policy", "pipeline.llm", "pipeline.tts",
        "host.connection", "fixture.session"
    ];

    internal static void ValidateProbe(ProbeResult probe)
    {
        Guard.Require(probe is not null);
        try { probe!.Validate(); }
        catch (ContractException) { throw new SupportException(SupportFailure.InvalidData); }
        Guard.Require(ProbeIds.Contains(probe.Id, StringComparer.Ordinal) && probe.DiagnosticCode is not null);
        var finding = DiagnosticEvent.CatalogFinding(probe.DiagnosticCode!);
        Guard.Require(probe.ActionId == finding.ActionId);
        Guard.Require(probe.Outcome == finding.Outcome ||
            probe.DiagnosticCode!.StartsWith("fixture.", StringComparison.Ordinal) &&
            probe.Outcome == ProbeOutcome.Failed && probe.Error is not null);
        // Shared validation covers Error.ActionId, but it and Error.Summary are omitted, not catalog-rendered.
        Guard.Require(!probe.DiagnosticCode!.StartsWith("fixture.", StringComparison.Ordinal) ||
            probe.Provenance == EvidenceProvenance.Fixture);
    }

    internal static SafeReport Freeze(DoctorReport report, Func<Guid, Guid> pseudonym)
    {
        Guard.Require(report is not null);
        Guard.Version(report!.ApplicationVersion);
        Guard.Utc(report.CreatedAt);
        // Copy the bounded source list first. Callers own synchronization while this method runs.
        Guard.Require(report.Probes is { Count: <= 64 });
        var source = report with { Probes = Enumerable.Range(0, report.Probes.Count).Select(i => report.Probes[i]).ToArray() };
        int exit;
        try { exit = source.ExitCode; }
        catch (ContractException) { throw new SupportException(SupportFailure.InvalidData); }
        if (source.InvocationError is { } invocation)
            Guard.Require(DiagnosticCatalog.Remedies.Any(r => r.Id == invocation.ActionId));
        var probes = source.Probes.Select(probe =>
        {
            ValidateProbe(probe);
            return new SafeProbe(probe.Id, probe.Stage, probe.Required, probe.Outcome, probe.Provenance,
                probe.Freshness, probe.ObservedAt, probe.DiagnosticCode!, probe.ActionId!,
                DiagnosticCatalog.Remedy(probe.ActionId!).Guidance, probe.Error?.Code,
                probe.Error?.TraceId is { } id ? pseudonym(id) : null, probe.Execution,
                probe.DurationMilliseconds, probe.AgeMilliseconds, probe.MaximumAgeMilliseconds,
                probe.OperationStillRunning);
        }).ToArray();
        return new(1, source.CreatedAt, source.ApplicationVersion, exit, source.SettingsState,
            source.ProfileKind, probes, source.InvocationError?.Code, source.InvocationError?.ActionId);
    }
}
