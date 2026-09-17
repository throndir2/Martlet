using Martlet.Core.Contracts;

namespace Martlet.VoiceActivity;

public sealed record VoiceActivityBinding(CorrelationIds Ids, long Epoch, Guid ProfileId,
    Guid ConfigurationRevision, int SampleCount)
{
    public int StartSample => 0;
    public int EndSampleExclusive => SampleCount;

    public void Validate()
    {
        VadCheck.Require(Ids is not null && Ids.SessionId != Guid.Empty && Ids.TurnId != Guid.Empty &&
            Ids.RequestId != Guid.Empty && Epoch is >= 0 and <= int.MaxValue &&
            ProfileId != Guid.Empty && ConfigurationRevision != Guid.Empty &&
            SampleCount is >= 1 and <= VoiceActivityOptions.HardMaximumSamples, VoiceActivityFailureCode.InvalidBinding);
    }
}

public readonly record struct VoiceActivityWindowScore(int SampleOffset, int ValidSamples, float ActivityScore)
{
    public int PaddedSamples => VoiceActivityOptions.WindowSamples - ValidSamples;
    public void Validate()
    {
        VadCheck.Require(SampleOffset >= 0 && ValidSamples is >= 1 and <= VoiceActivityOptions.WindowSamples &&
            SampleOffset <= VoiceActivityOptions.HardMaximumSamples - ValidSamples, VoiceActivityFailureCode.InvalidInput);
        VadCheck.Require(float.IsFinite(ActivityScore) && ActivityScore is >= 0 and <= 1, VoiceActivityFailureCode.InvalidScore);
    }
}

public enum SpeechEndpointReason { Silence, EndOfInput, MaximumInput }
public readonly record struct SpeechSegment(int SpeechStartSample, int SpeechEndSampleExclusive,
    int RangeStartSample, int RangeEndSampleExclusive, int DecisionSampleOffset, SpeechEndpointReason Reason);
public enum VoiceActivityOutcome { ActivityDetected, NoActivityDetected, Canceled, Failed }
public enum VoiceActivityEvidence { InternalFakeInference, NativeQualification }
public enum VoiceActivityOwnershipState { Pending, Released, Quarantined }

public sealed record VoiceActivityCompletion(VoiceActivityOutcome Outcome, VoiceActivityOwnershipState Ownership,
    VoiceActivityFailure? Failure);
public sealed record VoiceActivityOwnershipRelease(bool Released, VoiceActivityFailure? Failure);
public sealed record VoiceActivitySnapshot(bool IsTerminal, VoiceActivityOutcome? Outcome,
    VoiceActivityOwnershipState Ownership, VoiceActivityFailure? Failure, int RetainedPcmBytes);

public sealed class VoiceActivityResult
{
    public VoiceActivityBinding Binding { get; }
    public string ModelId => NativeEligibility.ModelId;
    public VoiceActivityEvidence Evidence { get; }
    public VoiceActivityOutcome Outcome => Segments.Count == 0 ? VoiceActivityOutcome.NoActivityDetected : VoiceActivityOutcome.ActivityDetected;
    public IReadOnlyList<VoiceActivityWindowScore> Scores { get; }
    public IReadOnlyList<SpeechSegment> Segments { get; }

    internal VoiceActivityResult(VoiceActivityBinding binding, VoiceActivityEvidence evidence,
        IEnumerable<VoiceActivityWindowScore> scores, IEnumerable<SpeechSegment> segments)
    {
        Binding = binding;
        Evidence = evidence;
        Scores = Array.AsReadOnly(scores.ToArray());
        Segments = Array.AsReadOnly(segments.ToArray());
    }

    public override string ToString() => $"{nameof(VoiceActivityResult)}: {Outcome}";
}
