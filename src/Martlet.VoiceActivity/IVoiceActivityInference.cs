namespace Martlet.VoiceActivity;

internal interface IVoiceActivityInferenceFactory
{
    VoiceActivityEvidence Evidence { get; }
    // Construction is inert; native/file access belongs to Load on the owning worker.
    IVoiceActivityInferenceSession Create();
}

internal interface IVoiceActivityInferenceSession : IDisposable
{
    void Load(string modelPath, VoiceActivityInferenceAccess access);
    float Score(ReadOnlySpan<float> window);
    // May run concurrently with Load/Score. The owner tracks its actual completion.
    void RequestCancellation();
}

internal sealed class VoiceActivityInferenceAccess(Action check)
{
    internal void Check() => check();
}
