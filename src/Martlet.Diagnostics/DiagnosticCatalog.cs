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
        new("settings.create", "Use Setup / resume or Create unconfigured local profile in the desktop. Explicit save creates settings; opening setup does not connect or look up credentials."),
        new("settings.restore", "Keep a backup of the original settings. Correct malformed settings or restore a compatible backup; use a compatible Martlet build for newer settings. Do not delete or reset the file."),
        new("settings.check_access", "Check data directory access and other Martlet processes, then retry. Do not run as administrator or disable protection."),
        new("settings.reload", "Reload and review the current profile before saving; another writer changed the settings."),
        new("settings.correct", "Correct the profile before saving. Existing settings must be preserved."),
        new("provider.setup", "Provider execution is unavailable in this build. Use Setup / resume to select named routes and record destination choices. Missing roles and unverified access stay incomplete; no credential lookup or request was attempted."),
        new("audio.input_guide", "Open Desktop Audio setup for explicit local selection and a separately permitted bounded microphone test. Review Windows desktop-app microphone privacy access. Ordinary diagnostics never enumerate or open devices."),
        new("audio.output_guide", "Open Desktop Audio setup for explicit output selection and a separately confirmed synthetic tone. Check volume manually. Ordinary diagnostics play nothing; only a deliberate listening confirmation records audibility."),
        new("pipeline.unavailable", "This pipeline stage is not wired into diagnostics. A future explicit fixture run must remain labeled fixture, never real AI or real-device evidence."),
        new("host.setup", "Host and GPU diagnostics are unavailable. Do not change firewall, drivers or services on the strength of this status."),
        new("fixture.explain", "Choose another offline scenario or Stop. Content is scripted, not AI; no-speech and not-addressed are reasoned silence, not device errors. Real provider setup remains unavailable."),
        new("fixture.retry", "Review the scenario and trace code, then explicitly start a new fixture. Failed/partial text is not a completed answer; nothing is retried or read aloud automatically."),
        new("fixture.audio", "Text remains usable. Check the intended Windows output and app volume; explicitly approve another tone only after previous device release. No fallback or replay occurs. Audibility requires a listening witness.")
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
        new("fixture.passed", ProbeOutcome.Passed, "The explicitly injected fixture check passed. This is not live provider, device or pipeline evidence.", "pipeline.unavailable"),
        new("fixture.running", ProbeOutcome.Running, "FIXTURE - NOT AI: scripted text is running; no completed evidence yet.", "fixture.explain"),
        new("fixture.playback", ProbeOutcome.Running, "Fixture text ended. The explicitly permitted synthetic tone is still running; this is not speech or audibility evidence.", "fixture.audio"),
        new("fixture.completed", ProbeOutcome.Passed, "The requested offline fixture completed. Any requested tone has separate backend accounting; no real provider or installation readiness is established.", "fixture.explain"),
        new("fixture.refused", ProbeOutcome.Warning, "The scripted provider refused. Refusal is separate from ordinary answer text and is never sent to playback.", "fixture.explain"),
        new("fixture.no_speech", ProbeOutcome.Warning, "Synthetic no-speech: the scripted turn is silent by design. No microphone was tested and this is not a device error.", "fixture.explain"),
        new("fixture.not_addressed", ProbeOutcome.Warning, "Synthetic not-addressed: policy intentionally stayed silent. This is not an error or a live talk-decision test.", "fixture.explain"),
        new("fixture.stopped", ProbeOutcome.Warning, "The fixture was canceled or stopped. Pending text/audio was invalidated; no automatic retry or playback follows.", "fixture.retry"),
        new("fixture.failed", ProbeOutcome.Failed, "The scripted fixture failed or ended partially. Inspect the normalized issue and trace; this does not indicate a real provider failure.", "fixture.retry", ErrorCode.ProviderFailed),
        new("fixture.deadline", ProbeOutcome.Failed, "The simulated provider deadline expired. Script time is synthetic, not measured network latency.", "fixture.retry", ErrorCode.ProviderFailed),
        new("fixture.audio_incomplete", ProbeOutcome.Warning, "Fixture text completed but the requested tone did not complete. This is not successful playback or audibility evidence.", "fixture.audio"),
        new("fixture.audio_failed", ProbeOutcome.Failed, "The permitted tone could not finish safely. Text remains available; no output fallback or automatic replay was attempted.", "fixture.audio", ErrorCode.AudioPlaybackFailed)
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
