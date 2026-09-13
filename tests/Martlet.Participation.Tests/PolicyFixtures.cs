namespace Martlet.Participation.Tests;

internal sealed class ManualClock : TimeProvider
{
    private long timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public DateTimeOffset Utc { get; set; } = DateTimeOffset.UnixEpoch;
    public int UtcReads { get; private set; }
    public override long GetTimestamp() => Interlocked.Read(ref timestamp);
    public override DateTimeOffset GetUtcNow()
    {
        UtcReads++;
        return Utc;
    }
    public void Advance(TimeSpan duration) => Interlocked.Add(ref timestamp, duration.Ticks);
}

internal static class PolicyFixtures
{
    internal static ParticipationState Consented => new()
    {
        CaptureAuthorized = true,
        TranscriptionAuthorized = true,
        TextDestinationAuthorized = true,
        AuthorizationRevision = 1
    };

    internal static ParticipationPolicy Policy(ManualClock clock,
        ParticipationMode mode = ParticipationMode.Conversational, ParticipationState? state = null) =>
        new(new Guid("40000000-0000-0000-0000-000000000005"), new(mode: mode), state ?? Consented, clock);

    internal static ParticipationInput Ambient(string text = "Does anyone know the next move?",
        double? confidence = 0.9, AudioOrigin origin = AudioOrigin.External) =>
        new(InputSource.AmbientSpeech, new(text, confidence: confidence), origin);

    internal static ParticipationInput Ptt(string text = "What is the next move?", double? confidence = null) =>
        new(InputSource.PushToTalkControl, new(text, confidence: confidence));

    internal static ParticipationInput Typed(string text = "What is the next move?") =>
        new(InputSource.TypedControl, new(text), trustedTypedAddress: true);

    internal static ParticipationDecision Decision(this ParticipationPolicy policy, ParticipationInput input) =>
        policy.Evaluate(policy.CreateIntent(input));

    internal static DispatchLease Accept(this ParticipationPolicy policy, ParticipationInput input)
    {
        var decision = policy.Decision(input);
        Assert.Equal(DecisionKind.Allow, decision.Kind);
        var committed = policy.TryCommit(decision);
        Assert.True(committed.Accepted);
        return Assert.IsType<DispatchLease>(committed.Lease);
    }
}
