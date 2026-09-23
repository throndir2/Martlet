using System.Collections.Immutable;
using System.Text;

namespace Martlet.Host.Inventory.Tests;

public sealed class ContractTests
{
    [Fact]
    public void All_typed_evidence_alternatives_round_trip()
    {
        var report = ReportFixtures.Create();
        var decoded = HostJson.Deserialize(Encoding.UTF8.GetBytes(HostJson.Serialize(report)));
        Assert.Equal(14, decoded.Probes.Count(p => p.Evidence is not null));
        Assert.Equal(HostJson.Serialize(report), HostJson.Serialize(decoded));
        Assert.All(new[] { ProbeId.ContainerGpu, ProbeId.ModelInference, ProbeId.ClockAccuracy, ProbeId.PairingFirewall },
            id => Assert.Equal(FindingCode.HOST_NOT_RUN, decoded.Probes.Single(p => p.Id == id).Code));
        Assert.Null(decoded.Probes.Single(p => p.Id == ProbeId.DockerAccess).Evidence!.DockerAccess!.DaemonAccess);
    }

    [Fact]
    public void Every_finding_and_action_uses_fixed_remedies_through_codec()
    {
        var actions = new HashSet<ActionId>();
        foreach (var code in Enum.GetValues<FindingCode>())
        {
            var report = ReportFixtures.With(ReportFixtures.Create(), ProbeId.Cpu, p => code == FindingCode.HOST_NOT_RUN
                ? p with { Code = code, Evidence = null, ObservedAt = null, AgeMilliseconds = null, Provenance = Provenance.NotObserved }
                : p with { Code = code, Evidence = code == FindingCode.HOST_OBSERVED ? p.Evidence : null });
            var decoded = HostJson.Deserialize(Encoding.UTF8.GetBytes(HostJson.Serialize(report)));
            var probe = decoded.Probes.Single(p => p.Id == ProbeId.Cpu);
            actions.Add(probe.Remedy.Action);
            Assert.Equal(RemedyCatalog.Get(code).State, probe.State);
            Assert.Equal("https", new Uri(probe.Remedy.OfficialUrl).Scheme);
            Assert.Contains("approval", probe.Remedy.Privilege);
            Assert.Contains("None performed", probe.Remedy.Download);
            Assert.Contains(probe.Summary, ReportFormatter.Human(decoded));
        }
        Assert.Equal(Enum.GetValues<ActionId>().Order(), actions.Order());
    }

    [Fact]
    public void Invalid_reports_fail_at_every_supported_entry_point()
    {
        var valid = ReportFixtures.Create();
        var invalid = new List<HostReport>
        {
            valid with { SchemaVersion = 2 }, valid with { ApplicationVersion = "future" },
            valid with { ManifestId = "other" }, valid with { Scope = (DoctorScope)99 },
            valid with { Provenance = Provenance.NotObserved }, valid with { Provenance = (Provenance)99 },
            valid with { CreatedAt = default }, valid with { Probes = default }, valid with { Probes = [] },
            valid with { Probes = valid.Probes.SetItem(0, null!) },
            valid with { Probes = valid.Probes.SetItem(0, valid.Probes[1]) }
        };
        Func<ProbeResult, ProbeResult>[] changes =
        [
            p => p with { Id = (ProbeId)99 }, p => p with { Code = (FindingCode)99 },
            p => p with { Provenance = (Provenance)99 }, p => p with { Provenance = Provenance.LiveLocal },
            p => p with { Required = false }, p => p with { Source = "secret-host" },
            p => p with { ObservedAt = null }, p => p with { ObservedAt = default(DateTimeOffset) },
            p => p with { ObservedAt = valid.CreatedAt.AddSeconds(1) },
            p => p with { DurationMilliseconds = -1 }, p => p with { DurationMilliseconds = double.PositiveInfinity },
            p => p with { AgeMilliseconds = double.NaN }, p => p with { AgeMilliseconds = -1 },
            p => p with { AgeMilliseconds = null }, p => p with { AgeMilliseconds = 0 },
            p => p with { Evidence = null }, p => p with { Evidence = new() },
            p => p with { Evidence = new() { Cpu = new(0, false, false, false, false) } },
            p => p with { Evidence = new() { Cpu = new(4097, false, false, false, false) } },
            p => p with { Evidence = new() { Cpu = new(2, true, true, true, false), Reboot = new(false) } }
        ];
        invalid.AddRange(changes.Select(change => ReportFixtures.With(valid, ProbeId.Cpu, change)));
        foreach (var report in invalid)
        {
            Assert.Throws<InvalidDataException>(report.Validate);
            Assert.Throws<InvalidDataException>(() => HostJson.Serialize(report));
            Assert.Throws<InvalidDataException>(() => ReportFormatter.Human(report));
        }
    }

