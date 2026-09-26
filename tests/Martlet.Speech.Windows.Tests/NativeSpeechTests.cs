using Martlet.Core.Contracts;
using Martlet.Providers;
using Martlet.Speech.Windows;
using Xunit.Abstractions;

namespace Martlet.Speech.Windows.Tests;

public sealed class NativeSpeechTests(ITestOutputHelper output)
{
    [NativeSpeechFact]
    [Trait("Category", "NativeWindowsSpeech")]
    public async Task Installed_engine_synthesizes_real_bounded_pcm_to_memory_without_playback()
    {
        await using var adapter = new WindowsSpeechSynthesisAdapter();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var voices = await adapter.GetInstalledVoicesAsync(timeout.Token);
        Assert.NotEmpty(voices);
        var voice = voices[0];
        var action = WindowsSpeechTests.Action();
        var consent = new WindowsSpeechAuthorization(action.Context, voice.Id, action.Input,
            action.Limits, action.Context.Deadline, true);
        var result = await adapter.SynthesizeAsync(action.Context, voice.Id, action.Input,
            action.Limits, consent, timeout.Token);
        Assert.Equal(WindowsSpeechFailure.None, result.Failure);
        Assert.Equal(SpeechSynthesisOutcome.Completed, result.Outcome);
        Assert.Equal(EvidenceProvenance.Live, result.Provenance);
        Assert.True(result.OwnershipReleased);
        var frames = await WindowsSpeechTests.Read(Assert.IsType<WindowsSpeechAudio>(result.Audio));
        Assert.NotEmpty(frames);
        Assert.True(frames.Sum(f => f.SamplesPerChannel) > 2400);
        Assert.True(frames.Sum(f => f.Data.Length) <= action.Limits.MaxSamples * 2);
        Assert.Contains(frames, frame => frame.Data.ToArray().Any(b => b != 0));
        Assert.All(frames, frame => Assert.Equal(WindowsSpeechSynthesisAdapter.Format, frame.Format));
        output.WriteLine($"Native installed engine: {voices.Count} available voices; {frames.Sum(f => f.SamplesPerChannel)} real PCM samples at 24000 Hz mono PCM16. Memory only; no playback/microphone.");
    }

    [NativeSpeechFact]
    [Trait("Category", "NativeWindowsSpeech")]
    public async Task Missing_native_voice_fails_without_fallback()
    {
        await using var adapter = new WindowsSpeechSynthesisAdapter();
        var action = WindowsSpeechTests.Action();
        var result = await WindowsSpeechTests.Synthesize(adapter, action);
        Assert.Equal(WindowsSpeechFailure.VoiceUnavailable, result.Failure);
        Assert.Null(result.Audio);
    }

    [NativeSpeechFact]
    [Trait("Category", "NativeWindowsSpeech")]
    public async Task Native_output_limit_cancels_and_releases_without_publishing_partial_speech()
    {
        await using var adapter = new WindowsSpeechSynthesisAdapter();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var voices = await adapter.GetInstalledVoicesAsync(timeout.Token);
        Assert.NotEmpty(voices);
        var action = WindowsSpeechTests.Action(new() { MaxAudioBytes = 2 });
        var consent = new WindowsSpeechAuthorization(action.Context, voices[0].Id, action.Input,
            action.Limits, action.Context.Deadline, true);
        var result = await adapter.SynthesizeAsync(action.Context, voices[0].Id, action.Input,
            action.Limits, consent, timeout.Token);
        Assert.Equal(WindowsSpeechFailure.AudioLimit, result.Failure);
        Assert.Equal(SpeechSynthesisOutcome.Failed, result.Outcome);
        Assert.True(result.OwnershipReleased);
        Assert.Null(result.Audio);
    }
}

public sealed class NativeSpeechFactAttribute : FactAttribute
{
    public NativeSpeechFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MARTLET_NATIVE_WINDOWS_SPEECH") != "1")
            Skip = "Explicit installed-engine memory-only execution is not enabled.";
    }
}
