using Martlet.Core.Contracts;
using Martlet.Core.Streaming;
using System.Text;

namespace Martlet.Fixtures;

public static class FixtureRunner
{
    public static FixtureTrace Run(FixtureScenario scenario, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        // Use the same bounded JSON boundary as stored scripts, even for in-process callers.
        scenario = ContractJson.Read<FixtureScenario>(ContractJson.Write(scenario));
        var clock = new ScriptTimeProvider();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var validator = new ProviderSequenceValidator(scenario.Request, scenario.Limits, clock, cancellation.Token);
        var observations = new List<FixtureObservation>(scenario.Steps.Count + 1);
        var atMilliseconds = 0;
        foreach (var step in scenario.Steps)
        {
            if (cancellation.IsCancellationRequested)
                break;
            clock.AdvanceTo(step.AtMilliseconds);
            atMilliseconds = step.AtMilliseconds;
            var deliveries = new List<FixtureDelivery>();
            SequenceUpdate update;
            switch (step.Action)
            {
                case FixtureAction.Event:
                    update = validator.AcceptJson(ContractJson.Write(step.Event!), step.Overflow);
                    break;
                case FixtureAction.RawEvent:
                    update = validator.AcceptJson(Encoding.UTF8.GetBytes(step.RawEvent!), step.Overflow);
                    break;
                case FixtureAction.Drain:
                    for (var i = 0; i < step.ReadCount && validator.TryReadText(out var chunk); i++)
                        deliveries.Add(new(chunk!.Ids, chunk.Epoch, chunk.Sequence, chunk.Text.Length));
                    update = validator.Poll();
                    break;
                case FixtureAction.Stop:
                    update = validator.Stop();
                    break;
                case FixtureAction.Cancel:
                    cancellation.Cancel();
                    update = validator.Poll();
                    break;
                case FixtureAction.End:
                    update = validator.EndOfInput();
                    break;
                case FixtureAction.Suppress:
                    update = validator.Suppress(step.Suppression!.Value);
                    break;
                case FixtureAction.Retry:
                case FixtureAction.Replace:
                    validator.Poll();
                    validator.Transition(step.NextRequest!, step.Action == FixtureAction.Retry
                        ? AttemptTransition.Retry : AttemptTransition.Replace, cancellation.Token);
                    update = validator.Poll();
                    break;
                default:
                    update = validator.Poll();
                    break;
            }
            observations.Add(new(step.AtMilliseconds, step.Action, update.Decision, update.Snapshot, deliveries.AsReadOnly()));
        }
        var final = validator.EndOfInput();
        observations.Add(new(atMilliseconds, FixtureAction.End, final.Decision, final.Snapshot, []));
        var trace = new FixtureTrace
        {
            Version = ContractVersion.Current, Name = scenario.Name, Label = FixtureScenario.EvidenceLabel,
            Provenance = EvidenceProvenance.Fixture, Observations = observations.AsReadOnly(), Final = final.Snapshot
        };
        // Bound the entire retained/serializable trace, not just individual observations.
        return ContractJson.Read<FixtureTrace>(ContractJson.Write(trace));
    }

    private sealed class ScriptTimeProvider : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public void AdvanceTo(int milliseconds) => ticks = TimeSpan.FromMilliseconds(milliseconds).Ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new NotSupportedException("Fixture scripts advance explicitly; they never create real timers.");
    }
}
