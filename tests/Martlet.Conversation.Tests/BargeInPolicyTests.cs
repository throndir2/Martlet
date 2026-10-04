using Martlet.Providers;

namespace Martlet.Conversation.Tests;

public sealed class BargeInPolicyTests
{
    private static UtteranceContext Heard(int voicedMs, double? mean = null, double? least = null) => new()
    {
        Voiced = TimeSpan.FromMilliseconds(voicedMs),
        Evidence = mean is null ? null : new TranscriptionEvidence { Engine = "parakeet", MeanProbability = mean, MinimumProbability = least }
    };

    [Theory]
    // Parakeet TDT 0.6B v2 and 110M on a Windows voice laughing (utterance_filter_check's audio).
    [InlineData("Come on.", 0.50, 0.18)]
    [InlineData("Cosmos was", 0.55, 0.34)]
    public void A_few_words_the_engine_was_unsure_of_do_not_stop_a_reply(string text, double mean, double least)
    {
        var decision = BargeInPolicy.Decide(text, Heard(400, mean, least), ListeningSensitivity.Normal);
        Assert.False(decision.Interrupt);
        Assert.True(decision.Words.Keep);
        Assert.Equal("speech-to-text wasn't sure of the words", decision.Reason);
    }

    [Theory]
    [InlineData("Can you", 0.80, 0.43, ListeningSensitivity.Normal)]
    [InlineData("Can you", 0.89, 0.67, ListeningSensitivity.Normal)]
    // Only the mean is low, or only one token: real words, partly heard.
    [InlineData("Can you", 0.60, 0.40, ListeningSensitivity.Normal)]
    [InlineData("Can you", 0.70, 0.20, ListeningSensitivity.Normal)]
    [InlineData("Come on.", 0.50, 0.18, ListeningSensitivity.Sensitive)]
    public void Words_the_engine_was_sure_enough_of_still_stop_a_reply(string text, double mean, double least, ListeningSensitivity sensitivity)
    {
        var decision = BargeInPolicy.Decide(text, Heard(400, mean, least), sensitivity);
        Assert.True(decision.Interrupt, decision.Reason);
    }

    [Fact]
    public void A_word_said_over_and_over_counts_once()
    {
        // Parakeet TDT 0.6B v2 on a Windows voice laughing "Ha ha ha ha!".
        var laugh = BargeInPolicy.Decide("One, one, one.", Heard(900, 0.75, 0.46), ListeningSensitivity.Normal);
        Assert.False(laugh.Interrupt);
        Assert.Equal("repeated words", laugh.Reason);
        Assert.True(BargeInPolicy.Decide("No, no, no.", Heard(900, 0.75, 0.46), ListeningSensitivity.Normal).Interrupt);
        Assert.True(BargeInPolicy.Decide("Go, go, go.", Heard(900, 0.9, 0.8), ListeningSensitivity.Sensitive).Interrupt);
        Assert.True(BargeInPolicy.Decide("Go back, go back", Heard(900, 0.9, 0.8), ListeningSensitivity.Normal).Interrupt);
    }

    [Fact]
    public void Stop_words_names_and_longer_speech_stop_a_reply_however_unsure()
    {
        Assert.Equal("a stop word", BargeInPolicy.Decide("Stop.", Heard(300, 0.52, 0.24), ListeningSensitivity.Normal).Reason);
        Assert.Equal("Martlet's name", BargeInPolicy.Decide("Martlet, look at this", Heard(600, 0.55, 0.2), ListeningSensitivity.Normal).Reason);
        Assert.True(BargeInPolicy.Decide("I think the second one is better", Heard(1400, 0.6, 0.2), ListeningSensitivity.Normal).Interrupt);
        // Without the engine's evidence nothing changes.
        Assert.True(BargeInPolicy.Decide("Come on.", Heard(400), ListeningSensitivity.Normal).Interrupt);
    }
}
