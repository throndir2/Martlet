using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using Martlet.Audio;
using Martlet.Audio.Windows;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Diagnostics;

namespace Martlet.Desktop;

// One bounded local recording that stops itself after the speaker pauses. The PCM never leaves memory.
internal sealed class VoiceSampleRecording
{
    private static readonly Guid Session = Guid.NewGuid();
    private static long epoch;
    private double level = -100;
    internal static TimeSpan MaximumDuration => TimeSpan.FromSeconds(10);
    internal double Level { get => Volatile.Read(ref level); private set => Volatile.Write(ref level, value); }
    internal byte[]? Pcm { get; private set; }
    internal string? Error { get; private set; }
    internal SetupOperation Worker { get; private set; } = null!;
    internal void Stop() => Worker.RequestCancellation();
    internal void Finish() => Volatile.Read(ref finish)?.Invoke();
    private Action? finish;

    internal static VoiceSampleRecording? Start(SetupOperationRunner operations, ICaptureDeviceFactory devices, AudioChoice? input, TimeProvider clock)
    {
        var recording = new VoiceSampleRecording();
        var worker = operations.TryStart(async token =>
        {
            try
            {
                await recording.RecordAsync(devices, input, clock, token).ConfigureAwait(false);
                return new SetupWorkResult(recording.Pcm is null ? SetupWorkOutcome.Failed : SetupWorkOutcome.Completed);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                recording.Error = "Recording canceled.";
                return new SetupWorkResult(SetupWorkOutcome.Canceled);
            }
            catch (CaptureDeviceException error)
            {
                recording.Error = AudioSetupDiagnostics.Remedy(error.Code);
                return new SetupWorkResult(SetupWorkOutcome.Failed);
            }
            catch (ContractException error)
            {
                recording.Error = AudioSetupDiagnostics.Remedy(error.Code);
                return new SetupWorkResult(SetupWorkOutcome.Failed);
            }
            catch (InvalidOperationException)
            {
                recording.Error = "The microphone is muted or busy. Try again in a moment.";
                return new SetupWorkResult(SetupWorkOutcome.Failed);
            }
        });
        if (worker is null) return null;
        recording.Worker = worker;
        return recording;
    }

    private async Task RecordAsync(ICaptureDeviceFactory devices, AudioChoice? input, TimeProvider clock, CancellationToken token)
    {
        await using var microphone = new MicrophoneCapture(Session, devices, new() { MaximumDuration = MaximumDuration, MaximumPcmBytes = 320_000 }, clock);
        var ids = new CorrelationIds { SessionId = Session, TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };
        var selection = input?.EndpointId is { } endpoint ? new InputSelection(InputPolicy.FixedEndpoint, endpoint)
            : new InputSelection(InputPolicy.FollowDefaultOnNextPress);
        var request = new CaptureRequest(ids, Interlocked.Increment(ref epoch), selection, MaximumDuration, clock.GetUtcNow().AddSeconds(12));
        var run = microphone.Press(request, new(request, true), token);
        Volatile.Write(ref finish, () => run.ReleaseAsync().Forget());
        var detector = new EnergyVoiceActivityDetector(new() { EndSilence = TimeSpan.FromSeconds(1) });
        var frame = new byte[EnergyVoiceActivityDetector.FrameBytes];
        try
        {
            var index = 0;
            while (!run.Completion.IsCompleted)
            {
                while (true)
                {
                    bool copied;
                    try { copied = run.TryCopyMonoFrame(index, frame); }
                    catch (OperationCanceledException) { copied = false; }
                    if (!copied) break;
                    index++;
                    var transition = detector.Process(frame);
                    Level = detector.LastLevelDb;
                    if (transition == VoiceActivityTransition.SpeechEnded &&
                        detector.SpeechEndFrame - detector.SpeechStartFrame >= 50)
                        await run.ReleaseAsync().ConfigureAwait(false);
                }
                await Task.WhenAny(run.Completion, Task.Delay(TimeSpan.FromMilliseconds(20), clock)).ConfigureAwait(false);
            }
            var terminal = await run.Completion.ConfigureAwait(false);
            using var utterance = run.TakeUtterance();
            if (terminal.State != CaptureState.Completed || utterance is null)
            {
                Error = terminal.Error is { } failure ? AudioSetupDiagnostics.Remedy(failure.Code) : "No microphone audio was received.";
                return;
            }
            var pcm = new byte[utterance.ByteCount];
            utterance.CopyPcmTo(pcm);
            Pcm = pcm;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(frame);
            Level = -100;
            await run.CancelAsync().ConfigureAwait(false);
            await run.DeviceRelease.ConfigureAwait(false);
        }
    }

