using System.Globalization;
using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Diagnostics;

// Local settings inspection only; this is not the F04 probe registry or an inference self-test.
public sealed class FoundationStatusService(SettingsStore settingsStore)
{
    public static string ApplicationVersion => typeof(FoundationStatusService).Assembly.GetName().Version!.ToString(3);

    public async Task<DoctorReport> GetReportAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var report = new DoctorReport
        {
            Version = ContractVersion.Current,
            ApplicationVersion = ApplicationVersion,
            CreatedAt = now,
            ProfileId = settings.Settings?.Profile.Id,
            ProfileKind = settings.Settings?.Profile.Kind,
            SettingsState = settings.State,
            Probes =
            [
                new ProbeResult
                {
                    Id = "settings.load", Stage = Stage.Settings, Required = true,
                    Outcome = settings.State switch
                    {
                        SettingsLoadState.Loaded => ProbeOutcome.Passed,
                        SettingsLoadState.FirstRun => ProbeOutcome.NotConfigured,
                        _ => ProbeOutcome.Failed
                    },
                    Provenance = EvidenceProvenance.Live, Freshness = EvidenceFreshness.Current, ObservedAt = now,
                    Summary = settings.Error?.Summary ?? (settings.State == SettingsLoadState.FirstRun
                        ? "First run: no settings file exists. No profile was created automatically."
                        : "The local settings file is valid. This does not establish provider readiness."),
                    Error = settings.Error
                },
                new ProbeResult
                {
                    Id = "provider.connection", Stage = Stage.Provider, Required = true,
                    Outcome = ProbeOutcome.NotConfigured, Provenance = EvidenceProvenance.NotRun, Freshness = EvidenceFreshness.Unknown,
                    Summary = "Not connected. Provider adapters, credentials and fixture conversations are not implemented. No network requests were made."
                },
                new ProbeResult
                {
                    Id = "audio.playback", Stage = Stage.Playback, Required = true,
                    Outcome = ProbeOutcome.Skipped, Provenance = EvidenceProvenance.NotRun, Freshness = EvidenceFreshness.Unknown,
                    Summary = "Playback and capture are not implemented. No audio devices were opened."
                }
            ]
        };
        report.Validate();
        return report;
    }
}

public static class ReportFormatter
{
    public static string Human(DoctorReport report)
    {
        report.Validate();
        var text = new StringBuilder();
        text.AppendLine($"Martlet {report.ApplicationVersion} - foundation");
        text.AppendLine($"Status: {(report.Ready ? "requested checks passed" : "not ready")} (exit {report.ExitCode})");
        text.AppendLine($"Profile: {report.ProfileKind?.ToString() ?? "not configured"}; ID: {report.ProfileId?.ToString() ?? "none"}");
        text.AppendLine($"Observed: {report.CreatedAt.ToString("O", CultureInfo.InvariantCulture)}");
        if (report.InvocationError is { } error)
            text.AppendLine($"{error.Code}: {error.Summary} Action: {error.ActionId}");
        foreach (var probe in report.Probes)
        {
            text.AppendLine($"{probe.Id}: {probe.Outcome} [{probe.Provenance}; {probe.Freshness}] - {probe.Summary}");
            if (probe.Error is { } probeError)
                text.AppendLine($"  {probeError.Code}; action: {probeError.ActionId}");
        }
        text.AppendLine("No AI conversation is available in this foundation build. Fixtures are not real AI.");
        return text.ToString();
    }
}
