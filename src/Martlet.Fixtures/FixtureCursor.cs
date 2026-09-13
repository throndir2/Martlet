using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Streaming;

namespace Martlet.Fixtures;

public sealed record FixtureStepResult(FixtureObservation Observation, IReadOnlyList<ValidatedTextChunk> Text);

// A single serialized owner advances this cursor. Presentation pacing never changes script time.
public sealed class FixtureCursor : IDisposable
{
    private readonly FixtureScenario scenario;
    private readonly ScriptTimeProvider clock = new();
    private readonly CancellationTokenSource cancellation;
    private readonly ProviderSequenceValidator validator;
    private readonly List<FixtureObservation> observations = [];
    private int index;
    private int atMilliseconds;
    private bool finished;

    public FixtureCursor(FixtureScenario scenario, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        this.scenario = ContractJson.Read<FixtureScenario>(ContractJson.Write(scenario));
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        validator = new(this.scenario.Request, this.scenario.Limits, clock, cancellation.Token);
    }

    public SequenceSnapshot Snapshot => validator.Snapshot;
    public string? RefusalText => validator.RefusalText;
    public int? NextMilliseconds => !finished && !cancellation.IsCancellationRequested && index < scenario.Steps.Count
        ? scenario.Steps[index].AtMilliseconds : null;

    public FixtureStepResult Advance()
    {
        if (finished || index >= scenario.Steps.Count)
            throw new InvalidOperationException("The fixture has no remaining step.");
        var step = scenario.Steps[index++];
        clock.AdvanceTo(step.AtMilliseconds);
        atMilliseconds = step.AtMilliseconds;
        var text = new List<ValidatedTextChunk>();
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
                    text.Add(chunk!);
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
        var observation = new FixtureObservation(atMilliseconds, step.Action, update.Decision, update.Snapshot,
            text.Select(chunk => new FixtureDelivery(chunk.Ids, chunk.Epoch, chunk.Sequence, chunk.Text.Length)).ToArray());
        observations.Add(observation);
        return new(observation, text.AsReadOnly());
    }

    public void Stop()
    {
        var update = validator.Stop();
        if (finished)
            observations[^1] = new(atMilliseconds, FixtureAction.Stop, update.Decision, update.Snapshot, []);
    }

    public FixtureTrace Finish()
    {
        if (!finished)
        {
            var final = validator.EndOfInput();
            observations.Add(new(atMilliseconds, FixtureAction.End, final.Decision, final.Snapshot, []));
            finished = true;
        }
        var trace = new FixtureTrace
        {
            Version = ContractVersion.Current, Name = scenario.Name, Label = FixtureScenario.EvidenceLabel,
            Provenance = EvidenceProvenance.Fixture, Observations = observations.ToArray(), Final = observations[^1].Snapshot
        };
        return ContractJson.Read<FixtureTrace>(ContractJson.Write(trace));
    }

    public void Dispose()
    {
        validator.Dispose();
        cancellation.Dispose();
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