    [Theory]
    [InlineData(ProbeId.ContainerGpu)]
    [InlineData(ProbeId.ModelInference)]
    [InlineData(ProbeId.ClockAccuracy)]
    [InlineData(ProbeId.PairingFirewall)]
    [InlineData(ProbeId.QualifiedTuple)]
    public void Future_checks_cannot_be_promoted(ProbeId id)
    {
        foreach (var code in new[] { FindingCode.HOST_OBSERVED, FindingCode.HOST_DOCKER_PRIVILEGE })
        {
            var report = ReportFixtures.With(ReportFixtures.Create(), id, p => p with
            {
                Code = code, Provenance = Provenance.AuthoredFixture, ObservedAt = ReportFixtures.Timestamp,
                AgeMilliseconds = 10000, Evidence = new() { Cpu = new(2, true, true, true, false) }
            });
            Assert.Throws<InvalidDataException>(report.Validate);
        }
    }

    [Fact]
    public void Evidence_limits_and_unknown_capabilities_are_fail_closed()
    {
        (ProbeId Id, Evidence Value)[] invalid =
        [
            (ProbeId.Platform, new() { Platform = new(Distro.Ubuntu, "24.04", "secret", KernelFlavor.Generic, "X64", "X64", "10.0.9", true) }),
            (ProbeId.Platform, new() { Platform = new(Distro.Ubuntu, "24.04\n", "6.8.0", KernelFlavor.Generic, "X64", "X64", "10.0.9", true) }),
            (ProbeId.Platform, new() { Platform = new(Distro.Ubuntu, "24.04", "6.8.0\n", KernelFlavor.Generic, "X64", "X64", "10.0.9", true) }),
            (ProbeId.Platform, new() { Platform = new(Distro.Ubuntu, "24.04", null, KernelFlavor.Generic, null!, "X64", "10.0.9", true) }),
            (ProbeId.Context, new() { Context = new(ExecutionContext.NoContainerIndicators, true) }),
            (ProbeId.Memory, new() { Memory = new(10, 11, 0, 0) }),
            (ProbeId.Memory, new() { Memory = new(10, 1, 1, 2) }),
            (ProbeId.Disk, new() { Disk = new(10, 1, FreeInodes: 1) }),
            (ProbeId.Disk, new() { Disk = new(10, 1, "/home/secret") }),
            (ProbeId.Nvidia, new() { Gpus = [] }),
            (ProbeId.Nvidia, new() { Gpus = default(ImmutableArray<GpuData>) }),
            (ProbeId.Nvidia, new() { Gpus = [null!] }),
            (ProbeId.Nvidia, new() { Gpus = [new(null!, "570.1", 1)] }),
            (ProbeId.Nvidia, new() { Gpus = [new("SECRET_UUID", "570.1", 1)] }),
            (ProbeId.Nvidia, new() { Gpus = [new("NVIDIA RTX 4090\n", "570.1", 1)] }),
            (ProbeId.Nvidia, new() { Gpus = [new("NVIDIA RTX 4090", "secret", 1)] }),
            (ProbeId.Nvidia, new() { Gpus = [new("NVIDIA RTX 4090", "570.1\n", 1)] }),
            (ProbeId.Nvidia, new() { Gpus = [new("NVIDIA RTX 4090", "570.1", 1048577)] }),
            (ProbeId.Nvidia, new() { Gpus = Enumerable.Repeat(new GpuData("NVIDIA RTX 4090", "570.1", 1), 17).ToImmutableArray() }),
            (ProbeId.Compose, new() { Tool = new(ToolKind.Compose, PackagePresence.Installed, BinaryPresence.Present, "1.9") }),
            (ProbeId.Compose, new() { Tool = new(ToolKind.DockerEngine, PackagePresence.Installed, BinaryPresence.Present, "2.9") }),
            (ProbeId.DockerEngine, new() { Tool = new(ToolKind.DockerEngine, PackagePresence.Unknown, BinaryPresence.Unknown, null) }),
            (ProbeId.DockerAccess, new() { DockerAccess = new(false, false, BinaryPresence.Present, true, true) }),
            (ProbeId.Network, new() { Network = new(0, 0, 0, 0, 0, 0, 0, 0, 0) }),
            (ProbeId.Network, new() { Network = new(1, 257, 0, 0, 0, 0, 0, 0, 0) }),
            (ProbeId.GatewayPort, new() { Port = new(7443, false, true) }),
            (ProbeId.GatewayPort, new() { Port = new(0, false) }),
            (ProbeId.GatewayPort, new() { Port = new(7443, true) }),
            (ProbeId.LocalClock, new() { Clock = new(ReportFixtures.Timestamp, true) }),
            (ProbeId.LocalClock, new() { Clock = new(default) }),
            (ProbeId.Reboot, new() { Reboot = new(false, true) })
        ];
        foreach (var (id, value) in invalid)
            Assert.Throws<InvalidDataException>(() => HostJson.Serialize(
                ReportFixtures.With(ReportFixtures.Create(), id, p => p with { Evidence = value })));
    }

