using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Diagnostics;

public enum ProbeEffect { LocalReadOnly, Permissioned, Network, ProviderCost, Device }
public enum ProbeExecution { NotRun, Running, Completed, TimedOut, Canceled, Faulted }

public sealed record DiagnosticRemedy(string Id, string Guidance);
public sealed record DiagnosticFinding(string Id, ProbeOutcome Outcome, string Summary, string ActionId,
    ErrorCode? ErrorCode = null);

// Authored metadata only. Neither findings nor actions accept exception text, paths or shell commands.
public static class DiagnosticCatalog
{
    public static IReadOnlyList<DiagnosticRemedy> Remedies { get; } = Array.AsReadOnly<DiagnosticRemedy>(
    [
        new("diagnostics.refresh", "Refresh local status. A local pass does not test audio, providers or GPU readiness."),
        new("diagnostics.wait", "Stop diagnostics and wait for the active check to finish. If it does not finish, close Martlet and retry. No automatic repair is performed."),
        new("diagnostics.report", "Retry local diagnostics. If the same internal error remains, report the probe ID and diagnostic code, not settings contents or secrets."),
        new("cli.help", "Use --help for implemented commands. Probe IDs are exact and case-sensitive; each may be selected only once."),
        new("settings.create", "Use Create unconfigured local profile in the desktop. This saves settings only and does not connect a provider."),
        new("settings.restore", "Keep a backup of the original settings. Correct malformed settings or restore a compatible backup; use a compatible Martlet build for newer settings. Do not delete or reset the file."),
        new("settings.check_access", "Check data directory access and other Martlet processes, then retry. Do not run as administrator or disable protection."),
        new("settings.reload", "Reload and review the current profile before saving; another writer changed the settings."),
        new("settings.correct", "Correct the profile before saving. Existing settings must be preserved."),
        new("provider.setup", "Provider execution is unavailable in this build. Future setup must select a supported route and obtain destination and cost consent; no credential lookup or request was attempted."),
        new("audio.input_guide", "Microphone testing is unavailable here. When supported, review Windows microphone privacy access and select the intended input. No permission was changed or device opened."),
        new("audio.output_guide", "Playback testing is unavailable here. Check the intended output and Windows app volume manually. No tone was played; only a deliberate listening test can establish audibility."),
        new("pipeline.unavailable", "This pipeline stage is not wired into diagnostics. A future explicit fixture run must remain labeled fixture, never real AI or real-device evidence."),
        new("host.setup", "Host and GPU diagnostics are unavailable. Do not change firewall, drivers or services on the strength of this status.")
    ]);

