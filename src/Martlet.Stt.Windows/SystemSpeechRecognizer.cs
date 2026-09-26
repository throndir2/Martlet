using System.Collections.Immutable;
using System.Speech.Recognition;
using System.Text;
using Martlet.LocalStt;

namespace Martlet.Stt.Windows;

internal sealed class SystemSpeechRecognizerFactory : IOfflineRecognizerFactory
{
    public WindowsOfflineSttProvenance Provenance => WindowsOfflineSttProvenance.InstalledWindowsRecognizer;

    public ImmutableArray<WindowsOfflineRecognizer> Discover()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        return [.. SpeechRecognitionEngine.InstalledRecognizers()
            .Select(info => new WindowsOfflineRecognizer(info.Id, info.Name, info.Culture.Name))];
    }

    public IOfflineRecognizerSession Create(string recognizerId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        var selected = SpeechRecognitionEngine.InstalledRecognizers()
            .SingleOrDefault(info => info.Id == recognizerId)
            ?? throw new RecognizerUnavailableException();
        return new SystemSpeechRecognizerSession(new SpeechRecognitionEngine(selected));
    }
}

internal sealed class SystemSpeechRecognizerSession(SpeechRecognitionEngine engine) : IOfflineRecognizerSession
{
    public string Recognize(Stream wave, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        engine.LoadGrammar(new DictationGrammar());
        engine.InitialSilenceTimeout = CanonicalWaveAudio.MaximumDuration;
        engine.BabbleTimeout = CanonicalWaveAudio.MaximumDuration;
        engine.SetInputToWaveStream(wave);
        using var completed = new ManualResetEvent(false);
        var gate = new object();
        var text = new StringBuilder();
        Exception? failure = null;
        var canceled = false;
        void OnRecognized(object? sender, SpeechRecognizedEventArgs args)
        {
            lock (gate)
            {
                var segment = args.Result.Text;
                if (failure is not null || string.IsNullOrWhiteSpace(segment))
                    return;
                if ((long)text.Length + segment.Length + (text.Length == 0 ? 0 : 1) >
                    LocalSttPackageManifest.MaximumTranscriptCharacters)
                {
                    failure = new TranscriptLimitException();
                    return;
                }
                if (text.Length != 0)
                    text.Append(' ');
                text.Append(segment);
            }
        }
        void OnCompleted(object? sender, RecognizeCompletedEventArgs args)
        {
            lock (gate)
            {
                failure ??= args.Error;
                canceled = args.Cancelled;
                completed.Set();
            }
        }
        engine.SpeechRecognized += OnRecognized;
        engine.RecognizeCompleted += OnCompleted;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            engine.RecognizeAsync(RecognizeMode.Multiple);
            if (WaitHandle.WaitAny([completed, cancellationToken.WaitHandle]) == 1)
            {
                engine.RecognizeAsyncCancel();
                // Keep ownership until SAPI has stopped reading the utterance; never detach a timed-out engine.
                completed.WaitOne();
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (failure is TranscriptLimitException)
                    throw new TranscriptLimitException();
                if (failure is not null)
                    throw new InvalidOperationException("The installed recognizer failed.", failure);
                if (canceled)
                    throw new InvalidOperationException("The installed recognizer canceled unexpectedly.");
                return text.ToString();
            }
        }
        finally
        {
            engine.SpeechRecognized -= OnRecognized;
            engine.RecognizeCompleted -= OnCompleted;
            text.Clear();
        }
    }

    public void Dispose() => engine.Dispose();
}
