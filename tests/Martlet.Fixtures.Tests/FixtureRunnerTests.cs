using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Streaming;
using static Martlet.Fixtures.Tests.SequenceTestData;

namespace Martlet.Fixtures.Tests;

public sealed class FixtureRunnerTests
{
    public static TheoryData<string, TurnOutcome, SequenceIssue, ErrorCode?> CatalogCases => new()
    {
        { "complete", TurnOutcome.Completed, SequenceIssue.None, null },
        { "streaming", TurnOutcome.Completed, SequenceIssue.None, null },
        { "no-speech", TurnOutcome.Suppressed, SequenceIssue.None, null },
        { "refused", TurnOutcome.Refused, SequenceIssue.None, null },
        { "suppressed", TurnOutcome.Suppressed, SequenceIssue.None, null },
        { "failed", TurnOutcome.Failed, SequenceIssue.ProviderFailure, ErrorCode.ProviderFailed },
        { "canceled", TurnOutcome.Canceled, SequenceIssue.None, null },
        { "first-deadline", TurnOutcome.Failed, SequenceIssue.FirstEventDeadline, ErrorCode.ProviderFailed },
        { "idle-deadline", TurnOutcome.Failed, SequenceIssue.IdleDeadline, ErrorCode.ProviderFailed },
        { "total-deadline", TurnOutcome.Failed, SequenceIssue.TotalDeadline, ErrorCode.ProviderFailed },
        { "duplicate", TurnOutcome.Completed, SequenceIssue.None, null },
        { "conflicting-duplicate", TurnOutcome.Failed, SequenceIssue.ConflictingDuplicate, ErrorCode.InvalidContract },
        { "out-of-order", TurnOutcome.Failed, SequenceIssue.SequenceGap, ErrorCode.StreamTruncated },
        { "wrong-ids", TurnOutcome.Failed, SequenceIssue.WrongCorrelation, ErrorCode.InvalidContract },
        { "wrong-epoch", TurnOutcome.Failed, SequenceIssue.WrongEpoch, ErrorCode.InvalidContract },
        { "late-after-stop", TurnOutcome.Canceled, SequenceIssue.None, null },
        { "late-after-replace", TurnOutcome.Completed, SequenceIssue.None, null },
        { "truncated", TurnOutcome.Failed, SequenceIssue.MissingTerminal, ErrorCode.StreamTruncated },
        { "unsupported", TurnOutcome.Failed, SequenceIssue.UnsupportedCapability, ErrorCode.ProviderCapability },
        { "unknown", TurnOutcome.Failed, SequenceIssue.UnknownCapability, ErrorCode.ProviderCapability },
        { "malformed", TurnOutcome.Failed, SequenceIssue.InvalidInput, ErrorCode.InvalidContract },
        { "text-limit", TurnOutcome.Failed, SequenceIssue.TextLimit, ErrorCode.PayloadTooLarge },
        { "event-limit", TurnOutcome.Failed, SequenceIssue.EventLimit, ErrorCode.PayloadTooLarge },
        { "backpressure", TurnOutcome.Completed, SequenceIssue.None, null },
        { "overflow", TurnOutcome.Failed, SequenceIssue.QueueOverflow, ErrorCode.PayloadTooLarge },
        { "empty", TurnOutcome.Failed, SequenceIssue.EmptyCompletion, ErrorCode.InvalidContract },
        { "wrong-provenance", TurnOutcome.Failed, SequenceIssue.WrongProvenance, ErrorCode.InvalidContract },
        { "stt-partial", TurnOutcome.Completed, SequenceIssue.None, null },
        { "refused-after-partial", TurnOutcome.Refused, SequenceIssue.None, null },
        { "version", TurnOutcome.Failed, SequenceIssue.InvalidInput, ErrorCode.UnsupportedVersion }
    };

    [Theory]
    [MemberData(nameof(CatalogCases))]
    public void CatalogExecutesActualProductionIngressAndDeterministicSerialization(
        string name, TurnOutcome outcome, SequenceIssue issue, ErrorCode? code)
    {
        var scenario = ContractJson.Read<FixtureScenario>(ContractJson.Write(FixtureCatalog.Create(name)));
        var trace = FixtureRunner.Run(scenario);
        Assert.Equal(outcome, trace.Final.Result!.Outcome);
        Assert.Equal(issue, trace.Final.Issue);
        Assert.Equal(code, trace.Final.Result.Error?.Code);
        Assert.Equal(EvidenceProvenance.Fixture, trace.Provenance);
        Assert.Equal(FixtureScenario.EvidenceLabel, trace.Label);
        Assert.All(trace.Observations, item =>
        {
            Assert.InRange(item.Snapshot.QueuedChunks, 0, scenario.Limits.MaxQueuedChunks);
            if (item.Snapshot.Result is not null)
                Assert.Equal(EvidenceProvenance.Fixture, item.Snapshot.Result.Provenance);
        });
        var bytes = ContractJson.Write(trace);
        Assert.Equal(bytes, ContractJson.Write(FixtureRunner.Run(scenario)));
        Assert.Equal(bytes, ContractJson.Write(ContractJson.Read<FixtureTrace>(bytes)));
        Assert.True(bytes.Length <= ContractRules.MaxJsonBytes);
    }