    public static IReadOnlyList<DiagnosticFinding> Findings { get; } = Array.AsReadOnly<DiagnosticFinding>(
    [
        new("settings.valid", ProbeOutcome.Passed, "The local settings file is valid. This does not establish provider readiness.", "diagnostics.refresh"),
        new("settings.first_run", ProbeOutcome.NotConfigured, "First run: no settings file exists. No profile was created automatically.", "settings.create"),
        new("settings.malformed", ProbeOutcome.Failed, "Settings are malformed or exceed supported limits. The original file was not changed.", "settings.restore", ErrorCode.SettingsMalformed),
        new("settings.version", ProbeOutcome.Failed, "Settings use an unsupported version. The original file was not changed.", "settings.restore", ErrorCode.UnsupportedVersion),
        new("settings.inaccessible", ProbeOutcome.Failed, "Settings cannot be accessed. No replacement profile was created.", "settings.check_access", ErrorCode.SettingsInaccessible),
        new("application.available", ProbeOutcome.Passed, "The Martlet diagnostic application is running. This is not a pipeline or installation qualification.", "diagnostics.refresh"),
        new("runtime.available", ProbeOutcome.Passed, "The required .NET 10 runtime is executing this process. No external runtime, GPU or host was inspected.", "diagnostics.refresh"),
        new("runtime.unsupported", ProbeOutcome.Failed, "This process is not running the required .NET 10 runtime.", "diagnostics.report", ErrorCode.UnsupportedVersion),
        new("provider.unavailable", ProbeOutcome.NotConfigured, "Not connected. Provider execution is not wired here. No network or credential-store requests were made.", "provider.setup"),
        new("audio.input_unavailable", ProbeOutcome.Skipped, "Microphone diagnostics are unavailable. No devices were enumerated or opened.", "audio.input_guide"),
        new("audio.output_unavailable", ProbeOutcome.Skipped, "Playback diagnostics are unavailable. No audio devices were opened and no sound was played.", "audio.output_guide"),
        new("pipeline.unavailable", ProbeOutcome.Skipped, "This stage is unavailable in this diagnostic build. No fixture or real pipeline was run.", "pipeline.unavailable"),
        new("host.unavailable", ProbeOutcome.NotConfigured, "Host and GPU diagnostics are not configured or implemented here. No DNS, network or GPU query was made.", "host.setup"),
        new("probe.not_run", ProbeOutcome.Skipped, "Catalog entry only: this check has not run.", "diagnostics.refresh"),
        new("probe.running", ProbeOutcome.Running, "Local diagnostics are running; no completed evidence is available yet.", "diagnostics.wait"),
        new("probe.effects_blocked", ProbeOutcome.Skipped, "This check declares effects beyond local read-only inspection and cannot run in this executor.", "diagnostics.refresh"),
        new("probe.timeout", ProbeOutcome.Warning, "The diagnostic deadline expired. No late result will be accepted; cancellation was requested, not assumed to have completed.", "diagnostics.wait"),
        new("probe.canceled", ProbeOutcome.Warning, "The caller stopped diagnostics. No late result will be accepted; cancellation was requested, not assumed to have completed.", "diagnostics.wait"),
        new("probe.busy", ProbeOutcome.Skipped, "An earlier check still owns an execution slot. This check was not started.", "diagnostics.wait"),
        new("probe.fault", ProbeOutcome.Failed, "The diagnostic callback faulted. Its exception details were not included in this report.", "diagnostics.report", ErrorCode.InvalidContract),
        new("probe.invalid_evidence", ProbeOutcome.Failed, "The diagnostic callback returned invalid evidence. It was rejected, not treated as a pass.", "diagnostics.report", ErrorCode.InvalidContract),
        new("probe.unknown", ProbeOutcome.Unknown, "The diagnostic check could not establish an observation.", "diagnostics.refresh"),
        new("fixture.passed", ProbeOutcome.Passed, "The explicitly injected fixture check passed. This is not live provider, device or pipeline evidence.", "pipeline.unavailable")
    ]);

    public static DiagnosticFinding Finding(string id) =>
        Findings.FirstOrDefault(item => item.Id == id) ?? throw new ArgumentException("Unknown diagnostic finding.");

    public static DiagnosticRemedy Remedy(string id) =>
        Remedies.FirstOrDefault(item => item.Id == id) ?? throw new ArgumentException("Unknown diagnostic remedy.");

    internal static string SettingsFinding(SettingsLoadResult settings)
    {
        ContractRules.Defined(settings.State);
        settings.Error?.Validate();
        ContractRules.Require(settings.Error is null || settings.Error.Stage == Stage.Settings, "Settings errors require the settings stage.");
        return settings.State switch
        {
            SettingsLoadState.Loaded when settings.Error is null => "settings.valid",
            SettingsLoadState.FirstRun when settings.Error is null => "settings.first_run",
            SettingsLoadState.Inaccessible when settings.Error?.Code == ErrorCode.SettingsInaccessible => "settings.inaccessible",
            SettingsLoadState.Invalid when settings.Error?.Code == ErrorCode.UnsupportedVersion => "settings.version",
            SettingsLoadState.Invalid when settings.Error?.Code == ErrorCode.SettingsMalformed => "settings.malformed",
            _ => throw new ContractException(ErrorCode.InvalidContract, "Settings state and error disagree.")
        };
    }

    internal static ProbeResult Result(ProbeDefinition definition, string findingId, ProbeExecution execution = ProbeExecution.NotRun)
    {
        var finding = Finding(findingId);
        return new()
        {
            Id = definition.Id, Stage = definition.Stage, Required = definition.Required,
            Outcome = finding.Outcome, Summary = finding.Summary, DiagnosticCode = finding.Id,
            ActionId = finding.ActionId, Remedy = Remedy(finding.ActionId).Guidance,
            Execution = execution, Effects = definition.Effects,
            Provenance = EvidenceProvenance.NotRun, Freshness = EvidenceFreshness.Unknown,
            Error = finding.ErrorCode is { } code ? new()
            {
                Code = code, Stage = definition.Stage, Retryable = false,
                Summary = finding.Summary, ActionId = finding.ActionId
            } : null
        };
    }
}
