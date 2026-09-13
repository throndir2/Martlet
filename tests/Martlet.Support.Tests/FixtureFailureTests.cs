using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Streaming;
using Martlet.Diagnostics;
using Martlet.Sessions;

namespace Martlet.Support.Tests;

public sealed class FixtureFailureTests
{
    private const string PrivateAction = "authored.private.action.canary";

    private static async Task<DoctorReport> RunFailure(string scenario, ManualClock clock)
    {
        await using var session = new FixtureSession(timeProvider: clock);
        var fixture = await session.RunAsync(scenario).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TurnOutcome.Failed, fixture.Sequence.Result!.Outcome);
        Assert.Equal(scenario == "slow" ? SequenceIssue.FirstEventDeadline : SequenceIssue.MissingTerminal,
            fixture.Sequence.Issue);
        Assert.False(fixture.ToneRequested);
        Assert.Null(fixture.Playback);
        var report = FixtureDiagnostics.Report(fixture, clock);
        Assert.Equal(1, report.ExitCode);
        var probe = Assert.Single(report.Probes);
        Assert.Equal(ProbeOutcome.Failed, probe.Outcome);
        Assert.Equal(EvidenceProvenance.Fixture, probe.Provenance);
        Assert.Equal("provider.stream.inspect", probe.Error!.ActionId);
        Assert.Equal("fixture.retry", probe.ActionId);
        return report;
    }

    [Theory]
    [InlineData("slow", false)]
    [InlineData("slow", true)]
    [InlineData("truncated", false)]
    [InlineData("truncated", true)]
    public async Task ActualSharedFailureReachesJournalPreviewAndExport(string scenario, bool injectCanaries)
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        var original = await RunFailure(scenario, clock);
        var probe = original.Probes[0];
        var report = injectCanaries ? original with
        {
            Probes = [probe with { Error = probe.Error! with { Summary = Fixtures.Canary, ActionId = PrivateAction } }]
        } : original;
        using var journal = DiagnosticJournal.Start(scope.Journal, clock: clock);
        var metadata = DiagnosticEvent.FromProbe(report.Probes[0], clock.Utc);
        Assert.Equal(scenario == "slow" ? "fixture.deadline" : "fixture.failed", metadata.Code);
        Assert.Equal(DiagnosticSeverity.Error, metadata.Severity);
        journal.Append(metadata);
        var selection = journal.Select(Fixtures.Range(clock));
        Assert.Equal(metadata, Assert.Single(selection.Records).Event);
        using var snapshot = SupportSnapshot.Freeze(Fixtures.Settings(), report, BuildMetadata.FromExecutingAssemblies(),
            selection, clock);
        var preview = snapshot.Preview("doctor.json");
        using var document = JsonDocument.Parse(preview);
        Assert.Equal(original.ExitCode, document.RootElement.GetProperty("shared_doctor_exit_code").GetInt32());
        var exportedProbe = document.RootElement.GetProperty("probes")[0];
        Assert.Equal("failed", exportedProbe.GetProperty("recorded_outcome").GetString());
        Assert.Equal("fixture", exportedProbe.GetProperty("provenance").GetString());
        Assert.Equal("fixture.retry", exportedProbe.GetProperty("action_id").GetString());
        foreach (var file in snapshot.Files)
        {
            var text = Encoding.UTF8.GetString(snapshot.Preview(file.Name));
            Assert.DoesNotContain("provider.stream.inspect", text);
            Assert.DoesNotContain(PrivateAction, text);
            Assert.DoesNotContain(Fixtures.Canary, text);
        }
        snapshot.Export(snapshot.Approve(snapshot.Id, snapshot.Digest, scope.Output), scope.Output);
        using var zip = ZipFile.OpenRead(scope.Output);
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            Assert.Equal(snapshot.Preview(entry.FullName), buffer.ToArray());
        }
    }

    [Theory]
    [InlineData("slow")]
    [InlineData("truncated")]
    public async Task ActualSharedFailureCanBeProjectedWithoutPriorLogMapping(string scenario)
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        var report = await RunFailure(scenario, clock);
        using var journal = DiagnosticJournal.Start(scope.Journal, clock: clock);
        using var snapshot = SupportSnapshot.Freeze(Fixtures.Settings(), report, BuildMetadata.FromExecutingAssemblies(),
            journal.Select(Fixtures.Range(clock)), clock);
        using var document = JsonDocument.Parse(snapshot.Preview("doctor.json"));
        Assert.Equal(report.ExitCode, document.RootElement.GetProperty("shared_doctor_exit_code").GetInt32());
    }

    [Theory]
    [InlineData("exported-code")]
    [InlineData("exported-action")]
    [InlineData("error-action-contract")]
    [InlineData("error-summary-contract")]
    [InlineData("error-code-contract")]
    [InlineData("error-stage-contract")]
    public async Task ExportedCatalogFieldsAndSharedErrorContractsRemainStrict(string invalid)
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        var report = await RunFailure("truncated", clock);
        var probe = report.Probes[0];
        var changed = invalid switch
        {
            "exported-code" => probe with { DiagnosticCode = PrivateAction },
            "exported-action" => probe with { ActionId = PrivateAction },
            "error-action-contract" => probe with { Error = probe.Error! with { ActionId = "not a valid identifier" } },
            "error-summary-contract" => probe with { Error = probe.Error! with { Summary = "\0" } },
            "error-code-contract" => probe with { Error = probe.Error! with { Code = (ErrorCode)int.MaxValue } },
            "error-stage-contract" => probe with { Error = probe.Error! with { Stage = Stage.Settings } },
            _ => throw new InvalidOperationException("Unknown authored test case.")
        };
        Fixtures.Failure(SupportFailure.InvalidData, () => DiagnosticEvent.FromProbe(changed, clock.Utc));
        using var journal = DiagnosticJournal.Start(scope.Journal, clock: clock);
        Fixtures.Failure(SupportFailure.InvalidData, () => SupportSnapshot.Freeze(Fixtures.Settings(),
            report with { Probes = [changed] }, BuildMetadata.FromExecutingAssemblies(),
            journal.Select(Fixtures.Range(clock)), clock));
    }
}