    internal void Clear()
    {
        if (Pcm is not null) CryptographicOperations.ZeroMemory(Pcm);
        Pcm = null;
    }
}

public partial class VoiceIdWindow : ThemedWindow
{
    private static readonly string[] Phrases =
    [
        "The quick brown fox jumps over the lazy dog, and the sun sets slowly behind the quiet hills.",
        "I like to talk with Martlet about my day, my games, my music and my plans for tomorrow.",
        "Please remember what my voice sounds like when I speak at my normal volume and pace."
    ];
    private readonly VoiceIdentity identity;
    private readonly SetupOperationRunner operations;
    private readonly IAudioSessionEvents sessionEvents;
    private readonly AudioChoice? input;
    private readonly ICaptureDeviceFactory devices;
    private readonly TimeProvider clock;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly List<byte[]> samples = [];
    private VoiceSampleRecording? recording;
    private bool testing, initializing, closed;

    internal VoiceIdWindow(VoiceIdentity identity, SetupOperationRunner operations, IAudioSessionEvents sessionEvents,
        AudioChoice? input, ICaptureDeviceFactory? devices = null, TimeProvider? clock = null)
    {
        this.identity = identity;
        this.operations = operations;
        this.sessionEvents = sessionEvents;
        this.input = input;
        this.devices = devices ?? new WasapiCaptureDeviceFactory();
        this.clock = clock ?? TimeProvider.System;
        InitializeComponent();
        initializing = true;
        ThresholdSlider.Value = identity.Current?.Threshold ?? SpeakerVerifier.DefaultThreshold;
        initializing = false;
        timer.Tick += (_, _) => Poll();
        sessionEvents.LockedChanged += SessionLocked;
        Render();
    }

    private void SessionLocked(bool locked)
    {
        if (locked) Dispatcher.BeginInvoke(() => { recording?.Stop(); AcceptRecording.IsChecked = false; });
    }

    private void Record_Click(object sender, RoutedEventArgs e) => Begin(test: false);
    private void Test_Click(object sender, RoutedEventArgs e) => Begin(test: true);
    private void Stop_Click(object sender, RoutedEventArgs e) => recording?.Finish();

    private void Begin(bool test)
    {
        if (recording is not null || AcceptRecording.IsChecked != true) return;
        recording = VoiceSampleRecording.Start(operations, devices, input, clock);
        if (recording is null)
        {
            StatusText.Text = "Another Martlet action is using the microphone. Stop it and try again.";
            return;
        }
        testing = test;
        StatusText.Text = test ? "Recording... say anything in your normal voice." : "Recording... read the phrase above, then pause.";
        if (test) TestResultText.Text = "";
        timer.Start();
        Render();
    }

    private void Poll()
    {
        if (recording is not { } active) { timer.Stop(); return; }
        LevelMeter.Value = Math.Clamp((active.Level + 60) / 50, 0, 1);
        if (!active.Worker.Completion.IsCompleted) return;
        timer.Stop();
        recording = null;
        LevelMeter.Value = 0;
        if (active.Pcm is not { } pcm)
        {
            StatusText.Text = active.Error ?? "Recording failed. Try again.";
            Render();
            return;
        }
        try
        {
            if (testing) ShowTest(pcm);
            else AddSample(pcm);
        }
        catch (VoiceIdentityException error) { StatusText.Text = error.Message; }
        catch (IOException) { StatusText.Text = "Couldn't save your voiceprint. Check folder access and try again."; }
        catch (UnauthorizedAccessException) { StatusText.Text = "Couldn't save your voiceprint. Check folder access and try again."; }
        finally { if (testing) active.Clear(); }
        Render();
    }

