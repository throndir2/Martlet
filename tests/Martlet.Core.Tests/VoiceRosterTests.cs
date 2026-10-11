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
        // One voice made the usual way: every sample, and names beyond the most are dropped.
        var (roster, voice) = VoiceRoster.Empty.Add(Voice(1), 3, "desk-a", Start);
        for (var s = 0; s < VoiceRoster.MaximumSamples; s++)
            roster = roster.Learn(voice!.Id, Voice(1, noise: 0.5f, variant: s), 2, "desk-a", Start);
        var names = Enumerable.Range(0, VoiceRoster.MaximumNames + 3).Select(n => $"Person {n} " + new string('x', 30)).ToArray();
        roster = roster.SetNames(voice!.Id, names[0], names.Skip(1), "desk-a", Start);
        var template = roster.Resolve(voice.Id)!;
        Assert.Equal(VoiceRoster.MaximumNames, template.Names.Count);
        Assert.Equal(VoiceRoster.MaximumSamples, template.Samples.Count);

        // Then a full list with every voice and tombstone at its most: names JSON escapes (no ASCII letters), the largest
        // counts, revisions and writers.
        static string Name(int n) => new string('Ж', VoiceRoster.MaximumNameLength - 2) + (char)('А' + n / 32 % 32) + (char)('а' + n % 32);
        static string Id(int n) => n.ToString("x32");
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(5.5)).AddTicks(1_234_567);
        var writer = new string('w', 64);
        var revision = long.MaxValue / 4;
        var live = Enumerable.Range(1, VoiceRoster.MaximumVoices).Select(i => template with
        {
            Id = Id(i), Number = i, Name = Name(VoiceRoster.MaximumNames),
            Names = Enumerable.Range(0, VoiceRoster.MaximumNames).Select(n => new VoiceName
            {
                Text = Name(n), Source = VoiceNameSource.Conversation, Uses = 1_000_000, LastUsedAt = at
            }).ToArray(),
            Owner = true, Link = new VoiceLink { Account = Guid.NewGuid(), Revision = revision, UpdatedBy = writer },
            MergedVoices = 1000, Heard = 1_000_000, SpeechSeconds = 9_999_999.9, FirstHeardAt = at, LastHeardAt = at,
            Revision = revision, UpdatedAt = at, UpdatedBy = writer
        });
        var tombstones = Enumerable.Range(1, VoiceRoster.MaximumTombstones).Select(i => new KnownVoice
        {
            Id = Id(1_000_000 + i), Number = 1_000_000 - i, Removed = true, MergedInto = Id(1),
            Link = new VoiceLink { Revision = revision, UpdatedBy = writer }, Revision = revision, UpdatedAt = at, UpdatedBy = writer
        });
        var full = new VoiceRoster
        {
            SchemaVersion = VoiceRoster.SchemaVersion1, Model = VoiceRoster.EmbeddingModel, Voices = [.. live, .. tombstones]
        };
        var bytes = full.Write();
        Assert.True(bytes.Length <= VoiceRoster.MaximumBytes, $"{bytes.Length:N0} of {VoiceRoster.MaximumBytes:N0} bytes");
        var parsed = VoiceRoster.Parse(bytes);
        Assert.Equal(VoiceRoster.MaximumVoices, parsed.Live.Count);
        Assert.Equal(VoiceRoster.MaximumTombstones, parsed.Voices.Count(v => v.Removed));
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
        var (next, added) = roster.Add(Voice(99_999), 3, "desk-a", Start.AddDays(1), out var forgotten);
        Assert.NotNull(added);
        // The voice forgotten to make room is said, so the owner can be told.
        Assert.Equal(oldest.Id, forgotten!.Id);
        Assert.Equal(VoiceRoster.MaximumVoices, next.Live.Count);
        Assert.Null(next.Resolve(oldest.Id));
        Assert.True(next.Write().Length <= VoiceRoster.MaximumBytes);
        // A voice linked to an account is never the one forgotten to make room.
        var linked = roster.SetAccount(oldest.Id, Sam, false, "desk-a", Start.AddDays(1));
        (next, added) = linked.Add(Voice(99_999), 3, "desk-a", Start.AddDays(1), out forgotten);
        Assert.NotNull(added);
        Assert.NotEqual(oldest.Id, forgotten!.Id);
        Assert.NotNull(next.Resolve(oldest.Id));
        // When every voice has a name, nothing is forgotten and nothing is added.
        var named = roster.Live.Aggregate(roster, (current, voice) => current.AddHeardName(voice.Id, "Sam", "desk-a", Start.AddDays(1)));
        (next, added) = named.Add(Voice(99_999), 3, "desk-a", Start.AddDays(2), out forgotten);
        Assert.Null(added);
        Assert.Null(forgotten);
        Assert.Same(named, next);
    }

    private static readonly Guid Sam = Guid.Parse("5a0d3b9e6c1f4e2a8b7d9c0e1f2a3b4c"), Alex = Guid.Parse("a1e7c3d5b9f24e6a8c0d2f4b6a8c0e1d");

    [Fact]
    public void AMergeKeepsEveryLiveVoiceAndOnlyTheOldestTombstonesLeave()
    {
        var a = VoiceRoster.Empty;
        for (var i = 0; i < VoiceRoster.MaximumVoices; i++) a = a.Add(Voice(100 + i), 3, "desk-a", Start.AddMinutes(i)).Roster;
        var b = VoiceRoster.Empty;
        for (var i = 0; i < 3; i++) b = b.Add(Voice(50_000 + i), 3, "desk-b", Start.AddMinutes(i)).Roster;
        // Two computers' copies together pass the limit: no live voice is dropped, and the list reads back whole.
        var merged = VoiceRoster.Merge(a, b);
        Assert.Equal(VoiceRoster.MaximumVoices + 3, merged.Live.Count);
        var copy = VoiceRoster.Parse(merged.Write());
        Assert.Equal(merged.Digest(), copy.Digest());
        // A new voice still makes room the visible way: one unnamed voice is forgotten and said.
        var (next, added) = copy.Add(Voice(99_999), 3, "desk-a", Start.AddDays(1), out var forgotten);
        Assert.NotNull(added);
        Assert.NotNull(forgotten);
        Assert.Equal(VoiceRoster.MaximumVoices + 3, next.Live.Count);

        // Tombstones past the limit: only the oldest leave.
        var tombstones = Enumerable.Range(1, VoiceRoster.MaximumTombstones + 5).Select(i => new KnownVoice
        {
            Id = i.ToString("x32"), Number = i, Removed = true, Revision = i, UpdatedAt = Start, UpdatedBy = "desk-a"
        }).ToArray();
        var trimmed = VoiceRoster.Merge(VoiceRoster.Empty,
            new VoiceRoster { SchemaVersion = VoiceRoster.SchemaVersion1, Model = VoiceRoster.EmbeddingModel, Voices = tombstones });
        Assert.Equal(VoiceRoster.MaximumTombstones, trimmed.Voices.Count);
        Assert.All(Enumerable.Range(1, 5), i => Assert.Null(trimmed.Find(i.ToString("x32"))));
    }

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
