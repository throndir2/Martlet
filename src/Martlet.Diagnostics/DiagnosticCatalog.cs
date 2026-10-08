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
        new("diagnostics.wait", "Wait for the active check to finish, then refresh the status. If it does not finish, close Martlet and retry. No automatic repair is performed."),
        new("diagnostics.report", "Retry local diagnostics. If the same internal error remains, report the probe ID and diagnostic code, not settings contents or secrets."),
        new("cli.help", "Use --help for implemented commands. Probe IDs are exact and case-sensitive; each may be selected only once."),
        new("settings.create", "Choose Thinking, Voice or Listening on the desktop's Companion page. Saving a choice creates settings; opening a page does not connect or look up credentials."),
        new("settings.restore", "Keep a backup of the original settings. Correct malformed settings or restore a compatible backup; use a compatible Martlet build for newer settings. Do not delete or reset the file."),
        new("settings.check_access", "Check data directory access and other Martlet processes, then retry. Do not run as administrator or disable protection."),
        new("settings.reload", "Reload and review the current profile before saving; another writer changed the settings."),
        new("settings.correct", "Correct the profile before saving. Existing settings must be preserved."),
        new("provider.setup", "Use the Thinking, Voice and Listening pages under Companion in the desktop to save supported named routes and role credentials, then the separate real API conversation surface for a freshly authorized typed/PTT action. Ordinary diagnostics do not test a provider. Missing roles and unverified access stay incomplete; no credential lookup or request was attempted."),
        new("audio.input_guide", "Open Desktop Audio setup for explicit local selection and a separately permitted bounded microphone test. Review Windows desktop-app microphone privacy access. Ordinary diagnostics never enumerate or open devices."),
        new("audio.output_guide", "Open Desktop Audio setup for explicit output selection and a separately confirmed synthetic tone. Check volume manually. Ordinary diagnostics play nothing; only a deliberate listening confirmation records audibility."),
        new("pipeline.unavailable", "This pipeline stage is not executed by ordinary diagnostics. Use the separate explicit Desktop conversation timeline. Configuration is not real provider/device readiness."),
        new("host.setup", "Host and GPU diagnostics are unavailable. Do not change firewall, drivers or services on the strength of this status."),
        new("conversation.review", "Review the real conversation stage and supported saved routes. Use typed/text-only fallback if audio failed. Stop and wait for actual cleanup before a NEW explicitly permitted action; no automatic retry. Completion is not account readiness or proof of heard speech."),
        new("conversation.wait", "The observed conversation action is unfinished or stopped. Wait for actual resource release; a timeout does not prove IO stopped. No replacement worker, replay or renewed permission is implied."),
        new("platform.windows_on_arm", "Windows on Arm: use cloud thinking, listening and speaking, or pair another computer with an NVIDIA GPU for GPU jobs such as Audio2Face and voice cloning. NVIDIA GPUs don't work on Windows on Arm.")
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
        new("platform.x64", ProbeOutcome.Passed, "This PC has an x64 processor and Martlet runs natively on it. No GPU or host was inspected.", "diagnostics.refresh"),
        new("platform.arm64_emulated", ProbeOutcome.Passed, "This PC is Windows on Arm (ARM64). Martlet's x64 build runs under Windows' x64 emulation, which works but uses more processor time and battery. No GPU or host was inspected.", "platform.windows_on_arm"),
        new("platform.arm64_native", ProbeOutcome.Passed, "This PC is Windows on Arm (ARM64) and Martlet's ARM64 build runs natively on it. No GPU or host was inspected.", "platform.windows_on_arm"),
        new("platform.unsupported", ProbeOutcome.Warning, "This PC's processor is neither x64 nor ARM64; Martlet is built and tested for x64 Windows and Windows 11 on Arm only.", "diagnostics.report"),
        new("provider.unavailable", ProbeOutcome.NotConfigured, "Connection not checked by this report. Provider execution is not a diagnostic probe here. No network or credential-store requests were made.", "provider.setup"),
        new("audio.input_unavailable", ProbeOutcome.Skipped, "Microphone diagnostics are unavailable. No devices were enumerated or opened.", "audio.input_guide"),
        new("audio.output_unavailable", ProbeOutcome.Skipped, "Playback diagnostics are unavailable. No audio devices were opened and no sound was played.", "audio.output_guide"),
        new("pipeline.unavailable", ProbeOutcome.Skipped, "This stage is unavailable in this diagnostic build. No pipeline was run.", "pipeline.unavailable"),
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
        new("fixture.passed", ProbeOutcome.Passed, "The explicitly injected fixture check passed. This is not live provider, device or pipeline evidence.", "pipeline.unavailable"),
        new("conversation.running", ProbeOutcome.Running, "An explicitly started conversation stage was observed running. No completed turn or readiness is claimed.", "conversation.wait"),
        new("conversation.completed", ProbeOutcome.Passed, "The observed conversation turn completed. This is not account, installation or audible-speech qualification.", "conversation.review"),
        new("conversation.failed", ProbeOutcome.Failed, "The observed conversation action failed. No input, response or raw provider error is included.", "conversation.review", ErrorCode.ProviderFailed),
        new("conversation.partial", ProbeOutcome.Warning, "The observed turn ended partially. Earlier output may remain; no replay or completed-answer claim.", "conversation.review"),
        new("conversation.refused", ProbeOutcome.Warning, "The observed provider refused. Refusal content is omitted and is not ordinary speech.", "conversation.review"),
        new("conversation.suppressed", ProbeOutcome.Warning, "The observed policy suppressed the action or STT reported no speech. No conversation content is included.", "conversation.review"),
        new("conversation.canceled", ProbeOutcome.Warning, "The observed conversation was canceled or its permission ended. Actual cleanup may still be pending.", "conversation.wait"),
        new("conversation.quarantined", ProbeOutcome.Warning, "The observed conversation cleanup is unproven. Its existing owner remains quarantined.", "conversation.wait")
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
