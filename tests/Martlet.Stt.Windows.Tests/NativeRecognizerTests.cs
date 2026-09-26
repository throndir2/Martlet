namespace Martlet.Stt.Windows.Tests;

public sealed class NativeRecognizerTests
{
    [InstalledRecognizerFact]
    [Trait("Category", "InstalledRecognizer")]
    public async Task Explicitly_authorized_installed_engine_accepts_in_memory_silence_without_device_access()
    {
        await using var adapter = new WindowsOfflineSttAdapter();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var discovery = await adapter.GetInstalledRecognizersAsync(timeout.Token);
        Assert.False(discovery.Canceled);
        Assert.Null(discovery.Failure);
        Assert.NotEmpty(discovery.Recognizers);
        var selected = discovery.Recognizers[0];
        using var audio = AdapterTests.Audio();
        var request = AdapterTests.Request() with { RecognizerId = selected.Id };
        var result = await adapter.TranscribeAsync(request, audio, AdapterTests.Permit(request, audio), timeout.Token);
        Assert.Null(result.Failure);
        Assert.Equal(WindowsOfflineSttOutcome.NoSpeech, result.Outcome);
        Assert.Equal(WindowsOfflineSttProvenance.InstalledWindowsRecognizer, result.Provenance);
        Assert.Null(result.Text);
    }

    public sealed class InstalledRecognizerFactAttribute : FactAttribute
    {
        public InstalledRecognizerFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("MARTLET_WINDOWS_STT_NATIVE") != "1")
                Skip = "Explicit installed-recognizer probe not requested; no native recognition was run.";
        }
    }
}
