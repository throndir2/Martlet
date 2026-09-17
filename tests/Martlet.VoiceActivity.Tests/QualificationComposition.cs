namespace Martlet.VoiceActivity.Tests;

// Not a test entry point. Binding is inert and grants neither permission nor native qualification.
// A future private host must have separate execution authorization before starting this analyzer.
internal static class QualificationComposition
{
    internal static CpuVoiceActivityAnalyzer Create(Guid sessionId, Guid profileId, Guid revision,
        string modelPath, VoiceActivityOptions? options = null, TimeProvider? timeProvider = null) =>
        new(sessionId, profileId, revision, modelPath, new SileroVadInferenceFactory(), options, timeProvider);
}
