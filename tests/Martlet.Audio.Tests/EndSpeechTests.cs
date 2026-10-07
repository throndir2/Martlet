using System.Buffers.Binary;
using Martlet.Audio;

namespace Martlet.Audio.Tests;

public sealed class EndSpeechTests
{
    private static byte[] Frame(double amplitude)
    {
        var frame = new byte[EnergyVoiceActivityDetector.FrameBytes];
        for (var i = 0; i < EnergyVoiceActivityDetector.FrameSamples; i++)
            BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(i * 2), (short)(amplitude * 32767 * Math.Sin(2 * Math.PI * 220 * i / 16000.0)));
        return frame;
    }

    [Fact]
    public void Ending_the_speech_early_ends_it_where_its_silence_began()
    {
        var detector = new EnergyVoiceActivityDetector(new VoiceActivitySettings { EndSilence = TimeSpan.FromMilliseconds(1600) });
        for (var i = 0; i < 10; i++) detector.Process(Frame(0.0005));
        for (var i = 0; i < 30; i++) detector.Process(Frame(0.3));
        Assert.True(detector.Speaking);
        Assert.Equal(0, detector.SilenceFrames);
        for (var i = 0; i < 13; i++) Assert.Equal(VoiceActivityTransition.None, detector.Process(Frame(0)));
        Assert.Equal(13, detector.SilenceFrames);

        Assert.True(detector.EndSpeech());

        Assert.False(detector.Speaking);
        Assert.Equal(40, detector.SpeechEndFrame);
        Assert.Equal(0, detector.SilenceFrames);
        Assert.False(detector.EndSpeech());
    }

    [Fact]
    public void Without_ending_it_the_detector_waits_for_its_own_pause()
    {
        var detector = new EnergyVoiceActivityDetector(new VoiceActivitySettings { EndSilence = TimeSpan.FromMilliseconds(800) });
        for (var i = 0; i < 10; i++) detector.Process(Frame(0.0005));
        for (var i = 0; i < 30; i++) detector.Process(Frame(0.3));
        var ended = -1;
        for (var i = 0; i < 60 && ended < 0; i++)
            if (detector.Process(Frame(0)) == VoiceActivityTransition.SpeechEnded) ended = i + 1;
        Assert.Equal(40, ended);
        Assert.Equal(40, detector.SpeechEndFrame);
    }
}
