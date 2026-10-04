using Martlet.Conversation;

namespace Martlet.Conversation.Tests;

public sealed class BargeInGateTests
{
    [Fact]
    public void Speech_counts_the_whole_stretch_but_not_what_the_speakers_explain()
    {
        var gate = new BargeInGate(Martlet.Providers.ListeningSensitivity.Normal);
        foreach (var (loud, speakers) in new[] { (true, false), (false, false), (true, false), (false, true), (true, true), (true, false) })
            gate.Process(loud, speakers, busy: true);

        Assert.Equal(TimeSpan.FromMilliseconds(60), gate.Voice);
        Assert.Equal(TimeSpan.FromMilliseconds(80), gate.Speech);
    }

    [Fact]
    public void A_long_pause_starts_the_speech_over()
    {
        var gate = new BargeInGate(Martlet.Providers.ListeningSensitivity.Normal);
        gate.Process(loud: true, speakers: false, busy: true);
        for (var i = 0; i < (int)(BargeInGate.Gap.TotalMilliseconds / BargeInGate.FrameMilliseconds) + 1; i++)
            gate.Process(loud: false, speakers: false, busy: true);

        Assert.Equal(-1, gate.StretchStartFrame);
        Assert.Equal(TimeSpan.Zero, gate.Speech);

        gate.Process(loud: true, speakers: false, busy: true);
        Assert.Equal(TimeSpan.FromMilliseconds(20), gate.Speech);
    }
}
