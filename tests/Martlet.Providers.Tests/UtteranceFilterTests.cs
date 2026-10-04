using Martlet.Providers;

namespace Martlet.Providers.Tests;

public sealed class UtteranceFilterTests
{
    private static readonly TranscriptionEvidence Sure = new() { Engine = "parakeet", MeanProbability = 0.93, MinimumProbability = 0.75 };

    [Fact]
    public void Fast_words_with_little_loud_voice_are_kept_when_the_speech_went_on_long_enough()
    {
        var context = new UtteranceContext
        {
            Voiced = TimeSpan.FromMilliseconds(460), Speech = TimeSpan.FromMilliseconds(940), Evidence = Sure
        };

        var decision = UtteranceFilter.Check("I'm gonna make it public.", context, ListeningSensitivity.Normal);

        Assert.True(decision.Keep);
        Assert.Equal(UtteranceKind.Words, decision.Kind);
        Assert.Equal(5, decision.Words);
    }

    [Fact]
    public void More_words_than_the_speech_could_hold_are_still_dropped()
    {
        var context = new UtteranceContext { Voiced = TimeSpan.FromMilliseconds(250), Speech = TimeSpan.FromMilliseconds(300) };

        var decision = UtteranceFilter.Check("I think the second one is better.", context, ListeningSensitivity.Normal);

        Assert.False(decision.Keep);
        Assert.Equal(UtteranceKind.Unlikely, decision.Kind);
        Assert.Equal("7 words from 300 ms of speech", decision.Reason);
    }

    [Fact]
    public void Without_a_speech_length_the_voice_bounds_the_words()
    {
        var context = new UtteranceContext { Voiced = TimeSpan.FromMilliseconds(460), Evidence = Sure };

        var decision = UtteranceFilter.Check("I'm gonna make it public.", context, ListeningSensitivity.Normal);

        Assert.False(decision.Keep);
        Assert.Equal(UtteranceKind.Unlikely, decision.Kind);
        Assert.Equal("5 words from 460 ms of voice", decision.Reason);
    }

    [Fact]
    public void A_speech_length_shorter_than_the_voice_never_tightens_the_bound()
    {
        var context = new UtteranceContext
        {
            Voiced = TimeSpan.FromMilliseconds(1400), Speech = TimeSpan.FromMilliseconds(200), Evidence = Sure
        };

        Assert.True(UtteranceFilter.Check("I think the second one is better.", context, ListeningSensitivity.Normal).Keep);
    }
}
