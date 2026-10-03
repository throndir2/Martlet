using Martlet.Audio;
using SoundFlow.Extensions.WebRtc.Apm;

namespace Martlet.EchoCancellation;

/// <summary>WebRTC's acoustic echo canceller (AEC3) on 10 ms frames of 16 kHz mono: the speaker frame is the far end it
/// learns the room's echo path from, and that echo is subtracted from the microphone frame. A high-pass filter removes rumble;
/// noise suppression and gain control stay off so speech-to-text hears the voice as it was. The same canceller AudioTranscriber
/// uses on its microphone track.</summary>
public sealed class WebRtcEchoCanceller : IEchoCanceller
{
    private const int Rate = EchoReduction.SampleRate;
    private const int Frame = EchoReduction.FrameSamples;
    private readonly AudioProcessingModule apm;
    private readonly StreamConfig stream;
    private readonly float[][] render = [new float[Frame]], renderOut = [new float[Frame]];
    private readonly float[][] capture = [new float[Frame]], captureOut = [new float[Frame]];
    private bool disposed;

    /// <summary>A new canceller; throws if the native library can't load or the module refuses its configuration.</summary>
    public static IEchoCanceller Create() => new WebRtcEchoCanceller();

    private WebRtcEchoCanceller()
    {
        apm = new AudioProcessingModule();
        StreamConfig? configured = null;
        try
        {
            using var config = new ApmConfig();
            configured = new StreamConfig(Rate, 1);
            config.SetEchoCanceller(true, false);
            config.SetHighPassFilter(true);
            config.SetNoiseSuppression(false, NoiseSuppressionLevel.Low);
            config.SetGainController1(false, GainControlMode.AdaptiveDigital, 3, 9, false);
            config.SetGainController2(false);
            config.SetPreAmplifier(false, 1f);
            Check(apm.ApplyConfig(config));
            Check(apm.Initialize());
            stream = configured;
        }
        catch
        {
            configured?.Dispose();
            apm.Dispose();
            throw;
        }
    }

    public void Process(ReadOnlySpan<float> speaker, Span<float> microphone)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (speaker.Length != Frame || microphone.Length != Frame)
            throw new ArgumentException("Echo cancellation takes 10 ms frames of 16 kHz mono audio.");
        try
        {
            speaker.CopyTo(render[0]);
            microphone.CopyTo(capture[0]);
            Check(apm.ProcessReverseStream(render, stream, stream, renderOut));
            apm.SetStreamDelayMs(0);
            Check(apm.ProcessStream(capture, stream, stream, captureOut));
            captureOut[0].CopyTo(microphone);
        }
        finally
        {
            Array.Clear(render[0]);
            Array.Clear(renderOut[0]);
            Array.Clear(capture[0]);
            Array.Clear(captureOut[0]);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stream.Dispose();
        apm.Dispose();
    }

    private static void Check(ApmError error)
    {
        if (error != ApmError.NoError) throw new InvalidOperationException($"The WebRTC echo canceller reported {error}.");
    }
}
