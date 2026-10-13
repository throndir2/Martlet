using Martlet.Conversation;
using Martlet.Core.Speakers;
using Martlet.Providers;
using Xunit;

namespace Martlet.Conversation.Tests;

public sealed class PeopleToolsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static float[] Print(int seed)
    {
        var random = new Random(seed);
        return VoicePrints.Normalize(Enumerable.Range(0, VoicePrints.Dimension).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray());
    }

    // V1 is the user (typed Robert, learned Bob twice), V2 is linked to someone else's account and learned Sam, V3 has no name.
    private static (VoiceRoster Roster, KnownVoice You, KnownVoice Sam, KnownVoice Unnamed, Guid Account) Roster()
    {
        var you = Guid.NewGuid();
        var roster = VoiceRoster.Empty;
        (roster, var robert) = roster.Add(Print(1), 6, "desk", Start);
        (roster, var sam) = roster.Add(Print(2), 4, "desk", Start.AddHours(-3));
        (roster, var unnamed) = roster.Add(Print(3), 3, "desk", Start.AddDays(-2));
        roster = roster.SetNames(robert!.Id, "Robert", [], "desk", Start).AddHeardName(robert.Id, "Bob", "desk", Start)
            .AddHeardName(robert.Id, "Bob", "desk", Start).SetAccount(robert.Id, you, false, "desk", Start)
            .AddHeardName(sam!.Id, "Sam", "desk", Start).SetAccount(sam.Id, Guid.NewGuid(), false, "desk", Start);
        return (roster, roster.Resolve(robert.Id)!, roster.Resolve(sam.Id)!, roster.Resolve(unnamed!.Id)!, you);
    }

    private static PeopleTools.Answer Run(string tool, string arguments, VoiceRoster roster, Guid you, DateTimeOffset? now = null) =>
        PeopleTools.Run(new TextToolCall("call", tool, arguments), roster, v => v.Account == you, now ?? Start.AddMinutes(5));

    [Fact]
    public void TheSetIsACheckInToolSetThatChangesNothingAndTakesNoReplyTool()
    {
        var set = Assert.Single(CheckInToolSets.All, s => s.Id == PeopleTools.SetId);
        Assert.Same(PeopleTools.Set, set);
        Assert.Equal(new[] { PeopleTools.List, PeopleTools.Lately }, set.Tools.Select(t => t.Name).ToArray());
        Assert.Empty(set.Replaces);
        Assert.True(set.Optional);
        Assert.True(CheckInToolSets.IsId(set.Id));
        // Only the People set is optional: every other set is the check-in's work, so it needs a member that calls tools.
        Assert.Equal([PeopleTools.SetId], CheckInToolSets.All.Where(s => s.Optional).Select(s => s.Id));
    }

    [Fact]
    public void TheListSaysEachVoicesNamesWhoseItIsAndWhenItWasHeardButNeverItsVoiceprint()
    {
        var (roster, you, sam, unnamed, account) = Roster();
        var answer = Run(PeopleTools.List, "{}", roster, account);

        Assert.False(answer.Result.IsError);
        var lines = answer.Result.Output.Split('\n');
        // The first line shows on the check-in card and in check-ins-status.json: counts only, never a name.
        Assert.Equal("People: 3 voice(s) saved, 3 listed.", lines[0]);
        Assert.Equal($"{you.Tag}: goes by Robert first (typed by the owner); also Bob (learned, used 2 times). The signed-in user. Heard 1 time, last 5 minutes ago.", lines[1]);
        Assert.Equal($"{sam.Tag}: goes by Sam first (learned). Linked to someone's Martlet account. Heard 1 time, last 3 hours ago.", lines[2]);
        Assert.Equal($"{unnamed.Tag}: no name yet. Heard 1 time, last 2 days ago.", lines[3]);
        Assert.Equal(new[] { you.Id, sam.Id, unnamed.Id }, answer.Shown.Select(v => v.Id).ToArray());
        Assert.DoesNotContain(roster.Live.SelectMany(v => v.Samples.Append(v.Centroid!)), print => answer.Result.Output.Contains(print, StringComparison.Ordinal));
    }

    [Fact]
    public void TheListFindsOnePersonByNameOrTag()
    {
        var (roster, you, sam, _, account) = Roster();
        Assert.Equal(you.Id, Assert.Single(Run(PeopleTools.List, """{"query":"bob"}""", roster, account).Shown).Id);
        Assert.Equal(sam.Id, Assert.Single(Run(PeopleTools.List, $$"""{"query":"{{sam.Tag}}"}""", roster, account).Shown).Id);
        var none = Run(PeopleTools.List, """{"query":"Zed"}""", roster, account);
        Assert.Empty(none.Shown);
        Assert.Equal("People: 3 voice(s) saved, 0 match.\nNobody saved goes by that name or tag.", none.Result.Output);
        Assert.Equal("People: 0 voice(s) saved, 0 listed.\nMartlet doesn't know anyone by voice yet.",
            Run(PeopleTools.List, "{}", VoiceRoster.Empty, account).Result.Output);
    }

    [Fact]
    public void LatelyListsWhoSpokeInTheLastMinutes()
    {
        var (roster, you, sam, _, account) = Roster();
        var hour = Run(PeopleTools.Lately, "{}", roster, account);
        Assert.Equal(you.Id, Assert.Single(hour.Shown).Id);
        Assert.StartsWith("People: 1 voice(s) heard in the last 60 minutes.", hour.Result.Output);
        var day = Run(PeopleTools.Lately, """{"minutes":1440}""", roster, account);
        Assert.Equal(new[] { you.Id, sam.Id }, day.Shown.Select(v => v.Id).ToArray());
        Assert.StartsWith("People: 2 voice(s) heard in the last 24 hours.", day.Result.Output);
        var none = Run(PeopleTools.Lately, """{"minutes":1}""", roster, account, Start.AddHours(1));
        Assert.Equal("People: 0 voice(s) heard in the last 1 minute.\nNobody Martlet knows by voice spoke in that time.", none.Result.Output);
        Assert.True(Run("people_forget", "{}", roster, account).Result.IsError);
    }
}
