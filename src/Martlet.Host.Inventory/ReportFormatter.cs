using System.Globalization;
using System.Text;

namespace Martlet.Host.Inventory;

public static class ReportFormatter
{
    public static string Human(HostReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        report.Validate();
        var text = new StringBuilder();
        text.AppendLine("UNAUTHENTICATED REPORT METADATA - NOT VERIFIED HOST OBSERVATIONS OR EXECUTION PERMISSION.");
        text.AppendLine("Provenance (including LiveLocal), source, times and findings below are reported claims, not proof of collection.");
        text.AppendLine($"Martlet host report inspection | report v{report.SchemaVersion} | claimed scope: {report.Scope}");
        text.AppendLine($"Reported scope notice: {report.ScopeNotice}");
        text.AppendLine($"Target: Ubuntu 24.04 LTS x86_64. Candidate: {report.ManifestId}");
        text.AppendLine($"Reported snapshot UTC: {report.CreatedAt:O}. Report exit {report.ExitCode}; no deployment-ready badge.");
        text.AppendLine("This inspection performs no collection, install, downloads, daemon/context contact, network probes, configuration changes or automatic repairs.");
        foreach (var probe in report.Probes)
        {
            text.AppendLine();
            text.AppendLine($"{probe.Id}: {probe.State} [{probe.Code}] {(probe.Required ? "(required in requested scope)" : "(informational / not requested)")}");
            text.AppendLine($"  {probe.Summary}");
            if (probe.Evidence is { } e) text.AppendLine($"  Reported evidence: {Describe(e)}");
            var requirement = CandidateManifest.Current.Requirements.FirstOrDefault(r => r.Probe == probe.Id);
            if (requirement is not null)
                text.AppendLine($"  Desired: {(requirement.Version.RequiredMajor is { } major ? $"major {major} family; " : "")}exact qualified version UNKNOWN until H02.");
            text.AppendLine(FormattableString.Invariant($"  Evidence: {probe.Provenance}; age {probe.AgeMilliseconds?.ToString("F0", CultureInfo.InvariantCulture) ?? "unknown"} ms; duration {probe.DurationMilliseconds:F0} ms."));
            text.AppendLine($"  Claimed source: {probe.Source}");
            text.AppendLine($"  Next [{probe.Remedy.Action}]: {probe.Remedy.Instruction}");
            text.AppendLine($"  Review official guidance: {probe.Remedy.OfficialUrl}");
        }
        text.AppendLine();
        text.AppendLine("Privilege: none here; manual package/configuration remedies require separate administrator approval.");
        text.AppendLine("Downloads: none here; future sizes UNKNOWN until H02. Reboot: never performed; schedule separately if needed.");
        text.AppendLine("Default report omits addresses, host/user/home names, GPU UUID/serial and native stderr. Review before sharing.");
        return text.ToString();
    }

    private static string Describe(Evidence e) => e switch
    {
        { Platform: { } p } => $"{p.Distribution} {p.Version ?? "(version unknown)"}; kernel {p.KernelRelease ?? "unknown"} / {p.KernelFlavor}; OS {p.OsArchitecture}, process {p.ProcessArchitecture}; bundled .NET {p.DotnetRuntime}",
        { Context: { } c } => $"{c.Context}; physical host NOT confirmed",
        { Cpu: { } c } => $"{c.LogicalProcessors} logical CPUs; SSE4.2={c.Sse42}, AVX={c.Avx}, AVX2={c.Avx2}, AVX512F={c.Avx512F}; no instruction-set/model certification",
        { Memory: { } m } => FormattableString.Invariant($"RAM available {m.AvailableBytes} bytes ({m.AvailableBytes / 1073741824.0:F2} GiB) / total {m.TotalBytes} bytes; swap free {m.SwapFreeBytes} / total {m.SwapTotalBytes} bytes"),
        { Disk: { } d } => FormattableString.Invariant($"/ available {d.AvailableBytes} bytes ({d.AvailableBytes / 1073741824.0:F2} GiB) / total {d.TotalBytes} bytes; free inodes UNKNOWN; model location NOT selected"),
        { Gpus: { } g } => string.Join("; ", g.Select(x => $"{x.Model}; driver {x.DriverVersion}; VRAM {x.MemoryMiB} MiB (not free budget or model fit)")),
        { Tool: { } t } => $"package {t.Package}; standard file {t.StandardFile}; numeric version core {t.VersionCore ?? "UNKNOWN"} (not exact distro build); daemon/runtime NOT tested",
        { DockerAccess: { } a } => $"effective root={a.EffectiveRoot}; Docker group member={a.DockerGroupMember}; standard socket={a.StandardSocket}; actual daemon access UNKNOWN",
        { Network: { } n } => $"{n.Interfaces} interfaces; IPv4 loopback/private/link-local/other={n.LoopbackV4}/{n.PrivateV4}/{n.LinkLocalV4}/{n.OtherV4}; IPv6 loopback/link-local/unique-local/other={n.LoopbackV6}/{n.LinkLocalV6}/{n.UniqueLocalV6}/{n.OtherV6}; counts only",
        { Port: { } p } => $"port {p.Port}; occupancy observed={p.OccupancyObserved}; gateway availability NOT established",
        { Clock: { } c } => $"local UTC {c.LocalUtc:O}; external accuracy UNKNOWN",
        { Reboot: { } r } => $"reboot-required marker={r.MarkerPresent}; reboot outcome NOT established",
        _ => throw new InvalidDataException("Unknown report evidence.")
    };
}