    [Fact]
    public void Inclusive_evidence_limits_remain_serializable()
    {
        var report = ReportFixtures.Create();
        (ProbeId Id, Evidence Value)[] boundaries =
        [
            (ProbeId.Cpu, new() { Cpu = new(4096, true, true, true, true) }),
            (ProbeId.Memory, new() { Memory = new(long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue) }),
            (ProbeId.Disk, new() { Disk = new(long.MaxValue, long.MaxValue) }),
            (ProbeId.Nvidia, new() { Gpus = Enumerable.Repeat(new GpuData("NVIDIA RTX 4090", "9999.9999.9999.9999", 1048576), 16).ToImmutableArray() }),
            (ProbeId.Network, new() { Network = new(64, 256, 256, 256, 256, 256, 256, 256, 256) }),
            (ProbeId.GatewayPort, new() { Port = new(65535, false) })
        ];
        foreach (var (id, evidence) in boundaries)
            report = ReportFixtures.With(report, id, p => p with { Evidence = evidence });
        var json = HostJson.Serialize(report);
        Assert.True(Encoding.UTF8.GetByteCount(json) < HostJson.MaximumBytes);
        Assert.Equal(json, HostJson.Serialize(HostJson.Deserialize(Encoding.UTF8.GetBytes(json))));
        var minimum = ReportFixtures.With(report, ProbeId.GatewayPort, p => p with { Evidence = new() { Port = new(1, false) } });
        minimum.Validate();
    }

    [Fact]
    public void Candidate_cannot_acquire_qualified_facts_or_new_pins()
    {
        var candidate = CandidateManifest.Current;
        candidate.Validate();
        CandidateManifest[] invalid =
        [
            candidate with { QualifiedTuple = candidate.QualifiedTuple with { RightsApproved = true } },
            candidate with { QualifiedTuple = null! },
            candidate with { Requirements = default },
            candidate with { Requirements = candidate.Requirements.SetItem(0, null!) },
            candidate with { Requirements = candidate.Requirements.SetItem(0, candidate.Requirements[0] with { Version = null! }) },
            candidate with { Requirements = candidate.Requirements.SetItem(0, candidate.Requirements[0] with { Version = new(ConstraintState.Known, 1, "1.0") }) },
            candidate with { Requirements = candidate.Requirements.SetItem(2, candidate.Requirements[2] with { Version = new(ConstraintState.UnknownUntilH02, 2, null) }) }
        ];
        foreach (var value in invalid) Assert.Throws<InvalidDataException>(value.Validate);
    }
}
