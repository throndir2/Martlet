using Martlet.Core.Speakers;
using Martlet.Desktop;
using Martlet.Memory;
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

    private static VoiceNamingContext Naming(HeardVoices heard, CompanionNames? companion = null, IReadOnlyList<KnownVoice>? others = null) =>
        new(heard, others ?? [], companion ?? CompanionNames.Martlet);

    [Fact]
    public void TheModelsAnswerOnlyNamesListedVoicesWithUsableNames()
    {
        var (heard, named, unnamed) = Heard();
        var naming = Naming(heard, CompanionNames.From(["Ava"]));
        var prompt = VoiceNaming.Prompt(naming, null, null, "Hi, I'm Alex. This is my friend Sam.", "Nice to meet you, Alex!");
        Assert.Contains("Martlet's own names (the companion's, never one of the people's): Martlet, Ava", prompt.Input.UserText);
        Assert.Contains(unnamed.Tag + ": no name yet (the one speaking to Martlet)", prompt.Input.UserText);
        Assert.Contains(named.Tag + ": goes by Sam", prompt.Input.UserText);
        Assert.DoesNotContain("Other voices", prompt.Input.UserText);
        var answer = $"NAME {unnamed.Tag}: Alex\n- **NAME {named.Tag}: Sammy**\nNAME V99: Bob\nNAME {unnamed.Tag}: Martlet\nNAME {unnamed.Tag}: Voice 7\n" +
            $"NAME {unnamed.Tag}: the person who likes games a lot\nNAME {unnamed.Tag}: Ava";
        var names = VoiceNaming.Parse(answer, naming, "Nice to meet you, Alex!");
        Assert.Equal(new[] { (unnamed.Id, "Alex"), (named.Id, "Sammy") }, names.Updates.Select(n => (n.VoiceId, n.Name!)).ToArray());
        Assert.Contains(names.Refused, r => r.Reason == "the companion's own name");
        Assert.Empty(VoiceNaming.Parse(VoiceNaming.Nothing, naming, null).Updates);
    }

    [Fact]
    public void AVoiceNeverLearnsTheCompanionsOwnName()
    {
        var (heard, _, unnamed) = Heard();
        var naming = Naming(heard, CompanionNames.From(["Jane Doe"], ["You are Jane, a cheerful friend."]));
        // Greeting the companion by its name is not a name coming up; greeting someone else is.
        var onlyNamed = new HeardVoices([heard.Voices[1]], false);
        Assert.False(VoiceNaming.Worth(onlyNamed, "Hey Jane, how are you?", "Great, thanks!", naming.Companion));
        Assert.True(VoiceNaming.Worth(onlyNamed, "Hey Robin, how are you?", "Hi!", naming.Companion));
        var answer = $"NAME {unnamed.Tag}: Jane\nCALL {unnamed.Tag}: Doe\nNAME {unnamed.Tag}: Kit\nNAME {unnamed.Tag}: Alex";
        var parsed = VoiceNaming.Parse(answer, naming, "Hi there! I'm Kit, by the way.");
        Assert.Equal(new[] { "Alex" }, parsed.Updates.Select(u => u.Name).ToArray());
        Assert.Equal(3, parsed.Refused.Count(r => r.Reason == "the companion's own name"));
    }

    [Fact]
    public void OtherVoicesAreListedOnlyWhenSomeoneSaysTheyAreTheSamePerson()
    {
        var roster = VoiceRoster.Empty;
        (roster, var owner) = roster.Add(Print(1), 3, "desk", Start);
        (roster, var fresh) = roster.Add(Print(2), 3, "desk", Start.AddMinutes(1));
        (roster, var quiet) = roster.Add(Print(3), 3, "desk", Start);
        roster = roster.SetNames(owner!.Id, "Robert", ["Bob", "Bobby", "Rob"], "desk", Start).SetOwner(owner.Id, true, "desk", Start)
            .AddHeardName(quiet!.Id, "Kim", "desk", Start);
        var heard = new HeardVoices([new(roster.Resolve(fresh!.Id), VoiceMatchKind.New, 0.2, 3, true)], false);

        Assert.Empty(VoiceNaming.Context(heard, roster, "what time is it", CompanionNames.Martlet).Others);
        var naming = VoiceNaming.Context(heard, roster, "it's me, Bob, I've got a cold", CompanionNames.Martlet);
        Assert.Contains(owner.Id, naming.Others.Select(v => v.Id));
        Assert.True(VoiceNaming.Worth(new HeardVoices([], false), "that was me too", "Got it.", CompanionNames.Martlet));
        var prompt = VoiceNaming.Prompt(naming, null, null, "it's me, Bob, I've got a cold", "Feel better, Bob!");
        Assert.Contains($"{owner.Tag}: goes by Robert, Bob, Bobby, Rob", prompt.Input.UserText);
        Assert.Contains("only for SAME lines", prompt.Input.UserText);

        var parsed = VoiceNaming.Parse($"SAME {fresh.Tag}: {owner.Tag}\nNAME {owner.Tag}: Bobby\nSAME {fresh.Tag}: {quiet.Tag}\nSAME {fresh.Tag}: V99",
            naming, null);
        Assert.Equal(new[] { VoiceUpdateKind.Same }, parsed.Updates.Select(u => u.Kind).ToArray());
        Assert.Equal(owner.Id, parsed.Updates[0].SameAsId);
        Assert.Contains(parsed.Refused, r => r.Reason == "the voice wasn't heard in this message");
        Assert.Contains(parsed.Refused, r => r.Reason == "only one merge per exchange");
        Assert.Contains(parsed.Refused, r => r.Reason == "not a listed voice");

        var (merged, applied, _) = VoiceUpdates.Apply(roster, parsed.Updates, "desk", Start.AddMinutes(2));
        Assert.Equal(owner.Id, merged.Resolve(fresh.Id)!.Id);
        Assert.Equal("Robert", merged.Resolve(owner.Id)!.DisplayName);
        Assert.Contains("same person as Robert (voice 1)", applied.Single().Text);
    }

    [Fact]
    public void NamingIsAskedForUnnamedVoicesOrWhenNamesComeUp()
    {
        var (heard, named, _) = Heard();
        var martlet = CompanionNames.Martlet;
        Assert.True(VoiceNaming.Worth(heard, "what's the weather", "Sunny.", martlet));
        var onlyNamed = new HeardVoices([new(named, VoiceMatchKind.Known, 0.9, 2, false)], false);
        Assert.False(VoiceNaming.Worth(onlyNamed, "what's the weather", "Sunny.", martlet));
        Assert.False(VoiceNaming.Worth(onlyNamed, "Hi Martlet, what's up?", "Not much.", martlet));
        Assert.True(VoiceNaming.Worth(onlyNamed, "you can call me Sammy", "Sure.", martlet));
        Assert.True(VoiceNaming.Worth(onlyNamed, "thanks, Robin", "Anytime.", martlet));
        Assert.True(VoiceNaming.Worth(onlyNamed, "that's the wrong name", "Sorry!", martlet));
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

    [Fact]
    public void AfterReplyAsksOnceWhenMemoryAndVoiceNamesAreBothDue()
    {
        var (heard, _, unnamed) = Heard();
        var provenance = MemoryProvenance.Conversation(Guid.NewGuid(), Start);
        var fact = new MemoryFact
        {
            Id = Guid.NewGuid(),
            Revision = 1,
            Content = "The user likes tea.",
            CreatedAtUtc = Start,
            UpdatedAtUtc = Start,
            CreatedFrom = provenance,
            LastModifiedBy = provenance,
            Retention = MemoryRetention.UntilDeleted()
        };

        var prompt = AfterReply.Prompt([fact], Naming(heard), "Earlier question.", "Earlier answer.",
            "Hi, I'm Alex.", "Nice to meet you, Alex!", null);

        Assert.False(prompt.Continued);
        Assert.Equal(1, prompt.ShownFacts);
        Assert.Equal(2, prompt.Voices.Count);
        Assert.Contains("REMEMBER:", prompt.Input.Personality);
        Assert.Contains("UPDATE <number>:", prompt.Input.Personality);
        Assert.Contains("FORGET <number>", prompt.Input.Personality);
        Assert.Contains("NAME V<number>:", prompt.Input.Personality);
        Assert.Contains("CALL V<number>:", prompt.Input.Personality);
        Assert.Contains("NOT V<number>:", prompt.Input.Personality);
        Assert.Contains("SAME V<number>: V<number>", prompt.Input.Personality);
        Assert.Contains("1. The user likes tea.", prompt.Input.UserText);
        Assert.Contains(unnamed.Tag + ": no name yet (the one speaking to Martlet)", prompt.Input.UserText);
        Assert.Contains($"User ({unnamed.Tag}): Hi, I'm Alex.", prompt.Input.UserText);
    }
}
