using Martlet.Core.Speakers;
using Xunit;

namespace Martlet.Core.Tests;

public sealed class VoiceUpdatesTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static float[] Print(int seed)
    {
        var random = new Random(seed);
        return VoicePrints.Normalize(Enumerable.Range(0, VoicePrints.Dimension).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray());
    }

    private static (VoiceRoster Roster, KnownVoice A, KnownVoice B) Two()
    {
        var roster = VoiceRoster.Empty;
        (roster, var a) = roster.Add(Print(1), 3, "desk", Start);
        (roster, var b) = roster.Add(Print(2), 3, "desk", Start);
        return (roster, a!, b!);
    }

    private static Dictionary<string, string> Tags(params KnownVoice[] voices) =>
        voices.ToDictionary(v => v.Tag, v => v.Id, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void TheCompanionsNamesComeFromPersonasTheirInstructionsAndItsReply()
    {
        var companion = CompanionNames.From(["Jane Doe (sarcastic)", "Martlet", null], ["You are Ivy, a cheerful friend. You're talking with Sam.",
            "Your name is Kit."]);
        Assert.Equal(new[] { "Martlet", "Jane Doe sarcastic", "Ivy", "Kit" }, companion.Names);
        foreach (var name in new[] { "Jane", "doe", "Jane Doe", "Ivy", "Kit", "Martlet", "Jane Doe Sarcastic" }) Assert.True(companion.Matches(name), name);
        foreach (var name in new[] { "Sam", "Alex", "Jane Smith", "Mary-Jane", "", "J" }) Assert.False(companion.Matches(name), name);
        var replied = companion.WithReply("Nice to meet you! I'm Nova, and you can call me Nov.");
        Assert.True(replied.Matches("Nova"));
        Assert.True(replied.Matches("Nov"));
        Assert.False(companion.Matches("Nova"));
        Assert.True(CompanionNames.Martlet.Matches("martlet"));
        Assert.False(CompanionNames.Martlet.Matches("Jane"));
    }

    [Fact]
    public void PlaceholdersAreNeverLearnedAsNames()
    {
        var (_, a, _) = Two();
        var answer = $"NAME {a.Tag}: no name yet\nNAME {a.Tag}: Unknown\nCALL {a.Tag}: Name unknown\nNAME {a.Tag}: Anonymous\nNAME {a.Tag}: Nate";
        var parsed = VoiceUpdates.Parse(answer, Tags(a), [a.Id], CompanionNames.Martlet);
        Assert.Equal(new[] { "Nate" }, parsed.Updates.Select(u => u.Name).ToArray());
        Assert.All(parsed.Refused, r => Assert.Equal("not a name", r.Reason));
        Assert.True(VoiceUpdates.IsNotName("No name yet"));
        Assert.False(VoiceUpdates.IsNotName("Mary Jane"));
    }

    [Fact]
    public void LinesAreCheckedAgainstHeardVoicesAndTheCompanionsNames()
    {
        var (_, a, b) = Two();
        var companion = CompanionNames.From(["Jane"]);
        var answer = $"NAME {a.Tag}: Sam\nCALL {a.Tag}: Sammy\nNOT {a.Tag}: Jane\nNAME {a.Tag}: Jane\nNAME {b.Tag}: Alex\n" +
            $"name {a.Tag} - Samuel\nSAME {a.Tag}: {a.Tag}\nNOTHING\nNAME V77: Bo\nNAME {a.Tag}: Voice 2\nNAME {a.Tag}: Sam";
        var parsed = VoiceUpdates.Parse(answer, Tags(a, b), [a.Id], companion);
        Assert.Equal(new[] { (VoiceUpdateKind.Name, "Sam"), (VoiceUpdateKind.Call, "Sammy"), (VoiceUpdateKind.Not, "Jane"), (VoiceUpdateKind.Name, "Samuel") },
            parsed.Updates.Select(u => (u.Kind, u.Name!)).ToArray());
        Assert.Equal(new[] { 1, 2, 3, 6 }, parsed.Updates.Select(u => u.Line).ToArray());
        Assert.Equal(new[] { "the companion's own name", "the voice wasn't heard in this message", "the same voice twice", "not a listed voice", "not a name" },
            parsed.Refused.Select(r => r.Reason).ToArray());
        Assert.Empty(VoiceUpdates.Parse(null, Tags(a), [a.Id], companion).Updates);

        var many = string.Join('\n', Enumerable.Range(1, 8).Select(i => $"NAME {a.Tag}: Name{(char)('a' + i)}"));
        var bounded = VoiceUpdates.Parse(many, Tags(a), [a.Id], companion);
        Assert.Equal(VoiceUpdates.MaximumUpdates, bounded.Updates.Count);
        Assert.Equal(2, bounded.Refused.Count);
    }

    [Fact]
    public void AVoiceGoesByManyNamesAndShowsTheOneItAskedFor()
    {
        var (roster, a, _) = Two();
        var updates = new[] { "Alexander", "Alex", "Lex", "Xander" }.Select(n => new VoiceUpdate(VoiceUpdateKind.Name, a.Id, n))
            .Append(new VoiceUpdate(VoiceUpdateKind.Call, a.Id, "Al")).ToArray();
        var (next, applied, refused) = VoiceUpdates.Apply(roster, updates, "desk", Start);
        Assert.Empty(refused);
        var voice = next.Resolve(a.Id)!;
        Assert.Equal(5, voice.Names.Count);
        Assert.Equal("Al", voice.DisplayName);
        Assert.Equal(new[] { "Alexander", "Alex", "Lex", "Xander" }.Order(), voice.OtherNames.Order());
        Assert.Equal("Learned voice 1 is Alexander.", applied[0].Text);
        Assert.Equal("Learned Alexander (voice 1) also goes by Alex.", applied[1].Text);
        Assert.Equal("Learned Alexander (voice 1) likes to be called Al.", applied[4].Text);

        // Being called by another name again keeps it shown; the owner's typed name still wins.
        (next, _, _) = VoiceUpdates.Apply(next, [new(VoiceUpdateKind.Name, a.Id, "Alex"), new(VoiceUpdateKind.Name, a.Id, "Alex")], "desk", Start);
        Assert.Equal("Alex", next.Resolve(a.Id)!.DisplayName);
        (next, _, _) = VoiceUpdates.Apply(next, [new(VoiceUpdateKind.Call, a.Id, "Al")], "desk", Start);
        Assert.Equal("Al", next.Resolve(a.Id)!.DisplayName);
        next = next.SetNames(a.Id, "Alexandra", next.Resolve(a.Id)!.Names.Select(n => n.Text), "desk", Start);
        (next, _, _) = VoiceUpdates.Apply(next, [new(VoiceUpdateKind.Call, a.Id, "Lex")], "desk", Start);
        Assert.Equal("Alexandra", next.Resolve(a.Id)!.DisplayName);
        next.Validate();
    }

    [Fact]
    public void AWrongLearnedNameIsDroppedButNeverOneTheOwnerTyped()
    {
        var (roster, a, _) = Two();
        roster = roster.AddHeardName(a.Id, "Jane", "desk", Start).AddHeardName(a.Id, "Sam", "desk", Start);
        var (next, applied, refused) = VoiceUpdates.Apply(roster, [new(VoiceUpdateKind.Not, a.Id, "jane", Line: 1)], "desk", Start);
        Assert.Equal(new[] { "Sam" }, next.Resolve(a.Id)!.Names.Select(n => n.Text).ToArray());
        Assert.Equal("Learned voice 1 doesn't go by jane.", applied.Single().Text);
        Assert.Empty(refused);

        next = next.SetNames(a.Id, "Samantha", ["Sam", "Sunny"], "desk", Start);
        var (kept, none, typed) = VoiceUpdates.Apply(next, [new(VoiceUpdateKind.Not, a.Id, "Samantha", Line: 2),
            new(VoiceUpdateKind.Not, a.Id, "Sunny", Line: 3), new(VoiceUpdateKind.Not, a.Id, "Bo", Line: 4)], "desk", Start);
        Assert.Same(next, kept);
        Assert.Empty(none);
        Assert.Equal(new[] { (2, "a name the owner typed"), (3, "a name the owner typed") }, typed.Select(r => (r.Line, r.Reason)).ToArray());

        // Dropping the companion's names (when a voice is heard) leaves typed names alone too.
        var companion = CompanionNames.From(["Sam"]);
        var dropped = roster.DropHeardNames(a.Id, companion.Matches, "desk", Start);
        Assert.Equal(new[] { "Jane" }, dropped.Resolve(a.Id)!.Names.Select(n => n.Text).ToArray());
        Assert.Same(next, next.DropHeardNames(a.Id, text => text is "Samantha" or "Sunny", "desk", Start));
    }

    [Fact]
    public void AVoiceWithOnlyOtherTypedNamesShowsOne()
    {
        var (roster, a, _) = Two();
        roster = roster.SetNames(a.Id, null, ["Bo", "Bobby", "Rob"], "desk", Start);
        var voice = roster.Resolve(a.Id)!;
        Assert.True(voice.Named);
        Assert.Equal("Bo", voice.DisplayName);
        Assert.Equal(new[] { "Bobby", "Rob" }, voice.OtherNames);
    }

    [Fact]
    public void SameMergesIntoTheOwnersVoiceUnlessTheOwnerNamedThemDifferently()
    {
        var (roster, a, b) = Two();
        roster = roster.SetOwner(b.Id, true, "desk", Start).AddHeardName(a.Id, "Robbie", "desk", Start).AddHeardName(b.Id, "Rob", "desk", Start);
        var (merged, applied, refused) = VoiceUpdates.Apply(roster, [new(VoiceUpdateKind.Same, a.Id, SameAsId: b.Id)], "desk", Start);
        Assert.Empty(refused);
        Assert.Single(merged.Live);
        Assert.Equal(b.Id, merged.Resolve(a.Id)!.Id);
        Assert.True(merged.Resolve(b.Id)!.Owner);
        Assert.Contains("Robbie", merged.Resolve(b.Id)!.Names.Select(n => n.Text));
        Assert.Equal("Learned Robbie (voice 1) is the same person as Rob (voice 2), and merged them.", applied.Single().Text);

        var named = roster.SetNames(a.Id, "Alice", [], "desk", Start).SetNames(b.Id, "Bob", [], "desk", Start);
        var (same, none, different) = VoiceUpdates.Apply(named, [new(VoiceUpdateKind.Same, a.Id, SameAsId: b.Id, Line: 1)], "desk", Start);
        Assert.Same(named, same);
        Assert.Empty(none);
        Assert.Equal("the owner named them differently", different.Single().Reason);

        var forgotten = roster.Forget(b.Id, "desk", Start);
        Assert.Equal("the voice was forgotten",
            VoiceUpdates.Apply(forgotten, [new(VoiceUpdateKind.Same, a.Id, SameAsId: b.Id)], "desk", Start).Refused.Single().Reason);
    }
}
