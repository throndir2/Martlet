using System.Collections.Immutable;

namespace Martlet.Host.Inventory.Tests;

internal static class ReportFixtures
{
    internal static DateTimeOffset Timestamp { get; } = new(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);

    internal static HostReport Create(string name = "prerequisites", Provenance provenance = Provenance.AuthoredFixture)
    {
        var scope = name is "inventory" or "unsupported" ? DoctorScope.Inventory : DoctorScope.Prerequisites;
        var probes = Enum.GetValues<ProbeId>().Select(id => new ProbeResult(
            id, HostReport.RequiredInScope(id, scope), FindingCode.HOST_NOT_RUN, Provenance.NotObserved,
            null, 0, null, EvidenceSources.Description(id), null)).ToImmutableArray();
        void Set(ProbeId id, Evidence? evidence, FindingCode code = FindingCode.HOST_OBSERVED)
        {
            var index = (int)id;
            probes = probes.SetItem(index, probes[index] with
            {
                Code = code, Provenance = provenance, ObservedAt = Timestamp,
                DurationMilliseconds = 1, AgeMilliseconds = 10000, Evidence = evidence
            });
        }

        Set(ProbeId.Platform, new() { Platform = new(Distro.Ubuntu, "24.04", "6.8.0-99", KernelFlavor.Generic, "X64", "X64", "10.0.9", true) });
        if (name == "unsupported")
        {
            Set(ProbeId.Platform, new() { Platform = new(Distro.NonLinux, null, null, KernelFlavor.Unknown, "X64", "X64", "10.0.9", false) },
                FindingCode.HOST_UNSUPPORTED_EXECUTION);
        }
        else
        {
            Set(ProbeId.Context, new() { Context = new(ExecutionContext.NoContainerIndicators) });
            Set(ProbeId.Cpu, new() { Cpu = new(2, true, true, true, false) });
            Set(ProbeId.Memory, new() { Memory = new(8589934592, 4294967296, 1073741824, 1073741824) });
            Set(ProbeId.Disk, new() { Disk = new(137438953472, 68719476736) });
            Set(ProbeId.Network, new() { Network = new(2, 1, 1, 0, 0, 1, 0, 0, 0) });
            Set(ProbeId.GatewayPort, new() { Port = new(7443, false) });
            Set(ProbeId.LocalClock, new() { Clock = new(Timestamp) });
            Set(ProbeId.Reboot, new() { Reboot = new(false) });
            if (scope == DoctorScope.Prerequisites)
            {
                Set(ProbeId.Nvidia, new() { Gpus = [new("NVIDIA GeForce RTX 4090", "570.124.06", 24564)] });
                Set(ProbeId.DockerEngine, new() { Tool = new(ToolKind.DockerEngine, PackagePresence.Installed, BinaryPresence.Present, "28.0.4") });
                Set(ProbeId.Compose, new() { Tool = new(ToolKind.Compose, PackagePresence.Installed, BinaryPresence.Present, "2.39.4") });
                Set(ProbeId.ContainerToolkit, new() { Tool = new(ToolKind.ContainerToolkit, PackagePresence.Installed, BinaryPresence.Present, "1.17.8") });
                Set(ProbeId.DockerAccess, new() { DockerAccess = new(false, false, BinaryPresence.Present) }, FindingCode.HOST_INCOMPLETE);
                Set(ProbeId.QualifiedTuple, null, FindingCode.HOST_UNQUALIFIED_VERSIONS);
                if (name == "missing-tools")
                    Set(ProbeId.Compose, new() { Tool = new(ToolKind.Compose, PackagePresence.NotInstalled, BinaryPresence.Missing, null) }, FindingCode.HOST_MISSING);
            }
        }
        return new(1, "0.1.0", CandidateManifest.Current.Id, scope, provenance, Timestamp.AddSeconds(10), probes);
    }

    internal static HostReport With(HostReport report, ProbeId id, Func<ProbeResult, ProbeResult> update)
    {
        var probe = report.Probes.Single(p => p.Id == id);
        return report with { Probes = report.Probes.Replace(probe, update(probe)) };
    }
}
