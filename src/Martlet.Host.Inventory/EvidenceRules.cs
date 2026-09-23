using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Martlet.Host.Inventory;

internal static class EvidenceRules
{
    public static void Validate(ProbeResult probe)
    {
        if (probe.Evidence is not { } e) return;
        object?[] fields = [e.Platform, e.Context, e.Cpu, e.Memory, e.Disk, e.Gpus, e.Tool, e.DockerAccess, e.Network, e.Port, e.Clock, e.Reboot];
        if (fields.Count(f => f is not null) != 1) Fail();
        var valid = probe.Id switch
        {
            ProbeId.Platform => e.Platform is { } p && Enum.IsDefined(p.Distribution) && Enum.IsDefined(p.KernelFlavor) &&
                Architecture(p.OsArchitecture) && Architecture(p.ProcessArchitecture) && EvidenceTextRules.SafeVersion(p.DotnetRuntime) &&
                (p.Version is null || EvidenceTextRules.SafeOsVersion(p.Version)) &&
                (p.KernelRelease is null || Regex.IsMatch(p.KernelRelease, @"^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,4}(-[0-9]{1,5})?\z", RegexOptions.NonBacktracking)),
            ProbeId.Context => e.Context is { } c && Enum.IsDefined(c.Context) && !c.PhysicalHostConfirmed,
            ProbeId.Cpu => e.Cpu is { LogicalProcessors: > 0 and <= 4096 },
            ProbeId.Memory => e.Memory is { } m && m.TotalBytes > 0 && m.AvailableBytes >= 0 && m.AvailableBytes <= m.TotalBytes &&
                m.SwapTotalBytes >= 0 && m.SwapFreeBytes >= 0 && m.SwapFreeBytes <= m.SwapTotalBytes,
            ProbeId.Disk => e.Disk is { } d && d.Location == "RootFilesystem" && d.TotalBytes > 0 &&
                d.AvailableBytes >= 0 && d.AvailableBytes <= d.TotalBytes && d.FreeInodes is null,
            ProbeId.Nvidia => e.Gpus is { IsDefault: false, Length: > 0 and <= 16 } g && g.All(EvidenceTextRules.SafeGpu),
            ProbeId.DockerEngine or ProbeId.Compose or ProbeId.ContainerToolkit => e.Tool is { } t &&
                Enum.IsDefined(t.Tool) && t.Tool.ToString() == probe.Id.ToString() && Enum.IsDefined(t.Package) &&
                Enum.IsDefined(t.StandardFile) && (t.VersionCore is null || EvidenceTextRules.SafeVersion(t.VersionCore)),
            ProbeId.DockerAccess => e.DockerAccess is { } a && Enum.IsDefined(a.StandardSocket) && !a.DaemonContacted && a.DaemonAccess is null,
            ProbeId.Network => e.Network is { } n && n.Interfaces is > 0 and <= 64 &&
                new[] { n.LoopbackV4, n.PrivateV4, n.LinkLocalV4, n.OtherV4, n.LoopbackV6, n.LinkLocalV6, n.UniqueLocalV6, n.OtherV6 }
                    .All(count => count is >= 0 and <= 256),
            ProbeId.GatewayPort => e.Port is { Port: >= 1 and <= 65535, GatewayAvailabilityEstablished: false },
            ProbeId.LocalClock => e.Clock is { AccuracyEstablished: false } clock && clock.LocalUtc != default,
            ProbeId.Reboot => e.Reboot is { RebootOutcomeEstablished: false },
            _ => false
        };
        if (!valid) Fail();
        if (probe.Code == FindingCode.HOST_OBSERVED && (e.Tool is { } tool &&
                (tool.Package != PackagePresence.Installed || tool.StandardFile != BinaryPresence.Present || tool.VersionCore is null ||
                 tool.Tool == ToolKind.Compose && !tool.VersionCore.StartsWith("2.", StringComparison.Ordinal)) ||
            e.Port is { OccupancyObserved: true } || e.Platform is { } platform &&
                (!platform.Linux || platform.Distribution != Distro.Ubuntu || platform.Version != "24.04" ||
                 platform.OsArchitecture != "X64" || platform.ProcessArchitecture != "X64" || platform.KernelFlavor == KernelFlavor.Wsl) ||
            e.DockerAccess is not null))
            Fail();
    }

    private static bool Architecture(string value) =>
        Enum.TryParse<Architecture>(value, out var arch) && Enum.IsDefined(arch) && arch.ToString() == value;
    private static void Fail() => throw new InvalidDataException("Unsafe or inconsistent typed host evidence.");
}
