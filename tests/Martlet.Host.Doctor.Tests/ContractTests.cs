using System.Collections.Immutable;
using System.Text.Json;
using Martlet.Host.Doctor;

namespace Martlet.Host.Doctor.Tests;

public sealed class ContractTests
{
    internal static HostReport Report(string fixture = "prerequisites", DoctorScope scope = DoctorScope.Prerequisites) =>
        HostEvaluator.Evaluate(FixtureCatalog.Create(fixture), scope, 7443, FixtureCatalog.Timestamp.AddSeconds(10));

    [Fact]
    public void Every_current_code_has_real_evaluator_fixture_and_actionable_remedy()
    {
        var reports = FixtureCatalog.Names.Select(name => Report(name)).ToArray();
        var codes = reports.SelectMany(r => r.Probes).Select(p => p.Code).ToHashSet();
        Assert.Equal(Enum.GetValues<FindingCode>().Order(), codes.Order());
        foreach (var report in reports)
        {
            report.Validate();
            Assert.False(report.DeploymentQualified);
            Assert.Contains("AUTHORED FIXTURE", ReportFormatter.Human(report));
            foreach (var probe in report.Probes)
            {
                Assert.NotEmpty(probe.Remedy.Instruction);
                Assert.True(Uri.TryCreate(probe.Remedy.OfficialUrl, UriKind.Absolute, out var uri));
                Assert.Equal("https", uri.Scheme);
                Assert.Contains("approval", probe.Remedy.Privilege);
                Assert.Contains("None performed", probe.Remedy.Download);
                Assert.Contains("None performed", probe.Remedy.Reboot);
            }
        }
    }

    [Theory]
    [InlineData("inventory", DoctorScope.Inventory, 0)]
    [InlineData("prerequisites", DoctorScope.Prerequisites, 2)]
    [InlineData("missing-tools", DoctorScope.Prerequisites, 1)]
    [InlineData("denied", DoctorScope.Prerequisites, 2)]
    [InlineData("windows", DoctorScope.Inventory, 3)]
    [InlineData("wrong-os", DoctorScope.Inventory, 2)]
    [InlineData("container", DoctorScope.Inventory, 2)]
    [InlineData("port-in-use", DoctorScope.Inventory, 1)]
    public void Exit_codes_follow_requested_required_scope_not_deployment(string fixture, DoctorScope scope, int exit)
    {
        Assert.Equal(exit, Report(fixture, scope).ExitCode);
    }

    [Fact]
    public void Json_is_deterministic_and_all_future_hardware_gates_remain_not_run()
    {
        var report = Report();
        Assert.Equal(HostJson.Serialize(report), HostJson.Serialize(Report()));
        using var json = JsonDocument.Parse(HostJson.Serialize(report));
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(json.RootElement.GetProperty("deploymentQualified").GetBoolean());
        Assert.Equal("AuthoredFixture", json.RootElement.GetProperty("provenance").GetString());
        foreach (var id in new[] { ProbeId.ContainerGpu, ProbeId.ModelInference, ProbeId.ClockAccuracy, ProbeId.PairingFirewall })
            Assert.Equal(FindingCode.HOST_NOT_RUN, report.Probes.Single(p => p.Id == id).Code);
        Assert.All(json.RootElement.GetProperty("candidateRequirements").GetProperty("qualifiedTuple").EnumerateObject(),
            p => Assert.Equal(JsonValueKind.Null, p.Value.ValueKind));
    }

    [Fact]
    public void Unknown_schema_enum_catalog_or_inconsistent_evidence_cannot_turn_green()
    {
        var report = Report("inventory", DoctorScope.Inventory);
        Assert.Throws<InvalidDataException>(() => (report with { SchemaVersion = 2 }).ExitCode);
        Assert.Throws<InvalidDataException>(() => (report with { Probes = report.Probes.SetItem(0, report.Probes[0] with { Code = (FindingCode)999 }) }).ExitCode);
        Assert.Throws<InvalidDataException>(() => (report with { Probes = [report.Probes[0]] }).ExitCode);
        Assert.Throws<InvalidDataException>(() => (report with { Probes = report.Probes.SetItem(0, report.Probes[0] with { Required = false }) }).ExitCode);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DoctorScope>("\"future\"", HostJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DoctorScope>("\"inventory\"", HostJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DoctorScope>("0", HostJson.Options));
        var port = report.Probes.Single(p => p.Id == ProbeId.GatewayPort);
        Assert.Throws<InvalidDataException>(() => HostJson.Serialize(report with
        {
            Probes = report.Probes.Replace(port, port with { Evidence = new() { Port = new(7443, true) } })
        }));
    }

    [Fact]
    public void Report_omits_raw_native_identifiers_environment_paths_and_addresses()
    {
        var snapshot = FixtureCatalog.Create("prerequisites");
        var canary = "SECRET_canary_user_host_UUID_012345";
        snapshot = snapshot with
        {
            Files = snapshot.Files
                .SetItem(LocalFile.OsRelease, snapshot.Files[LocalFile.OsRelease] with
                { Text = $"ID=ubuntu\nVERSION_ID=\"24.04\"\nPRETTY_NAME='{canary}'\nHOME=/home/{canary}\n" })
                .SetItem(LocalFile.Cgroup, snapshot.Files[LocalFile.Cgroup] with { Text = $"0::/docker/{canary}\n" })
                .SetItem(LocalFile.Groups, snapshot.Files[LocalFile.Groups] with { Text = $"docker:x:999:{canary}\n" })
        };
        var report = HostEvaluator.Evaluate(snapshot, DoctorScope.Prerequisites, 7443, FixtureCatalog.Timestamp.AddSeconds(10));
        var output = HostJson.Serialize(report) + ReportFormatter.Human(report);
        Assert.DoesNotContain(canary, output);
        Assert.DoesNotContain("192.168.20.30", output);
        Assert.DoesNotContain("127.0.0.1", output);
        Assert.DoesNotContain("/home/", output);
        Assert.DoesNotContain("eth0", output);
        Assert.DoesNotContain("570.124.06, 24564", output);
    }

    [Fact]
    public void Small_capacity_is_an_observation_not_an_invented_failure()
    {
        var report = Report("inventory", DoctorScope.Inventory);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal(2, report.Probes.Single(p => p.Id == ProbeId.Cpu).Evidence!.Cpu!.LogicalProcessors);
        Assert.Equal(8L * 1024 * 1024 * 1024, report.Probes.Single(p => p.Id == ProbeId.Memory).Evidence!.Memory!.TotalBytes);
        Assert.Contains("not enforced minima", report.Probes.Single(p => p.Id == ProbeId.Memory).Remedy.Instruction);
    }

    [Fact]
    public void Oversized_injected_snapshot_is_rejected_before_parsing()
    {
        var s = FixtureCatalog.Create("inventory");
        s = s with { Files = s.Files.SetItem(LocalFile.OsRelease, s.Files[LocalFile.OsRelease] with { Text = new('a', 16385) }) };
        Assert.Throws<InvalidDataException>(() => HostEvaluator.Evaluate(s, DoctorScope.Inventory, 7443, FixtureCatalog.Timestamp.AddSeconds(10)));
    }
}
