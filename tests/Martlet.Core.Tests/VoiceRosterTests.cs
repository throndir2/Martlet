using Martlet.Core.Contracts;
using Martlet.Core.Speakers;
using Xunit;

namespace Martlet.Core.Tests;

public sealed class VoiceRosterTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static float[] Voice(int seed, float noise = 0, int variant = 0)
    {
        var random = new Random(seed);
        var vector = Enumerable.Range(0, VoicePrints.Dimension).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray();
        var jitter = new Random(seed * 1000 + variant);
        return VoicePrints.Normalize(vector.Select(v => v + (float)((jitter.NextDouble() - 0.5) * noise)).ToArray());
    }

    [Fact]
    public void NewVoicesAreAddedAndKnownOnesRecognized()
    {
        var roster = VoiceRoster.Empty;
        Assert.Equal(VoiceMatchKind.New, roster.Identify(Voice(1)).Kind);
        (roster, var first) = roster.Add(Voice(1), 3, "desk-a", Start);
        (roster, var second) = roster.Add(Voice(2), 3, "desk-a", Start);
        Assert.Equal(1, first!.Number);
        Assert.Equal(2, second!.Number);
        var match = roster.Identify(Voice(1, noise: 0.02f, variant: 1));
        Assert.Equal(VoiceMatchKind.Known, match.Kind);
        Assert.Equal(first.Id, match.Voice!.Id);
        roster = roster.Learn(first.Id, Voice(1, noise: 0.02f, variant: 2), 2, "desk-a", Start.AddMinutes(1));
        Assert.Equal(2, roster.Resolve(first.Id)!.Heard);
        Assert.Equal(VoiceMatchKind.New, roster.Identify(Voice(3)).Kind);
    }

    [Fact]
    public void NamesAccumulateAndTheOwnersNameWins()
    {
        var (roster, voice) = VoiceRoster.Empty.Add(Voice(1), 3, "desk-a", Start);
        Assert.Equal("Voice 1", voice!.DisplayName);
        roster = roster.AddHeardName(voice.Id, "Sam", "desk-a", Start).AddHeardName(voice.Id, "Sammy", "desk-a", Start)
            .AddHeardName(voice.Id, "sam", "desk-a", Start);
        var named = roster.Resolve(voice.Id)!;
        Assert.Equal("Sam", named.DisplayName);
        Assert.Equal(new[] { "Sammy" }, named.OtherNames);
        roster = roster.SetNames(voice.Id, "Samantha", ["Sam"], "desk-a", Start);
        named = roster.Resolve(voice.Id)!;
        Assert.Equal("Samantha", named.DisplayName);
        Assert.DoesNotContain(named.Names, n => n.Text == "Sammy");
        Assert.Null(VoiceRoster.CleanName("  "));
        Assert.Null(VoiceRoster.CleanName("1234"));
        Assert.Equal("Mary Jane", VoiceRoster.CleanName(" \"Mary  Jane\". "));
    }

    [Fact]
    public void AFullListWithTheMostNamesStillFits()
    {
        var roster = VoiceRoster.Empty;
        for (var i = 0; i < VoiceRoster.MaximumTombstones; i++)
            roster = roster.Forget(roster.Add(Voice(1000 + i), 3, "desk-a", Start).Voice!.Id, "desk-a", Start);
        for (var i = 0; i < VoiceRoster.MaximumVoices; i++)
        {
            (roster, var voice) = roster.Add(Voice(i + 1), 3, "desk-a", Start);
            for (var s = 0; s < VoiceRoster.MaximumSamples; s++)
                roster = roster.Learn(voice!.Id, Voice(i + 1, noise: 0.5f, variant: s), 2, "desk-a", Start);
            var names = Enumerable.Range(0, VoiceRoster.MaximumNames + 3).Select(n => $"Person {i} {n} " + new string('x', 30)).ToArray();
            roster = roster.SetNames(voice!.Id, names[0], names.Skip(1), "desk-a", Start);
            for (var n = 0; n < 3; n++) roster = roster.AddHeardName(voice.Id, $"Heard {i} {n} " + new string('y', 30), "desk-a", Start);
            roster = roster.SetAccount(voice.Id, Guid.NewGuid(), i == 0, "desktop-" + new string('z', 40), Start);
        }
        Assert.All(roster.Live, v => Assert.Equal(VoiceRoster.MaximumNames, v.Names.Count));
        var bytes = roster.Write();
        Assert.True(bytes.Length <= VoiceRoster.MaximumBytes, $"{bytes.Length} bytes");
        Assert.Equal(roster.Digest(), VoiceRoster.Parse(bytes).Digest());
    }

    [Fact]
    public void JoinAndForgetLeaveTombstonesThatWinEverywhere()
    {
        var roster = VoiceRoster.Empty;
        (roster, var a) = roster.Add(Voice(1), 3, "desk-a", Start);
        (roster, var b) = roster.Add(Voice(2), 3, "desk-a", Start);
        roster = roster.AddHeardName(b!.Id, "Al", "desk-a", Start).AddHeardName(a!.Id, "David", "desk-a", Start)
            .SetNames(b.Id, "Zira", ["Al"], "desk-a", Start);
        var before = roster;
        var joined = roster.Join(b.Id, a.Id, "desk-a", Start.AddMinutes(1));
        Assert.Single(joined.Live);
        Assert.Equal(a.Id, joined.Resolve(b.Id)!.Id);
        Assert.Equal("David", joined.Resolve(a.Id)!.DisplayName);
        Assert.Contains("Al", joined.Resolve(a.Id)!.Names.Select(n => n.Text));
        Assert.Contains("Zira", joined.Resolve(a.Id)!.OtherNames);
        Assert.Equal(VoiceMatchKind.Known, joined.Identify(Voice(2, 0.02f, 3)).Kind);
        var merged = VoiceRoster.Merge(before, joined);
        Assert.Equal(merged.Digest(), VoiceRoster.Merge(joined, before).Digest());
        Assert.Single(merged.Live);
        var forgotten = merged.Forget(a.Id, "desk-b", Start.AddMinutes(2));
        Assert.Empty(VoiceRoster.Merge(merged, forgotten).Live);
    }

    [Fact]
    public void ACopyRoundTripsAndRejectsDamage()
    {
        var (roster, voice) = VoiceRoster.Empty.Add(Voice(1), 3, "desk-a", Start);
        roster = roster.SetOwner(voice!.Id, true, "desk-a", Start);
        var copy = VoiceRoster.Parse(roster.Write());
        Assert.Equal(roster.Digest(), copy.Digest());
        Assert.True(copy.Resolve(voice.Id)!.Owner);
        var text = System.Text.Encoding.UTF8.GetString(roster.Write()).Replace(VoiceRoster.EmbeddingModel, "other-model");
        Assert.Throws<ContractException>(() => VoiceRoster.Parse(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void AFullListForgetsTheLongestUnheardUnnamedVoice()
    {
        var roster = VoiceRoster.Empty;
        for (var i = 0; i < VoiceRoster.MaximumVoices; i++)
            roster = roster.Add(Voice(100 + i), 3, "desk-a", Start.AddMinutes(i)).Roster;
        var oldest = roster.Live.MinBy(v => v.LastHeardAt)!;
        var (next, added) = roster.Add(Voice(999), 3, "desk-a", Start.AddDays(1));
        Assert.NotNull(added);
        Assert.Equal(VoiceRoster.MaximumVoices, next.Live.Count);
        Assert.Null(next.Resolve(oldest.Id));
        Assert.True(next.Write().Length <= VoiceRoster.MaximumBytes);
        // A voice linked to an account is never the one forgotten to make room.
        var linked = roster.SetAccount(oldest.Id, Sam, false, "desk-a", Start.AddDays(1));
        (next, added) = linked.Add(Voice(999), 3, "desk-a", Start.AddDays(1));
        Assert.NotNull(added);
        Assert.NotNull(next.Resolve(oldest.Id));
    }

    private static readonly Guid Sam = Guid.Parse("5a0d3b9e6c1f4e2a8b7d9c0e1f2a3b4c"), Alex = Guid.Parse("a1e7c3d5b9f24e6a8c0d2f4b6a8c0e1d");

    [Fact]
    public void AnAccountLinkMergesApartFromTheRestOfTheVoice()
    {
        var (roster, voice) = VoiceRoster.Empty.Add(Voice(1), 3, "desk-a", Start);
        var id = voice!.Id;
        var linked = roster.SetAccount(id, Sam, owner: true, "desk-a", Start.AddMinutes(1));
        Assert.Equal(Sam, linked.Resolve(id)!.Account);
        Assert.True(linked.Resolve(id)!.Owner);
        Assert.Equal([id], linked.LinkedTo(Sam).Select(v => v.Id));
        Assert.Same(linked, linked.SetAccount(id, Sam, true, "desk-a", Start.AddMinutes(2)));

        // An older desktop never sees the link and keeps learning the voice: its newer copy doesn't remove the link.
        var legacy = VoiceRoster.Parse(linked.WithoutLinks().Write());
        Assert.DoesNotContain("\"link\"", System.Text.Encoding.UTF8.GetString(legacy.Write()));
        Assert.True(legacy.Resolve(id)!.Owner);
        legacy = legacy.Learn(id, Voice(1, 0.02f, 1), 2, "desk-old", Start.AddMinutes(5));
        var merged = VoiceRoster.Merge(linked, legacy);
        Assert.Equal(Sam, merged.Resolve(id)!.Account);
        Assert.Equal(2, merged.Resolve(id)!.Heard);
        Assert.Equal(merged.Digest(), VoiceRoster.Merge(legacy, linked).Digest());
        Assert.Equal(merged.Digest(), VoiceRoster.Merge(merged, merged).Digest());

        // Moving the link to Alex on one copy and unlinking on another: the newest change wins, whichever order they merge in.
        var moved = merged.SetAccount(id, Alex, owner: false, "desk-b", Start.AddMinutes(6));
        Assert.False(moved.Resolve(id)!.Owner);
        var unlinked = merged.SetAccount(id, null, owner: false, "desk-c", Start.AddMinutes(7));
        Assert.Null(unlinked.Resolve(id)!.Account);
        Assert.NotNull(unlinked.Resolve(id)!.Link);
        var all = new[] { linked, legacy, moved, unlinked };
        var expected = all.Aggregate(VoiceRoster.Merge).Digest();
        Assert.Equal(expected, all.Reverse().Aggregate(VoiceRoster.Merge).Digest());
        Assert.Equal(expected, VoiceRoster.Merge(VoiceRoster.Merge(linked, unlinked), VoiceRoster.Merge(moved, legacy)).Digest());
        Assert.Null(VoiceRoster.Parse(all.Aggregate(VoiceRoster.Merge).Write()).Resolve(id)!.Account);
        Assert.Throws<ArgumentException>(() => merged.SetAccount(id, Guid.Empty, false, "desk-a", Start));
    }

    [Fact]
    public void ForgettingUnlinksAndAMergedVoiceGivesItsLinkToTheKeptOne()
    {
        var roster = VoiceRoster.Empty;
        (roster, var a) = roster.Add(Voice(1), 3, "desk-a", Start);
        (roster, var b) = roster.Add(Voice(2), 3, "desk-a", Start);
        (roster, var c) = roster.Add(Voice(3), 3, "desk-a", Start);
        roster = roster.SetAccount(b!.Id, Sam, false, "desk-a", Start.AddMinutes(1)).SetAccount(c!.Id, Alex, false, "desk-a", Start.AddMinutes(1));
        var before = roster;
        var joined = roster.Join(b.Id, a!.Id, "desk-a", Start.AddMinutes(2));
        Assert.Equal(Sam, joined.Resolve(a.Id)!.Account);
        Assert.Null(joined.Find(b.Id)!.Account);
        Assert.Equal(Sam, VoiceRoster.Merge(before, joined).Resolve(b.Id)!.Account);
        var forgotten = joined.Forget(c.Id, "desk-b", Start.AddMinutes(3));
        Assert.Empty(VoiceRoster.Merge(before, forgotten).LinkedTo(Alex));
        Assert.Null(VoiceRoster.Merge(before, forgotten).Find(c.Id)!.Account);
        VoiceRoster.Parse(VoiceRoster.Merge(before, forgotten).Write());
    }

    [Fact]
    public void TheVoiceAnOlderMartletMarkedAsTheOwnersLinksToTheOwnerAccount()
    {
        var roster = VoiceRoster.Empty;
        (roster, var mine) = roster.Add(Voice(1), 3, "desk-a", Start);
        (roster, var other) = roster.Add(Voice(2), 3, "desk-a", Start);
        roster = roster.SetOwner(mine!.Id, true, "desk-old", Start);
        var migrated = roster.LinkOwnerVoices(Sam, "desk-a", Start.AddMinutes(1));
        Assert.Equal(Sam, migrated.Resolve(mine.Id)!.Account);
        Assert.Null(migrated.Resolve(other!.Id)!.Account);
        Assert.Same(migrated, migrated.LinkOwnerVoices(Sam, "desk-b", Start.AddMinutes(2)));
        Assert.Equal(migrated.Digest(), VoiceRoster.Merge(roster, migrated).Digest());
    }
}
