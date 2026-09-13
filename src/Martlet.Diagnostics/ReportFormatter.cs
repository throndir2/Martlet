using System.Globalization;
using System.Text;

namespace Martlet.Diagnostics;

public static class ReportFormatter
{
    public static string Human(DoctorReport report)
    {
        report.Validate();
        var text = new StringBuilder();
        text.AppendLine($"Martlet {report.ApplicationVersion} - diagnostics");
        text.AppendLine($"Status: {(report.Ready ? "requested checks passed" : "not ready")} (exit {report.ExitCode})");
        text.AppendLine($"Profile: {report.ProfileKind?.ToString() ?? "not inspected / not configured"}; ID: {report.ProfileId?.ToString() ?? "none"}");
        text.AppendLine($"Report: {report.CreatedAt.ToString("O", CultureInfo.InvariantCulture)}");
        if (report.StartedAt is { } started)
            text.AppendLine($"Run started: {started:O}; report completed: {report.CompletedAt:O}");
        if (report.InvocationError is { } error)
            text.AppendLine($"{error.Code}: {error.Summary} Next action: {error.ActionId}");
        if (report.Fixture is { } fixture)
            text.Append(FixtureDiagnostics.Describe(fixture));
        foreach (var probe in report.Probes)
        {
            text.AppendLine($"{probe.Id}: {probe.Outcome} [{probe.Provenance}; {probe.Freshness}; {probe.Execution}] - {probe.Summary}");
            text.AppendLine($"  Code: {probe.DiagnosticCode ?? probe.Error?.Code.ToString() ?? "none"}; age: {Milliseconds(probe.AgeMilliseconds)}; duration: {Milliseconds(probe.DurationMilliseconds)}");
            if (probe.Error is { } probeError)
                text.AppendLine($"  Error: {probeError.Code}");
            if (probe.Effects is not null)
                text.AppendLine($"  Declared effects: {string.Join(", ", probe.Effects)}");
            if (probe.OperationStillRunning)
                text.AppendLine("  Operation still running when reported. Late evidence is discarded; reruns are blocked until it actually ends.");
            if (probe.ActionId is { } action)
                text.AppendLine($"  Next action ({action}): {probe.Remedy ?? "No guide was included in this report."}");
        }
        text.AppendLine("Only listed requested checks are covered. Unselected audio, GPU, cloud and host paths were not tested.");
        text.AppendLine("No AI conversation is available in this diagnostic build. Fixtures are not real AI.");
        return text.ToString();
    }

    internal static string Milliseconds(double? value) => value is { } milliseconds
        ? $"{milliseconds.ToString("0", CultureInfo.InvariantCulture)} ms" : "unknown / not observed";
}
