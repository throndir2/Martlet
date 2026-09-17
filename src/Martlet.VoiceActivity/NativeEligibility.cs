namespace Martlet.VoiceActivity;

public sealed class NativeAvailability
{
    public bool Eligible => false;
    public VoiceActivityFailure Failure { get; } = new(VoiceActivityFailureCode.NativePrivacyUnqualified);
    internal NativeAvailability() { }
}

internal static class NativeEligibility
{
    internal const string ModelId = "silero-vad-v6.2.1-16k-op15";
    internal static NativeAvailability Production { get; } = new();
}
