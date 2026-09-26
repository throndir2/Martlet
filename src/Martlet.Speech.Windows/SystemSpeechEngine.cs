using System.Speech.AudioFormat;
using System.Speech.Synthesis;

namespace Martlet.Speech.Windows;

internal interface IWindowsSpeechEngine : IDisposable
{
    IReadOnlyList<WindowsSpeechVoice> GetVoices();
    void Synthesize(string voiceId, string text, Stream output, Action ensureActive);
}

internal sealed class SystemSpeechEngine : IWindowsSpeechEngine
{
    private readonly SpeechSynthesizer synthesizer = new();

    public IReadOnlyList<WindowsSpeechVoice> GetVoices() =>
        Array.AsReadOnly(synthesizer.GetInstalledVoices().Where(v => v.Enabled)
            .Select(v => new WindowsSpeechVoice(v.VoiceInfo.Id, v.VoiceInfo.Name, v.VoiceInfo.Culture.Name))
            .OrderBy(v => v.Id, StringComparer.Ordinal).ToArray());

    public void Synthesize(string voiceId, string text, Stream output, Action ensureActive)
    {
        ensureActive();
        var voices = GetVoices();
        var selected = voices.SingleOrDefault(v => v.Id == voiceId);
        if (selected is null || voices.Count(v => v.Name == selected.Name) != 1)
            throw new WindowsSpeechException(WindowsSpeechFailure.VoiceUnavailable);
        synthesizer.SelectVoice(selected.Name);
        if (synthesizer.Voice.Id != selected.Id)
            throw new WindowsSpeechException(WindowsSpeechFailure.VoiceUnavailable);
        // Raw, explicitly converted PCM. Never select the default audio device or use SSML.
        synthesizer.SetOutputToAudioStream(output,
            new SpeechAudioFormatInfo(24_000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        using var completed = new ManualResetEventSlim();
        SpeakCompletedEventArgs? terminal = null;
        void Finished(object? sender, SpeakCompletedEventArgs args)
        {
            terminal = args;
            completed.Set();
        }
        synthesizer.SpeakCompleted += Finished;
        try
        {
            ensureActive();
            synthesizer.SpeakAsync(text);
            try
            {
                while (!completed.Wait(TimeSpan.FromMilliseconds(20))) ensureActive();
                ensureActive();
            }
            catch (Exception error) when (error is OperationCanceledException or WindowsSpeechException)
            {
                synthesizer.SpeakAsyncCancelAll();
                // Do not release the stream/native owner while a callback may still write to it.
                completed.Wait();
                throw;
            }
            if (terminal?.Error is not null || terminal?.Cancelled != false)
                throw new WindowsSpeechException(WindowsSpeechFailure.EngineFailed);
        }
        finally { synthesizer.SpeakCompleted -= Finished; }
    }

    public void Dispose() => synthesizer.Dispose();
}
