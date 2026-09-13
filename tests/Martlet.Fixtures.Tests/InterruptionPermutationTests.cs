using Martlet.Core.Contracts;
using Martlet.Core.Streaming;
using static Martlet.Fixtures.Tests.SequenceTestData;

namespace Martlet.Fixtures.Tests;

public sealed class InterruptionPermutationTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var capability in Enum.GetValues<CancellationCapability>())
        foreach (var interruption in new[] { FixtureAction.Stop, FixtureAction.Replace, FixtureAction.End })
        foreach (var eagerConsumer in new[] { false, true })
        for (var cut = 1; cut <= 5; cut++)
            yield return [capability, interruption, eagerConsumer, cut];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void DeterministicStopReplaceDisconnectMatrixNeverDeliversRetiredOrDuplicateText(
        CancellationCapability capability, FixtureAction interruption, bool eagerConsumer, int cut)
    {
        var request = Request(cancellation: capability);
        var steps = new List<FixtureStep> { Emit(request, ProviderEventKind.Started, 0) };
        for (var seq = 1; seq <= cut; seq++)
        {
            var delta = Emit(request, ProviderEventKind.TextDelta, seq, $"Authored chunk {seq}.", seq * 10);
            steps.Add(delta);
            // A held consumer fills the one-chunk queue; retry the unconsumed event after draining.
            if (!eagerConsumer && seq > 1)
            {
                steps.Add(Act(FixtureAction.Drain, seq * 10));
                steps.Add(delta);
            }
            steps.Add(delta);
            if (eagerConsumer)
                steps.Add(Act(FixtureAction.Drain, seq * 10));
        }
        var at = cut * 10 + 1;
        var next = Request(epoch: 1, turn: 4, attempt: 5, cancellation: capability);
        var interruptionIndex = steps.Count;
        steps.Add(interruption == FixtureAction.Replace
            ? new() { AtMilliseconds = at, Action = interruption, NextRequest = next }
            : Act(interruption, at));
        steps.Add(Emit(request, ProviderEventKind.TextDelta, cut + 1, "Late retired chunk.", at + 1));
        steps.Add(Emit(request, ProviderEventKind.Completed, cut + 2, at: at + 2));
        steps.Add(Act(FixtureAction.Drain, at + 3));
        if (interruption == FixtureAction.Replace)
        {
            steps.Add(Emit(next, ProviderEventKind.Started, 0, at: at + 4));
            steps.Add(Emit(next, ProviderEventKind.Completed, 1, "New current chunk.", at + 5));
            steps.Add(Act(FixtureAction.Drain, at + 6));
        }
        var scenario = Scenario($"matrix-{capability}-{interruption}-{eagerConsumer}-{cut}", request, steps.ToArray())
            with { Limits = new() { MaxQueuedChunks = 1 } };
        var trace = FixtureRunner.Run(scenario);
        var oldDeliveries = trace.Observations.SelectMany(x => x.Deliveries).Where(x => x.Ids == request.Ids).ToArray();
        Assert.Equal(eagerConsumer ? cut : cut - 1, oldDeliveries.Length);
        Assert.Equal(oldDeliveries.Length, oldDeliveries.Select(x => (x.Ids, x.Epoch, x.Sequence)).Distinct().Count());
        Assert.DoesNotContain(trace.Observations.Skip(interruptionIndex).SelectMany(x => x.Deliveries), x => x.Ids == request.Ids);
        Assert.Equal(cut, trace.Observations.Count(x => x.Decision == SequenceDecision.DuplicateDiscarded));
        Assert.Equal(!eagerConsumer ? cut - 1 : 0, trace.Observations.Count(x => x.Decision == SequenceDecision.Backpressured));
        var expectedOutcome = interruption switch
        {
            FixtureAction.Replace => TurnOutcome.Completed,
            FixtureAction.End => TurnOutcome.Failed,
            _ => TurnOutcome.Canceled
        };
        Assert.Equal(expectedOutcome, trace.Final.Result!.Outcome);
        if (interruption == FixtureAction.End)
            Assert.Equal(ErrorCode.StreamTruncated, trace.Final.Result.Error!.Code);
        if (interruption == FixtureAction.Replace)
        {
            Assert.Equal(2, trace.Observations.Count(x => x.Decision == SequenceDecision.StaleDiscarded));
            Assert.Equal(next.Ids, Assert.Single(trace.Observations.SelectMany(x => x.Deliveries), x => x.Ids != request.Ids).Ids);
        }
        else
        {
            Assert.Equal(2, trace.Observations.Count(x => x.Decision == SequenceDecision.ClosedDiscarded));
            Assert.Equal(capability, trace.Final.CancellationCapability);
            Assert.Equal(capability switch
            {
                CancellationCapability.RequestAbort => CancellationRequest.AbortRequest,
                CancellationCapability.CooperativeComputeCancel => CancellationRequest.CooperativeComputeCancel,
                _ => CancellationRequest.LocalDiscardOnly
            }, trace.Final.CancellationRequested);
        }
        Assert.All(trace.Observations, x => Assert.InRange(x.Snapshot.PeakQueuedChunks, 0, 1));
        Assert.Equal(ContractJson.Write(trace), ContractJson.Write(FixtureRunner.Run(scenario)));
    }
}