    [Fact]
    public void CatalogExpectedResultsCoverEveryEntry()
    {
        Assert.Equal(FixtureCatalog.Names.Order(), CatalogCases.Select(x => (string)x[0]).Order());
        Assert.Throws<ContractException>(() => FixtureCatalog.Create("unknown-name"));
    }

    [Fact]
    public void ReplayAndBackpressureScenariosProveExactlyOnceDelivery()
    {
        foreach (var name in new[] { "streaming", "duplicate", "backpressure" })
        {
            var trace = FixtureRunner.Run(FixtureCatalog.Create(name));
            var delivered = trace.Observations.SelectMany(x => x.Deliveries).ToArray();
            Assert.Equal(new long[] { 1, 2 }, delivered.Select(x => x.Sequence));
            Assert.Equal(15, delivered.Sum(x => x.Characters));
            Assert.Equal(2, trace.Final.DeliveredChunks);
            Assert.Equal(1, trace.Final.PeakQueuedChunks);
        }
        Assert.Contains(FixtureRunner.Run(FixtureCatalog.Create("duplicate")).Observations,
            x => x.Decision == SequenceDecision.DuplicateDiscarded);
        Assert.Contains(FixtureRunner.Run(FixtureCatalog.Create("backpressure")).Observations,
            x => x.Decision == SequenceDecision.Backpressured);
    }

    [Fact]
    public void FixtureTraceDoesNotCollectPayloadText()
    {
        const string canary = "SYNTHETIC_CONTENT_CANARY_do_not_log";
        var request = Request();
        var scenario = Scenario("canary", request,
            Emit(request, ProviderEventKind.Started, 0),
            Emit(request, ProviderEventKind.TextDelta, 1, canary),
            Act(FixtureAction.Drain), Act(FixtureAction.End));
        var json = Encoding.UTF8.GetString(ContractJson.Write(FixtureRunner.Run(scenario)));
        Assert.DoesNotContain(canary, json);
        Assert.Contains("FIXTURE - NOT AI", json);
        Assert.DoesNotContain("ready", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CancellationTokenStopsRunnerBeforeAnyDelivery()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        foreach (var name in FixtureCatalog.Names)
        {
            var trace = FixtureRunner.Run(FixtureCatalog.Create(name), canceled.Token);
            Assert.Equal(TurnOutcome.Canceled, trace.Final.Result!.Outcome);
            Assert.Equal(0, trace.Final.IngressEvents);
            Assert.Empty(trace.Observations.SelectMany(x => x.Deliveries));
        }
    }

    [Fact]
    public void ScenarioBoundsAndProvenanceAreEnforcedBeforeExecution()
    {
        var scenario = FixtureCatalog.Create("complete");
        var invalid = new[]
        {
            scenario with { Label = "AI ready" },
            scenario with { Provenance = EvidenceProvenance.Live },
            scenario with { Request = scenario.Request with { Capabilities = scenario.Request.Capabilities with { Provenance = EvidenceProvenance.Live } } },
            scenario with { Steps = Enumerable.Repeat(scenario.Steps[0], 129).ToArray() },
            scenario with { Steps = [Act(FixtureAction.Poll, 10), Act(FixtureAction.Poll, 9)] },
            scenario with { Steps = [Act(FixtureAction.Poll, 900_001)] },
            scenario with { Steps = [new() { AtMilliseconds = 0, Action = FixtureAction.Event }] },
            scenario with { Limits = new() { MaxQueuedChunks = 129 } },
            scenario with { Limits = new() { TotalTimeout = TimeSpan.FromMinutes(6) } }
        };
        foreach (var value in invalid)
            Assert.Throws<ContractException>(() => FixtureRunner.Run(value));
        Assert.Equal(ErrorCode.PayloadTooLarge, Assert.Throws<ContractException>(() =>
            ContractJson.Read<FixtureScenario>(new byte[ContractRules.MaxJsonBytes + 1])).Code);
    }

    [Fact]
    public void TamperedTraceCannotClaimLiveReadinessOrForgedFinalSnapshot()
    {
        var trace = FixtureRunner.Run(FixtureCatalog.Create("complete"));
        Assert.Throws<ContractException>(() => ContractJson.Write(trace with { Provenance = EvidenceProvenance.Live }));
        Assert.Throws<ContractException>(() => ContractJson.Write(trace with { Final = trace.Final with { AcceptedEvents = 4000 } }));
        Assert.Throws<ContractException>(() => ContractJson.Write(trace with
        {
            Final = trace.Final with { Result = trace.Final.Result! with { Provenance = EvidenceProvenance.Live } }
        }));
    }

    [Theory]
    [InlineData("complete.json", TurnOutcome.Completed)]
    [InlineData("refused.json", TurnOutcome.Refused)]
    [InlineData("no-speech.json", TurnOutcome.Suppressed)]
    public void StoredScriptsUseTheSameCoreSerializationAndRunner(string file, TurnOutcome expected)
    {
        var input = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", file));
        var scenario = ContractJson.Read<FixtureScenario>(input);
        var trace = FixtureRunner.Run(scenario);
        Assert.Equal(expected, trace.Final.Result!.Outcome);
        Assert.Equal(ContractJson.Write(trace), ContractJson.Write(FixtureRunner.Run(FixtureCatalog.Create(scenario.Name))));
    }
}