    private void AddSample(byte[] pcm)
    {
        var speech = SpeakerVerifier.ExtractSpeech(pcm);
        var seconds = speech.Length / (double)SpeakerEncoder.SampleRate;
        Array.Clear(speech);
        if (seconds < 1.5)
        {
            CryptographicOperations.ZeroMemory(pcm);
            StatusText.Text = $"Only {seconds:F1} s of speech was heard. Move closer or speak up, then try again.";
            return;
        }
        samples.Add(pcm);
        if (samples.Count < Phrases.Length)
        {
            StatusText.Text = $"Phrase {samples.Count} recorded. Next phrase.";
            return;
        }
        var enrollment = SpeakerVerifier.Enroll(VoiceIdentity.Encoder, samples);
        ClearSamples();
        identity.Save(new(enrollment.Voiceprint, enrollment.SuggestedThreshold, enrollment.Consistency,
            DateTimeOffset.Now, enrollment.Speech.TotalSeconds));
        initializing = true;
        ThresholdSlider.Value = enrollment.SuggestedThreshold;
        initializing = false;
        StatusText.Text = enrollment.Consistency < 0.75f
            ? "Voiceprint saved, but the phrases sounded different. If tests miss you, start over somewhere quieter."
            : "Voiceprint saved. Try the test below.";
    }

    private void ShowTest(byte[] pcm)
    {
        var print = identity.Current ?? throw new VoiceIdentityException("Enroll your voice first.");
        var check = SpeakerVerifier.Check(VoiceIdentity.Encoder, print.Embedding, print.Threshold, pcm);
        TestResultText.Text = check.Verdict switch
        {
            SpeakerVerdict.User when check.OtherVoiceDetected =>
                $"Recognized you, but another voice may be present. Score {check.Similarity:F2}.",
            SpeakerVerdict.User => $"Recognized you. Score {check.Similarity:F2} (threshold {print.Threshold:F2}).",
            SpeakerVerdict.OtherSpeaker => $"Not recognized. Score {check.Similarity:F2} is below threshold {print.Threshold:F2}.",
            _ => $"Too little speech to decide ({check.SpeechAnalyzed.TotalSeconds:F1} s). Speak for at least a second."
        };
        StatusText.Text = "Test recording discarded.";
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        ClearSamples();
        StatusText.Text = "Enrollment restarted. Your saved voiceprint won't change until you finish all three phrases.";
        Render();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            identity.Delete();
            StatusText.Text = "Voiceprint deleted from this PC.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Couldn't delete your voiceprint. Check folder access.";
        }
        Render();
    }

    private void Threshold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThresholdValue is null) return;
        ThresholdValue.Text = ThresholdSlider.Value.ToString("F2");
        if (initializing || identity.Current is null) return;
        try { identity.SetThreshold((float)ThresholdSlider.Value); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Couldn't save the threshold. Check folder access.";
        }
        Render();
    }

    private void Consent_Changed(object sender, RoutedEventArgs e)
    {
        if (AcceptRecording.IsChecked != true) recording?.Stop();
        Render();
    }

    private void Render()
    {
        if (RecordButton is null) return;
        var busy = recording is not null;
        var consent = AcceptRecording.IsChecked == true;
        var next = Math.Min(samples.Count, Phrases.Length - 1);
        PhraseText.Text = $"\u201C{Phrases[next]}\u201D";
        RecordButton.Content = $"_Record phrase {next + 1} of {Phrases.Length}";
        RecordButton.IsEnabled = consent && !busy;
        StopButton.IsEnabled = busy;
        RestartButton.IsEnabled = !busy && samples.Count > 0;
        TestButton.IsEnabled = consent && !busy && identity.Current is not null;
        DeleteButton.IsEnabled = !busy && identity.Current is not null;
        ThresholdSlider.IsEnabled = identity.Current is not null;
        VoiceprintText.Text = identity.LoadError ?? (identity.Current is { } print
            ? $"Voiceprint saved {print.CreatedAt.LocalDateTime:g}. Speech used: {print.SpeechSeconds:F0} s. Consistency: {print.Consistency:F2}."
            : "No voiceprint yet. Finish step 1 to create one.");
    }

    private void ClearSamples()
    {
        foreach (var sample in samples) CryptographicOperations.ZeroMemory(sample);
        samples.Clear();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closed) return;
        closed = true;
        if (recording is { } active)
        {
            active.Stop();
            _ = active.Worker.Completion.ContinueWith(_ => active.Clear(), TaskScheduler.Default);
        }
        timer.Stop();
        ClearSamples();
        sessionEvents.LockedChanged -= SessionLocked;
    }
}
