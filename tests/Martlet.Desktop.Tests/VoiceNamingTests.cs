using Martlet.Core.Speakers;
using Martlet.Desktop;
using Xunit;

namespace Martlet.Desktop.Tests;

public sealed class VoiceNamingTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static float[] Print(int seed)
    {
        var random = new Random(seed);
        return VoicePrints.Normalize(Enumerable.Range(0, VoicePrints.Dimension).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray());
    }

    private static (HeardVoices Heard, KnownVoice Named, KnownVoice Unnamed) Heard()
    {
        var roster = VoiceRoster.Empty;
        (roster, var first) = roster.Add(Print(1), 3, "desk", Start);
        (roster, var second) = roster.Add(Print(2), 3, "desk", Start);
        roster = roster.AddHeardName(first!.Id, "Sam", "desk", Start);
        var named = roster.Resolve(first.Id)!;
        var unnamed = roster.Resolve(second!.Id)!;
        return (new([new(unnamed, VoiceMatchKind.New, 0, 3, true), new(named, VoiceMatchKind.Known, 0.9, 2, false)], false), named, unnamed);
    }

    [Fact]
    public void TheModelsAnswerOnlyNamesListedVoicesWithUsableNames()
    {
        var (heard, named, unnamed) = Heard();
        var prompt = VoiceNaming.Prompt(heard, null, null, "Hi, I'm Alex. This is my friend Sam.", "Nice to meet you, Alex!");
        Assert.Contains(unnamed.Tag + ": no name yet (the one speaking to Martlet)", prompt.Input.UserText);
        Assert.Contains(named.Tag + ": goes by Sam", prompt.Input.UserText);
        var answer = $"NAME {unnamed.Tag}: Alex\n- **NAME {named.Tag}: Sammy**\nNAME V99: Bob\nNAME {unnamed.Tag}: Martlet\nNAME {unnamed.Tag}: Voice 7\n" +
            $"NAME {unnamed.Tag}: the person who likes games a lot";
        var names = VoiceNaming.Parse(answer, prompt.Voices, ["Ava"]);
        Assert.Equal(new[] { (unnamed.Id, "Alex"), (named.Id, "Sammy") }, names.Select(n => (n.VoiceId, n.Name)).ToArray());
        Assert.Empty(VoiceNaming.Parse(VoiceNaming.Nothing, prompt.Voices, []));
    }

    [Fact]
    public void NamingIsAskedForUnnamedVoicesOrWhenNamesComeUp()
    {
        var (heard, named, _) = Heard();
        Assert.True(VoiceNaming.Worth(heard, "what's the weather", "Sunny."));
        var onlyNamed = new HeardVoices([new(named, VoiceMatchKind.Known, 0.9, 2, false)], false);
        Assert.False(VoiceNaming.Worth(onlyNamed, "what's the weather", "Sunny."));
        Assert.False(VoiceNaming.Worth(onlyNamed, "Hi Martlet, what's up?", "Not much."));
        Assert.True(VoiceNaming.Worth(onlyNamed, "you can call me Sammy", "Sure."));
        Assert.True(VoiceNaming.Worth(onlyNamed, "thanks, Robin", "Anytime."));
    }

    [Fact]
    public void TheReplyIsToldWhoIsSpeakingAndHistoryCarriesTheirName()
    {
        var (heard, named, unnamed) = Heard();
        var text = VoicePromptContext.Instructions(heard)!;
        Assert.Contains($"Speaking now: someone whose name Martlet doesn't know yet (voice {unnamed.Tag}; heard for the first time).", text);
        Assert.Contains($"Also heard in this message: Sam (voice {named.Tag}).", text);
        Assert.Equal($"[{unnamed.Tag}] ", VoicePromptContext.Prefix(heard));
        Assert.Equal("[Sam] ", VoicePromptContext.Prefix(new([new(named, VoiceMatchKind.Known, 0.9, 2, false)], false)));
        Assert.Null(VoicePromptContext.Instructions(HeardVoices.None));
        Assert.Equal("", VoicePromptContext.Prefix(null));
    }
}
