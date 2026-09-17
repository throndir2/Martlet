namespace Martlet.VoiceActivity;

public sealed record VoiceActivityOptions
{
    public const int SampleRate = 16_000;
    public const int WindowSamples = 512;
    public const int ContextSamples = 64;
    public const int HardMaximumSamples = 480_000;
    public const int HardMaximumScores = 938;
    public const int HardMaximumSegments = 128;

    public int MaximumInputSamples { get; init; } = HardMaximumSamples;
    public int MaximumSegments { get; init; } = HardMaximumSegments;
    public float SpeechOnThreshold { get; init; } = 0.50f;
    public float SpeechOffThreshold { get; init; } = 0.35f;
    public int MinimumSpeechSamples { get; init; } = 4096;
    public int EndSilenceSamples { get; init; } = 9600;
    public int PreRollSamples { get; init; } = 8000;
    public TimeSpan ProcessingBudget { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ObservationWait { get; init; } = TimeSpan.FromSeconds(2);

    public void Validate()
    {
        VadCheck.Require(MaximumInputSamples is >= 1 and <= HardMaximumSamples &&
            MaximumSegments is >= 1 and <= HardMaximumSegments &&
            float.IsFinite(SpeechOnThreshold) && float.IsFinite(SpeechOffThreshold) &&
            SpeechOffThreshold >= 0 && SpeechOffThreshold < SpeechOnThreshold && SpeechOnThreshold <= 1 &&
            MinimumSpeechSamples is >= 1 and <= 16_000 &&
            EndSilenceSamples is >= 1 and <= 32_000 &&
            PreRollSamples is >= 0 and <= 8000 &&
            ProcessingBudget > TimeSpan.Zero && ProcessingBudget <= TimeSpan.FromSeconds(10) &&
            ObservationWait > TimeSpan.Zero && ObservationWait <= TimeSpan.FromSeconds(2));
    }
}
