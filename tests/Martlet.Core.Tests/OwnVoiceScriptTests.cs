using System.Text.RegularExpressions;
using Martlet.Core.Settings;
using Martlet.Core.Voices;
using Xunit;

namespace Martlet.Core.Tests;

public sealed class OwnVoiceScriptTests
{
    private static int Words(string line) => Regex.Matches(line, @"[A-Za-z']+").Count;

    [Fact]
    public void EachLineIsOneRecordingThatEveryEngineTakes()
    {
        var lines = OwnVoiceScript.Lines;
        Assert.InRange(lines.Count, 2, SpeakingVoiceLibrary.MaximumClips);
        foreach (var line in lines)
        {
            Assert.True(SpeakingVoiceLibrary.IsTranscript(line), line);
            // 12 to 16 words last about 4 to 7 seconds read out loud: inside GPT-SoVITS's 3 to 10 seconds.
            Assert.InRange(Words(line), 12, 16);
            Assert.Equal("en", SpeechEngines.ReferenceLanguage(line));
        }
        Assert.True(SpeakingVoiceLibrary.IsTranscript(string.Join(" ", lines)));
    }

    [Fact]
    public void TogetherTheLinesFitOneVoiceEvenReadSlowly()
    {
        // At a slow 2.2 words a second, with the trimmed silence (0.5 s a line) and the pauses between them.
        var seconds = OwnVoiceScript.Lines.Sum(Words) / 2.2 + OwnVoiceScript.Lines.Count * 0.5 +
            (OwnVoiceScript.Lines.Count - 1) * SpeakingVoiceLibrary.ClipPauseMilliseconds / 1000d;
        Assert.True(seconds <= SpeakingVoiceLibrary.MaximumDurationMilliseconds / 1000d, $"{seconds:0.0} seconds");
    }

    [Theory]
    // Spellings of the sounds the lines must hold: the two "th" sounds, "sh", "ch", "j", "ng", the "zh" of measure and
    // usually, "z", "v", "w", "y", "h", and the glides of voice, how and my.
    [InlineData("thick")]
    [InlineData("the ")]
    [InlineData("ship")]
    [InlineData("chilly")]
    [InlineData("June")]
    [InlineData("ing")]
    [InlineData("measure")]
    [InlineData("usually")]
    [InlineData("Zoe")]
    [InlineData("voice")]
    [InlineData("Would")]
    [InlineData("you")]
    [InlineData("Hello")]
    [InlineData("toy")]
    [InlineData("how")]
    [InlineData("my")]
    public void TheLinesHoldTheSoundsOfSpokenEnglish(string spelling) =>
        Assert.Contains(OwnVoiceScript.Lines, line => line.Contains(spelling, StringComparison.Ordinal));

    [Fact]
    public void TheLinesAreSaidInDifferentWays()
    {
        Assert.Contains(OwnVoiceScript.Lines, line => line.EndsWith('?'));
        Assert.Contains(OwnVoiceScript.Lines, line => line.Contains('!'));
        Assert.Contains(OwnVoiceScript.Lines, line => line.EndsWith('.') && !line.Contains('!'));
    }
}
